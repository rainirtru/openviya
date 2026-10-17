using OpenViya.Ops.Core;

namespace OpenViya.Ops.Remote;

/// <summary>Exposes only token availability; the central Timeweb credential is never downloaded.</summary>
public sealed class RemoteDnsTokenStore(RemoteOpsClient client) : IDnsTokenStore
{
    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = await client.GetAsync<RemoteDnsCredentialState>("api/dns/token", cancellationToken);
        return state.Configured ? "[configured-on-lan-server]" : null;
    }

    public async Task SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token == "[configured-on-lan-server]" ||
            System.Text.Encoding.UTF8.GetByteCount(token) > 16 * 1024)
            throw new ArgumentException("Введите новый токен Timeweb длиной не более 16 КиБ.");
        await client.PutAsync<object?>("api/dns/token", new RemoteDnsTokenRequest(token), cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        client.DeleteAsync("api/dns/token", cancellationToken);
}

/// <summary>Timeweb operations execute on the LAN server using its encrypted token.</summary>
public sealed class RemoteDnsProviderService(RemoteOpsClient client) : IDnsProviderService
{
    public Task<DnsZoneCheck> CheckZoneAsync(string zone, CancellationToken cancellationToken = default) =>
        client.PostAsync<DnsZoneCheck>("api/dns/check-zone", new { zone }, cancellationToken);

    public Task<DnsPlan> BuildPlanAsync(ServerProfile profile, int ttl = 600, CancellationToken cancellationToken = default) =>
        client.PostAsync<DnsPlan>("api/dns/plan", new { profile = profile with { IdentityFile = "" }, ttl }, cancellationToken);

    public Task<DnsApplyResult> ApplyAsync(DnsPlan plan, CancellationToken cancellationToken = default) =>
        client.PostAsync<DnsApplyResult>("api/dns/apply", plan with { Profile = plan.Profile with { IdentityFile = "" } }, cancellationToken);
}
