using OpenViya.Ops.Core;
using OpenViya.Ops.Desktop;

var profile = new ServerProfile
{
    Name = "Panel test",
    Role = ServerRole.Control,
    Host = "198.51.100.10",
    PanelDomain = "panel-test.openviya.online",
    PanelPort = 38471,
    PanelBasePath = "/private-test-path/"
};

await VerifyTunnelModeStartsAndChecksBeforeOpening(profile);
await VerifyPublicModeOnlyRemovesOwnedHosts(profile);
await VerifyDeclinedHostChangesStopOpening(profile);
await VerifyCancellationHasNoSideEffects(profile);
await VerifyTlsFailureNeverLaunchesBrowser(profile);
Console.WriteLine("Panel opening orchestration checks passed.");

static async Task VerifyTunnelModeStartsAndChecksBeforeOpening(ServerProfile profile)
{
    var tunnels = new FakeTunnels();
    var hosts = new FakeHosts();
    var diagnostics = new FakeDiagnostics { Probe = ProbeStatus.Success };
    var launched = new List<Uri>();
    var prompts = 0;
    var service = CreateService(tunnels, diagnostics, hosts);
    var result = await service.OpenAsync(profile with { PanelOpenMode = PanelOpenMode.Tunnel },
        (_, _) => { prompts++; return Task.FromResult(true); },
        (_, _) => Task.FromResult(false),
        launched.Add);
    Require(result.WasOpened, "Tunnel mode should open after accepted hosts change and successful TLS.");
    Require(tunnels.StartCount == 1, "Tunnel mode should start the owned tunnel automatically.");
    Require(hosts.ApplyCount == 1 && prompts == 1, "A missing hosts record should require one explicit confirmation before apply.");
    Require(diagnostics.TunnelChecks == 1 && launched.Count == 1, "TLS should pass before browser launch.");
    Require(launched[0].Port == FakeTunnels.LocalPort && launched[0].AbsolutePath == profile.PanelBasePath, "Tunnel URL must use local port and full protected path.");
}

