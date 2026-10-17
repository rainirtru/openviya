using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Storage;

/// <summary>Stores the Timeweb API token in a DPAPI CurrentUser-protected file.</summary>
public sealed class DpapiDnsTokenStore : IDnsTokenStore
{
    private const int MaxTokenBytes = 16 * 1024;
    private const int MaxCipherBytes = MaxTokenBytes + 4096;
    private readonly string _tokenPath;

    public DpapiDnsTokenStore(AppPaths paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths), "Пути приложения не заданы.");
        if (string.IsNullOrWhiteSpace(paths.Root)) throw new ArgumentException("Укажите каталог данных приложения.", nameof(paths));
        Directory.CreateDirectory(paths.Root);
        _tokenPath = Path.Combine(paths.Root, "timeweb-token.dpapi");
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_tokenPath)) return null;
            await using var stream = new FileStream(_tokenPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxCipherBytes) throw new InvalidOperationException("Файл токена Timeweb повреждён или имеет недопустимый размер.");
            using var bounded = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (bounded.Length + read > MaxCipherBytes) throw new InvalidOperationException("Файл токена Timeweb повреждён или имеет недопустимый размер.");
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var protectedBytes = bounded.ToArray();
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            try
            {
                if (plain.Length is <= 0 or > MaxTokenBytes) throw new InvalidOperationException("Сохранённый токен Timeweb пуст или имеет недопустимый размер.");
                var token = new UTF8Encoding(false, true).GetString(plain);
                ValidateToken(token);
                return token;
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось расшифровать токен Timeweb для текущего пользователя Windows."); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("Сохранённый токен Timeweb имеет неверную кодировку."); }
    }

    public async Task SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var plain = Encoding.UTF8.GetBytes(token);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось защитить токен Timeweb для текущего пользователя Windows."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (protectedBytes.Length > MaxCipherBytes) throw new InvalidOperationException("Зашифрованный токен Timeweb превышает допустимый размер.");
        var temp = Path.Combine(Path.GetDirectoryName(_tokenPath)!, $".{Path.GetFileName(_tokenPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, _tokenPath, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); }
        catch (IOException) { throw new InvalidOperationException("Не удалось удалить локально сохранённый токен Timeweb."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Нет прав для удаления локально сохранённого токена Timeweb."); }
        return Task.CompletedTask;
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("Токен Timeweb не может быть пустым.", nameof(token));
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)) throw new ArgumentException("Токен Timeweb не должен содержать пробелы или управляющие символы.", nameof(token));
        if (Encoding.UTF8.GetByteCount(token) > MaxTokenBytes) throw new ArgumentException("Токен Timeweb превышает ограничение 16 КиБ.", nameof(token));
    }
}

/// <summary>Tracks DNS record IDs managed for profiles, independently of profile rows.</summary>
public sealed class SqliteDnsRecordStore : IDnsRecordStore
{
    private readonly string _connectionString;

    public SqliteDnsRecordStore(AppPaths paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths), "Пути приложения не заданы.");
        if (string.IsNullOrWhiteSpace(paths.Root)) throw new ArgumentException("Укажите каталог данных приложения.", nameof(paths));
        Directory.CreateDirectory(paths.Root);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS dns_managed_records (
                  server_id TEXT NOT NULL,
                  fqdn TEXT NOT NULL COLLATE NOCASE,
                  record_id INTEGER NOT NULL,
                  address TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  PRIMARY KEY(server_id, record_id)
                );
                CREATE INDEX IF NOT EXISTS ix_dns_records_server_fqdn ON dns_managed_records(server_id, fqdn);
                """;
            command.ExecuteNonQuery();
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось инициализировать локальный реестр DNS-записей."); }
    }

    public async Task SaveAsync(DnsManagedRecord record, CancellationToken cancellationToken = default)
    {
        if (record is null) throw new ArgumentNullException(nameof(record), "Сведения о DNS-записи не заданы.");
        if (record.ServerId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера для DNS-записи.", nameof(record));
        if (record.RecordId <= 0) throw new ArgumentException("Идентификатор записи DNS должен быть положительным.", nameof(record));
        if (!ProfileValidator.IsValidDomain(record.Fqdn)) throw new ArgumentException("Укажите корректное полное DNS-имя.", nameof(record));
        if (!ProfileValidator.IsPublicIpLiteral(record.Address)) throw new ArgumentException("DNS-запись должна содержать публичный IP-адрес.", nameof(record));
        if (record.UpdatedAt == default) throw new ArgumentException("Не указано время обновления DNS-записи.", nameof(record));
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO dns_managed_records(server_id,fqdn,record_id,address,updated_at) VALUES($server,$fqdn,$record,$address,$updated) ON CONFLICT(server_id,record_id) DO UPDATE SET fqdn=excluded.fqdn,address=excluded.address,updated_at=excluded.updated_at;";
            command.Parameters.AddWithValue("$server", record.ServerId.ToString("D"));
            command.Parameters.AddWithValue("$fqdn", record.Fqdn.Trim().TrimEnd('.').ToLowerInvariant());
            command.Parameters.AddWithValue("$record", record.RecordId);
            command.Parameters.AddWithValue("$address", record.Address);
            command.Parameters.AddWithValue("$updated", record.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось сохранить сведения об управляемой DNS-записи."); }
    }

    public async Task<IReadOnlyList<DnsManagedRecord>> LoadAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(serverId));
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT server_id,fqdn,record_id,address,updated_at FROM dns_managed_records WHERE server_id=$server ORDER BY fqdn COLLATE NOCASE,record_id;";
            command.Parameters.AddWithValue("$server", serverId.ToString("D"));
            var records = new List<DnsManagedRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(new DnsManagedRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)));
            return records;
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось прочитать сведения об управляемых DNS-записях."); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
