using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WMT.Core;

public sealed record UninstallRecord(
    string Source, string Architecture, string KeyName, string? DisplayName, string? Publisher, string? DisplayVersion,
    string? InstallLocation, bool SystemComponent, string? ParentKeyName, bool WindowsInstaller, bool PerUser,
    string? DisplayIcon = null, string? ReleaseType = null);

public sealed record SoftwareScanResult(
    List<SoftwareItem> Items, int FilteredCount, int DuplicateCount, List<Issue> Issues, string? WingetExportJson, int WingetPackageCount);

public interface IUninstallSource { (List<UninstallRecord> Records, List<Issue> Issues) Read(CancellationToken ct); }

/// <summary>SW-001: 64-bit + 32-bit machine entries, current-user entries and per-user entries of other LOADED profiles.
/// Unloaded hives are never mounted (reported as an issue instead).</summary>
public sealed class WindowsUninstallSource : IUninstallSource
{
    const string Path64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Path32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    readonly IRegistryReader reg;
    readonly Func<string?> currentSid;
    public WindowsUninstallSource(IRegistryReader reg, Func<string?>? currentSid = null)
    {
        this.reg = reg;
        this.currentSid = currentSid ?? (() => { try { return WindowsIdentity.GetCurrent().User?.Value; } catch { return null; } });
    }

