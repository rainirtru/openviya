namespace OpenViya.Ops.Core;

/// <summary>Latest non-secret observations about a server and its panel configuration.</summary>
public sealed record ServerObservedState(
    Guid ServerId,
    DateTimeOffset CheckedAt,
    ProbeStatus SshStatus,
    ProbeStatus PanelStatus,
    string Message,
    DateTimeOffset? CertificateExpiresAt = null,
    int? PanelPort = null,
    VpsPanelAccess? PanelAccess = null,
    string? PanelAllowedIpsText = null);

public interface IServerStateStore
{
    Task<ServerObservedState?> LoadAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task SaveAsync(ServerObservedState state, CancellationToken cancellationToken = default);
}
