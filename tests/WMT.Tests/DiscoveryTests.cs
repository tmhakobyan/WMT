using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class SoftwareDiscoveryTests
{
    static SoftwareScanResult Scan(FakeUninstall f) => new SoftwareDiscovery(f, null, Array.Empty<string>()).Scan(null, CancellationToken.None);

    [Fact] public void Zero_one_many()
    {
        Assert.Empty(Scan(new FakeUninstall()).Items);
        Assert.Single(Scan(new FakeUninstall().Add("7-Zip 23.01")).Items);
        var f = new FakeUninstall(); for (int i = 0; i < 300; i++) f.Add("App " + i);
        Assert.Equal(300, Scan(f).Items.Count);
    }

    [Fact] public void System_components_updates_children_and_blank_names_are_hidden()
    {
        var r = Scan(new FakeUninstall().Add("Hidden", sys: true).Add("Patch", parent: "X").Add("KB5030211").Add(null).Add("Security Update for Foo").Add("Real"));
        Assert.Single(r.Items);
        Assert.Equal(5, r.FilteredCount);
    }

    [Fact] public void Duplicates_merged_but_other_architecture_kept()
    {
        var r = Scan(new FakeUninstall().Add("App", arch: "x64").Add("App", arch: "x64", source: "HKCU (current user)").Add("App", arch: "x86"));
        Assert.Equal(2, r.Items.Count); Assert.Equal(1, r.DuplicateCount);
    }

    [Fact] public void Unknown_values_are_the_word_Unknown_never_guessed()
    {
        var it = Assert.Single(Scan(new FakeUninstall().Add("NoMeta", ver: null, publisher: null, arch: SoftwareItem.UnknownText, perUser: true)).Items);
        Assert.Equal("Unknown", it.Version); Assert.Equal("Unknown", it.Publisher); Assert.Equal("Unknown", it.Architecture); Assert.Equal("Unknown", it.Build);
        Assert.Equal(Confidence.Inferred, it.Confidence); Assert.Equal("Per-user", it.InstallationType);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "Runtime")]
    [InlineData("Microsoft .NET Runtime - 8.0.1 (x64)", "Runtime")]
    [InlineData("Microsoft Office Professional Plus 2016", "Office")]
    [InlineData("Python 3.11.9 (64-bit)", "Python")]
    [InlineData("Tesseract-OCR - open source OCR engine", "OCR")]
    [InlineData("7-Zip 23.01 (x64)", "Application")]
    public void Classification(string name, string cat) => Assert.Equal(cat, SoftwareDiscovery.Classify(name));

    [Fact] public void Office_and_Python_get_providers_and_runtime_gets_reinstall_not_copy()
    {
        var r = Scan(new FakeUninstall().Add("Microsoft Office Professional Plus 2016", "16.0.4266.1001").Add("Python 3.11.9 (64-bit)", "3.11.9150.0").Add("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "14.38.33135.0"));
        Assert.Contains(WordProvider.ProviderId, r.Items.Single(i => i.Category == "Office").Providers);
        Assert.Contains(PythonProvider.ProviderId, r.Items.Single(i => i.Category == "Python").Providers);
        var rt = r.Items.Single(i => i.Category == "Runtime");
        Assert.Contains("no DLL copy", rt.RecoveryMethod);
        Assert.StartsWith("https://learn.microsoft.com/", rt.OfficialSource);
    }

    [Fact] public void Portable_apps_detected_in_portable_roots()
    {
        var root = T.Temp("portable");
        var app = Directory.CreateDirectory(Path.Combine(root, "MyTool"));
        File.Copy(Environment.ProcessPath!, Path.Combine(app.FullName, "MyTool.exe"));
        var r = new SoftwareDiscovery(new FakeUninstall(), null, new[] { root }).Scan(null, CancellationToken.None);
        var p = Assert.Single(r.Items);
        Assert.Equal("Portable", p.InstallationType);
        Assert.Contains(PortableProvider.ProviderId, p.Providers);
    }

    [Fact] public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var f = new FakeUninstall(); for (int i = 0; i < 10; i++) f.Add("A" + i);
        Assert.Throws<OperationCanceledException>(() => new SoftwareDiscovery(f, null, Array.Empty<string>()).Scan(null, cts.Token));
    }

    [Fact] public void Windows_uninstall_source_reads_32_64_and_per_user_from_registry()
    {
        var reg = new MemoryRegistry();
        reg.Set("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\A", "DisplayName", "String", "App64", RegView.Reg64);
        reg.Set("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\B", "DisplayName", "String", "App32", RegView.Reg32);
        reg.Set("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\C", "DisplayName", "String", "UserApp");
        reg.Set("HKU", @"S-1-5-21-9-9-9-1005\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\D", "DisplayName", "String", "OtherUserApp");
        var (recs, _) = new WindowsUninstallSource(reg, () => "S-1-5-21-1-2-3-1001").Read(CancellationToken.None);
        Assert.Contains(recs, r => r.DisplayName == "App64" && r.Architecture == "x64");
        Assert.Contains(recs, r => r.DisplayName == "App32" && r.Architecture == "x86");
        Assert.Contains(recs, r => r.DisplayName == "UserApp" && r.PerUser);
        Assert.Contains(recs, r => r.DisplayName == "OtherUserApp" && r.Source.Contains("S-1-5-21-9-9-9-1005"));
    }
}

