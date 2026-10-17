using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenViya.Ops.Core;
using OpenViya.Ops.Provisioning;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<T> Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new InvalidOperationException(message);
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static async Task RunSshKeygenAsync(params string[] args)
{
    var sshKeygen = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
    if (!File.Exists(sshKeygen)) sshKeygen = "ssh-keygen.exe";
    var start = new ProcessStartInfo(sshKeygen) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not run system ssh-keygen.");
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    var keygenError = await process.StandardError.ReadToEndAsync();
    Check(process.ExitCode == 0, "Ephemeral ssh-keygen operation failed: " + keygenError.Replace("fixture-only-passphrase", "<fixture-secret>", StringComparison.Ordinal));
}

var workspaceRoot = FindOpsRoot();
var temporaryRoot = Path.Combine(Path.GetTempPath(), "openviya-provision-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
Process? fixture = null;
try
{
    var keyPath = Path.Combine(temporaryRoot, "client_ed25519");
    using (var bootstrap = typeof(SshNetVpsSessionFactory).Assembly.GetManifestResourceStream("OpenViya.Ops.Provisioning.bootstrap-vps.sh"))
    {
        Check(bootstrap is not null && bootstrap.Length > 0, "The fresh-VPS bootstrap script must be embedded in the provisioning assembly.");
    }
    await RunSshKeygenAsync("-q", "-t", "ed25519", "-N", "", "-f", keyPath, "-C", "provision-checks-only");
    var clientAdminKey = await new VpsAdminKeyService(new AppPaths(temporaryRoot)).PrepareAsync(Guid.NewGuid(), keyPath, "");
    var authorizedKeyPath = Path.Combine(temporaryRoot, "authorized_client.pub");
    await File.WriteAllTextAsync(authorizedKeyPath, clientAdminKey.PublicKey + Environment.NewLine);
    Console.WriteLine("Disposable client key ready.");
    var fixtureRoot = Path.Combine(temporaryRoot, "sftp-root");
    var runId = Guid.NewGuid().ToString("N");
    var fixtureStage = Path.Combine(fixtureRoot, "tmp", "openviya-" + runId);
    Directory.CreateDirectory(fixtureStage);

    var port = FreePort();
    var encryptedKeyPath = Path.Combine(temporaryRoot, "encrypted_client_ed25519");
    var fixtureScript = Path.Combine(AppContext.BaseDirectory, "fixture_server.py");
    var python = FindPython();
    var start = new ProcessStartInfo(python)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("-u");
    start.ArgumentList.Add(fixtureScript);
    start.ArgumentList.Add("--port"); start.ArgumentList.Add(port.ToString());
    start.ArgumentList.Add("--public-key"); start.ArgumentList.Add(authorizedKeyPath);
    start.ArgumentList.Add("--root"); start.ArgumentList.Add(fixtureRoot);
    start.ArgumentList.Add("--private-key-source"); start.ArgumentList.Add(keyPath);
    start.ArgumentList.Add("--encrypted-key-output"); start.ArgumentList.Add(encryptedKeyPath);
    var packageDir = Path.Combine(workspaceRoot, ".tools", "ssh-fixture");
    start.Environment["PYTHONPATH"] = packageDir;
    fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not start local AsyncSSH fixture.");
    Console.WriteLine($"AsyncSSH fixture process started (PID {fixture.Id}).");
    var fixtureError = fixture.StandardError.ReadToEndAsync();
    var readyTask = fixture.StandardOutput.ReadLineAsync();
    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(10))) != readyTask)
    {
        try { fixture.Kill(entireProcessTree: true); } catch { }
        throw new InvalidOperationException("Local SSH fixture did not report readiness. " + await fixtureError);
    }
    var readyLine = await readyTask;
    if (readyLine is null) throw new InvalidOperationException("Local SSH fixture did not report readiness. " + await fixtureError);
    using var ready = JsonDocument.Parse(readyLine!);
    var keyType = ready.RootElement.GetProperty("key_type").GetString()!;
    var hostKey = ready.RootElement.GetProperty("public_key").GetString()!;
    var hostKeyFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(hostKey))).TrimEnd('=');
    var candidate = new HostKeyCandidate(keyType, hostKey, hostKeyFingerprint);
    Console.WriteLine("Loopback AsyncSSH fixture ready.");
    var profile = new ServerProfile { Host = "127.0.0.1", SshPort = port };
    var factory = new SshNetVpsSessionFactory();
    const string fixturePassphrase = "fixture-only-passphrase";
    var missingPassphrase = await Throws<VpsKeyPassphraseRequiredException>(() => factory.ConnectAsync(profile,
        new VpsSshAccess { UserName = "root", Method = VpsAuthMethod.PrivateKey, IdentityFile = encryptedKeyPath }, candidate), "An encrypted private key without its passphrase must get a specific safe correction message.");
    Check(missingPassphrase.Message.Contains("passphrase", StringComparison.Ordinal) && !missingPassphrase.Message.Contains(fixturePassphrase, StringComparison.Ordinal), "The correction must identify the passphrase field without revealing a secret.");
    await using (var encryptedSession = await factory.ConnectAsync(profile,
        new VpsSshAccess { UserName = "root", Method = VpsAuthMethod.PrivateKey, IdentityFile = encryptedKeyPath, KeyPassphrase = fixturePassphrase }, candidate))
        Check((await encryptedSession.ExecuteAsync("id -u")).Output.Trim() == "0", "The encrypted key must authenticate when its correct passphrase is supplied.");

    await using (var session = await factory.ConnectAsync(profile, new VpsSshAccess { UserName = "root", Method = VpsAuthMethod.Password, Password = "test-password-local-only" }, candidate))
    {
        Console.WriteLine("Password-auth SSH and SFTP connected.");
        var result = await session.ExecuteAsync("id -u");
        Check(result.ExitCode == 0 && result.Output.Trim() == "0", "Password auth and fixed machine-output command should return root uid 0.");
        var remotePath = $"/tmp/openviya-{runId}/admin.pub";
        var payload = "ssh-ed25519 disposable fixture data\n"u8.ToArray();
        await session.UploadAsync(remotePath, payload);
        var uploaded = await File.ReadAllBytesAsync(Path.Combine(fixtureStage, "admin.pub"));
        Check(uploaded.SequenceEqual(payload), "SFTP should upload bytes to an allowed provisioning path.");
        await Throws<ArgumentException>(() => session.UploadAsync("/etc/authorized_keys", payload), "An upload outside the provisioning staging path must be rejected.");
        await Throws<ArgumentOutOfRangeException>(() => session.UploadAsync(remotePath, new byte[2 * 1024 * 1024 + 1]), "An upload above 2 MiB must be rejected.");
    }

    await using (var keySession = await factory.ConnectAsync(profile, new VpsSshAccess { UserName = "root", Method = VpsAuthMethod.PrivateKey, IdentityFile = keyPath }, candidate))
    {
        Console.WriteLine("Private-key SSH and SFTP connected.");
        var result = await keySession.ExecuteAsync("id -u");
        Check(result.ExitCode == 0 && result.Output.Trim() == "0", "Disposable Ed25519 key authentication should return root uid 0.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Throws<OperationCanceledException>(() => keySession.ExecuteAsync("sleep 5", cancel.Token), "Command cancellation should cancel the local command operation.");
    }

    var derived = clientAdminKey;
    var derivedParts = derived.PublicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    Check(derivedParts.Length == 2 && derivedParts[0] == "ssh-ed25519", "The admin key service should derive an Ed25519 public key from the private key itself.");
    var wrongPin = new HostKeyCandidate("ssh-ed25519", derivedParts[1], "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(derivedParts[1]))).TrimEnd('='));
    var mismatch = await Throws<InvalidOperationException>(() => factory.ConnectAsync(profile, new VpsSshAccess { UserName = "root", Method = VpsAuthMethod.Password, Password = "test-password-local-only" }, wrongPin), "A valid but nonmatching trusted host key must be rejected.");
    Check(!mismatch.Message.Contains("test-password-local-only", StringComparison.Ordinal) && !mismatch.Message.Contains("127.0.0.1", StringComparison.Ordinal), "Connection diagnostics must not expose password or endpoint values.");

    var generatedId = Guid.NewGuid();
    var paths = new AppPaths(Path.Combine(temporaryRoot, "managed-key-home"));
    var keyService = new VpsAdminKeyService(paths);
    var generated = await keyService.PrepareAsync(generatedId, "", "");
    Check(File.Exists(generated.IdentityFile) && generated.IdentityFile.Contains(generatedId.ToString("N"), StringComparison.Ordinal), "An empty identity selection must create a dedicated key for this server.");
    var bytesBeforeReuse = await File.ReadAllBytesAsync(generated.IdentityFile);
    var reused = await keyService.PrepareAsync(generatedId, "", "");
    var bytesAfterReuse = await File.ReadAllBytesAsync(reused.IdentityFile);
    Check(reused.IdentityFile == generated.IdentityFile && bytesBeforeReuse.SequenceEqual(bytesAfterReuse), "A safe managed key must be reused instead of overwritten.");
    Check(reused.PublicKey.StartsWith("ssh-ed25519 ", StringComparison.Ordinal), "The generated admin key must be Ed25519.");

    Console.WriteLine("Provision transport checks passed: localhost password/key authentication, strict host-key pinning, bounded command cancellation, staging-only bounded SFTP upload, key-derived public data, generated key reuse, and credential redaction. No VPS or user key/trust store was used.");
}
finally
{
    if (fixture is not null)
    {
        try { if (!fixture.HasExited) fixture.Kill(entireProcessTree: true); } catch { }
        fixture.Dispose();
    }
    try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
}

static string FindOpsRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, ".tools", "ssh-fixture"))) return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Could not find the local SSH fixture dependencies.");
}

static string FindPython()
{
    var configured = Environment.GetEnvironmentVariable("PYTHON");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory.Trim('"'), "python.exe");
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("Python 3 was not found for the local SSH fixture.");
}
