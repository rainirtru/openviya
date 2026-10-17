using System.Security.AccessControl;
using System.Text;

namespace OpenViya.Ops.HostsHelper;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("apply" or "remove") || !HostsFileEditor.IsAllowedPanelDomain(args[1])) return 2;
        try
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var hostsPath = Path.Combine(systemDirectory, "drivers", "etc", "hosts");
            var existed = File.Exists(hostsPath);
            var originalBytes = existed ? File.ReadAllBytes(hostsPath) : [];
            var originalText = Decode(originalBytes, out var encoding, out var preamble);
            var result = args[0] == "apply" ? HostsFileEditor.Apply(originalText, args[1]) : HostsFileEditor.Remove(originalText, args[1]);
            if (result.Outcome == HostsEditOutcome.Conflict) return 3;
            if (result.Content == originalText) return 0;

            var updatedBytes = Encode(result.Content, encoding, preamble);
            var directory = Path.GetDirectoryName(hostsPath)!;
            var backupPath = Path.Combine(directory, $"hosts.openviya-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak");
            using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                backup.Write(originalBytes);
                backup.Flush(flushToDisk: true);
            }

            var temporary = Path.Combine(directory, $"hosts.openviya-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, updatedBytes);
                if (existed)
                {
                    var attributes = File.GetAttributes(hostsPath);
                    File.SetAttributes(temporary, attributes);
                    try
                    {
                        var security = new FileInfo(hostsPath).GetAccessControl();
                        new FileInfo(temporary).SetAccessControl(security);
                    }
                    catch (PlatformNotSupportedException) { }
                }
                var currentBytes = File.Exists(hostsPath) ? File.ReadAllBytes(hostsPath) : [];
                if (existed != File.Exists(hostsPath) || !currentBytes.AsSpan().SequenceEqual(originalBytes)) return 5;
                File.Move(temporary, hostsPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return 0;
        }
        catch { return 4; }
    }

    private static string Decode(byte[] bytes, out Encoding encoding, out byte[] preamble)
    {
        preamble = [];
        encoding = new UTF8Encoding(false, true);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            preamble = Encoding.UTF8.Preamble.ToArray();
            return encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            preamble = Encoding.Unicode.Preamble.ToArray();
            encoding = new UnicodeEncoding(false, false, true);
            return encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            preamble = Encoding.BigEndianUnicode.Preamble.ToArray();
            encoding = new UnicodeEncoding(true, false, true);
            return encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        }
        return encoding.GetString(bytes);
    }

    private static byte[] Encode(string content, Encoding encoding, byte[] preamble)
    {
        var body = encoding.GetBytes(content);
        return preamble.Length == 0 ? body : [.. preamble, .. body];
    }
}
