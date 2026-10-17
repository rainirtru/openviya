using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Json;
using Npgsql;
using OpenViya.Ops.Core;
using OpenViya.Ops.Dns;
using OpenViya.Ops.Network;
using OpenViya.Ops.Provisioning;
using OpenViya.Ops.Server;
using Renci.SshNet;

var builder = WebApplication.CreateBuilder(args);
var tokenFile = RequiredPath("OPENVIYA_OPS_TOKEN_FILE");
var keyFile = RequiredPath("OPENVIYA_OPS_ENCRYPTION_KEY_FILE");
var dbConn = ReadConfiguredFile("OPENVIYA_OPS_DB_CONNECTION_STRING_FILE") ?? Environment.GetEnvironmentVariable("OPENVIYA_OPS_DB_CONNECTION_STRING") ?? builder.Configuration.GetConnectionString("Ops");
if (string.IsNullOrWhiteSpace(dbConn)) throw new InvalidOperationException("Configure OPENVIYA_OPS_DB_CONNECTION_STRING_FILE or ConnectionStrings:Ops.");
var authToken = TrimToken(File.ReadAllBytes(tokenFile));
if (authToken.Length < 32) throw new InvalidOperationException("The bearer token file must contain at least 32 bytes.");
var encryptionKey = ParseKey(File.ReadAllBytes(keyFile));
builder.Services.Configure<JsonOptions>(o => { o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase; o.SerializerOptions.MaxDepth = 32; o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()); });
builder.Services.AddSingleton(NpgsqlDataSource.Create(dbConn));
builder.Services.AddSingleton(new AuthToken(authToken));
builder.Services.AddSingleton(sp => new EncryptedStateStore(sp.GetRequiredService<NpgsqlDataSource>(), encryptionKey));
builder.Services.AddSingleton<PostgresProfileStore>();
builder.Services.AddSingleton<IProfileStore>(sp => sp.GetRequiredService<PostgresProfileStore>());
builder.Services.AddSingleton<WorkerProfileStore>();
builder.Services.AddSingleton<PostgresServerStateStore>();
builder.Services.AddSingleton<IServerStateStore>(sp => sp.GetRequiredService<PostgresServerStateStore>());
builder.Services.AddSingleton<PostgresDnsRecordStore>();
builder.Services.AddSingleton<IDnsRecordStore>(sp => sp.GetRequiredService<PostgresDnsRecordStore>());
builder.Services.AddSingleton<PostgresProvisionStore>();
builder.Services.AddSingleton<IVpsProvisionStore>(sp => sp.GetRequiredService<PostgresProvisionStore>());
builder.Services.AddSingleton<ProvisionJobQueue>();
builder.Services.AddSingleton<MaintenanceJobQueue>();
builder.Services.AddSingleton<ServerOperationLocks>();
var runtimeRoot = Environment.GetEnvironmentVariable("OPENVIYA_OPS_RUNTIME_DIR") ?? "/tmp/openviya-worker";
Directory.CreateDirectory(runtimeRoot);
builder.Services.AddSingleton(new AppPaths(runtimeRoot));
builder.Services.AddSingleton<IVpsRemoteSessionFactory, SshNetVpsSessionFactory>();
builder.Services.AddSingleton<IVpsKeyService, VaultVpsKeyService>();
builder.Services.AddSingleton<IVpsServerMaintenanceService, ServerMaintenanceService>();
builder.Services.AddSingleton<IVpsProvisioner>(sp => new VpsProvisioner(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<WorkerProfileStore>(), sp.GetRequiredService<IVpsProvisionStore>(), sp.GetRequiredService<IVpsRemoteSessionFactory>(), sp.GetRequiredService<IVpsKeyService>(), sp.GetRequiredService<IDnsProviderService>(), sp.GetRequiredService<IPublicDnsService>()));
builder.Services.AddSingleton<IDnsTokenStore, EncryptedDnsTokenStore>();
builder.Services.AddSingleton<IDnsProviderService>(sp => new TimewebDnsClient(sp.GetRequiredService<IDnsTokenStore>()));
builder.Services.AddSingleton<IPublicDnsService, PublicDnsService>();
builder.Services.AddSingleton<IManagedVpsKeyVault, PostgresManagedVpsKeyVault>();
builder.Services.AddSingleton(sp => new ProfileKeyMaterialCache(runtimeRoot, sp.GetRequiredService<EncryptedStateStore>()));
builder.Services.AddHostedService<ProvisionWorker>();

