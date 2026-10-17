using OpenViya.Ops.Core;
using OpenViya.Ops.Provisioning;
using OpenViya.Ops.Storage;
using OpenViya.Ops.Remote;
using System.Windows;

namespace OpenViya.Ops.Desktop;

public partial class MainWindow
{
    private void OpenMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (_operationBusy || _savingProfile || _selected is not { } profile) return;

        // Maintenance calls use the profile's existing administrative identity and the already-pinned
        // application known_hosts key. No discovery scan or first-use auto-trust is performed here.
        IVpsServerMaintenanceService service = _remoteClient is null
            ? new ServerMaintenanceService(_paths, new SshNetVpsSessionFactory(TimeSpan.FromMinutes(6)))
            : new RemoteVpsMaintenanceService(_remoteClient);
        var window = new ServerMaintenanceWindow(profile, service, _store, _serverStates,
            HasActiveMaintenanceTunnel, StopMaintenanceTunnelAsync, ReloadMaintenanceProfileAsync,
            serverManagedAccess: _remoteClient is not null)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private bool HasActiveMaintenanceTunnel(Guid serverId) => _tunnels.Snapshots.Any(snapshot =>
        snapshot.ServerId == serverId && snapshot.State is TunnelState.Starting or TunnelState.Verifying or TunnelState.Active or TunnelState.Degraded or TunnelState.Stopping);

    private Task StopMaintenanceTunnelAsync(Guid serverId) => _tunnels.StopAsync(serverId, _lifetime.Token);

    private async Task ReloadMaintenanceProfileAsync(Guid serverId)
    {
        _profiles = await _store.LoadAsync(_lifetime.Token);
        _selected = _profiles.FirstOrDefault(profile => profile.Id == serverId);
        RenderPage();
        RenderDetails();
        await RefreshObservedStateAsync(serverId);
    }
}
