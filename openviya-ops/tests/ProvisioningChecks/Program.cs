using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenViya.Ops.Core;
using OpenViya.Ops.Provisioning;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

var tempRoot = Path.Combine(Path.GetTempPath(), "OpenViyaOpsProvisioningChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    var success = await RunCase("success", new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)] });
    Check(success.Result.Run.Stage == VpsProvisionStage.Complete && success.Result.HasCredentials, "Fresh server setup should complete and return panel credentials.");
    Check(success.Result.Run.State == VpsProvisionRunState.Complete && success.Result.Run.LastSuccessfulVpsContactAt is not null && success.Result.Run.StepStartedAt is not null,
        "A completed run should retain its observed state, current-step start, and last validated VPS response time.");
    Check(success.Progress.Any(x => x.DnsItems.Count == 2 && x.DnsItems.All(d => d.Status == ProbeStatus.Success)),
        "DNS progress should expose each validated planned name with its concrete resolver result.");
    Check(success.Result.Profile.PanelBasePath == "/fake-secret-panel-path/", "Verified panel base path should be saved to the profile.");
    Check(success.Factory.Users.SequenceEqual(["root", "vpsadmin"]), "Fresh setup must keep the root SSH session and make a new admin SSH connection before finalize.");
    Check(success.Scenario.RootPasswordWasUsed, "Fake root password should be passed only to the initial SSH connector.");
    var prepare = success.Scenario.Commands.Single(x => x.Command.Contains("desktop-prepare --", StringComparison.Ordinal));
    var finalize = success.Scenario.Commands.Single(x => x.Command.Contains("desktop-finalize --", StringComparison.Ordinal));
    Check(prepare.User == "root" && finalize.User == "vpsadmin", "Prepare should run via initial root, then finalize via the new admin connection.");
    Check(success.Scenario.Events.IndexOf(prepare.Command) < success.Scenario.Events.IndexOf("connect:vpsadmin") && success.Scenario.Events.IndexOf("connect:vpsadmin") < success.Scenario.Events.IndexOf(finalize.Command), "Prepare, new admin authentication, and finalize must occur in order.");
    Check(success.Dns.BuildCalls == 1 && success.Dns.ApplyCalls == 1 && success.PublicDns.Calls > 0, "Automatic mode should apply DNS and verify public resolution before prepare.");
    Check(success.Scenario.Events.IndexOf("dns-apply") < success.Scenario.Events.IndexOf(prepare.Command), "DNS application must precede remote prepare.");
    Check(success.Scenario.Uploads.ContainsKey("/tmp/openviya-" + success.Request.RunId.ToString("N") + "/bootstrap-vps.sh"), "The pinned bootstrap resource should be staged under the run-specific path.");
    using (var resource = typeof(VpsProvisioner).Assembly.GetManifestResourceStream("OpenViya.Ops.Provisioning.bootstrap-vps.sh")!)
    using (var memory = new MemoryStream())
    {
        await resource.CopyToAsync(memory);
        var bundledHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(memory.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal))));
        var stagedHash = Convert.ToHexStringLower(SHA256.HashData(success.Scenario.Uploads["/tmp/openviya-" + success.Request.RunId.ToString("N") + "/bootstrap-vps.sh"]));
        Check(stagedHash == bundledHash, "Uploaded helper bytes should match the embedded bootstrap resource hash.");
    }
    Check(success.Scenario.Commands.Any(x => x.Command.Contains("sha256sum --check --status", StringComparison.Ordinal)), "Staged helper should be verified by SHA-256 on the remote side.");
    AssertNoSecrets(success);

    var sourcesRequest = Request(panelSources: "192.0.2.1/32\n2001:db8::1/128");
    var sources = await RunCase("panel-sources", new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)] }, seedRequest: sourcesRequest);
    Check(sources.Result.Run.Stage == VpsProvisionStage.Complete && sources.Scenario.Commands.Any(x => x.Command.Contains("--panel-cidr '192.0.2.1/32\n2001:db8::1/128'", StringComparison.Ordinal)), "Panel source list must reach prepare as one quoted argument.");
    Check(sources.Result.Profile.PanelAccess == VpsPanelAccess.Both && sources.Result.Profile.PanelAllowedIpsText == "192.0.2.1\n2001:db8::1",
        "The saved profile must reflect the actual panel exposure and its plain-IP source list.");
    foreach (var invalidSources in new[] { "", "any\n192.0.2.1/32", "192.0.2.1,192.0.2.2", "192.0.2.1/32\n", "192.0.2.1/32\n\n192.0.2.2/32", string.Join('\n', Enumerable.Repeat("192.0.2.1/32", 101)) })
    {
        var invalid = await CreateCase("invalid-panel-sources-" + Guid.NewGuid().ToString("N"));
        await Throws<ArgumentException>(() => invalid.Provisioner.RunAsync(Request(panelSources: invalidSources)), "Invalid panel source list must fail before remote work.");
        Check(invalid.Factory.Users.Count == 0 && invalid.Dns.BuildCalls == 0, "Invalid source lists must have no SSH/DNS effects.");
    }

    var sudoInitial = await RunCase("sudo-initial", new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)] }, seedRequest: Request(initialUser: "bootstrap"));
    Check(sudoInitial.Result.Run.Stage == VpsProvisionStage.Complete && sudoInitial.Factory.Users.SequenceEqual(["bootstrap", "vpsadmin"]), "A provider account with passwordless sudo should prepare before the new admin connects.");
    Check(sudoInitial.Scenario.Commands.Single(x => x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)).Command.StartsWith("sudo -n --preserve-env=SSH_CONNECTION ", StringComparison.Ordinal), "Initial sudo must preserve SSH_CONNECTION for the detached prepare access check.");

    var manualScenario = new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)] };
    var manual = await RunCase("manual", manualScenario, manual: true);
    Check(manual.Result.Run.Stage == VpsProvisionStage.Complete, "Manual DNS setup should complete when public DNS is already correct.");
    Check(manual.Dns.BuildCalls == 0 && manual.Dns.ApplyCalls == 0, "Manual DNS mode must not call DNS provider planning or writes.");
    Check(manual.PublicDns.Calls > 0 && manualScenario.Commands.Any(x => x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)), "Manual mode should verify public DNS and then prepare.");

    var unsafeCase = await CreateCase("unsafe");
    var unsafeRequest = Request(freshConfirmed: false);
    await Throws<ArgumentException>(() => unsafeCase.Provisioner.RunAsync(unsafeRequest), "Unsafe/unconfirmed root request must be rejected before remote work.");
    Check(unsafeCase.Scenario.Commands.Count == 0 && unsafeCase.Factory.Users.Count == 0 && unsafeCase.Keys.Calls == 0 && unsafeCase.Dns.BuildCalls == 0, "Request validation should happen before SSH, key, or DNS work.");

    var conflict = await RunCase("dns-conflict", new Scenario { Inspections = [Inspection("fresh", true)] }, dnsConflict: true);
    var transientStatus = await RunCase("systemd-status-gap", new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)], PrepareStates = new Queue<string>(["missing", "running", "missing", "missing", "succeeded"]) });
    Check(transientStatus.Result.Run.Stage == VpsProvisionStage.Complete && transientStatus.Scenario.PrepareStarts == 1, "Temporary missing systemd unit must be observed again without relaunching prepare.");
    Check(transientStatus.Progress.Any(x => x.State == VpsProvisionRunState.StatusUnknown && x.StepId == "prepare-status"),
        "A transiently missing status response must be labeled unknown while the same remote run is observed again.");
    var uncertainRequest = Request();
    var uncertainStore = new FakeProvisionStore();
    await uncertainStore.SaveRunAsync(new VpsProvisionRun(uncertainRequest.RunId, uncertainRequest.Profile.Id,
        VpsProvisionStage.NeedsAttention, "Состояние задания неизвестно", DateTimeOffset.UtcNow)
    {
        ActiveStage = VpsProvisionStage.PreparingAccess, StepId = "prepare-status", State = VpsProvisionRunState.NeedsOperator
    });
    var uncertainResume = await RunCase("unknown-resume-no-relaunch",
        new Scenario { HelperExists = true, Inspections = [Inspection("fresh", true)], PrepareStates = new Queue<string>(["missing"]) },
        seedRequest: uncertainRequest, seedStore: uncertainStore);
    Check(uncertainResume.Result.Run.Stage == VpsProvisionStage.NeedsAttention && uncertainResume.Scenario.PrepareStarts == 0 &&
        uncertainResume.Result.Run.Message.Contains("Автоматический повтор запрещён", StringComparison.Ordinal),
        "A missing status on resume of an already observed prepare stage must require operator review instead of relaunching it.");
    Check(conflict.Result.Run.Stage == VpsProvisionStage.NeedsAttention && conflict.Result.Run.RunId == conflict.Request.RunId, "DNS conflict should preserve a recoverable run.");
    Check(conflict.Result.Run.Message.Contains(conflict.Request.Profile.PanelDomain, StringComparison.Ordinal) && conflict.Result.Run.Message.Contains("уже занято", StringComparison.Ordinal), "Record conflict must identify the blocked domain.");
    Check(conflict.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)) && conflict.Dns.ApplyCalls == 0, "DNS conflicts must stop before prepare and provider writes.");

    var dnsUnavailable = await RunCase("dns-unavailable", new Scenario { Inspections = [Inspection("fresh", true)], DnsBuildFails = true });
    Check(dnsUnavailable.Result.Run.Stage == VpsProvisionStage.NeedsAttention && dnsUnavailable.Dns.ApplyCalls == 0 && dnsUnavailable.Scenario.PrepareStarts == 0, "A missing DNS token/provider failure must stop before applying records or changing access.");
    Check(dnsUnavailable.Result.Run.Message.Contains("Timeweb", StringComparison.Ordinal), "DNS failure should identify the configuration the operator can fix.");
    AssertNoSecrets(dnsUnavailable);

    var unsupported = await RunCase("unsupported", new Scenario { Inspections = [Inspection("fresh", true, supported: false)] });
    Check(unsupported.Result.Run.Stage == VpsProvisionStage.NeedsAttention && unsupported.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)), "Unsupported OS/architecture must not prepare.");
    var nonfresh = await RunCase("nonfresh", new Scenario { Inspections = [Inspection("fresh", false)] });
    Check(nonfresh.Result.Run.Stage == VpsProvisionStage.NeedsAttention && nonfresh.Dns.BuildCalls == 0 && nonfresh.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)), "An existing/nonfresh host must not prepare or write DNS.");

    var rolledBack = await RunCase("rolled-back", new Scenario { Inspections = [Inspection("rolled-back", false)] });
    Check(rolledBack.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal) && !x.Command.Contains("desktop-finalize --", StringComparison.Ordinal)), "Rolled-back servers must not repeat prepare or finalize.");
    var expired = await RunCase("expired", new Scenario { Inspections = [Inspection("pending", false, rollbackActive: false)] });
    Check(expired.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal) && !x.Command.Contains("desktop-finalize --", StringComparison.Ordinal)), "An expired rollback window must block finalize and never re-run prepare.");

    var authFailure = await RunCase("admin-auth-failure", new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true)], FailAdminConnect = true });
    Check(authFailure.Result.Run.Stage == VpsProvisionStage.NeedsAttention && authFailure.Scenario.Commands.All(x => !x.Command.Contains("desktop-finalize --", StringComparison.Ordinal)), "Admin authentication failure must block finalize and remain recoverable.");

    var partialRequest = Request();
    var partialStore = new FakeProvisionStore();
    await partialStore.SaveRunAsync(new VpsProvisionRun(partialRequest.RunId, partialRequest.Profile.Id, VpsProvisionStage.NeedsAttention, "Previously prepared", DateTimeOffset.UtcNow));
    var partial = await RunCase("partial-resume", new Scenario { HelperExists = true, PrepareStates = new Queue<string>(["succeeded"]), Inspections = [Inspection("pending", false, true), Inspection("complete", false)] }, seedRequest: partialRequest, seedStore: partialStore);
    Check(partial.Result.Run.Stage == VpsProvisionStage.Complete && partial.Factory.Users.SequenceEqual(["vpsadmin"]), "Partial resume should attempt the known admin key first, without opening root.");
    Check(partial.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)) && partial.Scenario.Commands.Any(x => x.Command.Contains("desktop-finalize --", StringComparison.Ordinal)), "Pending partial resume should finalize via admin without repeating prepare.");

    var remoteFailure = await RunCase("remote-step-failed", new Scenario { Inspections = [Inspection("fresh", true)], PrepareStates = new Queue<string>(["missing", "running", "failed"]), RemoteStep = "apt-install" });
    Check(remoteFailure.Progress.Any(x => x.Message.Contains("Установка системных пакетов", StringComparison.Ordinal)), "Live remote substeps must be reported to the wizard.");
    Check(remoteFailure.Result.Run.Message.Contains("Установка системных пакетов", StringComparison.Ordinal) && remoteFailure.Result.Run.Message.Contains("17", StringComparison.Ordinal) && remoteFailure.Scenario.FinalizeStarts == 0, "A remote failure must identify its step and exit code and block finalize.");
    var untrustedStep = await RunCase("untrusted-step", new Scenario { Inspections = [Inspection("fresh", true)], PrepareStates = new Queue<string>(["missing", "failed"]), RemoteStep = TestSecrets.RootPassword });
    AssertNoSecrets(untrustedStep);
    var pendingFailure = await RunCase("pending-prepare-failed", new Scenario { Inspections = [Inspection("pending", false, true)], PrepareStates = new Queue<string>(["failed"]), RemoteStep = "firewall" });
    Check(pendingFailure.Scenario.FinalizeStarts == 0 && pendingFailure.Result.Run.Message.Contains("UFW", StringComparison.Ordinal), "Pending phase alone must not hide a failed firewall step or permit finalize.");
    var legacyHelper = await RunCase("legacy-helper", new Scenario { HelperExists = true, LegacyHelper = true, Inspections = [Inspection("complete", false), Inspection("complete", false)] });
    var legacyList = await RunCase("legacy-list", new Scenario { HelperExists = true, LegacyHelper = true, Inspections = [Inspection("fresh", true)] }, seedRequest: Request(panelSources: "192.0.2.1/32\n2001:db8::1/128"));
    Check(legacyList.Result.Run.Stage == VpsProvisionStage.NeedsAttention && legacyList.Scenario.PrepareStarts == 0 && legacyList.Dns.ApplyCalls == 0, "A legacy helper cannot start fresh with multiple panel sources; no DNS or prepare writes allowed.");
    Check(legacyHelper.Result.Run.Stage == VpsProvisionStage.Complete && legacyHelper.Scenario.Uploads.Count == 0 && legacyHelper.Progress.Any(x => x.Message.Contains("предыдущий установщик", StringComparison.Ordinal)), "The exact approved previous helper may be observed without overwriting it, with honest coarse-progress notice.");
    var unknownHelper = await RunCase("unknown-helper", new Scenario { HelperExists = true, InvalidHelperHash = true });
    Check(unknownHelper.Result.Run.Stage == VpsProvisionStage.NeedsAttention && unknownHelper.Scenario.Commands.All(x => !x.Command.Contains("desktop-inspect", StringComparison.Ordinal)), "Unknown helper contents must never be executed for progress.");

    var completeRequest = Request();
    var completeStore = new FakeProvisionStore();
    await completeStore.SaveRunAsync(new VpsProvisionRun(completeRequest.RunId, completeRequest.Profile.Id, VpsProvisionStage.Complete, "Done", DateTimeOffset.UtcNow));
    var completeResume = await RunCase("complete-resume", new Scenario { HelperExists = true, Inspections = [Inspection("complete", false), Inspection("complete", false)] }, seedRequest: completeRequest, seedStore: completeStore);
    Check(completeResume.Result.Run.Stage == VpsProvisionStage.Complete && completeResume.Store.SavedCredentials.Count == 1, "Complete resume should recover credentials only.");
    Check(completeResume.Factory.Users.SequenceEqual(["vpsadmin"]) && completeResume.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal) && !x.Command.Contains("desktop-finalize --", StringComparison.Ordinal)), "Complete resume should use admin and skip prepare/finalize.");

    foreach (var mismatch in new[] { "domain", "port" })
    {
        var scenario = new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)], CredentialMismatch = mismatch };
        var failed = await RunCase("credential-mismatch-" + mismatch, scenario);
        Check(failed.Result.Run.Stage == VpsProvisionStage.NeedsAttention && failed.Store.SavedCredentials.Count == 0, $"Mismatched credential {mismatch} must not be persisted.");
        Check(failed.Profiles.SavedProfiles.All(x => x.PanelBasePath == "/"), $"Mismatched credential {mismatch} must not update profile path.");
        AssertNoSecrets(failed);
    }

    var malformed = await RunCase("malformed-inspection", new Scenario { Inspections = [Inspection("fresh", true)], MalformedInspection = true });
    Check(malformed.Result.Run.Stage == VpsProvisionStage.NeedsAttention && malformed.Scenario.Commands.All(x => !x.Command.Contains("desktop-prepare --", StringComparison.Ordinal)), "Malformed machine JSON must fail closed before prepare.");

    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60)))
    {
        var unknown = await RunCase("unknown-job", new Scenario { Inspections = [Inspection("fresh", true)], PrepareStates = new Queue<string>(["missing", "mystery"]) });
        // An unknown job state becomes a safe NeedsAttention run with the original RunId.
        Check(unknown.Result.Run.Stage == VpsProvisionStage.NeedsAttention && unknown.Result.Run.RunId == unknown.Request.RunId && unknown.Store.Latest(unknown.Request.Profile.Id)?.RunId == unknown.Request.RunId, "Unknown remote job state must preserve a resumable local run.");
        AssertNoSecrets(unknown);

        var canceled = await RunCase("cancelled-job", new Scenario { Inspections = [Inspection("fresh", true)], PrepareAlwaysRunning = true, PrepareStates = new Queue<string>(["missing"]) }, cancellationToken: cancellation.Token);
        Check(canceled.Result.Run.Stage == VpsProvisionStage.NeedsAttention && canceled.Result.Run.RunId == canceled.Request.RunId && canceled.Store.Latest(canceled.Request.Profile.Id)?.RunId == canceled.Request.RunId, "Cancellation after job start must preserve the recoverable RunId.");
        AssertNoSecrets(canceled);
    }

    Console.WriteLine("Provisioning checks passed: fresh/root-to-admin/finalize order, automatic/manual DNS, safety gates, recovery, credential validation, helper integrity, and secret-safe run state. All SSH/DNS services were fakes; no VPS or provider API was contacted.");
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