var app = builder.Build();
await app.Services.GetRequiredService<EncryptedStateStore>().InitializeAsync(app.Lifetime.ApplicationStopping);

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/api/health" && HttpMethods.IsGet(ctx.Request.Method)) { await next(); return; }
    var header = ctx.Request.Headers.Authorization.ToString();
    var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetBytes(header[7..]) : [];
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(supplied), ctx.RequestServices.GetRequiredService<AuthToken>().Digest))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new RemoteApiError("unauthorized", "Bearer authorization is required.")); return;
    }
    await next();
});
app.Use(async (ctx, next) =>
{
    var parts = ctx.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
    if (parts.Length >= 4 && parts[0] == "api" && parts[1] == "servers" && Guid.TryParse(parts[2], out var id) && parts[3] == "maintenance")
    {
        var guard = ctx.RequestServices.GetRequiredService<ServerOperationLocks>();
        if (!guard.TryAcquire(id, out var lease)) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new RemoteApiError("operation_in_progress", "Another operation is active for this server.")); return; }
        using (lease) await next(); return;
    }
    await next();
});
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        // Exception details and provider/SSH output are intentionally excluded from logs and responses.
        app.Logger.LogWarning("Request failed ({Type}) at {Path}", ex.GetType().Name, ctx.Request.Path.Value);
        ctx.Response.StatusCode = ex is ArgumentException or InvalidDataException ? 400 : ex is KeyNotFoundException ? 404 : ex is InvalidOperationException ? 409 : 500;
        await ctx.Response.WriteAsJsonAsync(new RemoteApiError("request_failed", SafeMessage(ex)));
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/profiles", async (IProfileStore store, CancellationToken ct) => Results.Ok(await store.LoadAsync(ct)));
app.MapGet("/api/profiles/{id:guid}", async (Guid id, IProfileStore store, CancellationToken ct) => (await store.LoadAsync(ct)).FirstOrDefault(p => p.Id == id) is { } profile ? Results.Ok(profile) : Results.NotFound());
app.MapPut("/api/profiles/{id:guid}", async (Guid id, ServerProfile profile, IProfileStore store, ProvisionJobQueue jobs, MaintenanceJobQueue maintenanceJobs, CancellationToken ct) =>
{
    if (id != profile.Id) return Results.BadRequest(new RemoteApiError("id_mismatch", "Profile identifier does not match the route."));
    if (await jobs.IsActiveAsync(id, ct) || await maintenanceJobs.IsActiveAsync(id, ct)) return Results.Conflict(new RemoteApiError("operation_in_progress", "Profile cannot be edited while a server job is active."));
    var existing = (await store.LoadAsync(ct)).FirstOrDefault(p => p.Id == id);
    if ((existing?.Revision ?? 0) != profile.Revision) return Results.Conflict(new RemoteApiError("stale_profile", "Profile changed on another device. Reload it before saving."));
    await store.SaveAsync(profile with { UpdatedAt = DateTimeOffset.UtcNow, IdentityFile = "" }, ct);
    return Results.Ok((await store.LoadAsync(ct)).Single(p => p.Id == id));
});
app.MapDelete("/api/profiles/{id:guid}", async (Guid id, IProfileStore store, ProvisionJobQueue jobs, MaintenanceJobQueue maintenanceJobs, CancellationToken ct) => { if (await jobs.IsActiveAsync(id, ct) || await maintenanceJobs.IsActiveAsync(id, ct)) return Results.Conflict(); await store.DeleteAsync(id, ct); return Results.NoContent(); });
app.MapPost("/api/profiles/import", async (HttpRequest req, IProfileStore store, CancellationToken ct) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new RemoteApiError("form_required", "Send a multipart form with a file field named file."));
    var file = req.Form.Files.GetFile("file"); if (file is null || file.Length is <= 0 or > 2 * 1024 * 1024) return Results.BadRequest(new RemoteApiError("file_invalid", "Profile import must be a file up to 2 MiB."));
    var temp = Path.Combine(Path.GetTempPath(), $"openviya-import-{Guid.NewGuid():N}.json");
    try { await using (var output = File.Create(temp)) await file.CopyToAsync(output, ct); var count = await store.ImportAsync(temp, ct); return Results.Ok(new RemoteImportResult(count)); }
    finally { try { File.Delete(temp); } catch { } }
});
app.MapGet("/api/profiles/export", async (IProfileStore store, CancellationToken ct) =>
{
    var profiles = await store.LoadAsync(ct);
    var safe = profiles.Select(p => new { p.Id, p.Name, p.Role, p.Region, p.NodeIndex, p.Host, p.SshPort, p.UserName, p.PanelDomain, p.PanelPort, p.VpnDomain, p.VpnPort, p.ControlServerId, p.UpdatedAt, p.LocalPort, p.SubdomainMode, p.DnsZone, p.DnsAddress, p.PanelAccess, p.PanelOpenMode, p.PanelAllowedIpsText, p.Revision });
    return Results.Ok(safe);
});
app.MapGet("/api/servers/{id:guid}/observations", async (Guid id, IServerStateStore store, CancellationToken ct) => await store.LoadAsync(id, ct) is { } state ? Results.Ok(state) : Results.NoContent());
app.MapPut("/api/servers/{id:guid}/observations", async (Guid id, ServerObservedState state, IServerStateStore store, CancellationToken ct) => { if (id != state.ServerId) return Results.BadRequest(); await store.SaveAsync(state, ct); return Results.Ok(state); });
app.MapGet("/api/servers/{id:guid}/dns-records", async (Guid id, IDnsRecordStore store, CancellationToken ct) => Results.Ok(await store.LoadAsync(id, ct)));
app.MapPut("/api/servers/{id:guid}/dns-records", async (Guid id, DnsManagedRecord[] records, IDnsRecordStore store, CancellationToken ct) =>
{
    if (records.Length > 100 || records.Any(r => r.ServerId != id)) return Results.BadRequest();
    foreach (var record in records) await store.SaveAsync(record, ct); return Results.Ok(await store.LoadAsync(id, ct));
});
app.MapGet("/api/servers/{id:guid}/credentials", async (Guid id, IVpsProvisionStore store, CancellationToken ct) => await store.LoadCredentialsAsync(id, ct) is { } creds ? Results.Ok(creds) : Results.NoContent());
app.MapPut("/api/servers/{id:guid}/credentials", async (Guid id, VpsPanelCredentials credentials, IVpsProvisionStore store, CancellationToken ct) => { if (credentials.ServerId != id) return Results.BadRequest(); await store.SaveCredentialsAsync(credentials, ct); return Results.NoContent(); });
app.MapPost("/api/servers/{id:guid}/credentials", async (Guid id, VpsPanelCredentials credentials, IVpsProvisionStore store, CancellationToken ct) => { if (credentials.ServerId != id) return Results.BadRequest(); await store.SaveCredentialsAsync(credentials, ct); return Results.NoContent(); });
app.MapGet("/api/servers/{id:guid}/host-key", async (Guid id, EncryptedStateStore state, CancellationToken ct) => await state.LoadAsync<HostKeyCandidate>("host-key", id.ToString("D"), ct) is { } key ? Results.Ok(key) : Results.NoContent());
app.MapPut("/api/servers/{id:guid}/host-key", async (Guid id, HostKeyCandidate key, EncryptedStateStore state, CancellationToken ct) => { ValidateHostKey(key); await state.SaveAsync("host-key", id.ToString("D"), key, ct); return Results.NoContent(); });
app.MapPut("/api/servers/{id:guid}/admin-key", async (Guid id, RemoteAdminKeyRequest request, IManagedVpsKeyVault vault, EncryptedStateStore state, ProfileKeyMaterialCache material, CancellationToken ct) =>
{
    ValidateKeyBase64(request.PrivateKeyBase64);
    if (string.IsNullOrEmpty(request.PrivateKeyBase64)) return Results.BadRequest();
    var privateKey = Convert.FromBase64String(request.PrivateKeyBase64);
    var path = await material.WriteTempAsync(id, privateKey, ct);
    try
    {
        try
        {
            using var parsed = string.IsNullOrEmpty(request.KeyPassphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, request.KeyPassphrase);
            if (!parsed.HostKeyAlgorithms.Any(a => a.Name == "ssh-ed25519")) return Results.BadRequest(new RemoteApiError("key_invalid", "An Ed25519 private key is required."));
        }
        catch (OperationCanceledException) { throw; }
        catch { return Results.BadRequest(new RemoteApiError("key_invalid", "Private key could not be parsed with the supplied passphrase.")); }
        await vault.WriteAsync(id, privateKey, ct); await state.SaveAsync("managed-admin-passphrase", id.ToString("D"), request.KeyPassphrase, ct);
    }
    finally { CryptographicOperations.ZeroMemory(privateKey); material.DeleteTemp(path); }
    return Results.NoContent();
});
app.MapPut("/api/dns/token", async (RemoteDnsTokenRequest request, IDnsTokenStore store, CancellationToken ct) => { if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 16_384) return Results.BadRequest(); await store.SaveAsync(request.Token, ct); return Results.NoContent(); });
app.MapGet("/api/dns/token", async (IDnsTokenStore store, CancellationToken ct) => Results.Ok(new RemoteDnsCredentialState(await store.LoadAsync(ct) is not null, null)));
app.MapDelete("/api/dns/token", async (IDnsTokenStore store, CancellationToken ct) => { await store.DeleteAsync(ct); return Results.NoContent(); });
app.MapPost("/api/dns/check-zone", async (RemoteDnsZoneRequest request, IDnsProviderService dns, CancellationToken ct) => Results.Ok(await dns.CheckZoneAsync(request.Zone, ct)));
app.MapPost("/api/dns/plan", async (RemoteDnsPlanRequest request, IDnsProviderService dns, CancellationToken ct) =>
{
    var errors = ProfileValidator.Validate(request.Profile);
    if (errors.Count != 0) return Results.BadRequest(new RemoteApiError("profile_invalid", "Profile is invalid for DNS planning."));
    return Results.Ok(await dns.BuildPlanAsync(ProfileDefaults.Apply(request.Profile), request.Ttl, ct));
});
app.MapPost("/api/dns/apply", async (DnsPlan submitted, IDnsProviderService dns) =>
{
    var ct = app.Lifetime.ApplicationStopping;
    var errors = ProfileValidator.Validate(submitted.Profile);
    if (errors.Count != 0) return Results.BadRequest(new RemoteApiError("profile_invalid", "DNS plan profile is invalid."));
    var fresh = await dns.BuildPlanAsync(ProfileDefaults.Apply(submitted.Profile), submitted.Ttl, ct);
    var sameItems = submitted.Items.Count == fresh.Items.Count && submitted.Items.Zip(fresh.Items).All(pair => pair.First == pair.Second);
    if (!submitted.CanApply || !fresh.CanApply || submitted.Profile.Id != fresh.Profile.Id ||
        !StringComparer.Ordinal.Equals(submitted.Zone, fresh.Zone) || !StringComparer.Ordinal.Equals(submitted.Address, fresh.Address) ||
        !submitted.NameServers.SequenceEqual(fresh.NameServers, StringComparer.OrdinalIgnoreCase) || !sameItems)
        return Results.Conflict(new RemoteApiError("dns_plan_stale", "DNS records changed after the plan was reviewed. Request a new plan."));
    return Results.Ok(await dns.ApplyAsync(fresh, ct));
});

