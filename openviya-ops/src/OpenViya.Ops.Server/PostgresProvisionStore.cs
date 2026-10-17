using Npgsql;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Server;

public sealed class PostgresProvisionStore(NpgsqlDataSource db, EncryptedStateStore encrypted) : IVpsProvisionStore
{
    public async Task SaveRunAsync(VpsProvisionRun run, CancellationToken ct = default)
    {
        var payload = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(run));
        await using var c = await db.OpenConnectionAsync(ct);
        await using (var up = new NpgsqlCommand("INSERT INTO ops_state(kind,id,nonce,tag,payload,updated_at) VALUES('provision-run',@id,@n,@t,@p,now()) ON CONFLICT(kind,id) DO UPDATE SET nonce=excluded.nonce,tag=excluded.tag,payload=excluded.payload,updated_at=now()", c))
        {
            up.Parameters.AddWithValue("id", run.RunId.ToString("D")); up.Parameters.AddWithValue("n", payload[..12]); up.Parameters.AddWithValue("t", payload[12..28]); up.Parameters.AddWithValue("p", payload[28..]); await up.ExecuteNonQueryAsync(ct);
        }
        await using var ev = new NpgsqlCommand("INSERT INTO provision_events(run_id,server_id,nonce,tag,payload) VALUES(@run,@server,@n,@t,@p)", c);
        ev.Parameters.AddWithValue("run", run.RunId); ev.Parameters.AddWithValue("server", run.ServerId); ev.Parameters.AddWithValue("n", payload[..12]); ev.Parameters.AddWithValue("t", payload[12..28]); ev.Parameters.AddWithValue("p", payload[28..]); await ev.ExecuteNonQueryAsync(ct);
    }
    public async Task<VpsProvisionRun?> LoadLatestRunAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nonce,tag,payload FROM ops_state WHERE kind='provision-run' AND id IN (SELECT run_id::text FROM provision_jobs WHERE server_id=@server ORDER BY created_at DESC LIMIT 1)", c);
        cmd.Parameters.AddWithValue("server", serverId);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) return null;
        return System.Text.Json.JsonSerializer.Deserialize<VpsProvisionRun>(encrypted.Unprotect([.. (byte[])r[0], .. (byte[])r[1], .. (byte[])r[2]]));
    }
    public async Task<IReadOnlyList<VpsProvisionRun>> LoadRunHistoryAsync(Guid runId, CancellationToken ct = default)
    {
        var result = new List<VpsProvisionRun>();
        await using var c = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nonce,tag,payload FROM provision_events WHERE run_id=@run ORDER BY event_id LIMIT 500", c); cmd.Parameters.AddWithValue("run", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(System.Text.Json.JsonSerializer.Deserialize<VpsProvisionRun>(encrypted.Unprotect([.. (byte[])r[0], .. (byte[])r[1], .. (byte[])r[2]]))!);
        return result;
    }
    public Task SaveCredentialsAsync(VpsPanelCredentials credentials, CancellationToken ct = default) => encrypted.SaveAsync("panel-credentials", credentials.ServerId.ToString("D"), credentials, ct);
    public Task<VpsPanelCredentials?> LoadCredentialsAsync(Guid serverId, CancellationToken ct = default) => encrypted.LoadAsync<VpsPanelCredentials>("panel-credentials", serverId.ToString("D"), ct);
}

public sealed record ProvisionJob(Guid RunId, Guid ServerId, RemoteProvisionRequest Request);

