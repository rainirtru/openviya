using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Storage;

/// <summary>Local profile registry. The database contains metadata only; identity file contents are never read.</summary>
public sealed class SqliteProfileStore : IProfileStore
{
    private const int SchemaVersion = 3;
    private const int MaxImportBytes = 2 * 1024 * 1024;
    private const int MaxImportProfiles = 500;
    private readonly AppPaths _paths;
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };

    public SqliteProfileStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths), "Пути приложения не заданы.");
        if (string.IsNullOrWhiteSpace(paths.Root)) throw new ArgumentException("Укажите каталог данных приложения.", nameof(paths));
        Directory.CreateDirectory(paths.Root);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString();
        Migrate();
    }

    public async Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,name,role,region,node_index,host,ssh_port,user_name,identity_file,panel_domain,panel_port,panel_base_path,vpn_domain,vpn_port,control_server_id,updated_at,local_port,subdomain_mode,dns_zone,dns_address,panel_access,panel_open_mode,panel_allowed_ips_text FROM server_profiles ORDER BY name COLLATE NOCASE,id;";
            var profiles = new List<ServerProfile>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) profiles.Add(ReadProfile(reader));
            return profiles;
        }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось расшифровать сохранённый путь панели для текущего пользователя Windows."); }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось прочитать локальную базу профилей серверов."); }
    }

    public async Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile), "Профиль сервера не задан.");
        var prepared = ProfileDefaults.Apply(profile);
        var errors = ProfileValidator.Validate(prepared);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var existing = await ReadAllForInvariantCheck(connection, transaction, cancellationToken).ConfigureAwait(false);
            var candidate = existing.Where(p => p.Id != prepared.Id).Append(prepared).ToArray();
            ValidateSet(candidate);
            await WriteProfile(connection, transaction, prepared, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось сохранить профиль сервера в локальной базе данных."); }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось защитить путь панели для текущего пользователя Windows."); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(id));
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM server_profiles WHERE control_server_id=$id LIMIT 1;";
                check.Parameters.AddWithValue("$id", id.ToString("D"));
                if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("У этого профиля CONTROL есть дочерние серверы. Сначала назначьте им другой CONTROL.");
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM server_profiles WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось удалить профиль сервера из локальной базы данных."); }
    }

    public async Task ExportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Укажите файл экспорта.", nameof(filePath));
        var profiles = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var document = new ExportDocument(3, profiles.Select(p => new ExportProfile(p.Id, p.Name, p.Role, p.Region, p.NodeIndex, p.Host, p.SshPort, p.UserName, p.PanelDomain, p.PanelPort, p.VpnDomain, p.VpnPort, p.ControlServerId, p.UpdatedAt, p.LocalPort, p.SubdomainMode, p.DnsZone, p.DnsAddress, p.PanelAccess, p.PanelOpenMode, p.PanelAllowedIpsText)).ToArray());
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Укажите каталог для файла экспорта.", nameof(filePath));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, fullPath, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    public async Task<int> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Укажите файл импорта.", nameof(filePath));
        ExportDocument document;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length is <= 0 or > MaxImportBytes) throw new InvalidDataException("Файл импорта отсутствует, пуст или превышает ограничение 2 МиБ.");
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxImportBytes) throw new InvalidDataException("Файл импорта пуст или превышает ограничение 2 МиБ.");
            using var bounded = new MemoryStream();
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (bounded.Length + read > MaxImportBytes) throw new InvalidDataException("Файл импорта превышает ограничение 2 МиБ.");
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            bounded.Position = 0;
            document = await JsonSerializer.DeserializeAsync<ExportDocument>(bounded, JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Файл импорта пуст.");
        }
        catch (JsonException) { throw new InvalidDataException("Файл импорта содержит некорректный JSON OpenViya Ops."); }
        if (document.Version is not (1 or 2 or 3) || document.Profiles is null || document.Profiles.Length > MaxImportProfiles) throw new InvalidDataException("Версия формата не поддерживается или превышено допустимое число профилей.");
        if (document.Profiles.Any(x => x is null)) throw new InvalidDataException("Файл импорта содержит пустую запись профиля.");
        var imported = document.Profiles.Select(x => x.ToProfile(document.Version)).ToArray();
        if (imported.Select(p => p.Id).Distinct().Count() != imported.Length) throw new InvalidDataException("Файл импорта содержит повторяющиеся идентификаторы профилей.");
        foreach (var profile in imported)
        {
            var errors = ProfileValidator.Validate(profile);
            if (errors.Count != 0) throw new InvalidDataException($"Профиль содержит некорректные данные: {string.Join(" ", errors)}");
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var existing = await ReadAllForInvariantCheck(connection, transaction, cancellationToken).ConfigureAwait(false);
            var incomingIds = imported.Select(p => p.Id).ToHashSet();
            var candidate = existing.Where(p => !incomingIds.Contains(p.Id)).Concat(imported).ToArray();
            ValidateSet(candidate);
            // An export is sorted by display name; a child may precede its CONTROL.
            await using (var defer = connection.CreateCommand())
            {
                defer.Transaction = transaction;
                defer.CommandText = "PRAGMA defer_foreign_keys=ON;";
                await defer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var profile in imported) await WriteProfile(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return imported.Length;
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось импортировать профили в локальную базу данных; изменения отменены."); }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось защитить пути панелей для текущего пользователя Windows."); }
    }

    private void Migrate()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (version > SchemaVersion) throw new InvalidOperationException("Локальная база профилей создана более новой версией OpenViya Ops.");
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE server_profiles (
                      id TEXT PRIMARY KEY NOT NULL,
                      name TEXT NOT NULL,
                      role INTEGER NOT NULL,
                      region TEXT NOT NULL,
                      node_index INTEGER NOT NULL,
                      host TEXT NOT NULL,
                      ssh_port INTEGER NOT NULL,
                      user_name TEXT NOT NULL,
                      identity_file TEXT NOT NULL,
                      panel_domain TEXT NOT NULL,
                      panel_port INTEGER NOT NULL,
                      panel_base_path BLOB NOT NULL,
                      vpn_domain TEXT NOT NULL,
                      vpn_port INTEGER NOT NULL,
                      control_server_id TEXT NULL REFERENCES server_profiles(id) ON DELETE RESTRICT,
                      updated_at TEXT NOT NULL,
                      local_port INTEGER NULL
                    );
                    CREATE UNIQUE INDEX ux_server_ssh_endpoint ON server_profiles(lower(host), ssh_port);
                    CREATE UNIQUE INDEX ux_server_panel_endpoint ON server_profiles(lower(panel_domain), panel_port) WHERE panel_domain <> '';
                    CREATE INDEX ix_server_control ON server_profiles(control_server_id);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
                version = 1;
            }
            if (version == 1)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "ALTER TABLE server_profiles ADD COLUMN subdomain_mode INTEGER NOT NULL DEFAULT 1; ALTER TABLE server_profiles ADD COLUMN dns_zone TEXT NOT NULL DEFAULT 'openviya.online'; ALTER TABLE server_profiles ADD COLUMN dns_address TEXT NOT NULL DEFAULT ''; PRAGMA user_version=2;";
                command.ExecuteNonQuery();
                transaction.Commit();
                version = 2;
            }
            if (version == 2)
            {
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "ALTER TABLE server_profiles ADD COLUMN panel_access INTEGER NOT NULL DEFAULT 0; ALTER TABLE server_profiles ADD COLUMN panel_open_mode INTEGER NOT NULL DEFAULT 0; ALTER TABLE server_profiles ADD COLUMN panel_allowed_ips_text TEXT NOT NULL DEFAULT ''; CREATE TABLE server_observed_state (server_id TEXT PRIMARY KEY NOT NULL REFERENCES server_profiles(id) ON DELETE CASCADE, checked_at TEXT NOT NULL, ssh_status INTEGER NOT NULL, panel_status INTEGER NOT NULL, message TEXT NOT NULL, certificate_expires_at TEXT NULL, panel_port INTEGER NULL, panel_access INTEGER NULL, panel_allowed_ips_text TEXT NULL); PRAGMA user_version=3;";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось инициализировать локальную базу профилей."); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static async Task WriteProfile(SqliteConnection connection, SqliteTransaction transaction, ServerProfile p, CancellationToken ct)
    {
        var protectedPath = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(p.PanelBasePath), null, DataProtectionScope.CurrentUser);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO server_profiles(id,name,role,region,node_index,host,ssh_port,user_name,identity_file,panel_domain,panel_port,panel_base_path,vpn_domain,vpn_port,control_server_id,updated_at,local_port,subdomain_mode,dns_zone,dns_address,panel_access,panel_open_mode,panel_allowed_ips_text)
            VALUES($id,$name,$role,$region,$node,$host,$ssh,$user,$identity,$panel,$panelPort,$base,$vpn,$vpnPort,$control,$updated,$local,$mode,$zone,$address,$panelAccess,$panelOpenMode,$panelAllowedIps)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,role=excluded.role,region=excluded.region,node_index=excluded.node_index,host=excluded.host,ssh_port=excluded.ssh_port,user_name=excluded.user_name,identity_file=excluded.identity_file,panel_domain=excluded.panel_domain,panel_port=excluded.panel_port,panel_base_path=excluded.panel_base_path,vpn_domain=excluded.vpn_domain,vpn_port=excluded.vpn_port,control_server_id=excluded.control_server_id,updated_at=excluded.updated_at,local_port=excluded.local_port,subdomain_mode=excluded.subdomain_mode,dns_zone=excluded.dns_zone,dns_address=excluded.dns_address,panel_access=excluded.panel_access,panel_open_mode=excluded.panel_open_mode,panel_allowed_ips_text=excluded.panel_allowed_ips_text;
            """;
        command.Parameters.AddWithValue("$id", p.Id.ToString("D")); command.Parameters.AddWithValue("$name", p.Name.Trim()); command.Parameters.AddWithValue("$role", (int)p.Role);
        command.Parameters.AddWithValue("$region", p.Region); command.Parameters.AddWithValue("$node", p.NodeIndex); command.Parameters.AddWithValue("$host", p.Host); command.Parameters.AddWithValue("$ssh", p.SshPort);
        command.Parameters.AddWithValue("$user", p.UserName); command.Parameters.AddWithValue("$identity", p.IdentityFile); command.Parameters.AddWithValue("$panel", p.PanelDomain); command.Parameters.AddWithValue("$panelPort", p.PanelPort);
        command.Parameters.Add("$base", SqliteType.Blob).Value = protectedPath; command.Parameters.AddWithValue("$vpn", p.VpnDomain); command.Parameters.AddWithValue("$vpnPort", p.VpnPort);
        command.Parameters.AddWithValue("$control", p.ControlServerId?.ToString("D") is { } control ? control : DBNull.Value);
        command.Parameters.AddWithValue("$updated", p.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$local", p.LocalPort is { } local ? local : DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)p.SubdomainMode); command.Parameters.AddWithValue("$zone", p.DnsZone); command.Parameters.AddWithValue("$address", p.DnsAddress);
        command.Parameters.AddWithValue("$panelAccess", (int)p.PanelAccess); command.Parameters.AddWithValue("$panelOpenMode", (int)p.PanelOpenMode); command.Parameters.AddWithValue("$panelAllowedIps", p.PanelAllowedIpsText);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ServerProfile ReadProfile(SqliteDataReader r)
    {
        var plain = ProtectedData.Unprotect((byte[])r[11], null, DataProtectionScope.CurrentUser);
        var roleValue = r.GetInt32(2);
        if (!Enum.IsDefined(typeof(ServerRole), roleValue)) throw new InvalidOperationException("В локальной базе указана неизвестная роль сервера.");
        var modeValue = r.GetInt32(17);
        if (!Enum.IsDefined(typeof(SubdomainMode), modeValue)) throw new InvalidOperationException("В локальной базе указан неизвестный режим поддоменов.");
        var panelAccessValue = r.GetInt32(20);
        if (!Enum.IsDefined(typeof(VpsPanelAccess), panelAccessValue)) throw new InvalidOperationException("В локальной базе указан неизвестный режим доступа к панели.");
        var panelOpenModeValue = r.GetInt32(21);
        if (!Enum.IsDefined(typeof(PanelOpenMode), panelOpenModeValue)) throw new InvalidOperationException("В локальной базе указан неизвестный режим открытия панели.");
        return new ServerProfile
        {
            Id = Guid.Parse(r.GetString(0)), Name = r.GetString(1), Role = (ServerRole)roleValue, Region = r.GetString(3), NodeIndex = r.GetInt32(4), Host = r.GetString(5), SshPort = r.GetInt32(6),
            UserName = r.GetString(7), IdentityFile = r.GetString(8), PanelDomain = r.GetString(9), PanelPort = r.GetInt32(10), PanelBasePath = System.Text.Encoding.UTF8.GetString(plain),
            VpnDomain = r.GetString(12), VpnPort = r.GetInt32(13), ControlServerId = r.IsDBNull(14) ? null : Guid.Parse(r.GetString(14)), UpdatedAt = DateTimeOffset.Parse(r.GetString(15), System.Globalization.CultureInfo.InvariantCulture), LocalPort = r.IsDBNull(16) ? null : r.GetInt32(16),
            SubdomainMode = (SubdomainMode)modeValue, DnsZone = r.GetString(18), DnsAddress = r.GetString(19),
            PanelAccess = (VpsPanelAccess)panelAccessValue, PanelOpenMode = (PanelOpenMode)panelOpenModeValue, PanelAllowedIpsText = r.GetString(22)
        };
    }

    private static async Task<List<ServerProfile>> ReadAllForInvariantCheck(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var result = new List<ServerProfile>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,name,role,region,node_index,host,ssh_port,user_name,identity_file,panel_domain,panel_port,panel_base_path,vpn_domain,vpn_port,control_server_id,updated_at,local_port,subdomain_mode,dns_zone,dns_address,panel_access,panel_open_mode,panel_allowed_ips_text FROM server_profiles;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(ReadProfile(reader));
        return result;
    }

    private static void ValidateSet(IReadOnlyCollection<ServerProfile> profiles)
    {
        var byId = profiles.ToDictionary(p => p.Id);
        var ssh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var panels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
        {
            var sshKey = $"{CanonicalHost(p.Host)}:{p.SshPort}";
            if (!ssh.Add(sshKey)) throw new InvalidOperationException($"Адрес SSH {p.Host}:{p.SshPort} уже назначен другому профилю.");
            if (!string.IsNullOrWhiteSpace(p.PanelDomain) && !panels.Add($"{p.PanelDomain.Trim().TrimEnd('.').ToLowerInvariant()}:{p.PanelPort}")) throw new InvalidOperationException($"Адрес панели {p.PanelDomain}:{p.PanelPort} уже назначен другому профилю.");
            if (p.ControlServerId is { } parent)
            {
                if (!byId.TryGetValue(parent, out var control) || control.Role != ServerRole.Control) throw new InvalidOperationException("Для связи укажите существующий профиль CONTROL.");
                if (p.Role != ServerRole.VpnNode) throw new InvalidOperationException("На профиль CONTROL могут ссылаться только профили VPN_NODE.");
                var visited = new HashSet<Guid> { p.Id };
                var cursor = control;
                while (true)
                {
                    if (!visited.Add(cursor.Id)) throw new InvalidOperationException("Связи CONTROL не могут образовывать цикл.");
                    if (cursor.ControlServerId is not { } next || !byId.TryGetValue(next, out var nextProfile)) break;
                    cursor = nextProfile;
                }
            }
        }
    }

    private static string CanonicalHost(string host) => System.Net.IPAddress.TryParse(host, out var ip) ? ip.ToString().ToLowerInvariant() : host.Trim().TrimEnd('.').ToLowerInvariant();
    private sealed record ExportDocument(int Version, ExportProfile[] Profiles);
    private sealed record ExportProfile(Guid Id, string Name, ServerRole Role, string Region, int NodeIndex, string Host, int SshPort, string UserName, string PanelDomain, int PanelPort, string VpnDomain, int VpnPort, Guid? ControlServerId, DateTimeOffset UpdatedAt, int? LocalPort, SubdomainMode? SubdomainMode = null, string? DnsZone = null, string? DnsAddress = null, VpsPanelAccess? PanelAccess = null, PanelOpenMode? PanelOpenMode = null, string? PanelAllowedIpsText = null)
    {
        public ServerProfile ToProfile(int formatVersion) => new() { Id = Id, Name = Name, Role = Role, Region = Region, NodeIndex = NodeIndex, Host = Host, SshPort = SshPort, UserName = UserName, PanelDomain = PanelDomain, PanelPort = PanelPort, PanelBasePath = "/", VpnDomain = VpnDomain, VpnPort = VpnPort, ControlServerId = ControlServerId, UpdatedAt = UpdatedAt, LocalPort = LocalPort, IdentityFile = "", SubdomainMode = formatVersion == 1 ? OpenViya.Ops.Core.SubdomainMode.Manual : SubdomainMode ?? OpenViya.Ops.Core.SubdomainMode.Manual, DnsZone = string.IsNullOrWhiteSpace(DnsZone) ? "openviya.online" : DnsZone, DnsAddress = DnsAddress ?? "", PanelAccess = formatVersion < 3 ? VpsPanelAccess.Tunnel : PanelAccess ?? VpsPanelAccess.Tunnel, PanelOpenMode = formatVersion < 3 ? OpenViya.Ops.Core.PanelOpenMode.Tunnel : PanelOpenMode ?? OpenViya.Ops.Core.PanelOpenMode.Tunnel, PanelAllowedIpsText = formatVersion < 3 ? "" : PanelAllowedIpsText ?? "" };
    }
}