static VpsInspection Inspection(string phase, bool fresh, bool rollbackActive = false, bool supported = true) => new("Ubuntu", "24.04", "amd64", supported, fresh, phase, rollbackActive, "test inspection");

static VpsProvisionRequest Request(bool manual = false, Guid? serverId = null, Guid? runId = null, bool freshConfirmed = true, string initialUser = "root", string? panelSources = null)
{
    var id = serverId ?? Guid.NewGuid();
    var profile = new ServerProfile
    {
        Id = id, Name = "Fake VPS", Role = ServerRole.VpnNode, Region = "usa", NodeIndex = 1,
        Host = "8.8.8.8", SshPort = 22, UserName = "vpsadmin", PanelPort = 38471,
        PanelDomain = manual ? "panel-manual.openviya.online" : "", VpnDomain = manual ? "manual.openviya.online" : "",
        SubdomainMode = manual ? SubdomainMode.Manual : SubdomainMode.Automatic, DnsZone = "openviya.online", DnsAddress = "8.8.8.8"
    };
    var key = Convert.ToBase64String(Ed25519HostKeyBlob());
    var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(key))).TrimEnd('=');
    return new VpsProvisionRequest
    {
        RunId = runId ?? Guid.NewGuid(), Profile = profile,
        InitialAccess = new VpsSshAccess { UserName = initialUser, Method = VpsAuthMethod.Password, Password = TestSecrets.RootPassword },
        HostKey = new HostKeyCandidate("ssh-ed25519", key, fingerprint),
        AdminIdentityFile = "C:\\fake\\vpsadmin_ed25519", AdminKeyPassphrase = TestSecrets.AdminPassphrase,
        AcmeEmail = "ops@example.org", PanelAccess = panelSources is null ? VpsPanelAccess.Tunnel : VpsPanelAccess.Both, PanelCidr = panelSources ?? "any",
        FreshServerConfirmed = freshConfirmed, LetsEncryptTermsAccepted = true, ProviderFirewallConfirmed = true
    };
}

