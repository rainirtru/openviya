using OpenViya.Ops.Core;

namespace OpenViya.Ops.Desktop;

/// <summary>Coordinates the guarded panel-open path; UI choices and browser launch stay at the edge.</summary>
internal sealed class PanelOpeningService(
    ITunnelManager tunnels,
    IDiagnosticsService diagnostics,
    IHostsService hostsService,
    Func<ServerProfile, CancellationToken, Task<(ProbeResult Probe, DateTimeOffset? CertificateExpiresAt)>> checkPublicHttps)
{
    public async Task<PanelOpenResult> OpenAsync(
        ServerProfile profile,
        Func<HostsPlan, CancellationToken, Task<bool>> confirmApplyHosts,
        Func<HostsPlan, CancellationToken, Task<bool>> confirmRemoveOwnedHosts,
        Action<Uri> launchBrowser,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(profile.PanelOpenMode) || !Enum.IsDefined(profile.PanelAccess))
            return PanelOpenResult.Blocked("Панель не открыта: режим доступа в профиле неизвестен.");
        if (!ProfileValidator.IsValidDomain(profile.PanelDomain) || profile.PanelPort is < 1 or > 65535 || !ProfileValidator.IsValidBasePath(profile.PanelBasePath))
            return PanelOpenResult.Blocked("Панель не открыта: проверьте домен, порт и защищённый путь панели в профиле.");
        if (profile.PanelOpenMode == PanelOpenMode.PublicHttps && profile.PanelAccess != VpsPanelAccess.Both)
            return PanelOpenResult.Blocked("Публичное открытие не разрешено профилем: доступ панели не подтверждён как Both.");
        if (profile.Role == ServerRole.App)
            return PanelOpenResult.Blocked("Для профиля APP открытие панели недоступно.");

        var publicMode = profile.PanelOpenMode == PanelOpenMode.PublicHttps;
        var tunnel = tunnels.Snapshots.FirstOrDefault(x => x.ServerId == profile.Id);
        if (publicMode)
        {
            var hostsPlan = await hostsService.InspectAsync(profile.PanelDomain, cancellationToken);
            if (hostsPlan.Conflict)
                return PanelOpenResult.Blocked("Публичная панель не открыта: системный hosts содержит конфликт. Удалите или исправьте чужую запись вручную.");
            if (hostsPlan.AlreadyConfigured)
            {
                if (!hostsPlan.OwnedByApplication)
                    return PanelOpenResult.Blocked("Публичная панель не открыта: домен панели направлен на loopback системной записью, которая не помечена как принадлежащая OpenViya Ops.");
                if (!await confirmRemoveOwnedHosts(hostsPlan, cancellationToken))
                    return PanelOpenResult.Blocked("Публичная панель не открыта: локальная запись hosts оставлена по вашему решению.");
                await hostsService.RemoveAsync(profile.PanelDomain, cancellationToken);
                hostsPlan = await hostsService.InspectAsync(profile.PanelDomain, cancellationToken);
                if (hostsPlan.AlreadyConfigured || hostsPlan.Conflict)
                    return PanelOpenResult.Blocked("Публичная панель не открыта: после удаления своей записи hosts всё ещё содержит override. Проверьте файл вручную.");
            }

            var publicCheck = await checkPublicHttps(profile, cancellationToken);
            if (publicCheck.Probe.Status != ProbeStatus.Success)
                return PanelOpenResult.CheckedButBlocked(publicCheck.Probe, "Публичная панель не открыта: " + publicCheck.Probe.Message, certificateExpiresAt: publicCheck.CertificateExpiresAt);
            var publicUri = BuildUri(profile, profile.PanelPort);
            launchBrowser(publicUri);
            return PanelOpenResult.Opened(publicUri, publicCheck.Probe, tunnel, publicCheck.CertificateExpiresAt);
        }

        if (tunnel?.State != TunnelState.Active || tunnel.LocalPort is < 1)
        {
            tunnel = await tunnels.StartAsync(profile, cancellationToken);
            if (tunnel.State != TunnelState.Active || tunnel.LocalPort is < 1)
                return PanelOpenResult.Blocked($"Панель не открыта: туннель не перешёл в состояние Active. {tunnel.Message}", tunnel);
        }

        var hosts = await hostsService.InspectAsync(profile.PanelDomain, cancellationToken);
        if (hosts.Conflict)
            return PanelOpenResult.Blocked("Панель не открыта: в hosts найден конфликт. Чужая запись не изменена; проверьте её вручную.", tunnel);
        if (!hosts.AlreadyConfigured)
        {
            if (!await confirmApplyHosts(hosts, cancellationToken))
                return PanelOpenResult.Blocked("Панель не открыта: запись hosts не добавлена.", tunnel);
            await hostsService.ApplyAsync(profile.PanelDomain, cancellationToken);
            hosts = await hostsService.InspectAsync(profile.PanelDomain, cancellationToken);
            if (!hosts.AlreadyConfigured || hosts.Conflict)
                return PanelOpenResult.Blocked("Панель не открыта: после запроса hosts-запись не подтверждена. Проверьте состояние вручную.", tunnel);
        }

        var tls = await diagnostics.CheckPanelThroughTunnelAsync(profile, tunnel.LocalPort, cancellationToken);
        if (tls.Status != ProbeStatus.Success)
            return PanelOpenResult.CheckedButBlocked(tls, $"Панель не открыта: {tls.Message} При 404 проверьте защищённый путь панели в профиле.", tunnel);
        var uri = BuildUri(profile, tunnel.LocalPort);
        launchBrowser(uri);
        return PanelOpenResult.Opened(uri, tls, tunnel, null);
    }

    private static Uri BuildUri(ServerProfile profile, int port) =>
        new UriBuilder(Uri.UriSchemeHttps, profile.PanelDomain, port, profile.PanelBasePath).Uri;
}

internal sealed record PanelOpenResult(bool WasOpened, Uri? Uri, ProbeResult? Probe, TunnelSnapshot? Tunnel, string Message, DateTimeOffset? CertificateExpiresAt = null)
{
    public static PanelOpenResult Blocked(string message, TunnelSnapshot? tunnel = null) => new(false, null, null, tunnel, message);
    public static PanelOpenResult CheckedButBlocked(ProbeResult probe, string message, TunnelSnapshot? tunnel = null, DateTimeOffset? certificateExpiresAt = null) => new(false, null, probe, tunnel, message, certificateExpiresAt);
    public static PanelOpenResult Opened(Uri uri, ProbeResult probe, TunnelSnapshot? tunnel, DateTimeOffset? certificateExpiresAt) => new(true, uri, probe, tunnel, $"Панель открыта после успешной HTTPS-проверки: {uri.Host}:{uri.Port}.", certificateExpiresAt);
}
