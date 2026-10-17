using Microsoft.Win32;
using OpenViya.Ops.Core;
using OpenViya.Ops.Network;
using OpenViya.Ops.Ssh;
using OpenViya.Ops.Storage;
using OpenViya.Ops.Dns;
using OpenViya.Ops.Provisioning;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenViya.Ops.Desktop;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly IProfileStore _store;
    private readonly SshService _ssh;
    private readonly ITunnelManager _tunnels;
    private readonly IDiagnosticsService _diagnostics = new DiagnosticsService();
    private readonly IHostsService _hosts;
    private readonly IDnsTokenStore _dnsTokens;
    private readonly IDnsRecordStore _dnsRecords;
    private readonly IDnsProviderService _dnsProvider;
    private readonly IPublicDnsService _publicDns;
    private readonly IServerStateStore _serverStates;
    private readonly PublicPanelHttpsDiagnostics _publicPanelHttps = new();
    private PanelOpeningService _panelOpening = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<ProfileRow> _pageRows = [];
    private IReadOnlyList<ServerProfile> _profiles = [];
    private ServerProfile? _selected;
    private ServerProfile? _editing;
    private ServerObservedState? _selectedObservedState;
    private int _page;
    private bool _applyingDefaults;
    private bool _closing;
    private bool _operationBusy;
    private bool _savingProfile;
    private bool _panelDomainAuto;
    private bool _vpnDomainAuto;
    private bool _dnsApplyBusy;
    private DnsPlan? _displayedDnsPlan;
    private string _sortProperty = "Name";
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    public string? SmokePath { get; set; }
    public string? DeleteDataDirectoryOnSmokeClose { get; set; }

    public MainWindow(string dataDirectory, bool smokeMode = false, IVpsProvisioner? vpsProvisioner = null, IVpsProvisionStore? vpsProvisionStore = null)
    {
        InitializeComponent();
        SizeChanged += (_, _) => EditorPanel.MaxHeight = Math.Max(240, ActualHeight - 280);
        _paths = new AppPaths(dataDirectory);
        _store = new SqliteProfileStore(_paths);
        _ssh = new SshService(_paths);
        _tunnels = new TunnelManager(_ssh);
        _hosts = new HostsService(_paths);
        _dnsTokens = new DpapiDnsTokenStore(_paths);
        _dnsRecords = new SqliteDnsRecordStore(_paths);
        _dnsProvider = new TimewebDnsClient(_dnsTokens);
        _publicDns = new PublicDnsService();
        _serverStates = new SqliteServerStateStore(_paths);
        _panelOpening = new PanelOpeningService(_tunnels, _diagnostics, _hosts, async (profile, ct) =>
        {
            var result = await _publicPanelHttps.CheckAsync(profile, ct);
            return (result.Probe, result.CertificateExpiresAt);
        });
        InitializeProvisioning(vpsProvisioner, vpsProvisionStore, smokeMode);
        _tunnels.Changed += TunnelChanged;
        ServerGrid.ItemsSource = _pageRows;
        if (smokeMode) { Left = -10000; Top = -10000; ShowInTaskbar = false; }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Загружаем локальный реестр…";
            await ReloadAsync(_lifetime.Token);
            StatusText.Text = $"Реестр загружен · {DateTime.Now:HH:mm}";
            if (SmokePath is not null) await RunSmokeAsync(SmokePath);
        }
        catch (Exception smokeException)
        {
            if (SmokePath is { } smoke)
            {
                try { await File.WriteAllTextAsync(smoke + ".error.txt", $"WPF smoke verification failed ({smokeException.GetType().Name}): {smokeException.Message}"); } catch { }
                Environment.ExitCode = 1; _closing = true; await _tunnels.DisposeAsync(); if (_dnsProvider is IDisposable disposableDns) disposableDns.Dispose(); if (_publicDns is IDisposable disposablePublicDns) disposablePublicDns.Dispose();
                if (DeleteDataDirectoryOnSmokeClose is { } owned && Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
                Close(); Application.Current.Shutdown(1); return;
            }
            StatusText.Text = "Не удалось открыть локальный реестр. Проверьте доступ к папке данных."; ShowFatalStoreState();
        }
    }

    private async Task RunSmokeAsync(string output)
    {
        if (_profiles.Count == 0)
        {
            var sample = new ServerProfile { Name = "QA control", Host = "192.0.2.10", Role = ServerRole.Control, Region = "usa", NodeIndex = 1, PanelDomain = "panel-control.openviya.online", LocalPort = 20530 };
            await _store.SaveAsync(sample, _lifetime.Token);
            await ReloadAsync(_lifetime.Token);
        }
        // Exercise real native controls and a valid profile write against the caller-supplied data directory only.
        var qa = _profiles.First();
        await _store.SaveAsync(qa with { Name = qa.Name + " · smoke", UpdatedAt = DateTimeOffset.UtcNow }, _lifetime.Token);
        await ReloadAsync(_lifetime.Token);
        ServerGrid.SelectedIndex = 0;
        New_Click(this, new RoutedEventArgs());
        if (EditorPanel.Visibility != Visibility.Visible || ServerGrid.Items.Count == 0 || !SaveProfileButton.IsEnabled) throw new InvalidOperationException("WPF smoke verification did not reach an enabled profile editor and registry list.");
        CancelEdit_Click(this, new RoutedEventArgs());
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); RenderDetails(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await CaptureSmokeAsync(output);
        var dnsSettings = new DnsSettingsWindow(_dnsTokens, _dnsProvider) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = -9000, ShowInTaskbar = false };
        dnsSettings.Show();
        await Dispatcher.InvokeAsync(() => dnsSettings.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".settings.png"), dnsSettings);
        dnsSettings.Close();
        New_Click(this, new RoutedEventArgs());
        if (!SaveProfileButton.IsEnabled) throw new InvalidOperationException("New profile editor save action is not enabled.");
        NameBox.Text = "QA VPN node"; HostBox.Text = "192.0.2.11";
        DnsZoneBox.Text = "openviya.";
        await SaveProfileAsync();
        if (EditorPanel.Visibility != Visibility.Visible || !FormError.Text.Contains("DNS-зона", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid partial DNS zone should be rejected inline without closing the editor.");
        DnsZoneBox.Text = "openviya.online";
        await SaveProfileAsync();
        var created = _profiles.SingleOrDefault(p => p.Name == "QA VPN node") ?? throw new InvalidOperationException("Profile form did not create a record.");
        if (created.SubdomainMode != SubdomainMode.Automatic || created.DnsZone != "openviya.online" || created.PanelDomain != "panel-usa-1.openviya.online" || created.VpnDomain != "usa-1.openviya.online" || !DnsPlanText.Text.Contains("ожидает настройки Timeweb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic defaults or missing-token recovery state were not rendered.");
        Edit_Click(this, new RoutedEventArgs());
        NameBox.Text = "QA VPN node edited";
        await SaveProfileAsync();
        if (!_profiles.Any(p => p.Id == created.Id && p.Name == "QA VPN node edited") || DetailsPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Profile edit did not persist or restore details.");
        Edit_Click(this, new RoutedEventArgs());
        if (!SaveProfileButton.IsEnabled) throw new InvalidOperationException("Edit profile editor save action is not enabled.");
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".editor.png"));
        SubdomainModeBox.SelectedIndex = 1;
        PanelBox.Text = "manual-panel.openviya.online"; VpnBox.Text = "manual-vpn.openviya.online";
        if (!Equals(SaveProfileButton.Content, "Сохранить профиль")) throw new InvalidOperationException("Manual mode must be local-only in the profile editor.");
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".manual.png"));
        await SaveProfileAsync();
        if (!_profiles.Any(p => p.Id == created.Id && p.SubdomainMode == SubdomainMode.Manual && p.PanelDomain == "manual-panel.openviya.online" && p.VpnDomain == "manual-vpn.openviya.online") || !DnsPlanText.Text.Contains("Ручной режим", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Manual domains were not persisted as a local-only profile.");
        Edit_Click(this, new RoutedEventArgs());
        Width = 900; Height = 620;
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".compact.png"));
        CancelEdit_Click(this, new RoutedEventArgs());
        _selected = _profiles.FirstOrDefault(p => p.Id == created.Id);
        await ReloadAsync(_lifetime.Token);
        await _vpsProvisionStore.SaveRunAsync(new VpsProvisionRun(Guid.NewGuid(), created.Id, VpsProvisionStage.NeedsAttention, "Preview: operator action required", DateTimeOffset.UtcNow), _lifetime.Token);
        await RefreshProvisionStateAsync(created.Id);
        if (ProvisionCard.Visibility != Visibility.Visible || ResumeProvisionButton.Visibility != Visibility.Visible || !ResumeProvisionButton.IsEnabled)
            throw new InvalidOperationException("Provisioning attention state did not expose the resume action in the main profile card.");
        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ServerGrid.ActualHeight < 100) throw new InvalidOperationException($"Server list collapsed under the provisioning card ({ServerGrid.ActualHeight:0}px); at least 100px must remain visible.");
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".provision-attention.png"));
        await _vpsProvisionStore.SaveRunAsync(new VpsProvisionRun(Guid.NewGuid(), created.Id, VpsProvisionStage.Complete, "Preview: setup completed", DateTimeOffset.UtcNow), _lifetime.Token);
        await _vpsProvisionStore.SaveCredentialsAsync(new VpsPanelCredentials { ServerId = created.Id, UserName = "preview-user", Password = "PreviewOnly-not-a-real-password", PanelPath = "/preview-only", PanelDomain = created.PanelDomain, PanelPort = created.PanelPort }, _lifetime.Token);
        await RefreshProvisionStateAsync(created.Id);
        if (ViewCredentialsButton.Visibility != Visibility.Visible || !ViewCredentialsButton.IsEnabled)
            throw new InvalidOperationException("Completed provisioning state did not expose the explicit credentials action.");
        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ServerGrid.ActualHeight < 100) throw new InvalidOperationException($"Server list collapsed under the completed provisioning card ({ServerGrid.ActualHeight:0}px); at least 100px must remain visible.");
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".provision-complete.png"));
        var loadedPreviewCredentials = await _vpsProvisionStore.LoadCredentialsAsync(created.Id, _lifetime.Token);
        if (loadedPreviewCredentials?.UserName != "preview-user") throw new InvalidOperationException("Smoke credentials were not isolated in the in-memory provisioning store.");
        var wizard = new VpsProvisionWizardWindow(_store, _ssh, _vpsProvisioner, _vpsProvisionStore) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = -9000, ShowInTaskbar = false, ShowActivated = false, Width = 1280, Height = 900 };
        wizard.SetSmokePreview(keyMode: false);
        wizard.Show();
        await Dispatcher.InvokeAsync(wizard.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!wizard.SmokeRunDisabled) throw new InvalidOperationException("Provisioning must stay disabled until operator accepts a verified host fingerprint and all confirmations.");
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-wizard.png"), wizard);
        wizard.SetSmokePreview(keyMode: true);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-wizard-key.png"), wizard);
        wizard.SetSmokePreview(keyMode: false, manualDns: true);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-wizard-manual.png"), wizard);
        wizard.ShowSmokeStep(1);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-wizard-server.png"), wizard);
        wizard.ShowSmokeStep(3);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-wizard-review.png"), wizard);
        wizard.SetSmokeProgressPreview(failed: false);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-progress-running.png"), wizard);
        var previewHistory = wizard.SetSmokeProgressPreview(failed: true);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-progress-error.png"), wizard);
        wizard.Width = 900; wizard.Height = 680;
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-progress-compact.png"), wizard);
        wizard.ShowSmokeAllowedIps();
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-allowed-ips-compact.png"), wizard);
        wizard.Close();
        var historyWindow = new VpsProgressWindow(previewHistory, previewHistory[^1]) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = -9000, ShowInTaskbar = false, ShowActivated = false };
        historyWindow.Show();
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-progress-history.png"), historyWindow);
        historyWindow.Close();
        var maintenancePreview = ServerMaintenanceWindow.CreatePreviewWindow(created,
            new ServerMaintenanceService(_paths, new SshNetVpsSessionFactory()), _store, _serverStates);
        maintenancePreview.Owner = this;
        maintenancePreview.WindowStartupLocation = WindowStartupLocation.Manual;
        maintenancePreview.Left = -9000; maintenancePreview.Top = -9000;
        maintenancePreview.ShowActivated = false;
        maintenancePreview.Show();
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".maintenance.png"), maintenancePreview);
        maintenancePreview.Width = 900; maintenancePreview.Height = 680;
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".maintenance-compact.png"), maintenancePreview);
        maintenancePreview.Close();
        var boundedJournal = new ProvisionProgressLog();
        for (var index = 0; index < 205; index++) boundedJournal.Append(previewHistory[0] with { Message = "Preview step " + index });
        boundedJournal.Append(previewHistory[0] with { Message = "Preview step 204" });
        if (boundedJournal.EventCount != 200 || !boundedJournal.Text.StartsWith(previewHistory[0].UpdatedAt.ToLocalTime().ToString("HH:mm:ss") + "  ШАГ  Preview step 5", StringComparison.Ordinal))
            throw new InvalidOperationException("Provisioning journal did not bound or deduplicate its events.");
        var previewCredentials = new VpsPanelCredentials { ServerId = Guid.NewGuid(), UserName = "preview-user", Password = "PreviewOnly-not-a-real-password", PanelPath = "/preview-only", PanelDomain = "panel-preview.openviya.online", PanelPort = 38471 };
        var credentialsWindow = new VpsCredentialsWindow(previewCredentials) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = -9000, ShowInTaskbar = false, ShowActivated = false };
        credentialsWindow.Show();
        await Dispatcher.InvokeAsync(credentialsWindow.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-credentials.png"), credentialsWindow);
        credentialsWindow.SetSmokeReveal(true);
        await CaptureSmokeAsync(Path.ChangeExtension(output, ".vps-credentials-revealed.png"), credentialsWindow);
        credentialsWindow.Close();
        await _store.DeleteAsync(created.Id, _lifetime.Token);
        _selected = null; await ReloadAsync(_lifetime.Token);
        if (_profiles.Any(p => p.Id == created.Id)) throw new InvalidOperationException("Temporary record deletion failed.");
        _closing = true; await _tunnels.DisposeAsync(); if (_dnsProvider is IDisposable disposableDns) disposableDns.Dispose(); if (_publicDns is IDisposable disposablePublicDns) disposablePublicDns.Dispose(); Close();
        if (DeleteDataDirectoryOnSmokeClose is { } ownedDirectory && Directory.Exists(ownedDirectory)) Directory.Delete(ownedDirectory, recursive: true);
        Application.Current.Shutdown();
    }

    private async Task CaptureSmokeAsync(string output, Window? window = null)
    {
        var target = window ?? this;
        await Dispatcher.InvokeAsync(target.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var width = Math.Max(1, (int)target.ActualWidth); var height = Math.Max(1, (int)target.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using (var stream = File.Create(output)) encoder.Save(stream);
        var minimumWidth = ReferenceEquals(target, this) ? 900 : 560;
        var minimumHeight = ReferenceEquals(target, this) ? 620 : 480;
        if (bitmap.PixelWidth < minimumWidth || bitmap.PixelHeight < minimumHeight || !File.Exists(output) || ReferenceEquals(target, this) && ServerGrid.Items.Count == 0)
            throw new InvalidOperationException("WPF smoke verification did not render the expected registry controls.");
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var keep = _selected?.Id;
        _profiles = await _store.LoadAsync(ct);
        if (keep is { } id) _selected = _profiles.FirstOrDefault(x => x.Id == id);
        else if (_selected is not null) _selected = _profiles.FirstOrDefault(x => x.Id == _selected.Id);
        RenderPage(); RenderDetails();
    }

    private IEnumerable<ServerProfile> FilteredProfiles()
    {
        var q = SearchBox.Text?.Trim();
        var filtered = string.IsNullOrEmpty(q) ? _profiles : _profiles.Where(p => new[] { p.Name, p.Host, RoleName(p.Role), p.Region, p.PanelDomain, p.VpnDomain }.Any(s => s.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
        return _sortProperty switch
        {
            "Host" => Order(filtered, p => p.Host),
            "RoleLabel" => Order(filtered, p => RoleName(p.Role)),
            "RegionNode" => Order(filtered, p => $"{p.Region}-{p.NodeIndex:D3}"),
            "SshEndpoint" => Order(filtered, p => $"{p.Host}:{p.SshPort:D5}"),
            _ => Order(filtered, p => p.Name)
        };
    }

    private IOrderedEnumerable<ServerProfile> Order<T>(IEnumerable<ServerProfile> profiles, Func<ServerProfile, T> key) =>
        _sortDirection == ListSortDirection.Ascending ? profiles.OrderBy(key) : profiles.OrderByDescending(key);

    private void RenderPage()
    {
        var list = FilteredProfiles().ToList();
        const int size = 20;
        var pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)size));
        _page = Math.Clamp(_page, 0, pages - 1);
        _pageRows.Clear();
        foreach (var p in list.Skip(_page * size).Take(size)) _pageRows.Add(new ProfileRow(p));
        if (_selected is not null)
        {
            var selectedRow = _pageRows.FirstOrDefault(row => row.Id == _selected.Id);
            if (selectedRow is null) { _selected = null; ServerGrid.SelectedItem = null; }
            else ServerGrid.SelectedItem = selectedRow;
        }
        PageLabel.Text = list.Count == 0 ? "0 серверов" : $"Страница {_page + 1} / {pages} · {list.Count} серверов";
        SidebarCount.Text = $"{_profiles.Count} серверов в реестре";
        var empty = list.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ServerGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _profiles.Count == 0 ? "Реестр пуст" : "Совпадений нет";
        EmptyCopy.Text = _profiles.Count == 0 ? "Добавьте существующий VPS, чтобы сохранить его адрес и параметры доступа." : "Измените поисковый запрос или очистите поле поиска.";
        if (_selected is not null && !_pageRows.Any(x => x.Id == _selected.Id)) ServerGrid.SelectedItem = null;
    }

    private void RenderDetails()
    {
        var available = _selected is not null;
        DetailsPanel.Visibility = available && EditorPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (!available) { ProvisionCard.Visibility = Visibility.Collapsed; return; }
        RenderProvisionCard();
        DetailTitle.Text = _selected!.Name;
        DetailMeta.Text = $"{RoleName(_selected.Role)}   ·   {_selected.Host}:{_selected.SshPort}   ·   {_selected.Region}-{_selected.NodeIndex}   ·   HTTPS://{_selected.PanelDomain}:{_selected.PanelPort}";
        var tunnel = _tunnels.Snapshots.FirstOrDefault(x => x.ServerId == _selected.Id);
        var panelView = PanelOpeningViewModel.Create(_selected, tunnel, _selectedObservedState?.ServerId == _selected.Id ? _selectedObservedState : null);
        DesiredPanelAccessText.Text = $"Режим открытия: {panelView.DesiredAccess}";
        TunnelLabel.Text = panelView.TunnelState;
        SshObservationText.Text = panelView.SshObservation;
        PanelObservationText.Text = panelView.PanelObservation;
        CertificateObservationText.Text = panelView.CertificateObservation;
        ObservedCheckedAtText.Text = panelView.CheckedAt;
        var active = tunnel?.State is TunnelState.Active or TunnelState.Verifying;
        var busy = tunnel?.State is TunnelState.Starting or TunnelState.Stopping;
        var panelActionsEnabled = _selected.Role != ServerRole.App;
        foreach (var button in FindLogicalChildren<Button>(DetailsPanel))
        {
            if (ReferenceEquals(button, StartTunnelButton)) button.IsEnabled = panelActionsEnabled && !active && !busy && !_operationBusy;
            if (ReferenceEquals(button, StopTunnelButton)) button.IsEnabled = panelActionsEnabled && active && !busy && !_operationBusy;
            if (ReferenceEquals(button, OpenPanelButton) || ReferenceEquals(button, CheckTlsButton)) button.IsEnabled = panelActionsEnabled && !_operationBusy;
            if (ReferenceEquals(button, CheckSshButton) || ReferenceEquals(button, DiagnosticsButton) || Equals(button.Content, "Сканировать SSH host key") || Equals(button.Content, "Изменить") || Equals(button.Content, "Удалить профиль")) button.IsEnabled = !_operationBusy;
        }
        var profileInUse = busy || active;
        foreach (var button in FindLogicalChildren<Button>(DetailsPanel))
            if (Equals(button.Content, "Изменить") || Equals(button.Content, "Удалить профиль")) button.IsEnabled = !_operationBusy && !profileInUse;
        var dnsAuto = _selected.SubdomainMode == SubdomainMode.Automatic && _selected.Role != ServerRole.App;
        DnsPlanCard.Visibility = dnsAuto ? Visibility.Visible : Visibility.Collapsed;
        BuildDnsPlanButton.IsEnabled = dnsAuto && !_operationBusy;
        ApplyDnsButton.IsEnabled = false;
        PublicDnsButton.IsEnabled = !_operationBusy;
        if (dnsAuto && _displayedDnsPlan?.Profile.Id == _selected.Id)
            ApplyDnsButton.IsEnabled = !_operationBusy && !_dnsApplyBusy && _displayedDnsPlan.CanApply && _displayedDnsPlan.Items.Any(x => x.Action == DnsPlanAction.Create);
        if (_selected.SubdomainMode == SubdomainMode.Manual) DnsPlanCard.Visibility = Visibility.Visible;
        if (_selected.Role == ServerRole.App)
        {
            BuildDnsPlanButton.Visibility = Visibility.Collapsed;
            ApplyDnsButton.IsEnabled = false;
        }
        else BuildDnsPlanButton.Visibility = dnsAuto ? Visibility.Visible : Visibility.Collapsed;
        ApplyDnsButton.Visibility = dnsAuto ? Visibility.Visible : Visibility.Collapsed;
        if (_selected.Role == ServerRole.App) TunnelLabel.Text = "APP: туннель панели недоступен в MVP";
    }

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindLogicalChildren<T>(child)) yield return nested;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible; _page = 0; RenderPage(); RenderDetails(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void PrevPage_Click(object sender, RoutedEventArgs e) { _page--; RenderPage(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _page++; RenderPage(); }
    private void ServerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerGrid.SelectedItem is ProfileRow row && _selected?.Id != row.Id)
        {
            _selected = _profiles.FirstOrDefault(p => p.Id == row.Id);
            _selectedProvisionRun = null;
            _selectedObservedState = null;
            _displayedDnsPlan = null;
            DnsPlanText.Text = _selected is null ? "DNS-план не запрашивался."
                : _selected.Role == ServerRole.App ? "Для APP автоматические DNS-имена не назначаются. Настройте DNS на сайте провайдера."
                : _selected.SubdomainMode == SubdomainMode.Manual ? "Ручной режим: имена и записи настраиваются на сайте DNS-провайдера."
                : "Автоматические DNS-имена создаются при сохранении профиля. Проверка DNS доступна отдельно.";
        }
        RenderDetails();
        if (_selected is { } selectedProfile) { _ = RefreshProvisionStateAsync(selectedProfile.Id); _ = RefreshObservedStateAsync(selectedProfile.Id); }
    }

    private async Task RefreshObservedStateAsync(Guid serverId)
    {
        try
        {
            var state = await _serverStates.LoadAsync(serverId, _lifetime.Token);
            if (_selected?.Id != serverId) return;
            _selectedObservedState = state;
            RenderDetails();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (_selected?.Id == serverId)
            {
                _selectedObservedState = null;
                ObservedCheckedAtText.Text = "Сохранённые проверки недоступны; текущая сеть не проверялась.";
            }
        }
    }

    private async Task SaveObservedStateAsync(ServerProfile profile, ProbeStatus sshStatus, ProbeStatus panelStatus, string message, DateTimeOffset? certificateExpiresAt, CancellationToken cancellationToken)
    {
        try
        {
            var previous = await _serverStates.LoadAsync(profile.Id, cancellationToken);
            var state = new ServerObservedState(profile.Id, DateTimeOffset.UtcNow,
                sshStatus == ProbeStatus.Unknown ? previous?.SshStatus ?? ProbeStatus.Unknown : sshStatus,
                panelStatus == ProbeStatus.Unknown ? previous?.PanelStatus ?? ProbeStatus.Unknown : panelStatus,
                message,
                certificateExpiresAt ?? previous?.CertificateExpiresAt,
                previous?.PanelPort,
                previous?.PanelAccess,
                previous?.PanelAllowedIpsText);
            await _serverStates.SaveAsync(state, cancellationToken);
            if (_selected?.Id == profile.Id)
            {
                _selectedObservedState = state;
                RenderDetails();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            if (_selected?.Id == profile.Id)
                ObservedCheckedAtText.Text = "Проверка выполнена, но результат не сохранился в локальном состоянии.";
        }
    }

    private void DnsSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        var dialog = new DnsSettingsWindow(_dnsTokens, _dnsProvider) { Owner = this };
        dialog.ShowDialog();
    }
    private async void BuildDnsPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.SubdomainMode != SubdomainMode.Automatic || _selected.Role == ServerRole.App) return;
        var profile = _selected;
        await RunOperationAsync("Запрашиваем read-only план DNS у Timeweb…", async ct =>
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(40));
            if (string.IsNullOrEmpty(await _dnsTokens.LoadAsync(bounded.Token)))
            {
                _displayedDnsPlan = null;
                DnsPlanText.Text = "DNS ожидает настройки Timeweb. Настройте токен в разделе слева; профиль уже сохранён.";
                StatusText.Text = "Для проверки DNS сохраните токен Timeweb в настройках.";
                return;
            }
            var plan = await _dnsProvider.BuildPlanAsync(profile, cancellationToken: bounded.Token);
            if (_selected?.Id != profile.Id) return;
            _displayedDnsPlan = plan;
            DnsPlanText.Text = FormatDnsPlan(plan);
            StatusText.Text = plan.CanApply ? "План DNS готов. Создание записей выполняется при сохранении автоматического профиля." : "План содержит конфликт; записи не изменены.";
        });
    }
    private async void CheckPublicDns_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _operationBusy) return;
        var profile = _selected;
        await RunOperationAsync("Проверяем публичный DNS через Cloudflare…", async ct =>
        {
            var address = string.IsNullOrWhiteSpace(profile.DnsAddress) ? profile.Host : profile.DnsAddress;
            if (!ProfileValidator.IsPublicIpLiteral(address))
            {
                DnsPlanText.Text = "Проверка не запущена: укажите публичный IP в поле «DNS-адрес». Пустое поле использует IP узла; hostname как DNS-адрес не подходит.";
                StatusText.Text = "Для публичной DNS-проверки нужен литерал публичного IPv4 или IPv6.";
                return;
            }

            DnsPlan plan;
            if (profile.SubdomainMode == SubdomainMode.Automatic && profile.Role != ServerRole.App)
            {
                // Automatic names are canonicalized by Core and fetched from Timeweb only after this explicit check.
                plan = await _dnsProvider.BuildPlanAsync(profile, cancellationToken: ct);
            }
            else
            {
                // Manual mode is intentionally independent from Timeweb and its token.
                var names = new[] { profile.PanelDomain, profile.VpnDomain }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (names.Length == 0)
                {
                    DnsPlanText.Text = "Добавьте домен панели или VPN в ручном режиме, затем запустите публичную проверку.";
                    StatusText.Text = "Публичный DNS не проверен: у профиля нет DNS-имён.";
                    return;
                }
                var type = System.Net.IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "A" : "AAAA";
                var items = names.Select(name => new DnsPlanItem(name, type, address, DnsPlanAction.AlreadyConfigured, "Публичная проверка без изменений.")).ToArray();
                plan = new DnsPlan(profile, profile.DnsZone, address, 600, items, [], DateTimeOffset.UtcNow);
            }

            if (plan.Items.Count == 0)
            {
                DnsPlanText.Text = "Не удалось получить проверяемые имена DNS. Проверьте зону, роль и токен Timeweb.";
                StatusText.Text = "Публичная DNS-проверка не выполнена: план пуст.";
                return;
            }
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(45));
            var results = await _publicDns.CheckAsync(plan, bounded.Token);
            if (_selected?.Id != profile.Id) return;
            DnsPlanText.Text = "Cloudflare DNS-over-HTTPS · только чтение" + Environment.NewLine + string.Join(Environment.NewLine, results.Select(result => $"{StatusGlyph(result.Status)} {result.Name}: {result.Message}"));
            StatusText.Text = "Публичный DNS проверен через Cloudflare. Ответы зависят от опубликованных NS и DNS-кэша.";
        });
    }
    private async void ApplyDnsPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.SubdomainMode != SubdomainMode.Automatic || _selected.Role == ServerRole.App) return;
        var profile = _selected;
        if (!Confirm("Создать DNS-записи?", "Будут созданы только отсутствующие записи, соответствующие этому профилю. Конфликтные или существующие записи не перезаписываются.", "Создать записи")) return;
        await RunOperationAsync("Обновляем план DNS…", ct => ApplyFreshDnsPlanAsync(profile, ct, automaticSave: false));
    }
    private async Task ApplyFreshDnsPlanAsync(ServerProfile profile, CancellationToken ct, bool automaticSave)
    {
        _dnsApplyBusy = true;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        ct = bounded.Token;
        try
        {
            // Rebuild immediately before mutation so an old preview can never authorize stale record IDs.
            var plan = await _dnsProvider.BuildPlanAsync(profile, cancellationToken: ct);
            _displayedDnsPlan = plan;
            if (!plan.CanApply)
            {
                DnsPlanText.Text = FormatDnsPlan(plan);
                StatusText.Text = "Обнаружен конфликт DNS. Записи не изменены; устраните конфликт у провайдера.";
                return;
            }
            var configured = plan.Items.Where(x => x.Action == DnsPlanAction.AlreadyConfigured && x.RecordId is not null).ToArray();
            foreach (var item in configured)
                await _dnsRecords.SaveAsync(new DnsManagedRecord(profile.Id, item.Fqdn, item.RecordId!.Value, item.Address, DateTimeOffset.UtcNow), ct);
            if (!plan.Items.Any(x => x.Action == DnsPlanAction.Create))
            {
                DnsPlanText.Text = FormatDnsPlan(plan);
                StatusText.Text = "DNS уже настроен для профиля.";
                return;
            }
            var result = await _dnsProvider.ApplyAsync(plan, ct);
            DnsPlanText.Text = FormatDnsApply(plan, result);
            var byName = plan.Items.ToDictionary(x => x.Fqdn, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in result.Items)
                {
                    if (item.Status != ProbeStatus.Success || item.RecordId is not { } recordId || !byName.TryGetValue(item.Fqdn, out var planned)) continue;
                    await _dnsRecords.SaveAsync(new DnsManagedRecord(profile.Id, item.Fqdn, recordId, planned.Address, DateTimeOffset.UtcNow), ct);
                }
            }
            catch (Exception)
            {
                StatusText.Text = "DNS-операция завершена, но не удалось сохранить часть ID локально. Проверьте результат выше и сверьте его с Timeweb перед повтором.";
                if (automaticSave) return;
                return;
            }
            var expected = plan.Items.Where(x => x.Action == DnsPlanAction.Create).Select(x => x.Fqdn).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byOutcome = result.Items.Where(x => expected.Contains(x.Fqdn)).ToDictionary(x => x.Fqdn, StringComparer.OrdinalIgnoreCase);
            var allCreated = expected.All(name => byOutcome.TryGetValue(name, out var outcome) && outcome.Status == ProbeStatus.Success && outcome.RecordId is not null);
            StatusText.Text = allCreated ? "Все запрошенные DNS-записи созданы; ID сохранены локально." : "DNS-результат частичный или не подтверждён. Сверьте имена выше перед повтором.";
        }
        catch (Exception ex)
        {
            DnsPlanText.Text = "DNS не изменён или результат не подтверждён. Профиль сохранён; проверьте настройки и повторите действие.";
            StatusText.Text = automaticSave ? $"Профиль сохранён; DNS не подтверждён. {SafeOperationError(ex)}" : SafeOperationError(ex);
            if (automaticSave) return;
            throw;
        }
        finally { _dnsApplyBusy = false; RenderDetails(); }
    }
    private static string FormatDnsPlan(DnsPlan plan)
    {
        if (plan.Items.Count == 0) return "План пуст: этому профилю не назначены автоматические DNS-имена.";
        return string.Join(Environment.NewLine, plan.Items.Select(item =>
        {
            var action = item.Action switch { DnsPlanAction.Create => "Создать", DnsPlanAction.AlreadyConfigured => "Уже настроено", _ => "Конфликт · требуется ручная проверка" };
            return $"{item.Fqdn}  →  {item.Address}   ·   {action}" + (string.IsNullOrWhiteSpace(item.Message) ? "" : $" · {item.Message}");
        }));
    }
    private static string FormatDnsApply(DnsPlan plan, DnsApplyResult result)
    {
        var outcomes = result.Items.ToDictionary(x => x.Fqdn, StringComparer.OrdinalIgnoreCase);
        return string.Join(Environment.NewLine, plan.Items.Select(item =>
        {
            if (item.Action == DnsPlanAction.AlreadyConfigured) return $"{item.Fqdn}  ·  Уже настроено";
            if (!outcomes.TryGetValue(item.Fqdn, out var outcome)) return $"{item.Fqdn}  ·  Результат не получен";
            var state = outcome.Status switch { ProbeStatus.Success => "Создано", ProbeStatus.Warning => "Не подтверждено", ProbeStatus.Failure => "Ошибка создания", _ => "Ожидает проверки" };
            return $"{item.Fqdn}  ·  {state}" + (string.IsNullOrWhiteSpace(outcome.Message) ? "" : $" · {outcome.Message}");
        }));
    }
    private void ServerGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_sortProperty == e.Column.SortMemberPath) _sortDirection = _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        else { _sortProperty = e.Column.SortMemberPath; _sortDirection = ListSortDirection.Ascending; }
        foreach (var column in ServerGrid.Columns) column.SortDirection = column == e.Column ? _sortDirection : null;
        RenderPage();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy) return;
        _editing = new ServerProfile(); EditorTitle.Text = "Новый сервер"; LoadEditor(_editing); EditorPanel.Visibility = Visibility.Visible; FormError.Text = ""; DefaultsChanged(this, new RoutedEventArgs()); RenderDetails(); NameBox.Focus();
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var tunnel = _tunnels.Snapshots.FirstOrDefault(x => x.ServerId == _selected.Id);
        if (tunnel?.State is TunnelState.Active or TunnelState.Verifying or TunnelState.Starting or TunnelState.Stopping) { StatusText.Text = "Сначала остановите туннель, чтобы не менять endpoint активного профиля."; return; }
        _editing = _selected; EditorTitle.Text = "Изменить профиль"; LoadEditor(_editing); EditorPanel.Visibility = Visibility.Visible; FormError.Text = ""; RenderDetails(); NameBox.Focus();
    }
    private void CancelEdit_Click(object sender, RoutedEventArgs e) { EditorPanel.Visibility = Visibility.Collapsed; _editing = null; RenderDetails(); }
    private void LoadEditor(ServerProfile p)
    {
        var suggestions = ProfileDefaults.Apply(p with { PanelDomain = "", VpnDomain = "" });
        _panelDomainAuto = string.IsNullOrWhiteSpace(p.PanelDomain) || string.Equals(p.PanelDomain, suggestions.PanelDomain, StringComparison.OrdinalIgnoreCase);
        _vpnDomainAuto = string.IsNullOrWhiteSpace(p.VpnDomain) || string.Equals(p.VpnDomain, suggestions.VpnDomain, StringComparison.OrdinalIgnoreCase);
        _applyingDefaults = true;
        NameBox.Text = p.Name; HostBox.Text = p.Host; RegionBox.Text = p.Region; IndexBox.Text = p.NodeIndex.ToString(CultureInfo.InvariantCulture); UserBox.Text = p.UserName;
        KeyBox.Text = p.IdentityFile; SshPortBox.Text = p.SshPort.ToString(CultureInfo.InvariantCulture); PanelBox.Text = p.PanelDomain; PanelPortBox.Text = p.PanelPort.ToString(CultureInfo.InvariantCulture);
        BasePathBox.Password = p.PanelBasePath; LocalPortBox.Text = p.LocalPort?.ToString(CultureInfo.InvariantCulture) ?? ""; VpnBox.Text = p.VpnDomain; VpnPortBox.Text = p.VpnPort.ToString(CultureInfo.InvariantCulture);
        PanelOpenModeBox.SelectedIndex = p.PanelOpenMode == PanelOpenMode.PublicHttps ? 1 : 0;
        PanelAllowedIpsBox.Text = p.PanelAllowedIpsText;
        RefreshPanelModeControls();
        DnsZoneBox.Text = p.DnsZone; DnsAddressBox.Text = p.DnsAddress;
        RoleBox.SelectedIndex = p.Role switch { ServerRole.Control => 1, ServerRole.App => 2, _ => 0 };
        SubdomainModeBox.SelectedIndex = p.SubdomainMode == SubdomainMode.Manual ? 1 : 0;
        _applyingDefaults = false;
        RefreshPanelModeControls();
        SaveProfileButton.IsEnabled = true;
        UpdateDomainInputs();
    }
    private void DefaultsChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingDefaults || IndexBox is null || RoleBox is null || DnsZoneBox is null || RegionBox is null || PanelBox is null || VpnBox is null) return;
        if (ReferenceEquals(sender, PanelBox)) _panelDomainAuto = false;
        if (ReferenceEquals(sender, VpnBox)) _vpnDomainAuto = false;
        if (!int.TryParse(IndexBox.Text, out var index)) return;
        var role = (RoleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch { "Control" => ServerRole.Control, "App" => ServerRole.App, _ => ServerRole.VpnNode };
        var zone = DnsZoneBox.Text.Trim();
        if (!ProfileValidator.IsValidDomain(zone))
        {
            DnsModeHint.Text = "DNS-зона временно некорректна. Введите полное доменное имя; автоматические имена обновятся после исправления.";
            return;
        }
        var candidate = ProfileDefaults.Apply(new ServerProfile { Role = role, Region = RegionBox.Text, NodeIndex = index, DnsZone = zone });
        _applyingDefaults = true;
        if (_panelDomainAuto) PanelBox.Text = candidate.PanelDomain;
        if (_vpnDomainAuto) VpnBox.Text = candidate.VpnDomain;
        _applyingDefaults = false;
        RefreshPanelModeControls();
        UpdateDomainInputs();
    }
    private void SubdomainModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingDefaults || SubdomainModeBox is null) return;
        _panelDomainAuto = _vpnDomainAuto = (SubdomainModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Automatic";
        DefaultsChanged(sender, e);
        UpdateDomainInputs();
    }

    private void PanelOpenModeChanged(object sender, SelectionChangedEventArgs e) => RefreshPanelModeControls();

    private void RefreshPanelModeControls()
    {
        if (PanelOpenModeBox is null || PanelAllowedIpsBox is null) return;
        var app = (RoleBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "App";
        var publicMode = (PanelOpenModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "PublicHttps";
        PanelOpenModeBox.IsEnabled = !app;
        PanelAllowedIpsBox.IsEnabled = !app && publicMode;
    }
    private void UpdateDomainInputs()
    {
        if (SubdomainModeBox is null || PanelBox is null || VpnBox is null || RoleBox is null || SaveProfileButton is null || DnsModeHint is null) return;
        var automatic = (SubdomainModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Manual";
        PanelBox.IsReadOnly = automatic;
        VpnBox.IsReadOnly = automatic;
        if (automatic && (RoleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "App") { PanelBox.Text = ""; VpnBox.Text = ""; }
        var appRole = (RoleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "App";
        SaveProfileButton.Content = automatic && !appRole ? "Сохранить и создать DNS" : "Сохранить профиль";
        DnsModeHint.Text = automatic && !appRole
            ? "Имена пересчитываются при смене роли, региона, индекса и зоны. При сохранении профиль создаст только отсутствующие DNS-записи через Timeweb; если токен не настроен, профиль сохранится, а DNS останется ожидающим настройки."
            : automatic ? "Для APP автоматические DNS-имена не назначаются. Сохранение профиля не создаёт DNS-записи." : "Вручную задайте имена и создайте записи на сайте DNS-провайдера. Сохранение профиля не выполняет API-запросы и не меняет DNS.";
    }
    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите приватный SSH identity-файл", CheckFileExists = true, Multiselect = false, Filter = "SSH key files|*|All files|*.*" };
        if (dialog.ShowDialog(this) == true) KeyBox.Text = dialog.FileName;
    }
    private async void SaveProfile_Click(object sender, RoutedEventArgs e) => await SaveProfileAsync();
    private async Task SaveProfileAsync()
    {
        if (_editing is null) return;
        var role = (RoleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch { "Control" => ServerRole.Control, "App" => ServerRole.App, _ => ServerRole.VpnNode };
        var panelOpenMode = role == ServerRole.App || (PanelOpenModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "PublicHttps" ? PanelOpenMode.Tunnel : PanelOpenMode.PublicHttps;
        var panelAccess = panelOpenMode == PanelOpenMode.PublicHttps && (_profiles.All(p => p.Id != _editing.Id) || _editing.PanelAccess == VpsPanelAccess.Both || _selectedObservedState?.PanelAccess == VpsPanelAccess.Both)
            ? VpsPanelAccess.Both
            : _editing.PanelAccess;
        var rawProfile = _editing with { Name = NameBox.Text.Trim(), Host = HostBox.Text.Trim(), Role = role, Region = RegionBox.Text.Trim().ToLowerInvariant(), NodeIndex = ParseInt(IndexBox.Text, 1), UserName = UserBox.Text.Trim(), IdentityFile = KeyBox.Text.Trim(), SshPort = ParseInt(SshPortBox.Text, 22), PanelDomain = PanelBox.Text.Trim().ToLowerInvariant(), PanelPort = ParseInt(PanelPortBox.Text, 38471), PanelBasePath = BasePathBox.Password, PanelAccess = panelAccess, PanelOpenMode = panelOpenMode, PanelAllowedIpsText = PanelAllowedIpsBox.Text, LocalPort = string.IsNullOrWhiteSpace(LocalPortBox.Text) ? null : ParseNullableInt(LocalPortBox.Text), VpnDomain = VpnBox.Text.Trim().ToLowerInvariant(), VpnPort = ParseInt(VpnPortBox.Text, 443), SubdomainMode = (SubdomainModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Manual" ? SubdomainMode.Manual : SubdomainMode.Automatic, DnsZone = DnsZoneBox.Text.Trim().ToLowerInvariant(), DnsAddress = DnsAddressBox.Text.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
        var errors = ProfileValidator.Validate(rawProfile);
        if (errors.Count > 0) { FormError.Text = string.Join(" ", errors); return; }
        ServerProfile p;
        try { p = ProfileDefaults.Apply(rawProfile); }
        catch (ArgumentException ex) { FormError.Text = ex.Message; return; }
        PanelBox.Text = p.PanelDomain; VpnBox.Text = p.VpnDomain;
        errors = ProfileValidator.Validate(p);
        if (errors.Count > 0) { FormError.Text = string.Join(" ", errors); return; }
        if (_savingProfile) return;
        _savingProfile = true;
        SaveProfileButton.IsEnabled = false;
        try
        {
            await _store.SaveAsync(p, _lifetime.Token); _selected = p; _displayedDnsPlan = null; await ReloadAsync(_lifetime.Token);
            EditorPanel.Visibility = Visibility.Collapsed; _editing = null; RenderDetails(); StatusText.Text = "Профиль сохранён в локальном реестре.";
            if (p.SubdomainMode == SubdomainMode.Automatic && p.Role != ServerRole.App)
            {
                await RunOperationAsync("Профиль сохранён · готовим DNS-план…", async ct =>
                {
                    string? token;
                    try { token = await _dnsTokens.LoadAsync(ct); }
                    catch (Exception) { _displayedDnsPlan = null; DnsPlanText.Text = "Не удалось прочитать токен Timeweb. Профиль сохранён; проверьте локальные настройки."; StatusText.Text = "Профиль сохранён. Исправьте состояние токена и повторите сохранение."; return; }
                    if (string.IsNullOrEmpty(token)) { _displayedDnsPlan = null; DnsPlanText.Text = "DNS ожидает настройки Timeweb. Профиль уже сохранён; настройте токен в разделе слева."; StatusText.Text = "Профиль сохранён. DNS ожидает настройки Timeweb; профиль не потерян."; return; }
                    await ApplyFreshDnsPlanAsync(p, ct, automaticSave: true);
                });
            }
            else if (p.SubdomainMode == SubdomainMode.Manual) DnsPlanText.Text = "Ручной режим: DNS настраивается на сайте провайдера. Приложение не изменяет записи.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FormError.Text = SafeOperationError(ex); }
        finally { _savingProfile = false; SaveProfileButton.IsEnabled = EditorPanel.Visibility == Visibility.Visible; }
    }
    private static int ParseInt(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
    private static int? ParseNullableInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var snap = _tunnels.Snapshots.FirstOrDefault(t => t.ServerId == _selected.Id);
        if (snap?.State is TunnelState.Active or TunnelState.Starting or TunnelState.Stopping)
        { StatusText.Text = "Сначала остановите активный туннель; удаление профиля отключено."; return; }
        if (!Confirm("Удалить профиль?", $"Профиль «{_selected.Name}» будет удалён из локального реестра. На сервере ничего не изменится.", "Удалить профиль")) return;
        try { await _store.DeleteAsync(_selected.Id, _lifetime.Token); _selected = null; await ReloadAsync(_lifetime.Token); StatusText.Text = "Профиль удалён из локального реестра."; }
        catch (Exception ex) { StatusText.Text = SafeOperationError(ex); }
    }

    private async void ScanKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        await RunOperationAsync("Сканируем публичные host key через ssh-keyscan…", async ct =>
        {
            var keys = await _ssh.ScanHostKeysAsync(profile, ct);
            var chosen = TrustKeyDialog.Choose(this, profile, keys);
            if (chosen is null) { StatusText.Text = "Trust не изменён: ключ не принят."; return; }
            if (!Confirm("Подтвердить SSH host key?", $"Сравните fingerprint {chosen.Fingerprint} с доверенным источником, например консолью провайдера или заранее подтверждённым отпечатком. Сканирование само по себе не подтверждает личность сервера. Принять этот ключ для профиля «{profile.Name}»?", "Принять fingerprint")) return;
            await _ssh.TrustHostKeyAsync(profile, chosen, ct); StatusText.Text = $"Fingerprint {chosen.Fingerprint} принят для этого профиля.";
        });
    }
    private async void CheckSsh_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        await RunOperationAsync("Проверяем SSH host key и аутентификацию…", async ct => { var p = await _ssh.CheckConnectionAsync(profile, ct); await SaveObservedStateAsync(profile, p.Status, ProbeStatus.Unknown, p.Message, null, ct); StatusText.Text = $"{p.Name}: {p.Message}"; });
    }
    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        await RunOperationAsync("Открываем внешний Windows Terminal…", async ct => { await _ssh.OpenTerminalAsync(profile, ct); StatusText.Text = "SSH-терминал открыт. Сеанс выполняется во внешнем Windows Terminal."; });
    }
    private async void StartTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        await RunOperationAsync("Запускаем принадлежащий приложению SSH-туннель…", async ct => { var s = await _tunnels.StartAsync(profile, ct); StatusText.Text = $"{s.State}: {s.Message}"; RenderDetails(); });
    }
    private async void StopTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var id = _selected.Id;
        await RunOperationAsync("Останавливаем SSH-туннель приложения…", async ct => { await _tunnels.StopAsync(id, ct); RenderDetails(); StatusText.Text = "SSH-туннель остановлен."; });
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        await RunOperationAsync("Проверяем DNS и TCP SSH…", async ct => { var results = await _diagnostics.CheckServerAsync(_selected, ct); ShowResults("Диагностика сервера", results.Select(x => $"{StatusGlyph(x.Status)}  {x.Name}: {x.Message}")); });
    }
    private async void CheckTls_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        if (profile.PanelOpenMode == PanelOpenMode.PublicHttps)
        {
            await RunOperationAsync("Проверяем публичный HTTPS панели…", async ct =>
            {
                var result = await _publicPanelHttps.CheckAsync(profile, ct);
                await SaveObservedStateAsync(profile, ProbeStatus.Unknown, result.Probe.Status, result.Probe.Message, result.CertificateExpiresAt, ct);
                StatusText.Text = $"{result.Probe.Name}: {result.Probe.Message}";
            });
            return;
        }
        var tunnel = _tunnels.Snapshots.FirstOrDefault(x => x.ServerId == profile.Id);
        if (tunnel?.State != TunnelState.Active || tunnel.LocalPort is < 1) { StatusText.Text = "Сначала запустите туннель и дождитесь состояния Active."; return; }
        await RunOperationAsync("Проверяем TLS панели через туннель…", async ct =>
        {
            var probe = await _diagnostics.CheckPanelThroughTunnelAsync(profile, tunnel.LocalPort, ct);
            await SaveObservedStateAsync(profile, ProbeStatus.Unknown, probe.Status, probe.Message, null, ct);
            StatusText.Text = $"{probe.Name}: {probe.Message}";
        });
    }
    private async void OpenPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        await RunOperationAsync("Подготавливаем защищённое открытие панели…", async ct =>
        {
            var result = await _panelOpening.OpenAsync(profile,
                (plan, _) => Task.FromResult(Confirm("Добавить локальную запись hosts?", $"Добавить принадлежащую OpenViya Ops запись 127.0.0.1 → {plan.Domain}. Для изменения Windows запросит повышение прав (UAC). Конфликтующие строки останутся без изменений.", "Применить hosts")),
                (plan, _) => Task.FromResult(Confirm("Убрать свою запись hosts?", $"Удалить только точную помеченную запись OpenViya Ops для {plan.Domain}, затем проверить публичный HTTPS. Windows запросит повышение прав (UAC).", "Удалить запись и продолжить")),
                uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
            if (result.Probe is { } probe)
                await SaveObservedStateAsync(profile, ProbeStatus.Unknown, probe.Status, probe.Message, result.CertificateExpiresAt, ct);
            RenderDetails();
            StatusText.Text = result.Message;
        });
    }
    private async void InspectHosts_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        await RunOperationAsync("Проверяем запись в системном hosts…", async ct => { var plan = await _hosts.InspectAsync(_selected.PanelDomain, ct); StatusText.Text = $"{plan.Message} Адрес: {plan.Address}."; });
    }
    private async void ApplyHosts_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        if (!Confirm("Изменить системный hosts?", $"Добавить принадлежащую OpenViya Ops запись 127.0.0.1 → {profile.PanelDomain}. Windows запросит повышение прав (UAC). Существующие чужие/conflicting строки не должны перезаписываться.", "Применить hosts")) return;
        await RunOperationAsync("HostsHelper запросит повышение прав Windows…", async ct => { await _hosts.ApplyAsync(profile.PanelDomain, ct); var p = await _hosts.InspectAsync(profile.PanelDomain, ct); StatusText.Text = p.Message; });
    }
    private async void RemoveHosts_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var profile = _selected;
        if (_profiles.Any(p => p.Id != profile.Id && string.Equals(p.PanelDomain, profile.PanelDomain, StringComparison.OrdinalIgnoreCase) && _tunnels.Snapshots.Any(t => t.ServerId == p.Id && (t.State is TunnelState.Active or TunnelState.Starting or TunnelState.Verifying or TunnelState.Stopping)))) { StatusText.Text = "Запись не удалена: другой активный профиль использует этот же домен панели."; return; }
        if (!Confirm("Удалить свою hosts-запись?", $"Удаляется только точная помеченная запись OpenViya Ops для {profile.PanelDomain}. Windows запросит повышение прав.", "Удалить свою запись")) return;
        await RunOperationAsync("HostsHelper запросит повышение прав Windows…", async ct => { await _hosts.RemoveAsync(profile.PanelDomain, ct); StatusText.Text = "Запрошено удаление только точной записи OpenViya Ops."; });
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_tunnels.Snapshots.Any(t => t.State is TunnelState.Active or TunnelState.Starting or TunnelState.Verifying or TunnelState.Stopping)) { StatusText.Text = "Импорт недоступен, пока работает туннель. Остановите туннели, чтобы не менять endpoint активного профиля."; return; }
        var d = new OpenFileDialog { Title = "Импорт профилей OpenViya Ops", Filter = "JSON profiles|*.json|All files|*.*", CheckFileExists = true };
        if (d.ShowDialog(this) != true) return;
        await RunOperationAsync("Импортируем профили…", async ct => { var count = await _store.ImportAsync(d.FileName, ct); await ReloadAsync(ct); StatusText.Text = $"Импортировано профилей: {count}. Пути ключей и защищённый путь панели не входят в экспорт."; });
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Title = "Экспорт профилей OpenViya Ops", Filter = "JSON profiles|*.json", DefaultExt = ".json", AddExtension = true, FileName = "openviya-ops-profiles.json" };
        if (d.ShowDialog(this) != true) return;
        await RunOperationAsync("Экспортируем профили…", async ct => { await _store.ExportAsync(d.FileName, ct); StatusText.Text = "Профили экспортированы. Приватные ключи и защищённый путь панели не экспортируются."; });
    }

    private async Task RunOperationAsync(string pending, Func<CancellationToken, Task> action)
    {
        if (_operationBusy) return;
        _operationBusy = true;
        ServerGrid.IsEnabled = false;
        DnsSettingsButton.IsEnabled = false;
        RenderDetails();
        StatusText.Text = pending;
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { StatusText.Text = "Операция отменена."; }
        catch (Exception ex) { StatusText.Text = SafeOperationError(ex); }
        finally { _operationBusy = false; ServerGrid.IsEnabled = true; DnsSettingsButton.IsEnabled = true; RenderDetails(); }
    }
    private static string SafeOperationError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Операция не выполнена: нет необходимых прав доступа.",
        IOException => "Операция не выполнена из-за ошибки доступа к файлу или сети.",
        InvalidOperationException => ex.Message,
        ArgumentException => ex.Message,
        _ => "Операция не выполнена. Проверьте параметры и повторите действие. Подробности исключения не отображаются."
    };
    private void ShowResults(string title, IEnumerable<string> lines) => OwnedDialog.Show(this, title, string.Join(Environment.NewLine + Environment.NewLine, lines));
    private bool Confirm(string title, string text, string action) => OwnedDialog.Confirm(this, title, text, action);
    private void ShowFatalStoreState() { EmptyTitle.Text = "Реестр недоступен"; EmptyCopy.Text = "Проверьте доступ к локальной папке данных и перезапустите приложение."; EmptyState.Visibility = Visibility.Visible; ServerGrid.Visibility = Visibility.Collapsed; }
    private static string StatusGlyph(ProbeStatus s) => s switch { ProbeStatus.Success => "●", ProbeStatus.Warning => "▲", ProbeStatus.Failure => "×", _ => "○" };
    private static string RoleName(ServerRole r) => r switch { ServerRole.Control => "CONTROL", ServerRole.App => "APP", _ => "VPN_NODE" };
    private void TunnelChanged(object? sender, TunnelSnapshot snapshot) => Dispatcher.BeginInvoke(() => { if (_selected?.Id == snapshot.ServerId) RenderDetails(); });

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _provisionLoadCts?.Cancel();
        _closing = true; _lifetime.Cancel(); if (_tunnels is not null) _tunnels.Changed -= TunnelChanged;
        _ = CloseAfterCleanupAsync();
    }

    private async Task CloseAfterCleanupAsync()
    {
        if (_tunnels is not null) await _tunnels.DisposeAsync();
        if (_dnsProvider is IDisposable disposableDns) disposableDns.Dispose();
        if (_publicDns is IDisposable disposablePublicDns) disposablePublicDns.Dispose();
        Close();
    }

    private sealed record ProfileRow(Guid Id, string Name, string Host, string RoleLabel, string RegionNode, string SshEndpoint)
    { public ProfileRow(ServerProfile p) : this(p.Id, p.Name, p.Host, RoleName(p.Role), $"{p.Region}-{p.NodeIndex}", $"{p.Host}:{p.SshPort}") { } }
}

