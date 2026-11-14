using WMT.Core;
using WMT.Core.ViewModels;
using Xunit;

namespace WMT.Tests;

sealed class CountingSoftware : ISoftwareScanner
{
    public int Calls; public Func<CancellationToken, SoftwareScanResult>? Impl;
    public SoftwareScanResult Scan(IProgress<ProgressInfo>? p, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Impl != null ? Impl(ct) : new SoftwareScanResult(new[] { Item("App A"), Item("App B") }, 3, 0, Array.Empty<string>());
    }
    public static SoftwareItem Item(string n) => new(n, "P", "1.0", "x64", "HKLM 64-bit", "Application", "Reinstall", Confidence.Confirmed, "");
}

sealed class CountingProfiles : IProfileScanner
{
    public int Calls;
    public ProfileScanResult Scan(IProgress<ProgressInfo>? p, CancellationToken ct)
    {
        Calls++;
        return new ProfileScanResult(new[] { new UserProfileItem("PC\\a", "S-1-5-21-1", @"C:\Users\a", true, AccessState.Accessible, "", null) }, 2, Array.Empty<string>());
    }
}

public class ViewModelTests
{
    static MainViewModel Vm(CountingSoftware sw, CountingProfiles pf, Func<DateTime>? clock = null) =>
        new(sw, pf, _ => OfficeFingerprint.Absent, T.NewLog(), null, clock);

    [Fact] public void Nothing_is_scanned_on_startup_and_lists_say_not_scanned()
    {
        var sw = new CountingSoftware(); var pf = new CountingProfiles(); var vm = Vm(sw, pf);
        Assert.Equal(0, sw.Calls); Assert.Equal(0, pf.Calls);
        Assert.Equal("Not scanned", vm.SoftwareStatus); Assert.Equal("Not scanned", vm.ProfilesStatus);
        Assert.Empty(vm.FilteredSoftware); Assert.Empty(vm.Profiles); Assert.True(vm.IsIdle);
    }

    [Fact] public async Task Discover_populates_lists_and_returns_to_idle()
    {
        var vm = Vm(new CountingSoftware(), new CountingProfiles());
        await vm.DiscoverSoftwareAsync(); await vm.DiscoverProfilesAsync();
        Assert.Equal(2, vm.FilteredSoftware.Count); Assert.Single(vm.Profiles); Assert.True(vm.IsIdle);
    }

    [Fact] public async Task Selection_survives_search_filter_and_rescan()
    {
        var vm = Vm(new CountingSoftware(), new CountingProfiles());
        await vm.DiscoverSoftwareAsync();
        vm.FilteredSoftware.Single(s => s.Item.Name == "App A").IsSelected = true;
        vm.SoftwareSearch = "App B"; Assert.Single(vm.FilteredSoftware);
        vm.SoftwareSearch = ""; Assert.True(vm.FilteredSoftware.Single(s => s.Item.Name == "App A").IsSelected);
        await vm.DiscoverSoftwareAsync();
        Assert.True(vm.FilteredSoftware.Single(s => s.Item.Name == "App A").IsSelected);
        Assert.False(vm.FilteredSoftware.Single(s => s.Item.Name == "App B").IsSelected);
    }

    [Fact] public async Task Select_all_applies_only_to_filtered_rows()
    {
        var vm = Vm(new CountingSoftware(), new CountingProfiles());
        await vm.DiscoverSoftwareAsync();
        vm.SoftwareSearch = "App A"; vm.SelectSoftware("all"); vm.SoftwareSearch = "";
        Assert.True(vm.FilteredSoftware.Single(s => s.Item.Name == "App A").IsSelected);
        Assert.False(vm.FilteredSoftware.Single(s => s.Item.Name == "App B").IsSelected);
    }

    [Fact] public async Task Failure_restores_idle_and_reports_failed_without_changing_list()
    {
        var sw = new CountingSoftware { Impl = _ => throw new InvalidOperationException("registry boom") };
        var vm = Vm(sw, new CountingProfiles());
        await vm.DiscoverSoftwareAsync();
        Assert.True(vm.IsIdle); Assert.Contains("Failed", vm.SoftwareStatus); Assert.Empty(vm.FilteredSoftware);
        Assert.True(vm.DiscoverSoftwareCommand.CanExecute(null));
    }

    [Fact] public async Task Cancel_restores_idle_and_reports_cancelled()
    {
        var sw = new CountingSoftware { Impl = ct => { ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); return null!; } };
        var vm = Vm(sw, new CountingProfiles());
        var task = vm.DiscoverSoftwareAsync();
        Assert.True(vm.IsBusy); Assert.False(vm.DiscoverSoftwareCommand.CanExecute(null)); Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await task;
        Assert.True(vm.IsIdle); Assert.Contains("Cancelled", vm.SoftwareStatus);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact] public async Task Heartbeat_warns_after_60_seconds_without_progress_and_never_claims_hung()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var sw = new CountingSoftware { Impl = ct => { ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); return null!; } };
        var vm = Vm(sw, new CountingProfiles(), () => now);
        var task = vm.DiscoverSoftwareAsync();
        vm.CheckHeartbeat(now.AddSeconds(59)); Assert.Equal("", vm.StopWarning);
        vm.CheckHeartbeat(now.AddSeconds(61)); Assert.Contains("No recent progress", vm.StopWarning);
        Assert.DoesNotContain("hung", vm.StopWarning, StringComparison.OrdinalIgnoreCase);
        vm.Cancel(); await task;
        Assert.Equal("", vm.StopWarning);
    }

    [Fact] public async Task Office_details_work_before_software_scan_and_note_it()
    {
        var vm = Vm(new CountingSoftware(), new CountingProfiles());
        await vm.ShowOfficeDetailsAsync();
        Assert.Contains("not detected", vm.OfficeDetailsText);
    }
}
