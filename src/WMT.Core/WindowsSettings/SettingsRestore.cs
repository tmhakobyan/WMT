using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WMT.Core;

public interface IElevatedRunner
{
    /// <summary>Runs a PowerShell script with administrator rights (Windows shows the UAC prompt). Output is captured into the result.</summary>
    Task<ProcessResult> RunPowerShellElevatedAsync(string script, CancellationToken ct);
}

public sealed class ElevatedPowerShell : IElevatedRunner
{
    /// <summary>False runs the same wrapper without the UAC prompt (used by tests on an already elevated CI machine).</summary>
    public bool RunAs { get; init; } = true;

    public async Task<ProcessResult> RunPowerShellElevatedAsync(string script, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(Path.GetTempPath(), "wmt-elevated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "out.txt");
        var ps1 = Path.Combine(dir, "run.ps1");
        var wrapped = "$ErrorActionPreference = 'Stop'\r\ntry {\r\n& {\r\n" + script + "\r\n} *>&1 | Out-File -FilePath '" + log.Replace("'", "''") + "' -Encoding utf8\r\nexit 0\r\n} catch {\r\n$_ | Out-File -FilePath '" + log.Replace("'", "''") + "' -Append -Encoding utf8\r\nexit 1\r\n}\r\n";
        File.WriteAllText(ps1, wrapped, new UTF8Encoding(true));
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            if (!RunAs) { psi.UseShellExecute = false; psi.Verb = ""; psi.CreateNoWindow = true; }
            Process? p;
            try { p = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { return new ProcessResult(null, "", "", false, true, "Administrator permission was declined", sw.Elapsed); }
            if (p == null) return new ProcessResult(null, "", "", false, true, "Process did not start", sw.Elapsed);
            await p.WaitForExitAsync(ct);
            var output = File.Exists(log) ? File.ReadAllText(log) : "";
            return new ProcessResult(p.ExitCode, output, p.ExitCode == 0 ? "" : output, false, false, null, sw.Elapsed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public sealed class SettingAction
{
    public string ItemId { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Selected { get; set; }
    public bool Blocked { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool RequiresSignOut { get; set; }
}

public sealed class SettingsRestoreRequest
{
    public required LoadedPackage Package { get; init; }
    public required WindowsSettingsSnapshot Snapshot { get; init; }
    /// <summary>Profile folder of the signed-in user on THIS computer.</summary>
    public required string TargetProfilePath { get; init; }
    public string TargetLocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public KeyboardSnapshot? Keyboard { get; init; }
}

/// <summary>Re-applies selected Windows settings for the signed-in user. Every registry change keeps its old value (Undo);
/// commands are listed with their manual undo.</summary>
public sealed class SettingsRestoreEngine
{
    readonly ActivityLog log; readonly IRegistryReader reader; readonly IRegistryWriter writer; readonly IProcessRunner proc; readonly IElevatedRunner elevated;
    public Action<string>? SetWallpaper { get; set; } = DefaultSetWallpaper;
    public Func<string, bool> KeyboardLayoutInstalled { get; set; }

    public SettingsRestoreEngine(ActivityLog log, IRegistryReader reader, IRegistryWriter writer, IProcessRunner proc, IElevatedRunner elevated)
    {
        this.log = log; this.reader = reader; this.writer = writer; this.proc = proc; this.elevated = elevated;
        KeyboardLayoutInstalled = klid => reader.KeyExists("HKLM", KeyboardLayoutDiscovery.LayoutsKey + "\\" + klid, RegView.Reg64);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfoW(uint action, uint param, string vparam, uint winIni);
    static void DefaultSetWallpaper(string path) { if (OperatingSystem.IsWindows()) SystemParametersInfoW(0x0014, 0, path, 0x01 | 0x02); }

    static string Remap(string? text, SettingsRestoreRequest r) =>
        text == null || string.IsNullOrEmpty(r.Snapshot.ProfilePath) ? text ?? "" : text.Replace(r.Snapshot.ProfilePath!, r.TargetProfilePath, StringComparison.OrdinalIgnoreCase);

    bool SameLanguages(SettingItem i)
    {
        var cur = new KeyboardLayoutDiscovery(reader).ReadUserInput().Languages;
        var want = i.Languages ?? new();
        return cur.Count == want.Count && cur.Zip(want).All(p => p.First.Tag.Equals(p.Second.Tag, StringComparison.OrdinalIgnoreCase)
            && p.First.InputTips.SequenceEqual(p.Second.InputTips, StringComparer.OrdinalIgnoreCase));
    }

    string? CurrentSystemLocale()
    {
        var hex = reader.Value("HKLM", @"SYSTEM\CurrentControlSet\Control\Nls\Language", "Default", RegView.Reg64)?.AsString();
        try { return hex == null ? null : System.Globalization.CultureInfo.GetCultureInfo(Convert.ToInt32(hex, 16)).Name; } catch { return null; }
    }

    static string Wanted(RegistryValueRecord v, SettingsRestoreRequest r) =>
        v.MultiData != null ? string.Join("; ", v.MultiData.Select(m => Remap(m, r))) : Remap(v.Data, r);

    FileEntry? PackageFile(SettingsRestoreRequest r, string? source) =>
        source == null ? null : r.Package.Files.FirstOrDefault(f => f.CopyStatus == FileCopyStatus.Copied && PathUtil.SamePath(f.SourcePath, source));

    public List<SettingAction> DryRun(SettingsRestoreRequest r)
    {
        var list = new List<SettingAction>();
        foreach (var i in r.Snapshot.Items)
        {
            var a = new SettingAction { ItemId = i.Id, Group = i.Group, Name = i.Name, Kind = i.Kind, RequiresAdmin = i.RequiresAdmin, RequiresSignOut = i.RequiresSignOut };
            switch (i.Kind)
            {
                case SettingKinds.Registry:
                {
                    var changes = i.Values.Count(v => (reader.Value("HKCU", v.KeyPath, v.ValueName)?.AsString() ?? "\u0000") != Wanted(v, r));
                    a.Description = changes == 0 ? "Already the same on this computer" : $"Set {changes} of {i.Values.Count} value(s) in HKCU (old values saved)";
                    a.Blocked = changes == 0;
                    break;
                }
                case SettingKinds.LanguageList when SameLanguages(i):
                    a.Description = "Already the same on this computer"; a.Blocked = true; break;
                case SettingKinds.TimeZone when string.Equals(reader.Value("HKLM", @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "TimeZoneKeyName", RegView.Reg64)?.AsString()?.Trim(), i.Parameter, StringComparison.OrdinalIgnoreCase):
                    a.Description = "Already the same on this computer"; a.Blocked = true; break;
                case SettingKinds.SystemLocale when string.Equals(CurrentSystemLocale(), i.Parameter, StringComparison.OrdinalIgnoreCase):
                    a.Description = "Already the same on this computer"; a.Blocked = true; break;
                case SettingKinds.LanguageList:
                {
                    var missing = (i.Languages ?? new()).SelectMany(l => l.InputTips).Where(t => !KeyboardLayoutInstalled(t.Split(':')[1])).ToList();
                    a.Description = "Set languages and keyboard order: " + i.Summary + (missing.Count > 0 ? $" — {missing.Count} layout(s) not installed yet and will be skipped: {string.Join(", ", missing)}" : "");
                    break;
                }
                case SettingKinds.TimeZone: a.Description = $"tzutil /s \"{i.Parameter}\""; break;
                case SettingKinds.SystemLocale: a.Description = $"Set-WinSystemLocale {i.Parameter} (administrator, then restart)"; break;
                case SettingKinds.WiFi: a.Description = $"Add Wi-Fi profile '{i.Parameter}' (password asked when connecting)"; break;
                case SettingKinds.MappedDrive:
                    a.Description = reader.KeyExists("HKCU", "Network\\" + i.Parameter) ? $"Drive {i.Parameter}: is already mapped here — kept" : $"Map {i.Parameter}: → {i.Entries.FirstOrDefault()} at next sign-in";
                    a.Blocked = reader.KeyExists("HKCU", "Network\\" + i.Parameter);
                    break;
                case SettingKinds.Printer: a.Description = $"Connect printer {i.Parameter}"; break;
                case SettingKinds.Hosts: a.Description = $"Add {i.Entries.Count} line(s) to hosts if missing (administrator)"; break;
                case SettingKinds.PowerPlan: a.Description = i.Payload != null ? $"Import and activate power plan {i.Parameter} (administrator)" : $"Activate power plan {i.Parameter}"; break;
                case SettingKinds.ScheduledTask: a.Description = i.Classification == Classification.InstallerManaged ? "Recreated by reinstalling its program" : $"Create task {i.Parameter} (never overwrites)"; a.Blocked = i.Classification == Classification.InstallerManaged; break;
                case SettingKinds.StartupFolder: a.Description = "Restored with your files (Backup & Recovery → Restore)"; a.Blocked = true; break;
                case SettingKinds.Font:
                    a.Description = PackageFile(r, i.SourcePath) != null ? $"Install font for this user: {Path.GetFileName(i.SourcePath)}" : "Font file is not in the package (it was not selected for backup)";
                    a.Blocked = PackageFile(r, i.SourcePath) == null;
                    break;
                case SettingKinds.Wallpaper:
                    bool builtIn = i.SourcePath == null && i.Parameter != null && File.Exists(i.Parameter);
                    a.Description = builtIn ? "Set Windows picture " + i.Parameter : PackageFile(r, i.SourcePath) != null ? "Copy picture to Pictures\\Restored wallpaper and set it" : "Picture is not in the package";
                    a.Blocked = !builtIn && PackageFile(r, i.SourcePath) == null;
                    break;
                case SettingKinds.PathMerge:
                {
                    var current = (reader.Value("HKCU", "Environment", "Path")?.AsString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var add = i.Entries.Select(e => Remap(e, r)).Where(e => !current.Contains(e)).ToList();
                    a.Description = add.Count == 0 ? "All entries already present" : "Add to PATH: " + string.Join("; ", add);
                    a.Blocked = add.Count == 0;
                    break;
                }
                default: a.Description = i.RestoreMethod; a.Blocked = true; break;
            }
            a.Selected = i.Selected && !a.Blocked;
            list.Add(a);
        }
        return list;
    }

    public async Task<RestoreJournal> ExecuteAsync(SettingsRestoreRequest r, IEnumerable<SettingAction> actions, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var journal = new RestoreJournal { PackageRoot = r.Package.Layout.Root };
        var jdir = Path.Combine(r.Package.Layout.Status, "Settings-" + journal.JournalId);
        Directory.CreateDirectory(jdir);
        var jpath = Path.Combine(jdir, "journal.json");
        var todo = actions.Where(a => a.Selected && !a.Blocked).ToList();
        int n = 0;
        foreach (var a in todo)
        {
            ct.ThrowIfCancellationRequested();
            var i = r.Snapshot.Items.First(x => x.Id == a.ItemId);
            progress?.Report(new ProgressInfo("Applying: " + i.Name, n++, todo.Count));
            try
            {
                await ApplyOne(r, i, journal, jdir, ct);
                journal.Restored++;
                log.Info("settings.applied", i.Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { journal.Failed++; journal.Errors.Add($"{i.Name}: {ex.Message}"); log.Error("settings.failed", $"{i.Name}: {ex.Message}"); }
            finally { Json.WriteFile(jpath, journal); }
        }
        Json.WriteFile(jpath, journal);
        return journal;
    }

    void SetReg(RestoreJournal j, string key, string name, string kind, object? data)
    {
        var old = reader.Value("HKCU", key, name);
        var e = new JournalEntry { Kind = "Registry", RegKey = key, RegName = name, RegExisted = old != null, RegOldType = old?.Kind };
        if (old?.Data is string[] arr) e.RegOldMulti = arr.ToList(); else e.RegOldData = old == null ? null : RegistryText.ToText(old.Kind, old.Data);
        j.Entries.Add(e);
        writer.SetValue("HKCU", key, name, kind, data);
    }

    void Command(RestoreJournal j, string description, string undo) => j.Entries.Add(new JournalEntry { Kind = "Command", Target = description, UndoHint = undo });

    async Task Run(string file, params string[] args)
    {
        var res = await proc.RunAsync(file, args, TimeSpan.FromMinutes(5), CancellationToken.None);
        if (!res.Succeeded) throw new InvalidOperationException($"{file} {string.Join(' ', args)}: {res.Describe()} {res.StdErr.Trim()} {res.StdOut.Trim()}".Trim());
    }

    async Task Elevated(string script, CancellationToken ct)
    {
        var res = await elevated.RunPowerShellElevatedAsync(script, ct);
        if (!res.Succeeded) throw new InvalidOperationException("Administrator step failed: " + (res.StartError ?? res.StdErr.Trim()));
    }

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    async Task ApplyOne(SettingsRestoreRequest r, SettingItem i, RestoreJournal j, string jdir, CancellationToken ct)
    {
        switch (i.Kind)
        {
            case SettingKinds.Registry:
                foreach (var v in i.Values)
                {
                    var data = v.MultiData != null ? RegistryText.FromText(v.Type, null, v.MultiData.Select(m => Remap(m, r)).ToList()) : RegistryText.FromText(v.Type, Remap(v.Data, r), null);
                    SetReg(j, v.KeyPath, v.ValueName, v.Type, data);
                }
                break;
            case SettingKinds.LanguageList:
            {
                var script = KeyboardLayoutDiscovery.LanguageListScript(i.Languages ?? new(), t => KeyboardLayoutInstalled(t.Split(':')[1]));
                var before = await proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "Get-WinUserLanguageList | ForEach-Object { $_.LanguageTag + ' ' + ($_.InputMethodTips -join ',') }" }, TimeSpan.FromMinutes(1), ct);
                File.WriteAllText(Path.Combine(jdir, "languages-before.txt"), before.StdOut);
                await Run("powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script);
                Command(j, "Language list set", "Previous list saved in languages-before.txt; set it again in Settings → Time & language");
                break;
            }
            case SettingKinds.TimeZone:
            {
                var before = await proc.RunAsync("tzutil", new[] { "/g" }, TimeSpan.FromSeconds(30), ct);
                await Run("tzutil", "/s", i.Parameter!);
                Command(j, "Time zone " + i.Parameter, $"tzutil /s \"{before.StdOut.Trim()}\"");
                break;
            }
            case SettingKinds.SystemLocale:
                await Elevated($"Set-WinSystemLocale -SystemLocale {Q(i.Parameter!)}", ct);
                Command(j, "System locale " + i.Parameter, "Region → Administrative → Change system locale (restart required)");
                break;
            case SettingKinds.WiFi:
            {
                var f = Path.Combine(jdir, "wifi-" + PathUtil.StableId(i.Parameter ?? i.Id) + ".xml");
                File.WriteAllText(f, WindowsSettingsCapture.StripWifiKey(i.Payload ?? ""));
                await Run("netsh", "wlan", "add", "profile", "filename=" + f, "user=current");
                Command(j, "Wi-Fi profile " + i.Parameter, $"netsh wlan delete profile name=\"{i.Parameter}\"");
                break;
            }
            case SettingKinds.MappedDrive:
            {
                var k = "Network\\" + i.Parameter;
                SetReg(j, k, "RemotePath", "String", i.Entries[0]);
                if (i.Entries.Count > 1 && i.Entries[1].Length > 0) SetReg(j, k, "UserName", "String", i.Entries[1]);
                SetReg(j, k, "ProviderName", "String", "Microsoft Windows Network");
                SetReg(j, k, "ProviderType", "DWord", 0x20000);
                SetReg(j, k, "ConnectionType", "DWord", 1);
                SetReg(j, k, "DeferFlags", "DWord", 4);
                break;
            }
            case SettingKinds.Printer:
                await Run("rundll32", "printui.dll,PrintUIEntry", "/in", "/n", i.Parameter!);
                Command(j, "Printer " + i.Parameter, $"Settings → Printers & scanners → remove {i.Parameter}");
                break;
            case SettingKinds.Hosts:
            {
                var backup = Path.Combine(jdir, "hosts.before");
                var sb = new StringBuilder();
                sb.AppendLine("$h = Join-Path $env:WINDIR 'System32\\drivers\\etc\\hosts'");
                sb.AppendLine($"Copy-Item $h {Q(backup)} -Force");
                sb.AppendLine("$cur = @(Get-Content $h -ErrorAction SilentlyContinue)");
                foreach (var line in i.Entries) sb.AppendLine($"if ($cur -notcontains {Q(line)}) {{ Add-Content -Path $h -Value {Q(line)} -Encoding ascii }}");
                await Elevated(sb.ToString(), ct);
                Command(j, "hosts entries", $"Copy {backup} back over C:\\Windows\\System32\\drivers\\etc\\hosts (administrator)");
                break;
            }
            case SettingKinds.PowerPlan:
                if (i.Payload != null)
                {
                    var f = Path.Combine(jdir, "plan.pow");
                    File.WriteAllBytes(f, Convert.FromBase64String(i.Payload));
                    await Elevated($"powercfg /import {Q(f)} {i.Parameter}\r\npowercfg /setactive {i.Parameter}", ct);
                }
                else await Run("powercfg", "/setactive", i.Parameter!);
                Command(j, "Power plan " + i.Parameter, "Settings → System → Power: choose another plan");
                break;
            case SettingKinds.ScheduledTask:
            {
                var f = Path.Combine(jdir, "task-" + PathUtil.StableId(i.Parameter ?? i.Id) + ".xml");
                File.WriteAllText(f, i.Payload ?? "", Encoding.Unicode);
                await Run("schtasks", "/create", "/tn", i.Parameter!, "/xml", f);
                Command(j, "Scheduled task " + i.Parameter, $"schtasks /delete /tn \"{i.Parameter}\"");
                break;
            }
            case SettingKinds.Font:
            {
                var pf = PackageFile(r, i.SourcePath) ?? throw new FileNotFoundException("Font not in package");
                var dir = Path.Combine(r.TargetLocalAppData, @"Microsoft\Windows\Fonts");
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, Path.GetFileName(i.SourcePath!));
                if (File.Exists(target)) throw new IOException("A font file with this name already exists: " + target);
                File.Copy(PathUtil.SafeCombine(r.Package.Layout.Root, pf.PackagePath!), target);
                j.Entries.Add(new JournalEntry { Kind = "File", Target = target, RestoredSha256 = Hashing.Sha256File(target) });
                SetReg(j, @"Software\Microsoft\Windows NT\CurrentVersion\Fonts", i.Parameter!, "String", target);
                break;
            }
            case SettingKinds.Wallpaper:
            {
                string path;
                var pf = PackageFile(r, i.SourcePath);
                if (pf != null)
                {
                    var dir = Path.Combine(r.TargetProfilePath, "Pictures", "Restored wallpaper");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, Path.GetFileName(i.SourcePath!));
                    if (!File.Exists(path))
                    {
                        File.Copy(PathUtil.SafeCombine(r.Package.Layout.Root, pf.PackagePath!), path);
                        j.Entries.Add(new JournalEntry { Kind = "File", Target = path, RestoredSha256 = Hashing.Sha256File(path) });
                    }
                }
                else path = i.Parameter!;
                foreach (var v in i.Values) SetReg(j, v.KeyPath, v.ValueName, v.Type, RegistryText.FromText(v.Type, v.Data, v.MultiData));
                SetReg(j, @"Control Panel\Desktop", "Wallpaper", "String", path);
                SetWallpaper?.Invoke(path);
                break;
            }
            case SettingKinds.PathMerge:
            {
                var cur = reader.Value("HKCU", "Environment", "Path");
                var parts = (cur?.AsString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                foreach (var e in i.Entries.Select(e => Remap(e, r)))
                    if (!parts.Contains(e, StringComparer.OrdinalIgnoreCase)) parts.Add(e);
                SetReg(j, "Environment", "Path", cur?.Kind ?? "ExpandString", string.Join(";", parts));
                break;
            }
            default:
                throw new InvalidOperationException("This setting can only be restored manually: " + i.RestoreMethod);
        }
    }
}

/// <summary>Installs keyboard layouts from the package (MSI found during backup). msiexec asks for administrator rights itself.</summary>
public enum KeyboardInstallMethod { AlreadyInstalled, Msi, Dll, BuildFromKlc, None }

public sealed record KeyboardInstallPlan(KeyboardInstallMethod Method, string Description);

/// <summary>Reinstalls custom keyboard layouts on the new Windows: with the MSI when the package has one, otherwise directly from the
/// saved layout DLL (copy to System32/SysWOW64 + register the KLID — what an MSKLC setup package does), otherwise by rebuilding the DLL
/// from a .klc with MSKLC's compiler (kbdutool) when MSKLC is installed.</summary>
public sealed class KeyboardRestore
{
    readonly IProcessRunner proc; readonly IRegistryReader reader; readonly IElevatedRunner elevated;
    public MsklcInstall? Msklc { get; set; }
    public string System32 { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
    public string SysWow64 { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
    public bool Is64BitOs { get; init; } = Environment.Is64BitOperatingSystem;

    public KeyboardRestore(IProcessRunner proc, IRegistryReader reader, IElevatedRunner? elevated = null, MsklcInstall? msklc = null)
    { this.proc = proc; this.reader = reader; this.elevated = elevated ?? new ElevatedPowerShell(); Msklc = msklc; }

    public static string GeneratedKlcFileName(KeyboardLayoutInfo l) => l.KlcBaseName + ".klc";
    public static string GeneratedKlcPackagePath(KeyboardLayoutInfo l) => Path.Combine("Reports", "Keyboard", GeneratedKlcFileName(l));

    public bool IsInstalled(KeyboardLayoutInfo l) => reader.KeyExists("HKLM", KeyboardLayoutDiscovery.LayoutsKey + "\\" + l.Klid, RegView.Reg64);

    static string? Copied(LoadedPackage pkg, string? source) =>
        source == null ? null : pkg.Files.FirstOrDefault(x => x.CopyStatus == FileCopyStatus.Copied && PathUtil.SamePath(x.SourcePath, source))?.PackagePath;

    /// <summary>Package-relative path of the best installer for this layout, or null.</summary>
    public static string? InstallerInPackage(LoadedPackage pkg, KeyboardLayoutInfo l)
    {
        foreach (var src in l.SetupPackages.Concat(l.CachedMsi != null ? new[] { l.CachedMsi } : Array.Empty<string>()))
            if (Copied(pkg, src) is string p) return p;
        return null;
    }

    /// <summary>Package-relative paths of the saved 64-bit (System32) and 32-bit (SysWOW64) layout DLLs.</summary>
    public static (string? Dll64, string? Dll32) DllsInPackage(LoadedPackage pkg, KeyboardLayoutInfo l) => (Copied(pkg, l.Dll64), Copied(pkg, l.Dll32));

    /// <summary>Absolute path of a .klc usable for rebuilding: the user's own .klc from the package, else the one WMT generated.</summary>
    public static string? KlcInPackage(LoadedPackage pkg, KeyboardLayoutInfo l)
    {
        foreach (var k in l.KlcSources)
            if (Copied(pkg, k) is string rel) return PathUtil.SafeCombine(pkg.Layout.Root, rel);
        var gen = Path.Combine(pkg.Layout.Root, GeneratedKlcPackagePath(l));
        return File.Exists(gen) ? gen : null;
    }

    public KeyboardInstallPlan Plan(LoadedPackage pkg, KeyboardLayoutInfo l)
    {
        if (IsInstalled(l)) return new(KeyboardInstallMethod.AlreadyInstalled, "Already installed on this computer");
        if (InstallerInPackage(pkg, l) is string msi) return new(KeyboardInstallMethod.Msi, "Run its installer: msiexec /i " + msi);
        if (DllsInPackage(pkg, l).Dll64 is string dll)
            return new(KeyboardInstallMethod.Dll, $"Copy {l.LayoutFile} to System32{(DllsInPackage(pkg, l).Dll32 != null ? " and SysWOW64" : "")} and register {l.Klid} (from {dll}; MSKLC not needed)");
        var klc = KlcInPackage(pkg, l);
        if (klc != null && Msklc?.CanBuild == true) return new(KeyboardInstallMethod.BuildFromKlc, $"Build the DLL with MSKLC (kbdutool) from {Path.GetFileName(klc)}, then copy and register it");
        if (klc != null) return new(KeyboardInstallMethod.None, $"Install Microsoft Keyboard Layout Creator ({Msklc_DownloadHint}) to rebuild from {Path.GetFileName(klc)}, or open it with 'Open in MSKLC'");
        return new(KeyboardInstallMethod.None, "Nothing to install from in the package: the layout must be recreated");
    }

    const string Msklc_DownloadHint = "free, from Microsoft";

    public string Describe(LoadedPackage pkg, KeyboardLayoutInfo l) => Plan(pkg, l).Description;

    public async Task<string> InstallAsync(LoadedPackage pkg, KeyboardLayoutInfo l, CancellationToken ct)
    {
        var plan = Plan(pkg, l);
        switch (plan.Method)
        {
            case KeyboardInstallMethod.AlreadyInstalled: return "already installed";
            case KeyboardInstallMethod.Msi:
            {
                var msi = PathUtil.SafeCombine(pkg.Layout.Root, InstallerInPackage(pkg, l)!);
                var logFile = Path.Combine(pkg.Layout.Status, $"msiexec-{l.Klid}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.log");
                Directory.CreateDirectory(pkg.Layout.Status);
                var r = await proc.RunAsync("msiexec", new[] { "/i", msi, "/qb", "/norestart", "/l*v", logFile }, TimeSpan.FromMinutes(10), ct);
                if (r.ExitCode is not (0 or 3010)) throw new InvalidOperationException($"msiexec failed: {r.Describe()} (log {logFile})");
                return r.ExitCode == 3010 ? "installed — restart required" : "installed";
            }
            case KeyboardInstallMethod.Dll:
            {
                var (d64, d32) = DllsInPackage(pkg, l);
                return await InstallDllAsync(PathUtil.SafeCombine(pkg.Layout.Root, d64!), d32 == null ? null : PathUtil.SafeCombine(pkg.Layout.Root, d32), l, ct);
            }
            case KeyboardInstallMethod.BuildFromKlc:
            {
                var outDir = Path.Combine(Path.GetTempPath(), "wmt-kbd-build-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var b = await WMT.Core.Msklc.BuildAsync(proc, Msklc!, KlcInPackage(pkg, l)!, outDir, ct);
                    Directory.CreateDirectory(pkg.Layout.Status);
                    File.WriteAllText(Path.Combine(pkg.Layout.Status, $"kbdutool-{l.Klid}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.log"), b.Output);
                    if (b.Dll64 == null) throw new InvalidOperationException("MSKLC could not build the layout DLL:\n" + b.Output.Trim());
                    return "built with MSKLC; " + await InstallDllAsync(b.Dll64, b.DllWow64, l, ct);
                }
                finally { try { Directory.Delete(outDir, true); } catch { } }
            }
            default: throw new InvalidOperationException(plan.Description);
        }
    }

    /// <summary>Layout Id values must be unique among installed layouts; keeps the old one when it is free.</summary>
    public string ChooseLayoutId(string? wanted)
    {
        var used = new HashSet<int>();
        foreach (var k in reader.SubKeys("HKLM", KeyboardLayoutDiscovery.LayoutsKey, RegView.Reg64))
        {
            var v = reader.Value("HKLM", KeyboardLayoutDiscovery.LayoutsKey + "\\" + k, "Layout Id", RegView.Reg64)?.AsString();
            if (v != null && int.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out var n)) used.Add(n);
        }
        if (wanted != null && int.TryParse(wanted, System.Globalization.NumberStyles.HexNumber, null, out var w) && !used.Contains(w)) return wanted.ToLowerInvariant();
        int next = (used.Count == 0 ? 0 : used.Max()) + 1;
        while (used.Contains(next)) next++;
        return next.ToString("x4");
    }

    static bool SameFile(string a, string b) => new FileInfo(a).Length == new FileInfo(b).Length && Hashing.Sha256File(a) == Hashing.Sha256File(b);

    /// <summary>Installs a layout from its DLL(s). Never overwrites a different existing file or registration.</summary>
    public async Task<string> InstallDllAsync(string dll64, string? dll32, KeyboardLayoutInfo l, CancellationToken ct)
    {
        if (IsInstalled(l)) return "already installed";
        var file = l.LayoutFile;
        if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected layout file name '{file}'.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(l.Klid, "^[0-9A-Fa-f]{8}$")) throw new InvalidOperationException($"Unexpected KLID '{l.Klid}'.");
        if (!File.Exists(dll64)) throw new FileNotFoundException("Layout DLL not found in the package", dll64);
        var t64 = Path.Combine(System32, file);
        bool copy64 = true, copy32 = dll32 != null && File.Exists(dll32) && Is64BitOs;
        if (File.Exists(t64))
        {
            if (!SameFile(t64, dll64)) throw new InvalidOperationException($"A different file named {file} already exists in System32 (another layout uses this name). Nothing was changed.");
            copy64 = false;
        }
        var t32 = Path.Combine(SysWow64, file);
        if (copy32 && File.Exists(t32))
        {
            if (!SameFile(t32, dll32!)) throw new InvalidOperationException($"A different file named {file} already exists in SysWOW64. Nothing was changed.");
            copy32 = false;
        }
        var layoutId = ChooseLayoutId(l.LayoutId);
        // Stage in the user's temp folder: the elevated session may not see the user's mapped drives.
        var stage = Path.Combine(Path.GetTempPath(), "wmt-kbd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string? s64 = null, s32 = null;
            if (copy64) { s64 = Path.Combine(stage, "x64-" + file); File.Copy(dll64, s64); }
            if (copy32) { s32 = Path.Combine(stage, "x86-" + file); File.Copy(dll32!, s32); }
            var r = await elevated.RunPowerShellElevatedAsync(DllInstallScript(l, s64, s32, layoutId), ct);
            if (r.StartFailed) throw new InvalidOperationException(r.StartError ?? "Could not start PowerShell as administrator.");
            if (r.ExitCode != 0) throw new InvalidOperationException("Installing the layout failed: " + (r.StdOut + r.StdErr).Trim());
        }
        finally { try { Directory.Delete(stage, true); } catch { } }
        if (!IsInstalled(l)) throw new InvalidOperationException("The layout was copied but its registration is not visible yet.");
        return "installed from its DLL" + (layoutId != (l.LayoutId ?? "").ToLowerInvariant() ? $" (Layout Id {layoutId})" : "") + " — sign out and in (or restart) if it does not appear in Win+Space";
    }

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>PowerShell run as administrator: copies the DLL(s) and registers the KLID exactly as an MSKLC setup package does.</summary>
    public static string DllInstallScript(KeyboardLayoutInfo l, string? staged64, string? staged32, string layoutId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$sys = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { Join-Path $env:WINDIR 'Sysnative' } else { Join-Path $env:WINDIR 'System32' }");
        if (staged64 != null) sb.AppendLine($"Copy-Item -LiteralPath {Q(staged64)} -Destination (Join-Path $sys {Q(l.LayoutFile)})");
        if (staged32 != null) sb.AppendLine($"Copy-Item -LiteralPath {Q(staged32)} -Destination (Join-Path (Join-Path $env:WINDIR 'SysWOW64') {Q(l.LayoutFile)})");
        sb.AppendLine($"$k = {Q(@"HKLM:\" + KeyboardLayoutDiscovery.LayoutsKey + "\\" + l.Klid.ToLowerInvariant())}");
        sb.AppendLine("New-Item -Path $k -Force | Out-Null");
        void Prop(string name, string value) => sb.AppendLine($"New-ItemProperty -Path $k -Name {Q(name)} -Value {Q(value)} -PropertyType String -Force | Out-Null");
        Prop("Layout File", l.LayoutFile);
        Prop("Layout Text", string.IsNullOrWhiteSpace(l.LayoutText) ? l.KlcBaseName : l.LayoutText);
        Prop("Layout Id", layoutId);
        if (l.DisplayName != null && l.DisplayName.Contains(l.LayoutFile, StringComparison.OrdinalIgnoreCase)) Prop("Layout Display Name", l.DisplayName);
        if (!string.IsNullOrWhiteSpace(l.ComponentId)) Prop("Layout Component ID", l.ComponentId!);
        sb.AppendLine($"Write-Output ('Registered ' + {Q(l.Klid)} + ' -> ' + {Q(l.LayoutFile)})");
        return sb.ToString();
    }
}
