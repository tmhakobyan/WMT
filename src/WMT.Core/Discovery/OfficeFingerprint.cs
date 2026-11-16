using System.Text;
using System.Text.RegularExpressions;

namespace WMT.Core;

/// <summary>OFF-001 Office environment fingerprint (stored in Office-Environment.json).</summary>
public sealed class OfficeEnvironment
{
    public bool Installed { get; set; }
    public string Family { get; set; } = SoftwareItem.UnknownText;
    public string Release { get; set; } = SoftwareItem.UnknownText;
    public string Version { get; set; } = SoftwareItem.UnknownText;
    public string Build { get; set; } = SoftwareItem.UnknownText;
    public string Architecture { get; set; } = SoftwareItem.UnknownText;
    public string InstallType { get; set; } = SoftwareItem.UnknownText;
    public string ProductReleaseIds { get; set; } = "";
    public string? WordPath { get; set; }
    public string? UpdateChannel { get; set; }
    public List<string> LanguageComponents { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string Machine { get; set; } = Environment.MachineName;

    public static OfficeEnvironment Absent() => new() { Installed = false, Notes = { "Microsoft Office was not detected." } };
}

public enum CompatLevel { Compatible, Warning, Blocked }
public sealed record OfficeCompatibility(CompatLevel Level, string Message, List<string> Risks);

public static class OfficeFingerprint
{
    static readonly Regex LangRx = new(@"Proofing|Language Pack|Language Interface|Localization|\(hy-am\)|\(ru-ru\)|Armenian|Russian", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static OfficeEnvironment Read(IRegistryReader reg, IReadOnlyList<SoftwareItem> software)
    {
        var o = new OfficeEnvironment();
        var officeItems = software.Where(s => s.Category == "Office").ToList();
        o.LanguageComponents = officeItems.Where(s => LangRx.IsMatch(s.Name)).Select(s => s.Name).Distinct().ToList();
        var main = officeItems.Where(s => !LangRx.IsMatch(s.Name))
            .OrderByDescending(s => Regex.IsMatch(s.Name, "Professional Plus|Standard|365|Home", RegexOptions.IgnoreCase)).FirstOrDefault();

        const string c2r = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        var ver = reg.Value("HKLM", c2r, "VersionToReport", RegView.Reg64)?.AsString();
        var winword = reg.Value("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Winword.exe", "", RegView.Reg64)?.AsString()
                      ?? reg.Value("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Winword.exe", "", RegView.Reg32)?.AsString();
        o.WordPath = string.IsNullOrWhiteSpace(winword) ? null : winword.Trim('"');

        if (!string.IsNullOrEmpty(ver))
        {
            o.Installed = true;
            o.InstallType = "Click-to-Run";
            o.Version = ver;
            o.Build = BuildPart(ver);
            o.ProductReleaseIds = reg.Value("HKLM", c2r, "ProductReleaseIds", RegView.Reg64)?.AsString() ?? "";
            var plat = reg.Value("HKLM", c2r, "Platform", RegView.Reg64)?.AsString();
            o.Architecture = string.IsNullOrWhiteSpace(plat) ? SoftwareItem.UnknownText : plat.Trim().ToLowerInvariant();
            o.UpdateChannel = reg.Value("HKLM", c2r, "UpdateChannel", RegView.Reg64)?.AsString() ?? reg.Value("HKLM", c2r, "CDNBaseUrl", RegView.Reg64)?.AsString();
            var culture = reg.Value("HKLM", c2r, "ClientCulture", RegView.Reg64)?.AsString();
            if (!string.IsNullOrEmpty(culture)) o.Notes.Add("Client culture: " + culture);
            o.Family = FamilyFromIds(o.ProductReleaseIds) ?? main?.Name ?? SoftwareItem.UnknownText;
        }
        else if (main != null)
        {
            o.Installed = true;
            o.InstallType = main.InstallationType == "MSI" ? "MSI (Windows Installer)" : main.InstallationType;
            o.Version = main.Version;
            o.Build = BuildPart(main.Version);
            o.Family = main.Name;
            var bit = reg.Value("HKLM", @"SOFTWARE\Microsoft\Office\16.0\Outlook", "Bitness", RegView.Reg64)?.AsString();
            o.Architecture = bit != null ? bit.ToLowerInvariant() : main.Architecture;
            o.Notes.Add("Click-to-Run configuration not found; values come from the Windows Installer entry.");
        }
        else return OfficeEnvironment.Absent();

        if (o.WordPath != null && File.Exists(o.WordPath))
        {
            var pe = PeInfo.Architecture(o.WordPath);
            if (pe != "Unknown")
            {
                if (o.Architecture == SoftwareItem.UnknownText) o.Architecture = pe;
                else if (!o.Architecture.Equals(pe, StringComparison.OrdinalIgnoreCase)) o.Notes.Add($"Registry says {o.Architecture} but WINWORD.EXE is {pe}; WINWORD.EXE is authoritative.");
                o.Architecture = pe;
            }
        }
        if (o.Architecture == SoftwareItem.UnknownText) o.Notes.Add("Office architecture could not be confirmed (x86/x64 matters for VBA Declare and COM add-ins).");
        o.Release = ReleaseFrom(o.Family, o.Version);
        if (o.LanguageComponents.Count == 0) o.Notes.Add("No separate proofing/language entries found in the program list (they may be built into the main product).");
        return o;
    }

    static string BuildPart(string version)
    {
        var p = version.Split('.');
        return p.Length >= 4 ? $"{p[2]}.{p[3]}" : version;
    }

    static string ReleaseFrom(string family, string version)
    {
        var m = Regex.Match(family, @"\b(2013|2016|2019|2021|2024)\b");
        if (m.Success) return m.Value;
        if (Regex.IsMatch(family, "365", RegexOptions.IgnoreCase)) return "Microsoft 365";
        if (version.StartsWith("15.")) return "2013";
        return version.StartsWith("16.") ? "16.0 (release not determinable from version alone)" : SoftwareItem.UnknownText;
    }

    public static string? FamilyFromIds(string ids)
    {
        var l = ids.ToLowerInvariant();
        foreach (var (key, name) in new[] {
            ("proplus2016", "Microsoft Office Professional Plus 2016"), ("proplus2019", "Microsoft Office Professional Plus 2019"),
            ("proplus2021", "Microsoft Office LTSC Professional Plus 2021"), ("proplus2024", "Microsoft Office LTSC Professional Plus 2024"),
            ("standard2016", "Microsoft Office Standard 2016"), ("standard2019", "Microsoft Office Standard 2019"),
            ("homebusiness2016", "Microsoft Office Home and Business 2016"), ("homestudent2016", "Microsoft Office Home and Student 2016"),
            ("o365proplus", "Microsoft 365 Apps for enterprise"), ("o365business", "Microsoft 365 Apps for business"), ("o365homeprem", "Microsoft 365 Family/Personal") })
            if (l.Contains(key)) return name;
        return null;
    }

    /// <summary>OFF-007: same release + other build is NOT incompatible; arch mismatch blocks risky steps; other release warns with concrete risks.</summary>
    public static OfficeCompatibility Compare(OfficeEnvironment source, OfficeEnvironment target)
    {
        var risks = new List<string>();
        if (!source.Installed) return new(CompatLevel.Warning, "Office was not detected on the source; Office-related restore cannot be planned.", risks);
        if (!target.Installed)
        {
            risks.Add("Install Office before restoring Word templates, add-ins and dictionaries.");
            return new(CompatLevel.Blocked, $"Office is not installed on this computer. Install {source.Family} ({source.Architecture}) first.", risks);
        }
        var sa = source.Architecture; var ta = target.Architecture;
        if (sa != SoftwareItem.UnknownText && ta != SoftwareItem.UnknownText && !sa.Equals(ta, StringComparison.OrdinalIgnoreCase))
        {
            risks.Add("VBA 'Declare' statements need PtrSafe/LongPtr on 64-bit Office; check every flagged macro.");
            risks.Add(".wll and COM/VSTO add-ins are architecture-specific and will not load in the other bitness.");
            risks.Add("ActiveX controls in templates may be unavailable.");
            return new(CompatLevel.Blocked, $"Architecture mismatch: source {sa}, target {ta}. Install Office {sa} or replace architecture-specific add-ins.", risks);
        }
        if (sa == SoftwareItem.UnknownText || ta == SoftwareItem.UnknownText)
            risks.Add("Architecture could not be confirmed on one side; verify x86/x64 manually.");
        bool sameRelease = source.Release.Equals(target.Release, StringComparison.OrdinalIgnoreCase) || source.Family.Equals(target.Family, StringComparison.OrdinalIgnoreCase);
        if (!sameRelease)
        {
            risks.Add("Templates and macros usually work, but test every custom tool; Ribbon/QAT files may not apply identically.");
            risks.Add("Building Blocks and AutoCorrect formats may differ between releases.");
            return new(CompatLevel.Warning, $"Different Office release: {source.Family} → {target.Family}.", risks);
        }
        if (!source.Build.Equals(target.Build, StringComparison.OrdinalIgnoreCase))
            return new(risks.Count > 0 ? CompatLevel.Warning : CompatLevel.Compatible, $"Same release and architecture; build differs ({source.Version} → {target.Version}). A different build is not treated as incompatible.", risks);
        return new(risks.Count > 0 ? CompatLevel.Warning : CompatLevel.Compatible, "Same Office release, architecture and build.", risks);
    }

    public static string Format(OfficeEnvironment o)
    {
        if (!o.Installed) return "Microsoft Office: not detected." + Environment.NewLine + string.Join(Environment.NewLine, o.Notes);
        var sb = new StringBuilder();
        sb.AppendLine($"Product / edition : {o.Family}");
        sb.AppendLine($"Release           : {o.Release}");
        sb.AppendLine($"Version / build   : {o.Version} / {o.Build}");
        sb.AppendLine($"Architecture      : {o.Architecture}");
        sb.AppendLine($"Install type      : {o.InstallType}");
        if (!string.IsNullOrEmpty(o.ProductReleaseIds)) sb.AppendLine($"Product IDs       : {o.ProductReleaseIds}");
        if (o.WordPath != null) sb.AppendLine($"WINWORD.EXE       : {o.WordPath}");
        sb.AppendLine($"Language/proofing : {(o.LanguageComponents.Count == 0 ? "none listed separately" : string.Join("; ", o.LanguageComponents))}");
        foreach (var n in o.Notes) sb.AppendLine("Note: " + n);
        sb.AppendLine("License/activation data is never exported. Reinstalling needs your own Office license and installation media.");
        return sb.ToString().TrimEnd();
    }
}
