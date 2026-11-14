using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class ProfileDiscoveryTests
{
    const string Alice = "S-1-5-21-1-2-3-1001", Bob = "S-1-5-21-1-2-3-1002", Gone = "S-1-5-21-1-2-3-1003";

    static ProfileScanResult Run(Func<string, AccessState> access, out FakeProfiles src)
    {
        src = new FakeProfiles();
        src.Recs.Add(new ProfileRecord("S-1-5-18", @"C:\Windows\system32\config\systemprofile", false, null));
        src.Recs.Add(new ProfileRecord("S-1-5-19", @"C:\Windows\ServiceProfiles\LocalService", false, null));
        src.Recs.Add(new ProfileRecord(Alice, @"C:\Users\alice", true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        src.Recs.Add(new ProfileRecord(Bob, @"C:\Users\bob", false, null));
        src.Recs.Add(new ProfileRecord(Gone, @"D:\Users\gone", false, null));
        var res = new FakeResolver(); res.Map[Alice] = @"PC\alice";
        return new ProfileDiscovery(src, res, access, @"C:\Users\alice").Scan(null, CancellationToken.None);
    }

    [Fact] public void System_and_service_profiles_are_excluded_by_default()
    {
        var r = Run(_ => AccessState.Accessible, out _);
        Assert.Equal(3, r.Items.Count);
        Assert.Equal(2, r.ExcludedSystemCount);
        Assert.DoesNotContain(r.Items, i => i.Sid == "S-1-5-18");
    }

    [Fact] public void Unresolved_account_falls_back_to_folder_name()
    {
        var r = Run(_ => AccessState.Accessible, out _);
        Assert.Contains(r.Items, i => i.Sid == Bob && i.Account.StartsWith("bob"));
        Assert.Contains(r.Items, i => i.Sid == Alice && i.Account == @"PC\alice");
    }

    [Fact] public void Unreadable_and_missing_profiles_are_reported_not_hidden()
    {
        var r = Run(p => p.Contains("bob") ? AccessState.Unreadable : p.Contains("gone") ? AccessState.Missing : AccessState.Accessible, out _);
        var bob = r.Items.Single(i => i.Sid == Bob);
        Assert.Equal(AccessState.Unreadable, bob.Access);
        Assert.Contains("administrator", bob.AccessNote);
        Assert.Equal(AccessState.Missing, r.Items.Single(i => i.Sid == Gone).Access);
    }

    [Fact] public void Current_user_has_no_warning_note_but_other_users_do()
    {
        var r = Run(_ => AccessState.Accessible, out _);
        Assert.Equal("", r.Items.Single(i => i.Sid == Alice).AccessNote);
        Assert.NotEqual("", r.Items.Single(i => i.Sid == Bob).AccessNote);
    }

    [Fact] public void Entra_id_sid_counts_as_real_account() => Assert.True(ProfileDiscovery.IsRealAccountSid("S-1-12-1-1-2-3-4"));
    [Fact] public void Local_system_is_not_a_real_account() => Assert.False(ProfileDiscovery.IsRealAccountSid("S-1-5-18"));
}
