using OpenViya.Ops.Core;
using OpenViya.Ops.Storage;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "OpenViyaOpsStorageChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var custom = new ServerProfile { Name = "Node 1", Role = ServerRole.VpnNode, Region = "usa", NodeIndex = 1, Host = "203.0.113.10", SshPort = 2222, PanelPort = 2053, VpnDomain = "custom.example.org", PanelDomain = "panel.custom.example.org", PanelBasePath = "/distinctive-secret/", SubdomainMode = SubdomainMode.Manual, DnsZone = "custom-zone.example.net", DnsAddress = "8.8.8.8", PanelAccess = VpsPanelAccess.Both, PanelOpenMode = PanelOpenMode.PublicHttps, PanelAllowedIpsText = "198.51.100.7\n2001:4860:4860::8888" };
    var completed = ProfileDefaults.Apply(custom);
    Check(completed.PanelPort == 2053 && completed.SshPort == 2222, "Defaults must preserve custom ports.");
    Check(completed.PanelDomain == custom.PanelDomain && completed.VpnDomain == custom.VpnDomain, "Defaults must preserve custom domains.");
    Check(ProfileValidator.Validate(completed).Count == 0, "Valid profile should pass validation.");
    Check(ProfileValidator.Validate(completed with { Host = "https://example.org:22" }).Count > 0, "Host must reject scheme and port.");
    Check(ProfileValidator.Validate(completed with { SshPort = 65536 }).Count > 0, "Port bounds must be enforced.");
    Check(ProfileValidator.Validate(completed with { PanelBasePath = "/a/../b" }).Count > 0, "Base path traversal must be rejected.");
    Check(ProfileValidator.Validate(completed with { PanelDomain = "bad..example" }).Count > 0, "Malformed domain must be rejected.");
    Check(ProfileValidator.Validate(completed with { DnsAddress = "192.168.1.10" }).Count > 0, "DNS address must be a public IP literal.");
    Check(ProfileValidator.Validate(completed with { DnsAddress = "vps.example.org" }).Count > 0, "DNS address must reject hostnames.");
    Check(ProfileValidator.Validate(completed with { DnsAddress = "2001:4860:4860::8888%3" }).Count > 0, "DNS address must reject interface-scoped IPv6 values.");
    Check(ProfileValidator.Validate(completed with { PanelOpenMode = PanelOpenMode.PublicHttps, PanelAccess = VpsPanelAccess.Tunnel }).Count > 0, "Public HTTPS mode must require Both panel access.");
    Check(ProfileValidator.Validate(completed with { PanelAllowedIpsText = "192.0.2.1,192.0.2.2" }).Count > 0, "Panel allow-list must reject delimiters and CIDR input.");
    var automatic = ProfileDefaults.Apply(new ServerProfile { Name = "Auto", Role = ServerRole.VpnNode, Region = "eu", NodeIndex = 2, Host = "8.8.8.8", PanelDomain = "custom.example.org", VpnDomain = "custom-vpn.example.org", DnsZone = "ops.example.net" });
    Check(automatic.PanelDomain == "panel-eu-2.ops.example.net" && automatic.VpnDomain == "eu-2.ops.example.net", "Automatic mode must derive names from zone, role, region, and index.");
    var automaticStore = new SqliteProfileStore(new AppPaths(Path.Combine(root, "automatic")));
    await automaticStore.SaveAsync(automatic with { DnsAddress = "8.8.8.8" });
    var automaticLoaded = (await automaticStore.LoadAsync()).Single();
    Check(automaticLoaded.SubdomainMode == SubdomainMode.Automatic && automaticLoaded.DnsZone == "ops.example.net" && automaticLoaded.DnsAddress == "8.8.8.8", "Automatic DNS fields should persist.");
    var appDefaults = ProfileDefaults.Apply(new ServerProfile { Name = "Application", Role = ServerRole.App, Host = "203.0.113.11" });
    Check(appDefaults.PanelDomain == "", "APP defaults must not invent a 3x-ui panel domain.");
    Check(ProfileDefaults.Apply(appDefaults with { PanelDomain = "admin.example.org" }).PanelDomain == "", "Automatic APP defaults must not create a 3x-ui panel domain.");
    Check(ProfileDefaults.Apply(appDefaults with { SubdomainMode = SubdomainMode.Manual, PanelDomain = "admin.example.org" }).PanelDomain == "admin.example.org", "Manual APP profiles must preserve an explicit panel domain.");

    var store = new SqliteProfileStore(new AppPaths(root));
    await store.SaveAsync(completed);
    var loaded = (await store.LoadAsync()).Single();
    Check(loaded.PanelBasePath == custom.PanelBasePath, "DPAPI path should roundtrip for the current user.");
    Check(loaded.PanelPort == 2053, "Custom panel port should persist.");
    Check(loaded.SubdomainMode == SubdomainMode.Manual && loaded.DnsZone == "custom-zone.example.net" && loaded.DnsAddress == "8.8.8.8", "Manual mode, zone, and DNS address should persist.");
    Check(loaded.PanelAccess == VpsPanelAccess.Both && loaded.PanelOpenMode == PanelOpenMode.PublicHttps && loaded.PanelAllowedIpsText == custom.PanelAllowedIpsText, "Panel access settings should persist.");

    var stateStore = new SqliteServerStateStore(new AppPaths(root));
    var observed = new ServerObservedState(custom.Id, DateTimeOffset.UtcNow, ProbeStatus.Success, ProbeStatus.Warning, "Panel certificate expires soon", DateTimeOffset.UtcNow.AddDays(12), 2053, VpsPanelAccess.Both, "198.51.100.7\n2001:4860:4860::8888");
    await stateStore.SaveAsync(observed);
    var observedLoaded = await stateStore.LoadAsync(custom.Id);
    Check(observedLoaded is not null && observedLoaded.ServerId == custom.Id && observedLoaded.SshStatus == ProbeStatus.Success && observedLoaded.PanelStatus == ProbeStatus.Warning, "Observed server statuses should persist.");
    Check(observedLoaded!.CertificateExpiresAt == observed.CertificateExpiresAt && observedLoaded.PanelPort == 2053 && observedLoaded.PanelAccess == VpsPanelAccess.Both, "Observed panel metadata should persist.");
    Check(observedLoaded.PanelAllowedIpsText == "198.51.100.7\n2001:4860:4860::8888", "Observed allow-list text should persist with normalized line endings.");
    Check(await stateStore.LoadAsync(Guid.NewGuid()) is null, "Missing observed state should load as null.");
    try { await stateStore.SaveAsync(observed with { Message = new string('x', 1001) }); throw new Exception("Oversized observed message was accepted."); }
    catch (ArgumentException) { }

    var export = Path.Combine(root, "profiles.json");
    await store.ExportAsync(export);
    var json = await File.ReadAllTextAsync(export);
    Check(!json.Contains("distinctive-secret", StringComparison.Ordinal), "Export must omit panel base path.");
    Check(!json.Contains("IdentityFile", StringComparison.OrdinalIgnoreCase), "Export must omit identity path.");

    var importedRoot = Path.Combine(root, "imported");
    var importedStore = new SqliteProfileStore(new AppPaths(importedRoot));
    Check(await importedStore.ImportAsync(export) == 1, "Portable import should add one profile.");
    var imported = (await importedStore.LoadAsync()).Single();
    Check(imported.PanelBasePath == "/" && imported.IdentityFile == "", "Import must not restore secret path metadata.");
    Check(imported.SubdomainMode == SubdomainMode.Manual && imported.DnsZone == custom.DnsZone && imported.DnsAddress == custom.DnsAddress, "Version 3 export must round-trip DNS settings.");
    Check(imported.PanelAccess == custom.PanelAccess && imported.PanelOpenMode == custom.PanelOpenMode && imported.PanelAllowedIpsText == custom.PanelAllowedIpsText, "Version 3 export must round-trip panel settings.");

    var oldImportRoot = Path.Combine(root, "old-import");
    var oldImportStore = new SqliteProfileStore(new AppPaths(oldImportRoot));
    var oldImport = Path.Combine(root, "profiles-v1.json");
    var legacy = new
    {
        Version = 1,
        Profiles = new[] { new { Id = Guid.NewGuid(), Name = "Legacy JSON", Role = ServerRole.VpnNode, Region = "usa", NodeIndex = 8, Host = "8.8.8.8", SshPort = 22, UserName = "vpsadmin", PanelDomain = "legacy-panel.example.net", PanelPort = 2053, VpnDomain = "legacy-vpn.example.net", VpnPort = 443, ControlServerId = (Guid?)null, UpdatedAt = DateTimeOffset.UtcNow, LocalPort = (int?)null } }
    };
    await File.WriteAllTextAsync(oldImport, JsonSerializer.Serialize(legacy));
    Check(await oldImportStore.ImportAsync(oldImport) == 1, "Version 1 JSON import should be supported.");
    Check((await oldImportStore.LoadAsync()).Single().SubdomainMode == SubdomainMode.Manual, "Version 1 JSON without a mode must import as Manual.");

    var tokenStore = new DpapiDnsTokenStore(new AppPaths(Path.Combine(root, "token")));
    const string fakeToken = "timeweb-test-token-placeholder";
    await tokenStore.SaveAsync(fakeToken);
    var encryptedTokenFile = Path.Combine(root, "token", "timeweb-token.dpapi");
    Check(!(await File.ReadAllBytesAsync(encryptedTokenFile)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(fakeToken)), "Token file must not contain plaintext token bytes.");
    Check(await tokenStore.LoadAsync() == fakeToken, "DPAPI token must round-trip for the current user.");
    var afterTokenExport = Path.Combine(root, "after-token-export.json");
    await store.ExportAsync(afterTokenExport);
    Check(!File.ReadAllText(afterTokenExport).Contains(fakeToken, StringComparison.Ordinal), "Profile export must omit the API token.");
    try { await tokenStore.SaveAsync("bad token"); throw new Exception("Whitespace token was accepted."); }
    catch (ArgumentException) { }
    await tokenStore.DeleteAsync();
    Check(await tokenStore.LoadAsync() is null, "Token delete should clear the encrypted token.");

    var dnsStore = new SqliteDnsRecordStore(new AppPaths(root));
    var managed = new DnsManagedRecord(custom.Id, "usa-1.openviya.online", 501, "8.8.8.8", DateTimeOffset.UtcNow);
    await dnsStore.SaveAsync(managed);
    Check((await dnsStore.LoadAsync(custom.Id)).Single().RecordId == 501, "Managed DNS record ID should persist.");

    var duplicate = completed with { Id = Guid.NewGuid(), Name = "Duplicate endpoint" };
    var duplicateRejected = false;
    try { await store.SaveAsync(duplicate); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Адрес SSH", StringComparison.OrdinalIgnoreCase)) { duplicateRejected = true; }
    Check(duplicateRejected, "Duplicate SSH endpoint should not be saved.");
    Check((await store.LoadAsync()).Count == 1, "Rejected duplicate save must leave the database unchanged.");

    var control = new ServerProfile { Name = "Z CONTROL", Role = ServerRole.Control, Host = "203.0.113.20", PanelDomain = "panel-control.openviya.online" };
    await store.SaveAsync(control);
    var child = completed with { Id = Guid.NewGuid(), Name = "A child", Host = "203.0.113.21", PanelDomain = "panel-ca-1.openviya.online", ControlServerId = control.Id };
    await store.SaveAsync(child);
    var hierarchyExport = Path.Combine(root, "hierarchy.json");
    await store.ExportAsync(hierarchyExport);
    var hierarchyStore = new SqliteProfileStore(new AppPaths(Path.Combine(root, "hierarchy-import")));
    Check(await hierarchyStore.ImportAsync(hierarchyExport) == 3, "Import must support children ordered before their parent CONTROL.");
    Check((await hierarchyStore.LoadAsync()).Single(p => p.Id == child.Id).ControlServerId == control.Id, "Parent relation must round-trip.");
    try { await store.DeleteAsync(control.Id); throw new Exception("Referenced CONTROL deletion was accepted."); }
    catch (InvalidOperationException) { }
    var invalidImport = Path.Combine(root, "invalid-import.json");
    await File.WriteAllTextAsync(invalidImport, (await File.ReadAllTextAsync(hierarchyExport)).Replace("203.0.113.21", "https://bad.invalid/"));
    try { await hierarchyStore.ImportAsync(invalidImport); throw new Exception("Invalid bulk import was accepted."); }
    catch (InvalidDataException) { }
    var nullRecordImport = Path.Combine(root, "null-record-import.json");
    await File.WriteAllTextAsync(nullRecordImport, "{\"Version\":1,\"Profiles\":[null]}");
    try { await hierarchyStore.ImportAsync(nullRecordImport); throw new Exception("Null profile record was accepted."); }
    catch (InvalidDataException) { }
    Check((await hierarchyStore.LoadAsync()).Count == 3, "Rejected import must leave existing rows unchanged.");

    var legacyDbRoot = Path.Combine(root, "legacy-db");
    Directory.CreateDirectory(legacyDbRoot);
    var legacyDbPath = Path.Combine(legacyDbRoot, "ops.db");
    var legacyProfileId = Guid.NewGuid();
    var legacyBasePath = ProtectedData.Protect(Encoding.UTF8.GetBytes("/legacy-secret/"), null, DataProtectionScope.CurrentUser);
    await using (var legacyDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyDbPath }.ToString()))
    {
        await legacyDb.OpenAsync();
        await using var legacyCommand = legacyDb.CreateCommand();
        legacyCommand.CommandText = """
            CREATE TABLE server_profiles (
              id TEXT PRIMARY KEY NOT NULL,name TEXT NOT NULL,role INTEGER NOT NULL,region TEXT NOT NULL,node_index INTEGER NOT NULL,
              host TEXT NOT NULL,ssh_port INTEGER NOT NULL,user_name TEXT NOT NULL,identity_file TEXT NOT NULL,panel_domain TEXT NOT NULL,
              panel_port INTEGER NOT NULL,panel_base_path BLOB NOT NULL,vpn_domain TEXT NOT NULL,vpn_port INTEGER NOT NULL,
              control_server_id TEXT NULL REFERENCES server_profiles(id) ON DELETE RESTRICT,updated_at TEXT NOT NULL,local_port INTEGER NULL
            );
            CREATE UNIQUE INDEX ux_server_ssh_endpoint ON server_profiles(lower(host), ssh_port);
            CREATE UNIQUE INDEX ux_server_panel_endpoint ON server_profiles(lower(panel_domain), panel_port) WHERE panel_domain <> '';
            CREATE INDEX ix_server_control ON server_profiles(control_server_id);
            INSERT INTO server_profiles(id,name,role,region,node_index,host,ssh_port,user_name,identity_file,panel_domain,panel_port,panel_base_path,vpn_domain,vpn_port,control_server_id,updated_at,local_port)
            VALUES($id,'Migrated legacy',2,'usa',4,'8.8.4.4',22,'vpsadmin','','kept-panel.example.net',2053,$base,'kept-vpn.example.net',443,NULL,$updated,NULL);
            PRAGMA user_version=1;
            """;
        legacyCommand.Parameters.AddWithValue("$id", legacyProfileId.ToString("D"));
        legacyCommand.Parameters.Add("$base", SqliteType.Blob).Value = legacyBasePath;
        legacyCommand.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await legacyCommand.ExecuteNonQueryAsync();
    }
    var migratedStore = new SqliteProfileStore(new AppPaths(legacyDbRoot));
    var migratedProfile = (await migratedStore.LoadAsync()).Single();
    Check(migratedProfile.Id == legacyProfileId && migratedProfile.SubdomainMode == SubdomainMode.Manual, "Schema version 1 profiles must migrate to Manual mode.");
    Check(migratedProfile.PanelDomain == "kept-panel.example.net" && migratedProfile.VpnDomain == "kept-vpn.example.net", "Migration must preserve existing endpoint names.");
    Check(migratedProfile.DnsZone == "openviya.online" && migratedProfile.DnsAddress == "", "Migration must add safe DNS defaults.");
    Check(migratedProfile.PanelAccess == VpsPanelAccess.Tunnel && migratedProfile.PanelOpenMode == PanelOpenMode.Tunnel && migratedProfile.PanelAllowedIpsText == "", "Migration must keep legacy profiles in Tunnel mode with no allow-list.");

    await store.DeleteAsync(custom.Id);
    Check((await dnsStore.LoadAsync(custom.Id)).Single().RecordId == 501, "Deleting a profile must retain its managed DNS record metadata.");
    Console.WriteLine("Storage checks passed. SQLite and DPAPI checks require Windows.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}