static byte[] Ed25519HostKeyBlob()
{
    var algorithm = Encoding.ASCII.GetBytes("ssh-ed25519");
    var bytes = new byte[4 + algorithm.Length + 4 + 32];
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), algorithm.Length);
    algorithm.CopyTo(bytes, 4);
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4 + algorithm.Length, 4), 32);
    for (var i = 0; i < 32; i++) bytes[8 + algorithm.Length + i] = (byte)(i + 1);
    return bytes;
}

static async Task<Case> CreateCase(string name, VpsProvisionRequest? seedRequest = null, FakeProvisionStore? seedStore = null, Scenario? scenario = null, bool dnsConflict = false, bool manual = false)
{
    var request = seedRequest ?? Request(manual);
    var world = scenario ?? new Scenario { Inspections = [Inspection("fresh", true), Inspection("pending", false, true), Inspection("complete", false)] };
    world.Profile = request.Profile;
    var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "OpenViyaOpsProvisionCase", name + "-" + Guid.NewGuid().ToString("N")));
    var profiles = new FakeProfileStore();
    var store = seedStore ?? new FakeProvisionStore();
    var sessions = new FakeSessionFactory(world);
    var keys = new FakeKeyService(world);
    var dns = new FakeDns(world, dnsConflict);
    var publicDns = new FakePublicDns(world);
    var provisioner = new VpsProvisioner(paths, profiles, store, sessions, keys, dns, publicDns,
        new VpsProvisionTiming { DnsTimeout = TimeSpan.FromSeconds(5), PrepareTimeout = TimeSpan.FromSeconds(5), FinalizeTimeout = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(2), DnsPollInterval = TimeSpan.Zero });
    return new Case(request, world, profiles, store, sessions, keys, dns, publicDns, provisioner);
}

