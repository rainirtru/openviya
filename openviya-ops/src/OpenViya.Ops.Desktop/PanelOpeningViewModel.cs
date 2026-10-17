using OpenViya.Ops.Core;

namespace OpenViya.Ops.Desktop;

internal sealed record PanelOpeningViewModel(
    string DesiredAccess,
    string TunnelState,
    string SshObservation,
    string PanelObservation,
    string CertificateObservation,
    string CheckedAt)
{
    public static PanelOpeningViewModel Create(ServerProfile profile, TunnelSnapshot? tunnel, ServerObservedState? observed)
    {
        var access = profile.PanelAccess == VpsPanelAccess.Both ? "туннель и публичный доступ" : "только SSH-туннель";
        var desired = profile.PanelOpenMode == PanelOpenMode.PublicHttps
            ? $"Открывать по публичному HTTPS · доступ по профилю: {access}"
            : $"Открывать через SSH-туннель · доступ по профилю: {access}";
        var tunnelText = tunnel is null
            ? "Туннель: не запускался в этом сеансе"
            : $"Туннель: {TunnelName(tunnel.State)} · {tunnel.Message}";
        if (observed is null)
            return new(desired, tunnelText, "SSH: ещё не проверялся", "Панель: ещё не проверялась", "Сертификат: срок не проверялся", "Наблюдения ещё не сохранены");
        var ssh = $"SSH · последний сохранённый результат: {StatusName(observed.SshStatus)}";
        var panel = $"HTTPS · последний сохранённый результат: {StatusName(observed.PanelStatus)}";
        var cert = observed.CertificateExpiresAt is { } expiry
            ? $"Сертификат · последний известный срок: {expiry.ToLocalTime():g}"
            : "Сертификат: срок ещё не зафиксирован";
        var differs = observed.PanelPort is { } actualPort && actualPort != profile.PanelPort ||
            observed.PanelAccess is { } actualAccess && actualAccess != profile.PanelAccess;
        var actual = observed.PanelPort is { } port ? $" · проверенный порт панели: {port}" : "";
        return new(desired, tunnelText, ssh, panel, cert,
            $"Снимок состояния обновлён: {observed.CheckedAt.ToLocalTime():g}{actual} · {observed.Message}" +
            (differs ? " Проверенные настройки отличаются от профиля; откройте обслуживание VPS для сверки." : ""));
    }

    private static string TunnelName(TunnelState state) => state switch
    {
        OpenViya.Ops.Core.TunnelState.Active => "работает", OpenViya.Ops.Core.TunnelState.Stopped => "остановлен", OpenViya.Ops.Core.TunnelState.Starting => "запускается",
        OpenViya.Ops.Core.TunnelState.Verifying => "проверяется", OpenViya.Ops.Core.TunnelState.Degraded => "требует проверки", OpenViya.Ops.Core.TunnelState.Failed => "ошибка",
        OpenViya.Ops.Core.TunnelState.Stopping => "останавливается", _ => "неизвестно"
    };

    private static string StatusName(ProbeStatus status) => status switch
    {
        ProbeStatus.Success => "успешно",
        ProbeStatus.Warning => "предупреждение",
        ProbeStatus.Failure => "ошибка",
        _ => "неизвестно"
    };
}
