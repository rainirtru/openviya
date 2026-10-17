using OpenViya.Ops.Core;
using OpenViya.Ops.Ssh;
using System.Diagnostics;
using System.Text;

if (args.Contains("--sleep-child", StringComparer.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var profile = new ServerProfile { Host = "2001:db8::10", UserName = "ops-user", SshPort = 2222 };
Require(SshService.NeedsManagedHostKeyScan("choose_kex: unsupported KEX method sntrup761x25519-sha512@openssh.com"), "The Windows keyscan KEX bug must activate the observation-only fallback.");
Require(!SshService.NeedsManagedHostKeyScan("Connection timed out") && !SshService.NeedsManagedHostKeyScan("Host key verification failed"), "Unrelated failures must not trigger a negotiation fallback.");
SshService.ValidateEndpoint(profile);
Require(SshService.Alias(profile.Id) == "openviya-" + profile.Id.ToString("N"), "Alias must remain profile-bound and stable.");
var keyBlob = new byte[4 + Encoding.ASCII.GetByteCount("ssh-ed25519") + 8];
System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(keyBlob, (uint)Encoding.ASCII.GetByteCount("ssh-ed25519"));
Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(keyBlob, 4);
Require(SshService.Fingerprint(Convert.ToBase64String(keyBlob)).StartsWith("SHA256:", StringComparison.Ordinal), "Fingerprint format must be OpenSSH SHA256.");
Require(SshService.KeyBlobMatchesType("ssh-ed25519", Convert.ToBase64String(keyBlob)), "SSH key type must match the public key blob.");
Require(!SshService.KeyBlobMatchesType("ssh-rsa", Convert.ToBase64String(keyBlob)), "Mismatched key type must be rejected.");
var candidate = new HostKeyCandidate("ssh-ed25519", Convert.ToBase64String(keyBlob), SshService.Fingerprint(Convert.ToBase64String(keyBlob)));
var alias = SshService.Alias(profile.Id);
Require(SshService.ValidateTrustPin($"{alias} ssh-ed25519 {candidate.PublicKey}\n", alias, candidate), "Same pinned key must be idempotent.");
try
{
    SshService.ValidateTrustPin($"{alias} ssh-rsa {Convert.ToBase64String(keyBlob)}\n", alias, candidate);
    throw new InvalidOperationException("Changed pinned key was accepted.");
}
catch (InvalidOperationException ex) when (ex.Message.StartsWith("Host key changed", StringComparison.Ordinal)) { }

Require(TunnelManager.CandidatePorts(null).SequenceEqual(Enumerable.Range(20530, 100)), "Automatic ports must use only the documented bounded range.");
Require(TunnelManager.CandidatePorts(18080).First() == 18080 && TunnelManager.CandidatePorts(18080).Distinct().Count() == 101, "Preferred port must precede the bounded auto range without duplicates.");

foreach (var invalid in new[] { "host\nHost *", "bad name", "-oProxyCommand=cmd", "host#comment" })
{
    try { SshService.ValidateEndpoint(profile with { Host = invalid }); throw new InvalidOperationException($"Rejected host input was accepted: {invalid}"); }
    catch (ArgumentException) { }
}

foreach (var invalidPort in new[] { 0, -1, 65536 })
{
    try { SshService.ValidatePort(invalidPort, "test"); throw new InvalidOperationException($"Rejected port was accepted: {invalidPort}"); }
    catch (ArgumentOutOfRangeException) { }
}

SshService.ValidatePort(20530, "test");
SshService.ValidatePort(65535, "test");

if (OperatingSystem.IsWindows())
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "--sleep-child" } })!;
    await Task.Delay(250);
    WindowsJob.AttachAndOwn(child);
    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync();
    Require(child.HasExited, "Owned local child process must terminate cleanly.");
}
Console.WriteLine("SSH contract checks passed; process lifecycle used only a local sleep child. No SSH/network/VPS, user key, or known_hosts file was accessed.");
