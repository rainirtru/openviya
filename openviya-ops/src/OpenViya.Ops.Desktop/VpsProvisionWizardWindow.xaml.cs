using Microsoft.Win32;
using OpenViya.Ops.Core;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenViya.Ops.Desktop;

public partial class VpsProvisionWizardWindow : Window
{
    private readonly IProfileStore _profiles;
    private readonly ISshService _ssh;
    private readonly IVpsProvisioner _provisioner;
    private readonly IVpsProvisionStore _provisionStore;
    private readonly VpsProvisionWizardViewModel _viewModel = new();
    private readonly VpsProgressViewModel _progressViewModel = new();
    private System.Windows.Threading.DispatcherTimer? _progressClock;
    private readonly IVpsConnectionPreflightService _preflight;
    private readonly ServerProfile? _resumeProfile;
    private readonly VpsProvisionRun? _resumeRun;
    private Guid _profileId;
    private readonly Guid _runId;
    private HostKeyCandidate? _acceptedHostKey;
    private string? _trustedEndpoint;
    private string _lastGeneratedName = "";
    private bool _initializing;
    private bool _programmaticName;
    private bool _nameWasEdited;
    private bool _running;
    private bool _scanning;
    private bool _detached;
    private bool _detachPending;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _connectionCheckCts;
    private Task<VpsProvisionResult>? _activeRunTask;
    private bool _historyReady;

    public ServerProfile? ProvisionedProfile { get; private set; }
    public bool ProvisioningCompleted { get; private set; }

    internal bool SmokeRunDisabled => !RunButton.IsEnabled;

    internal void ShowSmokeStep(int step)
    {
        _viewModel.MoveTo(step);
        UpdateStepView();
    }

    internal void ShowSmokeAllowedIps()
    {
        _viewModel.MoveTo(2);
        UpdateStepView();
        ProgressDetails.IsExpanded = false;
        UpdateLayout();
        ((FrameworkElement)PanelCidrBox.Parent).BringIntoView();
    }

    internal IReadOnlyList<VpsProvisionRun> SetSmokeProgressPreview(bool failed)
    {
        var time = DateTimeOffset.UtcNow.AddSeconds(-9);
        VpsProvisionRun[] events = [
            new(_runId, _profileId, VpsProvisionStage.Preflight, "Проверка ОС и правил доступа VPS.", time),
            new(_runId, _profileId, VpsProvisionStage.PreparingDns, "Публичный DNS подтверждён.", time.AddSeconds(2)),
            new(_runId, _profileId, VpsProvisionStage.PreparingAccess, "VPS: подготовка доступа завершена.", time.AddSeconds(4)),
            new(_runId, _profileId, VpsProvisionStage.VerifyingAdmin, "Новое подключение администратора проверено.", time.AddSeconds(5)),
            new(_runId, _profileId, VpsProvisionStage.InstallingPanel, "VPS: Выпуск сертификата Let’s Encrypt через TCP 80.", time.AddSeconds(7))
        ];
        if (ProgressJournal.EventCount == 0)
            foreach (var item in events) RenderProgress(item);
        else RenderProgress(events[^1]);
        if (failed)
        {
            var failure = new VpsProvisionRun(_runId, _profileId, VpsProvisionStage.NeedsAttention,
                "VPS остановился на шаге «Выпуск сертификата Let’s Encrypt через TCP 80». Код завершения: 1. Проверьте A/AAAA домена панели и доступность TCP 80 в firewall провайдера.", time.AddSeconds(9));
            RenderProgress(failure);
            return [..events, failure];
        }
        return events;
    }

    private void RenderProgress(VpsProvisionRun run)
    {
        ProgressSummaryPanel.Visibility = Visibility.Visible;
        _progressViewModel.Update(run);
        ProgressDnsPanel.Visibility = _progressViewModel.DnsItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StageText.Text = StageLabel(run.Stage);
        RunMessageText.Text = run.Message;
        ProgressJournal.Append(run);
        ProgressDetails.IsExpanded = true;
        var stopped = run.Stage is VpsProvisionStage.Complete or VpsProvisionStage.NeedsAttention or VpsProvisionStage.Failed;
        RunProgress.IsIndeterminate = !stopped;
        FormError.Text = run.Stage is VpsProvisionStage.NeedsAttention or VpsProvisionStage.Failed ? run.Message : "";
    }

    internal void SetSmokePreview(bool keyMode, bool manualDns = false)
    {
        DnsModeBox.SelectedIndex = manualDns ? 1 : 0;
        PanelAccessBox.SelectedIndex = manualDns ? 1 : 0;
        PanelCidrBox.Text = manualDns ? "127.0.0.1\n127.0.0.2\n2001:db8::1" : "";
        if (keyMode)
        {
            AuthMethodBox.SelectedIndex = 1;
            InitialKeyPathBox.Text = @"C:\PreviewOnly\initial-key";
            InitialSecretBox.Password = "preview-only-passphrase";
        }
        else
        {
            AuthMethodBox.SelectedIndex = 0;
            InitialKeyPathBox.Clear();
            InitialSecretBox.Password = "preview-only-password";
        }
        if (manualDns)
        {
            PanelDomainBox.Text = "panel-preview.openviya.online";
            VpnDomainBox.Text = "preview.openviya.online";
            if (!PanelCidrBox.AcceptsReturn || !PanelCidrBox.IsEnabled ||
                PanelAllowedIps.Normalize(PanelCidrBox.Text) != "127.0.0.1/32\n127.0.0.2/32\n2001:db8::1/128")
                throw new InvalidOperationException("Allowed IP multiline preview did not retain its values.");
            PanelCidrBox.BringIntoView();
            _viewModel.MoveTo(2);
            UpdateStepView();
        }
    }

