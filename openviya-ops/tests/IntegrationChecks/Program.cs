using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenViya.Ops.Core;
using OpenViya.Ops.Network;
using OpenViya.Ops.Ssh;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<string> Execute(string executable, CancellationToken cancellationToken, params string[] arguments)
{
    using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(45));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { try { process.Kill(true); } catch { } throw; }
    var text = await stdout;
    var error = await stderr;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error[..Math.Min(error.Length, 400)]}");
    return text;
}

static async Task<string> CaptureSshDiagnostic(string executable, string config, string key, string alias)
{
    using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
    foreach (var argument in new[] { "-vv", "-F", config, "-i", key, "-T", alias, "exit" }) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) return "Could not start diagnostic SSH process.";
    var errorTask = process.StandardError.ReadToEndAsync();
    var outputTask = process.StandardOutput.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(true); } catch { } }
    var output = (await errorTask) + (await outputTask);
    var useful = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.Contains("Offering public key", StringComparison.OrdinalIgnoreCase) || line.Contains("Server accepts key", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("Authenticated to", StringComparison.OrdinalIgnoreCase) || line.Contains("Authentications that can continue", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || line.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("exec request", StringComparison.OrdinalIgnoreCase) || line.Contains("Exit status", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("Could not", StringComparison.OrdinalIgnoreCase) || line.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        .Take(12);
    return string.Join(" | ", useful).Replace(key, "<ephemeral-key>", StringComparison.OrdinalIgnoreCase);
}

static int FreePort()
{
    using var socket = new TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    return ((IPEndPoint)socket.LocalEndpoint).Port;
}

static async Task ServeHttpAsync(TcpListener listener, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[2048];
                _ = await stream.ReadAsync(buffer, cancellationToken);
                var body = System.Text.Encoding.ASCII.GetBytes("fixture-http-ok");
                var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\nContent-Type: text/plain\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}

if (args.Length == 2 && args[0] == "--scan-only")
{
    var scanRoot = Path.Combine(Path.GetTempPath(), "openviya-scan-only-" + Guid.NewGuid().ToString("N"));
    var scanPaths = new AppPaths(scanRoot);
    try
    {
        var scanService = new SshService(scanPaths);
        var observed = await scanService.ScanHostKeysAsync(new ServerProfile { Host = args[1] });
        foreach (var observedKey in observed) Console.WriteLine(observedKey.KeyType + " " + observedKey.Fingerprint);
        Check(!File.Exists(scanPaths.KnownHostsPath), "Scan-only must never persist trust.");
        Console.WriteLine("Scan only: no authentication, commands, or trusted-key writes.");
    }
    finally { if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true); }
    return;
}
if (args.Contains("--docker-local", StringComparer.Ordinal))
    throw new InvalidOperationException("--docker-local is disabled in this harness revision; use --python-local.");
if (!args.Contains("--python-local", StringComparer.Ordinal))
    throw new InvalidOperationException("Pass --python-local for the isolated loopback fixture.");

var temp = Path.Combine(Path.GetTempPath(), "openviya-ops-local-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var name = "openviya-ops-local-check-" + Guid.NewGuid().ToString("N");
var key = Path.Combine(temp, "test_ed25519");
var systemSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
var sshPort = FreePort();
var fixturePython = Path.Combine(Environment.CurrentDirectory, "tests", "IntegrationChecks", "python_ssh_fixture.py");
if (!File.Exists(fixturePython)) fixturePython = Path.Combine(Environment.CurrentDirectory, "openviya-ops", "tests", "IntegrationChecks", "python_ssh_fixture.py");
var fixtureProcess = default(Process);
var httpListener = default(TcpListener);
using var operationStop = new CancellationTokenSource();
var httpStop = CancellationTokenSource.CreateLinkedTokenSource(operationStop.Token);
var httpTask = Task.CompletedTask;
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; operationStop.Cancel(); };
Console.CancelKeyPress += cancelHandler;
try
{
    await Execute(Path.Combine(systemSsh, "ssh-keygen.exe"), operationStop.Token, "-t", "ed25519", "-N", "", "-f", key);
    var targetPort = FreePort();
    httpListener = new TcpListener(IPAddress.Loopback, targetPort);
    httpListener.Start();
    httpTask = ServeHttpAsync(httpListener, httpStop.Token);

    fixtureProcess = new Process
    {
        StartInfo = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }
    };
    fixtureProcess.StartInfo.ArgumentList.Add(fixturePython);
    fixtureProcess.StartInfo.ArgumentList.Add("--port"); fixtureProcess.StartInfo.ArgumentList.Add(sshPort.ToString());
    fixtureProcess.StartInfo.ArgumentList.Add("--target-port"); fixtureProcess.StartInfo.ArgumentList.Add(targetPort.ToString());
    fixtureProcess.StartInfo.ArgumentList.Add("--client-public-key"); fixtureProcess.StartInfo.ArgumentList.Add(key + ".pub");
    var opsRoot = Directory.GetParent(Path.GetDirectoryName(fixturePython)!)!.Parent!.FullName;
    fixtureProcess.StartInfo.Environment["PYTHONPATH"] = Path.Combine(opsRoot, ".tools", "ssh-fixture");
    Check(fixtureProcess.Start(), "Could not start local AsyncSSH fixture.");
    WindowsJob.AttachAndOwn(fixtureProcess);
    var fixtureStderr = fixtureProcess.StandardError.ReadToEndAsync();
    using (var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationStop.Token))
    {
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var line = await fixtureProcess.StandardOutput.ReadLineAsync(readyTimeout.Token)
            ?? throw new InvalidOperationException("Local AsyncSSH fixture exited before reporting its host fingerprint: " + await fixtureStderr);
        var fixtureIdentity = JsonDocument.Parse(line).RootElement;
        Check(fixtureIdentity.GetProperty("ready").GetBoolean(), "Local SSH fixture did not become ready.");
        var fixtureType = fixtureIdentity.GetProperty("hostKeyType").GetString()!;
        var fixtureBlob = fixtureIdentity.GetProperty("hostKey").GetString()!;
        var fixtureFingerprint = fixtureIdentity.GetProperty("fingerprint").GetString()!;

        var paths = new AppPaths(Path.Combine(temp, "registry"));
        var ssh = new SshService(paths);
        var profile = new ServerProfile { Name = "Local SSH fixture", Host = "127.0.0.1", SshPort = sshPort, UserName = "root", IdentityFile = key, PanelDomain = "panel-test.openviya.online", PanelPort = targetPort };
        IReadOnlyList<HostKeyCandidate> candidates = [];
        for (var attempt = 0; attempt < 10 && candidates.Count == 0; attempt++)
        {
            try { candidates = await ssh.ScanHostKeysAsync(profile, operationStop.Token); }
            catch (InvalidOperationException) { await Task.Delay(250); }
        }
        var selected = candidates.FirstOrDefault(c => c.KeyType == fixtureType) ?? throw new InvalidOperationException("Fixture SSH server did not start with the expected host key.");
        Check(selected.Fingerprint == fixtureFingerprint && selected.PublicKey == fixtureBlob, "Scanned host key must match the independently reported in-memory fixture key.");
        var managed = await SshService.ScanManagedHostKeyAsync(profile, operationStop.Token);
        Check(managed.Single().Fingerprint == fixtureFingerprint && managed.Single().PublicKey == fixtureBlob, "Managed Windows-KEX fallback must capture the exact independently generated host key.");
        Check(!File.Exists(paths.KnownHostsPath), "Both scanner paths must leave the trust store untouched until explicit acceptance.");
        Check((await ssh.CheckConnectionAsync(profile, operationStop.Token)).Status == ProbeStatus.Failure, "Untrusted SSH server must fail closed.");
        await ssh.TrustHostKeyAsync(profile, selected, operationStop.Token);
        var authResult = await ssh.CheckConnectionAsync(profile, operationStop.Token);
        if (authResult.Status != ProbeStatus.Success)
        {
            var details = await CaptureSshDiagnostic(Path.Combine(systemSsh, "ssh.exe"), Path.Combine(paths.SshDirectory, SshService.Alias(profile.Id) + ".conf"), key, SshService.Alias(profile.Id));
            try { fixtureProcess.Kill(entireProcessTree: true); } catch { }
            try { await fixtureProcess.WaitForExitAsync(); } catch { }
            var serverDetails = await fixtureStderr;
            throw new InvalidOperationException($"Explicitly trusted fixture must authenticate with its disposable key. Isolated SSH details: {details}. Fixture events: {serverDetails.Trim()}");
        }

        await using var tunnels = new TunnelManager(ssh);
        var running = await tunnels.StartAsync(profile, operationStop.Token);
        Check(running.State == TunnelState.Active && running.ProcessId is not null, "SSH must own the listener before it becomes Active.");
        var repeated = await tunnels.StartAsync(profile, operationStop.Token);
        Check(repeated.RunId == running.RunId, "Repeated start must preserve the existing owned process.");
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        Check((await client.GetAsync($"http://127.0.0.1:{running.LocalPort}/", operationStop.Token)).IsSuccessStatusCode, "Real HTTP bytes must cross the SSH forward into the loopback fixture.");
        var strictTls = await new DiagnosticsService().CheckPanelThroughTunnelAsync(profile, running.LocalPort, operationStop.Token);
        Check(strictTls.Status == ProbeStatus.Failure, "An HTTP-only target must not pass HTTPS verification.");

        var second = profile with { Id = Guid.NewGuid(), Name = "Second fixture profile" };
        await ssh.TrustHostKeyAsync(second, selected, operationStop.Token);
        var secondRun = await tunnels.StartAsync(second, operationStop.Token);
        Check(secondRun.State == TunnelState.Active && secondRun.LocalPort != running.LocalPort, $"Simultaneous tunnels must use separate local ports (first={running.State}/{running.LocalPort}, second={secondRun.State}/{secondRun.LocalPort}: {secondRun.Message}).");
        await tunnels.StopAsync(profile.Id);
        Check((await client.GetAsync($"http://127.0.0.1:{secondRun.LocalPort}/", operationStop.Token)).IsSuccessStatusCode, "Stopping one tunnel must leave the second operational.");
        await tunnels.StopAllAsync();
        Check(tunnels.Snapshots.All(t => t.State == TunnelState.Stopped), "Owned tunnel states must be stopped after StopAll.");
        Console.WriteLine("Python loopback integration passed: fixture fingerprint, explicit trust, key auth, owned listeners, simultaneous forwards, duplicate start, isolated stop and TLS fail-closed.");
    }
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    try { operationStop.Cancel(); } catch { }
    try { await httpStop.CancelAsync(); } catch { }
    try { httpListener?.Stop(); } catch { }
    try { await httpTask; } catch (OperationCanceledException) { }
    if (fixtureProcess is not null)
    {
        try { if (!fixtureProcess.HasExited) fixtureProcess.Kill(entireProcessTree: true); } catch { }
        try { await fixtureProcess.WaitForExitAsync(); } catch { }
        fixtureProcess.Dispose();
    }
    try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    httpStop.Dispose();
}