    public (List<UninstallRecord>, List<Issue>) Read(CancellationToken ct)
    {
        var list = new List<UninstallRecord>();
        var issues = new List<Issue>();
        ReadKey("HKLM", Path64, RegView.Reg64, "HKLM 64-bit", "x64", false, list, ct);
        ReadKey("HKLM", Path64, RegView.Reg32, "HKLM 32-bit", "x86", false, list, ct);
        ReadKey("HKCU", Path64, RegView.Default, "HKCU (current user)", SoftwareItem.UnknownText, true, list, ct);
        var me = currentSid();
        foreach (var sid in reg.SubKeys("HKU", ""))
        {
            ct.ThrowIfCancellationRequested();
            if (!ProfileDiscovery.IsRealAccountSid(sid) || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
            if (me != null && sid.Equals(me, StringComparison.OrdinalIgnoreCase)) continue;
            ReadKey("HKU", sid + "\\" + Path64, RegView.Default, $"HKU\\{sid} (other loaded user)", SoftwareItem.UnknownText, true, list, ct);
        }
        if (!reg.KeyExists("HKLM", Path64, RegView.Reg64))
            issues.Add(new Issue(IssueSeverity.Warning, "Software", "64-bit Uninstall key could not be read; the list may be incomplete."));
        issues.Add(new Issue(IssueSeverity.Info, "Software",
            "Per-user programs of users who are not signed in are not visible: their registry hives are not loaded and are never mounted automatically."));
        return (list, issues);
    }

    void ReadKey(string hive, string path, RegView view, string source, string arch, bool perUser, List<UninstallRecord> list, CancellationToken ct)
    {
        foreach (var name in reg.SubKeys(hive, path, view))
        {
            ct.ThrowIfCancellationRequested();
            var p = path + "\\" + name;
            string? S(string v) => reg.Value(hive, p, v, view)?.AsString();
            int I(string v) { var x = reg.Value(hive, p, v, view)?.Data; try { return x == null ? 0 : Convert.ToInt32(x); } catch { return 0; } }
            list.Add(new UninstallRecord(source, arch, name, S("DisplayName"), S("Publisher"), S("DisplayVersion"), S("InstallLocation"),
                I("SystemComponent") == 1, S("ParentKeyName"), I("WindowsInstaller") == 1, perUser, S("DisplayIcon"), S("ReleaseType")));
        }
    }
}

public sealed class SoftwareDiscovery
{
    static readonly Regex Noise = new(@"^(KB\d{5,}|Security Update|Update for|Hotfix for|Service Pack \d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OfficeRx = new(@"^Microsoft (Office|365|Word|Excel|PowerPoint|Outlook|Visio|Project)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PythonRx = new(@"^Python \d+\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PyLauncherRx = new(@"^Python Launcher", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OcrRx = new(@"Tesseract|PaddleOCR|PaddleX|CALFA|ABBYY|OCR", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DevRx = new(@"Visual Studio|Git\b|Node\.js|Anaconda|Miniconda|PyCharm|JetBrains|CUDA|cuDNN|NVIDIA (CUDA|Nsight)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RuntimeRx = new(
        @"Visual C\+\+ .*Redistributable|Visual C\+\+ \d{4}.*Runtime|\.NET (Runtime|Core|Desktop Runtime|Framework|Host)|Microsoft Windows Desktop Runtime|ASP\.NET Core|DirectX|^Java(\(TM\))? |Java \d+ Update|OpenJDK|Temurin|WebView2 Runtime|Microsoft Edge WebView2|Visual Studio Tools for Office Runtime|VSTO",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex VcVersionRx = new(@"Visual C\+\+ (\d{4})(?:-(\d{4}))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NetVersionRx = new(@"(\d+)\.(\d+)\.\d+", RegexOptions.Compiled);

    readonly IUninstallSource source;
    readonly IProcessRunner? processes;
    readonly IReadOnlyList<string> portableRoots;

    public SoftwareDiscovery(IUninstallSource source, IProcessRunner? processes = null, IEnumerable<string>? portableRoots = null)
    {
        this.source = source; this.processes = processes;
        this.portableRoots = (portableRoots ?? DefaultPortableRoots()).ToList();
    }

    public static IEnumerable<string> DefaultPortableRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string> { @"C:\PortableApps", @"C:\Portable", @"C:\Tools" };
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var r = d.RootDirectory.FullName;
                if (r.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(Path.Combine(r, "PortableApps")); list.Add(Path.Combine(r, "Portable")); list.Add(Path.Combine(r, "Tools"));
            }
            catch { }
        }
        if (!string.IsNullOrEmpty(home)) { list.Add(Path.Combine(home, "PortableApps")); list.Add(Path.Combine(home, "Tools")); }
        return list;
    }

    public static string Classify(string name) =>
        OfficeRx.IsMatch(name) ? "Office" :
        PythonRx.IsMatch(name) || PyLauncherRx.IsMatch(name) ? "Python" :
        RuntimeRx.IsMatch(name) ? "Runtime" :
        OcrRx.IsMatch(name) ? "OCR" :
        DevRx.IsMatch(name) ? "Development" : "Application";

    public static string RecoveryFor(string category) => category switch
    {
        "Office" => "Reinstall from own licensed media (license/activation not exported)",
        "Runtime" => "Install runtime from official installer (no DLL copy)",
        "Python" => "Install exact Python version; recreate environments from captured package lists",
        "OCR" => "Reinstall engine; copy own models/configs",
        "Portable" => "Copy own config/data; whole folder only by explicit choice",
        _ => "Reinstall"
    };

    public static List<string> ProvidersFor(string name, string category)
    {
        var p = new List<string>();
        if (category == "Office" && Regex.IsMatch(name, @"Office|Word|365", RegexOptions.IgnoreCase)) p.Add(WordProvider.ProviderId);
        if (category == "Python" && !PyLauncherRx.IsMatch(name)) p.Add(PythonProvider.ProviderId);
        if (category == "OCR" || Regex.IsMatch(name, "Tesseract", RegexOptions.IgnoreCase)) p.Add(PythonProvider.OcrProviderId);
        return p;
    }

    /// <summary>Official installer sources only for well-known runtimes; otherwise null (never invented).</summary>
    public static string? OfficialSourceFor(string name, string category, string version, string arch)
    {
        if (category == "Runtime")
        {
            if (Regex.IsMatch(name, @"Visual C\+\+", RegexOptions.IgnoreCase)) return "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";
            if (Regex.IsMatch(name, @"\.NET|Desktop Runtime|ASP\.NET", RegexOptions.IgnoreCase))
            {
                if (Regex.IsMatch(name, "Framework", RegexOptions.IgnoreCase)) return "https://dotnet.microsoft.com/download/dotnet-framework";
                var m = NetVersionRx.Match(version != SoftwareItem.UnknownText ? version : name);
                return m.Success ? $"https://dotnet.microsoft.com/download/dotnet/{m.Groups[1].Value}.{m.Groups[2].Value}" : "https://dotnet.microsoft.com/download";
            }
            if (Regex.IsMatch(name, "WebView2", RegexOptions.IgnoreCase)) return "https://developer.microsoft.com/microsoft-edge/webview2/";
            if (Regex.IsMatch(name, "Visual Studio Tools for Office|VSTO", RegexOptions.IgnoreCase)) return "https://learn.microsoft.com/visualstudio/vsto/visual-studio-tools-for-office-runtime-installation-scenarios";
        }
        if (category == "Python")
        {
            var m = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)");
            if (m.Success && !PyLauncherRx.IsMatch(name))
            {
                var v = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}";
                return $"https://www.python.org/downloads/release/python-{v.Replace(".", "")}/";
            }
        }
        if (Regex.IsMatch(name, "Tesseract", RegexOptions.IgnoreCase)) return "https://github.com/UB-Mannheim/tesseract/wiki";
        return null;
    }