static async Task<Case> RunCase(string name, Scenario scenario, bool manual = false, VpsProvisionRequest? seedRequest = null, FakeProvisionStore? seedStore = null, CancellationToken cancellationToken = default, bool dnsConflict = false)
{
    var test = await CreateCase(name, seedRequest, seedStore, scenario, dnsConflict, manual);
    var progress = new List<VpsProvisionRun>();
    test.Progress = progress;
    test.Result = await test.Provisioner.RunAsync(test.Request, new InlineProgress<VpsProvisionRun>(progress.Add), cancellationToken);
    return test;
}

static void AssertNoSecrets(Case test)
{
    var messages = test.Store.Runs.Select(x => x.Message).Concat(test.Progress.Select(x => x.Message)).ToArray();
    Check(messages.All(x => !x.Contains(TestSecrets.RootPassword, StringComparison.Ordinal) && !x.Contains(TestSecrets.AdminPassphrase, StringComparison.Ordinal)), "Progress and persisted run messages must not contain SSH secrets.");
    Check(test.Scenario.Commands.All(x => !x.Command.Contains(TestSecrets.RootPassword, StringComparison.Ordinal) && !x.Command.Contains(TestSecrets.AdminPassphrase, StringComparison.Ordinal)), "Remote command strings must not contain SSH secrets.");
    Check(!test.Request.ToString().Contains(TestSecrets.RootPassword, StringComparison.Ordinal) && !test.Request.ToString().Contains(TestSecrets.AdminPassphrase, StringComparison.Ordinal), "Request diagnostics must redact SSH secrets.");
}

sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

sealed class Scenario
{
    public List<VpsInspection> Inspections { get; init; } = [];
    public int InspectionIndex { get; set; }
    public Queue<string> PrepareStates { get; init; } = new();
    public Queue<string> FinalizeStates { get; init; } = new();
    public bool HelperExists { get; init; }
    public bool LegacyHelper { get; init; }
    public bool InvalidHelperHash { get; init; }
    public string RemoteStep { get; init; } = "";
    public bool PrepareAlwaysRunning { get; init; }
    public bool FailAdminConnect { get; init; }
    public bool MalformedInspection { get; init; }
    public bool DnsBuildFails { get; init; }
    public string? CredentialMismatch { get; init; }
    public ServerProfile Profile { get; set; } = new();
    public List<string> Events { get; } = [];
    public List<(string User, string Command)> Commands { get; } = [];
    public Dictionary<string, byte[]> Uploads { get; } = new(StringComparer.Ordinal);
    public int PrepareStarts { get; set; }
    public int FinalizeStarts { get; set; }
    public bool RootPasswordWasUsed { get; set; }

    public VpsInspection NextInspection()
    {
        if (InspectionIndex < Inspections.Count) return Inspections[InspectionIndex++];
        if (Inspections.Count > 0) return Inspections[^1];
        return new("Ubuntu", "24.04", "amd64", true, true, "fresh", false, "fake");
    }

