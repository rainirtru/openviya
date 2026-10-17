using Microsoft.Win32;
using OpenViya.Ops.Core;
using OpenViya.Ops.Remote;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace OpenViya.Ops.Desktop;

public partial class ServerMaintenanceWindow : Window
{
    private readonly ServerProfile _profile;
    private readonly ServerMaintenanceViewModel _viewModel;
    private readonly Func<Guid, bool> _hasActiveTunnel;
    private readonly Func<Guid, Task> _stopTunnel;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCancellation;
    private readonly bool _previewMode;
    private readonly bool _serverManagedAccess;
    private readonly RemoteVpsMaintenanceService? _remoteMaintenance;
    private bool _initializing = true;
    private bool _closeWhenIdle;
    private bool _draftTouched;

    internal ServerMaintenanceWindow(
        ServerProfile profile,
        IVpsServerMaintenanceService service,
        IProfileStore profileStore,
        IServerStateStore stateStore,
        Func<Guid, bool>? hasActiveTunnel = null,
        Func<Guid, Task>? stopTunnel = null,
        Func<Guid, Task>? profileUpdated = null,
        bool smokeMode = false,
        bool serverManagedAccess = false)
    {
        InitializeComponent();
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _previewMode = smokeMode;
        _serverManagedAccess = serverManagedAccess;
        _remoteMaintenance = service as RemoteVpsMaintenanceService;
        _hasActiveTunnel = hasActiveTunnel ?? (_ => false);
        _stopTunnel = stopTunnel ?? (_ => Task.CompletedTask);
        _viewModel = new ServerMaintenanceViewModel(profile, service, profileStore, stateStore, profileUpdated);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelChanged;
        TitleText.Text = $"Обслуживание · {profile.Name}";
        EndpointText.Text = $"{profile.Host}:{profile.SshPort} · {profile.UserName} · {profile.PanelDomain}";
        if (string.IsNullOrWhiteSpace(profile.IdentityFile) && serverManagedAccess)
        {
            AuthLabel.Content = "Passphrase серверного ключа (если задан)";
            AuthHintText.Text = "Используется закрытый ключ из зашифрованного хранилища LAN-сервера. Passphrase нужен только если ключ защищён.";
        }
        else if (string.IsNullOrWhiteSpace(profile.IdentityFile))
        {
            AuthLabel.Content = "Пароль SSH";
            AuthHintText.Text = $"Подключение выполняется от имени {profile.UserName}. Введите административный SSH-пароль для выбранного действия.";
        }
        else
        {
            AuthLabel.Content = "Passphrase ключа (если задан)";
            AuthHintText.Text = $"Используется ключ профиля {Path.GetFileName(profile.IdentityFile)} от имени {profile.UserName}. Для ключа без passphrase оставьте поле пустым.";
        }
        if (_remoteMaintenance is not null) RecentJobsButton.Visibility = Visibility.Visible;
        PanelPortBox.Text = profile.PanelPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        OpeningModeBox.SelectedIndex = profile.PanelOpenMode == PanelOpenMode.PublicHttps ? 1 : 0;
        AllowedIpsBox.Text = profile.PanelAllowedIpsText;
        StatusText.Text = _viewModel.Status;
        if (smokeMode) ShowInTaskbar = false;
        _initializing = false;
        Closing += Window_Closing;
        Closed += (_, _) => { _lifetime.Cancel(); AuthSecretBox.Clear(); _viewModel.PropertyChanged -= ViewModelChanged; _lifetime.Dispose(); };
        SetEnabledState();
    }

    internal ServerMaintenanceViewModel ViewModel => _viewModel;

    internal static ServerMaintenanceWindow CreatePreviewWindow(ServerProfile profile, IVpsServerMaintenanceService service,
        IProfileStore profileStore, IServerStateStore stateStore)
    {
        var window = new ServerMaintenanceWindow(profile, service, profileStore, stateStore, smokeMode: true);
        window._viewModel.SetPreviewSnapshot(new VpsMaintenanceSnapshot(profile.Id, DateTimeOffset.UtcNow,
            "2.4.0", "2.4.1", true, profile.PanelPort, profile.PanelAccess, "any",
            DateTimeOffset.UtcNow.AddDays(24), true, true, true, true, false,
            [new VpsBackupInfo("backup-20261017T120000-1.tar.gz", 68_157_440, DateTimeOffset.UtcNow.AddHours(-2))],
            "Предварительный снимок для UI smoke; подключение к VPS не выполнялось."));
        window.SyncDraftToControls();
        return window;
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        await WithAccessAsync((access, token) => _viewModel.InspectAsync(access, token));
        if (!_draftTouched && _viewModel.Snapshot is not null) SyncDraftToControls();
    }

