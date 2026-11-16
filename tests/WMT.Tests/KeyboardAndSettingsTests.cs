using System.Text;
using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class KeyboardLayoutTests
{
    const string Klid = "A000042B";
    const string ProductCode = "{11111111-2222-3333-4444-555555555555}";

    sealed record Env(MemoryRegistry Reg, string Sys32, string SysWow, string Search, string CachedMsi, List<SoftwareItem> Software);

    static Env Make(bool withSources = true, bool withCache = true)
    {
        var reg = new MemoryRegistry();
        const string k = KeyboardLayoutDiscovery.LayoutsKey;
        reg.Set("HKLM", k + @"\00000409", "Layout File", "String", "kbdus.dll", RegView.Reg64);
        reg.Set("HKLM", k + @"\00000409", "Layout Text", "String", "US", RegView.Reg64);
        reg.Set("HKLM", k + @"\00000419", "Layout File", "String", "kbdru.dll", RegView.Reg64);
        reg.Set("HKLM", k + @"\00000419", "Layout Text", "String", "Russian", RegView.Reg64);
        reg.Set("HKLM", k + @"\" + Klid, "Layout File", "String", "kbdhycust.dll", RegView.Reg64);
        reg.Set("HKLM", k + @"\" + Klid, "Layout Text", "String", "Armenian Custom", RegView.Reg64);
        const string up = @"Control Panel\International\User Profile";
        reg.Set("HKCU", up, "Languages", "MultiString", new[] { "en-US", "hy-AM" });
        reg.Set("HKCU", up + @"\en-US", "0409:00000409", "DWord", 1);
        reg.Set("HKCU", up + @"\hy-AM", "042B:A000042B", "DWord", 1);
        var sys32 = T.Temp("sys32"); var wow = T.Temp("wow");
        foreach (var d in new[] { "kbdus.dll", "kbdru.dll", "kbdhycust.dll" }) { T.Write(Path.Combine(sys32, d), d); T.Write(Path.Combine(wow, d), d); }
        var cache = T.Write(Path.Combine(T.Temp("installer"), "1a2b3c.msi"), "cached msi");
        if (withCache)
        {
            var ip = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\ABCDEF\InstallProperties";
            reg.Set("HKLM", ip, "DisplayName", "String", "Armenian Custom", RegView.Reg64);
            reg.Set("HKLM", ip, "UninstallString", "String", "MsiExec.exe /X" + ProductCode, RegView.Reg64);
            reg.Set("HKLM", ip, "LocalPackage", "String", cache, RegView.Reg64);
        }
        var search = T.Temp("docs");
        if (withSources)
        {
            File.WriteAllText(Path.Combine(search, "my layout.klc"), "KBD\tkbdhycust\t\"Armenian Custom\"\r\n\r\nCOPYRIGHT\t\"(c) me\"\r\n", Encoding.Unicode);
            var setup = Path.Combine(search, "Build", "kbdhycust");
            T.Write(Path.Combine(setup, "setup.exe")); T.Write(Path.Combine(setup, "kbdhycust_amd64.msi"), "msi");
            T.Write(Path.Combine(setup, "amd64", "kbdhycust.dll"));
        }
        var sw = new List<SoftwareItem> { new() { Name = "Armenian Custom", InstallationType = "MSI", UninstallKey = ProductCode } };
        return new Env(reg, sys32, wow, search, cache, sw);
    }

    internal const string FakeKlc = "KBD\tkbdhycust\t\"Armenian Custom\"\r\n\r\nENDKBD\r\n";

    static KeyboardSnapshot Discover(Env e) =>
        new KeyboardLayoutDiscovery(e.Reg, p => File.Exists(p) ? (Path.GetFileName(p).StartsWith("kbdhy") ? "Tigran" : "Microsoft Corporation", "1.0") : null, e.Sys32, e.SysWow)
        { KlcGenerator = l => l.Dll64 != null ? FakeKlc : null }
            .Discover(e.Software, new[] { e.Search }, CancellationToken.None);

    [Fact] public void Custom_layout_detected_with_every_reinstall_source()
    {
        var e = Make();
        var s = Discover(e);
        var c = s.Layouts.Single(l => l.IsCustom);
        Assert.Equal(Klid, c.Klid); Assert.Equal("hy-AM", c.Language); Assert.True(c.EnabledForCurrentUser);
        Assert.Equal(ProductCode, c.ProductCode); Assert.Equal(e.CachedMsi, c.CachedMsi);
        Assert.Single(c.KlcSources); Assert.Single(c.SetupPackages);
        Assert.Equal(ReadinessState.Ready, c.BackupState);
        Assert.Contains("Tigran", c.Evidence);
        Assert.Contains(s.Layouts, l => l.Klid == "00000409" && !l.IsCustom);
        Assert.DoesNotContain(s.Layouts, l => l.Klid == "00000419");
        Assert.Equal(new[] { "en-US", "hy-AM" }, s.Input!.Languages.Select(l => l.Tag));
    }

    [Fact] public void Dll_only_layout_is_ready_and_gets_a_generated_klc()
    {
        var s = Discover(Make(withSources: false, withCache: false));
        var c = s.Layouts.Single(l => l.IsCustom);
        Assert.Equal(ReadinessState.Ready, c.BackupState);
        Assert.StartsWith("Layout DLL (WMT installs it directly) + generated .klc", c.BackupSource);
        Assert.Equal(FakeKlc, c.GeneratedKlc);
        Assert.Null(s.Layouts.Single(l => !l.IsCustom).GeneratedKlc);
    }

    [Fact] public void Layout_without_dll_installer_or_klc_is_a_blocker_with_msklc_instructions()
    {
        var e = Make(withSources: false, withCache: false);
        File.Delete(Path.Combine(e.Sys32, "kbdhycust.dll"));
        var s = Discover(e);
        var c = s.Layouts.Single(l => l.IsCustom);
        Assert.Equal(ReadinessState.Blocked, c.BackupState);
        Assert.Null(c.GeneratedKlc);
        Assert.Contains(s.Issues, i => i.RequiredAction!.Contains("Load Existing Keyboard"));
    }

    [Fact] public void Klc_generator_failure_is_reported_not_fatal()
    {
        var e = Make(withSources: false, withCache: false);
        var s = new KeyboardLayoutDiscovery(e.Reg, p => File.Exists(p) ? ("Tigran", "1.0") : null, e.Sys32, e.SysWow) { KlcGenerator = _ => throw new InvalidDataException("bad tables") }
            .Discover(e.Software, new[] { e.Search }, CancellationToken.None);
        var c = s.Layouts.Single(l => l.IsCustom);
        Assert.Equal("bad tables", c.GeneratedKlcError);
        Assert.Contains(s.Issues, i => i.Message.Contains("bad tables"));
    }

    [Fact] public void Klc_name_and_language_script()
    {
        var e = Make();
        Assert.Equal("kbdhycust", KeyboardLayoutDiscovery.KlcName(Path.Combine(e.Search, "my layout.klc")));
        var langs = Discover(e).Input!.Languages;
        var all = KeyboardLayoutDiscovery.LanguageListScript(langs, _ => true);
        Assert.Contains("Set-WinUserLanguageList", all); Assert.Contains("042B:A000042B", all); Assert.Contains("'hy-AM'", all);
        Assert.DoesNotContain("A000042B", KeyboardLayoutDiscovery.LanguageListScript(langs, t => !t.Contains("A000042B")));
    }

    [Fact] public void Backup_contains_installer_and_restore_uses_msiexec()
    {
        var e = Make();
        var snap = Discover(e);
        foreach (var l in snap.Layouts) l.Selected = l.IsCustom;
        var ctx = T.Ctx(opts: new DiscoveryOptions { Keyboard = snap, IncludeRuntimes = false, IncludeWindowsSettings = false });
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new KeyboardLayoutProvider() }).Build(ctx);
        var names = plan.Index.Files.Select(f => Path.GetFileName(f.SourcePath)).ToList();
        Assert.Contains("1a2b3c.msi", names); Assert.Contains("my layout.klc", names); Assert.Contains("kbdhycust_amd64.msi", names); Assert.Contains("setup.exe", names);
        Assert.Equal(3, names.Count(n => n == "kbdhycust.dll")); // System32 + SysWOW64 + setup\amd64
        Assert.DoesNotContain("kbdus.dll", names);
        var r = T.Backup(plan, T.Temp("kdest"));
        Assert.Equal("Complete", r.Status);
        var pkg = LoadedPackage.Load(r.PackagePath);
        var layout = pkg.Keyboard!.Layouts.Single(l => l.IsCustom);
        var inst = KeyboardRestore.InstallerInPackage(pkg, layout);
        Assert.NotNull(inst); Assert.EndsWith("kbdhycust_amd64.msi", inst);
        Assert.Contains(ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations()).Items, i => i.Key == "keyboard." + Klid && i.State == ReadinessState.Ready);

        var proc = new FakeProcess { Handler = (_, _) => FakeProcess.Ok("") };
        var kr = new KeyboardRestore(proc, new MemoryRegistry());
        Assert.Contains("msiexec /i", kr.Describe(pkg, layout));
        Assert.Equal(KeyboardInstallMethod.Msi, kr.Plan(pkg, layout).Method);
        var klcFile = Path.Combine(pkg.Layout.Root, "Reports", "Keyboard", "kbdhycust.klc");
        Assert.True(File.Exists(klcFile));
        Assert.Equal(FakeKlc, File.ReadAllText(klcFile));
        Assert.Equal(0xFE, File.ReadAllBytes(klcFile)[1]); // UTF-16 LE BOM like MSKLC
        Assert.Equal("installed", kr.InstallAsync(pkg, layout, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains(proc.Calls, c => c.StartsWith("msiexec /i") && c.Contains("kbdhycust_amd64.msi"));
        var installed = new MemoryRegistry(); installed.AddKey("HKLM", KeyboardLayoutDiscovery.LayoutsKey + "\\" + Klid, RegView.Reg64);
        Assert.Equal("Already installed on this computer", new KeyboardRestore(proc, installed).Describe(pkg, layout));
    }
}

public class WindowsSettingsTests
{
    const string WifiXml = "<?xml version=\"1.0\"?><WLANProfile><name>Home</name><MSM><security><authEncryption><authentication>WPA2PSK</authentication></authEncryption>" +
                           "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>SuperSecret123</keyMaterial></sharedKey></security></MSM></WLANProfile>";

    static (WindowsSettingsSnapshot Snap, FakeProcess Proc, string Font, string Wallpaper) Capture()
    {
        var reg = new MemoryRegistry();
        reg.Set("HKCU", @"Control Panel\International", "sShortDate", "String", "dd.MM.yyyy");
        reg.Set("HKCU", @"Control Panel\International", "LocaleName", "String", "hy-AM");
        reg.Set("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", "DWord", 0);
        reg.Set("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "SomethingElse", "DWord", 7);
        reg.Set("HKCU", "Environment", "MY_TOOL", "String", @"C:\Tools");
        reg.Set("HKCU", "Environment", "API_TOKEN", "String", "secret-token");
        reg.Set("HKCU", "Environment", "Path", "ExpandString", @"C:\Users\old\bin;C:\Tools");
        reg.Set("HKCU", @"Network\z", "RemotePath", "String", @"\\srv\share");
        reg.AddKey("HKCU", @"Printers\Connections\,,srv,HP1");
        reg.Set("HKLM", @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "TimeZoneKeyName", "String", "Caucasus Standard Time", RegView.Reg64);
        reg.Set("HKLM", @"SYSTEM\CurrentControlSet\Control\Nls\Language", "Default", "String", "042b", RegView.Reg64);
        var font = T.Write(Path.Combine(T.Temp("fonts"), "MyArmenian.ttf"), "font");
        reg.Set("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Fonts", "My Armenian (TrueType)", "String", font);
        var wp = T.Write(Path.Combine(T.Temp("pics"), "mountain.jpg"), "jpg");
        reg.Set("HKCU", @"Control Panel\Desktop", "Wallpaper", "String", wp);
        var proc = new FakeProcess
        {
            Handler = (exe, a) =>
            {
                if (exe == "netsh")
                {
                    var folder = a.First(x => x.StartsWith("folder=")).Substring(7);
                    File.WriteAllText(Path.Combine(folder, "Wi-Fi-Home.xml"), WifiXml);
                    return FakeProcess.Ok("exported");
                }
                if (exe == "powercfg") return FakeProcess.Ok("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)");
                if (exe == "powershell")
                    return FakeProcess.Ok("[{\"Name\":\"GoogleUpdateTaskMachineUA\",\"Path\":\"\\\\\",\"Author\":\"Google LLC\",\"State\":\"Ready\",\"Xml\":\"<Task/>\"}," +
                                          "{\"Name\":\"Backup docs\",\"Path\":\"\\\\\",\"Author\":\"PC\\\\tester\",\"State\":\"Ready\",\"Xml\":\"<Task/>\"}]");
                return FakeProcess.Fail(1, "unexpected");
            }
        };
        var env = new SettingsCaptureEnvironment
        {
            Registry = reg, Processes = proc, WindowsDir = T.Temp("win"), ProfilePath = @"C:\Users\old", Account = @"PC\tester", RoamingAppData = T.Temp("roam"), LocalAppData = T.Temp("local"),
            ReadText = p => p.EndsWith("hosts") ? "127.0.0.1 localhost\n10.0.0.5 nas.local\n# comment\n" : null
        };
        return (new WindowsSettingsCapture(env).Capture(), proc, font, wp);
    }

    [Fact] public void Captures_all_four_groups_without_secrets()
    {
        var (s, _, font, wp) = Capture();
        SettingItem I(string kind, string? name = null) => s.Items.Single(i => i.Kind == kind && (name == null || i.Name.Contains(name)));
        Assert.Contains(I(SettingKinds.Registry, "Regional format").Values, v => v.ValueName == "sShortDate" && v.Data == "dd.MM.yyyy");
        var exp = I(SettingKinds.Registry, "File Explorer");
        Assert.Single(exp.Values); Assert.Equal("0", exp.Values[0].Data);
        Assert.Equal("Caucasus Standard Time", I(SettingKinds.TimeZone).Parameter);
        Assert.Equal("hy-AM", I(SettingKinds.SystemLocale).Parameter);
        var wifi = I(SettingKinds.WiFi);
        Assert.Equal("Home", wifi.Parameter);
        Assert.DoesNotContain("SuperSecret123", wifi.Payload); Assert.DoesNotContain("keyMaterial", wifi.Payload);
        Assert.Equal(@"\\srv\share", I(SettingKinds.MappedDrive).Entries[0]);
        Assert.Equal(@"\\srv\HP1", I(SettingKinds.Printer).Parameter);
        Assert.Equal(2, I(SettingKinds.Hosts).Entries.Count);
        var power = I(SettingKinds.PowerPlan); Assert.Null(power.Payload); Assert.False(power.RequiresAdmin);
        var google = I(SettingKinds.ScheduledTask, "Google"); Assert.Equal(Classification.InstallerManaged, google.Classification); Assert.False(google.Selected);
        var mine = I(SettingKinds.ScheduledTask, "Backup docs"); Assert.Equal(Classification.Recommended, mine.Classification); Assert.True(mine.Selected);
        var env = I(SettingKinds.Registry, "environment variables");
        Assert.Contains(env.Values, v => v.ValueName == "MY_TOOL"); Assert.DoesNotContain(env.Values, v => v.ValueName is "API_TOKEN" or "Path");
        Assert.Equal(new[] { @"C:\Users\old\bin", @"C:\Tools" }, I(SettingKinds.PathMerge).Entries);
        Assert.Equal(font, I(SettingKinds.Font).SourcePath);
        Assert.Equal(wp, I(SettingKinds.Wallpaper).SourcePath);
        Assert.All(s.Items.Where(i => i.Classification == Classification.Recommended), i => Assert.True(i.Selected));
    }

    [Fact] public void Wifi_key_material_is_always_stripped() =>
        Assert.DoesNotContain("SuperSecret123", WindowsSettingsCapture.StripWifiKey(WifiXml));

    sealed class FakeElevated : IElevatedRunner
    {
        public List<string> Scripts { get; } = new();
        public Task<ProcessResult> RunPowerShellElevatedAsync(string script, CancellationToken ct) { Scripts.Add(script); return Task.FromResult(FakeProcess.Ok("")); }
    }

    [Fact] public async Task Restore_dry_run_apply_and_undo()
    {
        var (snap, _, font, _) = Capture();
        var src = Path.GetDirectoryName(font)!;
        var pkgPath = T.Backup(T.PlanFor(src), T.Temp("sdest")).PackagePath;
        var pkg = LoadedPackage.Load(pkgPath);
        var target = new MemoryRegistry();
        target.Set("HKCU", "Environment", "Path", "ExpandString", @"C:\Tools");
        target.Set("HKCU", @"Control Panel\International", "sShortDate", "String", "M/d/yyyy");
        var proc = new FakeProcess { Handler = (_, _) => FakeProcess.Ok("") };
        var elev = new FakeElevated();
        var engine = new SettingsRestoreEngine(T.Log(), target, target, proc, elev) { SetWallpaper = _ => { } };
        var local = T.Temp("newlocal");
        var req = new SettingsRestoreRequest { Package = pkg, Snapshot = snap, TargetProfilePath = @"C:\Users\new", TargetLocalAppData = local };

        var dry = engine.DryRun(req);
        var pathAction = dry.Single(a => a.Kind == SettingKinds.PathMerge);
        Assert.Contains(@"C:\Users\new\bin", pathAction.Description); Assert.DoesNotContain(@"C:\Tools", pathAction.Description);
        Assert.False(dry.Single(a => a.Name.Contains("Regional")).Blocked);
        Assert.False(dry.Single(a => a.Kind == SettingKinds.Font).Blocked);
        Assert.True(dry.Single(a => a.Kind == SettingKinds.Wallpaper).Blocked);
        Assert.False(dry.Single(a => a.Kind == SettingKinds.SystemLocale).Blocked);
        target.Set("HKLM", @"SYSTEM\CurrentControlSet\Control\Nls\Language", "Default", "String", "042b", RegView.Reg64);
        target.Set("HKLM", @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "TimeZoneKeyName", "String", "Caucasus Standard Time", RegView.Reg64);
        var dry2 = engine.DryRun(req);
        Assert.True(dry2.Single(a => a.Kind == SettingKinds.SystemLocale).Blocked);
        Assert.True(dry2.Single(a => a.Kind == SettingKinds.TimeZone).Blocked);
        Assert.Equal(@"C:\Tools", target.Value("HKCU", "Environment", "Path")!.AsString());

        var chosen = dry.Where(a => a.Kind is SettingKinds.PathMerge or SettingKinds.Registry or SettingKinds.MappedDrive or SettingKinds.WiFi or SettingKinds.Font or SettingKinds.Hosts).ToList();
        foreach (var a in chosen) a.Selected = !a.Blocked;
        var j = await engine.ExecuteAsync(req, chosen, null, CancellationToken.None);
        Assert.True(j.Failed == 0, string.Join("\n", j.Errors));
        Assert.Equal(@"C:\Tools;C:\Users\new\bin", target.Value("HKCU", "Environment", "Path")!.AsString());
        Assert.Equal("dd.MM.yyyy", target.Value("HKCU", @"Control Panel\International", "sShortDate")!.AsString());
        Assert.Equal(@"\\srv\share", target.Value("HKCU", @"Network\Z", "RemotePath")!.AsString());
        Assert.Contains(proc.Calls, c => c.StartsWith("netsh wlan add profile"));
        Assert.Contains(elev.Scripts, s => s.Contains("nas.local"));
        var installedFont = Path.Combine(local, @"Microsoft\Windows\Fonts\MyArmenian.ttf");
        Assert.True(File.Exists(installedFont));
        Assert.Equal(installedFont, target.Value("HKCU", @"Software\Microsoft\Windows NT\CurrentVersion\Fonts", "My Armenian (TrueType)")!.AsString());

        var jpath = Path.Combine(pkg.Layout.Status, "Settings-" + j.JournalId, "journal.json");
        var problems = new RestoreEngine(T.Log(), target, target).Undo(j, jpath);
        Assert.Equal(@"C:\Tools", target.Value("HKCU", "Environment", "Path")!.AsString());
        Assert.Equal("M/d/yyyy", target.Value("HKCU", @"Control Panel\International", "sShortDate")!.AsString());
        Assert.Null(target.Value("HKCU", @"Network\Z", "RemotePath"));
        Assert.False(File.Exists(installedFont));
        Assert.Contains(problems, p => p.Contains("Wi-Fi"));
    }
}
