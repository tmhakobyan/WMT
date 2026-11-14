using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class OfficeTests
{
    static OfficeInfo O(string family = "Microsoft Office Professional Plus 2016", string build = "16.0.20326.20318", string arch = "x64")
        => new(true, family, build, arch, "Click-to-Run", "", Array.Empty<string>(), Array.Empty<string>());

    [Fact] public void Click_to_run_values_are_parsed_without_hardcoding()
    {
        var d = new Dictionary<string, string>
        { ["VersionToReport"] = "16.0.20326.20318", ["Platform"] = "x64", ["ProductReleaseIds"] = "ProPlus2016Retail" };
        var o = OfficeFingerprint.Build(d, Array.Empty<SoftwareItem>());
        Assert.True(o.Installed);
        Assert.Equal("x64", o.Architecture);
        Assert.Equal("16.0.20326.20318", o.Build);
        Assert.Equal("Microsoft Office Professional Plus 2016", o.Family);
    }

    [Fact] public void No_data_means_absent()
        => Assert.False(OfficeFingerprint.Build(null, Array.Empty<SoftwareItem>()).Installed);

    [Fact] public void Fallback_to_uninstall_entry_is_marked_inferred()
    {
        var sw = new[] { new SoftwareItem("Microsoft Office Professional Plus 2016", "Microsoft", "16.0.4266.1001", "x86",
            "HKLM 32-bit", "Office", "", Confidence.Confirmed, "") };
        var o = OfficeFingerprint.Build(null, sw);
        Assert.True(o.Installed); Assert.Contains("inferred", o.InstallType);
    }

    [Fact] public void Same_build_is_compatible() => Assert.Equal(CompatLevel.Compatible, OfficeFingerprint.Compare(O(), O()).Level);
    [Fact] public void Different_build_is_not_automatically_incompatible()
        => Assert.Equal(CompatLevel.Compatible, OfficeFingerprint.Compare(O(), O(build: "16.0.99999.1")).Level);
    [Fact] public void Architecture_mismatch_is_blocked()
        => Assert.Equal(CompatLevel.Blocked, OfficeFingerprint.Compare(O(), O(arch: "x86")).Level);
    [Fact] public void Other_family_warns()
        => Assert.Equal(CompatLevel.Warning, OfficeFingerprint.Compare(O(), O(family: "Microsoft 365 Apps")).Level);
    [Fact] public void Absent_target_warns()
        => Assert.Equal(CompatLevel.Warning, OfficeFingerprint.Compare(O(), OfficeFingerprint.Absent).Level);
}
