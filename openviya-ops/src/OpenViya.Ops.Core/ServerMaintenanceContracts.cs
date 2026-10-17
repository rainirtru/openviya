namespace OpenViya.Ops.Core;

public sealed record VpsMaintenanceSnapshot(
    Guid ServerId,
    DateTimeOffset CheckedAt,
    string InstalledVersion,
    string LatestVersion,
    bool UpdateAvailable,
    int PanelPort,
    VpsPanelAccess PanelAccess,
    string AllowedIpsText,
    DateTimeOffset? CertificateExpiresAt,
    bool CertificateManagedByLetsEncrypt,
    bool PanelHealthy,
    bool FirewallActive,
    bool PanelFirewallRulesMatch,
    bool RollbackPending,
    IReadOnlyList<VpsBackupInfo> Backups,
    string Message);

public sealed record VpsBackupInfo(string Name, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record VpsMaintenancePlan(
    Guid ServerId,
    int CurrentPanelPort,
    VpsPanelAccess CurrentAccess,
    string CurrentAllowedIpsText,
    int RequestedPanelPort,
    VpsPanelAccess RequestedAccess,
    string RequestedAllowedIpsText,
    IReadOnlyList<string> Changes,
    bool RequiresConfirmation);

public sealed record VpsMaintenanceResult(bool Succeeded, string Message, VpsMaintenanceSnapshot? Snapshot = null, string? BackupName = null);

public interface IVpsServerMaintenanceService
{
    Task<HostKeyCandidate> GetTrustedHostKeyAsync(ServerProfile profile, CancellationToken cancellationToken = default);
    Task<VpsMaintenanceSnapshot> InspectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default);
    Task<VpsMaintenancePlan> PlanAccessChangeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, PanelOpenMode mode, string allowedIpsText, int panelPort, CancellationToken cancellationToken = default);
    Task<VpsMaintenanceResult> ApplyAccessChangeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, VpsMaintenancePlan plan, bool confirmHighRisk, CancellationToken cancellationToken = default);
    Task<VpsMaintenanceResult> CheckCertificateAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default);
    Task<VpsMaintenanceResult> RenewCertificateAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, bool confirmHighRisk, CancellationToken cancellationToken = default);
    Task<VpsMaintenanceResult> CreateBackupAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadBackupAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, string backupName, CancellationToken cancellationToken = default);
    Task<VpsMaintenanceResult> CheckUpdatesAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default);
}
