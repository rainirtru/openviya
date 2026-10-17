namespace OpenViya.Ops.Core;

public enum ServerRole { App, Control, VpnNode }
public enum ProbeStatus { Unknown, Success, Warning, Failure }
public enum TunnelState { Stopped, Starting, Verifying, Active, Degraded, Failed, Stopping }
public enum SubdomainMode { Automatic, Manual }
public enum PanelOpenMode { Tunnel, PublicHttps }
public enum VpsPanelAccess { Tunnel, Both }

public sealed record ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public ServerRole Role { get; init; } = ServerRole.VpnNode;
    public string Region { get; init; } = "usa";
    public int NodeIndex { get; init; } = 1;
    public string Host { get; init; } = "";
    public int SshPort { get; init; } = 22;
    public string UserName { get; init; } = "vpsadmin";
    public string IdentityFile { get; init; } = "";
    public string PanelDomain { get; init; } = "";
    public int PanelPort { get; init; } = 38471;
    public string PanelBasePath { get; init; } = "/";
    public VpsPanelAccess PanelAccess { get; init; } = VpsPanelAccess.Tunnel;
    public PanelOpenMode PanelOpenMode { get; init; } = PanelOpenMode.Tunnel;
    public string PanelAllowedIpsText { get; init; } = "";
    public int? LocalPort { get; init; }
    public string VpnDomain { get; init; } = "";
    public SubdomainMode SubdomainMode { get; init; } = SubdomainMode.Automatic;
    public string DnsZone { get; init; } = "openviya.online";
    public string DnsAddress { get; init; } = "";
    public int VpnPort { get; init; } = 443;
    public Guid? ControlServerId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AppPaths(string Root)
{
    public string DatabasePath => Path.Combine(Root, "ops.db");
    public string KnownHostsPath => Path.Combine(Root, "known_hosts");
    public string SshDirectory => Path.Combine(Root, "ssh");
    public static AppPaths Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenViya", "Ops"));
}

public sealed record ProbeResult(string Name, ProbeStatus Status, string Message, DateTimeOffset ObservedAt)
{
    public static ProbeResult Create(string name, ProbeStatus status, string message) => new(name, status, message, DateTimeOffset.UtcNow);
}
public sealed record HostKeyCandidate(string KeyType, string PublicKey, string Fingerprint);
public sealed record TunnelSnapshot(Guid ServerId, Guid RunId, int LocalPort, TunnelState State, string Message, DateTimeOffset UpdatedAt, int? ProcessId = null);
public sealed record HostsPlan(string Domain, string Address, bool AlreadyConfigured, bool Conflict, string Message, bool OwnedByApplication = false);

public interface IProfileStore
{
    Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ExportAsync(string filePath, CancellationToken cancellationToken = default);
    Task<int> ImportAsync(string filePath, CancellationToken cancellationToken = default);
}
public interface ISshService
{
    Task<IReadOnlyList<HostKeyCandidate>> ScanHostKeysAsync(ServerProfile server, CancellationToken cancellationToken = default);
    Task TrustHostKeyAsync(ServerProfile server, HostKeyCandidate candidate, CancellationToken cancellationToken = default);
    Task<ProbeResult> CheckConnectionAsync(ServerProfile server, CancellationToken cancellationToken = default);
    Task OpenTerminalAsync(ServerProfile server, CancellationToken cancellationToken = default);
}
public interface ITunnelManager : IAsyncDisposable
{
    event EventHandler<TunnelSnapshot>? Changed;
    IReadOnlyList<TunnelSnapshot> Snapshots { get; }
    Task<TunnelSnapshot> StartAsync(ServerProfile server, CancellationToken cancellationToken = default);
    Task StopAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
public interface IDiagnosticsService
{
    Task<IReadOnlyList<ProbeResult>> CheckServerAsync(ServerProfile server, CancellationToken cancellationToken = default);
    Task<ProbeResult> CheckPanelThroughTunnelAsync(ServerProfile server, int localPort, CancellationToken cancellationToken = default);
}
public interface IHostsService
{
    Task<HostsPlan> InspectAsync(string domain, CancellationToken cancellationToken = default);
    Task ApplyAsync(string domain, CancellationToken cancellationToken = default);
    Task RemoveAsync(string domain, CancellationToken cancellationToken = default);
}
