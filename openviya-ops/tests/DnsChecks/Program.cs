using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using OpenViya.Ops.Core;
using OpenViya.Ops.Dns;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("nested NS and wrapped v1 schemas", ZoneAndWrappers),
    ("provider SPF defaults allow address creation, retry and lost response recovery", ProviderDefaults),
    ("NS 404 uses public delegation and automatically creates both usa-2 records", MissingRegistrarNs),
    ("NS fallback rejects foreign, unrelated, failed and malformed answers", InvalidPublicNs),
    ("NS 403 never falls back", ForbiddenNs),
    ("automatic plan and paginated records", PlanAndPagination),
    ("subdomain then v2 record sequence", ApplySequence),
    ("matching record skips duplicate POST", ExistingRecord),
    ("equivalent IPv6 records skip duplicate POST", EquivalentIpv6Record),
    ("CNAME and duplicate conflicts", Conflicts),
    ("manual mode never calls API during apply", ManualNoWrites),
    ("partial success", PartialSuccess),
    ("lost POST response reconciliation", LostPost),
    ("accepted but unverified response stays warning", UnverifiedPost),
    ("zone, address, and token validation", Validation),
    ("401, 403 and token redaction", AuthAndRedaction),
    ("GET 429 bounded retry", Retry429),
    ("request timeout covers response body reads", ResponseBodyTimeout),
    ("cancellation before request", Cancellation)
};
foreach (var (name, run) in checks) { await run(); Console.WriteLine($"PASS {name}"); }
Console.WriteLine($"DNS checks passed: {checks.Count}");

static async Task MissingRegistrarNs()
{
    var f = new FakeTimeweb { NsStatus = HttpStatusCode.NotFound };
    var doh = new FakePublicNs();
    using var api = new HttpClient(f);
    using var publicHttp = new HttpClient(doh);
    using var client = new TimewebDnsClient(new MemoryTokenStore("fake-token"), api, publicDnsClient: publicHttp);
    var profile = new ServerProfile { Host = "8.8.8.8", Region = "usa", NodeIndex = 2 };
    var plan = await client.BuildPlanAsync(profile);
    Check(plan.CanApply && plan.Items.Count == 2, "NS 404 must not block valid public Timeweb delegation.");
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Count == 2 && result.Items.All(x => x.Status == ProbeStatus.Success), "Both DNS records must be created.");
    Check(f.Subdomains.SetEquals(["usa-2.openviya.online", "panel-usa-2.openviya.online"]), "Expected automatic usa-2 names.");
    Check(doh.Calls > 0, "Expected public NS fallback.");
    var repeat = await client.ApplyAsync(plan);
    Check(repeat.Items.All(x => x.Status == ProbeStatus.Success) && f.Requests.Count(x => x.Method == "POST" && x.Path.Contains("/dns-records")) == 2,
        "Retry must skip existing matching records.");
    f.AddRecord("panel-usa-2.openviya.online", "A", "1.1.1.1", 999);
    var conflict = await client.BuildPlanAsync(profile);
    Check(!conflict.CanApply, "Conflicting existing records must still block automatic writes.");
}

static async Task InvalidPublicNs()
{
    foreach (var body in new[] { FakePublicNs.Body.Replace("ns1.timeweb.ru", "ns1.example.org"),
        FakePublicNs.Body.Replace("openviya.online.", "unrelated.online."),
        FakePublicNs.Body.Replace("\"Status\":0", "\"Status\":2"), "{}", "invalid" })
    {
        var f = new FakeTimeweb { NsStatus = HttpStatusCode.NotFound };
        using var api = new HttpClient(f);
        using var doh = new HttpClient(new FakePublicNs { ResponseBody = body });
        using var client = new TimewebDnsClient(new MemoryTokenStore("fake-token"), api, publicDnsClient: doh);
        var plan = await client.BuildPlanAsync(ControlProfile());
        Check(!plan.CanApply && !f.Requests.Any(x => x.Method == "POST"), "Unconfirmed delegation must fail closed.");
    }
}

static async Task ForbiddenNs()
{
    var f = new FakeTimeweb { NsStatus = HttpStatusCode.Forbidden };
    var doh = new FakePublicNs();
    using var api = new HttpClient(f);
    using var publicHttp = new HttpClient(doh);
    using var client = new TimewebDnsClient(new MemoryTokenStore("fake-token"), api, publicDnsClient: publicHttp);
    var plan = await client.BuildPlanAsync(ControlProfile());
    Check(!plan.CanApply && doh.Calls == 0, "NS fallback must not bypass token permission failures.");
}

