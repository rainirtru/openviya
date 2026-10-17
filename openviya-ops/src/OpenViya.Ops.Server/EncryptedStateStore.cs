using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Server;

/// <summary>Application-level AES-GCM protection for every persisted application payload.</summary>
public sealed class EncryptedStateStore
{
    private readonly NpgsqlDataSource _db;
    private readonly byte[] _key;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EncryptedStateStore(NpgsqlDataSource db, byte[] key) { _db = db; _key = key; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS ops_state(kind text NOT NULL, id text NOT NULL, nonce bytea NOT NULL, tag bytea NOT NULL, payload bytea NOT NULL, updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS provision_jobs(run_id uuid PRIMARY KEY, server_id uuid NOT NULL, status text NOT NULL, nonce bytea NOT NULL, tag bytea NOT NULL, payload bytea NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX IF NOT EXISTS provision_jobs_status_created ON provision_jobs(status,created_at);
            CREATE TABLE IF NOT EXISTS provision_events(event_id bigserial PRIMARY KEY, run_id uuid NOT NULL, server_id uuid NOT NULL, nonce bytea NOT NULL, tag bytea NOT NULL, payload bytea NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX IF NOT EXISTS provision_events_run ON provision_events(run_id,event_id);
            CREATE TABLE IF NOT EXISTS maintenance_jobs(run_id uuid PRIMARY KEY, server_id uuid NOT NULL, action text NOT NULL, status text NOT NULL, request_nonce bytea NOT NULL, request_tag bytea NOT NULL, request_payload bytea NOT NULL, result_nonce bytea, result_tag bytea, result_payload bytea, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX IF NOT EXISTS maintenance_jobs_status_created ON maintenance_jobs(status,created_at);
            """, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAsync<T>(string kind, string id, T value, CancellationToken ct)
    {
        var (nonce, tag, cipher) = Seal(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        await using var c = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ops_state(kind,id,nonce,tag,payload,updated_at) VALUES(@kind,@id,@nonce,@tag,@payload,now())
            ON CONFLICT(kind,id) DO UPDATE SET nonce=excluded.nonce,tag=excluded.tag,payload=excluded.payload,updated_at=now()
            """, c);
        cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("nonce", nonce); cmd.Parameters.AddWithValue("tag", tag); cmd.Parameters.AddWithValue("payload", cipher);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<T?> LoadAsync<T>(string kind, string id, CancellationToken ct)
    {
        await using var c = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nonce,tag,payload FROM ops_state WHERE kind=@kind AND id=@id", c);
        cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return default;
        var json = Open((byte[])r[0], (byte[])r[1], (byte[])r[2]);
        return JsonSerializer.Deserialize<T>(json, Json);
    }

    public async Task<IReadOnlyList<T>> LoadAllAsync<T>(string kind, CancellationToken ct)
    {
        var result = new List<T>();
        await using var c = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nonce,tag,payload FROM ops_state WHERE kind=@kind ORDER BY id", c);
        cmd.Parameters.AddWithValue("kind", kind);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<T>(Open((byte[])r[0], (byte[])r[1], (byte[])r[2]), Json)!);
        return result;
    }

    public async Task DeleteAsync(string kind, string id, CancellationToken ct)
    {
        await using var c = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM ops_state WHERE kind=@kind AND id=@id", c);
        cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(string kind, string id, CancellationToken ct)
    {
        await using var c = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM ops_state WHERE kind=@kind AND id=@id)", c);
        cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    public byte[] Protect(byte[] data) { var (n,t,c) = Seal(data); return [.. n, .. t, .. c]; }
    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 28) throw new CryptographicException("Encrypted record is truncated.");
        return Open(data[..12], data[12..28], data[28..]);
    }
    private (byte[] Nonce, byte[] Tag, byte[] Cipher) Seal(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(_key, 16); aes.Encrypt(nonce, plaintext, cipher, tag); return (nonce, tag, cipher);
    }
    private byte[] Open(byte[] nonce, byte[] tag, byte[] cipher)
    {
        var plain = new byte[cipher.Length]; using var aes = new AesGcm(_key, 16); aes.Decrypt(nonce, cipher, tag, plain); return plain;
    }
}

public sealed class PostgresProfileStore(EncryptedStateStore store, NpgsqlDataSource db) : IProfileStore
{
    private const string Kind = "profile";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken ct = default) => await store.LoadAllAsync<ServerProfile>(Kind, ct);

    public async Task SaveAsync(ServerProfile profile, CancellationToken ct = default)
    {
        await MutateAsync(current =>
        {
            var safe = profile with { IdentityFile = "" };
            var previous = current.FirstOrDefault(p => p.Id == safe.Id);
            if (safe.Revision != (previous?.Revision ?? 0))
                throw new InvalidOperationException("The profile changed on another client. Refresh before saving.");
            var next = safe with { Revision = checked((previous?.Revision ?? 0) + 1), UpdatedAt = DateTimeOffset.UtcNow };
            return current.Where(p => p.Id != safe.Id).Append(next).ToList();
        }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Server id is required.");
        await MutateAsync(current =>
        {
            if (current.Any(p => p.ControlServerId == id))
                throw new InvalidOperationException("Reassign dependent servers before deleting their CONTROL profile.");
            return current.Where(p => p.Id != id).ToList();
        }, ct);
    }

    public static ServerProfile ForExport(ServerProfile profile) => profile with { IdentityFile = "", PanelBasePath = "/", Revision = 0 };
    public async Task ExportAsync(string filePath, CancellationToken ct = default)
    {
        var profiles = (await LoadAsync(ct)).Select(ForExport).ToArray();
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new { Version = 3, Profiles = profiles }, Json), ct);
    }

