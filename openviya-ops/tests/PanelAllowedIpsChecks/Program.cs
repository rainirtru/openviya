using OpenViya.Ops.Core;

static void Equal(string expected, string actual)
{
    if (expected != actual) throw new Exception($"Expected {expected}, got {actual}");
}
static void Reject(string input, string? messagePart = null)
{
    try { PanelAllowedIps.Normalize(input); }
    catch (ArgumentException ex)
    {
        if (messagePart is not null && !ex.Message.Contains(messagePart, StringComparison.Ordinal))
            throw new Exception("Validation must identify the correction without echoing input.");
        return;
    }
    throw new Exception("Malformed allowed-IP input was accepted.");
}

Equal("any", PanelAllowedIps.Normalize(null));
Equal("any", PanelAllowedIps.Normalize(" \r\n\t\n"));
Equal("127.0.0.1/32\n127.0.0.2/32\n127.0.0.3/32", PanelAllowedIps.Normalize("127.0.0.1\n127.0.0.2\n127.0.0.3"));
Equal("203.0.113.7/32\n2001:db8::1/128", PanelAllowedIps.Normalize(" 203.0.113.7 \r\n\n2001:0DB8:0:0:0:0:0:1\r203.0.113.7\n2001:db8::1"));
Equal("192.0.2.1/32", PanelAllowedIps.Normalize("192.0.2.1\n::ffff:192.0.2.1"));
foreach (var input in new[] { "any", "127.0.0.1/32", "127.0.0.1,127.0.0.2", "127.0.0.1 127.0.0.2", "localhost", "127.1", "2130706433", "0177.0.0.1", "127.000.0.1", "256.0.0.1", "[::1]", "fe80::1%5", "1.2.3.4;whoami", "::/0", "::1/128" })
    Reject(input);
Reject("127.0.0.1\n\nprivate-value-not-to-echo", "строка 3");
try { PanelAllowedIps.Normalize("private-value-not-to-echo"); }
catch (ArgumentException ex) { if (ex.Message.Contains("private-value-not-to-echo", StringComparison.Ordinal)) throw new Exception("Input leaked into error."); }
if (PanelAllowedIps.Normalize(string.Join('\n', Enumerable.Range(1, 100).Select(i => $"192.0.2.{i}"))).Split('\n').Length != 100)
    throw new Exception("100 valid addresses must be accepted.");
Reject(string.Join('\n', Enumerable.Repeat("192.0.2.1", 101)), "100");
Reject(new string(' ', PanelAllowedIps.MaximumInputLength) + "1", "длинный");
Console.WriteLine("Allowed IP checks passed: multiline, IPv4/IPv6, dedupe, strict syntax, safe row errors, bounds.");
