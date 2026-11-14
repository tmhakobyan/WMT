using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WMT.Core;

// Headless entry point (no GUI): used by tests/diagnostics.  Read-only.
// Exit codes: 0 ok, 3 completed with warnings, 1 failed, 64 usage.
Console.OutputEncoding = new UTF8Encoding(false);
var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new JsonStringEnumConverter() }
};
var cmd = args.FirstOrDefault()?.ToLowerInvariant();
try
{
    switch (cmd)
    {
        case "software":
            {
                var r = new SoftwareDiscovery(new WindowsUninstallSource()).Scan(null, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(r, json));
                return r.Warnings.Count > 0 ? 3 : 0;
            }
        case "profiles":
            {
                var r = new ProfileDiscovery(new WindowsProfileSource(), new WindowsAccountResolver()).Scan(null, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(r, json));
                return r.Warnings.Count > 0 ? 3 : 0;
            }
        case "office":
            {
                var sw = new SoftwareDiscovery(new WindowsUninstallSource()).Scan(null, CancellationToken.None);
                var o = OfficeFingerprint.Read(sw.Items);
                Console.WriteLine(JsonSerializer.Serialize(o, json));
                return 0;
            }
        default:
            Console.Error.WriteLine("Usage: wmt-cli software | profiles | office");
            return 64;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAILED: " + ex.Message);
    return 1;
}
