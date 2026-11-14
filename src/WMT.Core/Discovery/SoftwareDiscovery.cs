using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WMT.Core;

public sealed record UninstallRecord(
    string Source, string Architecture, string? DisplayName, string? Publisher, string? DisplayVersion,
    string? InstallLocation, bool SystemComponent, string? ParentKeyName, string KeyName);

public sealed record UninstallReadResult(IReadOnlyList<UninstallRecord> Records, IReadOnlyList<string> Warnings);

public interface IUninstallSource { UninstallReadResult Read(IProgress<ProgressInfo>? progress, CancellationToken ct); }

/// <summary>Reads the 32/64-bit machine and per-user Uninstall registry keys. Read-only.</summary>
public sealed class WindowsUninstallSource : IUninstallSource
{
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    static readonly (RegistryHive Hive, RegistryView View, string Source, string Arch)[] Locations =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM 64-bit", "x64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM 32-bit (WOW6432Node)", "x86"),
        (RegistryHive.CurrentUser, RegistryView.Default, "HKCU (current user)", "Unknown"),
    };

    public UninstallReadResult Read(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var records = new List<UninstallRecord>();
        var warnings = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            warnings.Add("Not running on Windows: registry software discovery is unavailable.");
            return new UninstallReadResult(records, warnings);
        }
        long done = 0;
        foreach (var loc in Locations)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("Reading " + loc.Source, done, Locations.Length));
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
                using var root = baseKey.OpenSubKey(UninstallPath);
                if (root == null) { warnings.Add($"{loc.Source}: Uninstall key not found."); done++; continue; }
                foreach (var name in root.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k == null) continue;
                        records.Add(new UninstallRecord(
                            loc.Source, loc.Arch,
                            Str(k, "DisplayName"), Str(k, "Publisher"), Str(k, "DisplayVersion"),
                            Str(k, "InstallLocation"), Int(k, "SystemComponent") == 1,
                            Str(k, "ParentKeyName"), name));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        warnings.Add($"{loc.Source}\\{name}: unreadable ({ex.GetType().Name}).");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                warnings.Add($"{loc.Source}: unreadable ({ex.GetType().Name}).");
            }
            done++;
        }
        return new UninstallReadResult(records, warnings);
    }

    static string? Str(RegistryKey k, string n) => k.GetValue(n)?.ToString();
    static int Int(RegistryKey k, string n)
    {
        try { return Convert.ToInt32(k.GetValue(n) ?? 0); } catch { return 0; }
    }
}

public sealed class SoftwareDiscovery : ISoftwareScanner
{
    static readonly Regex Noise = new(@"^(KB\d+|(Security |Hotfix for |Update for ).*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OfficeRx = new(@"^Microsoft (Office|365)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PythonRx = new(@"^Python \d", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RuntimeRx = new(
        @"Visual C\+\+.*Redistributable|\.NET (Runtime|Core|SDK|Desktop|Framework|Host)|Windows Desktop Runtime|ASP\.NET Core|DirectX|^Java(\(TM\))?( SE)? (Runtime|\d)|OpenJDK|WebView2",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly IUninstallSource source;
    public SoftwareDiscovery(IUninstallSource source) { this.source = source; }

    public static string Classify(string name) =>
        OfficeRx.IsMatch(name) ? "Office" :
        PythonRx.IsMatch(name) ? "Python" :
        RuntimeRx.IsMatch(name) ? "Runtime" : "Application";

    public static string RecoveryFor(string type) => type switch
    {
        "Office" => "Reinstall (installer media separate; license not exported)",
        "Runtime" => "Reinstall manifest (no DLL copy)",
        "Python" => "Reinstall + pip recovery plan (M2)",
        _ => "Reinstall"
    };

    public SoftwareScanResult Scan(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var read = source.Read(progress, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SoftwareItem>();
        int filtered = 0, dups = 0;
        long i = 0;
        foreach (var r in read.Records)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            if (i % 25 == 0) progress?.Report(new ProgressInfo("Classifying entries", i, read.Records.Count));
            var name = r.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name) || r.SystemComponent || !string.IsNullOrEmpty(r.ParentKeyName) || Noise.IsMatch(name))
            { filtered++; continue; }
            var version = r.DisplayVersion?.Trim() ?? "";
            if (!seen.Add($"{name}|{version}|{r.Architecture}")) { dups++; continue; }
            var type = Classify(name);
            items.Add(new SoftwareItem(
                name, r.Publisher?.Trim() ?? "", version, r.Architecture, r.Source, type, RecoveryFor(type),
                version.Length > 0 ? Confidence.Confirmed : Confidence.Inferred, r.InstallLocation?.Trim() ?? ""));
        }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        progress?.Report(new ProgressInfo("Software discovery finished", items.Count, items.Count));
        return new SoftwareScanResult(items, filtered, dups, read.Warnings);
    }
}
