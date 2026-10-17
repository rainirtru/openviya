using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenViya.Ops.Core;
using OpenViya.Ops.Provisioning;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
var temp = Path.Combine(Path.GetTempPath(), "OpenViyaMaintenanceChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var remote = new Remote();
    var service = new ServerMaintenanceService(new AppPaths(temp), remote);
    var profile = new ServerProfile { Host = "192.0.2.1", IdentityFile = "fixture-key", Name = "fixture" };
    var access = new VpsSshAccess { Method = VpsAuthMethod.PrivateKey, IdentityFile = "fixture-key", UserName = "vpsadmin" };
    var pin = new HostKeyCandidate("ssh-ed25519", "fixture-key", "SHA256:fixture");
    var plan = await service.PlanAccessChangeAsync(profile, access, pin, PanelOpenMode.PublicHttps, "192.0.2.9\n2001:db8::9", 38472);
    Check(plan.RequestedAllowedIpsText == "192.0.2.9/32\n2001:db8::9/128", "Plan must contain exact normalized host networks.");
    var refused = false;
    try { await service.ApplyAccessChangeAsync(profile, access, pin, plan, false); }
    catch (InvalidOperationException) { refused = true; }
    Check(refused && remote.AccessChanges == 0, "No mutations without explicit confirmation.");
    var applied = await service.ApplyAccessChangeAsync(profile, access, pin, plan, true);
    Check(applied.Succeeded && remote.AccessChanges == 1 && remote.Confirmed, "Canonical plan must apply and confirm after reconnect.");
    Check(remote.Connections >= 4 && remote.Uploads > 0, "Maintenance must reconnect and use approved staging.");
    var noChange = await service.PlanAccessChangeAsync(profile, access, pin, PanelOpenMode.PublicHttps, "192.0.2.9\n2001:db8::9", 38472);
    Check(!noChange.RequiresConfirmation, "Same desired settings must be idempotent.");
    var any = await service.PlanAccessChangeAsync(profile, access, pin, PanelOpenMode.PublicHttps, "", 38472);
    Check(any.RequestedAllowedIpsText == "any", "Optional empty IP list must retain established any-source semantics.");
    var stale = plan with { CurrentPanelPort = 30000 };
    var rejected = false;
    try { await service.ApplyAccessChangeAsync(profile, access, pin, stale, true); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected && remote.AccessChanges == 1, "Stale plans must not mutate remote settings.");
    var badName = false;
    try { await service.DownloadBackupAsync(profile, access, pin, "../../credentials"); }
    catch (ArgumentException) { badName = true; }
    Check(badName, "Backup path traversal must be rejected.");
    Console.WriteLine("Maintenance service checks passed: protocol decoding, user staging, exact IP plan, confirmation, reconnect, idempotence, stale plans, safe backup names. Fake SSH only.");
}
finally { Directory.Delete(temp, recursive: true); }

sealed class Remote : IVpsRemoteSessionFactory
{
    public int Connections, Uploads, AccessChanges;
    public bool Confirmed;
    private int _port = 38471;
    private string _mode = "tunnel", _ips = "any";
    public Task<IVpsRemoteSession> ConnectAsync(ServerProfile p, VpsSshAccess a, HostKeyCandidate k, CancellationToken cancellationToken = default)
    { Connections++; return Task.FromResult<IVpsRemoteSession>(new Session(this)); }
    private object Snapshot() => new { installedVersion = "2.0.0", latestVersion = "", updateAvailable = false, panelPort = _port, panelAccess = _mode,
        allowedIpsText = _ips, certificateExpiresAt = "2027-01-01T00:00:00Z", certificateManagedByLetsEncrypt = true, panelHealthy = true,
        firewallActive = true, panelFirewallRulesMatch = true, rollbackPending = false, backups = Array.Empty<object>() };
    sealed class Session(Remote owner) : IVpsRemoteSession
    {
        public Task<VpsRemoteResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            object data = new { };
            if (command.EndsWith(" run", StringComparison.Ordinal))
            {
                if (!command.Contains("| /usr/bin/base64 --decode |", StringComparison.Ordinal))
                    throw new InvalidOperationException("The helper reads JSON stdin; encoded data must be decoded before execution.");
                var match = Regex.Match(command, "printf '%s' '([A-Za-z0-9+/=]+)'");
                using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)));
                var request = json.RootElement;
                var action = request.GetProperty("action").GetString();
                if (action == "access")
                {
                    owner.AccessChanges++;
                    owner._port = request.GetProperty("panelPort").GetInt32();
                    owner._mode = request.GetProperty("panelAccess").GetString()!;
                    owner._ips = request.GetProperty("allowedIpsText").GetString()!;
                    data = new { operationId = request.GetProperty("operationId").GetString() };
                }
                else data = owner.Snapshot();
            }
            else if (command.Contains(" confirm ", StringComparison.Ordinal)) owner.Confirmed = true;
            return Task.FromResult(new VpsRemoteResult(0, JsonSerializer.Serialize(new { ok = true, result = data })));
        }
        public Task UploadAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            if (!Regex.IsMatch(path, "^/tmp/openviya-[0-9a-f]{32}/maintenance\\.py$")) throw new InvalidOperationException("User SFTP must upload into approved user-writable temporary directory.");
            owner.Uploads++; return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
