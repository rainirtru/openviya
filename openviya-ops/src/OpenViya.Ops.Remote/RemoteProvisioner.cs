using OpenViya.Ops.Core;
using System.Security.Cryptography;

namespace OpenViya.Ops.Remote;

/// <summary>Starts a durable server job, then polls it. Cancelling client polling never cancels accepted server work.</summary>
public sealed class RemoteVpsProvisioner(RemoteOpsClient client) : IVpsProvisioner
{
    public async Task<VpsProvisionResult> RunAsync(VpsProvisionRequest request, IProgress<VpsProvisionRun>? progress = null, CancellationToken cancellationToken = default)
    {
        var initial = await CredentialAsync(request.InitialAccess, cancellationToken);
        var adminKey = await ReadPrivateKeyAsync(request.AdminIdentityFile, cancellationToken);
        var profile = request.Profile with { IdentityFile = "" };
        var dto = new RemoteProvisionRequest(request.RunId, profile, initial, request.HostKey, adminKey, request.AdminKeyPassphrase,
            request.AcmeEmail, request.PanelAccess, request.PanelCidr, request.FreshServerConfirmed,
            request.LetsEncryptTermsAccepted, request.ProviderFirewallConfirmed);
        var run = await client.GetOptionalAsync<VpsProvisionRun>($"api/provisioning/runs/{request.RunId:D}", cancellationToken).ConfigureAwait(false);
        if (run is null)
            run = await client.PostAsync<VpsProvisionRun>("api/provisioning/runs", dto, cancellationToken).ConfigureAwait(false);
        else if (run.State is VpsProvisionRunState.Failed or VpsProvisionRunState.NeedsOperator or VpsProvisionRunState.Paused)
            run = await client.PostAsync<VpsProvisionRun>($"api/provisioning/runs/{request.RunId:D}/resume", dto, cancellationToken).ConfigureAwait(false);
        progress?.Report(run);
        while (run.State is VpsProvisionRunState.Running or VpsProvisionRunState.StatusUnknown)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            run = await client.GetAsync<VpsProvisionRun>($"api/provisioning/runs/{run.RunId:D}", cancellationToken).ConfigureAwait(false);
            progress?.Report(run);
        }
        var saved = await client.GetAsync<ServerProfile>($"api/profiles/{run.ServerId:D}", cancellationToken).ConfigureAwait(false);
        saved = saved with { IdentityFile = request.Profile.IdentityFile };
        var hasCredentials = run.State == VpsProvisionRunState.Complete;
        return new VpsProvisionResult(run, saved, hasCredentials);
    }

    internal static async Task<RemoteSshCredential> CredentialAsync(VpsSshAccess access, CancellationToken ct)
    {
        var key = access.Method == VpsAuthMethod.PrivateKey ? await ReadPrivateKeyAsync(access.IdentityFile, ct) : null;
        return new RemoteSshCredential(access.Method, access.UserName, access.Method == VpsAuthMethod.Password ? access.Password : null, key,
            access.Method == VpsAuthMethod.PrivateKey ? access.KeyPassphrase : null);
    }

    internal static async Task<string?> ReadPrivateKeyAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is 0 or > 128 * 1024) throw new InvalidOperationException("Файл закрытого SSH-ключа отсутствует, пуст или превышает 128 КиБ.");
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length is 0 or > 128 * 1024) { CryptographicOperations.ZeroMemory(bytes); throw new InvalidOperationException("Файл закрытого SSH-ключа превышает 128 КиБ."); }
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class RemoteVpsMaintenanceService(RemoteOpsClient client) : IVpsServerMaintenanceService
{
    private static string Root(Guid id) => $"api/servers/{id:D}/maintenance/";
    public Task<HostKeyCandidate> GetTrustedHostKeyAsync(ServerProfile profile, CancellationToken ct = default) => client.GetAsync<HostKeyCandidate>($"api/servers/{profile.Id:D}/host-key", ct);
    public async Task<VpsMaintenanceSnapshot> InspectAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, CancellationToken ct = default) => await client.PostAsync<VpsMaintenanceSnapshot>(Root(p.Id) + "inspect", await Request(a, k, ct), ct);
    public async Task<VpsMaintenancePlan> PlanAccessChangeAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, PanelOpenMode mode, string ips, int port, CancellationToken ct = default) => await client.PostAsync<VpsMaintenancePlan>(Root(p.Id) + "plan-access", new RemoteAccessChangeRequest(await RemoteVpsProvisioner.CredentialAsync(a, ct), k, mode, ips, port, false), ct);
    public async Task<VpsMaintenanceResult> ApplyAccessChangeAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, VpsMaintenancePlan plan, bool confirm, CancellationToken ct = default)
    {
        var change = new RemoteAccessChangeRequest(await RemoteVpsProvisioner.CredentialAsync(a, ct), k,
            plan.RequestedAccess == VpsPanelAccess.Tunnel ? PanelOpenMode.Tunnel : PanelOpenMode.PublicHttps,
            plan.RequestedAllowedIpsText, plan.RequestedPanelPort, confirm);
        return await RunJobAsync(new RemoteMaintenanceJobRequest(Guid.NewGuid(), p.Id, "apply-access", AccessChange: change, Plan: plan), ct);
    }
    public async Task<VpsMaintenanceResult> CheckCertificateAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, CancellationToken ct = default) => await client.PostAsync<VpsMaintenanceResult>(Root(p.Id) + "check-certificate", await Request(a, k, ct), ct);
    public async Task<VpsMaintenanceResult> RenewCertificateAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, bool confirm, CancellationToken ct = default) => await RunJobAsync(new RemoteMaintenanceJobRequest(Guid.NewGuid(), p.Id, "renew-certificate", ActionRequest: new RemoteMaintenanceActionRequest(await RemoteVpsProvisioner.CredentialAsync(a, ct), k, confirm)), ct);
    public async Task<VpsMaintenanceResult> CreateBackupAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, CancellationToken ct = default) => await RunJobAsync(new RemoteMaintenanceJobRequest(Guid.NewGuid(), p.Id, "create-backup", Maintenance: await Request(a, k, ct)), ct);
    public async Task<byte[]> DownloadBackupAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, string name, CancellationToken ct = default) =>
        await client.PostBytesAsync(Root(p.Id) + "backups/" + Uri.EscapeDataString(name),
            new RemoteMaintenanceRequest(await RemoteVpsProvisioner.CredentialAsync(a, ct), k), ct);
    public async Task<VpsMaintenanceResult> CheckUpdatesAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, CancellationToken ct = default) => await client.PostAsync<VpsMaintenanceResult>(Root(p.Id) + "updates", await Request(a, k, ct), ct);
    public async Task<IReadOnlyList<RemoteMaintenanceJobResult>> LoadRecentJobsAsync(Guid serverId, CancellationToken ct = default) =>
        await client.GetAsync<List<RemoteMaintenanceJobResult>>($"api/servers/{serverId:D}/maintenance/jobs?take=20", ct).ConfigureAwait(false);
    private static async Task<RemoteMaintenanceRequest> Request(VpsSshAccess a, HostKeyCandidate k, CancellationToken ct) => new(await RemoteVpsProvisioner.CredentialAsync(a, ct), k);

    private async Task<VpsMaintenanceResult> RunJobAsync(RemoteMaintenanceJobRequest request, CancellationToken ct)
    {
        var result = await client.PostAsync<RemoteMaintenanceJobResult>("api/maintenance/jobs", request, ct).ConfigureAwait(false);
        while (result.State is "queued" or "running")
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            result = await client.GetAsync<RemoteMaintenanceJobResult>($"api/maintenance/jobs/{request.RunId:D}", ct).ConfigureAwait(false);
        }
        var success = result.Succeeded ?? result.State == "complete";
        return new VpsMaintenanceResult(success, result.Message, result.Snapshot, result.BackupName);
    }
}
