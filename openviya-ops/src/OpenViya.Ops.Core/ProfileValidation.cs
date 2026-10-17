using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace OpenViya.Ops.Core;

public static class ProfileValidator
{
    private static readonly Regex UserPattern = new(@"^[A-Za-z_][A-Za-z0-9_.-]{0,31}\$?$", RegexOptions.CultureInvariant);
    private static readonly Regex RegionPattern = new(@"^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Validate(ServerProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile), "Профиль сервера не задан.");
        var errors = new List<string>();
        if (profile.Id == Guid.Empty) errors.Add("Идентификатор сервера не задан.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 100 || profile.Name.Any(char.IsControl)) errors.Add("Укажите имя сервера длиной до 100 символов.");
        if (!Enum.IsDefined(profile.Role)) errors.Add("Выбрана неизвестная роль сервера.");
        if (string.IsNullOrWhiteSpace(profile.Region) || !RegionPattern.IsMatch(profile.Region)) errors.Add("Регион должен содержать латинские буквы, цифры и дефисы.");
        if (profile.NodeIndex is < 1 or > 999) errors.Add("Индекс ноды должен быть от 1 до 999.");
        if (!IsHost(profile.Host)) errors.Add("Укажите корректный IPv4, IPv6 или DNS hostname без схемы и порта.");
        if (!ValidPort(profile.SshPort)) errors.Add("SSH-порт должен быть от 1 до 65535.");
        if (string.IsNullOrWhiteSpace(profile.UserName) || !UserPattern.IsMatch(profile.UserName)) errors.Add("Укажите допустимое имя SSH-пользователя (до 32 символов).");
        if (profile.IdentityFile is null || profile.IdentityFile.Length > 4096 || profile.IdentityFile.Any(char.IsControl)) errors.Add("Путь к SSH identity слишком длинный или содержит управляющие символы.");
        if (!ValidDomainOrEmpty(profile.PanelDomain)) errors.Add("Домен панели должен быть корректным DNS-именем.");
        if (!ValidPort(profile.PanelPort)) errors.Add("Порт панели должен быть от 1 до 65535.");
        if (!ValidBasePath(profile.PanelBasePath)) errors.Add("Путь панели должен быть абсолютным URL-путём без query, fragment и traversal.");
        if (!Enum.IsDefined(profile.PanelAccess)) errors.Add("Выбран неизвестный режим доступа к панели.");
        if (!Enum.IsDefined(profile.PanelOpenMode)) errors.Add("Выбран неизвестный режим открытия панели.");
        else if (profile.PanelOpenMode == PanelOpenMode.PublicHttps && profile.PanelAccess != VpsPanelAccess.Both)
            errors.Add("Публичная HTTPS-панель доступна только при режиме доступа Both.");
        try { _ = PanelAllowedIps.Normalize(profile.PanelAllowedIpsText); }
        catch (ArgumentException ex) { errors.Add(ex.Message); }
        if (profile.LocalPort is { } local && !ValidPort(local)) errors.Add("Локальный порт должен быть от 1 до 65535.");
        if (!ValidDomainOrEmpty(profile.VpnDomain)) errors.Add("VPN-домен должен быть корректным DNS-именем.");
        if (!ValidPort(profile.VpnPort)) errors.Add("VPN-порт должен быть от 1 до 65535.");
        if (!Enum.IsDefined(profile.SubdomainMode)) errors.Add("Выбран неизвестный режим поддоменов.");
        if (!IsValidDomain(profile.DnsZone)) errors.Add("DNS-зона должна быть корректным доменным именем.");
        if (!string.IsNullOrEmpty(profile.DnsAddress) && !IsPublicIpLiteral(profile.DnsAddress)) errors.Add("DNS-адрес должен быть публичным литералом IPv4 или IPv6.");
        if (profile.ControlServerId is not null && profile.Role != ServerRole.VpnNode) errors.Add("Только VPN_NODE может ссылаться на CONTROL-сервер.");
        if (profile.ControlServerId == profile.Id) errors.Add("Сервер не может ссылаться сам на себя как на CONTROL.");
        if (profile.UpdatedAt == default) errors.Add("Дата изменения профиля не задана.");
        return errors;
    }

    public static bool IsValidDomain(string? value) => !string.IsNullOrWhiteSpace(value) && ValidDomainOrEmpty(value);
    public static bool IsValidHost(string? value) => !string.IsNullOrWhiteSpace(value) && IsHost(value);
    public static bool IsValidBasePath(string? value) => !string.IsNullOrEmpty(value) && ValidBasePath(value);
    public static bool IsPublicIpLiteral(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || !IPAddress.TryParse(value, out var address)) return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.ScopeId != 0 || address.IsIPv4MappedToIPv6 || IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6Loopback.Equals(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
            var bytes = address.GetAddressBytes();
            // Globally routable unicast IPv6 allocations are in 2000::/3; exclude documentation space.
            if ((bytes[0] & 0xE0) != 0x20 || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false;
            return true;
        }
        var octets = address.GetAddressBytes();
        var first = octets[0];
        var second = octets[1];
        return first is not (0 or 10 or 127 or >= 224) &&
               !(first == 100 && second is >= 64 and <= 127) &&
               !(first == 169 && second == 254) &&
               !(first == 172 && second is >= 16 and <= 31) &&
               !(first == 192 && second == 168) &&
               !(first == 192 && second == 0 && octets[2] == 0) &&
               !(first == 192 && second == 0 && octets[2] == 2) &&
               !(first == 198 && second is 18 or 19) &&
               !(first == 198 && second == 51 && octets[2] == 100) &&
               !(first == 203 && second == 0 && octets[2] == 113);
    }

    private static bool ValidPort(int port) => port is >= 1 and <= 65535;

    private static bool ValidDomainOrEmpty(string value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        if (value.Length > 253 || value != value.Trim() || value.EndsWith('.')) return false;
        try
        {
            if (IPAddress.TryParse(value, out _)) return false;
            var ascii = new System.Globalization.IdnMapping().GetAscii(value);
            if (ascii.Length > 253) return false;
            var labels = ascii.Split('.');
            return labels.Length >= 2 && labels.All(label => label.Length is > 0 and <= 63 &&
                label[0] != '-' && label[^1] != '-' && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 253 || value.Contains('/') || value.Contains('\\') || value.Contains('@') || value.Contains(':') && !IPAddress.TryParse(value, out _)) return false;
        if (IPAddress.TryParse(value, out _)) return true;
        return ValidDomainOrEmpty(value);
    }

    private static bool ValidBasePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Contains('?') || value.Contains('#') || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) return false;
        var lower = value.ToLowerInvariant();
        if (lower.Contains("%2e") || lower.Contains("%2f") || lower.Contains("%5c")) return false;
        return !value.Split('/').Any(segment => segment is "." or "..");
    }
}