app.MapPost("/api/provisioning/runs", async (RemoteProvisionRequest request, ProvisionJobQueue jobs, IVpsProvisionStore runs, EncryptedStateStore state, IProfileStore profiles, CancellationToken ct) =>
{
    ValidateRemoteRequest(request);
    var existing = (await profiles.LoadAsync(ct)).FirstOrDefault(p => p.Id == request.Profile.Id);
    if (existing is not null && existing.Revision != request.Profile.Revision) return Results.Conflict(new RemoteApiError("stale_profile", "Profile changed on another device. Reload it before starting provisioning."));
    await jobs.EnqueueAsync(request, ct);
    await state.SaveAsync("host-key", request.Profile.Id.ToString("D"), request.HostKey, ct);
    return Results.Accepted($"/api/provisioning/runs/{request.RunId:D}", await jobs.ReadRunAsync(request.RunId, ct) ?? new VpsProvisionRun(request.RunId, request.Profile.Id, VpsProvisionStage.Created, "Queued", DateTimeOffset.UtcNow) { State = VpsProvisionRunState.Running });
});
app.MapPost("/api/provisioning/runs/{runId:guid}/resume", async (Guid runId, RemoteProvisionRequest request, ProvisionJobQueue jobs, IVpsProvisionStore runs, EncryptedStateStore state, IProfileStore profiles, CancellationToken ct) =>
{
    ValidateRemoteRequest(request);
    if (request.RunId != runId) return Results.BadRequest(new RemoteApiError("run_id_mismatch", "RunId does not match the route."));
    var prior = await jobs.ReadRunAsync(runId, ct) ?? throw new KeyNotFoundException("Provision run was not found.");
    if (prior.ServerId != request.Profile.Id) return Results.Conflict(new RemoteApiError("server_mismatch", "Run belongs to a different server."));
    var existing = (await profiles.LoadAsync(ct)).FirstOrDefault(p => p.Id == request.Profile.Id);
    if (existing is not null) request = request with { Profile = existing with { IdentityFile = "" } };
    var resumed = await jobs.ResumeAsync(request, prior, ct);
    await state.SaveAsync("host-key", request.Profile.Id.ToString("D"), request.HostKey, ct);
    return Results.Accepted($"/api/provisioning/runs/{runId:D}", resumed);
});
app.MapGet("/api/provisioning/runs/{runId:guid}", async (Guid runId, ProvisionJobQueue jobs, CancellationToken ct) => await jobs.ReadRunAsync(runId, ct) is { } run ? Results.Ok(run) : Results.NotFound());
app.MapGet("/api/provisioning/runs/{runId:guid}/history", async (Guid runId, IVpsProvisionStore store, CancellationToken ct) => Results.Ok(await store.LoadRunHistoryAsync(runId, ct)));
app.MapGet("/api/provisioning/history", async (Guid serverId, IVpsProvisionStore store, CancellationToken ct) => Results.Ok(await store.LoadLatestRunAsync(serverId, ct)));
app.MapGet("/api/provisioning/runs/latest", async (Guid serverId, IVpsProvisionStore store, CancellationToken ct) => await store.LoadLatestRunAsync(serverId, ct) is { } latest ? Results.Ok(latest) : Results.NoContent());
app.MapPost("/api/maintenance/jobs", async (RemoteMaintenanceJobRequest request, MaintenanceJobQueue jobs, CancellationToken ct) => { await jobs.EnqueueAsync(request, ct); return Results.Accepted($"/api/maintenance/jobs/{request.RunId:D}", await jobs.GetAsync(request.RunId, ct)); });
app.MapGet("/api/maintenance/jobs/{runId:guid}", async (Guid runId, MaintenanceJobQueue jobs, CancellationToken ct) => await jobs.GetAsync(runId, ct) is { } result ? Results.Ok(result) : Results.NotFound());
app.MapGet("/api/servers/{id:guid}/maintenance/jobs", async (Guid id, int? take, MaintenanceJobQueue jobs, CancellationToken ct) => Results.Ok(await jobs.GetRecentAsync(id, take ?? 20, ct)));

