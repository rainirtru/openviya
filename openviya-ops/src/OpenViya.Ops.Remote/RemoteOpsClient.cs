using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Remote;

/// <summary>Authenticated HTTPS transport. The remote server certificate must match the user-confirmed SHA-256 pin.</summary>
public sealed class RemoteOpsClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http;
    public Uri BaseAddress { get; }

    public RemoteOpsClient(string baseAddress, string bearerToken, string certificateSha256)
    {
        if (!Uri.TryCreate(baseAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Адрес сервера должен начинаться с https://.", nameof(baseAddress));
        if (string.IsNullOrWhiteSpace(bearerToken)) throw new ArgumentException("Введите токен доступа к серверу.", nameof(bearerToken));
        var pin = NormalizeFingerprint(certificateSha256);
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new ArgumentException("Укажите SHA-256 fingerprint сертификата (64 шестнадцатеричных символа).", nameof(certificateSha256));
        BaseAddress = uri;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            {
                if (certificate is null) return false;
                try
                {
                    var cert = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                    if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
                    var now = DateTime.UtcNow;
                    if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return false;
                    return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pin), SHA256.HashData(cert.RawData));
                }
                catch { return false; }
            }
        };
        _http = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string NormalizeFingerprint(string value) => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default) => await SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
    public async Task<T?> GetOptionalAsync<T>(string path, CancellationToken ct = default)
    {
        try { return await GetAsync<T>(path, ct).ConfigureAwait(false); }
        catch (RemoteApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return default; }
    }
    public async Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default) => await SendJsonAsync<T>(HttpMethod.Put, path, body, ct).ConfigureAwait(false);
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default) => await SendJsonAsync<T>(HttpMethod.Post, path, body, ct).ConfigureAwait(false);
    public async Task DeleteAsync(string path, CancellationToken ct = default) => await SendAsync<object?>(new HttpRequestMessage(HttpMethod.Delete, path), ct, allowEmpty: true).ConfigureAwait(false);

    public async Task<int> ImportProfilesAsync(IReadOnlyList<ServerProfile> profiles, CancellationToken ct = default)
    {
        var safe = profiles.Select(p => new { p.Id, p.Name, p.Role, p.Region, p.NodeIndex, p.Host, p.SshPort, p.UserName, p.PanelDomain, p.PanelPort, p.VpnDomain, p.VpnPort, p.ControlServerId, p.UpdatedAt, p.LocalPort, p.SubdomainMode, p.DnsZone, p.DnsAddress, p.PanelAccess, p.PanelOpenMode, p.PanelAllowedIpsText, p.PanelBasePath }).ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Profiles = safe }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(content, "file", "openviya-ops-profiles.json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/profiles/import") { Content = form };
        var result = await SendAsync<RemoteImportResult>(request, ct).ConfigureAwait(false);
        return result.Imported;
    }

    public async Task<IReadOnlyList<ServerProfile>> ExportProfilesAsync(CancellationToken ct = default) =>
        await GetAsync<List<ServerProfile>>("api/profiles/export", ct).ConfigureAwait(false);

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> PostBytesAsync(string path, object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, Json);
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return await SendAsync<T>(request, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct, bool allowEmpty = false)
    {
        using (request)
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            if (allowEmpty || response.StatusCode == HttpStatusCode.NoContent) return default!;
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Сервер не принял токен доступа.",
            HttpStatusCode.NotFound => "Сервер не поддерживает эту операцию или объект не найден.",
            HttpStatusCode.Conflict => "Данные изменились на сервере. Обновите список и повторите действие.",
            _ => "Сервер OpenViya Ops не выполнил запрос. Проверьте его состояние и сетевое подключение."
        };
        throw new RemoteApiException(message + $" (HTTP {(int)response.StatusCode}).", response.StatusCode);
    }
    public void Dispose() => _http.Dispose();
}

public sealed class RemoteApiException(string message, HttpStatusCode statusCode) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
