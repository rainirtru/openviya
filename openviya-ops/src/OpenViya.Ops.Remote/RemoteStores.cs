using System.Text.Json;
using OpenViya.Ops.Core;

namespace OpenViya.Ops.Remote;

/// <summary>Per-device SSH key paths stay local while central server profiles remain path-free.</summary>
public sealed class DeviceIdentityMap
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public DeviceIdentityMap(string dataDirectory) => _path = Path.Combine(dataDirectory, "remote-identities.json");
    public async Task<string> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct); try { var map = await ReadAsync(ct); return map.GetValueOrDefault(id.ToString("D"), ""); } finally { _gate.Release(); }
    }
    public async Task SetAsync(Guid id, string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct); try { var map = await ReadAsync(ct); if (string.IsNullOrWhiteSpace(path)) map.Remove(id.ToString("D")); else map[id.ToString("D")] = path; Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(map, Json), ct); File.Move(temp, _path, true); } finally { _gate.Release(); }
    }
    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, Json, ct) ?? new();
    }
}

public sealed class RemoteProfileStore(RemoteOpsClient client, DeviceIdentityMap identities) : IProfileStore
{
    private readonly Dictionary<Guid, long> _revisions = [];
    public async Task<IReadOnlyList<ServerProfile>> LoadAsync(CancellationToken ct = default)
    {
        var profiles = await client.GetAsync<List<ServerProfile>>("api/profiles", ct);
        var local = new List<ServerProfile>(profiles.Count);
        foreach (var p in profiles) { _revisions[p.Id] = p.Revision; local.Add(p with { IdentityFile = await identities.GetAsync(p.Id, ct) }); }
        return local;
    }
    public async Task SaveAsync(ServerProfile profile, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(profile.IdentityFile)) await identities.SetAsync(profile.Id, profile.IdentityFile, ct);
        var expected = _revisions.GetValueOrDefault(profile.Id, profile.Revision);
        var saved = await client.PutAsync<ServerProfile>($"api/profiles/{profile.Id:D}", profile with { IdentityFile = "", Revision = expected }, ct);
        _revisions[profile.Id] = saved.Revision;
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct = default) { await client.DeleteAsync($"api/profiles/{id:D}", ct); await identities.SetAsync(id, "", ct); }
    public async Task ExportAsync(string filePath, CancellationToken ct = default)
    {
        var profiles = await client.ExportProfilesAsync(ct);
        var safe = profiles.Select(p => new { p.Id, p.Name, p.Role, p.Region, p.NodeIndex, p.Host, p.SshPort, p.UserName, p.PanelDomain, p.PanelPort, p.VpnDomain, p.VpnPort, p.ControlServerId, p.UpdatedAt, p.LocalPort, p.SubdomainMode, p.DnsZone, p.DnsAddress, p.PanelAccess, p.PanelOpenMode, p.PanelAllowedIpsText }).ToArray();
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new { Version = 3, Profiles = safe }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct);
    }
    public async Task<int> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        if (bytes.Length > 2 * 1024 * 1024) throw new ArgumentException("Файл профилей превышает 2 МиБ.");
        var doc = JsonDocument.Parse(bytes);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        var document = doc.RootElement.Deserialize<ProfileImportDocument>(options);
        var profiles = document?.Profiles;
        if (profiles is null && doc.RootElement.ValueKind == JsonValueKind.Array) profiles = doc.RootElement.Deserialize<List<ServerProfile>>(options);
        profiles ??= [];
        var count = await client.ImportProfilesAsync(profiles.Select(x => x with { IdentityFile = "", PanelBasePath = "/" }).ToArray(), ct);
        foreach (var p in profiles) await identities.SetAsync(p.Id, p.IdentityFile, ct);
        return count;
    }
    private sealed record ProfileImportDocument(int Version, List<ServerProfile>? Profiles);
}

public sealed class RemoteServerStateStore(RemoteOpsClient client) : IServerStateStore
{
    public async Task<ServerObservedState?> LoadAsync(Guid id, CancellationToken ct = default)
    { return await client.GetOptionalAsync<ServerObservedState>($"api/servers/{id:D}/observations", ct); }
    public async Task SaveAsync(ServerObservedState state, CancellationToken ct = default) => _ = await client.PutAsync<ServerObservedState>($"api/servers/{state.ServerId:D}/observations", state, ct);
}

public sealed class RemoteDnsRecordStore(RemoteOpsClient client) : IDnsRecordStore
{
    public async Task SaveAsync(DnsManagedRecord record, CancellationToken ct = default)
    {
        var records = await LoadAsync(record.ServerId, ct);
        var merged = records.Where(x => x.RecordId != record.RecordId).Append(record).ToArray();
        _ = await client.PutAsync<DnsManagedRecord[]>($"api/servers/{record.ServerId:D}/dns-records", merged, ct);
    }
    public async Task<IReadOnlyList<DnsManagedRecord>> LoadAsync(Guid id, CancellationToken ct = default) =>
        await client.GetAsync<List<DnsManagedRecord>>($"api/servers/{id:D}/dns-records", ct);
}

public sealed class RemoteProvisionStore(RemoteOpsClient client) : IVpsProvisionStore
{
    public Task SaveRunAsync(VpsProvisionRun run, CancellationToken ct = default) => throw new InvalidOperationException("Состояние удалённого запуска записывает серверная служба.");
    public async Task<VpsProvisionRun?> LoadLatestRunAsync(Guid id, CancellationToken ct = default)
    { return await client.GetOptionalAsync<VpsProvisionRun>($"api/provisioning/runs/latest?serverId={id:D}", ct); }
    public async Task<IReadOnlyList<VpsProvisionRun>> LoadRunHistoryAsync(Guid runId, CancellationToken ct = default) => await client.GetAsync<List<VpsProvisionRun>>($"api/provisioning/runs/{runId:D}/history", ct);
    public Task SaveCredentialsAsync(VpsPanelCredentials credentials, CancellationToken ct = default) => throw new InvalidOperationException("Учётные данные панели сохраняются только сервером.");
    public async Task<VpsPanelCredentials?> LoadCredentialsAsync(Guid id, CancellationToken ct = default)
    { return await client.GetOptionalAsync<VpsPanelCredentials>($"api/servers/{id:D}/credentials", ct); }
}