public static class ProfileDefaults
{
    public static ServerProfile Apply(ServerProfile profile, string baseDomain = "openviya.online")
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile), "Профиль сервера не задан.");
        if (profile.SubdomainMode == SubdomainMode.Manual) return profile;
        if (profile.SubdomainMode != SubdomainMode.Automatic) throw new ArgumentException("Выбран неизвестный режим поддоменов.", nameof(profile));
        var zone = string.IsNullOrWhiteSpace(profile.DnsZone) ? baseDomain : profile.DnsZone;
        if (!ProfileValidator.IsValidDomain(zone)) throw new ArgumentException("DNS-зона должна быть корректным доменным именем.", nameof(profile));
        var domain = zone.Trim().TrimEnd('.').ToLowerInvariant();
        var region = Regex.Replace((profile.Region ?? "").Trim().ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');
        if (region.Length == 0) region = "node";
        var node = Math.Clamp(profile.NodeIndex, 1, 999);
        var suffix = $"{region}-{node.ToString(CultureInfo.InvariantCulture)}.{domain}";
        var panel = profile.Role switch
        {
            ServerRole.Control => $"panel-control.{domain}",
            ServerRole.VpnNode => $"panel-{suffix}",
            ServerRole.App => "",
            _ => ""
        };
        var vpn = profile.Role == ServerRole.VpnNode ? suffix : "";
        return profile with
        {
            PanelDomain = panel,
            VpnDomain = vpn,
            DnsZone = zone
        };
    }
}