static async Task ProviderDefaults()
{
    foreach (var existing in new[] { false, true })
    foreach (var lost in new[] { false, true })
    {
        var f = new FakeTimeweb { AddSpfOnCreate = true };
        var profile = new ServerProfile { Host = "8.8.8.8", Region = "usa", NodeIndex = 2 };
        var names = new[] { "usa-2.openviya.online", "panel-usa-2.openviya.online" };
        if (existing)
            foreach (var name in names) { f.Subdomains.Add(name); f.AddRecord(name, "TXT", "v=spf1 include:_spf.timeweb.ru ~all", 70); f.AddRecord(name, "MX", "mx1.timeweb.ru", 71); }
        if (lost) f.LoseCreateResponseFor = names[0];
        using var client = Client(f);
        var plan = await client.BuildPlanAsync(profile);
        Check(plan.CanApply, "SPF/MX must not block a plan.");
        var result = await client.ApplyAsync(plan);
        Check(result.Items.Count == 2 && result.Items.All(x => x.Status == ProbeStatus.Success), "Provider SPF must coexist with newly created A records, including lost POST recovery.");
        Check(names.All(name => f.Records[name].Any(x => x.Type == "TXT") && f.Records[name].Count(x => x.Type == "A") == 1), "Keep SPF and create exactly one A per name.");
        var postCount = f.Requests.Count(x => x.Method == "POST");
        var repeat = await client.ApplyAsync(plan);
        Check(repeat.Items.All(x => x.Status == ProbeStatus.Success) && f.Requests.Count(x => x.Method == "POST") == postCount, "Retry must recognize A alongside SPF without duplicate POST.");
    }
}

static async Task ZoneAndWrappers()
{
    var f = new FakeTimeweb { WrapV1 = true };
    using var client = Client(f);
    var zone = await client.CheckZoneAsync("openviya.online");
    Check(zone.Accessible && zone.NameServers.Count == 4, "Expected accessible zone and flattened NS.items.");
    Check(f.Requests.Any(x => x.Path == "/api/v1/domains/openviya.online/name-servers"), "Expected NS request.");
}

static async Task PlanAndPagination()
{
    var f = new FakeTimeweb();
    f.Subdomains.Add("panel-control.openviya.online");
    f.RecordPages = true;
    for (var i = 0; i < 101; i++) f.AddRecord("panel-control.openviya.online", "TXT", $"other-{i}");
    using var client = Client(f);
    var p = await client.BuildPlanAsync(ControlProfile());
    Check(p.Items.Single().Action == DnsPlanAction.Create, "Unrelated TXT records must not conflict.");
    Check(f.Requests.Any(x => x.Path.Contains("limit=100&offset=100", StringComparison.Ordinal)), "Expected second page read.");
}

static async Task ApplySequence()
{
    var f = new FakeTimeweb();
    using var client = Client(f);
    var plan = await client.BuildPlanAsync(ControlProfile());
    Check(plan.Items.Single().Action == DnsPlanAction.Create, "Expected a create plan.");
    f.Requests.Clear();
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Single().Status == ProbeStatus.Success && result.Items[0].RecordId == 2001, "Expected verified record and ID.");
    var createSub = f.Requests.FindIndex(x => x.Method == "POST" && x.Path.EndsWith("/subdomains/panel-control", StringComparison.Ordinal));
    var createRecord = f.Requests.FindIndex(x => x.Method == "POST" && x.Path == "/api/v2/domains/panel-control.openviya.online/dns-records");
    Check(createSub >= 0 && createRecord > createSub, "Subdomain must be created before v2 DNS record.");
    Check(f.Requests.Skip(createSub + 1).Take(createRecord - createSub - 1).Any(x => x.Method == "GET" && x.Path.EndsWith("/dns-records?limit=100&offset=0", StringComparison.Ordinal)), "Expected a read after creating the subdomain.");
    using var body = JsonDocument.Parse(f.Requests[createRecord].Body!);
    Check(body.RootElement.GetProperty("type").GetString() == "A" && body.RootElement.GetProperty("value").GetString() == "8.8.8.8" && body.RootElement.GetProperty("ttl").GetInt32() == 600, "v2 payload must be flat and use the public address.");
    Check(f.Requests.All(x => x.Authorization == "Bearer fake-token"), "Bearer token must be sent in header.");
}

static async Task ExistingRecord()
{
    var f = new FakeTimeweb();
    f.Subdomains.Add("panel-control.openviya.online");
    f.AddRecord("panel-control.openviya.online", "A", "8.8.8.8", 442);
    using var client = Client(f);
    var plan = await client.BuildPlanAsync(ControlProfile());
    Check(plan.Items.Single().Action == DnsPlanAction.AlreadyConfigured && plan.Items[0].RecordId == 442, "Exact record should be reported configured with ID.");
    f.Requests.Clear();
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Single().Status == ProbeStatus.Success && !f.Requests.Any(x => x.Method == "POST"), "Matching record should skip POST.");
}

