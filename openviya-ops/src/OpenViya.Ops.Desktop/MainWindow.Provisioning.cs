using OpenViya.Ops.Core;
using OpenViya.Ops.Provisioning;
using OpenViya.Ops.Storage;
using OpenViya.Ops.Remote;
using System.Windows;

namespace OpenViya.Ops.Desktop;

public partial class MainWindow
{
    private IVpsProvisionStore _vpsProvisionStore = null!;
    private IVpsProvisioner _vpsProvisioner = null!;
    private CancellationTokenSource? _provisionLoadCts;
    private VpsProvisionRun? _selectedProvisionRun;

    private void InitializeProvisioning(IVpsProvisioner? provisioner, IVpsProvisionStore? provisionStore, bool smokeMode)
    {
        _vpsProvisionStore = provisionStore ?? (smokeMode ? new SmokeProvisionStore() : _remoteConfig is null ? new DpapiVpsProvisionStore(_paths) : new RemoteProvisionStore(_remoteClient!));
        _vpsProvisioner = provisioner ?? (smokeMode
            ? new SmokeProvisioner()
            : _remoteConfig is null
                ? new VpsProvisioner(_paths, _store, _vpsProvisionStore, new SshNetVpsSessionFactory(), new VpsAdminKeyService(_paths), _dnsProvider, _publicDns)
                : new RemoteVpsProvisioner(_remoteClient!));
    }