    private async void RecentJobs_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteMaintenance is null) return;
        RecentJobsButton.IsEnabled = false;
        StatusText.Text = "Загружаем журнал задач с LAN-сервера…";
        try
        {
            var jobs = await _remoteMaintenance.LoadRecentJobsAsync(_profile.Id, _lifetime.Token);
            var body = jobs.Count == 0
                ? "Для этого профиля ещё нет серверных задач."
                : string.Join(Environment.NewLine + Environment.NewLine, jobs.Select(job =>
                    $"{ActionLabel(job.Action)} · {StateLabel(job.State)} · {job.UpdatedAt.ToLocalTime():g}"));
            OwnedDialog.Show(this, "Журнал серверных задач", body + Environment.NewLine + Environment.NewLine +
                "Это только просмотр. Задача продолжает выполняться на LAN-сервере, даже если закрыть это окно или приложение.");
            StatusText.Text = "Журнал серверных задач обновлён.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            StatusText.Text = "Не удалось загрузить журнал. Проверьте подключение к LAN-серверу и повторите.";
        }
        finally { RecentJobsButton.IsEnabled = !_lifetime.IsCancellationRequested; }
    }

    private static string ActionLabel(string action) => action switch
    {
        "apply-access" => "Изменение доступа панели",
        "renew-certificate" => "Продление сертификата",
        "create-backup" => "Создание резервной копии",
        _ => "Обслуживание сервера"
    };

    private static string StateLabel(string state) => state switch
    {
        "queued" => "В очереди",
        "running" => "Выполняется",
        "complete" => "Завершена",
        "failed" => "Ошибка",
        "needs-attention" => "Требует внимания",
        _ => "Статус уточняется"
    };

    private async void Plan_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PanelPortBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            StatusText.Text = "Введите порт панели числом от 1 до 65535.";
            PanelPortBox.Focus();
            PanelPortBox.SelectAll();
            return;
        }
        var mode = OpeningModeBox.SelectedIndex == 1 ? PanelOpenMode.PublicHttps : PanelOpenMode.Tunnel;
        _viewModel.SetDraft(PanelPortBox.Text, mode, AllowedIpsBox.Text);
        await WithAccessAsync((access, token) => _viewModel.PlanAccessChangeAsync(access, port, mode, AllowedIpsBox.Text, token));
        RenderPlan();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var plan = _viewModel.Plan;
        if (plan is null || plan.Changes.Count == 0) return;

        var stopTunnel = false;
        if (plan.CurrentPanelPort != plan.RequestedPanelPort && _hasActiveTunnel(_profile.Id))
        {
            if (!OwnedDialog.Confirm(this, "Остановить SSH-туннель перед сменой порта?",
                    $"Активный туннель профиля «{_profile.Name}» будет остановлен до изменения порта панели {plan.CurrentPanelPort} → {plan.RequestedPanelPort}.",
                    "Остановить туннель")) return;
            stopTunnel = true;
        }

        var details = string.Join(Environment.NewLine, plan.Changes.Select(change => "• " + change));
        var publicRisk = plan.RequestedAccess == VpsPanelAccess.Both
            ? plan.RequestedAllowedIpsText == "any"
                ? "Панель станет доступна из интернета с любого IP, если её разрешит provider firewall."
                : "Панель станет публично доступна только с указанными IP; соответствующее правило provider firewall нужно проверить вручную."
            : "Панель будет доступна только через SSH-туннель.";
        var confirmation = $"Изменения для «{_profile.Name}»:{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}{publicRisk}{Environment.NewLine}{Environment.NewLine}При неудачном переподключении VPS восстановит старые настройки через пять минут. Программа не меняет межсетевой экран провайдера. Продолжить?";
        if (!OwnedDialog.Confirm(this, "Применить настройки панели?", confirmation, "Применить настройки")) return;

        await WithAccessAsync((access, token) => _viewModel.ApplyAccessChangeAsync(
            access, true, stopTunnel, _hasActiveTunnel, _stopTunnel, token));
        RenderSnapshot();
        RenderPlan();
    }

    private async void CheckCertificate_Click(object sender, RoutedEventArgs e) => await WithAccessAsync((access, token) => _viewModel.CheckCertificateAsync(access, token));

    private async void RenewCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (!OwnedDialog.Confirm(this, "Продлить сертификат панели?",
                "VPS отправит запрос Let’s Encrypt, проверит домен через внешний ACME HTTP challenge и установит новый сертификат. Проверьте доступность домена и входящего TCP-порта 80. Продолжить?",
                "Обратиться к Let’s Encrypt")) return;
        await WithAccessAsync((access, token) => _viewModel.RenewCertificateAsync(access, token));
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await WithAccessAsync((access, token) => _viewModel.CreateBackupAsync(access, token));
        RenderSnapshot();
    }

    private async void Updates_Click(object sender, RoutedEventArgs e) => await WithAccessAsync((access, token) => _viewModel.CheckUpdatesAsync(access, token));

    private async void DownloadBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not VpsBackupInfo backup) return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить резервную копию VPS",
            FileName = backup.Name,
            DefaultExt = ".tar.gz",
            AddExtension = false,
            Filter = "Архив резервной копии (*.tar.gz)|*.tar.gz|Все файлы (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await WithAccessAsync(async (access, token) =>
        {
            var bytes = await _viewModel.DownloadBackupAsync(access, backup.Name, token);
            if (bytes.Length == 0) throw new InvalidOperationException("Резервная копия не получена.");
            await File.WriteAllBytesAsync(dialog.FileName, bytes, token);
            Array.Clear(bytes);
            StatusText.Text = $"Резервная копия сохранена локально: {Path.GetFileName(dialog.FileName)}.";
        });
    }

    private async Task WithAccessAsync(Func<VpsSshAccess, CancellationToken, Task> work)
    {
        if (_viewModel.IsBusy || _lifetime.IsCancellationRequested) return;
        var secret = AuthSecretBox.Password;
        if (string.IsNullOrEmpty(_profile.IdentityFile) && !_serverManagedAccess && string.IsNullOrEmpty(secret))
        {
            StatusText.Text = "Введите административный SSH-пароль для выбранного действия.";
            AuthSecretBox.Focus();
            return;
        }

        var access = string.IsNullOrWhiteSpace(_profile.IdentityFile)
            ? new VpsSshAccess { UserName = _profile.UserName, Method = _serverManagedAccess ? VpsAuthMethod.PrivateKey : VpsAuthMethod.Password, Password = _serverManagedAccess ? "" : secret, KeyPassphrase = _serverManagedAccess ? secret : "" }
            : new VpsSshAccess { UserName = _profile.UserName, Method = VpsAuthMethod.PrivateKey, IdentityFile = _profile.IdentityFile, KeyPassphrase = secret };
        AuthSecretBox.Clear();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = operation;
        try { await work(access, operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusText.Text = "Операция отменена. Проверьте состояние VPS перед повтором, так как удалённое изменение могло завершиться.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is UnauthorizedAccessException or IOException
                ? "Локальный файл резервной копии не удалось сохранить. Проверьте выбранное расположение и права доступа."
                : "Операция не завершена. Проверьте состояние VPS перед повтором.";
        }
        finally
        {
            AuthSecretBox.Clear();
            access = null!;
            _operationCancellation = null;
            SetEnabledState();
            if (_closeWhenIdle && !_viewModel.IsBusy) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        RenderSnapshot();
        RenderPlan();
    }

    private void Draft_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _viewModel is null) return;
        _draftTouched = true;
        var mode = OpeningModeBox.SelectedIndex == 1 ? PanelOpenMode.PublicHttps : PanelOpenMode.Tunnel;
        _viewModel.SetDraft(PanelPortBox.Text, mode, AllowedIpsBox.Text);
        RenderPlan();
    }

    private void Backup_SelectionChanged(object sender, SelectionChangedEventArgs e) => DownloadBackupButton.IsEnabled = !_previewMode && !_viewModel.IsBusy && BackupList.SelectedItem is VpsBackupInfo;

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerMaintenanceViewModel.Status) or null) StatusText.Text = _viewModel.Status;
        if (e.PropertyName is nameof(ServerMaintenanceViewModel.Snapshot) or null) RenderSnapshot();
        if (e.PropertyName is nameof(ServerMaintenanceViewModel.Plan) or null) RenderPlan();
        if (e.PropertyName is nameof(ServerMaintenanceViewModel.HostFingerprint) or null) FingerprintText.Text = _viewModel.HostFingerprint;
        if (e.PropertyName is nameof(ServerMaintenanceViewModel.IsBusy) or null)
        {
            SetEnabledState();
        }
    }

    private void RenderSnapshot()
    {
        var snapshot = _viewModel.Snapshot;
        if (snapshot is null) return;
        var access = snapshot.PanelAccess == VpsPanelAccess.Both ? "Публичный HTTPS" : "Только SSH-туннель";
        var certificate = snapshot.CertificateExpiresAt is { } expires
            ? $"сертификат до {expires.ToLocalTime():g}"
            : snapshot.CertificateManagedByLetsEncrypt ? "Let’s Encrypt настроен, срок не получен" : "сертификат Let’s Encrypt не обнаружен";
        var backups = snapshot.Backups.Count == 0 ? "резервных копий нет" : $"копий: {snapshot.Backups.Count}";
        var hasUneditableNetworks = snapshot.AllowedIpsText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(value =>
            value.Contains('/') && !value.EndsWith("/32", StringComparison.Ordinal) && !value.EndsWith("/128", StringComparison.Ordinal));
        var updates = string.IsNullOrWhiteSpace(snapshot.LatestVersion)
            ? "обновления не проверялись"
            : snapshot.UpdateAvailable ? $"доступна 3x-ui {snapshot.LatestVersion}" : $"актуальная проверенная версия {snapshot.InstalledVersion}";
        SnapshotText.Text = $"Порт {snapshot.PanelPort} · {access} · IP: {FormatAllowed(snapshot.AllowedIpsText)}{Environment.NewLine}" +
            $"Панель: {(snapshot.PanelHealthy ? "работает" : "проверка не пройдена")} · UFW: {(snapshot.FirewallActive ? "активен" : "неактивен")}, правила {(snapshot.PanelFirewallRulesMatch ? "соответствуют панели" : "не совпадают")}{Environment.NewLine}" +
            $"3x-ui {snapshot.InstalledVersion} · {updates} · {certificate} · {backups}{Environment.NewLine}" +
            $"Проверено {snapshot.CheckedAt.ToLocalTime():g}. {snapshot.Message}" +
            (hasUneditableNetworks ? $"{Environment.NewLine}В текущем ограничении есть CIDR-сети. Поле принимает только отдельные IP: замените список явно, если хотите изменить их." : "");
        BackupList.ItemsSource = snapshot.Backups;
        if (snapshot.Backups.Count > 0 && BackupList.SelectedItem is null) BackupList.SelectedIndex = 0;
        DownloadBackupButton.IsEnabled = !_previewMode && !_viewModel.IsBusy && BackupList.SelectedItem is VpsBackupInfo;
    }

    private void RenderPlan()
    {
        var plan = _viewModel.Plan;
        PlanPanel.Visibility = plan is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = !_previewMode && !_viewModel.IsBusy && plan is { Changes.Count: > 0 } && !_lifetime.IsCancellationRequested;
        if (plan is not null)
            PlanText.Text = plan.Changes.Count == 0 ? "Сервер уже соответствует этим настройкам. Изменений нет." : string.Join(Environment.NewLine, plan.Changes.Select(change => "• " + change));
    }

    private void SetEnabledState()
    {
        var enabled = !_previewMode && !_viewModel.IsBusy && !_lifetime.IsCancellationRequested;
        InspectButton.IsEnabled = PlanButton.IsEnabled = CheckCertificateButton.IsEnabled = RenewCertificateButton.IsEnabled = BackupButton.IsEnabled = UpdatesButton.IsEnabled = enabled;
        if (_remoteMaintenance is not null) RecentJobsButton.IsEnabled = enabled;
        PanelPortBox.IsEnabled = OpeningModeBox.IsEnabled = AllowedIpsBox.IsEnabled = enabled;
        AuthSecretBox.IsEnabled = enabled;
        CancelOperationButton.Visibility = _viewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = _operationCancellation is { IsCancellationRequested: false };
        DownloadBackupButton.IsEnabled = enabled && BackupList.SelectedItem is VpsBackupInfo;
        ApplyButton.IsEnabled = enabled && _viewModel.Plan is { Changes.Count: > 0 };
    }

    private void SyncDraftToControls()
    {
        _initializing = true;
        PanelPortBox.Text = _viewModel.PanelPortText;
        OpeningModeBox.SelectedIndex = _viewModel.OpeningMode == PanelOpenMode.PublicHttps ? 1 : 0;
        AllowedIpsBox.Text = _viewModel.AllowedIpsText;
        _initializing = false;
    }

    private static string FormatAllowed(string value) => string.IsNullOrWhiteSpace(value) || value == "any" ? "любой источник" : value.Replace('\n', ',');

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not { IsCancellationRequested: false } operation) return;
        StatusText.Text = "Отменяем ожидание. Если изменение уже достигло VPS, проверьте состояние перед повтором.";
        operation.Cancel();
        SetEnabledState();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true;
        _closeWhenIdle = true;
        if (!_lifetime.IsCancellationRequested)
        {
            StatusText.Text = "Отменяем операцию перед закрытием окна…";
            _lifetime.Cancel();
            SetEnabledState();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