public class ProfileDiscoveryTests
{
    const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    static MemoryRegistry Reg(string alice, string bob)
    {
        var r = new MemoryRegistry();
        r.Set("HKLM", Key + @"\S-1-5-18", "ProfileImagePath", "String", @"C:\Windows\system32\config\systemprofile", RegView.Reg64);
        r.Set("HKLM", Key + @"\S-1-5-21-1-2-3-1001", "ProfileImagePath", "String", alice, RegView.Reg64);
        r.Set("HKLM", Key + @"\S-1-5-21-1-2-3-1002", "ProfileImagePath", "String", bob, RegView.Reg64);
        r.Set("HKLM", Key + @"\S-1-12-1-5-6-7-8", "ProfileImagePath", "String", @"C:\Users\entra", RegView.Reg64);
        r.AddKey("HKU", "S-1-5-21-1-2-3-1001");
        return r;
    }
    sealed class Resolver : IAccountResolver { public string? Resolve(string sid) => sid.EndsWith("1001") ? @"PC\alice" : null; }

    [Fact] public void Real_profiles_only_and_never_preselected()
    {
        var alice = T.Temp("alice"); var bob = Path.Combine(T.Temp(), "missing-bob");
        var r = new ProfileDiscovery(Reg(alice, bob), new Resolver(), null, "S-1-5-21-1-2-3-1001").Scan(null, CancellationToken.None);
        Assert.Equal(3, r.Items.Count); Assert.Equal(1, r.ExcludedSystemCount);
        Assert.All(r.Items, p => Assert.False(p.Selected));
        var a = r.Items.Single(p => p.Account == @"PC\alice");
        Assert.True(a.IsCurrentUser); Assert.True(a.Loaded); Assert.Equal(AccessState.Accessible, a.Access);
        var b = r.Items.Single(p => p.Sid.EndsWith("1002"));
        Assert.Equal(AccessState.Missing, b.Access); Assert.Contains("missing-bob", b.Account); Assert.False(b.Loaded);
    }

    [Fact] public void Inaccessible_profile_reported_not_bypassed()
    {
        var r = new ProfileDiscovery(Reg("C:\\a", "C:\\b"), new Resolver(), _ => AccessState.Inaccessible, "x").Scan(null, CancellationToken.None);
        Assert.All(r.Items, p => { Assert.Equal(AccessState.Inaccessible, p.Access); Assert.Contains("never bypasses", p.AccessNote); });
    }
}

public class OfficeTests
{
    static OfficeEnvironment O(string fam = "Microsoft Office Professional Plus 2016", string ver = "16.0.20326.20318", string arch = "x64", string rel = "2016") =>
        new() { Installed = true, Family = fam, Release = rel, Version = ver, Build = ver.Split('.').Length >= 4 ? ver.Split('.')[2] + "." + ver.Split('.')[3] : ver, Architecture = arch };

    [Fact] public void Click_to_run_fingerprint_is_read_not_hardcoded()
    {
        var reg = new MemoryRegistry();
        const string k = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        reg.Set("HKLM", k, "VersionToReport", "String", "16.0.20326.20318", RegView.Reg64);
        reg.Set("HKLM", k, "Platform", "String", "x64", RegView.Reg64);
        reg.Set("HKLM", k, "ProductReleaseIds", "String", "ProPlus2016Retail", RegView.Reg64);
        var sw = new[] { new SoftwareItem { Name = "Microsoft Office Proofing Tools 2016 - Armenian", Category = "Office" } };
        var o = OfficeFingerprint.Read(reg, sw);
        Assert.True(o.Installed); Assert.Equal("x64", o.Architecture); Assert.Equal("16.0.20326.20318", o.Version); Assert.Equal("20326.20318", o.Build);
        Assert.Equal("Microsoft Office Professional Plus 2016", o.Family); Assert.Equal("2016", o.Release); Assert.Single(o.LanguageComponents);
    }

    [Fact] public void Absent_office() => Assert.False(OfficeFingerprint.Read(new MemoryRegistry(), Array.Empty<SoftwareItem>()).Installed);

    [Fact] public void Same_build_compatible() => Assert.Equal(CompatLevel.Compatible, OfficeFingerprint.Compare(O(), O()).Level);
    [Fact] public void Other_build_same_release_not_incompatible() => Assert.Equal(CompatLevel.Compatible, OfficeFingerprint.Compare(O(), O(ver: "16.0.99999.10000")).Level);
    [Fact] public void Arch_mismatch_blocked_with_risks()
    {
        var c = OfficeFingerprint.Compare(O(), O(arch: "x86"));
        Assert.Equal(CompatLevel.Blocked, c.Level); Assert.Contains(c.Risks, r => r.Contains("PtrSafe"));
    }
    [Fact] public void Other_family_warns() => Assert.Equal(CompatLevel.Warning, OfficeFingerprint.Compare(O(), O(fam: "Microsoft 365 Apps for enterprise", rel: "Microsoft 365")).Level);
    [Fact] public void Absent_target_blocks() => Assert.Equal(CompatLevel.Blocked, OfficeFingerprint.Compare(O(), OfficeEnvironment.Absent()).Level);
}