app.MapPost("/api/servers/{id:guid}/maintenance/inspect", async (Guid id, RemoteMaintenanceRequest req, IProfileStore profiles, IVpsServerMaintenanceService svc, ProfileKeyMaterialCache keys, IManagedVpsKeyVault vault, CancellationToken ct) =>
{
    var profile = await GetProfile(profiles, id, ct); var access = await MaterializeAccess(req.Access, id, keys, vault, ct);
    try { return Results.Ok(await svc.InspectAsync(profile, access, ValidateHostKey(req.HostKey), ct)); } finally { keys.DeleteTemp(access.IdentityFile); }
});
app.MapPost("/api/servers/{id:guid}/maintenance/plan-access", async (Guid id, RemoteAccessChangeRequest req, IProfileStore profiles, IVpsServerMaintenanceService svc, ProfileKeyMaterialCache keys, IManagedVpsKeyVault vault, CancellationToken ct) =>
{
    var profile = await GetProfile(profiles, id, ct); var access = await MaterializeAccess(req.Access, id, keys, vault, ct);
    try { return Results.Ok(await svc.PlanAccessChangeAsync(profile, access, ValidateHostKey(req.HostKey), req.Mode, req.AllowedIpsText, req.PanelPort, ct)); } finally { keys.DeleteTemp(access.IdentityFile); }
});
app.MapPost("/api/servers/{id:guid}/maintenance/check-certificate", (Guid id, RemoteMaintenanceRequest req, IProfileStore p, IVpsServerMaintenanceService svc, ProfileKeyMaterialCache keys, IManagedVpsKeyVault vault, CancellationToken ct) => MaintenanceAction(id, req, p, svc, keys, vault, (s,profile,a,h,t) => s.CheckCertificateAsync(profile,a,h,t), ct));
app.MapPost("/api/servers/{id:guid}/maintenance/updates", (Guid id, RemoteMaintenanceRequest req, IProfileStore p, IVpsServerMaintenanceService svc, ProfileKeyMaterialCache keys, IManagedVpsKeyVault vault, CancellationToken ct) => MaintenanceAction(id, req, p, svc, keys, vault, (s,profile,a,h,t) => s.CheckUpdatesAsync(profile,a,h,t), ct));
app.MapPost("/api/servers/{id:guid}/maintenance/backups/{name}", async (Guid id, string name, HttpRequest request, IProfileStore p, IVpsServerMaintenanceService svc, ProfileKeyMaterialCache keys, IManagedVpsKeyVault vault, CancellationToken ct) =>
{
    if (name.Length > 160 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) || name is "." or "..") return Results.BadRequest();
    var body = await request.ReadFromJsonAsync<RemoteMaintenanceRequest>(cancellationToken: ct) ?? throw new ArgumentException("Access credentials are required.");
    var profile = await GetProfile(p, id, ct); var access = await MaterializeAccess(body.Access, id, keys, vault, ct);
    try { var bytes = await svc.DownloadBackupAsync(profile, access, ValidateHostKey(body.HostKey), name, ct); return Results.File(bytes, "application/octet-stream", name); } finally { keys.DeleteTemp(access.IdentityFile); }
});

