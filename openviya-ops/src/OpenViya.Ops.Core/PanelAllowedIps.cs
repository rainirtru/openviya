using System.Net;
using System.Net.Sockets;

namespace OpenViya.Ops.Core;

/// <summary>Converts the operator's one-IP-per-line list to exact firewall host networks.</summary>
public static class PanelAllowedIps
{
    public const int MaximumEntries = 100;
    public const int MaximumInputLength = 16000;

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "any";
        if (text.Length > MaximumInputLength)
            throw new ArgumentException("Список разрешённых IP слишком длинный; укажите не более 100 адресов.");

        var networks = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var entries = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var value = lines[i].Trim();
            if (value.Length == 0) continue;
            if (++entries > MaximumEntries)
                throw new ArgumentException("Укажите не более 100 разрешённых IP, по одному на строку.");
            if (!IsLiteral(value, out var address))
                throw new ArgumentException($"Разрешённые IP: строка {i + 1} — укажите один IPv4 или IPv6 без запятых, CIDR и других разделителей.");
            if (address!.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var network = address + (address.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128");
            if (seen.Add(network)) networks.Add(network);
        }
        return networks.Count == 0 ? "any" : string.Join('\n', networks);
    }

    private static bool IsLiteral(string value, out IPAddress? address)
    {
        address = null;
        if (value.Length > 45 || value.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' or ':' or '.')))
            return false;
        if (!value.Contains(':'))
        {
            var parts = value.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length is < 1 or > 3 || p.Length > 1 && p[0] == '0' || !p.All(char.IsAsciiDigit)))
                return false;
        }
        return IPAddress.TryParse(value, out address);
    }
}