    public VpsProvisionWizardWindow(
        IProfileStore profiles,
        ISshService ssh,
        IVpsProvisioner provisioner,
        IVpsProvisionStore provisionStore,
        ServerProfile? resumeProfile = null,
        VpsProvisionRun? resumeRun = null,
        IVpsRemoteSessionFactory? remoteSessionFactory = null)
    {
        InitializeComponent();
        ProgressSummaryPanel.DataContext = _progressViewModel;
        _progressClock = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromSeconds(1) };
        _progressClock.Tick += (_, _) => _progressViewModel.RefreshClock(DateTimeOffset.UtcNow);
        _progressClock.Start();
        Closed += (_, _) => { _progressClock?.Stop(); _connectionCheckCts?.Cancel(); };
        WizardForm.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(WizardTextChanged), true);
        _preflight = new VpsConnectionPreflightService(remoteSessionFactory ?? new OpenViya.Ops.Provisioning.SshNetVpsSessionFactory());
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VpsProvisionWizardViewModel.ConnectionResult))
            {
                ConnectionCheckText.Text = _viewModel.ConnectionResult;
                ConnectionCheckText.Foreground = (System.Windows.Media.Brush)FindResource(_viewModel.ConnectionReady ? "Teal" : "Brass");
            }
            if (e.PropertyName == nameof(VpsProvisionWizardViewModel.ConnectionReady)) UpdateRunAvailability();
        };
        ConnectionCheckText.Text = _viewModel.ConnectionResult;
        _profiles = profiles;
        _ssh = ssh;
        _provisioner = provisioner;
        _provisionStore = provisionStore;
        _resumeProfile = resumeProfile;
        _resumeRun = resumeRun;
        _historyReady = resumeRun is null;
        Loaded += async (_, _) =>
        {
            if (resumeRun is null) return;
            try
            {
                var history = await _provisionStore.LoadRunHistoryAsync(_runId);
                _progressViewModel.SetHistory(history);
                foreach (var item in history) ProgressJournal.Append(item);
                if (history.Count == 0) ProgressJournal.Append(resumeRun);
                _progressViewModel.Update(resumeRun);
                ProgressSummaryPanel.Visibility = Visibility.Visible;
                ProgressDnsPanel.Visibility = _progressViewModel.DnsItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                ProgressDetails.IsExpanded = true;
            }
            catch { FormError.Text = "Историю шагов прочитать не удалось. Последнее состояние сохранено в карточке сервера."; }
            finally { _historyReady = true; UpdateRunAvailability(); }
        };
        _profileId = resumeProfile?.Id ?? Guid.NewGuid();
        _runId = resumeRun?.RunId ?? Guid.NewGuid();
        _initializing = true;
        RoleBox.SelectedIndex = 0;
        DnsModeBox.SelectedIndex = 0;
        AuthMethodBox.SelectedIndex = 0;
        PanelAccessBox.SelectedIndex = 0;
        PanelOpenModeBox.SelectedIndex = 0;
        AdminKeyModeBox.SelectedIndex = 0;
        if (resumeProfile is not null) LoadProfile(resumeProfile);
        else RefreshDefaults();
        _initializing = false;
        if (resumeRun is not null)
        {
            TitleText.Text = "Продолжить настройку VPS";
            if (FreshConfirmBox.Content is TextBlock freshConfirmation)
                freshConfirmation.Text = "Продолжаю сохранённый запуск на том же VPS. При начале настройки он был новым и не содержал существующей панели или важных данных.";
            StageText.Text = StageLabel(resumeRun.Stage);
            RunMessageText.Text = resumeRun.Message;
            _progressViewModel.Update(resumeRun);
            ProgressDnsPanel.Visibility = _progressViewModel.DnsItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RunButton.Content = "Возобновить проверку и настройку";
        }
        if (resumeProfile is not null)
        {
            // A resumed run must keep the original endpoint and identity; changing it would detach it from persisted progress.
            IpBox.IsReadOnly = true;
            SshPortBox.IsReadOnly = true;
            AdminUserBox.IsReadOnly = true;
        }
        PanelCidrBox.IsEnabled = SelectedTag(PanelAccessBox) == "Both";
        AllowedIpsPanel.IsEnabled = SelectedTag(PanelAccessBox) == "Both";
        InitialKeyPathBox.IsEnabled = SelectedTag(AuthMethodBox) == "PrivateKey";
        InitialKeyBrowseButton.IsEnabled = SelectedTag(AuthMethodBox) == "PrivateKey";
        AdminKeyPicker.IsEnabled = SelectedTag(AdminKeyModeBox) == "Existing";
        AdminKeyPassphraseBox.IsEnabled = SelectedTag(AdminKeyModeBox) == "Existing";
        UpdateModeControls();
        UpdateStepView();
        UpdateRunAvailability();
    }

    private void LoadProfile(ServerProfile profile)
    {
        NameBox.Text = profile.Name;
        IpBox.Text = profile.Host;
        RoleBox.SelectedIndex = profile.Role == ServerRole.Control ? 1 : 0;
        RegionBox.Text = profile.Region;
        IndexBox.Text = profile.NodeIndex.ToString(CultureInfo.InvariantCulture);
        SshPortBox.Text = profile.SshPort.ToString(CultureInfo.InvariantCulture);
        DnsModeBox.SelectedIndex = profile.SubdomainMode == SubdomainMode.Manual ? 1 : 0;
        ZoneBox.Text = profile.DnsZone;
        DnsAddressBox.Text = profile.DnsAddress;
        PanelDomainBox.Text = profile.PanelDomain;
        VpnDomainBox.Text = profile.VpnDomain;
        AdminUserBox.Text = profile.UserName;
        PanelPortBox.Text = profile.PanelPort.ToString(CultureInfo.InvariantCulture);
        PanelAccessBox.SelectedIndex = profile.PanelAccess == VpsPanelAccess.Both ? 1 : 0;
        PanelOpenModeBox.SelectedIndex = profile.PanelOpenMode == PanelOpenMode.PublicHttps ? 1 : 0;
        PanelCidrBox.Text = profile.PanelAllowedIpsText;
        if (!string.IsNullOrWhiteSpace(profile.IdentityFile))
        {
            AdminKeyModeBox.SelectedIndex = 1;
            AdminKeyPathBox.Text = profile.IdentityFile;
        }
        _nameWasEdited = true;
        _lastGeneratedName = profile.Name;
    }

    private void SettingsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        RefreshDefaults();
        UpdateRunAvailability();
    }

    private void DnsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateModeControls();
        RefreshDefaults();
        UpdateRunAvailability();
    }

    private void AuthMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _viewModel.InvalidateConnection("Способ входа изменён. Запустите проверку SSH/SFTP повторно.");
        var keyAuth = SelectedTag(AuthMethodBox) == "PrivateKey";
        InitialSecretBox.ToolTip = keyAuth ? "Passphrase начального приватного ключа" : "Пароль начального SSH-пользователя";
        InitialKeyPathBox.IsEnabled = keyAuth;
        InitialKeyBrowseButton.IsEnabled = keyAuth;
        UpdateRunAvailability();
    }

    private void PanelAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        PanelCidrBox.IsEnabled = SelectedTag(PanelAccessBox) == "Both";
        AllowedIpsPanel.IsEnabled = SelectedTag(PanelAccessBox) == "Both";
        UpdateRunAvailability();
    }

    private void AdminKeyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        AdminKeyPicker.IsEnabled = SelectedTag(AdminKeyModeBox) == "Existing";
        AdminKeyPassphraseBox.IsEnabled = SelectedTag(AdminKeyModeBox) == "Existing";
        UpdateRunAvailability();
    }

    private void UpdateModeControls()
    {
        var automatic = SelectedTag(DnsModeBox) != "Manual";
        PanelDomainBox.IsReadOnly = automatic;
        VpnDomainBox.IsReadOnly = automatic;
        DnsHint.Text = automatic
            ? "Автоматический режим создаст имена и DNS-записи через Timeweb API после нажатия кнопки настройки. Проверьте DNS token и права на зону."
            : "Ручной режим: мастер не создаёт DNS-записи. Создайте A-записи для этих имён на сайте DNS-провайдера и дождитесь распространения. ";
        DnsHint.TextWrapping = TextWrapping.Wrap;
        UpdateRunAvailability();
    }

    private void RefreshDefaults()
    {
        var role = SelectedTag(RoleBox) == "Control" ? ServerRole.Control : ServerRole.VpnNode;
        var region = RegionBox.Text.Trim();
        var index = ParsePositiveInt(IndexBox.Text, 1);
        if (string.IsNullOrWhiteSpace(region) || !ProfileValidator.IsValidDomain(ZoneBox.Text.Trim()))
        {
            if (string.IsNullOrWhiteSpace(region)) DnsHint.Text = "Укажите регион; домены будут рассчитаны автоматически после исправления.";
            else DnsHint.Text = "DNS-зона пока введена не полностью. Домен автоматически обновится после ввода корректной зоны.";
            return;
        }
        try
        {
            var suggestion = ProfileDefaults.Apply(new ServerProfile { Id = _profileId, Role = role, Region = region, NodeIndex = index, DnsZone = ZoneBox.Text.Trim() });
            if (!_nameWasEdited || NameBox.Text == _lastGeneratedName)
            {
                _programmaticName = true;
                NameBox.Text = role == ServerRole.Control ? $"Control {region}-{index}" : $"VPN {region}-{index}";
                _programmaticName = false;
                _lastGeneratedName = NameBox.Text;
            }
            if (SelectedTag(DnsModeBox) != "Manual")
            {
                PanelDomainBox.Text = suggestion.PanelDomain;
                VpnDomainBox.Text = suggestion.VpnDomain;
            }
        }
        catch (ArgumentException)
        {
            DnsHint.Text = "Не удалось сформировать имена: проверьте DNS-зону, регион и индекс.";
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_programmaticName && !_initializing) _nameWasEdited = true;
        UpdateRunAvailability();
    }

    private void WizardTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (ReferenceEquals(e.OriginalSource, RegionBox) || ReferenceEquals(e.OriginalSource, IndexBox) || ReferenceEquals(e.OriginalSource, ZoneBox))
            RefreshDefaults();
        UpdateRunAvailability();
    }

    private void WizardPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) _viewModel.InvalidateConnection("Секрет аутентификации изменён. Запустите проверку SSH/SFTP повторно.");
        UpdateRunAvailability();
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UpdateRunAvailability();

    private void EndpointChanged(object sender, TextChangedEventArgs e)
    {
        var endpoint = EndpointText();
        if (_trustedEndpoint is not null && !StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, endpoint))
        {
            _profileId = Guid.NewGuid();
            _trustedEndpoint = null;
            _acceptedHostKey = null;
            HostKeySummary.Text = "Адрес или SSH-порт изменён. Снова сверьте fingerprint сервера.";
        }
        else if (_acceptedHostKey is not null && !StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, endpoint))
        {
            _acceptedHostKey = null;
            HostKeySummary.Text = "Endpoint изменён. Fingerprint нужно проверить заново.";
        }
        _viewModel.InvalidateConnection("IP или SSH-порт изменён. Повторите проверку fingerprint и подключения.");
        UpdateRunAvailability();
    }

    private void AccessChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _viewModel.InvalidateConnection("Способ входа изменён. Запустите проверку SSH/SFTP повторно.");
        UpdateRunAvailability();
    }

    private async void CheckConnection_Click(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";
        if (_acceptedHostKey is null || _trustedEndpoint is null || !StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, EndpointText()))
        {
            _viewModel.SetConnectionResult(false, "Сначала сверьте fingerprint для текущего IP и SSH-порта.");
            return;
        }
        if (!TryPort(SshPortBox.Text, out var sshPort) || !ProfileValidator.IsPublicIpLiteral(IpBox.Text.Trim()))
        {
            _viewModel.SetConnectionResult(false, "Введите публичный IP VPS и SSH-порт перед проверкой.");
            return;
        }
        VpsSshAccess access;
        try { access = BuildInitialAccess(); }
        catch (ArgumentException ex) { _viewModel.SetConnectionResult(false, ex.Message); return; }
        var endpointAtStart = EndpointText();
        CheckConnectionButton.IsEnabled = false;
        ConnectionStep.IsEnabled = false;
        PreviousStepButton.IsEnabled = false;
        NextStepButton.IsEnabled = false;
        RunProgress.IsIndeterminate = true;
        StageText.Text = "Проверяем SSH/SFTP и читаем сведения об ОС…";
        _connectionCheckCts = new CancellationTokenSource();
        try
        {
            var profile = new ServerProfile { Id = _profileId, Host = IpBox.Text.Trim(), SshPort = sshPort, UserName = AdminUserBox.Text.Trim() };
            var result = await _preflight.CheckAsync(profile, access, _acceptedHostKey, _connectionCheckCts.Token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(endpointAtStart, EndpointText()))
            {
                _viewModel.InvalidateConnection("Адрес изменился во время проверки. Проверьте подключение заново.");
                return;
            }
            _viewModel.SetConnectionResult(result.Success, result.Message);
            StageText.Text = result.Success ? "SSH/SFTP и поддерживаемая ОС подтверждены." : "Проверка подключения требует внимания.";
        }
        catch (Exception ex) { _viewModel.SetConnectionResult(false, SafeError(ex)); }
        finally
        {
            _connectionCheckCts?.Dispose();
            _connectionCheckCts = null;
            RunProgress.IsIndeterminate = false;
            ConnectionStep.IsEnabled = true;
            CheckConnectionButton.IsEnabled = true;
            UpdateRunAvailability();
        }
    }

    private VpsSshAccess BuildInitialAccess()
    {
        if (_resumeRun is not null && !string.IsNullOrWhiteSpace(_resumeProfile?.IdentityFile))
            return new VpsSshAccess { UserName = _resumeProfile.UserName, Method = VpsAuthMethod.PrivateKey, IdentityFile = _resumeProfile.IdentityFile, KeyPassphrase = InitialSecretBox.Password };
        var method = SelectedTag(AuthMethodBox) == "PrivateKey" ? VpsAuthMethod.PrivateKey : VpsAuthMethod.Password;
        var path = InitialKeyPathBox.Text.Trim();
        if (method == VpsAuthMethod.PrivateKey && (string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
            throw new ArgumentException("Выберите существующий приватный ключ начального SSH-доступа.");
        if (method == VpsAuthMethod.Password && string.IsNullOrEmpty(InitialSecretBox.Password))
            throw new ArgumentException("Введите начальный SSH-пароль; он используется только в памяти.");
        return new VpsSshAccess
        {
            UserName = InitialUserBox.Text.Trim(), Method = method,
            Password = method == VpsAuthMethod.Password ? InitialSecretBox.Password : "",
            IdentityFile = method == VpsAuthMethod.PrivateKey ? Path.GetFullPath(path) : "",
            KeyPassphrase = method == VpsAuthMethod.PrivateKey ? InitialSecretBox.Password : ""
        };
    }

    private void PreviousStep_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Step > 0) _viewModel.MoveTo(_viewModel.Step - 1);
        UpdateStepView();
    }

    private void NextStep_Click(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";
        var error = ValidateStep(_viewModel.Step);
        if (error is not null) { FormError.Text = error; return; }
        _viewModel.MoveTo(_viewModel.Step + 1);
        UpdateStepView();
    }

    private string? ValidateStep(int step) => step switch
    {
        0 => !ProfileValidator.IsPublicIpLiteral(IpBox.Text.Trim()) ? "Введите публичный IPv4 или IPv6-адрес VPS." :
            !TryPort(SshPortBox.Text, out _) ? "SSH-порт должен быть числом от 1 до 65535." :
            !Regex.IsMatch(InitialUserBox.Text.Trim(), @"^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant) ? "Укажите корректного начального SSH-пользователя." :
            _acceptedHostKey is null || _trustedEndpoint is null || !StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, EndpointText()) ? "Сверьте fingerprint ssh-ed25519 для текущего IP и порта." :
            !_viewModel.ConnectionReady ? "Нажмите «Проверить подключение» и исправьте результат, прежде чем продолжить." : null,
        1 => ValidateServerInputs(),
        2 => ValidatePanelInputs(),
        3 => ValidateInputs(showErrors: false) ?? (FreshConfirmBox.IsChecked == true && FirewallConfirmBox.IsChecked == true && LetsEncryptConfirmBox.IsChecked == true ? null : "Подтвердите свежесть VPS, firewall провайдера и условия Let’s Encrypt."),
        _ => "Неизвестный шаг мастера."
    };

    private string? ValidateServerInputs()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) return "Укажите имя сервера.";
        if (!Regex.IsMatch(RegionBox.Text.Trim(), @"^[a-zA-Z0-9][a-zA-Z0-9-]{0,31}$", RegexOptions.CultureInvariant)) return "Укажите короткий регион из букв, цифр и дефиса.";
        if (!int.TryParse(IndexBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index is < 1 or > 999) return "Индекс узла должен быть числом от 1 до 999.";
        if (!Regex.IsMatch(AdminUserBox.Text.Trim(), @"^[a-z][a-z0-9_-]{0,30}$", RegexOptions.CultureInvariant) || StringComparer.Ordinal.Equals(AdminUserBox.Text.Trim(), "root")) return "Имя нового администратора должно быть корректным и отличаться от root.";
        if (!ProfileValidator.IsValidDomain(ZoneBox.Text.Trim())) return "Укажите корректную DNS-зону.";
        if (!ProfileValidator.IsValidDomain(PanelDomainBox.Text.Trim()) || !PanelDomainBox.Text.Trim().EndsWith("." + ZoneBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return "Домен панели должен находиться внутри выбранной DNS-зоны.";
        if (SelectedTag(RoleBox) == "VpnNode" && (!ProfileValidator.IsValidDomain(VpnDomainBox.Text.Trim()) || !VpnDomainBox.Text.Trim().EndsWith("." + ZoneBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))) return "VPN-домен должен находиться внутри выбранной DNS-зоны.";
        if (!string.IsNullOrWhiteSpace(DnsAddressBox.Text) && (!ProfileValidator.IsPublicIpLiteral(DnsAddressBox.Text.Trim()) || !IPAddress.TryParse(DnsAddressBox.Text.Trim(), out var dnsIp) || !IPAddress.TryParse(IpBox.Text.Trim(), out var hostIp) || !dnsIp.Equals(hostIp))) return "DNS-адрес должен совпадать с IP VPS; оставьте поле пустым, чтобы использовать IP узла.";
        if (SelectedTag(AdminKeyModeBox) == "Existing" && (string.IsNullOrWhiteSpace(AdminKeyPathBox.Text) || !File.Exists(AdminKeyPathBox.Text.Trim()) || VpsKeyFiles.IsPublicKeyPath(AdminKeyPathBox.Text))) return "Выберите существующий приватный Ed25519-ключ администратора.";
        return null;
    }

    private string? ValidatePanelInputs()
    {
        if (!TryPort(PanelPortBox.Text, out var panelPort) || panelPort is 80 or 443 or 2096 || panelPort == ParsePositiveInt(SshPortBox.Text, 22)) return "Порт панели должен быть свободным TCP-портом от 1 до 65535 и отличаться от SSH, 80, 443 и 2096.";
        if (SelectedTag(PanelOpenModeBox) == "PublicHttps" && SelectedTag(PanelAccessBox) != "Both") return "Для публичного HTTPS выберите публичный доступ к панели на VPS.";
        if (!Regex.IsMatch(AcmeEmailBox.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant)) return "Укажите корректный email для Let’s Encrypt.";
        if (SelectedTag(PanelAccessBox) == "Both")
        {
            try { PanelAllowedIps.Normalize(PanelCidrBox.Text); }
            catch (ArgumentException ex) { PanelAllowedIpsError.Text = ex.Message; return ex.Message; }
        }
        PanelAllowedIpsError.Text = "";
        return null;
    }

    private void UpdateStepView()
    {
        ConnectionStep.Visibility = _viewModel.Step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerStep.Visibility = _viewModel.Step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelStep.Visibility = _viewModel.Step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ReviewStep.Visibility = _viewModel.Step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepMark0.BorderThickness = new Thickness(0, 0, 0, _viewModel.Step == 0 ? 3 : 1);
        StepMark1.BorderThickness = new Thickness(0, 0, 0, _viewModel.Step == 1 ? 3 : 1);
        StepMark2.BorderThickness = new Thickness(0, 0, 0, _viewModel.Step == 2 ? 3 : 1);
        StepMark3.BorderThickness = new Thickness(0, 0, 0, _viewModel.Step == 3 ? 3 : 1);
        PreviousStepButton.IsEnabled = _viewModel.Step > 0 && !_running;
        NextStepButton.Visibility = _viewModel.Step < 3 ? Visibility.Visible : Visibility.Collapsed;
        RunButton.Visibility = _viewModel.Step == 3 ? Visibility.Visible : Visibility.Collapsed;
        RefreshReviewSummary();
        UpdateRunAvailability();
    }

    private void RefreshReviewSummary()
    {
        if (ReviewSummary is null) return;
        var mode = SelectedTag(DnsModeBox) == "Manual" ? "ручной DNS (изменения на сайте провайдера)" : "автоматический DNS (создание при запуске)";
        var panel = SelectedTag(PanelOpenModeBox) == "PublicHttps" ? "публичный HTTPS" : "SSH-туннель";
        ReviewSummary.Text = $"Сервер: {NameBox.Text.Trim()} · {IpBox.Text.Trim()}:{SshPortBox.Text.Trim()}\n" +
            $"Пользователь: {AdminUserBox.Text.Trim()} · {RegionBox.Text.Trim()} · {SelectedTag(RoleBox)}\n" +
            $"DNS: {mode} · {PanelDomainBox.Text.Trim()}" + (SelectedTag(RoleBox) == "Control" ? "" : $" · {VpnDomainBox.Text.Trim()}") + "\n" +
            $"Панель: {panel} · TCP {PanelPortBox.Text.Trim()} · {AcmeEmailBox.Text.Trim()}\n" +
            $"Firewall IP-ограничение: {(SelectedTag(PanelAccessBox) == "Both" ? PanelCidrBox.Text.Trim().Replace(Environment.NewLine, ", ") : "не используется")}\n" +
            $"Fingerprint: {_acceptedHostKey?.Fingerprint ?? "не принят"} · Проверка SSH/SFTP: {(_viewModel.ConnectionReady ? "пройдена" : "не пройдена")}";
    }

    private async void ScanHostKey_Click(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";
        if (!ProfileValidator.IsPublicIpLiteral(IpBox.Text.Trim()) || !TryPort(SshPortBox.Text, out var sshPort))
        {
            FormError.Text = "Для сканирования введите корректный IP-адрес VPS и SSH-порт.";
            return;
        }
        var scannedEndpoint = EndpointText();
        var profileForScan = new ServerProfile { Id = _profileId, Host = IpBox.Text.Trim(), SshPort = sshPort, UserName = InitialUserBox.Text.Trim() };
        _scanning = true;
        WizardForm.IsEnabled = false;
        ScanHostKeyButtonState(true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var candidates = await _ssh.ScanHostKeysAsync(profileForScan, timeout.Token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(scannedEndpoint, EndpointText()))
            {
                _acceptedHostKey = null;
                HostKeySummary.Text = "Endpoint изменён во время проверки. Повторите сканирование.";
                return;
            }
            var ed25519 = candidates.Where(x => StringComparer.Ordinal.Equals(x.KeyType, "ssh-ed25519")).ToArray();
            if (ed25519.Length == 0)
            {
                _acceptedHostKey = null;
                HostKeySummary.Text = "Не найден ssh-ed25519. Установка доступна только после проверки этого типа ключа.";
                return;
            }
            var chosen = HostKeyReviewWindow.Choose(this, IpBox.Text.Trim(), sshPort, ed25519);
            if (chosen is null)
            {
                _acceptedHostKey = null;
                HostKeySummary.Text = "Fingerprint не принят; доверие не изменено.";
                return;
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(scannedEndpoint, EndpointText()))
            {
                _acceptedHostKey = null;
                HostKeySummary.Text = "Endpoint изменён во время сверки. Trust не изменён; повторите проверку.";
                return;
            }
            using var trustTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _ssh.TrustHostKeyAsync(profileForScan, chosen, trustTimeout.Token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(scannedEndpoint, EndpointText()))
            {
                _acceptedHostKey = null;
                HostKeySummary.Text = "Endpoint изменился во время принятия ключа. Повторите проверку fingerprint.";
                return;
            }
            _acceptedHostKey = chosen;
            _trustedEndpoint = EndpointText();
            HostKeySummary.Text = $"Принят ssh-ed25519 · {chosen.Fingerprint} · {IpBox.Text.Trim()}:{sshPort}";
        }
        catch (OperationCanceledException) { FormError.Text = "Проверка host key превысила лимит времени. Повторите после проверки IP и порта."; }
        catch (Exception ex) { FormError.Text = SafeError(ex); }
        finally { _scanning = false; WizardForm.IsEnabled = true; ScanHostKeyButtonState(false); UpdateRunAvailability(); }
    }

    private void ScanHostKeyButtonState(bool busy)
    {
        foreach (var button in FindVisualChildren<Button>(this))
            if (Equals(button.Content, "Сканировать SSH host key…")) button.IsEnabled = !busy;
        RunProgress.IsIndeterminate = busy;
        if (busy) StageText.Text = "Получаем SSH host key с указанного адреса…";
    }

    private void BrowseInitialKey_Click(object sender, RoutedEventArgs e) => BrowseKey(InitialKeyPathBox);
    private void BrowseAdminKey_Click(object sender, RoutedEventArgs e) => BrowseKey(AdminKeyPathBox);

    private static void BrowseKey(TextBox target)
    {
        var dialog = new OpenFileDialog { Title = "Выберите SSH private key", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    private void UpdateRunAvailability()
    {
        if (RunButton is null || _running) return;
        if (PublicKeySelected()) FormError.Text = VpsKeyFiles.PublicKeySelectedMessage;
        else if (FormError.Text == VpsKeyFiles.PublicKeySelectedMessage) FormError.Text = "";
        var fullValidation = ValidateInputs(showErrors: false);
        var confirmations = FreshConfirmBox.IsChecked == true && FirewallConfirmBox.IsChecked == true && LetsEncryptConfirmBox.IsChecked == true;
        RunButton.IsEnabled = _historyReady && _viewModel.CanAdvance(3, fullValidation, _acceptedHostKey is not null && _viewModel.ConnectionReady, confirmations);
        PreviousStepButton.IsEnabled = _viewModel.Step > 0;
        var stepError = ValidateStep(_viewModel.Step);
        NextStepButton.IsEnabled = _viewModel.Step < 3 && _viewModel.CanAdvance(_viewModel.Step, stepError,
            _acceptedHostKey is not null && _trustedEndpoint is not null && StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, EndpointText()), _viewModel.ConnectionReady);
        RefreshReviewSummary();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        FormError.Text = "";
        if (_viewModel.Step != 3) { FormError.Text = "Перейдите к итоговой проверке перед запуском."; return; }
        if (!_viewModel.ConnectionReady || _acceptedHostKey is null || _trustedEndpoint is null || !StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, EndpointText()))
        {
            FormError.Text = "Перед установкой проверьте SSH/SFTP и сверьте fingerprint для текущего адреса.";
            return;
        }
        var validation = ValidateInputs(showErrors: true);
        if (validation is not null) { FormError.Text = validation; return; }
        if (_acceptedHostKey is null) { FormError.Text = "Сначала проверьте и явно примите fingerprint ssh-ed25519."; return; }
        if (FreshConfirmBox.IsChecked != true || FirewallConfirmBox.IsChecked != true || LetsEncryptConfirmBox.IsChecked != true)
        {
            FormError.Text = "Подтвердите свежесть VPS, firewall провайдера и условия Let’s Encrypt.";
            return;
        }
        if (_trustedEndpoint is null || !StringComparer.OrdinalIgnoreCase.Equals(_trustedEndpoint, EndpointText()))
        {
            FormError.Text = "IP или SSH-порт изменился после проверки. Повторите проверку fingerprint.";
            return;
        }
        ServerProfile profile;
        try { profile = BuildProfile(validate: true); }
        catch (ArgumentException ex) { FormError.Text = SafeError(ex); return; }
        if (_resumeProfile is null)
        {
            try
            {
                var existing = (await _profiles.LoadAsync(CancellationToken.None))
                    .FirstOrDefault(p => p.Id != _profileId && StringComparer.OrdinalIgnoreCase.Equals(p.Host, profile.Host));
                if (existing is not null)
                {
                    FormError.Text = $"IP уже связан с профилем «{existing.Name}». Откройте его и используйте «Продолжить настройку», если для него сохранён запуск.";
                    return;
                }
            }
            catch
            {
                FormError.Text = "Не удалось проверить локальный реестр перед запуском. VPS не изменён.";
                return;
            }
        }

        var secret = InitialSecretBox.Password;
        var method = SelectedTag(AuthMethodBox) == "PrivateKey" ? VpsAuthMethod.PrivateKey : VpsAuthMethod.Password;
        var resumeCanUseAdminKey = _resumeRun is not null && !string.IsNullOrWhiteSpace(_resumeProfile?.IdentityFile);
        if (!resumeCanUseAdminKey && method == VpsAuthMethod.Password && string.IsNullOrEmpty(secret)) { FormError.Text = "Введите начальный пароль SSH. Он останется только в памяти мастера."; return; }
        if (!resumeCanUseAdminKey && method == VpsAuthMethod.PrivateKey && string.IsNullOrWhiteSpace(InitialKeyPathBox.Text)) { FormError.Text = "Выберите приватный ключ начального SSH-доступа."; return; }

        var request = new VpsProvisionRequest
        {
            RunId = _runId,
            Profile = profile,
            InitialAccess = BuildInitialAccess(),
            HostKey = _acceptedHostKey,
            AdminIdentityFile = SelectedTag(AdminKeyModeBox) == "Existing" ? Path.GetFullPath(AdminKeyPathBox.Text.Trim()) : "",
            AdminKeyPassphrase = _resumeRun is not null && !string.IsNullOrWhiteSpace(_resumeProfile?.IdentityFile)
                ? (string.IsNullOrEmpty(AdminKeyPassphraseBox.Password) ? secret : AdminKeyPassphraseBox.Password)
                : SelectedTag(AdminKeyModeBox) == "Existing" ? AdminKeyPassphraseBox.Password : "",
            AcmeEmail = AcmeEmailBox.Text.Trim(),
            PanelAccess = SelectedTag(PanelAccessBox) == "Both" ? VpsPanelAccess.Both : VpsPanelAccess.Tunnel,
            PanelCidr = SelectedTag(PanelAccessBox) == "Both" ? PanelAllowedIps.Normalize(PanelCidrBox.Text) : "any",
            FreshServerConfirmed = FreshConfirmBox.IsChecked == true,
            LetsEncryptTermsAccepted = LetsEncryptConfirmBox.IsChecked == true,
            ProviderFirewallConfirmed = FirewallConfirmBox.IsChecked == true
        };
        InitialSecretBox.Clear();
        AdminKeyPassphraseBox.Clear();
        // This run ID stays bound to one server, including after a recoverable failure.
        IpBox.IsReadOnly = true;
        SshPortBox.IsReadOnly = true;
        AdminUserBox.IsReadOnly = true;
        InitialSecretBox.IsEnabled = false;
        AdminKeyPassphraseBox.IsEnabled = false;
        _running = true;
        RunButton.IsEnabled = false;
        RunButton.Content = "Настройка выполняется…";
        WizardForm.IsEnabled = false;
        RunProgress.IsIndeterminate = true;
        StageText.Text = "Проверяем свежесть системы и запускаем подготовку…";
        RunMessageText.Text = "Закрытие отменит локальное наблюдение. Уже запущенный удалённый этап может продолжиться; 20-минутный откат действует только после подготовки доступа.";
        ProgressDetails.IsExpanded = true;
        var progress = new Progress<VpsProvisionRun>(run =>
        {
            if (_detached || !IsLoaded) return;
            RenderProgress(run);
        });
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var runCts = _runCts;
        try
        {
            // Cancellation stops this client's observation/workflow. A systemd job already started remotely may continue.
            _activeRunTask = _provisioner.RunAsync(request, progress, runCts.Token);
            var result = await _activeRunTask;
            if (_detached) return;
            ProvisionedProfile = result.Profile;
            RenderProgress(result.Run);
            RunProgress.IsIndeterminate = false;
            _running = false;
            WizardForm.IsEnabled = true;
            InitialSecretBox.Clear();
            AdminKeyPassphraseBox.Clear();
            if (result.Run.Stage == VpsProvisionStage.Complete)
            {
                ProvisioningCompleted = true;
                DialogResult = true;
                return;
            }
            RunButton.Content = "Продолжить проверку и настройку";
            InitialSecretBox.IsEnabled = true;
            AdminKeyPassphraseBox.IsEnabled = true;
            FormError.Text = result.Run.Message;
        }
        catch (Exception ex)
        {
            if (_detached) return;
            _running = false;
            WizardForm.IsEnabled = true;
            RunProgress.IsIndeterminate = false;
            InitialSecretBox.Clear();
            AdminKeyPassphraseBox.Clear();
            RunButton.Content = "Возобновить проверку и настройку";
            InitialSecretBox.IsEnabled = true;
            AdminKeyPassphraseBox.IsEnabled = true;
            FormError.Text = SafeError(ex);
            StageText.Text = "Работа остановилась на безопасном этапе; удалённое состояние сохранено для проверки.";
            try { ProvisionedProfile = (await _profiles.LoadAsync(CancellationToken.None)).FirstOrDefault(p => p.Id == _profileId); }
            catch { /* Keep the safe error in the wizard; the main registry will refresh when it closes. */ }
        }
        finally
        {
            // Drop the wizard's managed references as soon as possible. Neither secret is persisted here.
            secret = "";
            if (ReferenceEquals(_runCts, runCts))
            {
                _runCts.Dispose();
                _runCts = null;
                _activeRunTask = null;
            }
        }
    }

    private string? ValidateInputs(bool showErrors)
    {
        string? error = null;
        string? allowedIpsError = null;
        if (SelectedTag(PanelAccessBox) == "Both")
        {
            try { PanelAllowedIps.Normalize(PanelCidrBox.Text); }
            catch (ArgumentException ex) { allowedIpsError = ex.Message; }
        }
        PanelAllowedIpsError.Text = allowedIpsError ?? "";
        var resumeCanUseAdminKey = _resumeRun is not null && !string.IsNullOrWhiteSpace(_resumeProfile?.IdentityFile);
        if (PublicKeySelected()) error = VpsKeyFiles.PublicKeySelectedMessage;
        else if (SelectedTag(PanelOpenModeBox) == "PublicHttps" && SelectedTag(PanelAccessBox) != "Both") error = "Для публичного HTTPS выберите публичный доступ к панели на VPS.";
        else if (!ProfileValidator.IsPublicIpLiteral(IpBox.Text.Trim())) error = "Введите публичный IPv4 или IPv6-адрес VPS.";
        else if (!TryPort(SshPortBox.Text, out _)) error = "SSH-порт должен быть числом от 1 до 65535.";
        else if (!TryPort(PanelPortBox.Text, out var panelPort)) error = "Порт панели должен быть числом от 1 до 65535.";
        else if (panelPort is 80 or 443 or 2096) error = "Порт панели не может совпадать с HTTP-01 (80), VPN (443) или подписками (2096).";
        else if (ParsePositiveInt(SshPortBox.Text, 22) is 80 or 443 or 2096) error = "SSH-порт не может совпадать с HTTP-01 (80), VPN (443) или подписками (2096).";
        else if (panelPort == ParsePositiveInt(SshPortBox.Text, 22)) error = "SSH-порт и порт панели должны различаться.";
        else if (!Regex.IsMatch(InitialUserBox.Text.Trim(), @"^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)) error = "Укажите корректное имя начального SSH-пользователя (по умолчанию root).";
        else if (!Regex.IsMatch(AdminUserBox.Text.Trim(), @"^[a-z][a-z0-9_-]{0,30}$", RegexOptions.CultureInvariant) || StringComparer.Ordinal.Equals(AdminUserBox.Text.Trim(), "root")) error = "Имя нового администратора должно быть корректным и отличаться от root.";
        else if (string.IsNullOrWhiteSpace(NameBox.Text)) error = "Укажите имя сервера.";
        else if (!Regex.IsMatch(RegionBox.Text.Trim(), @"^[a-zA-Z0-9][a-zA-Z0-9-]{0,31}$", RegexOptions.CultureInvariant)) error = "Укажите короткий регион из букв, цифр и дефиса.";
        else if (!int.TryParse(IndexBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 1 || index > 999) error = "Индекс узла должен быть числом от 1 до 999.";
        else if (!ProfileValidator.IsValidDomain(ZoneBox.Text.Trim())) error = "Укажите корректную DNS-зону.";
        else if (!ProfileValidator.IsValidDomain(PanelDomainBox.Text.Trim())) error = "Укажите корректное имя панели.";
        else if (SelectedTag(RoleBox) == "VpnNode" && !ProfileValidator.IsValidDomain(VpnDomainBox.Text.Trim())) error = "Укажите корректное VPN-имя.";
        else if (!string.IsNullOrWhiteSpace(DnsAddressBox.Text) && (!ProfileValidator.IsPublicIpLiteral(DnsAddressBox.Text.Trim()) || !IPAddress.TryParse(DnsAddressBox.Text.Trim(), out var dnsAddress) || !IPAddress.TryParse(IpBox.Text.Trim(), out var hostAddress) || !dnsAddress.Equals(hostAddress))) error = "DNS-адрес должен совпадать с IP VPS; оставьте поле пустым, чтобы использовать адрес узла.";
        else if (!Regex.IsMatch(AcmeEmailBox.Text.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant)) error = "Укажите корректный email для Let’s Encrypt.";
        else if (allowedIpsError is not null) error = allowedIpsError;
        else if (SelectedTag(AdminKeyModeBox) == "Existing" && string.IsNullOrWhiteSpace(AdminKeyPathBox.Text)) error = "Выберите существующий административный Ed25519-ключ.";
        else if (SelectedTag(AdminKeyModeBox) == "Existing" && !File.Exists(AdminKeyPathBox.Text.Trim())) error = "Файл административного ключа не найден.";
        else if (!resumeCanUseAdminKey && SelectedTag(AuthMethodBox) == "Password" && string.IsNullOrEmpty(InitialSecretBox.Password)) error = "Введите начальный SSH-пароль; он будет использован только в памяти мастера.";
        else if (!resumeCanUseAdminKey && SelectedTag(AuthMethodBox) == "PrivateKey" && string.IsNullOrWhiteSpace(InitialKeyPathBox.Text)) error = "Выберите приватный ключ начального SSH-доступа.";
        else if (!resumeCanUseAdminKey && SelectedTag(AuthMethodBox) == "PrivateKey" && !File.Exists(InitialKeyPathBox.Text.Trim())) error = "Файл начального приватного SSH-ключа не найден.";
        if (error is not null && showErrors)
        {
            if (error == allowedIpsError) { PanelCidrBox.BringIntoView(); PanelCidrBox.Focus(); }
            return error;
        }
        return error;
    }

    private bool PublicKeySelected() =>
        SelectedTag(AuthMethodBox) == "PrivateKey" && VpsKeyFiles.IsPublicKeyPath(InitialKeyPathBox.Text) ||
        SelectedTag(AdminKeyModeBox) == "Existing" && VpsKeyFiles.IsPublicKeyPath(AdminKeyPathBox.Text);

    private ServerProfile BuildProfile(bool validate)
    {
        if (!int.TryParse(IndexBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) throw new ArgumentException("Индекс узла должен быть числом.");
        if (!int.TryParse(SshPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var sshPort)) throw new ArgumentException("SSH-порт указан неверно.");
        if (!int.TryParse(PanelPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var panelPort)) throw new ArgumentException("Порт панели указан неверно.");
        var basis = _resumeProfile ?? new ServerProfile { Id = _profileId, LocalPort = 20530, VpnPort = 443, PanelBasePath = "/" };
        var profile = basis with
        {
            Name = NameBox.Text.Trim(),
            Role = SelectedTag(RoleBox) == "Control" ? ServerRole.Control : ServerRole.VpnNode,
            Region = RegionBox.Text.Trim().ToLowerInvariant(), NodeIndex = index, Host = IpBox.Text.Trim(),
            SshPort = sshPort, UserName = AdminUserBox.Text.Trim(),
            PanelDomain = PanelDomainBox.Text.Trim().ToLowerInvariant(), PanelPort = panelPort,
            PanelAccess = SelectedTag(PanelAccessBox) == "Both" ? VpsPanelAccess.Both : VpsPanelAccess.Tunnel,
            PanelOpenMode = SelectedTag(PanelOpenModeBox) == "PublicHttps" ? PanelOpenMode.PublicHttps : PanelOpenMode.Tunnel,
            PanelAllowedIpsText = PanelCidrBox.Text,
            IdentityFile = SelectedTag(AdminKeyModeBox) == "Existing" && !string.IsNullOrWhiteSpace(AdminKeyPathBox.Text) ? Path.GetFullPath(AdminKeyPathBox.Text.Trim()) : "",
            VpnDomain = VpnDomainBox.Text.Trim().ToLowerInvariant(),
            SubdomainMode = SelectedTag(DnsModeBox) == "Manual" ? SubdomainMode.Manual : SubdomainMode.Automatic,
            DnsZone = ZoneBox.Text.Trim().ToLowerInvariant(), DnsAddress = DnsAddressBox.Text.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (validate)
        {
            var errors = ProfileValidator.Validate(profile);
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        }
        return profile;
    }

    private async void StopObserving_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) { DialogResult = false; return; }
        if (_detachPending) return;
        var confirm = new VpsConfirmWindow("Прекратить наблюдение?", "Мастер закроется и прекратит выполнять следующие шаги. Уже начатый этап может продолжиться на VPS. Таймер восстановления доступа на 20 минут включается после подготовки доступа. Проверьте сохранённое состояние перед продолжением.", "Прекратить наблюдение") { Owner = this };
        if (confirm.ShowDialog() == true)
        {
            _detachPending = true;
            _detached = true;
            StopObservingButton.IsEnabled = false;
            RunMessageText.Text = "Отменяем локальное наблюдение и загружаем сохранённое состояние. Удалённая задача может продолжиться.";
            try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
            var activeRun = _activeRunTask;
            if (activeRun is not null)
            {
                try { await activeRun; }
                catch (OperationCanceledException) { }
                catch { /* The persisted run state is loaded below. */ }
            }
            try { ProvisionedProfile = (await _profiles.LoadAsync(CancellationToken.None)).FirstOrDefault(p => p.Id == _profileId) ?? ProvisionedProfile; }
            catch { /* The main window refreshes its local registry after this window closes. */ }
            DialogResult = false;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_scanning)
        {
            e.Cancel = true;
            FormError.Text = "Дождитесь окончания проверки SSH fingerprint, затем закройте мастер.";
            return;
        }
        if (!_running || _detached) return;
        e.Cancel = true;
        StopObserving_Click(this, new RoutedEventArgs());
    }

    private static string StageLabel(VpsProvisionStage stage) => stage switch
    {
        VpsProvisionStage.Created => "Создан локальный запуск",
        VpsProvisionStage.Preflight => "Проверка ОС и состояния VPS",
        VpsProvisionStage.PreparingDns => "Проверка DNS и создание записей",
        VpsProvisionStage.PreparingAccess => "Подготовка доступа и временный откат",
        VpsProvisionStage.VerifyingAdmin => "Проверка входа новым администратором",
        VpsProvisionStage.InstallingPanel => "Установка панели и TLS",
        VpsProvisionStage.RetrievingCredentials => "Получение учётных данных панели",
        VpsProvisionStage.Complete => "Настройка завершена",
        VpsProvisionStage.Failed => "Настройка остановлена с ошибкой",
        VpsProvisionStage.NeedsAttention => "Требуется внимание оператора",
        _ => "Неизвестное состояние запуска"
    };

    private static string SafeError(Exception ex) => ex switch
    {
        OperationCanceledException => "Операция превысила ограничение времени. Проверьте сохранённый статус и повторите после устранения причины.",
        TimeoutException => "Удалённый сервер не ответил вовремя. Проверьте адрес, SSH-порт и доступность VPS.",
        InvalidOperationException => ex.Message,
        ArgumentException => ex.Message,
        _ => "Не удалось выполнить действие. Проверьте подключение и безопасный статус задачи; секреты не сохранены в этом сообщении."
    };

    private string EndpointText() => $"{IpBox.Text.Trim()}:{SshPortBox.Text.Trim()}";
    private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    private static int ParsePositiveInt(string text, int fallback) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
    private static bool TryPort(string text, out int port) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}

internal sealed class HostKeyReviewWindow : Window
{
    private HostKeyCandidate? _result;
    private readonly CheckBox _verified;
    private readonly ListBox _candidates;

    private HostKeyReviewWindow(string ip, int port, IReadOnlyList<HostKeyCandidate> candidates)
    {
        Title = "Проверка SSH fingerprint"; Width = 720; Height = 480; MinWidth = 620; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = $"Проверьте ssh-ed25519 для {ip}:{port}", FontSize = 20, FontWeight = FontWeights.SemiBold };
        root.Children.Add(heading);
        var note = new TextBlock { Text = "Сравните fingerprint с независимой консолью или документацией VPS-провайдера. Сканирование само по себе не создаёт доверие. Не принимайте ключ, если отпечаток не совпадает.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10), Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Muted") };
        Grid.SetRow(note, 1); Grid.SetRowSpan(note, 1); root.Children.Add(note);
        _candidates = new ListBox { Margin = new Thickness(0, 55, 0, 8), DisplayMemberPath = "Fingerprint" };
        foreach (var candidate in candidates) _candidates.Items.Add(new CandidateRow(candidate));
        _candidates.SelectionChanged += (_, _) => { if (_candidates.SelectedItem is CandidateRow row) _result = row.Candidate; };
        Grid.SetRow(_candidates, 1); root.Children.Add(_candidates);
        _verified = new CheckBox { Content = "Я независимо сверил выбранный fingerprint в консоли VPS-провайдера.", Margin = new Thickness(0, 8, 0, 10) };
        Grid.SetRow(_verified, 2); root.Children.Add(_verified);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true };
        var accept = new Button { Content = "Сверить fingerprint и принять", MinWidth = 220, IsDefault = true, IsEnabled = false };
        _verified.Checked += (_, _) => accept.IsEnabled = _verified.IsChecked == true && _result is not null;
        _verified.Unchecked += (_, _) => accept.IsEnabled = false;
        _candidates.SelectionChanged += (_, _) => accept.IsEnabled = _verified.IsChecked == true && _result is not null;
        cancel.Click += (_, _) => Close();
        accept.Click += (_, _) => { if (_verified.IsChecked == true && _result is not null) DialogResult = true; };
        footer.Children.Add(cancel); footer.Children.Add(accept);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        _candidates.SelectedIndex = 0;
    }

    public static HostKeyCandidate? Choose(Window owner, string ip, int port, IReadOnlyList<HostKeyCandidate> candidates)
    {
        var dialog = new HostKeyReviewWindow(ip, port, candidates) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private sealed record CandidateRow(HostKeyCandidate Candidate)
    {
        public string Fingerprint => $"{Candidate.KeyType} · {Candidate.Fingerprint}";
    }
}

internal sealed class VpsConfirmWindow : Window
{
    public VpsConfirmWindow(string title, string message, string acceptText)
    {
        Title = "OpenViya Ops · подтверждение"; Width = 520; SizeToContent = SizeToContent.Height; MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20), LineHeight = 21 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Остаться", MinWidth = 94, IsCancel = true };
        var accept = new Button { Content = acceptText, MinWidth = 170, IsDefault = true, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Danger") };
        cancel.Click += (_, _) => Close(); accept.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); Content = panel;
    }
}
