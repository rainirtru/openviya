using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using OpenViya.Ops.Remote;

namespace OpenViya.Ops.Desktop;

public sealed record RemoteConnectionConfig(string BaseAddress, string Fingerprint, string EncryptedToken)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DecryptToken() => System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedToken), null, DataProtectionScope.CurrentUser));
    public RemoteOpsClient CreateClient() => new(BaseAddress, DecryptToken(), Fingerprint);
    public static RemoteConnectionConfig Create(string baseAddress, string fingerprint, string token) => new(baseAddress.Trim().TrimEnd('/'), RemoteOpsClient.NormalizeFingerprint(fingerprint), Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser)));
    public static string FilePath(string root) => Path.Combine(root, "remote-connection.json");
    public static RemoteConnectionConfig? Load(string root)
    {
        var path = FilePath(root); if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RemoteConnectionConfig>(File.ReadAllText(path), Json); }
        catch (CryptographicException) { throw new InvalidOperationException("Не удалось расшифровать токен LAN-сервера для текущего пользователя Windows."); }
        catch (JsonException) { throw new InvalidOperationException("Настройки подключения LAN-сервера повреждены."); }
    }
    public void Save(string root) { Directory.CreateDirectory(root); var temp = FilePath(root) + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this, Json)); File.Move(temp, FilePath(root), true); }
}