public sealed class ProvisionJobQueue(NpgsqlDataSource db, EncryptedStateStore encrypted)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<bool> IsActiveAsync(Guid serverId, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM provision_jobs WHERE server_id=@server AND status IN ('queued','running'))", c);
        cmd.Parameters.AddWithValue("server", serverId); return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }
    public async Task EnqueueAsync(RemoteProvisionRequest request, CancellationToken ct)
    {
        if (request.RunId == Guid.Empty || request.Profile.Id == Guid.Empty) throw new ArgumentException("RunId and server id are required.");
        await _gate.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await LockRequestAsync(c, tx, request.RunId, request.Profile.Id, ct);
            await using (var duplicate = new NpgsqlCommand("SELECT server_id,status FROM provision_jobs WHERE run_id=@run FOR UPDATE", c, tx))
            {
                duplicate.Parameters.AddWithValue("run", request.RunId);
                await using var r = await duplicate.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    if (r.GetGuid(0) != request.Profile.Id) throw new InvalidOperationException("This RunId belongs to another server.");
                    return;
                }
            }
            await using (var busy = new NpgsqlCommand("SELECT 1 FROM provision_jobs WHERE server_id=@server AND status IN ('queued','running') UNION ALL SELECT 1 FROM maintenance_jobs WHERE server_id=@server AND status IN ('queued','running') LIMIT 1", c, tx))
            {
                busy.Parameters.AddWithValue("server", request.Profile.Id);
                if (await busy.ExecuteScalarAsync(ct) is not null) throw new InvalidOperationException("A provisioning job is already active for this server.");
            }
            var payload = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ProvisionJob(request.RunId, request.Profile.Id, request)));
            await using var insert = new NpgsqlCommand("INSERT INTO provision_jobs(run_id,server_id,status,nonce,tag,payload) VALUES(@run,@server,'queued',@n,@t,@p)", c, tx);
            insert.Parameters.AddWithValue("run", request.RunId); insert.Parameters.AddWithValue("server", request.Profile.Id); insert.Parameters.AddWithValue("n", payload[..12]); insert.Parameters.AddWithValue("t", payload[12..28]); insert.Parameters.AddWithValue("p", payload[28..]); await insert.ExecuteNonQueryAsync(ct);
            var initialRun = new VpsProvisionRun(request.RunId, request.Profile.Id, VpsProvisionStage.Created, "Queued for server execution.", DateTimeOffset.UtcNow) { State = VpsProvisionRunState.Running };
            var runCipher = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(initialRun));
            await using (var saveRun = new NpgsqlCommand("INSERT INTO ops_state(kind,id,nonce,tag,payload,updated_at) VALUES('provision-run',@id,@n,@t,@p,now())", c, tx))
            {
                saveRun.Parameters.AddWithValue("id", request.RunId.ToString("D")); saveRun.Parameters.AddWithValue("n", runCipher[..12]); saveRun.Parameters.AddWithValue("t", runCipher[12..28]); saveRun.Parameters.AddWithValue("p", runCipher[28..]); await saveRun.ExecuteNonQueryAsync(ct);
            }
            await using (var eventRun = new NpgsqlCommand("INSERT INTO provision_events(run_id,server_id,nonce,tag,payload) VALUES(@run,@server,@n,@t,@p)", c, tx))
            {
                eventRun.Parameters.AddWithValue("run", request.RunId); eventRun.Parameters.AddWithValue("server", request.Profile.Id); eventRun.Parameters.AddWithValue("n", runCipher[..12]); eventRun.Parameters.AddWithValue("t", runCipher[12..28]); eventRun.Parameters.AddWithValue("p", runCipher[28..]); await eventRun.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async Task<VpsProvisionRun> ResumeAsync(RemoteProvisionRequest request, VpsProvisionRun previous, CancellationToken ct)
    {
        if (request.RunId != previous.RunId || request.Profile.Id != previous.ServerId) throw new ArgumentException("Resume must use the original RunId and server.");
        await _gate.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
            await LockRequestAsync(c, tx, request.RunId, request.Profile.Id, ct);
            string? status;
            await using (var check = new NpgsqlCommand("SELECT status FROM provision_jobs WHERE run_id=@run AND server_id=@server FOR UPDATE", c, tx))
            {
                check.Parameters.AddWithValue("run", request.RunId); check.Parameters.AddWithValue("server", request.Profile.Id);
                status = (string?)await check.ExecuteScalarAsync(ct);
            }
            var resumablePaused = status == "queued" && previous.State == VpsProvisionRunState.Paused;
            if (status is not ("failed" or "needs-operator") && !resumablePaused) throw new InvalidOperationException("Only a completed failed, paused, or operator-attention run can be explicitly resumed.");
            await using (var busy = new NpgsqlCommand("SELECT 1 FROM provision_jobs WHERE server_id=@server AND run_id<>@run AND status IN ('queued','running') UNION ALL SELECT 1 FROM maintenance_jobs WHERE server_id=@server AND status IN ('queued','running') LIMIT 1", c, tx))
            {
                busy.Parameters.AddWithValue("server", request.Profile.Id); busy.Parameters.AddWithValue("run", request.RunId);
                if (await busy.ExecuteScalarAsync(ct) is not null) throw new InvalidOperationException("Another operation is active for this server.");
            }
            var body = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ProvisionJob(request.RunId, request.Profile.Id, request)));
            await using (var update = new NpgsqlCommand("UPDATE provision_jobs SET status='queued',nonce=@n,tag=@t,payload=@p,updated_at=now() WHERE run_id=@run", c, tx))
            {
                update.Parameters.AddWithValue("run", request.RunId); update.Parameters.AddWithValue("n", body[..12]); update.Parameters.AddWithValue("t", body[12..28]); update.Parameters.AddWithValue("p", body[28..]); await update.ExecuteNonQueryAsync(ct);
            }
            var resumed = previous with { Message = "Operator requested continuation of the saved run.", UpdatedAt = DateTimeOffset.UtcNow, State = VpsProvisionRunState.Running };
            var runCipher = encrypted.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resumed));
            await using (var save = new NpgsqlCommand("UPDATE ops_state SET nonce=@n,tag=@t,payload=@p,updated_at=now() WHERE kind='provision-run' AND id=@id", c, tx))
            {
                save.Parameters.AddWithValue("id", request.RunId.ToString("D")); save.Parameters.AddWithValue("n", runCipher[..12]); save.Parameters.AddWithValue("t", runCipher[12..28]); save.Parameters.AddWithValue("p", runCipher[28..]); await save.ExecuteNonQueryAsync(ct);
            }
            await using (var ev = new NpgsqlCommand("INSERT INTO provision_events(run_id,server_id,nonce,tag,payload) VALUES(@run,@server,@n,@t,@p)", c, tx))
            {
                ev.Parameters.AddWithValue("run", request.RunId); ev.Parameters.AddWithValue("server", request.Profile.Id); ev.Parameters.AddWithValue("n", runCipher[..12]); ev.Parameters.AddWithValue("t", runCipher[12..28]); ev.Parameters.AddWithValue("p", runCipher[28..]); await ev.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct); return resumed;
        }
        finally { _gate.Release(); }
    }
    public async Task<ProvisionJob?> ClaimAsync(CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT run_id,nonce,tag,payload FROM provision_jobs WHERE status='queued' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) { await tx.RollbackAsync(ct); return null; }
        var id = r.GetGuid(0); var body = encrypted.Unprotect([.. (byte[])r[1], .. (byte[])r[2], .. (byte[])r[3]]); await r.CloseAsync();
        await using var update = new NpgsqlCommand("UPDATE provision_jobs SET status='running',updated_at=now() WHERE run_id=@id", c, tx); update.Parameters.AddWithValue("id", id); await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct); return System.Text.Json.JsonSerializer.Deserialize<ProvisionJob>(body);
    }
    public async Task SetStatusAsync(Guid id, string status, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        if (status is "complete" or "failed" or "needs-operator" or "paused" or "needs-attention")
        {
            var marker = encrypted.Protect(System.Text.Encoding.UTF8.GetBytes("terminal"));
            await using var cmd = new NpgsqlCommand("UPDATE provision_jobs SET status=@status,nonce=@n,tag=@t,payload=@p,updated_at=now() WHERE run_id=@id", c);
            cmd.Parameters.AddWithValue("status", status); cmd.Parameters.AddWithValue("n", marker[..12]); cmd.Parameters.AddWithValue("t", marker[12..28]); cmd.Parameters.AddWithValue("p", marker[28..]); cmd.Parameters.AddWithValue("id", id); await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var cmd = new NpgsqlCommand("UPDATE provision_jobs SET status=@status,updated_at=now() WHERE run_id=@id", c);
            cmd.Parameters.AddWithValue("status", status); cmd.Parameters.AddWithValue("id", id); await cmd.ExecuteNonQueryAsync(ct);
        }
    }
    public async Task<VpsProvisionRun?> ReadRunAsync(Guid runId, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct); await using var cmd = new NpgsqlCommand("SELECT nonce,tag,payload FROM ops_state WHERE kind='provision-run' AND id=@id", c); cmd.Parameters.AddWithValue("id", runId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) return null;
        return System.Text.Json.JsonSerializer.Deserialize<VpsProvisionRun>(encrypted.Unprotect([.. (byte[])r[0], .. (byte[])r[1], .. (byte[])r[2]]));
    }
    private static async Task LockRequestAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid runId, Guid serverId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(724091021,hashtext(@run)), pg_advisory_xact_lock(724091020,hashtext(@server))", c, tx);
        cmd.Parameters.AddWithValue("run", runId.ToString("D")); cmd.Parameters.AddWithValue("server", serverId.ToString("D")); await cmd.ExecuteNonQueryAsync(ct);
    }
}
