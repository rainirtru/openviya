using System.Text;
using System.Text.RegularExpressions;

namespace OpenViya.Ops.HostsHelper;

public enum HostsEditOutcome { Changed, AlreadyOwned, Removed, NotOwned, Conflict }
public sealed record HostsEditResult(string Content, HostsEditOutcome Outcome, string Message);

public static class HostsFileEditor
{
    private const string Marker = "# OpenViya Ops";
    private static readonly Regex DomainPattern = new("^panel-[a-z0-9]+(?:-[a-z0-9]+)*\\.openviya\\.online$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool IsAllowedPanelDomain(string? domain) => domain is not null && domain.Length <= 253 && DomainPattern.IsMatch(domain);

    public static HostsEditResult Apply(string content, string domain)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = ValidateAndNormalize(domain);
        var lines = ReadLines(content, out var newline, out var finalNewline);
        var references = FindMappings(lines, normalized);
        if (references.Any(IsExactOwned)) return new(content, HostsEditOutcome.AlreadyOwned, "Запись уже принадлежит OpenViya Ops.");
        if (references.Count != 0) return new(content, HostsEditOutcome.Conflict, "Для домена уже существует запись без точной метки владения или с другим адресом; файл не изменён.");
        lines.Add($"127.0.0.1\t{normalized}\t{Marker}");
        return new(Join(lines, newline, finalNewline), HostsEditOutcome.Changed, "Добавлена помеченная запись 127.0.0.1.");
    }

    public static HostsEditResult Remove(string content, string domain)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = ValidateAndNormalize(domain);
        var lines = ReadLines(content, out var newline, out var finalNewline);
        var removed = lines.RemoveAll(line => IsExactOwned(line, normalized));
        if (removed == 0) return new(content, HostsEditOutcome.NotOwned, "Помеченная запись OpenViya Ops не найдена; файл не изменён.");
        return new(Join(lines, newline, finalNewline), HostsEditOutcome.Removed, "Удалена только точная помеченная запись OpenViya Ops.");
    }

    private static string ValidateAndNormalize(string domain)
    {
        if (!IsAllowedPanelDomain(domain)) throw new ArgumentException("Разрешены только домены вида panel-*.openviya.online.", nameof(domain));
        return domain.ToLowerInvariant();
    }

    private static List<string> ReadLines(string content, out string newline, out bool finalNewline)
    {
        newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        finalNewline = content.EndsWith('\n');
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (finalNewline && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Join(List<string> lines, string newline, bool finalNewline) => string.Join(newline, lines) + (finalNewline ? newline : string.Empty);

    private static List<string> FindMappings(List<string> lines, string domain) => lines.Where(line =>
    {
        var parsed = Parse(line);
        return parsed is not null && parsed.Value.Names.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }).ToList();

    private static bool IsExactOwned(string line) => IsExactOwned(line, null);
    private static bool IsExactOwned(string line, string? expectedDomain)
    {
        var parsed = Parse(line);
        return parsed is not null && parsed.Value.Address == "127.0.0.1" &&
               parsed.Value.Names.Length == 1 &&
               (expectedDomain is null || parsed.Value.Names[0].Equals(expectedDomain, StringComparison.OrdinalIgnoreCase)) &&
               parsed.Value.Comment.Trim().Equals(Marker, StringComparison.Ordinal);
    }

    private static (string Address, string[] Names, string Comment)? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;
        var commentIndex = trimmed.IndexOf('#');
        var data = commentIndex < 0 ? trimmed : trimmed[..commentIndex].TrimEnd();
        var comment = commentIndex < 0 ? string.Empty : trimmed[commentIndex..].Trim();
        var tokens = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;
        return (tokens[0], tokens[1..], comment);
    }
}