    private async void ProvisionNew_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _savingProfile) return;
        await OpenProvisionWizardAsync(null, null);
    }

    private async void ResumeProvision_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedProvisionRun is null || _operationBusy) return;
        if (_tunnels.Snapshots.Any(x => x.ServerId == _selected.Id && (x.State is TunnelState.Active or TunnelState.Verifying or TunnelState.Starting or TunnelState.Stopping)))
        {
            StatusText.Text = "Остановите туннель этого профиля, затем продолжите настройку.";
            return;
        }
        await OpenProvisionWizardAsync(_selected, _selectedProvisionRun);
    }

    private async Task OpenProvisionWizardAsync(ServerProfile? resumeProfile, VpsProvisionRun? resumeRun)
    {
        var wizard = new VpsProvisionWizardWindow(_store, _ssh, _vpsProvisioner, _vpsProvisionStore, resumeProfile, resumeRun) { Owner = this };
        wizard.ShowDialog();
        if (wizard.ProvisionedProfile is { } resultProfile)
        {
            _selected = resultProfile;
            await ReloadAsync(_lifetime.Token);
            _selected = _profiles.FirstOrDefault(p => p.Id == resultProfile.Id) ?? resultProfile;
            RenderPage();
            var row = _pageRows.FirstOrDefault(r => r.Id == _selected.Id);
            if (row is not null) ServerGrid.SelectedItem = row;
            RenderDetails();
            await RefreshProvisionStateAsync(resultProfile.Id);
            StatusText.Text = wizard.ProvisioningCompleted
                ? "Настройка свежего VPS завершена; локальный профиль обновлён."
                : "Настройка VPS приостановлена; состояние сохранено для продолжения.";
        }
        else if (resumeProfile is not null)
        {
            await RefreshProvisionStateAsync(resumeProfile.Id);
        }
    }

    private async void ViewProvisionLog_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedProvisionRun is not { } run || run.ServerId != _selected.Id) return;
        var selectedId = _selected.Id;
        ViewProvisionLogButton.IsEnabled = false;
        try
        {
            var history = await _vpsProvisionStore.LoadRunHistoryAsync(run.RunId, _lifetime.Token);
            if (_selected?.Id != selectedId) return;
            new VpsProgressWindow(history, run) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) { }
        catch { StatusText.Text = "Не удалось прочитать сохранённый журнал настройки VPS."; }
        finally { ViewProvisionLogButton.IsEnabled = true; }
    }

    private async void ViewCredentials_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _operationBusy) return;
        try
        {
            // Credentials are loaded only after the operator explicitly opens this owned window.
            var credentials = await _vpsProvisionStore.LoadCredentialsAsync(_selected.Id, _lifetime.Token);
            if (credentials is null)
            {
                StatusText.Text = "Для этого профиля ещё нет сохранённых данных панели.";
                return;
            }
            var dialog = new VpsCredentialsWindow(credentials) { Owner = this };
            dialog.ShowDialog();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = ex is InvalidOperationException ? ex.Message : "Не удалось открыть локальные учётные данные панели.";
        }
    }

    private async Task RefreshProvisionStateAsync(Guid serverId)
    {
        _provisionLoadCts?.Cancel();
        _provisionLoadCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _provisionLoadCts = cts;
        try
        {
            var latest = await _vpsProvisionStore.LoadLatestRunAsync(serverId, cts.Token);
            if (_selected?.Id != serverId || cts.IsCancellationRequested) return;
            _selectedProvisionRun = latest;
            RenderProvisionCard();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (_selected?.Id == serverId)
            {
                _selectedProvisionRun = null;
                RenderProvisionCard(_remoteClient is null
                    ? "Состояние настройки не удалось прочитать из локального хранилища."
                    : "Состояние серверной задачи временно недоступно. Проверьте соединение с LAN-сервером.");
            }
        }
    }

    private void RenderProvisionCard(string? readError = null)
    {
        if (_selected is null)
        {
            ProvisionCard.Visibility = Visibility.Collapsed;
            return;
        }
        var run = _selectedProvisionRun is { } selectedRun && selectedRun.ServerId == _selected.Id ? selectedRun : null;
        if (run is null && readError is null)
        {
            ProvisionCard.Visibility = Visibility.Collapsed;
            return;
        }
        ProvisionCard.Visibility = Visibility.Visible;
        ViewProvisionLogButton.Visibility = run is not null ? Visibility.Visible : Visibility.Collapsed;
        ProvisionStageText.Text = readError ?? (run is null ? "Запуск не найден." : ProvisionStageName(run.Stage));
        ProvisionMessageText.Text = readError is not null ? "" : run?.Message ?? "";
        var complete = run?.Stage == VpsProvisionStage.Complete;
        ResumeProvisionButton.Visibility = run is not null && !complete ? Visibility.Visible : Visibility.Collapsed;
        ViewCredentialsButton.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
        ResumeProvisionButton.IsEnabled = !_operationBusy && !_savingProfile;
        ViewCredentialsButton.IsEnabled = !_operationBusy;
    }

    private static string ProvisionStageName(VpsProvisionStage stage) => stage switch
    {
        VpsProvisionStage.Created => "Создан локальный запуск",
        VpsProvisionStage.Preflight => "Проверка ОС и состояния VPS",
        VpsProvisionStage.PreparingDns => "Проверка DNS-записей",
        VpsProvisionStage.PreparingAccess => "Подготовка SSH-доступа и отката",
        VpsProvisionStage.VerifyingAdmin => "Проверка нового администратора",
        VpsProvisionStage.InstallingPanel => "Установка 3x-ui и TLS",
        VpsProvisionStage.RetrievingCredentials => "Получение данных панели",
        VpsProvisionStage.Complete => "Настройка завершена",
        VpsProvisionStage.Failed => "Настройка остановлена с ошибкой",
        VpsProvisionStage.NeedsAttention => "Требуется внимание оператора",
        _ => "Неизвестное состояние запуска"
    };

    private sealed class SmokeProvisioner : IVpsProvisioner
    {
        public Task<VpsProvisionResult> RunAsync(VpsProvisionRequest request, IProgress<VpsProvisionRun>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Запуск удалённой настройки отключён в smoke-режиме.");
    }

    private sealed class SmokeProvisionStore : IVpsProvisionStore
    {
        private readonly Dictionary<Guid, VpsProvisionRun> _runs = [];
        private readonly Dictionary<Guid, VpsPanelCredentials> _credentials = [];
        public Task SaveRunAsync(VpsProvisionRun run, CancellationToken cancellationToken = default) { _runs[run.ServerId] = run; return Task.CompletedTask; }
        public Task<VpsProvisionRun?> LoadLatestRunAsync(Guid serverId, CancellationToken cancellationToken = default) { _runs.TryGetValue(serverId, out var run); return Task.FromResult(run); }
        public Task SaveCredentialsAsync(VpsPanelCredentials credentials, CancellationToken cancellationToken = default) { _credentials[credentials.ServerId] = credentials; return Task.CompletedTask; }
        public Task<VpsPanelCredentials?> LoadCredentialsAsync(Guid serverId, CancellationToken cancellationToken = default) { _credentials.TryGetValue(serverId, out var credentials); return Task.FromResult(credentials); }
    }
}
