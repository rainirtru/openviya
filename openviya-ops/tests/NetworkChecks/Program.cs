using OpenViya.Ops.Core;
using OpenViya.Ops.HostsHelper;
using OpenViya.Ops.Network;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

const string existing = "# local comment\r\n127.0.0.1 localhost\r\n10.2.3.4 internal.example\r\n";
var added = HostsFileEditor.Apply(existing, "panel-usa-1.openviya.online");
Require(added.Outcome == HostsEditOutcome.Changed, "Should add a new owned panel entry.");
Require(added.Content.StartsWith(existing, StringComparison.Ordinal), "Existing hosts content and line endings should be preserved.");
Require(added.Content.Contains("127.0.0.1\tpanel-usa-1.openviya.online\t# OpenViya Ops", StringComparison.Ordinal), "New entry should be loopback and marked.");
Require(HostsFileEditor.Apply(added.Content, "PANEL-USA-1.OPENVIYA.ONLINE").Outcome == HostsEditOutcome.AlreadyOwned, "Apply should be idempotent, case-insensitively.");
Require(HostsFileEditor.Remove(added.Content, "panel-usa-1.openviya.online").Content == existing, "Remove should restore the untouched fixture.");
Require(HostsFileEditor.Remove("127.0.0.1 panel-usa-1.openviya.online # manual", "panel-usa-1.openviya.online").Outcome == HostsEditOutcome.NotOwned, "Manual rows must not be deleted.");
Require(HostsFileEditor.Remove("192.0.2.10 panel-usa-1.openviya.online # OpenViya Ops", "panel-usa-1.openviya.online").Outcome == HostsEditOutcome.NotOwned, "Manually changed owned rows must not be deleted.");
Require(HostsFileEditor.Apply("192.0.2.10 panel-usa-1.openviya.online\n", "panel-usa-1.openviya.online").Outcome == HostsEditOutcome.Conflict, "Conflicting mapping must not be overwritten.");
Require(!HostsFileEditor.IsAllowedPanelDomain("usa-1.openviya.online"), "VPN domain must not be accepted by hosts helper.");
Require(!HostsFileEditor.IsAllowedPanelDomain("panel-evil.example"), "Only the fixed OpenViya panel root is allowed.");
Require(!HostsFileEditor.IsAllowedPanelDomain("panel-a.openviya.online extra"), "Argument injection must be rejected.");
Require(ProfileValidator.IsValidHost("203.0.113.9") && ProfileValidator.IsValidHost("2001:db8::2"), "IPv4 and IPv6 literals should validate.");
Require(!ProfileValidator.IsValidHost("https://example.com"), "Host input must reject URI schemes.");
Require(ProfileValidator.IsValidDomain("panel-usa-1.openviya.online"), "Panel hostname should validate.");
Require(ProfileValidator.IsValidBasePath("/secret/entry") && !ProfileValidator.IsValidBasePath("/../etc"), "Base path must be absolute and reject traversal.");
var diagnostics = new DiagnosticsService();
var invalidUri = await diagnostics.CheckPanelThroughTunnelAsync(new ServerProfile { PanelDomain = "panel-usa-1.openviya.online", PanelPort = 0, PanelBasePath = "/safe" }, 18080);
Require(invalidUri.Status == ProbeStatus.Failure, "Invalid URI port must be rejected before network access.");
var traversal = await diagnostics.CheckPanelThroughTunnelAsync(new ServerProfile { PanelDomain = "panel-usa-1.openviya.online", PanelPort = 443, PanelBasePath = "/../secret" }, 18080);
Require(traversal.Status == ProbeStatus.Failure, "Traversal path must be rejected before network access.");
Console.WriteLine("Network and hosts checks passed.");
