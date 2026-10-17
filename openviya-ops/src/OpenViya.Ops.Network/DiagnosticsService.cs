using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Network;

public sealed class DiagnosticsService : IDiagnosticsService
{
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PanelTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<ProbeResult>> CheckServerAsync(ServerProfile server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var results = new List<ProbeResult>();
        if (!ProfileValidator.IsValidHost(server.Host))
        {
            results.Add(ProbeResult.Create("DNS", ProbeStatus.Failure, "Адрес сервера некорректен; DNS-проверка не выполнялась."));
            results.Add(ProbeResult.Create("TCP SSH", ProbeStatus.Unknown, "SSH-порт не проверялся из-за некорректного адреса."));
            return results;
        }
        if (server.SshPort is < 1 or > 65535)
        {
            results.Add(ProbeResult.Create("DNS", ProbeStatus.Unknown, "Адрес задан, но SSH-порт некорректен."));
            results.Add(ProbeResult.Create("TCP SSH", ProbeStatus.Failure, "SSH-порт должен быть от 1 до 65535."));
            return results;
        }

        IReadOnlyList<IPAddress>? hostAddresses = null;
        try
        {
            hostAddresses = await ResolveAsync(server.Host, DnsTimeout, cancellationToken).ConfigureAwait(false);
            results.Add(ProbeResult.Create("DNS сервера", ProbeStatus.Success,
                IPAddress.TryParse(server.Host, out _) ? "Адрес сервера задан IP-литералом." : $"DNS вернул {FormatAddresses(hostAddresses)}."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            results.Add(ProbeResult.Create("DNS сервера", ProbeStatus.Failure, "Не удалось разрешить адрес сервера."));
            results.Add(ProbeResult.Create("TCP SSH", ProbeStatus.Unknown, "SSH-порт не проверялся, так как адрес не разрешился."));
            return results;
        }

        var candidates = hostAddresses!.Take(4).ToArray();
        var tcpResults = await Task.WhenAll(candidates.Select(address => CheckTcpAsync("TCP SSH", address, server.SshPort, TcpTimeout, cancellationToken))).ConfigureAwait(false);
        if (tcpResults.Any(result => result.Status == ProbeStatus.Success))
            results.Add(ProbeResult.Create("TCP SSH", ProbeStatus.Success, $"TCP-порт {server.SshPort} доступен на одном из {candidates.Length} первых адресов {FormatAddresses(candidates)}; это не подтверждает SSH-аутентификацию."));
        else
            results.Add(ProbeResult.Create("TCP SSH", ProbeStatus.Failure, $"TCP-порт {server.SshPort} не ответил ни на одном из проверенных адресов {FormatAddresses(candidates)}."));
        if (ProfileValidator.IsValidDomain(server.PanelDomain))
        {
            try
            {
                var panelAddresses = await ResolveAsync(server.PanelDomain, DnsTimeout, cancellationToken).ConfigureAwait(false);
                var intersects = hostAddresses!.Any(host => panelAddresses.Any(panel => host.Equals(panel)));
                var usesLoopback = panelAddresses.Any(IPAddress.IsLoopback);
                var status = intersects ? ProbeStatus.Success : ProbeStatus.Warning;
                var message = intersects
                    ? $"Системный resolver вернул адрес сервера для домена панели ({FormatAddresses(panelAddresses)}). Это не проверяет HTTPS или сервис панели."
                    : usesLoopback
                        ? $"Системный resolver вернул loopback {FormatAddresses(panelAddresses)} для домена панели. Это похоже на локальный hosts override для туннеля; здесь не проверяется публичная DNS-запись."
                        : $"Системный resolver вернул {FormatAddresses(panelAddresses)} для панели, адрес сервера: {FormatAddresses(hostAddresses!)}. Отличие может быть ожидаемо при proxy/CDN; проверка не обращается напрямую к публичным DNS-серверам.";
                results.Add(ProbeResult.Create("DNS панели", status, message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                results.Add(ProbeResult.Create("DNS панели", ProbeStatus.Failure, "Не удалось разрешить домен панели."));
            }
        }
        else results.Add(ProbeResult.Create("DNS панели", ProbeStatus.Unknown, "Домен панели не задан или некорректен."));

        return results;
    }

    public async Task<ProbeResult> CheckPanelThroughTunnelAsync(ServerProfile server, int localPort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!ProfileValidator.IsValidDomain(server.PanelDomain))
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "Домен панели некорректен.");
        if (server.PanelPort is < 1 or > 65535 || localPort is < 1 or > 65535)
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "Порт панели или локальный порт некорректен.");
        if (!ProfileValidator.IsValidBasePath(server.PanelBasePath))
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "Путь панели некорректен.");

        var builder = new UriBuilder(Uri.UriSchemeHttps, server.PanelDomain, server.PanelPort, server.PanelBasePath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PanelTimeout);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TcpTimeout,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, localPort, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var status = code is >= 200 and < 300 ? ProbeStatus.Success : ProbeStatus.Warning;
            var detail = code is >= 200 and < 300 ? "HTTPS-панель вернула успешный ответ."
                : code is >= 300 and < 400 ? "TLS прошёл, но панель ответила redirect; переход не выполнялся."
                : code == 404 ? "TLS прошёл, но HTTP 404 указывает на неверный путь панели или отсутствующий endpoint."
                : code is 401 or 403 ? "TLS прошёл, но доступ к endpoint требует авторизации или запрещён."
                : $"TLS прошёл, но HTTP вернул статус {code}.";
            return ProbeResult.Create("HTTPS панели через туннель", status,
                $"{detail} Проверены TLS с системной проверкой сертификата/имени и HTTP по локальному туннелю, не VPN-трафик и не готовность Xray.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "Проверка HTTPS через локальный туннель превысила ограниченное время ожидания.");
        }
        catch (HttpRequestException ex) when (ex.GetBaseException() is AuthenticationException)
        {
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "TLS-проверка сертификата или имени панели не прошла.");
        }
        catch (HttpRequestException)
        {
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "Не удалось получить HTTPS-ответ через выбранный локальный туннель.");
        }
        catch (SocketException)
        {
            return ProbeResult.Create("HTTPS панели через туннель", ProbeStatus.Failure, "Не удалось подключиться к выбранному локальному порту туннеля.");
        }
    }

    private static async Task<ProbeResult> CheckTcpAsync(string name, IPAddress address, int port, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return ProbeResult.Create(name, ProbeStatus.Success, $"TCP-соединение с {address}:{port} установлено; это не подтверждает SSH-аутентификацию.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ProbeResult.Create(name, ProbeStatus.Failure, $"Подключение к TCP {address}:{port} превысило ограниченное время ожидания."); }
        catch (SocketException) { return ProbeResult.Create(name, ProbeStatus.Failure, $"TCP-подключение к {address}:{port} не удалось."); }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, TimeSpan limit, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address)) return [address];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        return await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
    }

    private static string FormatAddresses(IEnumerable<IPAddress> addresses) => string.Join(", ", addresses.Select(address => address.ToString()));
    private static bool IsNetworkFailure(Exception ex) => ex is SocketException or ArgumentException or OperationCanceledException;
}
