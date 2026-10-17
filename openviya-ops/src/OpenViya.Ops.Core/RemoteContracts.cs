namespace OpenViya.Ops.Core;

/// <summary>Wire contracts for the LAN operations service. Secret material is only sent on write/action requests.</summary>
public sealed record RemoteSshCredential(VpsAuthMethod Method, string UserName, string? Password = null,
    string? PrivateKeyBase64 = null, string? KeyPassphrase = null)
{
    public override string ToString() => "RemoteSshCredential (credentials hidden)";
}

public sealed record RemoteProvisionRequest(Guid RunId, ServerProfile Profile, RemoteSshCredential InitialAccess,
    HostKeyCandidate HostKey, string? AdminPrivateKeyBase64, string? AdminKeyPassphrase, string AcmeEmail,
    VpsPanelAccess PanelAccess, string PanelCidr, bool FreshServerConfirmed, bool LetsEncryptTermsAccepted,
    bool ProviderFirewallConfirmed)
{
    public override string ToString() => $"RemoteProvisionRequest {RunId} (credentials hidden)";
}

public sealed record RemoteMaintenanceRequest(RemoteSshCredential Access, HostKeyCandidate HostKey);
public sealed record RemoteAccessChangeRequest(RemoteSshCredential Access, HostKeyCandidate HostKey,
    PanelOpenMode Mode, string AllowedIpsText, int PanelPort, bool ConfirmHighRisk)
{
    public override string ToString() => "RemoteAccessChangeRequest (credentials hidden)";
}
public sealed record RemoteMaintenanceActionRequest(RemoteSshCredential Access, HostKeyCandidate HostKey,
    bool ConfirmHighRisk = false, string? BackupName = null)
{
    public override string ToString() => "RemoteMaintenanceActionRequest (credentials hidden)";
}
public sealed record RemoteDnsTokenRequest(string Token)
{
    public override string ToString() => "RemoteDnsTokenRequest (credential hidden)";
}
public sealed record RemoteDnsZoneRequest(string Zone);
public sealed record RemoteDnsPlanRequest(ServerProfile Profile, int Ttl = 600);
public sealed record RemoteImportResult(int Imported);
public sealed record RemoteApiError(string Code, string Message);

/// <summary>Non-secret view of provider DNS credential state.</summary>
public sealed record RemoteDnsCredentialState(bool Configured, DateTimeOffset? UpdatedAt);

public sealed record RemoteAdminKeyRequest(string PrivateKeyBase64, string KeyPassphrase = "")
{
    public override string ToString() => "RemoteAdminKeyRequest (credentials hidden)";
}

public sealed record RemoteMaintenanceJobRequest(Guid RunId, Guid ServerId, string Action,
    RemoteMaintenanceRequest? Maintenance = null, RemoteAccessChangeRequest? AccessChange = null,
    VpsMaintenancePlan? Plan = null, RemoteMaintenanceActionRequest? ActionRequest = null)
{
    public override string ToString() => $"RemoteMaintenanceJobRequest {RunId} {Action} (credentials hidden)";
}

public sealed record RemoteMaintenanceJobResult(Guid RunId, Guid ServerId, string Action, string State,
    bool? Succeeded, string Message, VpsMaintenanceSnapshot? Snapshot = null, string? BackupName = null,
    DateTimeOffset UpdatedAt = default);