app.Run();

static string RequiredPath(string name) { var value = Environment.GetEnvironmentVariable(name); if (string.IsNullOrWhiteSpace(value) || !File.Exists(value)) throw new InvalidOperationException($"Required secret file is not configured: {name}."); return value; }
static string? ReadConfiguredFile(string? pathVariable) { if (pathVariable is null) return null; var path = Environment.GetEnvironmentVariable(pathVariable); return string.IsNullOrWhiteSpace(path) ? null : File.ReadAllText(path).Trim(); }
static byte[] TrimToken(byte[] bytes) { var start = 0; var end = bytes.Length; while (start < end && bytes[start] is 9 or 10 or 13 or 32) start++; while (end > start && bytes[end - 1] is 9 or 10 or 13 or 32) end--; return bytes[start..end]; }
static byte[] ParseKey(byte[] raw)
{
    if (raw.Length == 32) return raw;
    var value = Encoding.UTF8.GetString(raw).Trim();
    try { var hex = Convert.FromHexString(value); if (hex.Length == 32) return hex; } catch { }
    try { var b64 = Convert.FromBase64String(value); if (b64.Length == 32) return b64; } catch { }
    throw new InvalidOperationException("Encryption key file must contain 32 raw bytes, 64 hexadecimal characters, or base64 for 32 bytes.");
}
static string SafeMessage(Exception ex) => ex switch { ArgumentException => "Invalid request.", InvalidDataException => "Imported data is invalid.", KeyNotFoundException => "The requested record was not found.", InvalidOperationException => "The operation could not be completed in the current state.", _ => "The request could not be completed." };
static HostKeyCandidate ValidateHostKey(HostKeyCandidate key)
{
    if (key.KeyType != "ssh-ed25519") throw new ArgumentException("Only explicitly trusted Ed25519 SSH host keys are supported.");
    byte[] raw; try { raw = Convert.FromBase64String(key.PublicKey); } catch { throw new ArgumentException("Trusted SSH host key is malformed."); }
    var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(raw)).TrimEnd('=');
    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(fingerprint), Encoding.ASCII.GetBytes(key.Fingerprint))) throw new ArgumentException("SSH host key fingerprint does not match its public key.");
    return key;
}
static void ValidateRemoteRequest(RemoteProvisionRequest request)
{
    if (request.RunId == Guid.Empty || request.Profile.Id == Guid.Empty) throw new ArgumentException("RunId and profile id are required.");
    if (request.InitialAccess is null) throw new ArgumentException("SSH credentials are required.");
    ValidateCredential(request.InitialAccess); ValidateHostKey(request.HostKey);
    ValidateKeyBase64(request.AdminPrivateKeyBase64);
}
static void ValidateCredential(RemoteSshCredential credential, bool allowManagedKey = false) { if (credential.UserName.Length is 0 or > 32 || credential.UserName.Any(char.IsControl)) throw new ArgumentException("SSH username is invalid."); ValidateKeyBase64(credential.PrivateKeyBase64); if (credential.Method == VpsAuthMethod.Password && string.IsNullOrEmpty(credential.Password)) throw new ArgumentException("SSH password is required."); if (credential.Method == VpsAuthMethod.PrivateKey && string.IsNullOrWhiteSpace(credential.PrivateKeyBase64) && !allowManagedKey) throw new ArgumentException("Private key bytes are required."); }
static void ValidateKeyBase64(string? value) { if (value is null) return; if (value.Length > 180_000) throw new ArgumentException("Private key exceeds the 128 KiB upload limit."); try { if (Convert.FromBase64String(value).Length > 128 * 1024) throw new ArgumentException("Private key exceeds the 128 KiB upload limit."); } catch (FormatException) { throw new ArgumentException("Private key encoding is invalid."); } }
static async Task<ServerProfile> GetProfile(IProfileStore store, Guid id, CancellationToken ct) => (await store.LoadAsync(ct)).FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("Server profile was not found.");
static async Task<VpsSshAccess> MaterializeAccess(RemoteSshCredential remote, Guid id, ProfileKeyMaterialCache cache, IManagedVpsKeyVault vault, CancellationToken ct)
{
    ValidateCredential(remote, allowManagedKey: true); string path = ""; var useVaultKey = remote.PrivateKeyBase64 is null && remote.Method == VpsAuthMethod.PrivateKey;
    if (remote.PrivateKeyBase64 is not null) path = await cache.WriteTempAsync(id, Convert.FromBase64String(remote.PrivateKeyBase64), ct);
    else if (remote.Method == VpsAuthMethod.PrivateKey)
    {
        var bytes = await vault.ReadAsync(id, ct) ?? throw new ArgumentException("No server-managed SSH key is available; submit private key bytes.");
        try { path = await cache.WriteTempAsync(id, bytes, ct); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    var passphrase = remote.KeyPassphrase;
    if (useVaultKey && string.IsNullOrEmpty(passphrase)) passphrase = await cache.LoadManagedPassphraseAsync(id, ct);
    return new VpsSshAccess { UserName = remote.UserName, Method = remote.Method, Password = remote.Password ?? "", IdentityFile = path, KeyPassphrase = passphrase ?? "" };
}
static async Task<IResult> MaintenanceAction(Guid id, RemoteMaintenanceRequest req, IProfileStore profiles, IVpsServerMaintenanceService svc, ProfileKeyMaterialCache keys, IManagedVpsKeyVault vault, Func<IVpsServerMaintenanceService, ServerProfile, VpsSshAccess, HostKeyCandidate, CancellationToken, Task<VpsMaintenanceResult>> action, CancellationToken ct)
{
    var profile = await GetProfile(profiles, id, ct); var access = await MaterializeAccess(req.Access, id, keys, vault, ct);
    try { return Results.Ok(await action(svc, profile, access, ValidateHostKey(req.HostKey), ct)); } finally { keys.DeleteTemp(access.IdentityFile); }
}
public sealed class AuthToken(byte[] token) { public byte[] Digest { get; } = SHA256.HashData(token); }
