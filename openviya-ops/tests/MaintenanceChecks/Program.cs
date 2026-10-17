using OpenViya.Ops.Core;
using OpenViya.Ops.Desktop;

var profile = new ServerProfile
{
    Name = "Maintenance fake",
    Host = "192.0.2.15",
    UserName = "vpsadmin",
    PanelDomain = "panel.example.test",
    PanelPort = 38471,
    PanelAccess = VpsPanelAccess.Tunnel,
    PanelOpenMode = PanelOpenMode.Tunnel
};
var service = new FakeMaintenanceService(profile.Id);
var profiles = new MemoryProfiles(profile);
var states = new MemoryStates();
var refreshed = 0;
var vm = new ServerMaintenanceViewModel(profile, service, profiles, states, _ => { refreshed++; return Task.CompletedTask; });
Require(service.CallCount == 0, "Constructing the ViewModel must not contact a VPS.");

var access = new VpsSshAccess { UserName = profile.UserName, Method = VpsAuthMethod.Password, Password = "ephemeral-test-secret" };
await vm.InspectAsync(access);
Require(vm.Snapshot?.PanelPort == 38471, "Explicit inspect should publish the actual snapshot.");
Require(vm.AllowedIpsText == "", "A canonical any-source snapshot should show an empty operator IP field.");
Require(vm.HostFingerprint == "SHA256:fake-confirmed-key", "The stored trusted fingerprint should be exposed as read-only context.");

vm.SetDraft("42000", PanelOpenMode.PublicHttps, "198.51.100.7\n2001:db8::7");
await vm.PlanAccessChangeAsync(access, 42000, PanelOpenMode.PublicHttps, vm.AllowedIpsText);
Require(vm.Plan is { Changes.Count: > 0, RequestedPanelPort: 42000, RequestedAccess: VpsPanelAccess.Both }, "A changed draft should produce a concrete plan.");

var stopped = 0;
var active = true;
await vm.ApplyAccessChangeAsync(access, confirmChanges: true, confirmTunnelStop: true,
    _ => active,
    _ => { stopped++; active = false; return Task.CompletedTask; });
Require(stopped == 1, "Port changes should stop the active owned tunnel only after explicit caller confirmation.");
Require(profiles.Saved?.PanelPort == 42000 && profiles.Saved.PanelOpenMode == PanelOpenMode.PublicHttps, "A confirmed remote plan should update the selected local profile.");
Require(profiles.Saved?.PanelAllowedIpsText == "198.51.100.7\n2001:db8::7", "The profile should retain operator-facing IP literals.");
Require(states.Saved?.PanelPort == 42000 && states.Saved.PanelAccess == VpsPanelAccess.Both, "The verified server snapshot should be persisted as observed state.");
Require(refreshed == 1, "Successful settings persistence should refresh the selected profile card.");

