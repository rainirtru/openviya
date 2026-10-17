using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Dns;

public sealed class TimewebDnsClient : IDnsProviderService, IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.timeweb.cloud/");
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private const int PageSize = 100, MaxPages = 20, MaxBytes = 1_048_576;
    private static readonly HashSet<string> KnownNs = new(StringComparer.OrdinalIgnoreCase) { "ns1.timeweb.ru", "ns2.timeweb.ru", "ns3.timeweb.org", "ns4.timeweb.org" };
    private readonly IDnsTokenStore _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly HttpClient _publicDns;
    private readonly bool _ownsPublicDns;
    private readonly TimeSpan _requestTimeout;

    public TimewebDnsClient(IDnsTokenStore tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(6) }, true) { Timeout = DefaultTimeout };
        _requestTimeout = DefaultTimeout;
        _ownsHttp = true;
        _publicDns = CreatePublicDnsClient();
        _ownsPublicDns = true;
    }

    // Injectable transport for deterministic tests. Requests still use the fixed Timeweb HTTPS origin.
    public TimewebDnsClient(IDnsTokenStore tokens, HttpClient httpClient, TimeSpan? requestTimeout = null, HttpClient? publicDnsClient = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? DefaultTimeout;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _publicDns = publicDnsClient ?? CreatePublicDnsClient();
        _ownsPublicDns = publicDnsClient is null;
    }

    public async Task<DnsZoneCheck> CheckZoneAsync(string zone, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDomain(zone, out var normalized)) return new DnsZoneCheck(zone ?? "", false, [], "Укажите корректное имя DNS-зоны.");
        try
        {
            var token = await LoadTokenAsync(cancellationToken).ConfigureAwait(false);
            using var domain = await GetDomainAsync(normalized, token, cancellationToken).ConfigureAwait(false);
            if (domain is null) return new DnsZoneCheck(normalized, false, [], "Зона не найдена в аккаунте Timeweb Cloud.");
            var ns = await GetNameServersAsync(normalized, token, cancellationToken).ConfigureAwait(false);
            if (!AreTimewebNs(ns)) return new DnsZoneCheck(normalized, false, ns, "API доступен, но NS не подтверждают обслуживание зоны у Timeweb; автоматическая запись заблокирована.");
            return new DnsZoneCheck(normalized, true, ns, "Зона найдена, NS указывают на поддерживаемую инфраструктуру Timeweb Cloud. Это не подтверждает распространение записей в публичном DNS.");
        }
        catch (OperationCanceledException) { throw; }
        catch (DnsApiException ex) { return new DnsZoneCheck(normalized, false, [], ex.SafeMessage); }
    }

    public async Task<DnsPlan> BuildPlanAsync(ServerProfile profile, int ttl = 600, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        var zone = NormalizeDomain(profile.DnsZone);
        var rawAddress = string.IsNullOrWhiteSpace(profile.DnsAddress) ? profile.Host : profile.DnsAddress;
        if (zone.Length == 0) return InvalidPlan(profile, "", rawAddress, ttl, "DNS-зона некорректна.");
        if (ttl is < 60 or > 86400) return InvalidPlan(profile, zone, rawAddress, ttl, "TTL должен быть от 60 до 86400 секунд.");
        if (!IsPublicLiteral(rawAddress) || !IPAddress.TryParse(rawAddress, out var ip))
            return InvalidPlan(profile, zone, rawAddress, ttl, "Нужен публичный литерал IPv4/IPv6 в DNS-адресе; SSH hostname не используется как A/AAAA value.");
        if (!Enum.IsDefined(profile.SubdomainMode)) return InvalidPlan(profile, zone, rawAddress, ttl, "Режим поддоменов не распознан.");
        var effective = profile.SubdomainMode == SubdomainMode.Automatic ? ProfileDefaults.Apply(profile, zone) : profile;
        var targets = GetTargets(effective, zone, ip);
        if (targets.Count == 0) return new DnsPlan(profile, zone, rawAddress, ttl, [], [], DateTimeOffset.UtcNow);
        if (targets.Any(x => !InZone(x.Fqdn, zone))) return InvalidPlan(profile, zone, rawAddress, ttl, "Каждое ручное имя должно находиться внутри выбранной зоны точным суффиксом.");

        var token = await LoadTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var domain = await GetDomainAsync(zone, token, cancellationToken).ConfigureAwait(false);
            if (domain is null) return ConflictPlan(profile, zone, rawAddress, ttl, targets, "Зона не найдена в аккаунте Timeweb Cloud.");
            var ns = await GetNameServersAsync(zone, token, cancellationToken).ConfigureAwait(false);
            if (!AreTimewebNs(ns)) return ConflictPlan(profile, zone, rawAddress, ttl, targets, "NS не подтверждают обслуживание зоны у Timeweb; применение через API заблокировано.", ns);
            var subdomains = ReadSubdomains(domain, zone);
            var items = new List<DnsPlanItem>();
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exists = target.Fqdn.Equals(zone, StringComparison.OrdinalIgnoreCase) || subdomains.Contains(target.Fqdn);
                var records = await ReadTargetRecordsAsync(target.Fqdn, zone, token, cancellationToken).ConfigureAwait(false);
                items.Add(Analyze(target.Fqdn, target.Type, target.Address, records.Records, exists));
            }
            return new DnsPlan(profile, zone, rawAddress, ttl, items, ns, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (DnsApiException ex) { return ConflictPlan(profile, zone, rawAddress, ttl, targets, ex.SafeMessage); }
    }

    public async Task<DnsApplyResult> ApplyAsync(DnsPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Profile.SubdomainMode == SubdomainMode.Manual)
            return new DnsApplyResult(plan.Items.Select(x => new DnsApplyItem(x.Fqdn, ProbeStatus.Warning, "Ручной режим: создайте или измените эту DNS-запись на сайте провайдера; API-запросы не выполнялись.", x.RecordId)).ToArray());
        var fresh = await BuildPlanAsync(plan.Profile, plan.Ttl, cancellationToken).ConfigureAwait(false);
        if (!SameIntent(plan, fresh)) return new DnsApplyResult(plan.Items.Select(x => new DnsApplyItem(x.Fqdn, ProbeStatus.Warning, "Профиль, зона, адрес или имена изменились. Пересмотрите свежий DNS-план.")).ToArray());
        if (fresh.Items.Any(x => x.Action == DnsPlanAction.Conflict))
            return new DnsApplyResult(fresh.Items.Select(x => new DnsApplyItem(x.Fqdn, ProbeStatus.Failure, x.Action == DnsPlanAction.Conflict ? x.Message : "Ничего не изменено: план содержит конфликт.", x.RecordId)).ToArray());
        var results = new List<DnsApplyItem>();
        if (fresh.Items.Count == 0) return new DnsApplyResult([]);
        var token = await LoadTokenAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < fresh.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = fresh.Items[index];
            if (item.Action == DnsPlanAction.AlreadyConfigured)
            {
                results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Success, "Совпадающая запись уже существует; повторный POST пропущен.", item.RecordId));
                continue;
            }
            try
            {
                if (!item.Fqdn.Equals(fresh.Zone, StringComparison.OrdinalIgnoreCase) && !item.SubdomainExists)
                {
                    var relative = Relative(item.Fqdn, fresh.Zone);
                    try { await PostAsync($"api/v1/domains/{Esc(fresh.Zone)}/subdomains/{Esc(relative)}", null, token, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        var nowExists = await SubdomainExistsAsync(fresh.Zone, item.Fqdn, token, CancellationToken.None).ConfigureAwait(false);
                        results.Add(new DnsApplyItem(item.Fqdn, nowExists ? ProbeStatus.Warning : ProbeStatus.Unknown, "Создание поддомена было отменено после отправки; состояние перечитано. Пересчитайте план."));
                        AddRemainder(results, fresh.Items, index + 1); break;
                    }
                    catch (DnsApiException ex) when (ex.IsTransportFailure || ex.StatusCode >= 500)
                    {
                        if (!await SubdomainExistsAsync(fresh.Zone, item.Fqdn, token, CancellationToken.None).ConfigureAwait(false))
                        {
                            results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Unknown, "Ответ создания поддомена неизвестен, а поддомен пока не виден; перечитайте план перед повтором."));
                            continue;
                        }
                    }
                    catch (DnsApiException ex) { results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Failure, ex.SafeMessage)); continue; }
                }

                // Creating a subdomain can create provider defaults; read both sets again before POST.
                var current = await ReadTargetRecordsAsync(item.Fqdn, fresh.Zone, token, cancellationToken).ConfigureAwait(false);
                var currentPlan = Analyze(item.Fqdn, item.Type, item.Address, current.Records, current.SubdomainExists);
                if (currentPlan.Action == DnsPlanAction.AlreadyConfigured)
                {
                    results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Success, "Совпадающая запись появилась при повторном чтении; POST пропущен.", currentPlan.RecordId));
                    continue;
                }
                if (currentPlan.Action == DnsPlanAction.Conflict)
                {
                    results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Failure, currentPlan.Message, currentPlan.RecordId));
                    continue;
                }
                try
                {
                    var json = JsonSerializer.Serialize(new { type = item.Type, value = item.Address, ttl = fresh.Ttl });
                    var body = await PostAsync($"api/v2/domains/{Esc(item.Fqdn)}/dns-records", json, token, cancellationToken).ConfigureAwait(false);
                    var responseId = ReadCreatedId(body);
                    var after = await SafeReadTargetRecordsAsync(item.Fqdn, fresh.Zone, token).ConfigureAwait(false);
                    var verified = after is null ? null : Analyze(item.Fqdn, item.Type, item.Address, after.Value.Records, after.Value.SubdomainExists);
                    if (verified?.Action == DnsPlanAction.AlreadyConfigured)
                        results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Success, "Запись создана и подтверждена повторным чтением.", verified.RecordId ?? responseId));
                    else
                        results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Warning, "POST принят, но точная единственная запись не подтвердилась при повторном чтении.", responseId ?? verified?.RecordId));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var after = await SafeReadTargetRecordsAsync(item.Fqdn, fresh.Zone, token).ConfigureAwait(false);
                    var found = after is null ? null : DesiredRecord(after.Value.Records, item.Type, item.Address);
                    results.Add(found is null ? new DnsApplyItem(item.Fqdn, ProbeStatus.Unknown, "Ответ создания записи неизвестен после отмены; перечитайте план перед повтором.") : new DnsApplyItem(item.Fqdn, ProbeStatus.Warning, "Запись создана до отмены и подтверждена чтением.", found.Id));
                    AddRemainder(results, fresh.Items, index + 1); break;
                }
                catch (DnsApiException ex) when (ex.IsTransportFailure || ex.StatusCode >= 500)
                {
                    var after = await SafeReadTargetRecordsAsync(item.Fqdn, fresh.Zone, token).ConfigureAwait(false);
                    var found = after is null ? null : DesiredRecord(after.Value.Records, item.Type, item.Address);
                    results.Add(found is null ? new DnsApplyItem(item.Fqdn, ProbeStatus.Unknown, "Ответ создания записи неизвестен; проверьте свежий план перед повтором.") : new DnsApplyItem(item.Fqdn, ProbeStatus.Success, "Запись создана до потери ответа и подтверждена чтением.", found.Id));
                }
                catch (DnsApiException ex) { results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Failure, ex.SafeMessage)); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new DnsApplyItem(item.Fqdn, ProbeStatus.Unknown, "Операция отменена до подтверждения результата."));
                AddRemainder(results, fresh.Items, index + 1); break;
            }
            catch (DnsApiException ex) { results.Add(new DnsApplyItem(item.Fqdn, ex.IsTransportFailure ? ProbeStatus.Unknown : ProbeStatus.Failure, ex.SafeMessage)); }
        }
        return new DnsApplyResult(results);
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); if (_ownsPublicDns) _publicDns.Dispose(); }

    private static HttpClient CreatePublicDnsClient() => new(new SocketsHttpHandler
    { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(6) }) { Timeout = Timeout.InfiniteTimeSpan };

    private async Task<string> LoadTokenAsync(CancellationToken ct)
    {
        string? token;
        try { token = await _tokens.LoadAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw new DnsApiException("Не удалось прочитать сохранённый токен DNS-провайдера."); }
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c)))) throw new DnsApiException("Токен Timeweb Cloud не задан или имеет недопустимый формат.");
        return token.Trim();
    }

    private async Task<JsonDocument?> GetDomainAsync(string zone, string token, CancellationToken ct)
    {
        var reply = await SendAsync(HttpMethod.Get, $"api/v1/domains/{Esc(zone)}", null, token, true, ct).ConfigureAwait(false);
        if (reply.Status == HttpStatusCode.NotFound) return null;
        EnsureSuccess(reply);
        var document = Parse(reply.Body, "Ответ проверки зоны имеет недопустимый формат.");
        var domain = Prop(document.RootElement, "domain");
        if (domain.ValueKind == JsonValueKind.Undefined) domain = Nested(document.RootElement, "data", "domain");
        var returned = Str(domain, "fqdn");
        if (!TryDomain(returned, out var normalized) || !normalized.Equals(zone, StringComparison.OrdinalIgnoreCase))
        { document.Dispose(); throw new DnsApiException("Ответ API не подтвердил точное имя выбранной зоны."); }
        return document;
    }

    private async Task<IReadOnlyList<string>> GetNameServersAsync(string zone, string token, CancellationToken ct)
    {
        var reply = await SendAsync(HttpMethod.Get, $"api/v1/domains/{Esc(zone)}/name-servers", null, token, true, ct).ConfigureAwait(false);
        // Some accessible DNS zones have no registrar NS resource. Confirm actual
        // public delegation instead; never send the Timeweb bearer token to DoH.
        if (reply.Status == HttpStatusCode.NotFound)
            return await GetPublicNameServersAsync(zone, ct).ConfigureAwait(false);
        EnsureSuccess(reply);
        using var doc = Parse(reply.Body, "Ответ NS имеет недопустимый формат.");
        var entries = Prop(doc.RootElement, "name_servers");
        if (entries.ValueKind == JsonValueKind.Undefined) entries = Nested(doc.RootElement, "data", "name_servers");
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
            {
                var task = Str(entry, "task_status");
                if (!string.Equals(task, "done", StringComparison.OrdinalIgnoreCase)) values.Add("!ns-task-not-done");
                var items = Prop(entry, "items");
                if (items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    var host = Str(item, "host");
                    if (!string.IsNullOrWhiteSpace(host)) values.Add(host.Trim().TrimEnd('.').ToLowerInvariant());
                }
            }
        return values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<string>> GetPublicNameServersAsync(string zone, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://cloudflare-dns.com/dns-query?name=" + Uri.EscapeDataString(zone) + "&type=NS");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        try
        {
            using var response = await _publicDns.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DnsApiException("Timeweb не вернул NS (404), а публичная проверка NS недоступна. Повторите позже.");
            using var doc = Parse(await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false), "Публичный ответ NS имеет недопустимый формат.");
            if (Long(doc.RootElement, "Status") != 0 || Prop(doc.RootElement, "TC").ValueKind == JsonValueKind.True)
                throw new DnsApiException("Timeweb не вернул NS (404); публичный DNS не подтвердил делегирование зоны.");
            var answers = Prop(doc.RootElement, "Answer");
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (answers.ValueKind == JsonValueKind.Array)
                foreach (var answer in answers.EnumerateArray())
                    if (Long(answer, "type") == 2 && NormalizeDomain(Str(answer, "name")?.TrimEnd('.')) == zone && TryDomain(Str(answer, "data")?.TrimEnd('.'), out var host))
                        values.Add(host);
            return values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new DnsApiException("Публичная проверка NS не ответила за ограниченное время."); }
        catch (HttpRequestException) { throw new DnsApiException("Не удалось проверить публичные NS после ответа Timeweb 404."); }
    }

    private async Task<IReadOnlyList<DnsRecord>> GetRecordsAsync(string fqdn, string zone, string token, CancellationToken ct, bool defaults = false)
    {
        var path = $"api/v1/domains/{Esc(fqdn)}/{(defaults ? "default-dns-records" : "dns-records")}";
        var result = new List<DnsRecord>();
        var offset = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var reply = await SendAsync(HttpMethod.Get, $"{path}?limit={PageSize}&offset={offset}", null, token, true, ct).ConfigureAwait(false);
            EnsureSuccess(reply);
            using var doc = Parse(reply.Body, "Ответ DNS-записей имеет недопустимый формат.");
            var array = Prop(doc.RootElement, "dns_records");
            if (array.ValueKind == JsonValueKind.Undefined) array = Nested(doc.RootElement, "data", "dns_records");
            if (array.ValueKind != JsonValueKind.Array) throw new DnsApiException("Ответ API не содержит список DNS-записей.");
            var count = 0;
            foreach (var entry in array.EnumerateArray())
            {
                count++;
                var type = Str(entry, "type")?.Trim().ToUpperInvariant();
                var data = Prop(entry, "data");
                var value = Str(data, "value")?.Trim();
                var name = NormalizeRecordName(Str(data, "subdomain"), fqdn, zone);
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value)) throw new DnsApiException("Ответ содержит неполную DNS-запись; автоматическое применение остановлено.");
                result.Add(new DnsRecord(name, type, value, Long(entry, "id")));
            }
            var meta = Prop(doc.RootElement, "meta");
            if (meta.ValueKind == JsonValueKind.Undefined) meta = Nested(doc.RootElement, "data", "meta");
            var total = Long(meta, "total");
            if (total is not null)
            {
                if (offset + count >= total) return result;
                if (count == 0) throw new DnsApiException("Ответ пагинации не содержит ожидаемые DNS-записи.");
                offset += count;
                continue;
            }
            if (count < PageSize) return result;
            offset += count;
            if (page == MaxPages - 1) throw new DnsApiException("Слишком много DNS-записей для ограниченной проверки.");
        }
        return result;
    }
    private async Task<(IReadOnlyList<DnsRecord> Records, bool SubdomainExists)> ReadTargetRecordsAsync(string fqdn, string zone, string token, CancellationToken ct)
    {
        using var domain = await GetDomainAsync(zone, token, ct).ConfigureAwait(false);
        if (domain is null) throw new DnsApiException("Зона не найдена при повторном чтении.");
        var exists = fqdn.Equals(zone, StringComparison.OrdinalIgnoreCase) || ReadSubdomains(domain, zone).Contains(fqdn);
        if (!exists)
        {
            // An absent child may not have its own endpoint yet. Inspect apex lists for exact-name defaults first.
            var rootCustom = await GetRecordsAsync(zone, zone, token, ct).ConfigureAwait(false);
            var rootDefaults = await GetRecordsAsync(zone, zone, token, ct, defaults: true).ConfigureAwait(false);
            return (rootCustom.Concat(rootDefaults).Where(x => x.Fqdn.Equals(fqdn, StringComparison.OrdinalIgnoreCase)).ToArray(), false);
        }
        var custom = await GetRecordsAsync(fqdn, zone, token, ct).ConfigureAwait(false);
        var defaults = await GetRecordsAsync(fqdn, zone, token, ct, defaults: true).ConfigureAwait(false);
        return (custom.Concat(defaults).Where(x => x.Fqdn.Equals(fqdn, StringComparison.OrdinalIgnoreCase)).ToArray(), true);
    }
    private async Task<(IReadOnlyList<DnsRecord> Records, bool SubdomainExists)?> SafeReadTargetRecordsAsync(string fqdn, string zone, string token)
    {
        try { return await ReadTargetRecordsAsync(fqdn, zone, token, CancellationToken.None).ConfigureAwait(false); }
        catch { return null; }
    }
    private async Task<bool> SubdomainExistsAsync(string zone, string fqdn, string token, CancellationToken ct)
    {
        using var domain = await GetDomainAsync(zone, token, ct).ConfigureAwait(false);
        return domain is not null && ReadSubdomains(domain, zone).Contains(fqdn);
    }

    private async Task<string> PostAsync(string path, string? json, string token, CancellationToken ct)
    {
        var reply = await SendAsync(HttpMethod.Post, path, json, token, false, ct).ConfigureAwait(false);
        EnsureSuccess(reply);
        return reply.Body;
    }

    private async Task<ApiReply> SendAsync(HttpMethod method, string path, string? json, string token, bool retryGet, CancellationToken ct)
    {
        if (!Uri.TryCreate(ApiRoot, path, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("api.timeweb.cloud", StringComparison.OrdinalIgnoreCase))
            throw new DnsApiException("Сформирован недопустимый адрес Timeweb API.");
        for (var attempt = 0; ; attempt++)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false);
                var body = await ReadBodyAsync(response.Content, requestTimeout.Token).ConfigureAwait(false);
                if (retryGet && method == HttpMethod.Get && response.StatusCode == (HttpStatusCode)429 && attempt < 2)
                {
                    var delay = RetryDelay(response.Headers.RetryAfter, attempt);
                    await Task.Delay(delay, requestTimeout.Token).ConfigureAwait(false);
                    continue;
                }
                return new ApiReply(response.StatusCode, body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new DnsApiException("Timeweb API не ответил за ограниченное время.", transport: true); }
            catch (HttpRequestException) { throw new DnsApiException("Не удалось связаться с Timeweb API.", transport: true); }
        }
    }

    private static TimeSpan RetryDelay(RetryConditionHeaderValue? header, int attempt)
    {
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250 * (attempt + 1)));
        return TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 3000));
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxBytes) throw new DnsApiException("Ответ Timeweb API превышает допустимый размер.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (memory.Length + count > MaxBytes) throw new DnsApiException("Ответ Timeweb API превышает допустимый размер.");
            memory.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static void EnsureSuccess(ApiReply reply)
    {
        if ((int)reply.Status is >= 200 and < 300) return;
        var code = (int)reply.Status;
        var text = code switch
        {
            401 => "Timeweb API отклонил токен (401). Проверьте токен.",
            403 => "Токен Timeweb API не имеет требуемых прав (403).",
            429 => "Достигнут лимит запросов Timeweb API (429).",
            _ => $"Timeweb API вернул HTTP {code}."
        };
        throw new DnsApiException(text, code);
    }

    private static JsonDocument Parse(string body, string safeMessage)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new DnsApiException(safeMessage); }
    }

    private static IReadOnlyList<string> ReadSubdomains(JsonDocument document, string zone)
    {
        var domain = Prop(document.RootElement, "domain");
        if (domain.ValueKind == JsonValueKind.Undefined) domain = Nested(document.RootElement, "data", "domain");
        var entries = Prop(domain, "subdomains");
        if (entries.ValueKind != JsonValueKind.Array) return [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            var raw = Str(entry, "fqdn");
            if (TryResponseName(raw, zone, out var name) && InZone(name, zone)) names.Add(name);
        }
        return names.ToArray();
    }

    private static string NormalizeRecordName(string? raw, string endpoint, string zone)
    {
        if (string.IsNullOrWhiteSpace(raw)) return endpoint;
        if (!TryResponseName(raw, zone, out var normalized)) throw new DnsApiException("Ответ содержит DNS-запись с некорректным именем или вне выбранной зоны.");
        return normalized;
    }

    private static bool TryResponseName(string? raw, string zone, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsWhiteSpace) || raw.Contains('/') || raw.Contains('@') || raw.Contains(':')) return false;
        raw = raw.Trim().TrimEnd('.');
        if (TryDomain(raw, out var full) && InZone(full, zone)) { normalized = full; return true; }
        if (raw.Contains("..", StringComparison.Ordinal)) return false;
        if (TryDomain(raw + "." + zone, out var relative) && InZone(relative, zone)) { normalized = relative; return true; }
        return false;
    }

    private static DnsPlanItem Analyze(string fqdn, string type, string address, IReadOnlyList<DnsRecord> records, bool exists)
    {
        // Provider-created SPF/MX records can coexist with address records.
        // Only address records and aliases can conflict with this A/AAAA intent.
        records = records.Where(IsAddressOrAlias).ToArray();
        if (records.Count == 1 && records[0].Type.Equals(type, StringComparison.OrdinalIgnoreCase) && SameAddress(records[0].Value, address))
            return new DnsPlanItem(fqdn, type, address, DnsPlanAction.AlreadyConfigured, "Найдена одна совпадающая DNS-запись.", records[0].Id, exists);
        if (records.Count != 0) return new DnsPlanItem(fqdn, type, address, DnsPlanAction.Conflict, "Для имени найдены другие типы/значения или несколько записей; автоматическое добавление остановлено.", records[0].Id, exists, DnsBlockReason.RecordConflict);
        return new DnsPlanItem(fqdn, type, address, DnsPlanAction.Create, "Запись отсутствует и может быть создана после повторной проверки.", null, exists);
    }

    private static DnsRecord? DesiredRecord(IEnumerable<DnsRecord> records, string type, string address)
    {
        var all = records.Where(IsAddressOrAlias).ToArray();
        return all.Length == 1 && all[0].Type.Equals(type, StringComparison.OrdinalIgnoreCase) && SameAddress(all[0].Value, address) ? all[0] : null;
    }
    private static bool IsAddressOrAlias(DnsRecord record) =>
        record.Type.Equals("A", StringComparison.OrdinalIgnoreCase) ||
        record.Type.Equals("AAAA", StringComparison.OrdinalIgnoreCase) ||
        record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) ||
        record.Type.Equals("DNAME", StringComparison.OrdinalIgnoreCase);
    private static bool SameAddress(string left, string right) =>
        IPAddress.TryParse(left, out var actual) && IPAddress.TryParse(right, out var expected) && actual.Equals(expected);
    private static long? ReadCreatedId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var record = Prop(doc.RootElement, "dns_record");
            if (record.ValueKind == JsonValueKind.Undefined) record = Nested(doc.RootElement, "data", "dns_record");
            return Long(record, "id");
        }
        catch (JsonException) { return null; }
    }
    private static IReadOnlyList<Target> GetTargets(ServerProfile profile, string zone, IPAddress address)
    {
        if (profile.Role == ServerRole.App) return [];
        var type = address.AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA";
        var names = profile.Role switch
        {
            ServerRole.Control => new[] { profile.PanelDomain },
            ServerRole.VpnNode => new[] { profile.VpnDomain, profile.PanelDomain },
            _ => []
        };
        var targets = new List<Target>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (!TryDomain(raw, out var fqdn)) return [new Target(raw, type, address.ToString())];
            if (seen.Add(fqdn)) targets.Add(new Target(fqdn, type, address.ToString()));
        }
        return targets;
    }

    private static DnsPlan InvalidPlan(ServerProfile p, string zone, string address, int ttl, string message) =>
        new(p, zone, address, ttl, [new DnsPlanItem("", "", address, DnsPlanAction.Conflict, message)], [], DateTimeOffset.UtcNow);

    private static DnsPlan ConflictPlan(ServerProfile p, string zone, string address, int ttl, IReadOnlyList<Target> targets, string message, IReadOnlyList<string>? ns = null) =>
        new(p, zone, address, ttl, targets.Select(x => new DnsPlanItem(x.Fqdn, x.Type, x.Address, DnsPlanAction.Conflict, message)).ToArray(), ns ?? [], DateTimeOffset.UtcNow);

    private static bool SameIntent(DnsPlan requested, DnsPlan fresh)
    {
        if (!NormalizeDomain(requested.Zone).Equals(fresh.Zone, StringComparison.OrdinalIgnoreCase) || !requested.Address.Equals(fresh.Address, StringComparison.OrdinalIgnoreCase) || requested.Ttl != fresh.Ttl) return false;
        var before = requested.Items.Select(x => (NormalizeDomain(x.Fqdn), x.Type.ToUpperInvariant(), x.Address)).OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
        var after = fresh.Items.Select(x => (NormalizeDomain(x.Fqdn), x.Type.ToUpperInvariant(), x.Address)).OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
        return before.SequenceEqual(after);
    }

    private static void AddRemainder(List<DnsApplyItem> result, IReadOnlyList<DnsPlanItem> items, int from)
    {
        for (var i = from; i < items.Count; i++) result.Add(new DnsApplyItem(items[i].Fqdn, ProbeStatus.Unknown, "Не обработано из-за отмены предыдущего действия."));
    }

    private static bool IsPublicLiteral(string value) => !value.Contains('%') && ProfileValidator.IsPublicIpLiteral(value);
    private static bool AreTimewebNs(IReadOnlyList<string> ns) => ns.Count >= 2 && ns.All(KnownNs.Contains);
    private static bool InZone(string fqdn, string zone) => fqdn.Equals(zone, StringComparison.OrdinalIgnoreCase) || fqdn.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);
    private static string Relative(string fqdn, string zone) => fqdn.Equals(zone, StringComparison.OrdinalIgnoreCase) ? "" : fqdn[..^(zone.Length + 1)];
    private static string Esc(string pathPart) => string.Join("/", pathPart.Split('/').Select(Uri.EscapeDataString));

    private static bool TryDomain(string? value, out string result)
    {
        result = "";
        if (!ProfileValidator.IsValidDomain(value)) return false;
        try
        {
            result = new IdnMapping().GetAscii(value!.Trim().TrimEnd('.')).ToLowerInvariant();
            return result.Length <= 253 && result.Split('.').All(x => x.Length is > 0 and <= 63 && x[0] != '-' && x[^1] != '-');
        }
        catch (ArgumentException) { return false; }
    }

    private static string NormalizeDomain(string? value) => TryDomain(value, out var result) ? result : "";
    private static string? Str(JsonElement obj, string key)
    {
        var value = Prop(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static long? Long(JsonElement obj, string key)
    {
        var value = Prop(obj, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
    private static JsonElement Prop(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in obj.EnumerateObject()) if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    private static JsonElement Nested(JsonElement obj, string parent, string child) => Prop(Prop(obj, parent), child);

    private sealed record Target(string Fqdn, string Type, string Address);
    private sealed record DnsRecord(string Fqdn, string Type, string Value, long? Id);
    private sealed record ApiReply(HttpStatusCode Status, string Body);
    private sealed class DnsApiException(string safeMessage, int status = 0, bool transport = false) : Exception(safeMessage)
    {
        public string SafeMessage { get; } = safeMessage;
        public int StatusCode { get; } = status;
        public bool IsTransportFailure { get; } = transport;
    }
}
