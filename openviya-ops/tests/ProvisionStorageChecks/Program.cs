using System.Text;
using Microsoft.Data.Sqlite;
using OpenViya.Ops.Core;
using OpenViya.Ops.Storage;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static VpsPanelCredentials Credentials(Guid serverId, string password, string user = "panel-user") => new()
{
    ServerId = serverId,
    UserName = user,
    Password = password,
    PanelPath = "/hidden-panel-path/",
    PanelDomain = "panel.example.org",
    PanelPort = 38471,
    RetrievedAt = DateTimeOffset.UtcNow
};

var root = Path.Combine(Path.GetTempPath(), "OpenViyaOpsProvisionChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var legacyRoot = Path.Combine(root, "legacy-schema");
    Directory.CreateDirectory(legacyRoot);
    var legacyPaths = new AppPaths(legacyRoot);
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPaths.DatabasePath }.ToString()))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE vps_provision_runs (run_id TEXT PRIMARY KEY NOT NULL, server_id TEXT NOT NULL, stage INTEGER NOT NULL, message TEXT NOT NULL, updated_at TEXT NOT NULL); CREATE TABLE vps_provision_events (event_id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, server_id TEXT NOT NULL, stage INTEGER NOT NULL, message TEXT NOT NULL, updated_at TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();
    }
    var migratedStore = new DpapiVpsProvisionStore(legacyPaths);
    var migratedRun = new VpsProvisionRun(Guid.NewGuid(), Guid.NewGuid(), VpsProvisionStage.Preflight, "Миграция", DateTimeOffset.UtcNow)
    {
        ActiveStage = VpsProvisionStage.Preflight, StepId = "preflight", State = VpsProvisionRunState.StatusUnknown,
        StepStartedAt = DateTimeOffset.UtcNow.AddSeconds(-20), LastSuccessfulVpsContactAt = DateTimeOffset.UtcNow.AddSeconds(-3)
    };
    await migratedStore.SaveRunAsync(migratedRun);
    Check((await migratedStore.LoadLatestRunAsync(migratedRun.ServerId))?.LastSuccessfulVpsContactAt == migratedRun.LastSuccessfulVpsContactAt,
        "Existing run tables without detail columns must migrate additively and retain new progress fields.");

    var paths = new AppPaths(root);
    var store = new DpapiVpsProvisionStore(paths);
    var serverId = Guid.NewGuid();
    var otherServerId = Guid.NewGuid();
    var runId = Guid.NewGuid();
    var older = new VpsProvisionRun(runId, serverId, VpsProvisionStage.Preflight, "Проверка сервера", DateTimeOffset.UtcNow.AddMinutes(-2));
    var now = DateTimeOffset.UtcNow;
    var newer = older with
    {
        Stage = VpsProvisionStage.InstallingPanel, ActiveStage = VpsProvisionStage.InstallingPanel,
        Message = "Установка панели", UpdatedAt = now, StepId = "tls-issue",
        StepStartedAt = now.AddMinutes(-3), LastSuccessfulVpsContactAt = now.AddSeconds(-8),
        NextPollAt = now.AddSeconds(2), State = VpsProvisionRunState.StatusUnknown,
        DnsItems = [new VpsProvisionDnsItem("panel.example.org", "A", "203.0.113.9", ProbeStatus.Warning)]
    };
    await store.SaveRunAsync(older);
    await store.SaveRunAsync(newer);
    await store.SaveRunAsync(new VpsProvisionRun(Guid.NewGuid(), otherServerId, VpsProvisionStage.Created, "Другой сервер", DateTimeOffset.UtcNow));
    Check((await store.LoadLatestRunAsync(serverId))?.Stage == VpsProvisionStage.InstallingPanel, "Latest run should be selected by server and update time.");
    var restoredLatest = await new DpapiVpsProvisionStore(paths).LoadLatestRunAsync(serverId);
    Check(restoredLatest is { StepId: "tls-issue", State: VpsProvisionRunState.StatusUnknown, ActiveStage: VpsProvisionStage.InstallingPanel } &&
        restoredLatest.StepStartedAt == newer.StepStartedAt && restoredLatest.LastSuccessfulVpsContactAt == newer.LastSuccessfulVpsContactAt &&
        restoredLatest.NextPollAt == newer.NextPollAt && restoredLatest.DnsItems.Count == 1 && restoredLatest.DnsItems[0] == newer.DnsItems[0],
        "Detailed progress must survive database reopen, including DNS results and timing observations.");
    Check(await store.LoadLatestRunAsync(Guid.NewGuid()) is null, "Unknown server should have no run.");
    await store.SaveRunAsync(newer with { UpdatedAt = newer.UpdatedAt.AddSeconds(3) });
    var history = await new DpapiVpsProvisionStore(paths).LoadRunHistoryAsync(runId);
    Check(history.Count == 2 && history[0].Message == older.Message && history[1].Message == newer.Message, "History must survive reopening, preserve order, and deduplicate unchanged polls.");
    Check(history[1].StepId == "tls-issue" && history[1].State == VpsProvisionRunState.StatusUnknown && history[1].DnsItems.Single().Name == "panel.example.org",
        "Event history must retain detailed progress fields.");
    Check((await store.LoadRunHistoryAsync(Guid.NewGuid())).Count == 0, "An unrelated run must not inherit another run's events.");
    var boundedRun = Guid.NewGuid();
    for (var index = 0; index < 205; index++)
        await store.SaveRunAsync(new VpsProvisionRun(boundedRun, otherServerId, VpsProvisionStage.PreparingAccess, "Event " + index, DateTimeOffset.UtcNow));
    var bounded = await store.LoadRunHistoryAsync(boundedRun);
    Check(bounded.Count == 200 && bounded[0].Message == "Event 5" && bounded[^1].Message == "Event 204", "A long run must retain only its latest 200 events in order.");

    var serverConflict = false;
    try { await store.SaveRunAsync(new VpsProvisionRun(runId, otherServerId, VpsProvisionStage.Complete, "Переиспользование ID", DateTimeOffset.UtcNow)); }
    catch (InvalidOperationException) { serverConflict = true; }
    Check(serverConflict, "A run ID cannot be rebound to a different server.");
    var invalidStage = false;
    try { await store.SaveRunAsync(new VpsProvisionRun(Guid.NewGuid(), serverId, (VpsProvisionStage)999, "invalid", DateTimeOffset.UtcNow)); }
    catch (ArgumentException) { invalidStage = true; }
    Check(invalidStage, "Unknown run stages must be rejected.");
    var invalidMessage = false;
    try { await store.SaveRunAsync(new VpsProvisionRun(Guid.NewGuid(), serverId, VpsProvisionStage.Failed, "bad\nmessage", DateTimeOffset.UtcNow)); }
    catch (ArgumentException) { invalidMessage = true; }
    Check(invalidMessage, "Run messages with controls must be rejected.");
    var longMessage = false;
    try { await store.SaveRunAsync(new VpsProvisionRun(Guid.NewGuid(), serverId, VpsProvisionStage.Failed, new string('x', 1001), DateTimeOffset.UtcNow)); }
    catch (ArgumentException) { longMessage = true; }
    Check(longMessage, "Run messages must be bounded.");

    const string firstPassword = "panel-password-very-secret-A";
    const string secondPassword = "panel-password-very-secret-B";
    var credentials = Credentials(serverId, firstPassword);
    await store.SaveCredentialsAsync(credentials);
    var restored = await store.LoadCredentialsAsync(serverId);
    Check(restored is not null && restored.Password == firstPassword && restored.PanelPath == credentials.PanelPath && restored.PanelDomain == credentials.PanelDomain && restored.PanelPort == credentials.PanelPort, "Panel credentials should round-trip.");
    Check(credentials.ToString() == "VpsPanelCredentials (credentials hidden)", "Credentials ToString must redact values.");
    var access = new VpsSshAccess { UserName = "root", Password = firstPassword, KeyPassphrase = secondPassword };
    Check(!access.ToString().Contains(firstPassword, StringComparison.Ordinal) && !access.ToString().Contains(secondPassword, StringComparison.Ordinal), "SSH access ToString must redact values.");
    var request = new VpsProvisionRequest
    {
        Profile = new ServerProfile { Name = "test", Host = "203.0.113.9" },
        InitialAccess = access,
        HostKey = new HostKeyCandidate("ssh-ed25519", "ssh-ed25519 public-material", "SHA256:fingerprint"),
        AdminKeyPassphrase = firstPassword
    };
    Check(!request.ToString().Contains(firstPassword, StringComparison.Ordinal), "Provision request ToString must redact values.");

    var credentialFile = Path.Combine(root, "vps-provision-credentials", $"{serverId:N}.dpapi");
    var encryptedBytes = await File.ReadAllBytesAsync(credentialFile);
    Check(!Encoding.UTF8.GetString(encryptedBytes).Contains(firstPassword, StringComparison.Ordinal), "Credential file must not contain plaintext password.");
    Check(!Encoding.UTF8.GetString(encryptedBytes).Contains(credentials.PanelPath, StringComparison.Ordinal), "Credential file must not contain plaintext panel path.");
    Check(!Directory.GetFiles(Path.GetDirectoryName(credentialFile)!).Any(p => p.EndsWith(".tmp", StringComparison.Ordinal)), "Atomic credential writes should not leave temp files.");

    await store.SaveCredentialsAsync(Credentials(serverId, secondPassword, "updated-user"));
    var overwritten = await store.LoadCredentialsAsync(serverId);
    Check(overwritten?.Password == secondPassword && overwritten.UserName == "updated-user", "Credential overwrite should replace the previous payload atomically.");
    Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(credentialFile)).Contains(secondPassword, StringComparison.Ordinal), "Overwritten credentials must remain encrypted.");

    var otherCredentials = Credentials(otherServerId, "other-server-secret");
    await store.SaveCredentialsAsync(otherCredentials);
    var otherFile = Path.Combine(root, "vps-provision-credentials", $"{otherServerId:N}.dpapi");
    await File.WriteAllBytesAsync(credentialFile, await File.ReadAllBytesAsync(otherFile));
    var crossedServerPayload = false;
    try { await store.LoadCredentialsAsync(serverId); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("другому серверу", StringComparison.OrdinalIgnoreCase)) { crossedServerPayload = true; }
    Check(crossedServerPayload, "A valid encrypted payload for another server must be rejected.");

    await File.WriteAllBytesAsync(credentialFile, [1, 2, 3, 4]);
    var truncatedRejected = false;
    try { await store.LoadCredentialsAsync(serverId); }
    catch (InvalidOperationException) { truncatedRejected = true; }
    Check(truncatedRejected, "Truncated encrypted credential payload must be rejected.");
    await store.SaveCredentialsAsync(Credentials(serverId, secondPassword));

    var invalidCredentials = false;
    try { await store.SaveCredentialsAsync(new VpsPanelCredentials { ServerId = serverId, UserName = "panel-user", Password = "secret", PanelPath = "/a/../b", PanelDomain = "panel.example.org", PanelPort = 38471, RetrievedAt = DateTimeOffset.UtcNow }); }
    catch (ArgumentException) { invalidCredentials = true; }
    Check(invalidCredentials, "Invalid panel path must be rejected.");

    var profileStore = new SqliteProfileStore(paths);
    var profile = new ServerProfile { Id = serverId, Name = "Credential export test", Host = "203.0.113.9", SubdomainMode = SubdomainMode.Manual };
    await profileStore.SaveAsync(profile);
    var exportPath = Path.Combine(root, "profiles.json");
    await profileStore.ExportAsync(exportPath);
    var exported = await File.ReadAllTextAsync(exportPath);
    Check(!exported.Contains(secondPassword, StringComparison.Ordinal) && !exported.Contains("hidden-panel-path", StringComparison.Ordinal), "Profile export must omit panel credentials and panel base path.");
    Check(!exported.Contains("identity_file", StringComparison.OrdinalIgnoreCase), "Profile export must omit identity file paths.");

    Console.WriteLine("Provision storage checks passed. DPAPI checks require Windows.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}
