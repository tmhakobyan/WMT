using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WMT.Core;

public static class SettingGroups
{
    public const string Language = "Language and formats";
    public const string Interface = "Interface";
    public const string Network = "Network and printers";
    public const string System = "System";
    public const string Fonts = "Fonts";
    public const string Environment = "Environment";
    public static readonly string[] All = { Language, Interface, Network, System, Fonts, Environment };
}

public static class SettingKinds
{
    public const string Registry = "Registry", LanguageList = "LanguageList", TimeZone = "TimeZone", SystemLocale = "SystemLocale", WiFi = "WiFi",
        MappedDrive = "MappedDrive", Printer = "Printer", Hosts = "Hosts", PowerPlan = "PowerPlan", ScheduledTask = "ScheduledTask",
        StartupFolder = "StartupFolder", Font = "Font", Wallpaper = "Wallpaper", PathMerge = "PathMerge", Manual = "Manual";
}

/// <summary>One Windows setting that can be backed up and (usually) re-applied.</summary>
public sealed class SettingItem
{
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RestoreMethod { get; set; } = "";
    public bool RequiresAdmin { get; set; }
    public bool RequiresSignOut { get; set; }
    public Classification Classification { get; set; } = Classification.Recommended;
    public string Note { get; set; } = "";
    public bool Selected { get; set; }
    public List<RegistryValueRecord> Values { get; set; } = new();
    public string? Payload { get; set; }
    public string? PayloadEncoding { get; set; }
    public string? SourcePath { get; set; }
    public string? Parameter { get; set; }
    public List<string> Entries { get; set; } = new();
    public List<UserLanguage>? Languages { get; set; }
}

public sealed class WindowsSettingsSnapshot
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string Machine { get; set; } = Environment.MachineName;
    public string? ProfileSid { get; set; }
    public string? ProfilePath { get; set; }
    public string Account { get; set; } = "";
    public List<SettingItem> Items { get; set; } = new();
    public List<Issue> Issues { get; set; } = new();
}

