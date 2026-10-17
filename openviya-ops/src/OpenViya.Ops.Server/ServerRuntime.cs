using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenViya.Ops.Core;
using OpenViya.Ops.Provisioning;

namespace OpenViya.Ops.Server;

public sealed class ServerOperationLocks
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    public bool TryAcquire(Guid id, out IDisposable lease)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) { lease = EmptyLease.Instance; return false; }
        lease = new Lease(gate); return true;
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
    private sealed class EmptyLease : IDisposable { public static readonly EmptyLease Instance = new(); public void Dispose() { } }
}

/// <summary>Worker-only profile adapter: serialized jobs update the latest row revision before each internal save.</summary>
public sealed class WorkerProfileStore(IProfileStore inner) : IProfileStore
{
    public Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
    public async Task SaveAsync(ServerProfile profile, CancellationToken ct = default)
    {
        var latest = (await inner.LoadAsync(ct)).FirstOrDefault(p => p.Id == profile.Id);
        await inner.SaveAsync(profile with { Revision = latest?.Revision ?? profile.Revision, IdentityFile = "" }, ct);
    }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    public Task ExportAsync(string filePath, CancellationToken ct = default) => inner.ExportAsync(filePath, ct);
    public Task<int> ImportAsync(string filePath, CancellationToken ct = default) => inner.ImportAsync(filePath, ct);
}

public sealed class PostgresManagedVpsKeyVault(EncryptedStateStore store) : IManagedVpsKeyVault
{
    public async Task<byte[]?> ReadAsync(Guid serverId, CancellationToken ct = default)
    {
        var encoded = await store.LoadAsync<string>("managed-vps-key", serverId.ToString("D"), ct);
        if (encoded is null) return null;
        try { return Convert.FromBase64String(encoded); } catch (FormatException) { throw new CryptographicException("Managed key record is invalid."); }
    }
    public Task WriteAsync(Guid serverId, byte[] privateKey, CancellationToken ct = default) => store.SaveAsync("managed-vps-key", serverId.ToString("D"), Convert.ToBase64String(privateKey), ct);
}

public sealed class EncryptedDnsTokenStore(EncryptedStateStore store) : IDnsTokenStore
{
    public Task<string?> LoadAsync(CancellationToken ct = default) => store.LoadAsync<string>("timeweb-token", "primary", ct);
    public Task SaveAsync(string token, CancellationToken ct = default) => store.SaveAsync("timeweb-token", "primary", token, ct);
    public Task DeleteAsync(CancellationToken ct = default) => store.DeleteAsync("timeweb-token", "primary", ct);
}

/// <summary>Only creates bounded, private temporary files beneath the configured runtime directory.</summary>
public sealed class ProfileKeyMaterialCache
{
    private readonly string _root;
    private readonly EncryptedStateStore _encrypted;
    public ProfileKeyMaterialCache(string root, EncryptedStateStore encrypted) { _root = Path.Combine(root, "ephemeral"); _encrypted = encrypted; Directory.CreateDirectory(_root); TryRestrict(_root, true); }
    public Task<string?> LoadManagedPassphraseAsync(Guid id, CancellationToken ct) => _encrypted.LoadAsync<string>("managed-admin-passphrase", id.ToString("D"), ct);
    public async Task<string> WriteTempAsync(Guid id, byte[] key, CancellationToken ct)
    {
        if (key.Length is 0 or > 128 * 1024) throw new ArgumentException("Private key size is invalid.");
        var path = Path.Combine(_root, $"{id:N}-{Guid.NewGuid():N}.key");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var f = new FileStream(path, options)) { await f.WriteAsync(key, ct); await f.FlushAsync(ct); }
        return path;
    }
    public void DeleteTemp(string path) { if (path.StartsWith(_root, StringComparison.Ordinal) && File.Exists(path)) try { File.Delete(path); } catch { } }
    private static void TryRestrict(string path, bool dir) { try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, dir ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }
}

