using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Network;

/// <summary>Checks public DNS through Cloudflare's DNS-over-HTTPS JSON endpoint.</summary>
public sealed class PublicDnsService : IPublicDnsService, IDisposable
{
    private static readonly Uri Endpoint = new("https://cloudflare-dns.com/dns-query");
    private readonly TimeSpan _requestTimeout;
    private const int MaxResponseBytes = 128 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    public PublicDnsService()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
        _requestTimeout = TimeSpan.FromSeconds(8);
    }

    /// <summary>Accepts a caller-owned client, mainly for deterministic transport tests.</summary>
    public PublicDnsService(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = false;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(8);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "DNS request timeout must be positive and finite.");
    }

    public async Task<IReadOnlyList<ProbeResult>> CheckAsync(DnsPlan plan, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryValidatePlan(plan, out var normalizedZone, out var validationMessage))
            return [ProbeResult.Create("Публичный DNS", ProbeStatus.Failure, validationMessage)];

        var results = new List<ProbeResult>();
        var nsResponse = await QueryAsync(normalizedZone, "NS", cancellationToken).ConfigureAwait(false);
        results.Add(CompareNameServers(normalizedZone, plan.NameServers, nsResponse));

        foreach (var item in plan.Items.OrderBy(x => NormalizeName(x.Fqdn), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Type, StringComparer.OrdinalIgnoreCase))
        {
            var qname = NormalizeName(item.Fqdn);
            var response = await QueryAsync(qname, item.Type.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
            results.Add(CompareAddress(qname, item.Type.ToUpperInvariant(), item.Address, response));
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }

    private async Task<DohResponse> QueryAsync(string name, string type, CancellationToken cancellationToken)
    {
        var uri = new Uri(Endpoint + "?name=" + Uri.EscapeDataString(name) + "&type=" + Uri.EscapeDataString(type));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                return DohResponse.TransportFailure("Cloudflare DoH вернул перенаправление; оно не выполняется.");
            if (!response.IsSuccessStatusCode)
                return DohResponse.TransportFailure($"Cloudflare DoH вернул HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return DohResponse.TransportFailure("Ответ Cloudflare DoH превысил допустимый размер.");

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await body.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > MaxResponseBytes)
                    return DohResponse.TransportFailure("Ответ Cloudflare DoH превысил допустимый размер.");
                memory.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Status", out var statusElement) || statusElement.ValueKind != JsonValueKind.Number || !statusElement.TryGetInt32(out var dnsStatus))
                return DohResponse.TransportFailure("Cloudflare DoH вернул некорректный DNS JSON.");

            var answers = new List<DnsAnswer>();
            if (root.TryGetProperty("Answer", out var answerArray))
            {
                if (answerArray.ValueKind != JsonValueKind.Array)
                    return DohResponse.TransportFailure("Cloudflare DoH вернул некорректные DNS-ответы.");
                foreach (var answer in answerArray.EnumerateArray())
                {
                    if (answer.ValueKind != JsonValueKind.Object ||
                        !answer.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                        !answer.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.Number || !typeElement.TryGetInt32(out var answerType) ||
                        !answer.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String)
                        return DohResponse.TransportFailure("Cloudflare DoH вернул некорректные DNS-ответы.");
                    long? ttl = null;
                    if (answer.TryGetProperty("TTL", out var ttlElement))
                    {
                        if (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt64(out var parsedTtl) || parsedTtl < 0)
                            return DohResponse.TransportFailure("Cloudflare DoH вернул некорректное значение TTL.");
                        ttl = parsedTtl;
                    }
                    answers.Add(new DnsAnswer(nameElement.GetString()!, answerType, dataElement.GetString()!, ttl));
                }
            }
            return new DohResponse(dnsStatus, answers, null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DohResponse.TransportFailure("Истекло время ожидания ответа Cloudflare DoH."); }
        catch (HttpRequestException) { return DohResponse.TransportFailure("Не удалось выполнить запрос Cloudflare DoH."); }
        catch (IOException) { return DohResponse.TransportFailure("Не удалось прочитать ответ Cloudflare DoH."); }
        catch (JsonException) { return DohResponse.TransportFailure("Cloudflare DoH вернул некорректный DNS JSON."); }
        catch (ArgumentException) { return DohResponse.TransportFailure("Не удалось сформировать запрос Cloudflare DoH."); }
        catch (InvalidOperationException) { return DohResponse.TransportFailure("Cloudflare DoH вернул некорректный DNS JSON."); }
    }

    private static ProbeResult CompareNameServers(string zone, IReadOnlyList<string> expected, DohResponse response)
    {
        const string name = "Публичный DNS NS";
        if (response.TransportError is not null) return ProbeResult.Create(name, ProbeStatus.Failure, response.TransportError);
        if (response.DnsStatus == 3) return ProbeResult.Create(name, ProbeStatus.Warning, $"Публичный рекурсивный DNS Cloudflare вернул NXDOMAIN для {zone}; делегирование ещё может распространяться или зона не существует.");
        if (response.DnsStatus != 0) return ProbeResult.Create(name, ProbeStatus.Failure, $"Публичный рекурсивный DNS Cloudflare вернул DNS-статус {response.DnsStatus} для NS {zone}.");

        var actualAnswers = response.Answers.Where(x => x.Type == 2 && SameName(x.Name, zone)).ToArray();
        if (actualAnswers.Length == 0) return ProbeResult.Create(name, ProbeStatus.Warning, $"Публичный рекурсивный DNS Cloudflare не вернул записи NS для {zone}; делегирование ещё может распространяться.");
        if (actualAnswers.Any(x => !TryNormalizeDomain(x.Data, out _)))
            return ProbeResult.Create(name, ProbeStatus.Failure, $"Публичный рекурсивный DNS Cloudflare вернул некорректные данные NS для {zone}.");
        var actual = actualAnswers.Select(x => NormalizeName(x.Data)).ToArray();
        if (actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() != actual.Length)
            return ProbeResult.Create(name, ProbeStatus.Failure, $"Публичный рекурсивный DNS Cloudflare вернул повторяющиеся записи NS для {zone}.");
        if (expected.Count == 0)
            return ProbeResult.Create(name, ProbeStatus.Warning, $"Публичный NS для {zone} получен через Cloudflare, но ожидаемый набор не указан; сверка не выполнена. Рекурсивный кэш и TTL могут задерживать обновление; это не прямой запрос к авторитетным серверам.");
        var expectedSet = expected.Select(NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSet = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualSet.SetEquals(expectedSet))
            return ProbeResult.Create(name, ProbeStatus.Failure,
                $"Несовпадение делегирования зоны: публичный рекурсивный DNS Cloudflare видит [{string.Join(", ", actual.Order(StringComparer.OrdinalIgnoreCase))}], а в плане указаны [{string.Join(", ", expectedSet.Order(StringComparer.OrdinalIgnoreCase))}]. Возможно, зона обслуживается другими NS-серверами.");

        var ttl = MinimumTtl(actualAnswers);
        return ProbeResult.Create(name, ProbeStatus.Success,
            $"Публичный рекурсивный DNS Cloudflare видит указанный в плане набор NS для {zone}{FormatTtl(ttl)}. Рекурсивный кэш и TTL могут задерживать обновление; это не прямой запрос к авторитетным серверам и не подтверждает доступность VPN.");
    }

    private static ProbeResult CompareAddress(string qname, string type, string expectedAddress, DohResponse response)
    {
        var name = $"Публичный DNS {type} {qname}";
        if (response.TransportError is not null) return ProbeResult.Create(name, ProbeStatus.Failure, response.TransportError);
        if (response.DnsStatus == 3) return ProbeResult.Create(name, ProbeStatus.Warning, $"Публичный рекурсивный DNS Cloudflare вернул NXDOMAIN для {qname}; записи DNS ещё могут распространяться.");
        if (response.DnsStatus != 0) return ProbeResult.Create(name, ProbeStatus.Failure, $"Публичный рекурсивный DNS Cloudflare вернул DNS-статус {response.DnsStatus} для {type} {qname}.");

        var relevant = response.Answers.Where(x => SameName(x.Name, qname) && x.Type is 1 or 5 or 28).ToArray();
        if (relevant.Length == 0) return ProbeResult.Create(name, ProbeStatus.Warning, $"Публичный рекурсивный DNS Cloudflare не вернул адрес {type} для {qname}; записи DNS ещё могут распространяться.");
        if (relevant.Any(x => x.Type == 5))
            return ProbeResult.Create(name, ProbeStatus.Failure, $"В публичном DNS для {qname} указана запись CNAME, а план ожидает прямую запись {type}.");

        var expectedType = type == "A" ? 1 : 28;
        var records = relevant.Where(x => x.Type == expectedType).ToArray();
        var unexpectedType = relevant.Any(x => x.Type != expectedType);
        if (unexpectedType)
            return ProbeResult.Create(name, ProbeStatus.Failure, $"В публичном DNS для {qname} смешаны записи A и AAAA, а план ожидает только {type}.");
        if (records.Length == 0) return ProbeResult.Create(name, ProbeStatus.Warning, $"Публичный рекурсивный DNS Cloudflare не вернул адрес {type} для {qname}; записи DNS ещё могут распространяться.");
        if (records.Length != 1)
            return ProbeResult.Create(name, ProbeStatus.Failure, $"В публичном DNS найдены повторяющиеся или смешанные записи для {qname}; ожидалось ровно одно значение {type}.");

        if (!IPAddress.TryParse(records[0].Data, out var actualAddress) || actualAddress.AddressFamily != (type == "A" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6) || actualAddress.AddressFamily == AddressFamily.InterNetworkV6 && actualAddress.ScopeId != 0)
            return ProbeResult.Create(name, ProbeStatus.Failure, $"Публичный рекурсивный DNS Cloudflare вернул некорректные данные {type} для {qname}.");
        if (!IPAddress.TryParse(expectedAddress, out var expected) || expected.AddressFamily != actualAddress.AddressFamily || !actualAddress.Equals(expected))
            return ProbeResult.Create(name, ProbeStatus.Failure, $"Публичная запись DNS {type} для {qname} указывает на {actualAddress}, а в плане указан адрес {expectedAddress}.");

        return ProbeResult.Create(name, ProbeStatus.Success,
            $"Публичный рекурсивный DNS Cloudflare возвращает для {qname} {type} указанный в плане адрес {actualAddress}{FormatTtl(records[0].Ttl)}. Рекурсивный кэш и TTL могут задерживать обновление; это не подтверждает доступность VPN.");
    }

    private static bool TryValidatePlan(DnsPlan? plan, out string normalizedZone, out string message)
    {
        normalizedZone = "";
        message = "";
        if (plan is null || plan.Profile is null || plan.Items is null || plan.NameServers is null)
        { message = "План DNS заполнен не полностью."; return false; }
        if (!TryNormalizeDomain(plan.Zone, out normalizedZone) || !TryNormalizeDomain(plan.Profile.DnsZone, out var profileZone) || !SameName(normalizedZone, profileZone))
        { message = "Зона в плане DNS некорректна или не совпадает с зоной профиля."; return false; }
        if (plan.Items.Count == 0 || plan.Items.Count > 64)
        { message = "План DNS должен содержать от одной до 64 записей адресов."; return false; }
        if (plan.NameServers.Count > 32 || plan.NameServers.Any(x => !TryNormalizeDomain(x, out _)) ||
            plan.NameServers.Select(NormalizeName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.NameServers.Count)
        { message = "План DNS содержит некорректный список ожидаемых NS-серверов."; return false; }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items)
        {
            if (item is null || !TryNormalizeDomain(item.Fqdn, out var fqdn) || !IsWithinZone(fqdn, normalizedZone))
            { message = "План DNS содержит некорректное имя или имя вне выбранной зоны."; return false; }
            if (item.Type is not ("A" or "AAAA") || !IPAddress.TryParse(item.Address, out var address) || address.AddressFamily != (item.Type == "A" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6) || address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            { message = "План DNS содержит некорректный тип записи или адрес."; return false; }
            if (!keys.Add(fqdn + "|" + item.Type))
            { message = "План DNS содержит повторяющееся имя и тип записи."; return false; }
        }
        return true;
    }

    private static bool TryNormalizeDomain(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 254 || input.Any(char.IsControl) || input.Any(char.IsWhiteSpace)) return false;
        var candidate = input.TrimEnd('.');
        try { candidate = new IdnMapping().GetAscii(candidate).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        if (candidate.Length is < 1 or > 253 || !candidate.Contains('.') || IPAddress.TryParse(candidate, out _) || candidate.Split('.').Any(label => label.Length is < 1 or > 63 || !Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant))) return false;
        normalized = candidate;
        return true;
    }

    private static string NormalizeName(string input) => TryNormalizeDomain(input, out var normalized) ? normalized : input.Trim().TrimEnd('.').ToLowerInvariant();
    private static bool SameName(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(NormalizeName(left), NormalizeName(right));
    private static bool IsWithinZone(string fqdn, string zone) => fqdn.Length > zone.Length && fqdn.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);
    private static long? MinimumTtl(IEnumerable<DnsAnswer> answers) => answers.Where(x => x.Ttl.HasValue).Select(x => x.Ttl).Min();
    private static string FormatTtl(long? ttl) => ttl is null ? "" : $" (указанный TTL: {ttl.Value.ToString(CultureInfo.InvariantCulture)} с)";

    private sealed record DnsAnswer(string Name, int Type, string Data, long? Ttl);
    private sealed record DohResponse(int DnsStatus, IReadOnlyList<DnsAnswer> Answers, string? TransportError, bool IsTruncated)
    {
        internal static DohResponse TransportFailure(string message) => new(-1, [], message, false);
    }
}
