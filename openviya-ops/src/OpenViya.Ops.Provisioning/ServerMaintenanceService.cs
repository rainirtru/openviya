using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Provisioning;

/// <summary>Обслуживание управляемого VPS через явно закреплённый SSH host key.</summary>
public sealed class ServerMaintenanceService(AppPaths paths, IVpsRemoteSessionFactory sessions) : IVpsServerMaintenanceService
{
    private const string HelperResource = "OpenViya.Ops.Provisioning.maintenance-helper.py";
    private const string RemoteRoot = "/var/lib/openviya-desktop-maintenance";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 12 };
    private readonly AppPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly IVpsRemoteSessionFactory _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async Task<HostKeyCandidate> GetTrustedHostKeyAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.Id == Guid.Empty) throw new ArgumentException("Не указан идентификатор профиля сервера.", nameof(profile));
        if (!File.Exists(_paths.KnownHostsPath)) throw new InvalidOperationException("Для этого профиля ещё не принят SSH host key.");
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(_paths.KnownHostsPath, cancellationToken).ConfigureAwait(false); }
        catch { throw new InvalidOperationException("Не удалось прочитать принятый host key из файла known_hosts приложения."); }
        var alias = "openviya-" + profile.Id.ToString("N");
        var matches = lines.Where(line => !line.StartsWith('#') && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == alias).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0
            ? "Для этого профиля ещё не принят SSH host key."
            : "Для профиля найдено несколько записей host key. Разрешите конфликт в настройках SSH.");
        var fields = matches[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[1] != "ssh-ed25519" || !TryFingerprint(fields[2], out var fingerprint))
            throw new InvalidOperationException("Сохранённая запись SSH host key повреждена. Обслуживание остановлено до её исправления.");
        return new HostKeyCandidate(fields[1], fields[2], fingerprint);
    }

    public async Task<VpsMaintenanceSnapshot> InspectAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(profile, access, hostKey, new { action = "inspect" }, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(profile.Id, result);
    }

    public async Task<VpsMaintenancePlan> PlanAccessChangeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey,
        PanelOpenMode mode, string allowedIpsText, int panelPort, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ValidatePanelPort(panelPort);
        var allowed = PanelAllowedIps.Normalize(allowedIpsText);
        var current = await InspectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false);
        var requestedAccess = mode == PanelOpenMode.PublicHttps ? VpsPanelAccess.Both : VpsPanelAccess.Tunnel;
        var changes = new List<string>();
        if (current.PanelPort != panelPort) changes.Add($"Порт панели: {current.PanelPort} → {panelPort}");
        if (current.PanelAccess != requestedAccess) changes.Add($"Доступ к панели: {Describe(current.PanelAccess)} → {Describe(requestedAccess)}");
        if (!StringComparer.Ordinal.Equals(NormalizeAllowedIps(current.AllowedIpsText), allowed)) changes.Add($"Разрешённые источники панели: {DisplayIps(current.AllowedIpsText)} → {DisplayIps(allowed)}");
        if (!current.PanelFirewallRulesMatch) changes.Add("Восстановить правила UFW, соответствующие настройкам панели");
        return new VpsMaintenancePlan(profile.Id, current.PanelPort, current.PanelAccess, current.AllowedIpsText,
            panelPort, requestedAccess, allowed, changes, changes.Count > 0);
    }

    public async Task<VpsMaintenanceResult> ApplyAccessChangeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey,
        VpsMaintenancePlan plan, bool confirmHighRisk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ServerId != profile.Id) throw new ArgumentException("The plan belongs to a different server.", nameof(plan));
        ValidatePanelPort(plan.RequestedPanelPort);
        var allowed = NormalizeAllowedIps(plan.RequestedAllowedIpsText);
        var current = await InspectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false);
        if (current.PanelPort != plan.CurrentPanelPort || current.PanelAccess != plan.CurrentAccess ||
            !StringComparer.Ordinal.Equals(NormalizeAllowedIps(current.AllowedIpsText), NormalizeAllowedIps(plan.CurrentAllowedIpsText)))
            throw new InvalidOperationException("Настройки панели изменились после подготовки плана. Проверьте сервер и сформируйте новый план.");
        if (plan.RequiresConfirmation && !confirmHighRisk)
            throw new InvalidOperationException("Проверьте перечисленные изменения доступа к панели и явно подтвердите их перед применением.");
        if (!plan.RequiresConfirmation) return new VpsMaintenanceResult(true, "Настройки панели уже совпадают с запрошенными.", current);

        var operationId = Guid.NewGuid().ToString("N");
        var applied = await InvokeAsync(profile, access, hostKey, new
        {
            action = "access", operationId, panelPort = plan.RequestedPanelPort,
            panelAccess = plan.RequestedAccess == VpsPanelAccess.Both ? "both" : "tunnel", allowedIpsText = allowed,
            expectedPanelPort = plan.CurrentPanelPort,
            expectedPanelAccess = plan.CurrentAccess == VpsPanelAccess.Both ? "both" : "tunnel",
            expectedAllowedIpsText = NormalizeAllowedIps(plan.CurrentAllowedIpsText)
        }, cancellationToken, operationId).ConfigureAwait(false);
        var rollbackId = ReadString(applied, "operationId");
        var rollbackHelper = HelperPath(profile.Id, operationId);
        try
        {
            // Reconnect after the change before cancelling the five-minute remote rollback.
            var healthSession = await _sessions.ConnectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false);
            await using (healthSession.ConfigureAwait(false))
            {
                var health = await InvokeWithSessionAsync(healthSession, profile.Id, new { action = "inspect" }, cancellationToken).ConfigureAwait(false);
                var snapshot = ToSnapshot(profile.Id, health);
                if (!snapshot.PanelHealthy || !snapshot.FirewallActive || !snapshot.PanelFirewallRulesMatch) throw new InvalidOperationException("SSH-подключение восстановлено, но проверка панели или межсетевого экрана не пройдена; VPS восстановит старые настройки по таймеру.");
                var confirmed = await healthSession.ExecuteAsync($"sudo -n /usr/bin/python3 {ShellQuote(rollbackHelper)} confirm {ShellQuote(rollbackId)}", cancellationToken).ConfigureAwait(false);
                if (confirmed.ExitCode != 0) throw new InvalidOperationException("Не удалось подтвердить применение настроек на VPS.");
                return new VpsMaintenanceResult(true, "Доступ к панели изменён и подтверждён после нового SSH-подключения и проверки панели.", snapshot with { RollbackPending = false });
            }
        }
        catch
        {
            // A failed confirmation leaves the independent VPS rollback timer active.
            throw new InvalidOperationException("Новое SSH-подключение или проверка состояния не прошли. Таймер VPS восстановит прежние настройки через пять минут.");
        }
    }

    public async Task<VpsMaintenanceResult> CheckCertificateAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        var data = await InvokeAsync(profile, access, hostKey, new { action = "cert-check" }, cancellationToken).ConfigureAwait(false);
        var expiry = ReadNullableDate(data, "certificateExpiresAt");
        return new VpsMaintenanceResult(true, expiry is null ? "Сертификат панели не найден или не читается." : $"Срок сертификата панели истекает {expiry:O}.",
            await InspectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VpsMaintenanceResult> RenewCertificateAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey,
        bool confirmHighRisk, CancellationToken cancellationToken = default)
    {
        if (!confirmHighRisk) throw new InvalidOperationException("Подтвердите продление сертификата перед обращением к Let’s Encrypt и установкой нового сертификата.");
        var data = await InvokeAsync(profile, access, hostKey, new { action = "cert-renew" }, cancellationToken).ConfigureAwait(false);
        var expiry = ReadNullableDate(data, "certificateExpiresAt");
        return new VpsMaintenanceResult(expiry is not null, expiry is null ? "Продление завершено, но срок сертификата не удалось проверить." : $"Сертификат продлён и проверен до {expiry:O}.",
            await InspectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false));
    }

    public async Task<VpsMaintenanceResult> CreateBackupAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        var data = await InvokeAsync(profile, access, hostKey, new { action = "backup-create" }, cancellationToken).ConfigureAwait(false);
        var backups = ReadBackups(data);
        return new VpsMaintenanceResult(backups.Count > 0, backups.Count > 0 ? "На VPS создана согласованная резервная копия панели и управляемой конфигурации." : "Команда резервного копирования завершилась, но архив не удалось проверить.",
            await InspectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false), backups.FirstOrDefault()?.Name);
    }

    public async Task<byte[]> DownloadBackupAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, string backupName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupName) || !Regex.IsMatch(backupName, @"^backup-[0-9]{8}T[0-9]{6}-[0-9]+\.tar\.gz$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Некорректное имя резервной копии.", nameof(backupName));
        await using var session = await _sessions.ConnectAsync(profile, access, hostKey, cancellationToken).ConfigureAwait(false);
        var stagingId = Guid.NewGuid().ToString("N");
        await StageHelperAsync(session, profile.Id, cancellationToken, stagingId).ConfigureAwait(false);
        using var destination = new MemoryStream();
        long expected = -1;
        while (expected < 0 || destination.Length < expected)
        {
            var data = await InvokePreparedWithSessionAsync(session, profile.Id,
                new { action = "backup-read", name = backupName, offset = checked((int)destination.Length) },
                HelperPath(profile.Id, stagingId), cancellationToken).ConfigureAwait(false);
            var total = ReadLong(data, "totalBytes");
            if (ReadLong(data, "offset") != destination.Length)
                throw new InvalidOperationException("Нарушен порядок частей резервной копии.");
            if (total is < 0 or > 128 * 1024 * 1024) throw new InvalidOperationException("Резервная копия VPS превышает ограничение загрузки 128 МиБ.");
            if (expected < 0) expected = total;
            if (expected != total) throw new InvalidOperationException("Размер резервной копии изменился во время загрузки.");
            var encoded = ReadString(data, "dataBase64");
            byte[] chunk;
            try { chunk = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new InvalidOperationException("При загрузке резервной копии получены некорректные данные."); }
            if (chunk.Length > 60 * 1024 || destination.Length + chunk.Length > expected)
                throw new InvalidOperationException("Размер части резервной копии не соответствует ответу VPS.");
            if (chunk.Length == 0 && destination.Length < expected) throw new InvalidOperationException("Загрузка резервной копии завершилась раньше времени.");
            await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    public async Task<VpsMaintenanceResult> CheckUpdatesAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate hostKey, CancellationToken cancellationToken = default)
    {
        var data = await InvokeAsync(profile, access, hostKey, new { action = "update-check" }, cancellationToken).ConfigureAwait(false);
        var snapshot = ToSnapshot(profile.Id, data);
        return new VpsMaintenanceResult(true, snapshot.UpdateAvailable
            ? $"Установлена 3x-ui {snapshot.InstalledVersion}; последняя официальная версия — {snapshot.LatestVersion}."
            : $"Установлена последняя проверенная версия 3x-ui {snapshot.InstalledVersion}.", snapshot);
    }

    private async Task<JsonElement> InvokeAsync(ServerProfile profile, VpsSshAccess access, HostKeyCandidate key, object request, CancellationToken ct, string? stagingId = null)
    {
        ValidateProfile(profile);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(key);
        var session = await _sessions.ConnectAsync(profile, access, key, ct).ConfigureAwait(false);
        await using (session.ConfigureAwait(false)) return await InvokeWithSessionAsync(session, profile.Id, request, ct, stagingId).ConfigureAwait(false);
    }

    private async Task<JsonElement> InvokeWithSessionAsync(IVpsRemoteSession session, Guid serverId, object request, CancellationToken ct, string? stagingId = null)
    {
        var operationId = stagingId ?? Guid.NewGuid().ToString("N");
        await StageHelperAsync(session, serverId, ct, operationId).ConfigureAwait(false);
        return await InvokePreparedWithSessionAsync(session, serverId, request, HelperPath(serverId, operationId), ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> InvokePreparedWithSessionAsync(IVpsRemoteSession session, Guid serverId, object request, string helperPath, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("Запрос обслуживания превышает допустимый размер.");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var command = $"printf '%s' '{encoded}' | /usr/bin/base64 --decode | sudo -n /usr/bin/python3 {ShellQuote(helperPath)} run";
        var response = await session.ExecuteAsync(command, ct).ConfigureAwait(false);
        if (response.ExitCode != 0) throw new InvalidOperationException(ReadRemoteError(response.Output));
        try
        {
            using var document = JsonDocument.Parse(response.Output, new JsonDocumentOptions { MaxDepth = 12 });
            if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new InvalidOperationException("Помощник VPS вернул некорректный ответ.");
            return document.RootElement.GetProperty("result").Clone();
        }
        catch (JsonException) { throw new InvalidOperationException("Помощник VPS вернул некорректный ответ протокола."); }
    }

    private async Task StageHelperAsync(IVpsRemoteSession session, Guid serverId, CancellationToken ct, string? stagingId = null)
    {
        var operationId = stagingId ?? Guid.NewGuid().ToString("N");
        if (!Regex.IsMatch(operationId, @"^[0-9a-f]{32}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Некорректный идентификатор операции обслуживания.", nameof(stagingId));
        var directory = $"{RemoteRoot}/{operationId}";
        var tempDirectory = $"/tmp/openviya-{operationId}";
        var tempPath = tempDirectory + "/maintenance.py";
        var mkdir = await session.ExecuteAsync($"/bin/mkdir -m 700 -- {ShellQuote(tempDirectory)}", ct).ConfigureAwait(false);
        if (mkdir.ExitCode != 0) throw new InvalidOperationException("Не удалось создать временный каталог обслуживания на VPS.");
        var helperPath = HelperPath(serverId, operationId);
        var assembly = typeof(ServerMaintenanceService).Assembly;
        await using var stream = assembly.GetManifestResourceStream(HelperResource) ?? throw new InvalidOperationException("Embedded maintenance helper is missing.");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
        var bytes = NormalizeLf(memory.ToArray());
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await session.UploadAsync(tempPath, bytes, ct).ConfigureAwait(false);
        var stage = await session.ExecuteAsync($"sudo -n /usr/bin/install -d -o root -g root -m 700 {ShellQuote(directory)} && sudo -n /usr/bin/install -o root -g root -m 700 -- {ShellQuote(tempPath)} {ShellQuote(helperPath)} && /bin/rm -- {ShellQuote(tempPath)} && printf '%s  %s\\n' '{digest}' {ShellQuote(helperPath)} | sudo -n /usr/bin/sha256sum --check --status && /bin/rmdir -- {ShellQuote(tempDirectory)}", ct).ConfigureAwait(false);
        if (stage.ExitCode != 0) throw new InvalidOperationException("Не удалось проверить и установить вспомогательный файл обслуживания на VPS.");
    }

    private static string HelperPath(Guid serverId, string operationId) => $"{RemoteRoot}/{operationId}/maintenance.py";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static byte[] NormalizeLf(byte[] bytes) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
    private static void ValidateProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Host) || profile.SshPort is < 1 or > 65535)
            throw new ArgumentException("Требуется сохранённый профиль с корректным SSH-адресом.", nameof(profile));
    }
    private static void ValidatePanelPort(int port)
    {
        if (port is < 1 or > 65535 || port is 22 or 80 or 2096) throw new ArgumentOutOfRangeException(nameof(port), "Порт панели задан неверно либо зарезервирован для SSH, ACME HTTP или подписок.");
    }
    private static string NormalizeAllowedIps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw == "any") return "any";
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 100 || lines.Any(x => x.Length == 0 || x != x.Trim() || x.Contains('%')))
            throw new ArgumentException("Разрешённый список может содержать не более 100 корректных IP или CIDR.", nameof(raw));
        var normalized = new List<string>();
        foreach (var line in lines)
        {
            if (IPAddress.TryParse(line, out var ip)) normalized.Add(ip + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
            else if (System.Net.IPNetwork.TryParse(line, out var network) && network.ToString() == line) normalized.Add(line);
            else throw new ArgumentException("Разрешённый список содержит некорректный IP или CIDR.", nameof(raw));
        }
        return string.Join('\n', normalized.Distinct(StringComparer.Ordinal));
    }
    private static string Describe(VpsPanelAccess access) => access == VpsPanelAccess.Both ? "публичный HTTPS" : "только через SSH-туннель";
    private static string DisplayIps(string value) => string.IsNullOrEmpty(value) ? "нет" : value == "any" ? "любой источник" : value.Replace('\n', ',');

    private static VpsMaintenanceSnapshot ToSnapshot(Guid serverId, JsonElement data)
    {
        var accessText = ReadString(data, "panelAccess");
        return new VpsMaintenanceSnapshot(serverId, DateTimeOffset.UtcNow,
            ReadString(data, "installedVersion"), ReadString(data, "latestVersion"), ReadBool(data, "updateAvailable"),
            ReadInt(data, "panelPort"), accessText == "both" ? VpsPanelAccess.Both : VpsPanelAccess.Tunnel,
            ReadString(data, "allowedIpsText"), ReadNullableDate(data, "certificateExpiresAt"), ReadBool(data, "certificateManagedByLetsEncrypt"),
            ReadBool(data, "panelHealthy"), ReadBool(data, "firewallActive"), ReadBool(data, "panelFirewallRulesMatch"), ReadBool(data, "rollbackPending"), ReadBackups(data),
            "Состояние управляемого VPS проверено через закреплённый SSH host key.");
    }
    private static IReadOnlyList<VpsBackupInfo> ReadBackups(JsonElement data)
    {
        if (!data.TryGetProperty("backups", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var result = new List<VpsBackupInfo>();
        foreach (var item in items.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (!Regex.IsMatch(name, @"^backup-[0-9]{8}T[0-9]{6}-[0-9]+\.tar\.gz$", RegexOptions.CultureInvariant)) continue;
            result.Add(new VpsBackupInfo(name, ReadLong(item, "sizeBytes"), ReadNullableDate(item, "createdAt") ?? DateTimeOffset.MinValue));
        }
        return result;
    }
    private static string ReadRemoteError(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var remoteError = Regex.Replace(error.GetString() ?? "", @"[\r\n\p{C}]", " ").Trim();
                return TranslateRemoteError(remoteError);
            }
        }
        catch { }
        return "Обслуживание VPS не выполнено. Сырые данные SSH в приложении не сохранялись.";
    }
    private static string TranslateRemoteError(string message) => message switch
    {
        "server is not a completed OpenViya managed installation" => "Сервер не прошёл завершённую установку OpenViya VPS; обслуживание не запущено.",
        "certificate is not managed by Let’s Encrypt" => "Сертификат не управляется Let’s Encrypt.",
        "controlled update is unavailable until an official release checksum can be verified; use the 3x-ui panel after creating a backup" => "Автоматическое обновление отключено: официальный архив нельзя проверить по опубликованной контрольной сумме. Создайте резервную копию и обновите 3x-ui через панель.",
        "backup exceeds the 128 MiB download limit" => "Резервная копия превышает ограничение загрузки 128 МиБ.",
        "saved panel port disagrees with the live 3x-ui database; manual repair is required" => "Порт в конфигурации OpenViya не совпадает с настройкой 3x-ui. Сначала восстановите согласованность вручную.",
        "panel health check failed; old settings restored" => "Проверка панели не прошла. Старые настройки восстановлены.",
        _ => "Операция обслуживания на VPS не выполнена. Проверьте состояние сервера и повторите действие после устранения причины."
    };
    private static string ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int ReadInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static long ReadLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static bool ReadBool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static DateTimeOffset? ReadNullableDate(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
    private static bool TryFingerprint(string publicKey, out string fingerprint)
    {
        fingerprint = "";
        try
        {
            var blob = Convert.FromBase64String(publicKey);
            if (!StringComparer.Ordinal.Equals(Convert.ToBase64String(blob), publicKey) || blob.Length < 4 + 11 + 4 + 32) return false;
            var nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(0, 4));
            if (nameLength != 11 || !blob.AsSpan(4, 11).SequenceEqual("ssh-ed25519"u8)) return false;
            var keyLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(15, 4));
            if (keyLength != 32 || blob.Length != 19 + 32) return false;
            fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
            return true;
        }
        catch { return false; }
    }
}