public sealed class ProvisionWorker(IServiceScopeFactory scopes, NpgsqlDataSource db, ServerOperationLocks operationLocks, ILogger<ProvisionWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var lockConnection = await db.OpenConnectionAsync(stoppingToken);
        await using var advisory = new NpgsqlCommand("SELECT pg_try_advisory_lock(724091018)", lockConnection);
        if (!(bool)(await advisory.ExecuteScalarAsync(stoppingToken) ?? false)) { logger.LogError("Another OpenViya Ops worker holds the global provisioning lock."); lifetime.StopApplication(); return; }
        await using (var recover = new NpgsqlCommand("UPDATE provision_jobs SET status='queued',updated_at=now() WHERE status='running'", lockConnection)) await recover.ExecuteNonQueryAsync(stoppingToken);
        using (var scope = scopes.CreateScope()) await scope.ServiceProvider.GetRequiredService<MaintenanceJobQueue>().RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await using var checkLock = new NpgsqlCommand("SELECT 1", lockConnection);
                    await checkLock.ExecuteScalarAsync(stoppingToken);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // A reconnect would not own the session lock. Restart the host before claiming more work.
                    logger.LogError("The worker database lock connection was lost; stopping the server.");
                    lifetime.StopApplication();
                    return;
                }
                ProvisionJob? job;
                using (var scope = scopes.CreateScope()) job = await scope.ServiceProvider.GetRequiredService<ProvisionJobQueue>().ClaimAsync(stoppingToken);
                if (job is not null) { await ExecuteJobAsync(job, stoppingToken); continue; }
                RemoteMaintenanceJobRequest? maintenance;
                using (var scope = scopes.CreateScope()) maintenance = await scope.ServiceProvider.GetRequiredService<MaintenanceJobQueue>().ClaimAsync(stoppingToken);
                if (maintenance is not null) { await ExecuteMaintenanceAsync(maintenance, stoppingToken); continue; }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Provision worker loop failed ({Type}).", ex.GetType().Name); await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        }
    }

    private async Task ExecuteJobAsync(ProvisionJob job, CancellationToken ct)
    {
        if (!operationLocks.TryAcquire(job.ServerId, out var operationLease))
        {
            await using var retryConnection = await db.OpenConnectionAsync(ct);
            await using var retry = new NpgsqlCommand("UPDATE provision_jobs SET status='queued' WHERE run_id=@id", retryConnection);
            retry.Parameters.AddWithValue("id", job.RunId); await retry.ExecuteNonQueryAsync(ct); return;
        }
        using (operationLease)
        {
        using var scope = scopes.CreateScope(); var services = scope.ServiceProvider;
        var queue = services.GetRequiredService<ProvisionJobQueue>(); var profiles = services.GetRequiredService<WorkerProfileStore>();
        var provisioner = services.GetRequiredService<IVpsProvisioner>(); var keys = services.GetRequiredService<ProfileKeyMaterialCache>();
        var keyVault = services.GetRequiredService<IManagedVpsKeyVault>(); var encrypted = services.GetRequiredService<EncryptedStateStore>();
        byte[]? adminBytesForVault = null;
        string? initialPath = null, adminPath = null;
        try
        {
            var request = job.Request;
            var profile = (request.Profile with { IdentityFile = "" });
            await profiles.SaveAsync(profile, ct);
            var initial = request.InitialAccess;
            if (initial.PrivateKeyBase64 is not null) initialPath = await keys.WriteTempAsync(job.ServerId, Convert.FromBase64String(initial.PrivateKeyBase64), ct);
            if (request.AdminPrivateKeyBase64 is not null)
            {
                adminBytesForVault = Convert.FromBase64String(request.AdminPrivateKeyBase64);
                adminPath = await keys.WriteTempAsync(job.ServerId, adminBytesForVault, ct);
            }
            var vps = new VpsProvisionRequest
            {
                RunId = request.RunId, Profile = profile,
                InitialAccess = new VpsSshAccess { UserName = initial.UserName, Method = initial.Method, Password = initial.Password ?? "", IdentityFile = initialPath ?? "", KeyPassphrase = initial.KeyPassphrase ?? "" },
                HostKey = request.HostKey, AdminIdentityFile = adminPath ?? "", AdminKeyPassphrase = request.AdminKeyPassphrase ?? "",
                AcmeEmail = request.AcmeEmail, PanelAccess = request.PanelAccess, PanelCidr = request.PanelCidr,
                FreshServerConfirmed = request.FreshServerConfirmed, LetsEncryptTermsAccepted = request.LetsEncryptTermsAccepted,
                ProviderFirewallConfirmed = request.ProviderFirewallConfirmed
            };
            var result = await provisioner.RunAsync(vps, cancellationToken: ct);
            if (ct.IsCancellationRequested || result.Run.State == VpsProvisionRunState.Paused)
            {
                await queue.SetStatusAsync(job.RunId, "queued", CancellationToken.None);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return;
            }
            if (result.Run.State == VpsProvisionRunState.Complete && adminBytesForVault is not null)
            {
                await keyVault.WriteAsync(job.ServerId, adminBytesForVault, ct);
                await encrypted.SaveAsync("managed-admin-passphrase", job.ServerId.ToString("D"), request.AdminKeyPassphrase ?? "", ct);
            }
            await queue.SetStatusAsync(job.RunId, result.Run.State == VpsProvisionRunState.Complete ? "complete" : result.Run.State == VpsProvisionRunState.NeedsOperator ? "needs-operator" : "failed", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await queue.SetStatusAsync(job.RunId, "queued", CancellationToken.None); throw; }
        catch (Exception ex)
        {
            // Keep error text generic: provider, SSH and credential material is not copied to logs or the API.
            logger.LogWarning("Provision job {RunId} failed ({Type}).", job.RunId, ex.GetType().Name);
            try { await services.GetRequiredService<IVpsProvisionStore>().SaveRunAsync(new VpsProvisionRun(job.RunId, job.ServerId, VpsProvisionStage.Failed, "Provisioning failed. Inspect server state before retrying.", DateTimeOffset.UtcNow) { State = VpsProvisionRunState.Failed }, CancellationToken.None); } catch { }
            await queue.SetStatusAsync(job.RunId, "failed", CancellationToken.None);
        }
        finally { if (initialPath is not null) keys.DeleteTemp(initialPath); if (adminPath is not null) keys.DeleteTemp(adminPath); if (adminBytesForVault is not null) CryptographicOperations.ZeroMemory(adminBytesForVault); }
        }
    }

    private async Task ExecuteMaintenanceAsync(RemoteMaintenanceJobRequest job, CancellationToken ct)
    {
        if (!operationLocks.TryAcquire(job.ServerId, out var operationLease))
        {
            using var scopeRetry = scopes.CreateScope();
            await scopeRetry.ServiceProvider.GetRequiredService<MaintenanceJobQueue>().FinishAsync(new RemoteMaintenanceJobResult(job.RunId, job.ServerId, job.Action, "needs-attention", false, "Another operation is active; inspect the server before retrying."), CancellationToken.None); return;
        }
        using (operationLease)
        using (var scope = scopes.CreateScope())
        {
            var services = scope.ServiceProvider; var queue = services.GetRequiredService<MaintenanceJobQueue>();
            var profiles = services.GetRequiredService<IProfileStore>(); var maintenance = services.GetRequiredService<IVpsServerMaintenanceService>();
            var vault = services.GetRequiredService<IManagedVpsKeyVault>(); var keys = services.GetRequiredService<ProfileKeyMaterialCache>(); var encrypted = services.GetRequiredService<EncryptedStateStore>();
            string? keyPath = null;
            try
            {
                var profile = (await profiles.LoadAsync(ct)).FirstOrDefault(p => p.Id == job.ServerId) ?? throw new InvalidOperationException("Server profile was not found.");
                var accessDto = job.Maintenance?.Access ?? job.AccessChange?.Access ?? job.ActionRequest?.Access ?? throw new InvalidOperationException("SSH access credentials are missing.");
                if (accessDto.Method == VpsAuthMethod.PrivateKey)
                {
                    byte[]? privateKey = string.IsNullOrEmpty(accessDto.PrivateKeyBase64) ? await vault.ReadAsync(job.ServerId, ct) : Convert.FromBase64String(accessDto.PrivateKeyBase64);
                    if (privateKey is null) throw new InvalidOperationException("No server-managed SSH key is available.");
                    try { keyPath = await keys.WriteTempAsync(job.ServerId, privateKey, ct); } finally { CryptographicOperations.ZeroMemory(privateKey); }
                }
                var storedPassphrase = await encrypted.LoadAsync<string>("managed-admin-passphrase", job.ServerId.ToString("D"), ct);
                var access = new VpsSshAccess { UserName = accessDto.UserName, Method = accessDto.Method, Password = accessDto.Password ?? "", IdentityFile = keyPath ?? "", KeyPassphrase = accessDto.KeyPassphrase ?? storedPassphrase ?? "" };
                var hostKey = job.Maintenance?.HostKey ?? job.AccessChange?.HostKey ?? job.ActionRequest?.HostKey ?? throw new InvalidOperationException("SSH host key is missing.");
                VpsMaintenanceResult result = job.Action switch
                {
                    "apply-access" => await maintenance.ApplyAccessChangeAsync(profile, access, hostKey, job.Plan!, job.AccessChange!.ConfirmHighRisk, ct),
                    "renew-certificate" => await maintenance.RenewCertificateAsync(profile, access, hostKey, job.ActionRequest!.ConfirmHighRisk, ct),
                    "create-backup" => await maintenance.CreateBackupAsync(profile, access, hostKey, ct),
                    _ => throw new InvalidOperationException("Unsupported maintenance action.")
                };
                await queue.FinishAsync(new RemoteMaintenanceJobResult(job.RunId, job.ServerId, job.Action, result.Succeeded ? "complete" : "failed", result.Succeeded, result.Message, result.Snapshot, result.BackupName, DateTimeOffset.UtcNow), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Maintenance job {RunId} failed ({Type}).", job.RunId, ex.GetType().Name);
                await queue.FinishAsync(new RemoteMaintenanceJobResult(job.RunId, job.ServerId, job.Action, "failed", false, "The operation failed. Inspect server state before retrying.", UpdatedAt: DateTimeOffset.UtcNow), CancellationToken.None);
            }
            finally { if (keyPath is not null) keys.DeleteTemp(keyPath); }
        }
    }
}
