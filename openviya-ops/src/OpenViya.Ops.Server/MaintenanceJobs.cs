using Npgsql;
using System.Security.Cryptography;
using System.Text;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Server;

public sealed class MaintenanceJobQueue(NpgsqlDataSource db, EncryptedStateStore encrypted)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<bool> IsActiveAsync(Guid serverId, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM maintenance_jobs WHERE server_id=@server AND status IN ('queued','running'))", c);
        cmd.Parameters.AddWithValue("server", serverId); return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }
    public async Task EnqueueAsync(RemoteMaintenanceJobRequest request, CancellationToken ct)
    {
        Validate(request);
        await _gate.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(724091021,hashtext(@run)), pg_advisory_xact_lock(724091020,hashtext(@server))", c, tx))
            {
                gate.Parameters.AddWithValue("run", request.RunId.ToString("D")); gate.Parameters.AddWithValue("server", request.ServerId.ToString("D")); await gate.ExecuteNonQueryAsync(ct);
            }
            await using (var existing = new NpgsqlCommand("SELECT server_id FROM maintenance_jobs WHERE run_id=@id", c, tx))
            {
                existing.Parameters.AddWithValue("id", request.RunId);
                var server = await existing.ExecuteScalarAsync(ct);
                if (server is Guid same && same == request.ServerId) { await tx.RollbackAsync(ct); return; }
                if (server is not null) throw new InvalidOperationException("RunId belongs to a different server.");
            }
            await using (var busy = new NpgsqlCommand("SELECT 1 FROM maintenance_jobs WHERE server_id=@server AND status IN ('queued','running') UNION ALL SELECT 1 FROM provision_jobs WHERE server_id=@server AND status IN ('queued','running') LIMIT 1", c, tx))
            {
                busy.Parameters.AddWithValue("server", request.ServerId);
                if (await busy.ExecuteScalarAsync(ct) is not null) throw new InvalidOperationException("Another server operation is already active.");
            }
            var body = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request));
            await using var insert = new NpgsqlCommand("INSERT INTO maintenance_jobs(run_id,server_id,action,status,request_nonce,request_tag,request_payload) VALUES(@id,@server,@action,'queued',@n,@t,@p)", c, tx);
            insert.Parameters.AddWithValue("id", request.RunId); insert.Parameters.AddWithValue("server", request.ServerId); insert.Parameters.AddWithValue("action", request.Action); insert.Parameters.AddWithValue("n", body[..12]); insert.Parameters.AddWithValue("t", body[12..28]); insert.Parameters.AddWithValue("p", body[28..]); await insert.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async Task<RemoteMaintenanceJobRequest?> ClaimAsync(CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT run_id,request_nonce,request_tag,request_payload FROM maintenance_jobs WHERE status='queued' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) { await tx.RollbackAsync(ct); return null; }
        var id = r.GetGuid(0); var data = encrypted.Unprotect([.. (byte[])r[1], .. (byte[])r[2], .. (byte[])r[3]]); await r.CloseAsync();
        await using var update = new NpgsqlCommand("UPDATE maintenance_jobs SET status='running',updated_at=now() WHERE run_id=@id", c, tx); update.Parameters.AddWithValue("id", id); await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct); return System.Text.Json.JsonSerializer.Deserialize<RemoteMaintenanceJobRequest>(data, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
    public async Task FinishAsync(RemoteMaintenanceJobResult result, CancellationToken ct)
    {
        var body = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result));
        var redacted = encrypted.Protect(Encoding.UTF8.GetBytes("terminal"));
        await using var c = await db.OpenConnectionAsync(ct); await using var cmd = new NpgsqlCommand("UPDATE maintenance_jobs SET status=@status,request_nonce=@rn,request_tag=@rt,request_payload=@rp,result_nonce=@n,result_tag=@t,result_payload=@p,updated_at=now() WHERE run_id=@id", c);
        cmd.Parameters.AddWithValue("status", result.State); cmd.Parameters.AddWithValue("rn", redacted[..12]); cmd.Parameters.AddWithValue("rt", redacted[12..28]); cmd.Parameters.AddWithValue("rp", redacted[28..]); cmd.Parameters.AddWithValue("n", body[..12]); cmd.Parameters.AddWithValue("t", body[12..28]); cmd.Parameters.AddWithValue("p", body[28..]); cmd.Parameters.AddWithValue("id", result.RunId); await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<RemoteMaintenanceJobResult?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct); await using var cmd = new NpgsqlCommand("SELECT server_id,action,status,result_nonce,result_tag,result_payload,updated_at FROM maintenance_jobs WHERE run_id=@id", c); cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) return null;
        if (!r.IsDBNull(3)) return System.Text.Json.JsonSerializer.Deserialize<RemoteMaintenanceJobResult>(encrypted.Unprotect([.. (byte[])r[3], .. (byte[])r[4], .. (byte[])r[5]]), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return new RemoteMaintenanceJobResult(id, r.GetGuid(0), r.GetString(1), r.GetString(2), null, r.GetString(2) == "needs-attention" ? "The operation may have changed the server before interruption. Inspect the server before retrying." : "Operation is queued or running.", UpdatedAt: r.GetDateTime(6));
    }
    public async Task<IReadOnlyList<RemoteMaintenanceJobResult>> GetRecentAsync(Guid serverId, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 50); var ids = new List<Guid>();
        await using (var c = await db.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT run_id FROM maintenance_jobs WHERE server_id=@server ORDER BY updated_at DESC LIMIT @take", c))
        {
            cmd.Parameters.AddWithValue("server", serverId); cmd.Parameters.AddWithValue("take", take);
            await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        }
        var result = new List<RemoteMaintenanceJobResult>(ids.Count);
        foreach (var id in ids) if (await GetAsync(id, ct) is { } job) result.Add(job);
        return result;
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        var redacted = encrypted.Protect(Encoding.UTF8.GetBytes("interrupted"));
        await using var c = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE maintenance_jobs SET status='needs-attention',request_nonce=@n,request_tag=@t,request_payload=@p,updated_at=now() WHERE status='running'", c);
        cmd.Parameters.AddWithValue("n", redacted[..12]); cmd.Parameters.AddWithValue("t", redacted[12..28]); cmd.Parameters.AddWithValue("p", redacted[28..]);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static void Validate(RemoteMaintenanceJobRequest request)
    {
        if (request.RunId == Guid.Empty || request.ServerId == Guid.Empty) throw new ArgumentException("RunId and server id are required.");
        if (request.Action is not ("apply-access" or "renew-certificate" or "create-backup")) throw new ArgumentException("Maintenance action is unsupported.");
        if (request.Action == "apply-access" && (request.AccessChange is null || request.Plan is null)) throw new ArgumentException("An access plan and credentials are required.");
        if (request.Action == "renew-certificate" && request.ActionRequest is null) throw new ArgumentException("Credentials and explicit confirmation are required.");
        if (request.Action == "create-backup" && request.Maintenance is null) throw new ArgumentException("Credentials are required.");
        var hostKey = request.Maintenance?.HostKey ?? request.AccessChange?.HostKey ?? request.ActionRequest?.HostKey ?? throw new ArgumentException("A trusted SSH host key is required.");
        if (hostKey.KeyType != "ssh-ed25519") throw new ArgumentException("Only Ed25519 SSH host keys are supported.");
        byte[] raw; try { raw = Convert.FromBase64String(hostKey.PublicKey); } catch { throw new ArgumentException("SSH host key is invalid."); }
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(raw)).TrimEnd('=');
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(fingerprint), Encoding.ASCII.GetBytes(hostKey.Fingerprint))) throw new ArgumentException("SSH host key fingerprint is invalid.");
    }
}
