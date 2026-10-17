namespace OpenViya.Ops.Core;

public enum DnsPlanAction { Create, AlreadyConfigured, Conflict }
public enum DnsBlockReason { Unknown, RecordConflict }
public sealed record DnsZoneCheck(string Zone, bool Accessible, IReadOnlyList<string> NameServers, string Message);
public sealed record DnsPlanItem(string Fqdn, string Type, string Address, DnsPlanAction Action, string Message, long? RecordId = null, bool SubdomainExists = false, DnsBlockReason BlockReason = DnsBlockReason.Unknown);
public sealed record DnsPlan(ServerProfile Profile, string Zone, string Address, int Ttl, IReadOnlyList<DnsPlanItem> Items, IReadOnlyList<string> NameServers, DateTimeOffset CreatedAt)
{
    public bool CanApply => Profile.SubdomainMode == SubdomainMode.Automatic && Items.Count > 0 && Items.All(x => x.Action != DnsPlanAction.Conflict);
}
public sealed record DnsApplyItem(string Fqdn, ProbeStatus Status, string Message, long? RecordId = null);
public sealed record DnsApplyResult(IReadOnlyList<DnsApplyItem> Items);
public sealed record DnsManagedRecord(Guid ServerId, string Fqdn, long RecordId, string Address, DateTimeOffset UpdatedAt);
public interface IDnsTokenStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string token, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
public interface IDnsRecordStore
{
    Task SaveAsync(DnsManagedRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DnsManagedRecord>> LoadAsync(Guid serverId, CancellationToken cancellationToken = default);
}
public interface IDnsProviderService
{
    Task<DnsZoneCheck> CheckZoneAsync(string zone, CancellationToken cancellationToken = default);
    Task<DnsPlan> BuildPlanAsync(ServerProfile profile, int ttl = 600, CancellationToken cancellationToken = default);
    Task<DnsApplyResult> ApplyAsync(DnsPlan plan, CancellationToken cancellationToken = default);
}
public interface IPublicDnsService
{
    Task<IReadOnlyList<ProbeResult>> CheckAsync(DnsPlan plan, CancellationToken cancellationToken = default);
}
