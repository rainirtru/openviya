namespace OpenViya.Ops.Core;

public enum VpsAuthMethod { Password, PrivateKey }
public enum VpsProvisionStage { Created, Preflight, PreparingDns, PreparingAccess, VerifyingAdmin, InstallingPanel, RetrievingCredentials, Complete, Failed, NeedsAttention }
public enum VpsProvisionRunState { Running, StatusUnknown, Paused, Failed, NeedsOperator, Complete }
public enum VpsProvisionStepState { Pending, Running, Done, NeedsAttention }
public sealed record VpsProvisionDnsItem(string Name, string RecordType, string ExpectedAddress, ProbeStatus Status);

// Transient credentials: never serialize these objects or include them in logs.
public sealed class VpsSshAccess
{
    public string UserName { get; init; } = "root";
    public VpsAuthMethod Method { get; init; } = VpsAuthMethod.Password;
    public string Password { get; init; } = "";
    public string IdentityFile { get; init; } = "";
    public string KeyPassphrase { get; init; } = "";
    public override string ToString() => "VpsSshAccess (credentials hidden)";
}
public sealed class VpsProvisionRequest
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public required ServerProfile Profile { get; init; }
    public required VpsSshAccess InitialAccess { get; init; }
    public required HostKeyCandidate HostKey { get; init; }
    public string AdminIdentityFile { get; init; } = "";
    public string AdminKeyPassphrase { get; init; } = "";
    public string AcmeEmail { get; init; } = "";
    public VpsPanelAccess PanelAccess { get; init; } = VpsPanelAccess.Tunnel;
    public string PanelCidr { get; init; } = "any";
    public bool FreshServerConfirmed { get; init; }
    public bool LetsEncryptTermsAccepted { get; init; }
    public bool ProviderFirewallConfirmed { get; init; }
    public override string ToString() => $"VpsProvisionRequest {RunId} (credentials hidden)";
}
public sealed record VpsProvisionRun(Guid RunId, Guid ServerId, VpsProvisionStage Stage, string Message, DateTimeOffset UpdatedAt)
{
    // Optional detail is deliberately separate from Message: these are typed, non-secret observations,
    // safe to persist and project without ever rendering arbitrary SSH/provider output.
    public string StepId { get; init; } = "";
    public VpsProvisionStage? ActiveStage { get; init; }
    public DateTimeOffset? StepStartedAt { get; init; }
    public DateTimeOffset? LastSuccessfulVpsContactAt { get; init; }
    public DateTimeOffset? NextPollAt { get; init; }
    public VpsProvisionRunState State { get; init; } = VpsProvisionRunState.Running;
    public IReadOnlyList<VpsProvisionDnsItem> DnsItems { get; init; } = [];
}
public sealed record VpsInspection(string Os, string Version, string Architecture, bool Supported, bool Fresh, string Phase, bool RollbackActive, string Message);
public sealed class VpsPanelCredentials
{
    public Guid ServerId { get; init; }
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public string PanelPath { get; init; } = "/";
    public string PanelDomain { get; init; } = "";
    public int PanelPort { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
    public override string ToString() => "VpsPanelCredentials (credentials hidden)";
}
public sealed record VpsProvisionResult(VpsProvisionRun Run, ServerProfile Profile, bool HasCredentials)
{
    public override string ToString() => $"VpsProvisionResult {Run.RunId} {Run.Stage} (credentials hidden)";
}
public interface IVpsProvisionStore
{
    Task SaveRunAsync(VpsProvisionRun run, CancellationToken cancellationToken = default);
    Task<VpsProvisionRun?> LoadLatestRunAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VpsProvisionRun>> LoadRunHistoryAsync(Guid runId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VpsProvisionRun>>([]);
    Task SaveCredentialsAsync(VpsPanelCredentials credentials, CancellationToken cancellationToken = default);
    Task<VpsPanelCredentials?> LoadCredentialsAsync(Guid serverId, CancellationToken cancellationToken = default);
}
public interface IVpsProvisioner
{
    Task<VpsProvisionResult> RunAsync(VpsProvisionRequest request, IProgress<VpsProvisionRun>? progress = null, CancellationToken cancellationToken = default);
}
public sealed record VpsRemoteResult(int ExitCode, string Output)
{
    public override string ToString() => $"VpsRemoteResult exit={ExitCode} (output hidden)";
}
public interface IVpsRemoteSession : IAsyncDisposable
{
    Task<VpsRemoteResult> ExecuteAsync(string command, CancellationToken cancellationToken = default);
    Task UploadAsync(string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}
public interface IVpsRemoteSessionFactory
{
    Task<IVpsRemoteSession> ConnectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default);
}
public sealed record VpsAdminKey(string IdentityFile, string PublicKey);
public interface IVpsKeyService
{
    Task<VpsAdminKey> PrepareAsync(Guid serverId, string identityFile, string passphrase, CancellationToken cancellationToken = default);
}
