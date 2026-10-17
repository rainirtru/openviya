using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Ssh;

/// <summary>Windows OpenSSH adapter with per-profile host identity and explicit trust.</summary>
public sealed class SshService : ISshService
{
    private readonly AppPaths _paths;
    private readonly string _sshExe;
    private readonly string _keyscanExe;
    private static readonly SemaphoreSlim TrustLock = new(1, 1);
    private static readonly ConditionalWeakTable<Process, DiagnosticBuffer> TunnelDiagnostics = new();

    public SshService(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _sshExe = FindOpenSsh("ssh.exe");
        _keyscanExe = FindOpenSsh("ssh-keyscan.exe");
        Directory.CreateDirectory(_paths.SshDirectory);
    }

    public async Task<IReadOnlyList<HostKeyCandidate>> ScanHostKeysAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(server);
        var result = await RunAsync(_keyscanExe,
            ["-T", "8", "-p", server.SshPort.ToString(CultureInfo.InvariantCulture), HostForCommand(server.Host)],
            TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
        // Windows OpenSSH 9.x keyscan advertises sntrup even in builds that cannot execute it.
        // Recover only from that specific negotiation failure; never auto-trust the observed key.
        if (NeedsManagedHostKeyScan(result.Stderr))
            return await ScanManagedHostKeyAsync(server, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException("OpenSSH не получил ключ узла. Проверьте адрес, порт и доступность SSH.");

        var candidates = new Dictionary<string, HostKeyCandidate>(StringComparer.Ordinal);
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !IsSafeKeyType(fields[1]) || !IsBase64Key(fields[2]) || !KeyBlobMatchesType(fields[1], fields[2])) continue;
            var fingerprint = Fingerprint(fields[2]);
            candidates.TryAdd(fields[1] + " " + fields[2], new HostKeyCandidate(fields[1], fields[2], fingerprint));
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException("Ответ ssh-keyscan не содержал пригодных host key.");
        return candidates.Values.ToArray();
    }

    internal static bool NeedsManagedHostKeyScan(string stderr) =>
        stderr.Contains("choose_kex: unsupported KEX method", StringComparison.OrdinalIgnoreCase);