    public async Task<int> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Profile import size is invalid.");
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        if (bytes.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("Profile import size is invalid.");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        JsonElement entries = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            entries = root.EnumerateObject().FirstOrDefault(p => p.Name.Equals("profiles", StringComparison.OrdinalIgnoreCase)).Value;
            if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Profile import must contain a profiles array.");
        }
        var profiles = entries.Deserialize<ServerProfile[]>(Json) ?? [];
        return await ImportProfilesAsync(profiles, ct);
    }

    public async Task<int> ImportProfilesAsync(IReadOnlyList<ServerProfile> profiles, CancellationToken ct = default)
    {
        if (profiles.Count > 500 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
            throw new InvalidDataException("Profile import contains duplicate or too many entries.");
        var imported = 0;
        await MutateAsync(current =>
        {
            var ids = current.Select(p => p.Id).ToHashSet();
            var additions = profiles.Where(p => !ids.Contains(p.Id)).Select(p => p with
                { IdentityFile = "", Revision = 1, UpdatedAt = DateTimeOffset.UtcNow }).ToArray();
            imported = additions.Length;
            return current.Concat(additions).ToList();
        }, ct);
        return imported;
    }

    private async Task MutateAsync(Func<List<ServerProfile>, List<ServerProfile>> transform, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // One transaction serializes profile validation and writes across every client/process.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(724091019)", connection, tx))
            await gate.ExecuteNonQueryAsync(ct);
        var current = new List<ServerProfile>();
        await using (var read = new NpgsqlCommand("SELECT nonce,tag,payload FROM ops_state WHERE kind='profile' FOR UPDATE", connection, tx))
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                current.Add(JsonSerializer.Deserialize<ServerProfile>(store.Unprotect([.. (byte[])reader[0], .. (byte[])reader[1], .. (byte[])reader[2]]), Json)
                    ?? throw new InvalidDataException("Stored profile is invalid."));
        }
        var next = transform(current);
        ValidateSet(next);
        foreach (var removed in current.Where(p => next.All(n => n.Id != p.Id)))
        {
            await using var delete = new NpgsqlCommand("DELETE FROM ops_state WHERE kind='profile' AND id=@id", connection, tx);
            delete.Parameters.AddWithValue("id", removed.Id.ToString("D")); await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var profile in next.Where(p => !current.Contains(p)))
        {
            var payload = store.Protect(JsonSerializer.SerializeToUtf8Bytes(profile, Json));
            await using var write = new NpgsqlCommand("INSERT INTO ops_state(kind,id,nonce,tag,payload,updated_at) VALUES('profile',@id,@n,@t,@p,now()) ON CONFLICT(kind,id) DO UPDATE SET nonce=excluded.nonce,tag=excluded.tag,payload=excluded.payload,updated_at=now()", connection, tx);
            write.Parameters.AddWithValue("id", profile.Id.ToString("D")); write.Parameters.AddWithValue("n", payload[..12]); write.Parameters.AddWithValue("t", payload[12..28]); write.Parameters.AddWithValue("p", payload[28..]);
            await write.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static void ValidateSet(IReadOnlyList<ServerProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            var errors = ProfileValidator.Validate(profile);
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
        }
        if (profiles.Select(p => p.Id).Distinct().Count() != profiles.Count) throw new InvalidDataException("Duplicate profile ids.");
        if (profiles.Where(p => p.Role == ServerRole.VpnNode).GroupBy(p => (p.Region.ToLowerInvariant(), p.NodeIndex)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate VPN node region/index.");
        foreach (var profile in profiles.Where(p => p.ControlServerId is not null))
            if (profile.ControlServerId == profile.Id || !profiles.Any(c => c.Id == profile.ControlServerId && c.Role == ServerRole.Control))
                throw new InvalidOperationException("A referenced CONTROL server is missing or invalid.");
        var fixedPorts = profiles.Where(p => p.LocalPort.HasValue).Select(p => p.LocalPort!.Value);
        if (fixedPorts.Distinct().Count() != fixedPorts.Count()) throw new InvalidOperationException("A local tunnel port is assigned to multiple profiles.");
    }
}

public sealed class PostgresServerStateStore(EncryptedStateStore store) : IServerStateStore
{
    public Task SaveAsync(ServerObservedState state, CancellationToken ct = default) => store.SaveAsync("observation", state.ServerId.ToString("D"), state, ct);
    public Task<ServerObservedState?> LoadAsync(Guid serverId, CancellationToken ct = default) => store.LoadAsync<ServerObservedState>("observation", serverId.ToString("D"), ct);
}

public sealed class PostgresDnsRecordStore(EncryptedStateStore store) : IDnsRecordStore
{
    public Task SaveAsync(DnsManagedRecord record, CancellationToken ct = default) => store.SaveAsync("dns-record:" + record.ServerId.ToString("D"), record.Fqdn, record, ct);
    public async Task<IReadOnlyList<DnsManagedRecord>> LoadAsync(Guid serverId, CancellationToken ct = default) => await store.LoadAllAsync<DnsManagedRecord>("dns-record:" + serverId.ToString("D"), ct);
}
