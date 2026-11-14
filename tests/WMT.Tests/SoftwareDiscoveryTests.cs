using WMT.Core;
using Xunit;
using static WMT.Tests.T;

namespace WMT.Tests;

public class SoftwareDiscoveryTests
{
    static SoftwareScanResult Scan(FakeUninstall f) => new SoftwareDiscovery(f).Scan(null, CancellationToken.None);

    [Fact] public void Zero_entries_gives_empty_result()
    {
        var r = Scan(new FakeUninstall());
        Assert.Empty(r.Items); Assert.Equal(0, r.FilteredCount);
    }

    [Fact] public void One_entry()
    {
        var f = new FakeUninstall(); f.Recs.Add(R("7-Zip 23.01"));
        var r = Scan(f);
        Assert.Single(r.Items);
        Assert.Equal("Application", r.Items[0].Type);
    }

    [Fact] public void Many_entries_are_sorted_by_name()
    {
        var f = new FakeUninstall();
        foreach (var n in new[] { "Zeta", "alpha", "Mid" }) f.Recs.Add(R(n));
        Assert.Equal(new[] { "alpha", "Mid", "Zeta" }, Scan(f).Items.Select(i => i.Name));
    }

    [Fact] public void System_components_patches_kb_and_blank_names_are_filtered()
    {
        var f = new FakeUninstall();
        f.Recs.Add(R("Hidden", sys: true));
        f.Recs.Add(R("Some patch", parent: "Parent"));
        f.Recs.Add(R("KB5030211"));
        f.Recs.Add(R(null));
        f.Recs.Add(R("Real App"));
        var r = Scan(f);
        Assert.Single(r.Items);
        Assert.Equal(4, r.FilteredCount);
    }

    [Fact] public void Duplicates_across_hives_are_merged_but_different_arch_is_kept()
    {
        var f = new FakeUninstall();
        f.Recs.Add(R("App", "1.0", arch: "x64"));
        f.Recs.Add(R("App", "1.0", arch: "x64", source: "HKCU (current user)"));
        f.Recs.Add(R("App", "1.0", arch: "x86"));
        var r = Scan(f);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(1, r.DuplicateCount);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "Runtime")]
    [InlineData(".NET Desktop Runtime 8.0.1 (x64)", "Runtime")]
    [InlineData("Microsoft Office Professional Plus 2016", "Office")]
    [InlineData("Python 3.11.9 (64-bit)", "Python")]
    [InlineData("7-Zip 23.01", "Application")]
    public void Classification(string name, string expected) => Assert.Equal(expected, SoftwareDiscovery.Classify(name));

    [Fact] public void Runtime_recovery_is_a_reinstall_manifest_not_a_copy()
        => Assert.Contains("no DLL copy", SoftwareDiscovery.RecoveryFor("Runtime"));

    [Fact] public void Missing_version_and_publisher_are_kept_with_inferred_confidence()
    {
        var f = new FakeUninstall(); f.Recs.Add(R("NoMeta", ver: null, pub: null));
        var item = Assert.Single(Scan(f).Items);
        Assert.Equal(Confidence.Inferred, item.Confidence);
        Assert.Equal("", item.Publisher);
    }

    [Fact] public void Source_warnings_are_propagated()
    {
        var f = new FakeUninstall(); f.Warn.Add("HKCU unreadable");
        Assert.Contains("HKCU unreadable", Scan(f).Warnings);
    }

    [Fact] public void Cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SoftwareDiscovery(new FakeUninstall()).Scan(null, cts.Token));
    }
}
