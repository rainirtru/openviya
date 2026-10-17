using System.Net.Http;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Network;

public sealed record PublicPanelHttpsResult(ProbeResult Probe, DateTimeOffset? CertificateExpiresAt);

/// <summary>Checks the configured public HTTPS endpoint using the system proxy and OS trust store.</summary>
public sealed class PublicPanelHttpsDiagnostics
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    public async Task<PublicPanelHttpsResult> CheckAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ProfileValidator.IsValidDomain(profile.PanelDomain) || profile.PanelPort is < 1 or > 65535 || !ProfileValidator.IsValidBasePath(profile.PanelBasePath))
            return new(ProbeResult.Create("Публичный HTTPS панели", ProbeStatus.Failure, "Домен, порт или путь панели некорректен."), null);

        DateTimeOffset? certificateExpiresAt = null;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not null && errors == System.Net.Security.SslPolicyErrors.None)
                {
                    certificateExpiresAt = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
                }
                return errors == System.Net.Security.SslPolicyErrors.None;
            }
        };
        using (handler)
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);
            var uri = new UriBuilder(Uri.UriSchemeHttps, profile.PanelDomain, profile.PanelPort, profile.PanelBasePath).Uri;
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                var status = code is >= 200 and < 300 ? ProbeStatus.Success : ProbeStatus.Warning;
                var detail = code is >= 200 and < 300 ? "Публичный HTTPS endpoint вернул успешный ответ."
                    : code is >= 300 and < 400 ? "Системная проверка TLS прошла, но redirect не выполнялся."
                    : code == 404 ? "Системная проверка TLS прошла, но HTTP 404 указывает на неверный путь или отсутствующий endpoint."
                    : code is 401 or 403 ? "Системная проверка TLS прошла, но endpoint требует авторизации или запрещён."
                    : $"Системная проверка TLS прошла, но HTTP вернул статус {code}.";
                return new(ProbeResult.Create("Публичный HTTPS панели", status, detail), certificateExpiresAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                return new(ProbeResult.Create("Публичный HTTPS панели", ProbeStatus.Failure, "Публичная HTTPS-проверка превысила ограниченное время ожидания."), certificateExpiresAt);
            }
            catch (HttpRequestException ex) when (ex.GetBaseException() is System.Security.Authentication.AuthenticationException)
            {
                return new(ProbeResult.Create("Публичный HTTPS панели", ProbeStatus.Failure, "Сертификат публичной панели просрочен, недоверен или не совпадает с именем домена."), certificateExpiresAt);
            }
            catch (HttpRequestException)
            {
                return new(ProbeResult.Create("Публичный HTTPS панели", ProbeStatus.Failure, "Не удалось получить HTTPS-ответ публичной панели."), certificateExpiresAt);
            }
        }
    }
}