    public SoftwareScanResult Scan(IProgress<ProgressInfo>? progress, CancellationToken ct, bool includeWinget = false, bool includePortable = true)
    {
        progress?.Report(new ProgressInfo("Reading installed-program registry entries", 0, null));
        var (records, issues) = source.Read(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SoftwareItem>();
        int filtered = 0, dups = 0; long i = 0;
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            if (++i % 50 == 0) progress?.Report(new ProgressInfo("Classifying installed programs", i, records.Count));
            var name = r.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name) || r.SystemComponent || !string.IsNullOrEmpty(r.ParentKeyName) || Noise.IsMatch(name)
                || string.Equals(r.ReleaseType, "Update", StringComparison.OrdinalIgnoreCase) || string.Equals(r.ReleaseType, "Hotfix", StringComparison.OrdinalIgnoreCase))
            { filtered++; continue; }
            var version = string.IsNullOrWhiteSpace(r.DisplayVersion) ? SoftwareItem.UnknownText : r.DisplayVersion.Trim();
            var arch = r.Architecture;
            if (arch == SoftwareItem.UnknownText) arch = GuessArchFromName(name) ?? SoftwareItem.UnknownText;
            if (!seen.Add($"{name}|{version}|{arch}")) { dups++; continue; }
            var cat = Classify(name);
            var item = new SoftwareItem
            {
                Id = PathUtil.StableId($"{r.Source}|{r.KeyName}|{name}"),
                Name = name,
                Publisher = string.IsNullOrWhiteSpace(r.Publisher) ? SoftwareItem.UnknownText : r.Publisher.Trim(),
                Version = version,
                Build = cat == "Python" ? version : SoftwareItem.UnknownText,
                Architecture = arch,
                InstallationType = r.PerUser ? "Per-user" : r.WindowsInstaller ? "MSI" : "Installer (EXE)",
                InstallLocation = string.IsNullOrWhiteSpace(r.InstallLocation) ? SoftwareItem.UnknownText : r.InstallLocation.Trim().Trim('"'),
                DetectedSource = r.Source,
                UninstallKey = r.KeyName,
                Category = cat,
                RecoveryMethod = RecoveryFor(cat),
                Confidence = version != SoftwareItem.UnknownText ? Confidence.Confirmed : Confidence.Inferred,
                Providers = ProvidersFor(name, cat),
            };
            if (r.KeyName.StartsWith("O365", StringComparison.OrdinalIgnoreCase) || r.KeyName.Contains("ClickToRun", StringComparison.OrdinalIgnoreCase)
                || (cat == "Office" && r.KeyName.Contains(" - ", StringComparison.Ordinal)))
                item.InstallationType = "Click-to-Run";
            item.OfficialSource = OfficialSourceFor(name, cat, version, arch);
            items.Add(item);
        }

