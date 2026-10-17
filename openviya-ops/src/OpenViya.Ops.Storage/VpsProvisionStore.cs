using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using Microsoft.Data.Sqlite;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Storage;

/// <summary>Persists nonsecret VPS provisioning progress and DPAPI-protected panel credentials.</summary>
public sealed class DpapiVpsProvisionStore : IVpsProvisionStore
{
    private const int MaxRunMessageLength = 1000;
    private const int MaxCredentialPayloadBytes = 32 * 1024;
    private const int MaxProtectedCredentialBytes = MaxCredentialPayloadBytes + 4096;
    private readonly string _connectionString;
    private readonly string _credentialDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public DpapiVpsProvisionStore(AppPaths paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths), "Пути приложения не заданы.");
        if (string.IsNullOrWhiteSpace(paths.Root)) throw new ArgumentException("Укажите каталог данных приложения.", nameof(paths));
        Directory.CreateDirectory(paths.Root);
        _credentialDirectory = Path.Combine(paths.Root, "vps-provision-credentials");
        Directory.CreateDirectory(_credentialDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS vps_provision_runs (
                  run_id TEXT PRIMARY KEY NOT NULL,
                  server_id TEXT NOT NULL,
                  stage INTEGER NOT NULL,
                  message TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  progress_json TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_vps_provision_runs_latest ON vps_provision_runs(server_id, updated_at DESC);
                CREATE TABLE IF NOT EXISTS vps_provision_events (
                  event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                  run_id TEXT NOT NULL, server_id TEXT NOT NULL, stage INTEGER NOT NULL,
                  message TEXT NOT NULL, updated_at TEXT NOT NULL,
                  progress_json TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_vps_provision_events_run ON vps_provision_events(run_id,event_id);
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "vps_provision_runs", "progress_json");
            EnsureColumn(connection, "vps_provision_events", "progress_json");
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось инициализировать локальное хранилище подготовки VPS."); }
    }

    public async Task SaveRunAsync(VpsProvisionRun run, CancellationToken cancellationToken = default)
    {
        ValidateRun(run);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var progressJson = SerializeProgress(run);
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT server_id FROM vps_provision_runs WHERE run_id=$run;";
                check.Parameters.AddWithValue("$run", run.RunId.ToString("D"));
                var existingServerId = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (existingServerId is not null && !string.Equals(existingServerId, run.ServerId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Идентификатор запуска уже связан с другим сервером.");
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO vps_provision_runs(run_id,server_id,stage,message,updated_at,progress_json) VALUES($run,$server,$stage,$message,$updated,$progress) ON CONFLICT(run_id) DO UPDATE SET stage=excluded.stage,message=excluded.message,updated_at=excluded.updated_at,progress_json=excluded.progress_json;";
            command.Parameters.AddWithValue("$run", run.RunId.ToString("D"));
            command.Parameters.AddWithValue("$server", run.ServerId.ToString("D"));
            command.Parameters.AddWithValue("$stage", (int)run.Stage);
            command.Parameters.AddWithValue("$message", run.Message);
            command.Parameters.AddWithValue("$updated", run.UpdatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$progress", progressJson is null ? DBNull.Value : progressJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = """
                INSERT INTO vps_provision_events(run_id,server_id,stage,message,updated_at,progress_json)
                SELECT $run,$server,$stage,$message,$updated,$progress WHERE NOT EXISTS (
                  SELECT 1 FROM vps_provision_events WHERE event_id=(
                    SELECT MAX(event_id) FROM vps_provision_events WHERE run_id=$run
                  ) AND stage=$stage AND message=$message
                );
                DELETE FROM vps_provision_events WHERE run_id=$run AND event_id NOT IN (
                  SELECT event_id FROM vps_provision_events WHERE run_id=$run ORDER BY event_id DESC LIMIT 200
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось сохранить состояние подготовки VPS."); }
    }

    public async Task<VpsProvisionRun?> LoadLatestRunAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(serverId));
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_id,server_id,stage,message,updated_at,progress_json FROM vps_provision_runs WHERE server_id=$server ORDER BY updated_at DESC,run_id DESC LIMIT 1;";
            command.Parameters.AddWithValue("$server", serverId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            var stageValue = reader.GetInt32(2);
            if (!Enum.IsDefined(typeof(VpsProvisionStage), stageValue)) throw new InvalidOperationException("В локальном хранилище указана неизвестная стадия подготовки VPS.");
            return RestoreProgress(new VpsProvisionRun(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (VpsProvisionStage)stageValue, reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)), reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось прочитать состояние подготовки VPS."); }
    }

    public async Task<IReadOnlyList<VpsProvisionRun>> LoadRunHistoryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Не указан идентификатор запуска.", nameof(runId));
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_id,server_id,stage,message,updated_at,progress_json FROM vps_provision_events WHERE run_id=$run ORDER BY event_id LIMIT 200;";
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<VpsProvisionRun>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = RestoreProgress(new VpsProvisionRun(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    (VpsProvisionStage)reader.GetInt32(2), reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)), reader.IsDBNull(5) ? null : reader.GetString(5));
                ValidateRun(item);
                result.Add(item);
            }
            return result;
        }
        catch (SqliteException) { throw new InvalidOperationException("Не удалось прочитать историю настройки VPS."); }
    }

    public async Task SaveCredentialsAsync(VpsPanelCredentials credentials, CancellationToken cancellationToken = default)
    {
        ValidateCredentials(credentials);
        var payload = new CredentialPayload(credentials.ServerId, credentials.UserName, credentials.Password, credentials.PanelPath, credentials.PanelDomain, credentials.PanelPort, credentials.RetrievedAt);
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (plain.Length > MaxCredentialPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new ArgumentException("Данные учётной записи панели превышают допустимый размер.", nameof(credentials));
        }
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось защитить учётные данные панели для текущего пользователя Windows."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (protectedBytes.Length > MaxProtectedCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            throw new ArgumentException("Зашифрованные данные учётной записи превышают допустимый размер.", nameof(credentials));
        }

        var destination = CredentialPath(credentials.ServerId);
        var temp = Path.Combine(_credentialDirectory, $".{credentials.ServerId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, destination, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public async Task<VpsPanelCredentials?> LoadCredentialsAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(serverId));
        var path = CredentialPath(serverId);
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxProtectedCredentialBytes) throw new InvalidOperationException("Зашифрованный файл учётных данных повреждён или имеет недопустимый размер.");
            using var bounded = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (bounded.Length + read > MaxProtectedCredentialBytes) throw new InvalidOperationException("Зашифрованный файл учётных данных превышает допустимый размер.");
                await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var protectedBytes = bounded.ToArray();
            byte[] plain;
            try { plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { CryptographicOperations.ZeroMemory(protectedBytes); throw new InvalidOperationException("Не удалось расшифровать учётные данные панели для текущего пользователя Windows."); }
            CryptographicOperations.ZeroMemory(protectedBytes);
            try
            {
                if (plain.Length is <= 0 or > MaxCredentialPayloadBytes) throw new InvalidOperationException("Расшифрованные данные учётной записи имеют недопустимый размер.");
                var payload = JsonSerializer.Deserialize<CredentialPayload>(plain, JsonOptions) ?? throw new InvalidOperationException("Зашифрованный файл не содержит данных учётной записи.");
                var credentials = payload.ToCredentials();
                if (credentials.ServerId != serverId) throw new InvalidOperationException("Учётные данные панели относятся к другому серверу.");
                ValidateCredentials(credentials);
                return credentials;
            }
            catch (JsonException) { throw new InvalidOperationException("Зашифрованный файл учётных данных повреждён."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (IOException) { throw new InvalidOperationException("Не удалось прочитать локальные учётные данные панели."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Нет прав для чтения локальных учётных данных панели."); }
    }

    private static void ValidateRun(VpsProvisionRun run)
    {
        if (run is null) throw new ArgumentNullException(nameof(run), "Состояние подготовки VPS не задано.");
        if (run.RunId == Guid.Empty || run.ServerId == Guid.Empty) throw new ArgumentException("Не заданы идентификаторы запуска или сервера.", nameof(run));
        if (!Enum.IsDefined(run.Stage)) throw new ArgumentException("Указана неизвестная стадия подготовки VPS.", nameof(run));
        if (run.UpdatedAt == default) throw new ArgumentException("Не указано время состояния подготовки VPS.", nameof(run));
        if (run.Message is null || run.Message.Length > MaxRunMessageLength || run.Message.Any(char.IsControl)) throw new ArgumentException("Сообщение состояния пусто, слишком длинное или содержит управляющие символы.", nameof(run));
        if (run.StepId is null || run.StepId.Length > 64 || run.StepId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Идентификатор шага некорректен.", nameof(run));
        if (!Enum.IsDefined(run.State)) throw new ArgumentException("Неизвестно состояние наблюдения за настройкой VPS.", nameof(run));
        if (run.ActiveStage.HasValue && !Enum.IsDefined(run.ActiveStage.Value)) throw new ArgumentException("Указана неизвестная активная стадия настройки VPS.", nameof(run));
        if (run.StepStartedAt == DateTimeOffset.MinValue || run.LastSuccessfulVpsContactAt == DateTimeOffset.MinValue || run.NextPollAt == DateTimeOffset.MinValue) throw new ArgumentException("Временная отметка шага некорректна.", nameof(run));
        if (run.DnsItems is null || run.DnsItems.Count > 64) throw new ArgumentException("Список проверок DNS превышает допустимый размер.", nameof(run));
        foreach (var item in run.DnsItems)
        {
            if (item is null || !ProfileValidator.IsValidDomain(item.Name) || item.RecordType is not ("A" or "AAAA") ||
                !IPAddress.TryParse(item.ExpectedAddress, out var address) || address.AddressFamily != (item.RecordType == "A" ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6) ||
                !Enum.IsDefined(item.Status)) throw new ArgumentException("Результат проверки DNS некорректен.", nameof(run));
        }
    }

    private static void ValidateCredentials(VpsPanelCredentials credentials)
    {
        if (credentials is null) throw new ArgumentNullException(nameof(credentials), "Учётные данные панели не заданы.");
        if (credentials.ServerId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.UserName) || credentials.UserName.Length > 256 || credentials.UserName.Any(char.IsControl)) throw new ArgumentException("Имя пользователя панели пусто, слишком длинное или содержит управляющие символы.", nameof(credentials));
        if (string.IsNullOrEmpty(credentials.Password) || credentials.Password.Length > 4096 || credentials.Password.Any(char.IsControl)) throw new ArgumentException("Пароль панели пуст, слишком длинный или содержит управляющие символы.", nameof(credentials));
        if (!ProfileValidator.IsValidBasePath(credentials.PanelPath)) throw new ArgumentException("Укажите корректный базовый путь панели.", nameof(credentials));
        if (!ProfileValidator.IsValidDomain(credentials.PanelDomain)) throw new ArgumentException("Укажите корректный домен панели.", nameof(credentials));
        if (credentials.PanelPort is < 1 or > 65535) throw new ArgumentException("Порт панели должен быть от 1 до 65535.", nameof(credentials));
        if (credentials.RetrievedAt == default) throw new ArgumentException("Не указано время получения учётных данных.", nameof(credentials));
    }

    private string CredentialPath(Guid serverId) => Path.Combine(_credentialDirectory, $"{serverId:N}.dpapi");

    private static void EnsureColumn(SqliteConnection connection, string table, string column)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT;";
        alter.ExecuteNonQuery();
    }

    private static string? SerializeProgress(VpsProvisionRun run)
    {
        var details = new RunProgressPayload(run.StepId, run.ActiveStage, run.StepStartedAt, run.LastSuccessfulVpsContactAt,
            run.NextPollAt, run.State, run.DnsItems);
        var json = JsonSerializer.Serialize(details, JsonOptions);
        if (json.Length > 16 * 1024) throw new ArgumentException("Подробности настройки превышают допустимый размер.", nameof(run));
        return json;
    }

    private static VpsProvisionRun RestoreProgress(VpsProvisionRun run, string? json)
    {
        if (string.IsNullOrEmpty(json)) return run; // legacy schema/run
        if (json.Length > 16 * 1024) throw new InvalidOperationException("Подробности настройки в локальном хранилище повреждены.");
        try
        {
            var payload = JsonSerializer.Deserialize<RunProgressPayload>(json, JsonOptions) ?? throw new JsonException();
            var restored = run with
            {
                StepId = payload.StepId ?? "", StepStartedAt = payload.StepStartedAt,
                ActiveStage = payload.ActiveStage,
                LastSuccessfulVpsContactAt = payload.LastSuccessfulVpsContactAt, NextPollAt = payload.NextPollAt,
                State = payload.State, DnsItems = payload.DnsItems ?? []
            };
            ValidateRun(restored);
            return restored;
        }
        catch (JsonException) { throw new InvalidOperationException("Подробности настройки в локальном хранилище повреждены."); }
    }

    private sealed record RunProgressPayload(string? StepId, VpsProvisionStage? ActiveStage, DateTimeOffset? StepStartedAt,
        DateTimeOffset? LastSuccessfulVpsContactAt, DateTimeOffset? NextPollAt,
        VpsProvisionRunState State, IReadOnlyList<VpsProvisionDnsItem>? DnsItems);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed record CredentialPayload(Guid ServerId, string UserName, string Password, string PanelPath, string PanelDomain, int PanelPort, DateTimeOffset RetrievedAt)
    {
        public VpsPanelCredentials ToCredentials() => new() { ServerId = ServerId, UserName = UserName, Password = Password, PanelPath = PanelPath, PanelDomain = PanelDomain, PanelPort = PanelPort, RetrievedAt = RetrievedAt };
    }
}