await vm.CheckCertificateAsync(access);
await vm.RenewCertificateAsync(access);
await vm.CreateBackupAsync(access);
var downloaded = await vm.DownloadBackupAsync(access, "backup-20261017T120000-1.tar.gz");
await vm.CheckUpdatesAsync(access);
Require(downloaded.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Backup download should return service bytes for the explicit file save flow.");
Require(service.CertificateChecks == 1 && service.CertificateRenewals == 1 && service.BackupCreates == 1 && service.BackupDownloads == 1 && service.UpdateChecks == 1,
    "Each maintenance action should delegate once to the explicit service method.");
Require(!vm.IsBusy, "The ViewModel should leave busy state after all operations.");
Console.WriteLine("Installed server maintenance ViewModel checks passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeMaintenanceService(Guid serverId) : IVpsServerMaintenanceService
{
    public int CallCount { get; private set; }
    public int CertificateChecks { get; private set; }
    public int CertificateRenewals { get; private set; }
    public int BackupCreates { get; private set; }
    public int BackupDownloads { get; private set; }
    public int UpdateChecks { get; private set; }
    public Task<HostKeyCandidate> GetTrustedHostKeyAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new HostKeyCandidate("ssh-ed25519", "ZmFrZQ==", "SHA256:fake-confirmed-key"));
    }
    public Task<VpsMaintenanceSnapshot> InspectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(Snapshot(profile.PanelPort, profile.PanelAccess, profile.PanelAllowedIpsText));
    }
    public Task<VpsMaintenancePlan> PlanAccessChangeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, PanelOpenMode mode, string allowedIpsText, int panelPort, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new VpsMaintenancePlan(serverId, 38471, VpsPanelAccess.Tunnel, "any", panelPort,
            mode == PanelOpenMode.PublicHttps ? VpsPanelAccess.Both : VpsPanelAccess.Tunnel,
            allowedIpsText == "" ? "any" : string.Join('\n', allowedIpsText.Split('\n').Select(x => x.Contains(':') ? x + "/128" : x + "/32")),
            ["Порт панели: 38471 → 42000", "Доступ к панели: туннель → публичный HTTPS"], true));
    }
    public Task<VpsMaintenanceResult> ApplyAccessChangeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, VpsMaintenancePlan plan, bool confirmHighRisk, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new VpsMaintenanceResult(true, "Тестовые настройки применены.", Snapshot(plan.RequestedPanelPort, plan.RequestedAccess, plan.RequestedAllowedIpsText)));
    }
    public Task<VpsMaintenanceResult> CheckCertificateAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        CallCount++; CertificateChecks++;
        return Task.FromResult(new VpsMaintenanceResult(true, "Сертификат проверен.", Snapshot(profile.PanelPort, profile.PanelAccess, "any")));
    }
    public Task<VpsMaintenanceResult> RenewCertificateAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, bool confirmHighRisk, CancellationToken cancellationToken = default)
    {
        CallCount++; CertificateRenewals++;
        return Task.FromResult(new VpsMaintenanceResult(true, "Тестовый сертификат продлён.", Snapshot(profile.PanelPort, profile.PanelAccess, "any")));
    }
    public Task<VpsMaintenanceResult> CreateBackupAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        CallCount++; BackupCreates++;
        return Task.FromResult(new VpsMaintenanceResult(true, "Копия создана.", Snapshot(profile.PanelPort, profile.PanelAccess, "any"), "backup-20261017T120000-1.tar.gz"));
    }
    public Task<byte[]> DownloadBackupAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, string backupName, CancellationToken cancellationToken = default)
    {
        CallCount++; BackupDownloads++;
        return Task.FromResult(new byte[] { 1, 2, 3, 4 });
    }
    public Task<VpsMaintenanceResult> CheckUpdatesAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        CallCount++; UpdateChecks++;
        return Task.FromResult(new VpsMaintenanceResult(true, "3x-ui обновлена.", Snapshot(profile.PanelPort, profile.PanelAccess, "any")));
    }
    private VpsMaintenanceSnapshot Snapshot(int port, VpsPanelAccess access, string allowed) =>
        new(serverId, DateTimeOffset.UtcNow, "2.4.0", "2.4.1", true, port, access, allowed,
            DateTimeOffset.UtcNow.AddDays(20), true, true, true, true, false,
            [new VpsBackupInfo("backup-20261017T120000-1.tar.gz", 4, DateTimeOffset.UtcNow)], "Подставной снимок.");
}

sealed class MemoryProfiles(ServerProfile initial) : IProfileStore
{
    public ServerProfile? Saved { get; private set; }
    public Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ServerProfile>>([Saved ?? initial]);
    public Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default) { Saved = profile; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ExportAsync(string filePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> ImportAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

sealed class MemoryStates : IServerStateStore
{
    public ServerObservedState? Saved { get; private set; }
    public Task<ServerObservedState?> LoadAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(Saved);
    public Task SaveAsync(ServerObservedState state, CancellationToken cancellationToken = default) { Saved = state; return Task.CompletedTask; }
}
