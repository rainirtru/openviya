using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Network;

public sealed class HostsService(AppPaths paths) : IHostsService
{
    private readonly AppPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private static readonly System.Text.RegularExpressions.Regex DomainPattern = new("^panel-[a-z0-9]+(?:-[a-z0-9]+)*\\.openviya\\.online$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static bool IsAllowedPanelDomain(string? domain) => domain is not null && domain.Length <= 253 && DomainPattern.IsMatch(domain);
    private static readonly string HostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public Task<HostsPlan> InspectAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowedPanelDomain(domain))
            return Task.FromResult(new HostsPlan(domain, "127.0.0.1", false, false, "Разрешены только домены вида panel-*.openviya.online."));
        try
        {
            var content = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : string.Empty;
            var normalized = domain.ToLowerInvariant();
            var mappings = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select(ParseMapping).Where(mapping => mapping is not null && mapping.Value.Names.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                .Select(mapping => mapping!.Value).ToArray();
            if (mappings.Length == 0) return Task.FromResult(new HostsPlan(normalized, "127.0.0.1", false, false, "Явной записи для домена нет."));
            var hasLoopback = mappings.Any(mapping => mapping.Address.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase));
            var hasConflict = mappings.Any(mapping => !mapping.Address.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || mapping.Names.Length != 1);
            var ownedByApplication = mappings.Any(mapping => mapping.Owned && mapping.Address.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) && mapping.Names.Length == 1);
            var message = hasConflict ? "Найдена конфликтующая запись; автоматически применять изменение нельзя."
                : mappings.Any(mapping => mapping.Owned) ? "Найдена помеченная запись OpenViya Ops."
                : "Найдена явная локальная запись; используется существующая запись без изменения hosts-файла.";
            return Task.FromResult(new HostsPlan(normalized, "127.0.0.1", hasLoopback && !hasConflict, hasConflict, message, ownedByApplication));
        }
        catch (IOException) { return Task.FromResult(new HostsPlan(domain, "127.0.0.1", false, true, "Не удалось прочитать системный hosts-файл.")); }
        catch (UnauthorizedAccessException) { return Task.FromResult(new HostsPlan(domain, "127.0.0.1", false, true, "Нет прав для чтения системного hosts-файла.")); }
    }

    public Task ApplyAsync(string domain, CancellationToken cancellationToken = default) => RunHelperAsync("apply", domain, cancellationToken);
    public Task RemoveAsync(string domain, CancellationToken cancellationToken = default) => RunHelperAsync("remove", domain, cancellationToken);

    private async Task RunHelperAsync(string operation, string domain, CancellationToken cancellationToken)
    {
        if (!IsAllowedPanelDomain(domain)) throw new ArgumentException("Разрешены только домены вида panel-*.openviya.online.", nameof(domain));
        cancellationToken.ThrowIfCancellationRequested();
        var helperFolder = Path.Combine(AppContext.BaseDirectory, "helper");
        var helperPath = Path.Combine(helperFolder, "OpenViya.Ops.HostsHelper.exe");
        if (!File.Exists(helperPath)) helperPath = Path.Combine(AppContext.BaseDirectory, "OpenViya.Ops.HostsHelper.exe");
        if (!File.Exists(helperPath)) throw new FileNotFoundException("Установленный HostsHelper не найден в папке helper или рядом с приложением.", helperPath);
        var start = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = $"{operation} {domain.ToLowerInvariant()}",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows не запустила HostsHelper.");
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                var state = await InspectAsync(domain, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException($"Операция HostsHelper была отменена после запуска; результат проверен повторно: {state.Message}");
            }
            if (process.ExitCode == 3) throw new InvalidOperationException("HostsHelper обнаружил конфликт и оставил hosts-файл без изменений.");
            if (process.ExitCode != 0) throw new InvalidOperationException($"HostsHelper завершился с кодом {process.ExitCode}; состояние hosts следует проверить через Inspect.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Запрос UAC был отменён; hosts-файл не изменён.", ex);
        }
    }

    private static (string Address, string[] Names, bool Owned)? ParseMapping(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;
        var index = trimmed.IndexOf('#');
        var data = index < 0 ? trimmed : trimmed[..index].TrimEnd();
        var comment = index < 0 ? string.Empty : trimmed[index..].Trim();
        var parts = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : (parts[0], parts[1..], comment == "# OpenViya Ops");
    }
}