static async Task EquivalentIpv6Record()
{
    var f = new FakeTimeweb();
    f.Subdomains.Add("panel-control.openviya.online");
    f.AddRecord("panel-control.openviya.online", "AAAA", "2001:4860:4860:0000:0000:0000:0000:8888", 443);
    using var client = Client(f);
    var plan = await client.BuildPlanAsync(ControlProfile() with { DnsAddress = "2001:4860:4860::8888" });
    Check(plan.Items.Single().Action == DnsPlanAction.AlreadyConfigured, "IPv6 notation must not cause a false DNS conflict.");
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Single().Status == ProbeStatus.Success && !f.Requests.Any(x => x.Method == "POST"), "Equivalent IPv6 must skip creation.");
}

static async Task Conflicts()
{
    foreach (var scenario in new[] { new[] { "CNAME", "edge.example.net" }, new[] { "A", "9.9.9.9" }, new[] { "DUP", "" } })
    {
        var f = new FakeTimeweb(); f.Subdomains.Add("panel-control.openviya.online");
        if (scenario[0] == "DUP")
        {
            f.AddRecord("panel-control.openviya.online", "A", "8.8.8.8", 10);
            f.AddRecord("panel-control.openviya.online", "A", "8.8.8.8", 11);
        }
        else f.AddRecord("panel-control.openviya.online", scenario[0], scenario[1], 10);
        using var client = Client(f);
        var plan = await client.BuildPlanAsync(ControlProfile());
        Check(plan.Items.Single().Action == DnsPlanAction.Conflict, "CNAME, different values, and duplicates must conflict.");
        f.Requests.Clear(); await client.ApplyAsync(plan);
        Check(!f.Requests.Any(x => x.Method == "POST"), "Conflict must block all writes.");
    }
}
static async Task ManualNoWrites()
{
    var f = new FakeTimeweb(); using var client = Client(f);
    var profile = ControlProfile() with { SubdomainMode = SubdomainMode.Manual, PanelDomain = "custom-panel.openviya.online" };
    var plan = await client.BuildPlanAsync(profile);
    f.Requests.Clear();
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Single().Status == ProbeStatus.Warning && f.Requests.Count == 0, "Manual Apply must not load or call provider API.");
}

static async Task PartialSuccess()
{
    var f = new FakeTimeweb { FailCreateFor = "panel-usa-1.openviya.online" };
    using var client = Client(f);
    var profile = new ServerProfile { Host = "8.8.8.8", DnsZone = "openviya.online", Role = ServerRole.VpnNode, Region = "usa" };
    var plan = await client.BuildPlanAsync(profile);
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Count == 2 && result.Items.Any(x => x.Fqdn.StartsWith("usa-", StringComparison.Ordinal) && x.Status == ProbeStatus.Success) && result.Items.Any(x => x.Fqdn.StartsWith("panel-", StringComparison.Ordinal) && x.Status == ProbeStatus.Unknown), "Each name should retain its own partial result; a 500 without read-back is unknown.");
    Check(f.Records.Values.SelectMany(x => x).Any(x => x.Value == "8.8.8.8"), "Successful record must remain after a partial failure.");
}

static async Task LostPost()
{
    var f = new FakeTimeweb { LoseCreateResponseFor = "panel-control.openviya.online" };
    using var client = Client(f);
    var plan = await client.BuildPlanAsync(ControlProfile());
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Single().Status == ProbeStatus.Success && result.Items[0].RecordId == 2001, "Lost response should reconcile from subsequent read.");
    Check(f.Requests.Count(x => x.Method == "POST" && x.Path.StartsWith("/api/v2/", StringComparison.Ordinal)) == 1, "POST must not retry after unknown outcome.");
}

static async Task UnverifiedPost()
{
    var f = new FakeTimeweb { AcceptButDoNotPersistFor = "panel-control.openviya.online" };
    using var client = Client(f);
    var plan = await client.BuildPlanAsync(ControlProfile());
    var result = await client.ApplyAsync(plan);
    Check(result.Items.Single().Status == ProbeStatus.Warning && result.Items[0].RecordId == 2001, "Response ID is retained, but missing read-back must not be green.");
}