        if (includePortable)
        {
            try { items.AddRange(FindPortable(issues, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add(new Issue(IssueSeverity.Warning, "Software", "Portable application search failed: " + ex.Message)); }
        }

        string? winget = null; int wingetCount = 0;
        if (includeWinget && processes != null)
        {
            progress?.Report(new ProgressInfo("Querying winget (supplementary source)", 0, null));
            (winget, wingetCount) = RunWingetExport(issues, ct);
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        progress?.Report(new ProgressInfo("Software inventory finished", items.Count, items.Count));
        return new SoftwareScanResult(items, filtered, dups, issues, winget, wingetCount);
    }

    static string? GuessArchFromName(string name) =>
        Regex.IsMatch(name, @"\(x64\)|64-bit|\bx64\b|amd64", RegexOptions.IgnoreCase) ? "x64" :
        Regex.IsMatch(name, @"\(x86\)|32-bit|\bx86\b", RegexOptions.IgnoreCase) ? "x86" : null;

    /// <summary>SW-006: lightweight portable detection in known portable roots (depth ≤ 2, no recursion into user data).</summary>
    IEnumerable<SoftwareItem> FindPortable(List<Issue> issues, CancellationToken ct)
    {
        var result = new List<SoftwareItem>();
        foreach (var root in portableRoots.Distinct(PathUtil.Comparer))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception ex) { issues.Add(new Issue(IssueSeverity.Warning, "Software", "Portable folder not readable: " + ex.Message, root)); continue; }
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var exes = di.EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                        .Where(f => !Regex.IsMatch(f.Name, @"unins|uninst|setup|update|crash|helper", RegexOptions.IgnoreCase)).ToList();
                    if (exes.Count == 0) continue;
                    var main = exes.OrderByDescending(f => f.Name.Contains(di.Name, StringComparison.OrdinalIgnoreCase)).ThenByDescending(f => f.Length).First();
                    var vi = FileVersionInfo.GetVersionInfo(main.FullName);
                    var name = string.IsNullOrWhiteSpace(vi.ProductName) ? di.Name : vi.ProductName!.Trim();
                    result.Add(new SoftwareItem
                    {
                        Id = PathUtil.StableId("portable|" + main.FullName),
                        Name = name + " (portable)",
                        Publisher = string.IsNullOrWhiteSpace(vi.CompanyName) ? SoftwareItem.UnknownText : vi.CompanyName!.Trim(),
                        Version = string.IsNullOrWhiteSpace(vi.ProductVersion) ? SoftwareItem.UnknownText : vi.ProductVersion!.Trim(),
                        Architecture = PeInfo.Architecture(main.FullName),
                        InstallationType = "Portable",
                        InstallLocation = di.FullName,
                        DetectedSource = "Portable folder scan",
                        Category = "Portable",
                        RecoveryMethod = RecoveryFor("Portable"),
                        Confidence = Confidence.Inferred,
                        Providers = new List<string> { PortableProvider.ProviderId },
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
        return result;
    }

    (string?, int) RunWingetExport(List<Issue> issues, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wmt-winget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var r = processes!.RunAsync("winget", new[] { "export", "-o", tmp, "--include-versions", "--accept-source-agreements", "--disable-interactivity" },
                TimeSpan.FromSeconds(120), ct).GetAwaiter().GetResult();
            if (!File.Exists(tmp))
            {
                issues.Add(new Issue(IssueSeverity.Warning, "Software", "winget export produced no file (" + r.Describe() + "). winget is only a supplementary source; the registry list is still complete."));
                return (null, 0);
            }
            var text = File.ReadAllText(tmp);
            int count = 0;
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var s in doc.RootElement.GetProperty("Sources").EnumerateArray())
                    if (s.TryGetProperty("Packages", out var pk)) count += pk.GetArrayLength();
            }
            catch { issues.Add(new Issue(IssueSeverity.Warning, "Software", "winget export JSON could not be parsed.")); }
            if (!r.Succeeded) issues.Add(new Issue(IssueSeverity.Info, "Software", "winget reported " + r.Describe() + "; some packages may be unavailable from winget sources."));
            return (text, count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            issues.Add(new Issue(IssueSeverity.Info, "Software", "winget not available: " + ex.Message));
            return (null, 0);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
