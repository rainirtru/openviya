using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OpenViya.Ops.Core;
using OpenViya.Ops.Network;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static DnsPlan Plan(params DnsPlanItem[] items) => new(
    new ServerProfile { DnsZone = "openviya.online", DnsAddress = "203.0.113.10" },
    "openviya.online", "203.0.113.10", 600, items,
    ["ns1.timeweb.ru", "ns2.timeweb.ru", "ns3.timeweb.org", "ns4.timeweb.org"], DateTimeOffset.UtcNow);
static DnsPlan PlanWithoutExpectedNameServers(params DnsPlanItem[] items) => Plan(items) with { NameServers = [] };

static DnsPlanItem Record(string fqdn, string type, string address) => new(fqdn, type, address, DnsPlanAction.AlreadyConfigured, "test");
static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(body, Encoding.UTF8, "application/dns-json")
};
static string Answer(string name, int type, string data, int ttl = 300) => $$"""{"name":"{{name}}.","type":{{type}},"TTL":{{ttl}},"data":"{{data}}"}""";
static string Response(int status, params string[] answers) => $$"""{"Status":{{status}},"TC":false,"RD":true,"RA":true,"AD":false,"CD":false,"Question":[],"Answer":[{{string.Join(",", answers)}}]}""";
static string NsAnswers() => string.Join(",", new[] { Answer("openviya.online", 2, "ns1.timeweb.ru."), Answer("openviya.online", 2, "ns2.timeweb.ru."), Answer("openviya.online", 2, "ns3.timeweb.org."), Answer("openviya.online", 2, "ns4.timeweb.org.") });
static (PublicDnsService Service, HttpClient Client) MakeService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? clientTimeout = null)
{
    var client = new HttpClient(new StubHandler(send)) { Timeout = clientTimeout ?? Timeout.InfiniteTimeSpan };
    return (new PublicDnsService(client), client);
}
static ProbeResult Find(IReadOnlyList<ProbeResult> results, string type, string fqdn) => results.Single(x => x.Name == $"Публичный DNS {type} {fqdn}");

var ns = Response(0, NsAnswers());
var requests = new ConcurrentBag<Uri>();
var good = MakeService((request, _) =>
{
    requests.Add(request.RequestUri!);
    Check(request.Headers.Accept.Any(x => x.MediaType == "application/dns-json"), "DoH request must advertise application/dns-json.");
    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
    var type = query["type"];
    var name = query["name"]!.TrimEnd('.');
    return Task.FromResult(type switch
    {
        "NS" => Json(ns),
        "A" when name == "vpn.openviya.online" => Json(Response(0, Answer(name, 1, "203.0.113.10"))),
        "AAAA" when name == "v6.openviya.online" => Json(Response(0, Answer(name, 28, "2001:db8::10"))),
        _ => Json(Response(0))
    });
});
using (good.Client)
using (good.Service)
{
    var result = await good.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10"), Record("v6.openviya.online", "AAAA", "2001:db8::10")));
    Check(result.All(x => x.Status == ProbeStatus.Success), "Matching NS, A, and AAAA results should pass.");
    Check(result.All(x => x.Message.Contains("Cloudflare", StringComparison.Ordinal) && x.Message.Contains("рекурсивный DNS", StringComparison.Ordinal)), "Results must name Cloudflare's recursive source explicitly in Russian.");
    Check(result.All(x => x.Message.Contains("TTL", StringComparison.Ordinal) || x.Name == "Публичный DNS NS"), "Address results should disclose the observed TTL.");
    Check(requests.Count == 3 && requests.All(x => x.Host == "cloudflare-dns.com" && x.Scheme == "https"), "All queries must use the explicit Cloudflare HTTPS endpoint.");
}

var mismatch = MakeService((request, _) => Task.FromResult(Json(request.RequestUri!.Query.Contains("type=NS", StringComparison.Ordinal)
    ? Response(0, Answer("openviya.online", 2, "wrong-ns.example."))
    : Response(0, Answer("vpn.openviya.online", 1, "203.0.113.10")))));