static async Task Validation()
{
    var f = new FakeTimeweb(); using var client = Client(f);
    var invalidName = await client.BuildPlanAsync(ControlProfile() with { SubdomainMode = SubdomainMode.Manual, PanelDomain = "panel.other.net" });
    Check(invalidName.Items.Single().Action == DnsPlanAction.Conflict && f.Requests.Count == 0, "Out-of-zone manual domain must fail before API access.");
    foreach (var address in new[] { "203.0.113.10", "10.0.0.1", "2001:4860::8888%3" })
    {
        var plan = await client.BuildPlanAsync(ControlProfile() with { DnsAddress = address });
        Check(plan.Items.Single().Action == DnsPlanAction.Conflict, $"Nonpublic/scoped address {address} must be refused.");
    }
    var hostName = await client.BuildPlanAsync(ControlProfile() with { Host = "vps.example.com", DnsAddress = "" });
    Check(hostName.Items.Single().Action == DnsPlanAction.Conflict, "SSH hostname must not become A/AAAA value.");
}

static async Task AuthAndRedaction()
{
    foreach (var status in new HttpStatusCode[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
    {
        var f = new FakeTimeweb { AlwaysStatus = status };
        using var client = Client(f);
        var result = await client.CheckZoneAsync("openviya.online");
        Check(!result.Accessible && !result.Message.Contains("fake-token", StringComparison.Ordinal) && !result.Message.Contains("SECRET-BODY", StringComparison.Ordinal), "Auth error must be sanitized.");
    }
}

static async Task Retry429()
{
    var f = new FakeTimeweb { RateLimitPathOnce = "/api/v1/domains/openviya.online/name-servers" };
    using var client = Client(f);
    var result = await client.CheckZoneAsync("openviya.online");
    Check(result.Accessible && f.Requests.Count(x => x.Path.EndsWith("/name-servers", StringComparison.Ordinal)) == 2, "GET 429 should retry once with Retry-After.");
}

static async Task Cancellation()
{
    var f = new FakeTimeweb(); using var client = Client(f);
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await client.CheckZoneAsync("openviya.online", cts.Token); throw new Exception("Expected cancellation."); }
    catch (OperationCanceledException) { }
    Check(f.Requests.Count == 0, "Canceled operation should not start a request.");
}

static async Task ResponseBodyTimeout()
{
    var handler = new SlowBodyHandler();
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    using var client = new TimewebDnsClient(new MemoryTokenStore("fake-token"), http, TimeSpan.FromMilliseconds(80));
    var result = await client.CheckZoneAsync("openviya.online");
    Check(!result.Accessible && result.Message.Contains("ограниченное время", StringComparison.OrdinalIgnoreCase), "A response body that stalls after headers must hit the per-request timeout.");
}

static ServerProfile ControlProfile() => new() { Role = ServerRole.Control, Host = "8.8.8.8", DnsZone = "openviya.online" };
static TimewebDnsClient Client(FakeTimeweb fake) => new(new MemoryTokenStore("fake-token"), new HttpClient(fake));
static void Check(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); }