    internal static async Task<IReadOnlyList<HostKeyCandidate>> ScanManagedHostKeyAsync(ServerProfile server, CancellationToken ct = default)
    {
        ValidateEndpoint(server);
        // The host-key callback deliberately rejects the connection before authentication.
        // No private key, password, agent, remote command, or known_hosts write is involved.
        var info = new Renci.SshNet.ConnectionInfo(server.Host, server.SshPort, "openviya-hostkey-probe",
            new Renci.SshNet.NoneAuthenticationMethod("openviya-hostkey-probe")) { Timeout = TimeSpan.FromSeconds(8) };
        var algorithms = info.HostKeyAlgorithms.ToArray();
        info.HostKeyAlgorithms.Clear();
        foreach (var algorithm in algorithms.OrderBy(x => x.Key == "ssh-ed25519" ? 0 : 1))
            info.HostKeyAlgorithms.Add(algorithm.Key, algorithm.Value);
        using var client = new Renci.SshNet.SshClient(info);
        HostKeyCandidate? candidate = null;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = false;
            var blob = e.HostKey;
            if (blob.Length is < 8 or > 16384) return;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4));
            if (length is < 2 or > 64 || length > blob.Length - 4) return;
            var type = Encoding.ASCII.GetString(blob, 4, (int)length);
            var key = Convert.ToBase64String(blob);
            if (IsSafeKeyType(type) && KeyBlobMatchesType(type, key))
                candidate = new HostKeyCandidate(type, key, Fingerprint(key));
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await client.ConnectAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Renci.SshNet.Common.SshConnectionException) when (candidate is not null)
        { /* Expected rejection by our observation-only callback. */ }
        catch (OperationCanceledException) { throw new TimeoutException("Истёк срок получения SSH host key встроенным сканером."); }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or IOException)
        { throw new InvalidOperationException("Встроенный сканер не получил SSH host key. Проверьте адрес, порт и доступность VPS."); }
        ct.ThrowIfCancellationRequested();
        return candidate is not null ? [candidate] : throw new InvalidOperationException("Сервер не предъявил пригодный публичный SSH host key.");
    }

    public async Task TrustHostKeyAsync(ServerProfile server, HostKeyCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateEndpoint(server);
        if (!IsSafeKeyType(candidate.KeyType) || !IsBase64Key(candidate.PublicKey) || !KeyBlobMatchesType(candidate.KeyType, candidate.PublicKey) ||
            !StringComparer.Ordinal.Equals(candidate.Fingerprint, Fingerprint(candidate.PublicKey)))
            throw new InvalidOperationException("Кандидат host key повреждён или fingerprint не совпадает.");

        // Candidate objects are untrusted input. Re-scan the exact endpoint before persisting any key.
        var observed = await ScanHostKeysAsync(server, cancellationToken).ConfigureAwait(false);
        if (!observed.Any(x => x.KeyType == candidate.KeyType && x.PublicKey == candidate.PublicKey && x.Fingerprint == candidate.Fingerprint))
            throw new InvalidOperationException("Этот ключ сейчас не предъявляется указанным SSH-узлом. Доверие не добавлено.");

        await TrustLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.KnownHostsPath)!);
            var existing = File.Exists(_paths.KnownHostsPath) ? await File.ReadAllTextAsync(_paths.KnownHostsPath, cancellationToken).ConfigureAwait(false) : "";
            var alias = Alias(server.Id);
            if (ValidateTrustPin(existing, alias, candidate)) return; // Same pinned material is idempotent.
            var lines = existing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            lines.Add($"{alias} {candidate.KeyType} {candidate.PublicKey}");
            var temp = _paths.KnownHostsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            try { File.Move(temp, _paths.KnownHostsPath, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { TrustLock.Release(); }
    }

    public async Task<ProbeResult> CheckConnectionAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateEndpoint(server);
            var config = await EnsureConfigAsync(server, cancellationToken).ConfigureAwait(false);
            var args = CommonArgs(config, server);
            args.Add("-T"); args.Add(Alias(server.Id)); args.Add("exit");
            var result = await RunAsync(_sshExe, args, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0
                ? ProbeResult.Create("SSH", ProbeStatus.Success, "SSH host key и key-auth проверены.")
                : ProbeResult.Create("SSH", ProbeStatus.Failure, "SSH не прошёл проверку host key или аутентификации. Проверьте доверенный fingerprint и выбранный ключ.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Win32Exception or TimeoutException)
        { return ProbeResult.Create("SSH", ProbeStatus.Failure, SafeMessage(ex)); }
    }

    public async Task OpenTerminalAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(server);
        if (!HasTrustedKey(server.Id)) throw new InvalidOperationException("Сначала просмотрите fingerprint и явно примите host key.");
        var config = await EnsureConfigAsync(server, cancellationToken).ConfigureAwait(false);
        var args = CommonArgs(config, server, batchMode: false);
        args.Add("-tt"); args.Add(Alias(server.Id));
        var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false, WorkingDirectory = _paths.Root, CreateNoWindow = false };
        psi.ArgumentList.Add("new-tab"); psi.ArgumentList.Add(_sshExe);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        // Explicit user action only; Windows Terminal owns this interactive session.
        Process.Start(psi);
    }

    internal async Task<Process> StartTunnelProcessAsync(ServerProfile server, int localPort, CancellationToken cancellationToken)
    {
        ValidateEndpoint(server);
        ValidatePort(localPort, nameof(localPort)); ValidatePort(server.PanelPort, nameof(server.PanelPort));
        if (!HasTrustedKey(server.Id)) throw new InvalidOperationException("Для профиля нет принятого host key.");
        var config = await EnsureConfigAsync(server, cancellationToken).ConfigureAwait(false);
        var args = CommonArgs(config, server);
        args.Add("-N"); args.Add("-T");
        args.Add("-o"); args.Add("ExitOnForwardFailure=yes");
        args.Add("-L"); args.Add($"127.0.0.1:{localPort}:127.0.0.1:{server.PanelPort}");
        args.Add(Alias(server.Id));
        var psi = new ProcessStartInfo(_sshExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) { process.Dispose(); throw new InvalidOperationException("Не удалось запустить OpenSSH."); }
        var diagnostics = new DiagnosticBuffer();
        TunnelDiagnostics.Add(process, diagnostics);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) diagnostics.Append(e.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try { WindowsJob.AttachAndOwn(process); }
        catch { try { process.Kill(true); } catch { } process.Dispose(); throw; }
        return process;
    }

    internal static bool IsAddressCollision(Process process)
    {
        if (!TunnelDiagnostics.TryGetValue(process, out var buffer)) return false;
        var output = buffer.Read();
        return output.Contains("LOCAL_ADDRESS_COLLISION", StringComparison.Ordinal);
    }

    internal bool HasTrustedKey(Guid profileId)
    {
        if (!File.Exists(_paths.KnownHostsPath)) return false;
        var alias = Alias(profileId);
        return File.ReadLines(_paths.KnownHostsPath).Any(x => LineHasAlias(x, alias));
    }

    internal static string Alias(Guid id) => "openviya-" + id.ToString("N");
    internal static void ValidatePort(int port, string field)
    { if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(field, "Port must be between 1 and 65535."); }

    internal static void ValidateEndpoint(ServerProfile server)
    {
        ArgumentNullException.ThrowIfNull(server);
        ValidatePort(server.SshPort, nameof(server.SshPort));
        if (server.Id == Guid.Empty) throw new ArgumentException("Server profile ID is required.");
        if (string.IsNullOrWhiteSpace(server.Host) || server.Host.Length > 253 || server.Host.Any(char.IsControl) || server.Host.Any(char.IsWhiteSpace) || server.Host.Contains('"') || server.Host.Contains('#'))
            throw new ArgumentException("SSH host must be a valid IP address or DNS name.");
        if (!IPAddress.TryParse(server.Host, out _) && !IsDnsName(server.Host)) throw new ArgumentException("SSH host must be a valid IP address or DNS name.");
        if (server.UserName.Length is < 1 or > 128 || !Regex.IsMatch(server.UserName, @"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("SSH username contains unsupported characters.");
        if (!string.IsNullOrWhiteSpace(server.IdentityFile) && !Path.IsPathFullyQualified(server.IdentityFile))
            throw new ArgumentException("SSH identity path must be absolute.");
        if (!string.IsNullOrWhiteSpace(server.IdentityFile) && (server.IdentityFile.Contains('\n') || server.IdentityFile.Contains('\r') || server.IdentityFile.Contains('"')))
            throw new ArgumentException("SSH identity path contains unsupported characters.");
    }

    private async Task<string> EnsureConfigAsync(ServerProfile s, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.SshDirectory);
        var path = Path.Combine(_paths.SshDirectory, Alias(s.Id) + ".conf");
        var identity = string.IsNullOrWhiteSpace(s.IdentityFile) ? "" : $"IdentityFile \"{s.IdentityFile.Replace("\\", "/", StringComparison.Ordinal)}\"{Environment.NewLine}";
        var text = $"Host {Alias(s.Id)}{Environment.NewLine}" +
            $"  HostName {HostForConfig(s.Host)}{Environment.NewLine}" +
            $"  Port {s.SshPort}{Environment.NewLine}" + $"  User {s.UserName}{Environment.NewLine}" +
            $"  HostKeyAlias {Alias(s.Id)}{Environment.NewLine}" +
            $"  UserKnownHostsFile \"{_paths.KnownHostsPath.Replace("\\", "/", StringComparison.Ordinal)}\"{Environment.NewLine}" +
            $"  GlobalKnownHostsFile NUL{Environment.NewLine}" + "  StrictHostKeyChecking yes\n  UpdateHostKeys no\n  ForwardAgent no\n  IdentitiesOnly yes\n  BatchMode yes\n  ConnectTimeout 8\n  ServerAliveInterval 15\n  ServerAliveCountMax 3\n" + identity;
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }

    private static List<string> CommonArgs(string config, ServerProfile s, bool batchMode = true)
    {
        var args = new List<string> { "-F", config };
        if (!string.IsNullOrWhiteSpace(s.IdentityFile)) { args.Add("-i"); args.Add(s.IdentityFile); }
        args.Add("-o"); args.Add(batchMode ? "BatchMode=yes" : "BatchMode=no");
        args.Add("-o"); args.Add("ForwardAgent=no");
        return args;
    }

    private static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("OpenSSH process did not start.");
        var stdout = ReadBoundedAsync(process.StandardOutput, 16_384, ct);
        var stderr = ReadBoundedAsync(process.StandardError, 4_096, ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("OpenSSH operation timed out.");
        }
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var buf = new char[1024]; var result = new StringBuilder(Math.Min(maxChars, 1024));
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            if (result.Length < maxChars) result.Append(buf, 0, Math.Min(n, maxChars - result.Length));
        }
        return result.ToString();
    }

    private static string FindOpenSsh(string executable)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system, "OpenSSH", executable);
        if (!File.Exists(candidate)) throw new FileNotFoundException("Windows OpenSSH Client is required.", candidate);
        return candidate;
    }

    internal static string Fingerprint(string key)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(key); } catch (FormatException) { throw new InvalidOperationException("Invalid public host key material."); }
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=');
    }
    private static bool IsBase64Key(string key) { try { return Convert.FromBase64String(key).Length > 8; } catch { return false; } }
    internal static bool KeyBlobMatchesType(string type, string key)
    {
        try
        {
            var bytes = Convert.FromBase64String(key);
            if (bytes.Length < 5) return false;
            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return len > 0 && len <= bytes.Length - 4 && System.Text.Encoding.ASCII.GetString(bytes, 4, (int)len) == type;
        }
        catch { return false; }
    }
    internal static bool ValidateTrustPin(string existingText, string alias, HostKeyCandidate candidate)
    {
        var lines = existingText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(x => LineHasAlias(x, alias)).ToArray();
        if (lines.Any(line =>
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length < 3 || fields[1] != candidate.KeyType || fields[2] != candidate.PublicKey;
        }))
            throw new InvalidOperationException("Host key changed for this profile. Existing trust was preserved; compare the new fingerprint and use an explicit trust-reset flow before replacing it.");
        return lines.Length > 0;
    }
    private static bool IsSafeKeyType(string type) => Regex.IsMatch(type, @"^[A-Za-z0-9@._+-]{2,64}$", RegexOptions.CultureInvariant);
    private static bool LineHasAlias(string line, string alias) => !line.StartsWith('#') && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == alias;
    private static string HostForCommand(string host) => IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
    private static string HostForConfig(string host) => host;
    private static bool IsDnsName(string host) => host.Split('.').All(label => label.Length is > 0 and <= 63 && Regex.IsMatch(label, @"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant));
    private static string SafeMessage(Exception ex) => ex switch
    {
        TimeoutException => "SSH-проверка превысила допустимое время.",
        FileNotFoundException => "Не найден Windows OpenSSH Client.",
        ArgumentException => "Проверьте SSH-адрес, порт, пользователя и путь к ключу.",
        _ => "SSH-проверка не выполнена. Проверьте host key, ключ авторизации и доступность узла."
    };
    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
    private sealed class DiagnosticBuffer
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        internal void Append(string line)
        {
            // Never retain raw OpenSSH text because it may contain user-specific paths.
            var category = line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
                ? "LOCAL_ADDRESS_COLLISION" : "SSH_PROCESS_DIAGNOSTIC";
            lock (_gate) { if (_text.Length < 4096) _text.AppendLine(category); }
        }
        internal string Read() { lock (_gate) return _text.ToString(); }
    }
}
