using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WMT.Core;

public sealed record OfficeInfo(
    bool Installed, string Family, string Build, string Architecture, string InstallType,
    string ProductIds, IReadOnlyList<string> LanguageComponents, IReadOnlyList<string> Notes);

public enum CompatLevel { Compatible, Warning, Blocked }
public sealed record OfficeCompatibility(CompatLevel Level, string Message);

public static class OfficeFingerprint
{
    public static readonly OfficeInfo Absent = new(false, "", "", "", "", "", Array.Empty<string>(), new[] { "Office not detected." });
    static readonly Regex LangRx = new(@"(Proofing|Language Pack|Localization)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static OfficeInfo Read(IReadOnlyList<SoftwareItem> software) => Build(ReadClickToRun(), software);

    static IReadOnlyDictionary<string, string>? ReadClickToRun()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            if (k == null) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in k.GetValueNames()) d[n] = k.GetValue(n)?.ToString() ?? "";
            return d;
        }
        catch { return null; }
    }

    public static OfficeInfo Build(IReadOnlyDictionary<string, string>? c2r, IReadOnlyList<SoftwareItem> software)
    {
        var officeItems = software.Where(s => s.Type == "Office").ToList();
        var langs = officeItems.Where(s => LangRx.IsMatch(s.Name)).Select(s => s.Name).ToList();
        var main = officeItems.FirstOrDefault(s => !LangRx.IsMatch(s.Name));
        var notes = new List<string>();

        if (c2r != null && c2r.TryGetValue("VersionToReport", out var ver) && ver.Length > 0)
        {
            c2r.TryGetValue("Platform", out var plat);
            c2r.TryGetValue("ProductReleaseIds", out var ids);
            ids ??= "";
            var arch = string.IsNullOrWhiteSpace(plat) ? "Unknown" : plat!.Trim().ToLowerInvariant();
            if (arch == "Unknown") notes.Add("Architecture not reported by Click-to-Run configuration.");
            var family = FamilyFromIds(ids) ?? main?.Name ?? ids;
            if (string.IsNullOrEmpty(family)) { family = "Unknown"; notes.Add("Product family could not be determined."); }
            if (langs.Count == 0) notes.Add("No proofing/language components found in software list (scan software first for a complete list).");
            return new OfficeInfo(true, family, ver, arch, "Click-to-Run", ids, langs, notes);
        }
        if (main != null)
        {
            notes.Add("Click-to-Run configuration not found; values inferred from the Uninstall entry (MSI or other).");
            return new OfficeInfo(true, main.Name, main.Version, main.Architecture, "Uninstall entry (inferred)", "", langs, notes);
        }
        return Absent;
    }

    static string? FamilyFromIds(string ids)
    {
        var l = ids.ToLowerInvariant();
        if (l.Contains("proplus2016")) return "Microsoft Office Professional Plus 2016";
        if (l.Contains("proplus2019")) return "Microsoft Office Professional Plus 2019";
        if (l.Contains("proplus2021")) return "Microsoft Office Professional Plus 2021";
        if (l.Contains("proplus2024")) return "Microsoft Office Professional Plus 2024";
        if (l.Contains("o365") || l.Contains("microsoft365")) return "Microsoft 365 Apps";
        return null;
    }

    /// <summary>Rules: architecture mismatch blocks risky steps; different family warns;
    /// same family+architecture with a different build is NOT treated as incompatible.</summary>
    public static OfficeCompatibility Compare(OfficeInfo source, OfficeInfo target)
    {
        if (!source.Installed) return new(CompatLevel.Warning, "Source Office not detected; Office-dependent recovery cannot be planned.");
        if (!target.Installed) return new(CompatLevel.Warning, "Target Office is absent: Office must be installed before restoring Word customizations.");
        if (!string.Equals(source.Architecture, target.Architecture, StringComparison.OrdinalIgnoreCase))
            return new(CompatLevel.Blocked, $"Architecture mismatch ({source.Architecture} -> {target.Architecture}): VBA Declare/COM add-ins may fail; block risky steps.");
        if (!string.Equals(source.Family, target.Family, StringComparison.OrdinalIgnoreCase))
            return new(CompatLevel.Warning, $"Different Office family ({source.Family} -> {target.Family}): review templates/add-ins before restore.");
        if (!string.Equals(source.Build, target.Build, StringComparison.OrdinalIgnoreCase))
            return new(CompatLevel.Compatible, $"Same family and architecture; build differs ({source.Build} -> {target.Build}). Not treated as incompatible.");
        return new(CompatLevel.Compatible, "Same Office family, architecture and build.");
    }

    public static string Format(OfficeInfo o)
    {
        if (!o.Installed) return "Office: not detected." + Environment.NewLine + string.Join(Environment.NewLine, o.Notes);
        var sb = new StringBuilder();
        sb.AppendLine($"Product / edition : {o.Family}");
        sb.AppendLine($"Release / build   : {o.Build}");
        sb.AppendLine($"Architecture      : {o.Architecture}");
        sb.AppendLine($"Install type      : {o.InstallType}");
        if (o.ProductIds.Length > 0) sb.AppendLine($"Product release ID: {o.ProductIds}");
        sb.AppendLine($"Proofing/language components found: {o.LanguageComponents.Count}");
        foreach (var l in o.LanguageComponents) sb.AppendLine("  - " + l);
        sb.AppendLine("Word add-ins: not inspected in M1 (planned for M2).");
        foreach (var n in o.Notes) sb.AppendLine("Note: " + n);
        sb.AppendLine("Comparison rules: arch mismatch = blocked; other family = warning; build difference alone = compatible.");
        return sb.ToString().TrimEnd();
    }
}