public sealed class SettingsCaptureEnvironment
{
    public required IRegistryReader Registry { get; init; }
    public required IProcessRunner Processes { get; init; }
    public string WindowsDir { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public string? ProfilePath { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? ProfileSid { get; init; } = ProfileDiscovery.CurrentUserSid();
    public string Account { get; init; } = Environment.UserDomainName + "\\" + Environment.UserName;
    public string RoamingAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public Func<string, string?> ReadText { get; init; } = p => File.Exists(p) ? File.ReadAllText(p) : null;
    public CancellationToken Token { get; init; }
}

/// <summary>Reads the signed-in user's Windows settings (read-only). Secrets and Wi-Fi keys are never exported.</summary>
public sealed class WindowsSettingsCapture
{
    static readonly string[] ExplorerValues = { "HideFileExt", "Hidden", "ShowSuperHidden", "LaunchTo", "NavPaneExpandToCurrentFolder", "NavPaneShowAllFolders", "ShowInfoTip",
        "ShowStatusBar", "SeparateProcess", "PersistBrowsers", "AutoCheckSelect", "ShowCompColor", "DontPrettyPath", "TaskbarGlomLevel", "TaskbarSmallIcons",
        "ShowTaskViewButton", "Start_TrackDocs", "Start_TrackProgs", "TaskbarAl", "IconsOnly", "ShowEncryptCompressedColor" };
    static readonly string[] DesktopValues = { "WheelScrollLines", "WheelScrollChars", "CursorBlinkRate", "MenuShowDelay", "DragFullWindows", "FontSmoothing", "FontSmoothingType",
        "FontSmoothingGamma", "DoubleClickHeight", "DoubleClickWidth", "ActiveWndTrackTimeout", "CaretWidth" };
    static readonly string[] BuiltInPowerPlans = { "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "a1841308-3541-4fab-bc81-f71556f20b4a", "e9a42b02-d5df-448d-aa00-03f14749eb61" };
    static readonly Regex VendorTask = new(@"Microsoft|Google|Adobe|NVIDIA|Intel|OneDrive|Mozilla|Dropbox|Opera|Apple|Lenovo|Dell|HP |Hewlett|ASUS|Acer|Avast|AVG|Kaspersky|ESET|McAfee|Norton|CCleaner|Zoom|Teams|Brave|Yandex|Realtek|AMD|Logitech|Steam|Epic",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> BuiltInPrinters = new(StringComparer.OrdinalIgnoreCase) { "Microsoft Print to PDF", "Microsoft XPS Document Writer", "Fax", "OneNote", "OneNote (Desktop)", "OneNote for Windows 10", "Send To OneNote 2016" };
    static readonly HashSet<string> EnvSkip = new(StringComparer.OrdinalIgnoreCase) { "Path", "TEMP", "TMP", "OneDrive", "OneDriveConsumer", "OneDriveCommercial" };

    readonly SettingsCaptureEnvironment env;
    readonly WindowsSettingsSnapshot snap = new();
    public WindowsSettingsCapture(SettingsCaptureEnvironment env) { this.env = env; }

    public WindowsSettingsSnapshot Capture(IProgress<ProgressInfo>? progress = null)
    {
        snap.ProfileSid = env.ProfileSid; snap.ProfilePath = env.ProfilePath; snap.Account = env.Account;
        void Step(string s, Action a)
        {
            env.Token.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("Windows settings: " + s, snap.Items.Count, null));
            try { a(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { snap.Issues.Add(new Issue(IssueSeverity.Warning, "Windows settings", $"{s}: could not be read ({ex.Message})")); }
        }
        Step("regional format", RegionalFormat);
        Step("languages", LanguageList);
        Step("time zone and system locale", TimeAndLocale);
        Step("interface", Interface);
        Step("Wi-Fi", WiFi);
        Step("mapped drives and printers", DrivesAndPrinters);
        Step("hosts file", Hosts);
        Step("power plan", PowerPlan);
        Step("scheduled tasks", ScheduledTasks);
        Step("startup programs", Startup);
        Step("fonts", Fonts);
        Step("environment", EnvironmentVars);
        foreach (var i in snap.Items) i.Selected = i.Classification == Classification.Recommended;
        return snap;
    }

    SettingItem Add(string group, string name, string kind, string summary, string method, Classification cls = Classification.Recommended, string note = "", bool admin = false, bool signout = false)
    {
        var i = new SettingItem { Id = PathUtil.StableId(group + "|" + name + "|" + kind), Group = group, Name = name, Kind = kind, Summary = summary, RestoreMethod = method,
            Classification = cls, Note = note, RequiresAdmin = admin, RequiresSignOut = signout };
        snap.Items.Add(i);
        return i;
    }

    List<RegistryValueRecord> ReadValues(string key, IEnumerable<string>? only = null, Func<string, bool>? skip = null)
    {
        var list = new List<RegistryValueRecord>();
        var names = only?.ToList() ?? env.Registry.ValueNames("HKCU", key).ToList();
        foreach (var n in names)
        {
            if (skip?.Invoke(n) == true || Redaction.LooksSecretName(n)) continue;
            var v = env.Registry.Value("HKCU", key, n);
            if (v == null || v.Data is byte[] { Length: > 1024 }) continue;
            var r = new RegistryValueRecord { Id = PathUtil.StableId(key + "|" + n), Hive = "HKCU", KeyPath = key, ValueName = n, Type = v.Kind, ProfileSid = env.ProfileSid, Application = "Windows" };
            if (v.Data is string[] arr) r.MultiData = arr.ToList(); else r.Data = RegistryText.ToText(v.Kind, v.Data);
            list.Add(r);
        }
        return list;
    }

    static string Describe(IEnumerable<RegistryValueRecord> vals, int max = 6)
    {
        var l = vals.ToList();
        return $"{l.Count} value(s): " + string.Join(", ", l.Take(max).Select(v => $"{v.ValueName}={Short(v.Data ?? string.Join("|", v.MultiData ?? new()))}")) + (l.Count > max ? ", …" : "");
    }
    static string Short(string s) => s.Length > 40 ? s[..37] + "…" : s;

    void RegItem(string group, string name, string key, IEnumerable<string>? only, Classification cls = Classification.Recommended, string note = "", bool signout = true, Func<string, bool>? skip = null)
    {
        var vals = ReadValues(key, only, skip);
        if (vals.Count == 0) return;
        var i = Add(group, name, SettingKinds.Registry, Describe(vals), "Apply values to HKEY_CURRENT_USER (old values saved for Undo)", cls, note, signout: signout);
        i.Values = vals;
    }

    void RegionalFormat() =>
        RegItem(SettingGroups.Language, "Regional format (dates, numbers, currency)", @"Control Panel\International", null,
            note: "Applied for the signed-in user; sign out and in again to see every change.", skip: n => n.Equals("User Profile", StringComparison.OrdinalIgnoreCase));

    void LanguageList()
    {
        var input = new KeyboardLayoutDiscovery(env.Registry).ReadUserInput();
        if (input.Languages.Count == 0) return;
        var i = Add(SettingGroups.Language, "Display/input languages and keyboard order", SettingKinds.LanguageList,
            string.Join("; ", input.Languages.Select(l => $"{l.Tag} [{string.Join(", ", l.InputTips)}]")),
            "PowerShell Set-WinUserLanguageList (install custom keyboard layouts first)",
            note: "Language packs (display language, spelling, handwriting) may download from Windows Update.");
        i.Languages = input.Languages;
        RegItem(SettingGroups.Language, "Keyboard switching hotkeys", @"Keyboard Layout\Toggle", null, note: "Alt+Shift / Ctrl+Shift choice");
    }

    void TimeAndLocale()
    {
        var tz = env.Registry.Value("HKLM", @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "TimeZoneKeyName", RegView.Reg64)?.AsString();
        if (!string.IsNullOrWhiteSpace(tz))
        {
            var i = Add(SettingGroups.Language, "Time zone", SettingKinds.TimeZone, tz, "tzutil /s");
            i.Parameter = tz.Trim();
        }
        var lcidHex = env.Registry.Value("HKLM", @"SYSTEM\CurrentControlSet\Control\Nls\Language", "Default", RegView.Reg64)?.AsString();
        var acp = env.Registry.Value("HKLM", @"SYSTEM\CurrentControlSet\Control\Nls\CodePage", "ACP", RegView.Reg64)?.AsString();
        if (!string.IsNullOrWhiteSpace(lcidHex))
        {
            string name;
            try { name = CultureInfo.GetCultureInfo(Convert.ToInt32(lcidHex, 16)).Name; } catch { name = lcidHex; }
            var i = Add(SettingGroups.Language, "Language for non-Unicode programs (system locale)", SettingKinds.SystemLocale,
                $"{name} (code page {acp ?? "?"}){(acp == "65001" ? ", 'Beta: Use Unicode UTF-8' is ON" : "")}",
                "Set-WinSystemLocale (administrator; restart required)", admin: true, note: acp == "65001" ? "UTF-8 option must be switched on manually: Region → Administrative → Change system locale." : "");
            i.Parameter = name;
        }
    }

    void Interface()
    {
        RegItem(SettingGroups.Interface, "File Explorer options (extensions, hidden files, taskbar)", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ExplorerValues,
            note: "Explorer restarts or sign-out needed.");
        RegItem(SettingGroups.Interface, "Mouse", @"Control Panel\Mouse", null);
        RegItem(SettingGroups.Interface, "Keyboard repeat rate and delay", @"Control Panel\Keyboard", null);
        RegItem(SettingGroups.Interface, "Scrolling, cursor and menu behaviour", @"Control Panel\Desktop", DesktopValues);
        var personalize = ReadValues(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dwm = ReadValues(@"Software\Microsoft\Windows\DWM", new[] { "AccentColor", "ColorPrevalence", "ColorizationColor", "EnableWindowColorization" });
        if (personalize.Count + dwm.Count > 0)
        {
            var i = Add(SettingGroups.Interface, "Dark/light mode and accent colour", SettingKinds.Registry, Describe(personalize.Concat(dwm)), "Apply values to HKEY_CURRENT_USER", signout: true);
            i.Values = personalize.Concat(dwm).ToList();
        }
        var acc = new List<RegistryValueRecord>();
        foreach (var sub in new[] { "StickyKeys", "ToggleKeys", "FilterKeys", "Keyboard Response", "HighContrast", "MouseKeys" })
            acc.AddRange(ReadValues($@"Control Panel\Accessibility\{sub}"));
        if (acc.Count > 0)
        {
            var i = Add(SettingGroups.Interface, "Accessibility (sticky keys, filter keys, …)", SettingKinds.Registry, Describe(acc), "Apply values to HKEY_CURRENT_USER", Classification.Review, signout: true);
            i.Values = acc;
        }
        RegItem(SettingGroups.Interface, "Mouse pointer scheme", @"Control Panel\Cursors", null, Classification.Review, "Custom pointer files must exist on the new system.");
        var wp = env.Registry.Value("HKCU", @"Control Panel\Desktop", "Wallpaper")?.AsString();
        if (!string.IsNullOrWhiteSpace(wp))
        {
            bool builtIn = wp.StartsWith(env.WindowsDir, StringComparison.OrdinalIgnoreCase);
            bool exists = File.Exists(wp);
            var i = Add(SettingGroups.Interface, "Desktop wallpaper", SettingKinds.Wallpaper, wp + (exists ? "" : " (file not found)"),
                builtIn ? "Set the same Windows picture" : "Copy the picture and set it as wallpaper", exists || builtIn ? Classification.Recommended : Classification.Unsupported);
            i.SourcePath = exists && !builtIn ? wp : null; i.Parameter = wp;
            i.Values = ReadValues(@"Control Panel\Desktop", new[] { "WallpaperStyle", "TileWallpaper" });
        }
    }

    void WiFi()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wmt-wifi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var r = env.Processes.RunAsync("netsh", new[] { "wlan", "export", "profile", "folder=" + tmp }, TimeSpan.FromSeconds(60), env.Token).GetAwaiter().GetResult();
            var files = Directory.GetFiles(tmp, "*.xml");
            if (files.Length == 0)
            {
                snap.Issues.Add(new Issue(IssueSeverity.Info, "Windows settings", "No Wi-Fi profiles were exported (" + (r.Succeeded ? "none on this computer" : r.Describe()) + ")."));
                return;
            }
            foreach (var f in files)
            {
                var xml = StripWifiKey(File.ReadAllText(f));
                var name = Regex.Match(xml, @"<name>(.*?)</name>").Groups[1].Value;
                var auth = Regex.Match(xml, @"<authentication>(.*?)</authentication>").Groups[1].Value;
                var i = Add(SettingGroups.Network, "Wi-Fi: " + name, SettingKinds.WiFi, $"{name} ({auth}), password NOT exported", "netsh wlan add profile (you type the password once when connecting)");
                i.Payload = xml; i.PayloadEncoding = "text"; i.Parameter = name;
            }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Removes the key material so the exported profile never contains a password.</summary>
    public static string StripWifiKey(string xml) =>
        Regex.Replace(xml, @"\s*<sharedKey>.*?</sharedKey>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    void DrivesAndPrinters()
    {
        foreach (var letter in env.Registry.SubKeys("HKCU", "Network"))
        {
            var k = "Network\\" + letter;
            var remote = env.Registry.Value("HKCU", k, "RemotePath")?.AsString();
            if (string.IsNullOrWhiteSpace(remote)) continue;
            var user = env.Registry.Value("HKCU", k, "UserName")?.AsString();
            var i = Add(SettingGroups.Network, $"Mapped drive {letter.ToUpperInvariant()}:", SettingKinds.MappedDrive, remote + (string.IsNullOrEmpty(user) ? "" : $" (user {user})"),
                "Recreate the persistent mapping; Windows reconnects at next sign-in and asks for credentials", signout: true);
            i.Parameter = letter.ToUpperInvariant(); i.Entries = new() { remote, user ?? "" };
        }
        foreach (var conn in env.Registry.SubKeys("HKCU", @"Printers\Connections"))
        {
            var path = conn.Replace(',', '\\');
            var i = Add(SettingGroups.Network, "Network printer " + path, SettingKinds.Printer, path, "Connect the printer (Windows downloads the driver from the print server)");
            i.Parameter = path;
        }
        const string lp = @"SYSTEM\CurrentControlSet\Control\Print\Printers";
        foreach (var p in env.Registry.SubKeys("HKLM", lp, RegView.Reg64))
        {
            if (BuiltInPrinters.Contains(p)) continue;
            var driver = env.Registry.Value("HKLM", lp + "\\" + p, "Printer Driver", RegView.Reg64)?.AsString() ?? "?";
            var port = env.Registry.Value("HKLM", lp + "\\" + p, "Port", RegView.Reg64)?.AsString() ?? "?";
            if (port.StartsWith("\\\\")) continue;
            Add(SettingGroups.Network, "Local printer " + p, SettingKinds.Manual, $"driver {driver}, port {port}",
                "Manual: install the printer driver from the manufacturer, then add the printer on the same port", Classification.Unsupported);
        }
        var def = env.Registry.Value("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Windows", "Device")?.AsString();
        if (!string.IsNullOrWhiteSpace(def))
            Add(SettingGroups.Network, "Default printer", SettingKinds.Manual, def.Split(',')[0], "Manual: Settings → Printers & scanners (after the printer exists)", Classification.Review);
    }

    void Hosts()
    {
        var path = Path.Combine(env.WindowsDir, @"System32\drivers\etc\hosts");
        var text = env.ReadText(path);
        if (text == null) return;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
        if (lines.Count == 0) return;
        var i = Add(SettingGroups.Network, "hosts file entries", SettingKinds.Hosts, $"{lines.Count} custom line(s): " + Short(string.Join(" | ", lines)),
            "Add missing lines to the new hosts file (administrator; previous file saved)", admin: true);
        i.Entries = lines; i.Payload = text; i.PayloadEncoding = "text";
    }

    void PowerPlan()
    {
        var r = env.Processes.RunAsync("powercfg", new[] { "/getactivescheme" }, TimeSpan.FromSeconds(20), env.Token).GetAwaiter().GetResult();
        if (!r.Succeeded) { snap.Issues.Add(new Issue(IssueSeverity.Info, "Windows settings", "Power plan not readable: " + r.Describe())); return; }
        var m = Regex.Match(r.StdOut, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((.*?)\)");
        if (!m.Success) return;
        var guid = m.Groups[1].Value.ToLowerInvariant(); var name = m.Groups[2].Value;
        bool builtIn = BuiltInPowerPlans.Contains(guid);
        var i = Add(SettingGroups.System, "Power plan", SettingKinds.PowerPlan, $"{name} ({guid}){(builtIn ? ", built into Windows" : ", custom")}",
            builtIn ? "powercfg /setactive" : "powercfg /import + /setactive (administrator)", admin: !builtIn);
        i.Parameter = guid;
        if (!builtIn)
        {
            var file = Path.Combine(Path.GetTempPath(), "wmt-power-" + Guid.NewGuid().ToString("N") + ".pow");
            try
            {
                var ex = env.Processes.RunAsync("powercfg", new[] { "/export", file, guid }, TimeSpan.FromSeconds(30), env.Token).GetAwaiter().GetResult();
                if (ex.Succeeded && File.Exists(file)) { i.Payload = Convert.ToBase64String(File.ReadAllBytes(file)); i.PayloadEncoding = "base64"; }
                else { i.Note = "Export failed (" + ex.Describe() + "): the plan's settings must be re-created manually."; i.Classification = Classification.Review; }
            }
            finally { try { File.Delete(file); } catch { } }
        }
    }

    const string TaskScript = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' } | ForEach-Object { [pscustomobject]@{ Name=$_.TaskName; Path=$_.TaskPath; Author=[string]$_.Author; State=[string]$_.State; Xml=[string](Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath) } } | ConvertTo-Json -Depth 3 -Compress";

    void ScheduledTasks()
    {
        var r = env.Processes.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", TaskScript }, TimeSpan.FromSeconds(90), env.Token).GetAwaiter().GetResult();
        if (!r.Succeeded) { snap.Issues.Add(new Issue(IssueSeverity.Info, "Windows settings", "Scheduled tasks not readable: " + r.Describe())); return; }
        var text = r.StdOut.Trim();
        if (text.Length == 0) return;
        using var doc = JsonDocument.Parse(text);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        var me = env.Account.Contains('\\') ? env.Account[(env.Account.LastIndexOf('\\') + 1)..] : env.Account;
        foreach (var t in arr)
        {
            string S(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S("Name"); var path = S("Path"); var author = S("Author"); var xml = S("Xml");
            if (name.Length == 0 || xml.Length == 0) continue;
            bool vendor = VendorTask.IsMatch(name + " " + author + " " + path);
            bool mine = me.Length > 0 && author.Contains(me, StringComparison.OrdinalIgnoreCase);
            var cls = vendor ? Classification.InstallerManaged : mine ? Classification.Recommended : Classification.Review;
            var i = Add(SettingGroups.System, $"Scheduled task {path}{name}", SettingKinds.ScheduledTask, $"author {(author.Length == 0 ? "unknown" : author)}, {S("State")}",
                vendor ? "Created by a program: reinstalling the program recreates it" : "schtasks /create /xml (existing tasks are never overwritten)", cls);
            i.Payload = xml; i.PayloadEncoding = "text"; i.Parameter = path + name;
        }
    }

    void Startup()
    {
        RegItem(SettingGroups.System, "Programs started at sign-in (Run key)", @"Software\Microsoft\Windows\CurrentVersion\Run", null, Classification.Review,
            "The programs must be installed on the new system first.", signout: false);
        var folder = Path.Combine(env.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs\Startup");
        if (Directory.Exists(folder))
        {
            var files = Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count > 0)
            {
                var i = Add(SettingGroups.System, "Startup folder shortcuts", SettingKinds.StartupFolder, string.Join(", ", files.Select(Path.GetFileName)),
                    "Copied with your files; restored into the mapped profile (Backup & Recovery → Restore)", Classification.Review);
                i.SourcePath = folder; i.Entries = files;
            }
        }
    }

    void Fonts()
    {
        var userFonts = Path.Combine(env.LocalAppData, @"Microsoft\Windows\Fonts");
        const string fk = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
        foreach (var n in env.Registry.ValueNames("HKCU", fk))
        {
            var file = env.Registry.Value("HKCU", fk, n)?.AsString();
            if (string.IsNullOrWhiteSpace(file)) continue;
            if (!Path.IsPathRooted(file)) file = Path.Combine(userFonts, file);
            if (!File.Exists(file)) continue;
            var i = Add(SettingGroups.Fonts, "Font " + n, SettingKinds.Font, file, "Install for the signed-in user (copied + registered, no administrator needed)",
                Classification.Recommended, "Check that the font's license allows installing it again.");
            i.SourcePath = file; i.Parameter = n;
        }
        // System-wide fonts added later than Windows' own fonts (heuristic): offered for review.
        var winFonts = Path.Combine(env.WindowsDir, "Fonts");
        var entries = new List<(string Name, string File, DateTime Created)>();
        foreach (var n in env.Registry.ValueNames("HKLM", fk, RegView.Reg64))
        {
            var file = env.Registry.Value("HKLM", fk, n, RegView.Reg64)?.AsString();
            if (string.IsNullOrWhiteSpace(file)) continue;
            if (!Path.IsPathRooted(file)) file = Path.Combine(winFonts, file);
            try { if (File.Exists(file)) entries.Add((n, file, File.GetCreationTimeUtc(file).Date)); } catch { }
        }
        if (entries.Count < 20) return;
        var common = entries.GroupBy(e => e.Created).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToHashSet();
        foreach (var e in entries.Where(e => !common.Contains(e.Created)))
        {
            var i = Add(SettingGroups.Fonts, "Font " + e.Name + " (system-wide)", SettingKinds.Font, e.File + $" (added {e.Created:yyyy-MM-dd})",
                "Install for the signed-in user", Classification.Review, "Probably installed by you or by a program (date differs from Windows' own fonts). Keep only fonts you installed.");
            i.SourcePath = e.File; i.Parameter = e.Name;
        }
    }

    void EnvironmentVars()
    {
        var vals = ReadValues("Environment", null, n => EnvSkip.Contains(n));
        if (vals.Count > 0)
        {
            var i = Add(SettingGroups.Environment, "User environment variables", SettingKinds.Registry, Describe(vals), "Apply to HKEY_CURRENT_USER\\Environment (secret-looking names are never exported)", signout: true);
            i.Values = vals;
        }
        var path = env.Registry.Value("HKCU", "Environment", "Path")?.AsString();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var i = Add(SettingGroups.Environment, "User PATH entries", SettingKinds.PathMerge, string.Join("; ", parts), "Add the missing entries to the user PATH (existing entries kept)", signout: true);
            i.Entries = parts;
        }
        var assoc = new List<string>();
        foreach (var ext in new[] { ".doc", ".docx", ".docm", ".dotm", ".rtf", ".txt", ".pdf", ".py", ".ipynb", ".csv", ".xlsx", ".tif", ".tiff", ".png", ".jpg", ".html" })
        {
            var prog = env.Registry.Value("HKCU", $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice", "ProgId")?.AsString();
            if (prog != null) assoc.Add($"{ext} → {prog}");
        }
        if (assoc.Count > 0)
        {
            var i = Add(SettingGroups.Environment, "Default apps for file types", SettingKinds.Manual, string.Join("; ", assoc),
                "Manual: Settings → Apps → Default apps (Windows protects these choices)", Classification.Unsupported);
            i.Entries = assoc;
        }
    }
}