static async Task VerifyPublicModeOnlyRemovesOwnedHosts(ServerProfile profile)
{
    var tunnels = new FakeTunnels();
    var hosts = new FakeHosts { State = HostsState.Owned };
    var diagnostics = new FakeDiagnostics { Probe = ProbeStatus.Success };
    var launched = new List<Uri>();
    var publicChecks = 0;
    var service = new PanelOpeningService(tunnels, diagnostics, hosts, (_, _) =>
    {
        publicChecks++;
        return Task.FromResult((ProbeResult.Create("Public", ProbeStatus.Success, "ok"), (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(30)));
    });
    var result = await service.OpenAsync(profile with { PanelOpenMode = PanelOpenMode.PublicHttps, PanelAccess = VpsPanelAccess.Both },
        (_, _) => Task.FromResult(false),
        (_, _) => Task.FromResult(true),
        launched.Add);
    Require(result.WasOpened && hosts.RemoveCount == 1, "An owned loopback entry should be removed only after the explicit callback accepts.");
    Require(tunnels.StartCount == 0 && publicChecks == 1, "Public mode should check HTTPS directly without starting a tunnel.");
    Require(launched.Single().Port == profile.PanelPort && launched.Single().AbsolutePath == profile.PanelBasePath, "Public URL must use remote port and full protected path.");

    var unownedHosts = new FakeHosts { State = HostsState.UnownedLoopback };
    var unownedLaunches = new List<Uri>();
    var unownedTunnels = new FakeTunnels();
    var unownedService = CreateService(unownedTunnels, diagnostics, unownedHosts);
    var unowned = await unownedService.OpenAsync(profile with { PanelOpenMode = PanelOpenMode.PublicHttps, PanelAccess = VpsPanelAccess.Both },
        (_, _) => Task.FromResult(false),
        (_, _) => Task.FromResult(true),
        unownedLaunches.Add);
    Require(!unowned.WasOpened && unownedHosts.RemoveCount == 0 && unownedLaunches.Count == 0, "A user-owned loopback entry must block public opening and must never be removed.");
}

static async Task VerifyTlsFailureNeverLaunchesBrowser(ServerProfile profile)
{
    var tunnels = new FakeTunnels();
    tunnels.SnapshotsList.Add(new TunnelSnapshot(profile.Id, Guid.NewGuid(), 20531, TunnelState.Active, "Active", DateTimeOffset.UtcNow));
    var hosts = new FakeHosts { State = HostsState.Owned };
    var diagnostics = new FakeDiagnostics { Probe = ProbeStatus.Failure };
    var launches = new List<Uri>();
    var service = CreateService(tunnels, diagnostics, hosts);
    var result = await service.OpenAsync(profile with { PanelOpenMode = PanelOpenMode.Tunnel },
        (_, _) => Task.FromResult(false),
        (_, _) => Task.FromResult(false),
        launches.Add);
    Require(!result.WasOpened && launches.Count == 0, "TLS failure must block browser launch.");
}

static async Task VerifyDeclinedHostChangesStopOpening(ServerProfile profile)
{
    var tunnels = new FakeTunnels();
    var hosts = new FakeHosts();
    var diagnostics = new FakeDiagnostics { Probe = ProbeStatus.Success };
    var launches = new List<Uri>();
    var service = CreateService(tunnels, diagnostics, hosts);
    var result = await service.OpenAsync(profile with { PanelOpenMode = PanelOpenMode.Tunnel },
        (_, _) => Task.FromResult(false),
        (_, _) => Task.FromResult(false),
        launches.Add);
    Require(!result.WasOpened && hosts.ApplyCount == 0 && diagnostics.TunnelChecks == 0 && launches.Count == 0,
        "Declining the hosts change must stop before mutation, TLS and browser launch.");
}

static async Task VerifyCancellationHasNoSideEffects(ServerProfile profile)
{
    var tunnels = new FakeTunnels();
    var hosts = new FakeHosts();
    var diagnostics = new FakeDiagnostics { Probe = ProbeStatus.Success };
    var launches = new List<Uri>();
    var service = CreateService(tunnels, diagnostics, hosts);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await service.OpenAsync(profile, (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(true), launches.Add, cancellation.Token);
        throw new InvalidOperationException("Expected canceled panel opening to stop before work.");
    }
    catch (OperationCanceledException) { }
    Require(tunnels.StartCount == 0 && hosts.ApplyCount == 0 && launches.Count == 0,
        "Cancellation must stop before tunnel, hosts or browser side effects.");
}

static PanelOpeningService CreateService(FakeTunnels tunnels, FakeDiagnostics diagnostics, FakeHosts hosts) =>
    new(tunnels, diagnostics, hosts, (_, _) => Task.FromResult((ProbeResult.Create("Public", ProbeStatus.Success, "ok"), (DateTimeOffset?)null)));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeTunnels : ITunnelManager
{
    public const int LocalPort = 20531;
    public int StartCount { get; private set; }
    public List<TunnelSnapshot> SnapshotsList { get; } = [];
    public IReadOnlyList<TunnelSnapshot> Snapshots => SnapshotsList;
    public event EventHandler<TunnelSnapshot>? Changed;
    public Task<TunnelSnapshot> StartAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        StartCount++;
        var snapshot = new TunnelSnapshot(server.Id, Guid.NewGuid(), LocalPort, TunnelState.Active, "Active", DateTimeOffset.UtcNow);
        SnapshotsList.Add(snapshot);
        Changed?.Invoke(this, snapshot);
        return Task.FromResult(snapshot);
    }
    public Task StopAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeDiagnostics : IDiagnosticsService
{
    public ProbeStatus Probe { get; init; }
    public int TunnelChecks { get; private set; }
    public Task<IReadOnlyList<ProbeResult>> CheckServerAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProbeResult>>([]);
    public Task<ProbeResult> CheckPanelThroughTunnelAsync(ServerProfile server, int localPort, CancellationToken cancellationToken = default)
    {
        TunnelChecks++;
        return Task.FromResult(ProbeResult.Create("Tunnel", Probe, Probe.ToString()));
    }
}

sealed class FakeHosts : IHostsService
{
    public HostsState State { get; set; }
    public int ApplyCount { get; private set; }
    public int RemoveCount { get; private set; }
    public Task<HostsPlan> InspectAsync(string domain, CancellationToken cancellationToken = default) => Task.FromResult(State switch
    {
        HostsState.Owned => new HostsPlan(domain, "127.0.0.1", true, false, "owned", true),
        HostsState.UnownedLoopback => new HostsPlan(domain, "127.0.0.1", true, false, "manual"),
        HostsState.Conflict => new HostsPlan(domain, "192.0.2.1", false, true, "conflict"),
        _ => new HostsPlan(domain, "127.0.0.1", false, false, "missing")
    });
    public Task ApplyAsync(string domain, CancellationToken cancellationToken = default) { ApplyCount++; State = HostsState.Owned; return Task.CompletedTask; }
    public Task RemoveAsync(string domain, CancellationToken cancellationToken = default) { RemoveCount++; State = HostsState.Missing; return Task.CompletedTask; }
}

enum HostsState { Missing, Owned, UnownedLoopback, Conflict }
