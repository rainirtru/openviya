using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using OpenViya.Ops.Core;
using Renci.SshNet;

namespace OpenViya.Ops.Provisioning;

/// <summary>Loads a selected Ed25519 admin key or creates a dedicated, protected OpenSSH key for the server.</summary>
[SupportedOSPlatform("windows")]
public sealed class VpsAdminKeyService(AppPaths paths) : IVpsKeyService
{
    private static readonly TimeSpan KeyGenerationTimeout = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);
    private readonly AppPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<VpsAdminKey> PrepareAsync(Guid serverId, string identityFile, string passphrase, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("Server id is required.", nameof(serverId));
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(identityFile))
            return await LoadSelectedKeyAsync(identityFile, passphrase, cancellationToken).ConfigureAwait(false);

        var directory = Path.GetFullPath(Path.Combine(_paths.Root, "keys", serverId.ToString("N")));
        var keyPath = Path.Combine(directory, "admin_ed25519");
        await GenerationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(keyPath))
            {
                EnsureSafeAcl(directory, isDirectory: true);
                EnsureSafeAcl(keyPath, isDirectory: false);
                return await LoadSelectedKeyAsync(keyPath, "", cancellationToken).ConfigureAwait(false);
            }

            SetOwnerOnlyAcl(directory, isDirectory: true);
            if (File.Exists(keyPath))
            {
                EnsureSafeAcl(directory, isDirectory: true);
                EnsureSafeAcl(keyPath, isDirectory: false);
                return await LoadSelectedKeyAsync(keyPath, "", cancellationToken).ConfigureAwait(false);
            }

            await GenerateKeyAsync(keyPath, serverId, cancellationToken).ConfigureAwait(false);
            SetOwnerOnlyAcl(directory, isDirectory: true);
            if (!File.Exists(keyPath)) throw new InvalidOperationException("ssh-keygen did not create the admin key.");
            SetOwnerOnlyAcl(keyPath, isDirectory: false);
            if (File.Exists(keyPath + ".pub")) SetOwnerOnlyAcl(keyPath + ".pub", isDirectory: false);
            return await LoadSelectedKeyAsync(keyPath, "", cancellationToken).ConfigureAwait(false);
        }
        finally { GenerationGate.Release(); }
    }

    private static async Task<VpsAdminKey> LoadSelectedKeyAsync(string identityFile, string passphrase, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath;
        try { fullPath = Path.GetFullPath(identityFile); }
        catch (Exception) { throw new InvalidOperationException("Selected private-key path is invalid."); }
        if (!File.Exists(fullPath)) throw new InvalidOperationException("Selected private-key file does not exist.");

        try
        {
            using var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(fullPath) : new PrivateKeyFile(fullPath, passphrase);
            var ed25519 = key.HostKeyAlgorithms.SingleOrDefault(x => StringComparer.Ordinal.Equals(x.Name, "ssh-ed25519"));
            if (ed25519 is null || !IsEd25519Blob(ed25519.Data))
                throw new InvalidOperationException("The selected private key must be Ed25519.");
            cancellationToken.ThrowIfCancellationRequested();
            return new VpsAdminKey(fullPath, "ssh-ed25519 " + Convert.ToBase64String(ed25519.Data));
        }
        catch (OperationCanceledException) { throw; }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException) { throw new VpsKeyPassphraseRequiredException(administrative: true); }
        catch (InvalidOperationException) { throw; }
        catch (Exception) { throw new InvalidOperationException("The selected private key could not be read. Check its format and passphrase."); }
    }

    private static async Task GenerateKeyAsync(string keyPath, Guid serverId, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(FindSshKeygen())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", keyPath, "-C", "openviya-" + serverId.ToString("N") })
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start ssh-keygen.");
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KeyGenerationTimeout);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("ssh-keygen could not create the admin key.");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception) { throw new InvalidOperationException("Could not create the dedicated admin key with Windows OpenSSH."); }
    }

    private static string FindSshKeygen()
    {
        var systemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
        if (File.Exists(systemPath)) return systemPath;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim('"'), "ssh-keygen.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Windows OpenSSH ssh-keygen.exe was not found.");
    }

    private static bool IsEd25519Blob(byte[] blob)
    {
        if (blob.Length != 4 + 11 + 4 + 32) return false;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4)) == 11 &&
               blob.AsSpan(4, 11).SequenceEqual("ssh-ed25519"u8) &&
               System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(15, 4)) == 32;
    }

    private static void SetOwnerOnlyAcl(string path, bool isDirectory)
    {
        var currentSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var access = FileSystemRights.FullControl;
        foreach (var sid in new[] { currentSid, systemSid, adminsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, access, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (isDirectory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    private static void EnsureSafeAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        if (!security.AreAccessRulesProtected) throw new InvalidOperationException("Existing managed admin key ACL is not protected.");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            WindowsIdentity.GetCurrent().User?.Value ?? "",
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        };
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || !allowed.Contains(sid.Value) || (rule.FileSystemRights & ~FileSystemRights.FullControl) != 0)
                throw new InvalidOperationException("Existing managed admin key has unsafe Windows permissions.");
        }
    }
}
