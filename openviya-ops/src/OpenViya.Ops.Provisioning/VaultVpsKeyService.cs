using System.Diagnostics;
using System.Security.Cryptography;
using OpenViya.Ops.Core;
using Renci.SshNet;

namespace OpenViya.Ops.Provisioning;

/// <summary>The implementation must encrypt private key bytes at rest.</summary>
public interface IManagedVpsKeyVault
{
    Task<byte[]?> ReadAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task WriteAsync(Guid serverId, byte[] privateKey, CancellationToken cancellationToken = default);
}

/// <summary>Linux worker keys are durable in the vault and materialized only in its private tmpfs.</summary>
public sealed class VaultVpsKeyService(AppPaths runtimePaths, IManagedVpsKeyVault vault) : IVpsKeyService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<VpsAdminKey> PrepareAsync(Guid serverId, string identityFile, string passphrase,
        CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("Server id is required.", nameof(serverId));
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The LAN worker requires Linux containers.");
        if (!string.IsNullOrWhiteSpace(identityFile)) return ReadKey(identityFile, passphrase);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(runtimePaths.Root, "managed-keys", serverId.ToString("N"));
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var keyPath = Path.Combine(directory, "admin_ed25519");
            var saved = await vault.ReadAsync(serverId, cancellationToken).ConfigureAwait(false);
            if (saved is not null)
            {
                try
                {
                    if (saved.Length is 0 or > 65536) throw new InvalidOperationException("Invalid managed key payload.");
                    await WritePrivateAsync(keyPath, saved, cancellationToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(saved); }
            }
            else
            {
                // Never replace an orphan key: persist it if an earlier vault write lost its response.
                if (!File.Exists(keyPath)) await GenerateAsync(keyPath, cancellationToken).ConfigureAwait(false);
                var bytes = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
                try
                {
                    ReadKey(keyPath, "");
                    await vault.WriteAsync(serverId, bytes, cancellationToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            return ReadKey(keyPath, "");
        }
        finally { _gate.Release(); }
    }

    private static VpsAdminKey ReadKey(string path, string passphrase)
    {
        try
        {
            using var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
            var algorithm = key.HostKeyAlgorithms.FirstOrDefault(item => item.Name == "ssh-ed25519")
                ?? throw new InvalidOperationException("An Ed25519 key is required.");
            return new VpsAdminKey(Path.GetFullPath(path), "ssh-ed25519 " + Convert.ToBase64String(algorithm.Data));
        }
        catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
        { throw new VpsKeyPassphraseRequiredException(administrative: true); }
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The LAN worker requires Linux containers.");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task GenerateAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ssh-keygen")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", path, "-C", "openviya-lan" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start ssh-keygen.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not generate the managed SSH key.");
    }
}
