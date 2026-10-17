using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenViya.Ops.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OpenViya.Ops.Provisioning;

/// <summary>Creates provisioning sessions which accept only the wizard's explicitly pinned Ed25519 host key.</summary>
public sealed class SshNetVpsSessionFactory : IVpsRemoteSessionFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _executeTimeout;
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);
    private const int MaxOutputBytes = 128 * 1024;
    private const int MaxUploadBytes = 2 * 1024 * 1024;

    public SshNetVpsSessionFactory(TimeSpan? executeTimeout = null)
    {
        _executeTimeout = executeTimeout ?? TimeSpan.FromSeconds(30);
        if (_executeTimeout <= TimeSpan.Zero || _executeTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(executeTimeout));
    }

    public async Task<IVpsRemoteSession> ConnectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(hostKey);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateEndpoint(profile, access, hostKey);
        PrivateKeyFile? privateKey = null;
        PrivateKeyFile? sftpPrivateKey = null;
        SshClient? ssh = null;
        SftpClient? sftp = null;
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(ConnectTimeout);
        try
        {
            var auth = CreateAuthentication(access, out privateKey);
            var sshInfo = CreateConnectionInfo(profile, access.UserName, auth);
            ssh = new SshClient(sshInfo);
            RestrictHostKeyAlgorithms(sshInfo);
            ConfigureHostKeyPin(ssh, hostKey);
            await ssh.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            if (!ssh.IsConnected) throw new InvalidOperationException("SSH-аутентификация не выполнена.");

            var sftpAuth = CreateAuthentication(access, out sftpPrivateKey);
            // Each protocol gets its own SSH connection and independent strict host-key callback.
            var sftpInfo = CreateConnectionInfo(profile, access.UserName, sftpAuth);
            sftp = new SftpClient(sftpInfo);
            RestrictHostKeyAlgorithms(sftpInfo);
            ConfigureHostKeyPin(sftp, hostKey);
            await sftp.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            if (!sftp.IsConnected)
            {
                throw new InvalidOperationException("SFTP-соединение не установлено.");
            }

            return new SshNetVpsRemoteSession(ssh, sftp, privateKey, sftpPrivateKey, _executeTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sftp?.Dispose(); ssh?.Dispose(); sftpPrivateKey?.Dispose(); privateKey?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            sftp?.Dispose(); ssh?.Dispose(); sftpPrivateKey?.Dispose(); privateKey?.Dispose();
            throw new InvalidOperationException("SSH/SFTP connection timed out while verifying the pinned host key.");
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            sftp?.Dispose(); ssh?.Dispose(); sftpPrivateKey?.Dispose(); privateKey?.Dispose();
            throw new VpsKeyPassphraseRequiredException();
        }
        catch (Exception)
        {
            sftp?.Dispose(); ssh?.Dispose(); sftpPrivateKey?.Dispose(); privateKey?.Dispose();
            throw new InvalidOperationException("Не удалось подключиться по SSH/SFTP. Проверьте адрес, порт, учётные данные и принятый fingerprint ssh-ed25519.");
        }
    }

    private static AuthenticationMethod[] CreateAuthentication(VpsSshAccess access, out PrivateKeyFile? privateKey)
    {
        privateKey = null;
        if (access.Method == VpsAuthMethod.Password)
        {
            if (string.IsNullOrEmpty(access.Password)) throw new ArgumentException("Пароль SSH не указан.");
            return [new PasswordAuthenticationMethod(access.UserName, access.Password)];
        }
        if (access.Method != VpsAuthMethod.PrivateKey || string.IsNullOrWhiteSpace(access.IdentityFile) || !File.Exists(access.IdentityFile))
            throw new ArgumentException("Файл приватного SSH-ключа не найден.");

        privateKey = string.IsNullOrEmpty(access.KeyPassphrase)
            ? new PrivateKeyFile(access.IdentityFile)
            : new PrivateKeyFile(access.IdentityFile, access.KeyPassphrase);
        return [new PrivateKeyAuthenticationMethod(access.UserName, privateKey)];
    }

    private static ConnectionInfo CreateConnectionInfo(ServerProfile profile, string userName, AuthenticationMethod[] methods)
    {
        var info = new ConnectionInfo(profile.Host, profile.SshPort, userName, methods) { Timeout = ConnectTimeout };
        return info;
    }

    private static void RestrictHostKeyAlgorithms(ConnectionInfo info)
    {
        var algorithms = info.HostKeyAlgorithms;
        foreach (var name in algorithms.Keys.Where(x => !StringComparer.Ordinal.Equals(x, "ssh-ed25519")).ToArray())
            algorithms.Remove(name);
        if (!algorithms.ContainsKey("ssh-ed25519"))
            throw new InvalidOperationException("Эта версия SSH.NET не поддерживает host key ssh-ed25519.");
    }

    private static void ConfigureHostKeyPin(BaseClient client, HostKeyCandidate candidate)
    {
        var pinned = Convert.FromBase64String(candidate.PublicKey);
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = StringComparer.Ordinal.Equals(e.HostKeyName, "ssh-ed25519") && CryptographicOperations.FixedTimeEquals(e.HostKey, pinned);
        };
    }

    private static void ValidateEndpoint(ServerProfile profile, VpsSshAccess access, HostKeyCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Host.Length > 253 || profile.Host.Any(char.IsControl) || profile.Host.Any(char.IsWhiteSpace) ||
            profile.SshPort is < 1 or > 65535)
            throw new ArgumentException("SSH endpoint is invalid.");
        if (IPAddress.TryParse(profile.Host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            throw new ArgumentException("SSH IPv6 host must not contain a local scope id.");
        if (!IPAddress.TryParse(profile.Host, out _) &&
            (profile.Host.StartsWith('-') || !Regex.IsMatch(profile.Host, @"^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.?$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("SSH host name is invalid.");
        if (string.IsNullOrWhiteSpace(access.UserName) || access.UserName.Length > 64 || !Regex.IsMatch(access.UserName, @"^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("SSH username is invalid.");
        if (!StringComparer.Ordinal.Equals(candidate.KeyType, "ssh-ed25519") || !TryReadPinnedKey(candidate, out _))
            throw new ArgumentException("Pinned host key must be a valid ssh-ed25519 key with a matching SHA-256 fingerprint.");
    }

    private static bool TryReadPinnedKey(HostKeyCandidate candidate, out byte[] keyBlob)
    {
        keyBlob = [];
        try
        {
            var blob = Convert.FromBase64String(candidate.PublicKey);
            if (!StringComparer.Ordinal.Equals(Convert.ToBase64String(blob), candidate.PublicKey)) return false;
            if (blob.Length < 4 + 11 + 4 + 32) return false;
            var nameLength = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4));
            if (nameLength != 11 || !blob.AsSpan(4, 11).SequenceEqual("ssh-ed25519"u8)) return false;
            var keyLengthOffset = 4 + (int)nameLength;
            var keyLength = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(keyLengthOffset, 4));
            if (keyLength != 32 || blob.Length != keyLengthOffset + 4 + 32) return false;
            var digest = SHA256.HashData(blob);
            var fingerprint = "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
            if (!StringComparer.Ordinal.Equals(fingerprint, candidate.Fingerprint)) return false;
            keyBlob = blob;
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private sealed class SshNetVpsRemoteSession(SshClient ssh, SftpClient sftp, PrivateKeyFile? sshKey, PrivateKeyFile? sftpKey, TimeSpan executeTimeout) : IVpsRemoteSession
    {
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private int _disposed;

        public async Task<VpsRemoteResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(command) || command.Length > 32 * 1024 || command.Contains('\0'))
                throw new ArgumentException("Remote command is invalid or too long.", nameof(command));
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(executeTimeout);
                using var remoteCommand = ssh.CreateCommand(command);
                remoteCommand.CommandTimeout = executeTimeout;
                using var output = new MemoryStream();
                try
                {
                    var execution = remoteCommand.ExecuteAsync(timeout.Token);
                    var stdoutDrain = DrainAsync(remoteCommand.OutputStream, output, timeout.Token);
                    var stderrDrain = DrainAsync(remoteCommand.ExtendedOutputStream, null, timeout.Token);
                    var pending = new List<Task> { execution, stdoutDrain, stderrDrain };
                    while (pending.Count != 0)
                    {
                        var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                        try { await completed.ConfigureAwait(false); }
                        catch
                        {
                            try { remoteCommand.CancelAsync(); } catch { }
                            throw;
                        }
                        pending.Remove(completed);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { throw new TimeoutException("SSH command timed out."); }
                catch (OutputLimitExceededException) { throw new InvalidOperationException("Remote command output exceeded the 128 KiB limit."); }
                catch (Exception) { throw new InvalidOperationException("Remote command failed."); }

                var bytes = output.ToArray();
                var resultText = Encoding.UTF8.GetString(bytes);
                if (resultText.Length > MaxOutputBytes)
                    throw new InvalidOperationException("Remote command output exceeded the 128 KiB limit.");
                return new VpsRemoteResult(remoteCommand.ExitStatus ?? -1, resultText);
            }
            finally { _operationGate.Release(); }
        }

        public async Task UploadAsync(string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidateUploadPath(remotePath);
            if (content.Length > MaxUploadBytes) throw new ArgumentOutOfRangeException(nameof(content), "Upload exceeds the 2 MiB limit.");
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(UploadTimeout);
                using var source = new MemoryStream(content.ToArray(), writable: false);
                try { await sftp.UploadFileAsync(source, remotePath, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { throw new TimeoutException("SFTP upload timed out."); }
                catch (Exception) { throw new InvalidOperationException("SFTP upload failed."); }
            }
            finally { _operationGate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            try { if (sftp.IsConnected) sftp.Disconnect(); } catch { }
            try { if (ssh.IsConnected) ssh.Disconnect(); } catch { }
            sftp.Dispose(); ssh.Dispose();
            sftpKey?.Dispose(); sshKey?.Dispose();
            _operationGate.Dispose();
            return ValueTask.CompletedTask;
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        private static async Task DrainAsync(Stream input, MemoryStream? capture, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                total += read;
                if (total > MaxOutputBytes) throw new OutputLimitExceededException();
                if (capture is not null) await capture.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        private static void ValidateUploadPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 512 || path.Contains('\\') || path.Contains('%') || path.Any(char.IsControl))
                throw new ArgumentException("Upload path is invalid.", nameof(path));
            var staged = Regex.IsMatch(path, @"^/tmp/openviya-[0-9a-f]{32}/(?:bootstrap-vps\.sh|bootstrap\.sh|admin\.pub|maintenance\.py)$", RegexOptions.CultureInvariant);
            var controlled = Regex.IsMatch(path, @"^/var/lib/openviya-desktop/[0-9a-f]{32}/(?:bootstrap-vps\.sh|bootstrap\.sh|admin\.pub)$", RegexOptions.CultureInvariant);
            var maintenance = Regex.IsMatch(path, @"^/var/lib/openviya-desktop-maintenance/[0-9a-f]{32}/maintenance\.py$", RegexOptions.CultureInvariant);
            if (!staged && !controlled && !maintenance) throw new ArgumentException("Upload path is outside an approved OpenViya staging area.", nameof(path));
        }
    }

    private sealed class OutputLimitExceededException : IOException { }
}
