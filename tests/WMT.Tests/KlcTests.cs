using System.Runtime.InteropServices;
using System.Text;
using WMT.Core;
using Xunit;
using Xunit.Abstractions;

namespace WMT.Tests;

/// <summary>Keyboard layouts without an MSI: install from the saved DLL, rebuild from .klc with MSKLC, and .klc generation.</summary>
public class KlcAndDllInstallTests
{
    readonly ITestOutputHelper output;
    public KlcAndDllInstallTests(ITestOutputHelper output) { this.output = output; }

    const string Klid = "A000042B";
    const string K = KeyboardLayoutDiscovery.LayoutsKey;

    /// <summary>Fake UAC runner: records the script and "performs" the registration in the in-memory registry.</summary>
    sealed class RegisteringElevated : IElevatedRunner
    {
        readonly MemoryRegistry reg; readonly string klid;
        public RegisteringElevated(MemoryRegistry reg, string klid) { this.reg = reg; this.klid = klid; }
        public List<string> Scripts { get; } = new();
        public Task<ProcessResult> RunPowerShellElevatedAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            reg.Set("HKLM", K + "\\" + klid, "Layout File", "String", "x.dll", RegView.Reg64);
            return Task.FromResult(FakeProcess.Ok("Registered"));
        }
    }

    /// <summary>Fake kbdutool: writes "&lt;name&gt;.dll" into its working directory.</summary>
    sealed class FakeKbdUtool : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, string? workingDirectory = null,
            IDictionary<string, string?>? environment = null, Action<string>? onOutputLine = null)
        {
            var a = args.ToList();
            Calls.Add(Path.GetFileName(fileName) + " " + string.Join(' ', a.Take(a.Count - 1)));
            var name = KeyboardLayoutDiscovery.KlcName(a[^1])!;
            File.WriteAllText(Path.Combine(workingDirectory!, name + ".dll"), "built " + a[^2]);
            return Task.FromResult(FakeProcess.Ok("ok"));
        }
    }

    static (MemoryRegistry Reg, string Sys32, string Wow, string Docs) Machine(bool dll, bool klc)
    {
        var reg = new MemoryRegistry();
        reg.Set("HKLM", K + @"\" + Klid, "Layout File", "String", "kbdhycust.dll", RegView.Reg64);
        reg.Set("HKLM", K + @"\" + Klid, "Layout Text", "String", "Armenian O'Custom", RegView.Reg64);
        reg.Set("HKLM", K + @"\" + Klid, "Layout Id", "String", "00c0", RegView.Reg64);
        reg.Set("HKLM", K + @"\" + Klid, "Layout Display Name", "String", @"@%SystemRoot%\system32\kbdhycust.dll,-1000", RegView.Reg64);
        reg.Set("HKCU", @"Control Panel\International\User Profile", "Languages", "MultiString", new[] { "hy-AM" });
        reg.Set("HKCU", @"Control Panel\International\User Profile\hy-AM", "042B:A000042B", "DWord", 1);
        var sys32 = T.Temp("sys32"); var wow = T.Temp("wow"); var docs = T.Temp("docs");
        if (dll) { T.Write(Path.Combine(sys32, "kbdhycust.dll"), "dll64"); T.Write(Path.Combine(wow, "kbdhycust.dll"), "dll32"); }
        if (klc) File.WriteAllText(Path.Combine(docs, "armenian.klc"), KeyboardLayoutTests.FakeKlc, Encoding.Unicode);
        return (reg, sys32, wow, docs);
    }

    static LoadedPackage BackUp(MemoryRegistry reg, string sys32, string wow, string docs)
    {
        var snap = new KeyboardLayoutDiscovery(reg, p => File.Exists(p) ? ("Tigran", "1.0") : null, sys32, wow) { KlcGenerator = l => l.Dll64 != null ? KeyboardLayoutTests.FakeKlc : null }
            .Discover(new List<SoftwareItem>(), new[] { docs }, CancellationToken.None);
        foreach (var l in snap.Layouts) l.Selected = l.IsCustom;
        var ctx = T.Ctx(opts: new DiscoveryOptions { Keyboard = snap, IncludeRuntimes = false, IncludeWindowsSettings = false });
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new KeyboardLayoutProvider() }).Build(ctx);
        var r = T.Backup(plan, T.Temp("kdest"));
        Assert.Equal("Complete", r.Status);
        return LoadedPackage.Load(r.PackagePath);
    }

    [Fact] public async Task Dll_only_layout_is_installed_from_the_package_without_msklc()
    {
        var (reg, sys32, wow, docs) = Machine(dll: true, klc: false);
        var pkg = BackUp(reg, sys32, wow, docs);
        var layout = pkg.Keyboard!.Layouts.Single(l => l.IsCustom);
        var (d64, d32) = KeyboardRestore.DllsInPackage(pkg, layout);
        Assert.NotNull(d64); Assert.NotNull(d32);
        Assert.Contains(ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations()).Items, i => i.Key == "keyboard." + Klid && i.State == ReadinessState.Ready);

        var target = new MemoryRegistry();
        target.Set("HKLM", K + @"\00000409", "Layout File", "String", "kbdus.dll", RegView.Reg64);
        var elev = new RegisteringElevated(target, Klid);
        var newSys = T.Temp("newsys32"); var newWow = T.Temp("newwow");
        var kr = new KeyboardRestore(new FakeProcess(), target, elev) { System32 = newSys, SysWow64 = newWow, Is64BitOs = true };
        Assert.Equal(KeyboardInstallMethod.Dll, kr.Plan(pkg, layout).Method);
        Assert.Contains("System32 and SysWOW64", kr.Describe(pkg, layout));

        var result = await kr.InstallAsync(pkg, layout, CancellationToken.None);
        Assert.StartsWith("installed from its DLL", result);
        var script = Assert.Single(elev.Scripts);
        Assert.Contains("Copy-Item -LiteralPath", script);
        Assert.Contains("SysWOW64", script);
        Assert.Contains(@"Keyboard Layouts\a000042b'", script);
        Assert.Contains("'Layout File' -Value 'kbdhycust.dll'", script);
        Assert.Contains("'Layout Text' -Value 'Armenian O''Custom'", script); // quote escaped
        Assert.Contains("'Layout Id' -Value '00c0'", script);
        Assert.Contains("Layout Display Name", script);
        Assert.Equal(KeyboardInstallMethod.AlreadyInstalled, kr.Plan(pkg, layout).Method);
        Assert.Equal("already installed", await kr.InstallAsync(pkg, layout, CancellationToken.None));
        Assert.Single(elev.Scripts);
    }

    [Fact] public async Task Different_existing_dll_is_never_overwritten_and_same_dll_is_not_copied_again()
    {
        var (reg, sys32, wow, docs) = Machine(dll: true, klc: false);
        var pkg = BackUp(reg, sys32, wow, docs);
        var layout = pkg.Keyboard!.Layouts.Single(l => l.IsCustom);
        var target = new MemoryRegistry();
        var elev = new RegisteringElevated(target, Klid);
        var newSys = T.Temp("newsys32"); var newWow = T.Temp("newwow");
        T.Write(Path.Combine(newSys, "kbdhycust.dll"), "someone else's layout");
        var kr = new KeyboardRestore(new FakeProcess(), target, elev) { System32 = newSys, SysWow64 = newWow, Is64BitOs = true };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => kr.InstallAsync(pkg, layout, CancellationToken.None));
        Assert.Contains("already exists in System32", ex.Message);
        Assert.Empty(elev.Scripts);
        Assert.Equal("someone else's layout", File.ReadAllText(Path.Combine(newSys, "kbdhycust.dll")));

        T.Write(Path.Combine(newSys, "kbdhycust.dll"), "dll64"); // identical file already there (e.g. second attempt)
        await kr.InstallAsync(pkg, layout, CancellationToken.None);
        var script = Assert.Single(elev.Scripts);
        Assert.DoesNotContain("Join-Path $sys", script);
        Assert.Contains("SysWOW64", script);
    }

    [Fact] public void Layout_id_is_kept_when_free_and_renumbered_on_conflict()
    {
        var reg = new MemoryRegistry();
        reg.Set("HKLM", K + @"\00000409", "Layout File", "String", "kbdus.dll", RegView.Reg64);
        reg.Set("HKLM", K + @"\00010409", "Layout Id", "String", "00c0", RegView.Reg64);
        reg.Set("HKLM", K + @"\00020409", "Layout Id", "String", "00d4", RegView.Reg64);
        var kr = new KeyboardRestore(new FakeProcess(), reg, new RegisteringElevated(reg, Klid));
        Assert.Equal("0100", kr.ChooseLayoutId("0100"));
        Assert.Equal("00d5", kr.ChooseLayoutId("00c0"));
        Assert.Equal("00d5", kr.ChooseLayoutId(null));
    }

    [Fact] public async Task Klc_only_layout_is_rebuilt_with_msklc_kbdutool_then_installed()
    {
        var (reg, sys32, wow, docs) = Machine(dll: false, klc: true);
        var pkg = BackUp(reg, sys32, wow, docs);
        var layout = pkg.Keyboard!.Layouts.Single(l => l.IsCustom);
        Assert.Null(KeyboardRestore.DllsInPackage(pkg, layout).Dll64);
        Assert.NotNull(KeyboardRestore.KlcInPackage(pkg, layout));
        Assert.Contains(ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations()).Items, i => i.Key == "keyboard." + Klid && i.State == ReadinessState.NeedsReview);

        var target = new MemoryRegistry();
        var elev = new RegisteringElevated(target, Klid);
        var kbdutool = T.Write(Path.Combine(T.Temp("msklc"), "bin", "i386", "kbdutool.exe"), "tool");
        var proc = new FakeKbdUtool();
        var kr = new KeyboardRestore(proc, target, elev) { System32 = T.Temp("ns"), SysWow64 = T.Temp("nw"), Is64BitOs = true };
        Assert.Equal(KeyboardInstallMethod.None, kr.Plan(pkg, layout).Method);
        Assert.Contains("Install Microsoft Keyboard Layout Creator", kr.Describe(pkg, layout));

        kr.Msklc = Msklc.Inspect(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(kbdutool)))!);
        Assert.NotNull(kr.Msklc); Assert.True(kr.Msklc!.CanBuild);
        Assert.Equal(KeyboardInstallMethod.BuildFromKlc, kr.Plan(pkg, layout).Method);
        var result = await kr.InstallAsync(pkg, layout, CancellationToken.None);
        Assert.StartsWith("built with MSKLC; installed from its DLL", result);
        Assert.Equal(new[] { "kbdutool.exe -u -n -m", "kbdutool.exe -u -n -o" }, proc.Calls);
        var script = Assert.Single(elev.Scripts);
        Assert.Contains("SysWOW64", script);
        Assert.Single(Directory.GetFiles(pkg.Layout.Status, "kbdutool-*.log"));
    }

    [Fact] public void Msklc_is_found_through_environment_or_uninstall_entry()
    {
        var dir = T.Temp("MSKLC");
        T.Write(Path.Combine(dir, "MSKLC.exe"), "exe");
        var found = Msklc.Find(new[] { new SoftwareItem { Name = "Microsoft Keyboard Layout Creator 1.4", InstallLocation = dir } });
        if (Environment.GetEnvironmentVariable("WMT_MSKLC_DIR") == null)
        {
            Assert.NotNull(found);
            Assert.Equal(Path.Combine(dir, "MSKLC.exe"), found!.Exe);
            Assert.False(found.CanBuild);
        }
        Assert.Null(Msklc.Inspect(T.Temp("empty")));
    }

    // ---------------- real Windows keyboard DLLs ----------------

    static string Sys(string dll) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll);

    static KlcExporter.ExportResult ExportSystem(string dll, string klid) =>
        KlcExporter.Export(Sys(dll), Path.GetFileNameWithoutExtension(dll), "Test " + dll, klid, "Test company", "(c) test", "1.0");

    [Fact] public void Klc_from_kbdus_matches_the_us_layout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var r = ExportSystem("kbdus.dll", "00000409");
        output.WriteLine(r.Klc);
        Assert.StartsWith("KBD\tkbdus\t\"Test kbdus.dll\"", r.Klc);
        Assert.Contains("LOCALENAME\t\"en-US\"", r.Klc);
        Assert.Contains("\r\n1e\tA\t\t1\t0061\t0041\t", r.Klc);
        Assert.Contains("\r\n02\t1\t\t0\t0031\t0021\t", r.Klc);
        Assert.Contains("\r\n39\tSPACE\t\t0\t0020\t0020\t", r.Klc);
        Assert.Contains("KEYNAME\r\n", r.Klc);
        Assert.EndsWith("ENDKBD\r\n", r.Klc);
        Assert.True(r.KeyRows >= 47, $"rows {r.KeyRows}");
        Assert.Equal(0, r.DeadKeys);
    }

    [Fact] public void Klc_from_us_international_has_dead_keys_and_altgr()
    {
        if (!OperatingSystem.IsWindows()) return;
        var r = ExportSystem("kbdusx.dll", "00020409");
        output.WriteLine(r.Klc);
        Assert.True(r.DeadKeys > 20, $"dead keys {r.DeadKeys}");
        Assert.Contains("DEADKEY\t", r.Klc);
        Assert.Contains("@\t", r.Klc);
        Assert.Contains("KEYNAME_DEAD", r.Klc);
        Assert.True(r.ShiftStates >= 5, $"shift states {r.ShiftStates}");
    }

    [Fact] public void Klc_from_swiss_german_and_french_and_armenian()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sg = ExportSystem("kbdsg.dll", "00000807");
        output.WriteLine(sg.Klc);
        Assert.Contains("SGCap", sg.Klc);
        var fr = ExportSystem("kbdfr.dll", "0000040c");
        Assert.Contains("\r\n10\tA\t\t1\t0061\t0041\t", fr.Klc); // AZERTY
        if (File.Exists(Sys("kbdarme.dll")))
        {
            var hy = ExportSystem("kbdarme.dll", "0000042b");
            output.WriteLine(hy.Klc);
            Assert.Contains("LOCALENAME\t\"hy-AM\"", hy.Klc);
            Assert.Matches(@"\r\n[0-9a-f]{2}\t\w+\t\t\d\t05[3-8][0-9a-f]", hy.Klc); // Armenian letters U+0530–U+058F
        }
    }

    /// <summary>The strongest check of the generated .klc: MSKLC's own compiler builds it, and the built DLL exports to the same .klc.
    /// Runs only where MSKLC is available (CI extracts it and sets WMT_MSKLC_DIR).</summary>
    [Fact] public async Task Generated_klc_round_trips_through_msklc_compiler()
    {
        if (!OperatingSystem.IsWindows()) return;
        var m = Msklc.Find();
        if (m?.CanBuild != true) { output.WriteLine("MSKLC not available: skipped"); return; }
        var failures = new List<string>();
        foreach (var (dll, klid) in new[] { ("kbdus.dll", "00000409"), ("kbdusx.dll", "00020409"), ("kbdsg.dll", "00000807"), ("kbdfr.dll", "0000040c"), ("kbdgr.dll", "00000407"), ("kbdarme.dll", "0000042b"), ("kbdru.dll", "00000419") })
        {
            if (!File.Exists(Sys(dll))) continue;
            var original = ExportSystem(dll, klid);
            var dir = T.Temp("rt-" + dll);
            var klcPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(dll) + ".klc");
            Msklc.WriteKlc(klcPath, original.Klc);
            var b = await Msklc.BuildAsync(new ProcessRunner(), m, klcPath, Path.Combine(dir, "out"), CancellationToken.None);
            output.WriteLine($"{dll}: {b.Output}");
            if (b.Dll64 == null) { failures.Add($"{dll}: kbdutool did not build: {b.Output}"); continue; }
            if (b.DllWow64 == null) failures.Add($"{dll}: no WOW64 DLL");
            var rebuilt = KlcExporter.Export(b.Dll64, Path.GetFileNameWithoutExtension(dll), "Test " + dll, klid, "Test company", "(c) test", "1.0");
            if (rebuilt.Klc != original.Klc)
            {
                var a = original.Klc.Split("\r\n"); var c = rebuilt.Klc.Split("\r\n");
                var diff = Enumerable.Range(0, Math.Max(a.Length, c.Length)).Where(i => i >= a.Length || i >= c.Length || a[i] != c[i]).Take(5)
                    .Select(i => $"line {i}: '{(i < a.Length ? a[i] : "")}' vs '{(i < c.Length ? c[i] : "")}'");
                failures.Add($"{dll}: round trip differs: {string.Join(" | ", diff)}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayoutW(string klid, uint flags);
    [DllImport("user32.dll")] static extern bool UnloadKeyboardLayout(IntPtr hkl);

    /// <summary>Real install into System32 + registry on the (already elevated) CI machine, then full cleanup.
    /// Runs only when WMT_ADMIN_TESTS=1 so it never touches a developer's computer.</summary>
    [Fact] public async Task Real_dll_install_registers_a_working_layout()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WMT_ADMIN_TESTS") != "1") { output.WriteLine("skipped"); return; }
        const string klid = "A0FF0409", file = "kbdwmtt.dll";
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        var src = T.Temp("realdll");
        File.Copy(Sys("kbdus.dll"), Path.Combine(src, file));
        if (File.Exists(Path.Combine(wow, "kbdus.dll"))) { Directory.CreateDirectory(Path.Combine(src, "wow")); File.Copy(Path.Combine(wow, "kbdus.dll"), Path.Combine(src, "wow", file)); }
        var reg = new WindowsRegistry();
        var layout = new KeyboardLayoutInfo { Klid = klid, LayoutFile = file, LayoutText = "WMT test layout", IsCustom = true };
        var kr = new KeyboardRestore(new ProcessRunner(), reg, new ElevatedPowerShell { RunAs = false });
        var hkl = IntPtr.Zero;
        try
        {
            Assert.False(kr.IsInstalled(layout));
            var result = await kr.InstallDllAsync(Path.Combine(src, file), File.Exists(Path.Combine(src, "wow", file)) ? Path.Combine(src, "wow", file) : null, layout, CancellationToken.None);
            output.WriteLine(result);
            Assert.StartsWith("installed from its DLL", result);
            Assert.True(kr.IsInstalled(layout));
            Assert.True(File.Exists(Path.Combine(sys32, file)));
            Assert.Equal(file, reg.Value("HKLM", K + "\\" + klid, "Layout File", RegView.Reg64)?.AsString());
            Assert.NotNull(reg.Value("HKLM", K + "\\" + klid, "Layout Id", RegView.Reg64));
            hkl = LoadKeyboardLayoutW(klid, 0);
            Assert.NotEqual(IntPtr.Zero, hkl);
            var exported = KlcExporter.Export(Path.Combine(sys32, file), "kbdwmtt", "WMT test layout", klid);
            Assert.Contains("\r\n1e\tA\t\t1\t0061\t0041\t", exported.Klc);
        }
        finally
        {
            if (hkl != IntPtr.Zero) UnloadKeyboardLayout(hkl);
            var cleanup = $"Remove-Item -Path 'HKLM:\\{K}\\{klid.ToLowerInvariant()}' -Recurse -Force -ErrorAction SilentlyContinue\n" +
                          $"Remove-Item -LiteralPath '{Path.Combine(sys32, file)}' -Force -ErrorAction SilentlyContinue\n" +
                          $"Remove-Item -LiteralPath '{Path.Combine(wow, file)}' -Force -ErrorAction SilentlyContinue";
            await new ElevatedPowerShell { RunAs = false }.RunPowerShellElevatedAsync(cleanup, CancellationToken.None);
        }
        Assert.False(kr.IsInstalled(layout));
    }
}
