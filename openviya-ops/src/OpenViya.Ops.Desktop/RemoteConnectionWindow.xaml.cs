using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using OpenViya.Ops.Core;
using OpenViya.Ops.Remote;
using OpenViya.Ops.Storage;

namespace OpenViya.Ops.Desktop;

public partial class RemoteConnectionWindow : Window
{
    private readonly string _dataDirectory;
    private readonly RemoteConnectionConfig? _current;
    private RemoteConnectionConfig? _tested;
    private bool _loading;
    public RemoteConnectionWindow(string dataDirectory, RemoteConnectionConfig? current)
    {
        InitializeComponent(); _dataDirectory = dataDirectory; _current = current; _loading = true;
        ModeBox.SelectedIndex = current is null ? 0 : 1;
        if (current is not null) { UrlBox.Text = current.BaseAddress; FingerprintBox.Text = current.Fingerprint; }
        _loading = false; UpdateMode();
        if (current is not null) MigrationSummary.Text = "Перенос локальных данных доступен при первом переходе с локального хранилища. Изменение режима не удаляет локальную базу.";
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateMode(); }
    private void ConnectionField_Changed(object sender, TextChangedEventArgs e) { if (!_loading) { _tested = null; MigrateButton.IsEnabled = false; } }
    private bool IsRemote => (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string == "remote";
    private void UpdateMode() { RemoteFields.Visibility = IsRemote ? Visibility.Visible : Visibility.Collapsed; SaveButton.Content = IsRemote ? "Сохранить для следующего запуска" : "Перейти на локальное хранилище"; }

    private void RevealToken_Changed(object sender, RoutedEventArgs e)
    {
        if (TokenBox is null || TokenRevealBox is null || RevealTokenBox is null) return;
        if (RevealTokenBox.IsChecked == true) { TokenRevealBox.Text = TokenBox.Password; TokenBox.Visibility = Visibility.Collapsed; TokenRevealBox.Visibility = Visibility.Visible; }
        else { TokenBox.Password = TokenRevealBox.Text; TokenRevealBox.Clear(); TokenRevealBox.Visibility = Visibility.Collapsed; TokenBox.Visibility = Visibility.Visible; }
    }

    private RemoteConnectionConfig BuildConfig()
    {
        var token = RevealTokenBox.IsChecked == true ? TokenRevealBox.Text : TokenBox.Password;
        if (string.IsNullOrWhiteSpace(token) && _tested is not null) token = _tested.DecryptToken();
        if (string.IsNullOrWhiteSpace(token) && _current is not null) token = _current.DecryptToken();
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Введите токен доступа к серверу.");
        return RemoteConnectionConfig.Create(UrlBox.Text, FingerprintBox.Text, token);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false; MigrateButton.IsEnabled = false; StatusText.Text = "Проверяем HTTPS-соединение и fingerprint…";
        try
        {
            var candidate = BuildConfig(); using var client = candidate.CreateClient();
            _ = await client.GetAsync<List<ServerProfile>>("api/profiles"); _tested = candidate;
            TokenBox.Clear(); TokenRevealBox.Clear(); MigrateButton.IsEnabled = _current is null; StatusText.Text = "Защищённое соединение подтверждено. Fingerprint совпал, токен принят сервером.";
        }
        catch (Exception ex) { _tested = null; StatusText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "Проверка не выполнена. Проверьте адрес, fingerprint, токен и доступность сервера."; }
        finally { TestButton.IsEnabled = true; }
    }

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        if (_tested is null || !TestMatchesCurrentFields()) { StatusText.Text = "Сначала проверьте текущее HTTPS-адрес, fingerprint и токен."; return; }
        MigrateButton.IsEnabled = false;
        try
        {
            var paths = new AppPaths(_dataDirectory); var localProfiles = await new SqliteProfileStore(paths).LoadAsync();
            var trustedKeys = IncludeTrustedHostKeysBox.IsChecked == true ? ReadTrustedHostKeys(paths, localProfiles) : new Dictionary<Guid, HostKeyCandidate>();
            MigrationSummary.Text = $"Будут скопированы {localProfiles.Count} профилей, сохранённые наблюдения и управляемые DNS-записи. Явно принятых SSH host keys: {trustedKeys.Count}. Локальные данные не удаляются.";
            var confirmText = $"Импортировать {localProfiles.Count} профилей на сервер {_tested.BaseAddress}? Уже существующие профили сервер сохранит. Выбранные дополнительные данные, сохранённые наблюдения и управляемые DNS-записи для совпадающих ID будут обновлены значениями с этого устройства. Локальная база останется на этом компьютере.";
            if (IncludePanelPathsBox.IsChecked == true) confirmText += " Сохранённые пути панелей будут расшифрованы на этом ПК и переданы через закреплённое HTTPS-соединение.";
            if (IncludePanelCredentialsBox.IsChecked == true) confirmText += " Сохранённые учётные данные панелей будут расшифрованы через DPAPI на этом ПК и переданы через закреплённое HTTPS-соединение.";
            if (IncludeSshKeysBox.IsChecked == true) confirmText += " Закрытые SSH-ключи из профилей будут переданы через закреплённое HTTPS-соединение и сохранены в зашифрованном хранилище сервера.";
            if (IncludeDnsTokenBox.IsChecked == true) confirmText += " Токен Timeweb DNS будет расшифрован через DPAPI на этом ПК и передан серверу для операций DNS.";
            if (IncludeTrustedHostKeysBox.IsChecked == true) confirmText += $" Будут также переданы {trustedKeys.Count} ранее явно принятых публичных SSH host keys.";
            if (!OwnedDialog.Confirm(this, "Проверка переноса", confirmText, "Скопировать данные")) return;
            using var client = _tested.CreateClient();
            var identityMap = new DeviceIdentityMap(_dataDirectory);
            var provisionStore = new DpapiVpsProvisionStore(paths);
            var sharedProfiles = localProfiles.Select(profile => profile with
            {
                IdentityFile = "",
                PanelBasePath = IncludePanelPathsBox.IsChecked == true ? profile.PanelBasePath : "/"
            }).ToList();
            var importedCount = await client.ImportProfilesAsync(sharedProfiles);
            foreach (var profile in localProfiles)
                await identityMap.SetAsync(profile.Id, profile.IdentityFile);
            var skippedEncryptedKeys = 0;
            foreach (var profile in localProfiles)
            {
                if (trustedKeys.TryGetValue(profile.Id, out var hostKey)) _ = await client.PutAsync<HostKeyCandidate>($"api/servers/{profile.Id:D}/host-key", hostKey);
                if (IncludeSshKeysBox.IsChecked == true && !string.IsNullOrWhiteSpace(profile.IdentityFile))
                {
                    var keyPath = profile.IdentityFile;
                    var keyInfo = new FileInfo(keyPath);
                    if (!keyInfo.Exists || keyInfo.Length is 0 or > 128 * 1024) throw new InvalidOperationException($"Закрытый ключ профиля «{profile.Name}» отсутствует, пуст или превышает 128 КиБ.");
                    var keyBytes = await File.ReadAllBytesAsync(keyPath);
                    if (keyBytes.Length is 0 or > 128 * 1024) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes); throw new InvalidOperationException($"Закрытый ключ профиля «{profile.Name}» превышает 128 КиБ."); }
                    try
                    {
                        if (IsEncryptedPrivateKey(keyBytes)) skippedEncryptedKeys++;
                        else _ = await client.PutAsync<JsonElement>($"api/servers/{profile.Id:D}/admin-key", new RemoteAdminKeyRequest(Convert.ToBase64String(keyBytes)));
                    }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes); }
                }
                var observation = await new SqliteServerStateStore(paths).LoadAsync(profile.Id);
                if (observation is not null) _ = await client.PutAsync<ServerObservedState>($"api/servers/{profile.Id:D}/observations", observation);
                var dns = await new SqliteDnsRecordStore(paths).LoadAsync(profile.Id);
                if (dns.Count > 0) _ = await client.PutAsync<DnsManagedRecord[]>($"api/servers/{profile.Id:D}/dns-records", dns);
                if (IncludePanelCredentialsBox.IsChecked == true && await provisionStore.LoadCredentialsAsync(profile.Id) is { } credentials)
                    _ = await client.PutAsync<VpsPanelCredentials>($"api/servers/{profile.Id:D}/credentials", credentials);
            }
            if (IncludeDnsTokenBox.IsChecked == true)
            {
                var dnsToken = await new DpapiDnsTokenStore(paths).LoadAsync();
                if (string.IsNullOrWhiteSpace(dnsToken)) throw new InvalidOperationException("Профили импортированы, но токен Timeweb DNS не найден в локальных DPAPI-настройках. Сохраните токен и повторите его перенос.");
                _ = await client.PutAsync<JsonElement>("api/dns/token", new RemoteDnsTokenRequest(dnsToken));
            }
            var existingCount = localProfiles.Count - importedCount;
            MigrationSummary.Text = $"Добавлено профилей: {importedCount}. Уже существовали на сервере: {existingCount}. Дополнительные данные, наблюдения и DNS-записи обновлены значениями с этого устройства, если были сохранены. Пропущено зашифрованных SSH-ключей: {skippedEncryptedKeys}. Локальная база и исходные файлы ключей остались на этом ПК.";
            StatusText.Text = skippedEncryptedKeys == 0 ? $"Копирование завершено: новых профилей {importedCount}, существующих {existingCount}. Перезапустите приложение после выбора режима LAN." : $"Копирование завершено: новых профилей {importedCount}, существующих {existingCount}. Зашифрованные закрытые ключи не загружены; добавьте их passphrase или выберите другой ключ отдельно.";
        }
        catch (Exception ex) { StatusText.Text = ex is InvalidOperationException or ArgumentException ? ex.Message : "Копирование прервано. Локальные данные не удалены; проверьте список профилей на сервере и повторите."; }
        finally { MigrateButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = RemoteConnectionConfig.FilePath(_dataDirectory);
            if (IsRemote) { if (_tested is null || !TestMatchesCurrentFields()) throw new InvalidOperationException("Проверьте текущее HTTPS-адрес, fingerprint и токен перед сохранением."); var config = BuildConfig(); config.Save(_dataDirectory); TokenBox.Clear(); TokenRevealBox.Clear(); }
            else if (File.Exists(path)) File.Delete(path);
            StatusText.Text = "Выбор сохранён. Перезапустите OpenViya Ops, чтобы применить хранилище.";
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "Настройку не удалось сохранить."; }
    }

    private bool TestMatchesCurrentFields()
    {
        if (_tested is null) return false;
        if (!string.Equals(_tested.BaseAddress.TrimEnd('/'), UrlBox.Text.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(_tested.Fingerprint, RemoteOpsClient.NormalizeFingerprint(FingerprintBox.Text), StringComparison.Ordinal)) return false;
        var token = RevealTokenBox.IsChecked == true ? TokenRevealBox.Text : TokenBox.Password;
        return string.IsNullOrEmpty(token) || string.Equals(token, _tested.DecryptToken(), StringComparison.Ordinal);
    }

    private static bool IsEncryptedPrivateKey(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("ENCRYPTED", StringComparison.Ordinal)) return true;
        if (text.Contains("Encryption:", StringComparison.Ordinal) && !text.Contains("Encryption: none", StringComparison.OrdinalIgnoreCase)) return true;
        const string openssh = "openssh-key-v1\0";
        var marker = Encoding.ASCII.GetBytes(openssh);
        byte[]? decoded = null;
        ReadOnlySpan<byte> payload = bytes;
        const string begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        if (text.StartsWith(begin, StringComparison.Ordinal))
        {
            var end = text.IndexOf("-----END OPENSSH PRIVATE KEY-----", StringComparison.Ordinal);
            if (end < 0) return true;
            var body = new string(text[begin.Length..end].Where(c => !char.IsWhiteSpace(c)).ToArray());
            try { decoded = Convert.FromBase64String(body); payload = decoded; } catch (FormatException) { return true; }
        }
        if (payload.Length < marker.Length + 4 || !payload[..marker.Length].SequenceEqual(marker)) return false;
        var position = marker.Length;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(position, 4)); position += 4;
        if (length == 0 || length > 64 || position + length > payload.Length) return true;
        return !Encoding.ASCII.GetString(payload.Slice(position, (int)length)).Equals("none", StringComparison.Ordinal);
    }

    private static Dictionary<Guid, HostKeyCandidate> ReadTrustedHostKeys(AppPaths paths, IReadOnlyList<ServerProfile> profiles)
    {
        var result = new Dictionary<Guid, HostKeyCandidate>();
        if (!File.Exists(paths.KnownHostsPath)) return result;
        var byAlias = profiles.ToDictionary(p => "openviya-" + p.Id.ToString("N"), p => p.Id, StringComparer.Ordinal);
        foreach (var line in File.ReadLines(paths.KnownHostsPath))
        {
            if (line.StartsWith('#')) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !byAlias.TryGetValue(fields[0], out var id)) continue;
            try
            {
                var bytes = Convert.FromBase64String(fields[2]);
                if (bytes.Length < 8) continue;
                var typeLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
                if (typeLength is 0 or > 64 || typeLength > bytes.Length - 4) continue;
                var embeddedType = Encoding.ASCII.GetString(bytes, 4, (int)typeLength);
                if (!StringComparer.Ordinal.Equals(fields[1], embeddedType)) continue;
                var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=');
                result[id] = new HostKeyCandidate(fields[1], fields[2], fingerprint);
            }
            catch (FormatException) { }
        }
        return result;
    }
}