sealed class MemoryTokenStore(string token) : IDnsTokenStore
{
    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(token);
    public Task SaveAsync(string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class FakeTimeweb : HttpMessageHandler
{
    public sealed record Rec(string Type, string Value, long Id, string? Name = null);
    public sealed record Seen(string Method, string Path, string? Body, string? Authorization);
    public List<Seen> Requests { get; } = [];
    public HashSet<string> Subdomains { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<Rec>> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Defaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool WrapV1 { get; set; }
    public bool AddSpfOnCreate { get; set; }
    public HttpStatusCode? NsStatus { get; set; }
    public bool RecordPages { get; set; }
    public string? FailCreateFor { get; set; }
    public string? LoseCreateResponseFor { get; set; }
    public string? AcceptButDoNotPersistFor { get; set; }
    public string? RateLimitPathOnce { get; set; }
    public HttpStatusCode? AlwaysStatus { get; set; }
    private int _id = 2000;

    public void AddRecord(string fqdn, string type, string value, long id = 1) { if (!Records.TryGetValue(fqdn, out var list)) Records[fqdn] = list = []; list.Add(new Rec(type, value, id)); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath + request.RequestUri.Query;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Seen(request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
        if (request.RequestUri.Scheme != "https" || request.RequestUri.Host != "api.timeweb.cloud") throw new InvalidOperationException("Request escaped fixed Timeweb API origin.");
        if (AlwaysStatus is { } forced) return Json((int)forced, "{\"error\":\"SECRET-BODY fake-token\"}");
        if (RateLimitPathOnce == request.RequestUri.AbsolutePath) { RateLimitPathOnce = null; var response = Json(429, "{}"); response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero); return response; }
        var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == HttpMethod.Get && segments.Length == 3 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "domains") return Json(404, "{}");
        if (request.Method == HttpMethod.Get && path == "/api/v1/domains/openviya.online")
        {
            var subs = Subdomains.Select(x => new { fqdn = x }).ToArray();
            var domain = new { fqdn = "openviya.online", subdomains = subs };
            return Json(200, WrapV1 ? JsonSerializer.Serialize(new { data = new { domain } }) : JsonSerializer.Serialize(new { domain }));
        }
        if (request.Method == HttpMethod.Get && path == "/api/v1/domains/openviya.online/name-servers")
        {
            if (NsStatus is { } status) return Json((int)status, "{}");
            var ns = new[] { "ns1.timeweb.ru", "ns2.timeweb.ru", "ns3.timeweb.org", "ns4.timeweb.org" }.Select(x => new { task_status = "done", items = new[] { new { host = x, ips = Array.Empty<string>() } } }).ToArray();
            return Json(200, WrapV1 ? JsonSerializer.Serialize(new { data = new { name_servers = ns } }) : JsonSerializer.Serialize(new { name_servers = ns }));
        }
        if (request.Method == HttpMethod.Get && segments.Length == 5 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "domains" && segments[^1] is "dns-records" or "default-dns-records")
        {
            var fqdn = segments[3]; var isDefault = segments[4] == "default-dns-records";
            var records = Records.GetValueOrDefault(fqdn) ?? [];
            if (isDefault) records = records.Where(r => Defaults.Contains(fqdn)).ToList();
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var offset = int.TryParse(query["offset"], out var o) ? o : 0;
            var page = records.Skip(offset).Take(100).Select(r => new { id = r.Id, type = r.Type, data = new { value = r.Value, subdomain = r.Name ?? (fqdn == "openviya.online" ? null : WrapV1 && r.Type == "A" ? fqdn[..(fqdn.Length - ".openviya.online".Length)] : fqdn) } }).ToArray();
            var payload = JsonSerializer.Serialize(new { meta = new { total = records.Count }, dns_records = page });
            return Json(200, WrapV1 ? JsonSerializer.Serialize(new { data = JsonSerializer.Deserialize<JsonElement>(payload) }) : payload);
        }
        if (request.Method == HttpMethod.Post && segments.Length == 6 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "domains" && segments[4] == "subdomains")
        {
            var full = segments[5] + "." + segments[3]; Subdomains.Add(full);
            if (AddSpfOnCreate) AddRecord(full, "TXT", "v=spf1 include:_spf.timeweb.ru ~all", ++_id);
            return Json(201, JsonSerializer.Serialize(new { subdomain = new { fqdn = full, id = 55 } }));
        }
        if (request.Method == HttpMethod.Post && segments.Length == 5 && segments[0] == "api" && segments[1] == "v2" && segments[2] == "domains" && segments[4] == "dns-records")
        {
            var fqdn = segments[3];
            if (FailCreateFor == fqdn) return Json(500, "{\"error\":\"fake-token SECRET-BODY\"}");
            using var json = JsonDocument.Parse(body!);
            var type = json.RootElement.GetProperty("type").GetString()!; var value = json.RootElement.GetProperty("value").GetString()!;
            var id = ++_id;
            if (AcceptButDoNotPersistFor != fqdn) AddRecord(fqdn, type, value, id);
            if (LoseCreateResponseFor == fqdn) throw new HttpRequestException("socket reset after remote commit");
            return Json(201, JsonSerializer.Serialize(new { dns_record = new { id, type, fqdn, data = new { value } } }));
        }
        return Json(404, "{}");
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

sealed class FakePublicNs : HttpMessageHandler
{
    public const string Body = """{"Status":0,"Answer":[{"name":"openviya.online.","type":2,"data":"ns1.timeweb.ru."},{"name":"openviya.online.","type":2,"data":"ns2.timeweb.ru."}]}""";
    public string ResponseBody { get; init; } = Body;
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.Host != "cloudflare-dns.com" || request.Method != HttpMethod.Get || request.Headers.Authorization is not null)
            throw new InvalidOperationException("Public NS request must use fixed DoH endpoint without provider token.");
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseBody) });
    }
}

sealed class SlowBodyHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri!.AbsolutePath.EndsWith("/name-servers", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowReadStream()) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"domain\":{\"fqdn\":\"openviya.online\",\"subdomains\":[]}}") });
    }
}

sealed class SlowReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => WaitAsync(cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WaitTaskAsync(cancellationToken);
    private static async ValueTask<int> WaitAsync(CancellationToken cancellationToken) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    private static async Task<int> WaitTaskAsync(CancellationToken cancellationToken) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