internal static class OwnedDialog
{
    public static bool Confirm(Window owner, string title, string body, string action)
    {
        var dlg = Build(owner, title, body); var result = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 92 };
        var accept = new Button { Content = action, IsDefault = true, MinWidth = 130, Background = new SolidColorBrush(Color.FromRgb(57, 83, 93)), BorderBrush = new SolidColorBrush(Color.FromRgb(92, 134, 150)), Margin = new Thickness(8,0,0,0) };
        cancel.Click += (_, _) => dlg.Close(); accept.Click += (_, _) => { result = true; dlg.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(accept); ((StackPanel)dlg.Content).Children.Add(buttons); dlg.Loaded += (_, _) => cancel.Focus(); dlg.ShowDialog(); return result;
    }
    public static void Show(Window owner, string title, string body) { var dlg = Build(owner, title, body); var ok = new Button { Content = "Закрыть", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 92, Margin = new Thickness(0,18,0,0) }; ok.Click += (_, _) => dlg.Close(); ((StackPanel)dlg.Content).Children.Add(ok); dlg.ShowDialog(); }
    private static Window Build(Window owner, string title, string body)
    {
        var dlg = new Window { Owner = owner, Title = title, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = owner.Background, Foreground = owner.Foreground, FontFamily = owner.FontFamily, FontSize = owner.FontSize };
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = body, Foreground = new SolidColorBrush(Color.FromRgb(190, 205, 211)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), LineHeight = 21 }); dlg.Content = stack; return dlg;
    }
}

