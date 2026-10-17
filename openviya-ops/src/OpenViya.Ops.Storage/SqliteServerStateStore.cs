using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Storage;

/// <summary>Persists bounded, non-secret observations in the local profile database.</summary>
public sealed class SqliteServerStateStore : IServerStateStore
{
    private const int MaxMessageLength = 1000;
    private readonly string _connectionString;

    public SqliteServerStateStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(paths.Root)) throw new ArgumentException("Укажите каталог данных приложения.", nameof(paths));
        // Profile store owns the database migrations and creates the shared database file.
        _ = new SqliteProfileStore(paths);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    public async Task<ServerObservedState?> LoadAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(serverId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checked_at,ssh_status,panel_status,message,certificate_expires_at,panel_port,panel_access,panel_allowed_ips_text FROM server_observed_state WHERE server_id=$id;";
        command.Parameters.AddWithValue("$id", serverId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        var sshStatus = ReadEnum<ProbeStatus>(reader.GetInt32(1), "SSH status");
        var panelStatus = ReadEnum<ProbeStatus>(reader.GetInt32(2), "panel status");
        VpsPanelAccess? panelAccess = reader.IsDBNull(6) ? null : ReadEnum<VpsPanelAccess>(reader.GetInt32(6), "panel access");
        var allowedIps = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (allowedIps is not null) allowedIps = NormalizeObservedIps(allowedIps);
        return new ServerObservedState(
            serverId,
            ParseDate(reader.GetString(0)),
            sshStatus,
            panelStatus,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            panelAccess,
            allowedIps);
    }

    public async Task SaveAsync(ServerObservedState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        var message = SanitizeMessage(state.Message);
        var allowedIps = state.PanelAllowedIpsText is null ? null : NormalizeObservedIps(state.PanelAllowedIpsText);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_observed_state(server_id,checked_at,ssh_status,panel_status,message,certificate_expires_at,panel_port,panel_access,panel_allowed_ips_text)
            VALUES($id,$checked,$ssh,$panel,$message,$expires,$port,$access,$ips)
            ON CONFLICT(server_id) DO UPDATE SET checked_at=excluded.checked_at,ssh_status=excluded.ssh_status,panel_status=excluded.panel_status,message=excluded.message,certificate_expires_at=excluded.certificate_expires_at,panel_port=excluded.panel_port,panel_access=excluded.panel_access,panel_allowed_ips_text=excluded.panel_allowed_ips_text;
            """;
        command.Parameters.AddWithValue("$id", state.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$checked", FormatDate(state.CheckedAt));
        command.Parameters.AddWithValue("$ssh", (int)state.SshStatus);
        command.Parameters.AddWithValue("$panel", (int)state.PanelStatus);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$expires", state.CertificateExpiresAt is { } expires ? FormatDate(expires) : DBNull.Value);
        command.Parameters.AddWithValue("$port", state.PanelPort is { } port ? port : DBNull.Value);
        command.Parameters.AddWithValue("$access", state.PanelAccess is { } access ? (int)access : DBNull.Value);
        command.Parameters.AddWithValue("$ips", allowedIps is null ? DBNull.Value : allowedIps);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void Validate(ServerObservedState state)
    {
        if (state.ServerId == Guid.Empty) throw new ArgumentException("Не указан идентификатор сервера.", nameof(state));
        if (state.CheckedAt == default) throw new ArgumentException("Не указано время проверки.", nameof(state));
        if (!Enum.IsDefined(state.SshStatus) || !Enum.IsDefined(state.PanelStatus)) throw new ArgumentException("Указан неизвестный статус проверки.", nameof(state));
        if (state.Message is null || state.Message.Length > MaxMessageLength || state.Message.Any(char.IsControl))
            throw new ArgumentException("Сообщение состояния слишком длинное или содержит управляющие символы.", nameof(state));
        if (state.CertificateExpiresAt == DateTimeOffset.MinValue) throw new ArgumentException("Указана некорректная дата сертификата.", nameof(state));
        if (state.PanelPort is < 1 or > 65535) throw new ArgumentException("Порт панели должен быть от 1 до 65535.", nameof(state));
        if (state.PanelAccess is { } access && !Enum.IsDefined(access)) throw new ArgumentException("Указан неизвестный режим доступа к панели.", nameof(state));
        if (state.PanelAllowedIpsText is { Length: > PanelAllowedIps.MaximumInputLength }) throw new ArgumentException("Список разрешённых IP слишком длинный.", nameof(state));
        if (state.PanelAllowedIpsText is not null) _ = NormalizeObservedIps(state.PanelAllowedIpsText);
    }

    private static string SanitizeMessage(string message) => message.Trim();

    private static string NormalizeObservedIps(string text)
    {
        _ = PanelAllowedIps.Normalize(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static T ReadEnum<T>(int value, string label) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) : throw new InvalidOperationException($"В локальной базе указан неизвестный статус {label}.");

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
