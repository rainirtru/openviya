using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Desktop;

/// <summary>Owns the maintenance workflow and local persistence; UI code only gathers input and confirms risk.</summary>
internal sealed class ServerMaintenanceViewModel : INotifyPropertyChanged
{
    private ServerProfile _profile;
    private readonly IVpsServerMaintenanceService _service;
    private readonly IProfileStore _profiles;
    private readonly IServerStateStore _states;
    private readonly Func<Guid, Task> _profileUpdated;
    private HostKeyCandidate? _hostKey;
    private VpsMaintenanceSnapshot? _snapshot;
    private VpsMaintenancePlan? _plan;
    private bool _isBusy;
    private string _status = "Проверка ещё не запускалась. Подключение выполняется только после явного действия.";

    public ServerMaintenanceViewModel(
        ServerProfile profile,
        IVpsServerMaintenanceService service,
        IProfileStore profiles,
        IServerStateStore states,
        Func<Guid, Task>? profileUpdated = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _profileUpdated = profileUpdated ?? (_ => Task.CompletedTask);
        PanelPortText = profile.PanelPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OpeningMode = profile.PanelOpenMode;
        AllowedIpsText = profile.PanelAllowedIpsText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public VpsMaintenanceSnapshot? Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public VpsMaintenancePlan? Plan { get => _plan; private set => Set(ref _plan, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string PanelPortText { get; set; }
    public PanelOpenMode OpeningMode { get; set; }
    public string AllowedIpsText { get; set; }
    public VpsBackupInfo? SelectedBackup { get; set; }
    public string HostFingerprint => _hostKey?.Fingerprint ?? "Fingerprint ещё не прочитан";

    internal void SetPreviewSnapshot(VpsMaintenanceSnapshot snapshot)
    {
        Snapshot = snapshot;
        FillDraftFromSnapshot(snapshot);
        Status = "Предварительный просмотр · удалённые действия отключены.";
    }

    public void SetDraft(string portText, PanelOpenMode mode, string allowedIpsText)
    {
        PanelPortText = portText;
        OpeningMode = mode;
        AllowedIpsText = allowedIpsText;
        Plan = null;
        OnPropertyChanged(nameof(Plan));
    }

    public async Task InspectAsync(VpsSshAccess access, CancellationToken ct = default)
    {
        await RunAsync("Подключаемся и проверяем текущее состояние VPS…", async token =>
        {
            var key = await GetHostKeyAsync(token).ConfigureAwait(true);
            Snapshot = await _service.InspectAsync(_profile, access, key, token).ConfigureAwait(true);
            FillDraftFromSnapshot(Snapshot);
            Status = Snapshot.Message;
        }, ct).ConfigureAwait(true);
    }

    public async Task PlanAccessChangeAsync(VpsSshAccess access, int panelPort, PanelOpenMode mode, string allowedIpsText, CancellationToken ct = default)
    {
        await RunAsync("Сверяем запрошенные настройки с VPS…", async token =>
        {
            var key = await GetHostKeyAsync(token).ConfigureAwait(true);
            Plan = await _service.PlanAccessChangeAsync(_profile, access, key, mode, allowedIpsText, panelPort, token).ConfigureAwait(true);
            Snapshot = await _service.InspectAsync(_profile, access, key, token).ConfigureAwait(true);
            Status = Plan.Changes.Count == 0 ? "Изменения не нужны. VPS уже соответствует запрошенным настройкам." : "План подготовлен. Проверьте перечисленные изменения перед применением.";
        }, ct).ConfigureAwait(true);
    }

    public async Task ApplyAccessChangeAsync(
        VpsSshAccess access,
        bool confirmChanges,
        bool confirmTunnelStop,
        Func<Guid, bool> hasActiveTunnel,
        Func<Guid, Task> stopTunnel,
        CancellationToken ct = default)
    {
        var plan = Plan ?? throw new InvalidOperationException("Сначала подготовьте план изменений.");
        if (plan.Changes.Count == 0)
        {
            Status = "Изменения не нужны. VPS уже соответствует запрошенным настройкам.";
            return;
        }

        await RunAsync("Применяем настройки панели с контролем восстановления…", async token =>
        {
            if (plan.CurrentPanelPort != plan.RequestedPanelPort && hasActiveTunnel(_profile.Id))
            {
                if (!confirmTunnelStop) throw new InvalidOperationException("Сначала остановите активный SSH-туннель, чтобы сменить порт панели.");
                await stopTunnel(_profile.Id).ConfigureAwait(true);
            }

            var key = await GetHostKeyAsync(token).ConfigureAwait(true);
            var result = await _service.ApplyAccessChangeAsync(_profile, access, key, plan, confirmChanges, token).ConfigureAwait(true);
            if (!result.Succeeded) throw new InvalidOperationException(result.Message);
            if (result.Snapshot is { } snapshot) Snapshot = snapshot;

            var updated = _profile with
            {
                PanelPort = plan.RequestedPanelPort,
                PanelAccess = plan.RequestedAccess,
                PanelOpenMode = plan.RequestedAccess == VpsPanelAccess.Both ? PanelOpenMode.PublicHttps : PanelOpenMode.Tunnel,
                PanelAllowedIpsText = plan.RequestedAllowedIpsText == "any" ? "" : AllowedIpsText.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            try { await _profiles.SaveAsync(updated, token).ConfigureAwait(true); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                Status = "VPS подтвердил настройки, но локальный профиль не сохранился. Повторно проверьте VPS и сохраните профиль вручную.";
                return;
            }
            _profile = updated;
            var stateSaved = true;
            try
            {
                var prior = await _states.LoadAsync(updated.Id, token).ConfigureAwait(true);
                await _states.SaveAsync(new ServerObservedState(updated.Id, DateTimeOffset.UtcNow,
                    ProbeStatus.Success,
                    Snapshot?.PanelHealthy == true ? ProbeStatus.Success : ProbeStatus.Warning,
                    "Состояние панели сохранено после применения настроек обслуживания.",
                    Snapshot?.CertificateExpiresAt ?? prior?.CertificateExpiresAt,
                    Snapshot?.PanelPort,
                    Snapshot?.PanelAccess,
                    ToPersistableObservedIps(Snapshot?.AllowedIpsText)), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { stateSaved = false; }
            try { await _profileUpdated(updated.Id).ConfigureAwait(true); }
            catch { stateSaved = false; }
            Plan = null;
            FillDraftFromSnapshot(Snapshot);
            Status = result.Message + (stateSaved
                ? " Профиль и последнее наблюдение сохранены локально."
                : " Профиль сохранён, но локальная карточка или снимок наблюдения не обновились; повторно выберите профиль.");
        }, ct).ConfigureAwait(true);
    }

    public Task CheckCertificateAsync(VpsSshAccess access, CancellationToken ct = default) => RunMaintenanceAsync("Проверяем сертификат панели…", access,
        (key, token) => _service.CheckCertificateAsync(_profile, access, key, token), ct);

    public Task RenewCertificateAsync(VpsSshAccess access, CancellationToken ct = default) => RunMaintenanceAsync("Обращаемся к Let’s Encrypt и проверяем установленный сертификат…", access,
        (key, token) => _service.RenewCertificateAsync(_profile, access, key, true, token), ct);

    public Task CreateBackupAsync(VpsSshAccess access, CancellationToken ct = default) => RunMaintenanceAsync("Создаём и проверяем резервную копию…", access,
        (key, token) => _service.CreateBackupAsync(_profile, access, key, token), ct);

    public async Task<byte[]> DownloadBackupAsync(VpsSshAccess access, string backupName, CancellationToken ct = default)
    {
        byte[]? result = null;
        await RunAsync("Загружаем резервную копию…", async token =>
        {
            var key = await GetHostKeyAsync(token).ConfigureAwait(true);
            result = await _service.DownloadBackupAsync(_profile, access, key, backupName, token).ConfigureAwait(true);
            Status = $"Резервная копия загружена в память ({result.Length:N0} байт). Выберите место сохранения файла.";
        }, ct).ConfigureAwait(true);
        return result ?? [];
    }

    public Task CheckUpdatesAsync(VpsSshAccess access, CancellationToken ct = default) => RunMaintenanceAsync("Проверяем актуальную официальную версию 3x-ui…", access,
        (key, token) => _service.CheckUpdatesAsync(_profile, access, key, token), ct);

    private async Task RunMaintenanceAsync(string pending, VpsSshAccess access,
        Func<HostKeyCandidate, CancellationToken, Task<VpsMaintenanceResult>> action, CancellationToken ct)
    {
        await RunAsync(pending, async token =>
        {
            var key = await GetHostKeyAsync(token).ConfigureAwait(true);
            var result = await action(key, token).ConfigureAwait(true);
            if (result.Snapshot is not null) Snapshot = result.Snapshot;
            if (!result.Succeeded) throw new InvalidOperationException(result.Message);
            Status = result.Message;
        }, ct).ConfigureAwait(true);
    }

    private async Task<HostKeyCandidate> GetHostKeyAsync(CancellationToken ct)
    {
        if (_hostKey is not null) return _hostKey;
        _hostKey = await _service.GetTrustedHostKeyAsync(_profile, ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(HostFingerprint));
        return _hostKey;
    }

    private void FillDraftFromSnapshot(VpsMaintenanceSnapshot? snapshot)
    {
        if (snapshot is null) return;
        PanelPortText = snapshot.PanelPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OpeningMode = snapshot.PanelAccess == VpsPanelAccess.Both ? PanelOpenMode.PublicHttps : PanelOpenMode.Tunnel;
        AllowedIpsText = ToOperatorIps(snapshot.AllowedIpsText);
        OnPropertyChanged(nameof(PanelPortText));
        OnPropertyChanged(nameof(OpeningMode));
        OnPropertyChanged(nameof(AllowedIpsText));
    }

    private static string ToOperatorIps(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical) || canonical == "any") return "";
        return string.Join(Environment.NewLine, canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value =>
            value.EndsWith("/32", StringComparison.Ordinal) || value.EndsWith("/128", StringComparison.Ordinal)
                ? value[..value.LastIndexOf('/')]
                : value));
    }

    private static string? ToPersistableObservedIps(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical) || canonical == "any") return "";
        var values = canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var plain = new List<string>(values.Length);
        foreach (var value in values)
        {
            if (value.EndsWith("/32", StringComparison.Ordinal) || value.EndsWith("/128", StringComparison.Ordinal))
                plain.Add(value[..value.LastIndexOf('/')]);
            else if (value.Contains('/'))
                return null; // State storage and this editor intentionally support exact host IPs only.
            else
                plain.Add(value);
        }
        var result = string.Join('\n', plain.Distinct(StringComparer.Ordinal));
        try { _ = PanelAllowedIps.Normalize(result); return result; }
        catch (ArgumentException) { return null; }
    }

    private async Task RunAsync(string pending, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = pending;
        try { await work(ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Status = "Операция отменена. Повторно проверьте VPS перед новым изменением."; }
        catch (OperationCanceledException) { Status = "Время ожидания истекло. Проверьте связь с VPS и его состояние перед повтором."; }
        catch (Exception ex) { Status = SafeError(ex); }
        finally { IsBusy = false; }
    }

    private static string SafeError(Exception ex) => ex switch
    {
        VpsKeyPassphraseRequiredException => "Приватный ключ профиля зашифрован. Введите его passphrase в поле выше.",
        ArgumentException => ex.Message,
        UnauthorizedAccessException => "Нет прав для выполнения локального действия.",
        InvalidOperationException => ex.Message,
        _ => "Операция не выполнена. Проверьте принятый SSH fingerprint, учётные данные и доступность VPS. Сырые SSH-данные не отображаются."
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