using (mismatch.Client)
using (mismatch.Service)
{
    var result = await mismatch.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
    Check(result[0].Status == ProbeStatus.Failure && result[0].Message.Contains("обслуживается другими NS-серверами", StringComparison.OrdinalIgnoreCase), "NS mismatch must report that the zone may be served elsewhere.");
}

var cname = MakeService((request, _) => Task.FromResult(Json(request.RequestUri!.Query.Contains("type=NS", StringComparison.Ordinal)
    ? ns
    : Response(0, Answer("vpn.openviya.online", 5, "other.example."), Answer("other.example", 1, "203.0.113.10")))));
using (cname.Client)
using (cname.Service)
{
    var result = await cname.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
    Check(Find(result, "A", "vpn.openviya.online").Status == ProbeStatus.Failure, "CNAME plus an additional A answer must fail a direct A plan.");
}

foreach (var answerSet in new[]
{
    new[] { Answer("vpn.openviya.online", 1, "203.0.113.10"), Answer("vpn.openviya.online", 1, "203.0.113.10") },
    new[] { Answer("vpn.openviya.online", 1, "203.0.113.10"), Answer("vpn.openviya.online", 1, "203.0.113.11") }
})
{
    var duplicates = MakeService((request, _) => Task.FromResult(Json(request.RequestUri!.Query.Contains("type=NS", StringComparison.Ordinal) ? ns : Response(0, answerSet))));
    using (duplicates.Client)
    using (duplicates.Service)
    {
        var result = await duplicates.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
        Check(Find(result, "A", "vpn.openviya.online").Status == ProbeStatus.Failure, "Duplicate records or an unexpected IP must fail the exact match.");
    }
}

foreach (var noDataStatus in new[] { 0, 3 })
{
    var noData = MakeService((request, _) => Task.FromResult(Json(request.RequestUri!.Query.Contains("type=NS", StringComparison.Ordinal) ? Response(0, NsAnswers()) : Response(noDataStatus))));
    using (noData.Client)
    using (noData.Service)
    {
        var result = await noData.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
        Check(Find(result, "A", "vpn.openviya.online").Status == ProbeStatus.Warning, "NODATA and NXDOMAIN should be a propagation warning.");
    }
}