internal sealed class TrustKeyDialog
{
    public static HostKeyCandidate? Choose(Window owner, ServerProfile profile, IReadOnlyList<HostKeyCandidate> keys)
    {
        var dlg = new Window { Owner = owner, Title = "Кандидаты SSH host key", Width = 700, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = owner.Background, Foreground = owner.Foreground, FontFamily = owner.FontFamily, ShowInTaskbar = false };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new TextBlock { Text = $"Узел {profile.Host}:{profile.SshPort} · профиль {profile.Name}\nssh-keyscan сообщает публичный ключ, но не подтверждает личность узла. Перед принятием сверьте fingerprint с доверенным независимым источником.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(216, 180, 107)), Margin = new Thickness(0,0,0,14) };
        Grid.SetRow(header, 0); root.Children.Add(header);
        var list = new ListBox { Background = new SolidColorBrush(Color.FromRgb(30,45,56)), Foreground = owner.Foreground, BorderBrush = new SolidColorBrush(Color.FromRgb(52,70,83)), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        foreach (var key in keys) list.Items.Add(new ListBoxItem { Content = $"{key.KeyType}\n{key.Fingerprint}", Tag = key, Padding = new Thickness(10), Margin = new Thickness(2) });
        list.SelectedIndex = 0; Grid.SetRow(list, 1); root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; var choose = new Button { Content = "Сверить fingerprint…", IsDefault = true, Margin = new Thickness(8,0,0,0), Background = new SolidColorBrush(Color.FromRgb(57,83,93)) };
        HostKeyCandidate? result = null;
        cancel.Click += (_, _) => dlg.Close(); choose.Click += (_, _) => { if (list.SelectedItem is ListBoxItem item) { result = (HostKeyCandidate)item.Tag; dlg.Close(); } };
        buttons.Children.Add(cancel); buttons.Children.Add(choose); Grid.SetRow(buttons, 2); buttons.Margin = new Thickness(0,14,0,0); root.Children.Add(buttons); dlg.Content = root; dlg.Loaded += (_, _) => list.Focus(); dlg.ShowDialog(); return result;
    }
}