    public string NextJobState(string stage)
    {
        var queue = stage == "prepare" ? PrepareStates : FinalizeStates;
        if (queue.Count > 0) return queue.Dequeue();
        if (stage == "prepare" && PrepareAlwaysRunning && PrepareStarts > 0) return "running";
        return stage == "prepare" && PrepareStarts > 0 || stage == "finalize" && FinalizeStarts > 0 ? "succeeded" : "missing";
    }
}

sealed record Case(VpsProvisionRequest Request, Scenario Scenario, FakeProfileStore Profiles, FakeProvisionStore Store, FakeSessionFactory Factory, FakeKeyService Keys, FakeDns Dns, FakePublicDns PublicDns, VpsProvisioner Provisioner)
{
    public List<VpsProvisionRun> Progress { get; set; } = [];
    public VpsProvisionResult Result { get; set; } = new(new VpsProvisionRun(Guid.Empty, Guid.Empty, VpsProvisionStage.Created, "", DateTimeOffset.UtcNow), new ServerProfile(), false);
}

sealed class FakeProfileStore : IProfileStore
{
    public List<ServerProfile> SavedProfiles { get; } = [];
    public Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ServerProfile>>(SavedProfiles.ToArray());
    public Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default) { SavedProfiles.Add(profile); return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ExportAsync(string filePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> ImportAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

sealed class FakeProvisionStore : IVpsProvisionStore
{
    public List<VpsProvisionRun> Runs { get; } = [];
    public List<VpsPanelCredentials> SavedCredentials { get; } = [];
    public async Task SaveRunAsync(VpsProvisionRun run, CancellationToken cancellationToken = default) { await Task.Yield(); Runs.Add(run); }
    public Task<VpsProvisionRun?> LoadLatestRunAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(Runs.Where(x => x.ServerId == serverId).OrderByDescending(x => x.UpdatedAt).FirstOrDefault());
    public Task SaveCredentialsAsync(VpsPanelCredentials credentials, CancellationToken cancellationToken = default) { SavedCredentials.Add(credentials); return Task.CompletedTask; }
    public Task<VpsPanelCredentials?> LoadCredentialsAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(SavedCredentials.LastOrDefault(x => x.ServerId == serverId));
    public VpsProvisionRun? Latest(Guid serverId) => Runs.Where(x => x.ServerId == serverId).OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
}

sealed class FakeKeyService(Scenario scenario) : IVpsKeyService
{
    public int Calls { get; private set; }
    public Task<VpsAdminKey> PrepareAsync(Guid serverId, string identityFile, string passphrase, CancellationToken cancellationToken = default)
    {
        Calls++;
        scenario.Events.Add("prepare-key");
        return Task.FromResult(new VpsAdminKey("C:\\fake\\" + serverId.ToString("N") + "_ed25519", "ssh-ed25519 AQIDBAUGBwg="));
    }
}

sealed class FakeSessionFactory(Scenario scenario) : IVpsRemoteSessionFactory
{
    public List<string> Users { get; } = [];
    public Task<IVpsRemoteSession> ConnectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Users.Add(access.UserName);
        scenario.Events.Add("connect:" + access.UserName);
        scenario.RootPasswordWasUsed |= access.UserName == "root" && access.Password == TestSecrets.RootPassword;
        if (access.UserName == profile.UserName && scenario.FailAdminConnect) throw new InvalidOperationException("fake admin auth failure");
        scenario.Profile = profile;
        return Task.FromResult<IVpsRemoteSession>(new FakeRemoteSession(scenario, access.UserName));
    }
}

sealed class FakeRemoteSession(Scenario scenario, string user) : IVpsRemoteSession
{
    public Task<VpsRemoteResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scenario.Commands.Add((user, command));
        scenario.Events.Add(command);
        if (command.Contains("test -f", StringComparison.Ordinal)) return Result(scenario.HelperExists ? 0 : 1, "");
        if (command.Contains("sha256sum --check --status", StringComparison.Ordinal))
            return Result(scenario.InvalidHelperHash || scenario.LegacyHelper && command.Contains("/bootstrap.sh", StringComparison.Ordinal) && !command.Contains("d1c7b83a3d1c941295513e8f8da0f4eb7421575d8c35f99692f571789a475bb3", StringComparison.Ordinal) ? 1 : 0, "");
        if (command.EndsWith("id -u", StringComparison.Ordinal)) return Result(0, "0\n");
        if (command.Contains("desktop-inspect", StringComparison.Ordinal))
        {
            if (scenario.MalformedInspection) return Result(0, "{broken json");
            var inspection = scenario.NextInspection();
            return Result(0, JsonSerializer.Serialize(new { schemaVersion = 1, os = inspection.Os, version = inspection.Version, architecture = inspection.Architecture, supported = inspection.Supported, fresh = inspection.Fresh, phase = inspection.Phase, rollbackActive = inspection.RollbackActive }));
        }
        if (command.Contains("desktop-job-status", StringComparison.Ordinal))
        {
            var stage = command.Contains("--stage finalize", StringComparison.Ordinal) ? "finalize" : "prepare";
            var state = scenario.NextJobState(stage);
            return Result(0, JsonSerializer.Serialize(new { schemaVersion = 1, state, step = scenario.RemoteStep, exitCode = state == "succeeded" ? 0 : state == "failed" ? 17 : -1,
                phase = state == "succeeded" ? (stage == "prepare" ? "pending" : "complete") : "none" }));
        }
        if (command.Contains("desktop-prepare --", StringComparison.Ordinal)) { scenario.PrepareStarts++; scenario.Events.Add("prepare-start:" + user); return Result(0, ""); }
        if (command.Contains("desktop-finalize --", StringComparison.Ordinal)) { scenario.FinalizeStarts++; scenario.Events.Add("finalize-start:" + user); return Result(0, ""); }
        if (command.Contains("desktop-credentials", StringComparison.Ordinal))
        {
            var domain = scenario.Profile.PanelDomain;
            var port = scenario.Profile.PanelPort;
            if (scenario.CredentialMismatch == "domain") domain = "wrong.example.org";
            if (scenario.CredentialMismatch == "port") port++;
            return Result(0, JsonSerializer.Serialize(new { schemaVersion = 1, phase = "complete", tlsMode = "letsencrypt", domain, port, path = "/fake-secret-panel-path/", username = "panel-admin", password = "FAKE-PANEL-PASSWORD-SECRET-9876" }));
        }
        return Result(0, "");
    }

    public Task UploadAsync(string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scenario.Uploads[remotePath] = content.ToArray();
        scenario.Events.Add("upload:" + remotePath);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static Task<VpsRemoteResult> Result(int exitCode, string output) => Task.FromResult(new VpsRemoteResult(exitCode, output));
}

sealed class FakeDns(Scenario scenario, bool conflict) : IDnsProviderService
{
    public int BuildCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public Task<DnsZoneCheck> CheckZoneAsync(string zone, CancellationToken cancellationToken = default) => Task.FromResult(new DnsZoneCheck(zone, true, [], "fake"));
    public Task<DnsPlan> BuildPlanAsync(ServerProfile profile, int ttl = 600, CancellationToken cancellationToken = default)
    {
        BuildCalls++;
        scenario.Events.Add("dns-build");
        if (scenario.DnsBuildFails) throw new InvalidOperationException(TestSecrets.RootPassword);
        var names = profile.Role == ServerRole.Control ? new[] { profile.PanelDomain } : new[] { profile.PanelDomain, profile.VpnDomain };
        var items = names.Where(x => !string.IsNullOrWhiteSpace(x)).Select(name => new DnsPlanItem(name, "A", profile.DnsAddress, conflict ? DnsPlanAction.Conflict : DnsPlanAction.Create, "fake", BlockReason: conflict ? DnsBlockReason.RecordConflict : DnsBlockReason.Unknown)).ToArray();
        return Task.FromResult(new DnsPlan(profile, profile.DnsZone, profile.DnsAddress, ttl, items, ["ns1.example.org"], DateTimeOffset.UtcNow));
    }
    public Task<DnsApplyResult> ApplyAsync(DnsPlan plan, CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        scenario.Events.Add("dns-apply");
        return Task.FromResult(new DnsApplyResult(plan.Items.Select(x => new DnsApplyItem(x.Fqdn, ProbeStatus.Success, "fake", 99)).ToArray()));
    }
}

sealed class FakePublicDns(Scenario scenario) : IPublicDnsService
{
    public int Calls { get; private set; }
    public Task<IReadOnlyList<ProbeResult>> CheckAsync(DnsPlan plan, CancellationToken cancellationToken = default)
    {
        Calls++;
        scenario.Events.Add("public-dns");
        IReadOnlyList<ProbeResult> result = Enumerable.Range(0, plan.Items.Count + 1).Select(i => ProbeResult.Create("fake", ProbeStatus.Success, "fake")).ToArray();
        return Task.FromResult(result);
    }
}

static class TestSecrets
{
    public const string RootPassword = "ROOT-PASSWORD-TEST-SECRET-DO-NOT-LOG";
    public const string AdminPassphrase = "ADMIN-PASSPHRASE-TEST-SECRET-DO-NOT-LOG";
}