var httpError = MakeService((_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
using (httpError.Client)
using (httpError.Service)
    Check((await httpError.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")))).All(x => x.Status == ProbeStatus.Failure), "HTTP errors must fail checks without trusting response bodies.");

var malformed = MakeService((request, _) => Task.FromResult(Json(request.RequestUri!.Query.Contains("type=NS", StringComparison.Ordinal) ? "not-json" : Response(0))));
using (malformed.Client)
using (malformed.Service)
    Check((await malformed.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")))).First().Status == ProbeStatus.Failure, "Malformed JSON must fail the relevant check.");

foreach (var malformedTypedJson in new[]
{
    """{"Status":"0","Answer":[]}""",
    """{"Status":0,"Answer":[{"name":"openviya.online.","type":"2","TTL":300,"data":"ns1.example."}]}""",
    """{"Status":0,"Answer":[{"name":"openviya.online.","type":2,"TTL":"300","data":"ns1.example."}]}"""
})
{
    var badField = MakeService((_, _) => Task.FromResult(Json(malformedTypedJson)));
    using (badField.Client)
    using (badField.Service)
    {
        var result = await badField.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
        Check(result[0].Status == ProbeStatus.Failure && result[0].Message.Contains("некоррект", StringComparison.OrdinalIgnoreCase), "Wrong JSON field types must return a Russian failure result instead of escaping.");
    }
}

var manualNoNsRequests = new ConcurrentBag<Uri>();
var manualNoNs = MakeService((request, _) =>
{
    manualNoNsRequests.Add(request.RequestUri!);
    var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
    var name = query["name"]!.TrimEnd('.');
    return Task.FromResult(query["type"] == "NS"
        ? Json(Response(0, NsAnswers()))
        : Json(Response(0, Answer(name, 1, "203.0.113.10"))));
});
using (manualNoNs.Client)
using (manualNoNs.Service)
{
    var result = await manualNoNs.Service.CheckAsync(PlanWithoutExpectedNameServers(Record("vpn.openviya.online", "A", "203.0.113.10")));
    Check(result.Count == 2 && result[0].Status == ProbeStatus.Warning && result[0].Message.Contains("ожидаемый набор не указан; сверка не выполнена", StringComparison.Ordinal), "Manual plans without an expected NS set must report a comparison warning.");
    Check(result[1].Status == ProbeStatus.Success && manualNoNsRequests.Count == 2, "Manual plans without NS must continue checking address records.");
}

var escapedRequests = new ConcurrentBag<Uri>();
var idn = MakeService((request, _) =>
{
    escapedRequests.Add(request.RequestUri!);
    return Task.FromResult(Json(request.RequestUri!.Query.Contains("type=NS", StringComparison.Ordinal) ? ns : Response(0, Answer("xn--caf-dma.openviya.online", 1, "203.0.113.10"))));
});
using (idn.Client)
using (idn.Service)
{
    var result = await idn.Service.CheckAsync(Plan(Record("café.openviya.online", "A", "203.0.113.10")));
    Check(result.All(x => x.Status == ProbeStatus.Success) && escapedRequests.Any(x => x.Query.Contains("xn--caf-dma.openviya.online", StringComparison.Ordinal)), "Internationalized labels must normalize safely before request encoding.");
}

var invalidPlanRequests = 0;
var invalid = MakeService((_, _) => { Interlocked.Increment(ref invalidPlanRequests); return Task.FromResult(Json(ns)); });
using (invalid.Client)
using (invalid.Service)
{
    var result = await invalid.Service.CheckAsync(Plan(Record("outside.example", "A", "203.0.113.10")));
    Check(result.Count == 1 && result[0].Status == ProbeStatus.Failure && invalidPlanRequests == 0, "Cross-zone plans must be rejected before sending requests.");
}

var timeout = MakeService(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); }, TimeSpan.FromMilliseconds(50));
using (timeout.Client)
using (timeout.Service)
{
    var result = await timeout.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
    Check(result[0].Status == ProbeStatus.Failure && result[0].Message.Contains("Истекло время ожидания", StringComparison.OrdinalIgnoreCase), "Transport timeout must be bounded and visible.");
}

var bodyTimeoutClient = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent() }))) { Timeout = Timeout.InfiniteTimeSpan };
using (bodyTimeoutClient)
using (var bodyTimeoutService = new PublicDnsService(bodyTimeoutClient, TimeSpan.FromMilliseconds(50)))
{
    var result = await bodyTimeoutService.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")));
    Check(result[0].Status == ProbeStatus.Failure && result[0].Message.Contains("Истекло время ожидания", StringComparison.OrdinalIgnoreCase), "The same bounded timeout must cover response body reads.");
}

var cancel = MakeService(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); });
using (cancel.Client)
using (cancel.Service)
using (var cancellation = new CancellationTokenSource(50))
{
    try { await cancel.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")), cancellation.Token); throw new InvalidOperationException("Caller cancellation was swallowed."); }
    catch (OperationCanceledException) { }
}

var redirect = MakeService((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com") } }));
using (redirect.Client)
using (redirect.Service)
    Check((await redirect.Service.CheckAsync(Plan(Record("vpn.openviya.online", "A", "203.0.113.10")))).All(x => x.Status == ProbeStatus.Failure), "Redirects must fail closed.");

Console.WriteLine("Public DNS checks passed: Cloudflare HTTPS, A/AAAA, NS matching and manual no-NS warning, exact records, CNAME/duplicates, IDN normalization, malformed typed JSON/HTTP/timeout/cancellation, and cross-zone rejection. No public DNS request was sent.");

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

sealed class SlowContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowStream());
}

sealed class SlowStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
