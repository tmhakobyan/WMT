using System.Security.Principal;

namespace WMT.Core;

public interface IAccountResolver { string? Resolve(string sid); }

public sealed class WindowsAccountResolver : IAccountResolver
{
    public string? Resolve(string sid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; } catch { return null; }
    }
}

public sealed record ProfileScanResult(List<UserProfileInfo> Items, int ExcludedSystemCount, List<Issue> Issues);

/// <summary>USR-001/002: profiles from ProfileList (the data behind Win32_UserProfile), never auto-selected.</summary>
public sealed class ProfileDiscovery
{
    const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    readonly IRegistryReader reg;
    readonly IAccountResolver resolver;
    readonly Func<string, AccessState> accessCheck;
    readonly string? currentSid;

    public ProfileDiscovery(IRegistryReader reg, IAccountResolver resolver, Func<string, AccessState>? accessCheck = null, string? currentSid = null)
    {
        this.reg = reg; this.resolver = resolver;
        this.accessCheck = accessCheck ?? DefaultAccess;
        this.currentSid = currentSid ?? CurrentUserSid();
    }

    public static string? CurrentUserSid() { try { return WindowsIdentity.GetCurrent().User?.Value; } catch { return null; } }

    /// <summary>Local/domain (S-1-5-21-*) and Microsoft Entra ID (S-1-12-1-*) accounts are real users.</summary>
    public static bool IsRealAccountSid(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);

    public static AccessState DefaultAccess(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return AccessState.Missing;
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
            return AccessState.Accessible;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return AccessState.Inaccessible; }
    }

    public ProfileScanResult Scan(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var items = new List<UserProfileInfo>();
        var issues = new List<Issue>();
        int excluded = 0;
        var sids = reg.SubKeys("HKLM", ProfileListKey, RegView.Reg64);
        if (sids.Count == 0) issues.Add(new Issue(IssueSeverity.Warning, "Profiles", "ProfileList registry key could not be read."));
        long i = 0;
        foreach (var sid in sids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("Checking user profiles", i++, sids.Count));
            if (!IsRealAccountSid(sid) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) { excluded++; continue; }
            var path = Environment.ExpandEnvironmentVariables(reg.Value("HKLM", ProfileListKey + "\\" + sid, "ProfileImagePath", RegView.Reg64)?.AsString() ?? "");
            var access = string.IsNullOrEmpty(path) ? AccessState.Missing : accessCheck(path);
            bool loaded = reg.KeyExists("HKU", sid);
            bool isCurrent = currentSid != null && sid.Equals(currentSid, StringComparison.OrdinalIgnoreCase);
            DateTime? last = null;
            try
            {
                if (access == AccessState.Accessible)
                {
                    var dat = new FileInfo(Path.Combine(path, "NTUSER.DAT"));
                    if (dat.Exists) last = dat.LastWriteTimeUtc;
                }
            }
            catch { }
            var note = access switch
            {
                AccessState.Inaccessible => "No permission to read this profile. Run as administrator or sign in as that user. The tool never bypasses permissions.",
                AccessState.Missing => "Profile folder not found (deleted account or disconnected drive).",
                _ when !isCurrent => "Another user's profile: some files and the registry hive may be unreadable" + (loaded ? "." : " (hive not loaded; not mounted automatically)."),
                _ => ""
            };
            var account = resolver.Resolve(sid);
            if (string.IsNullOrEmpty(account))
            {
                var folder = string.IsNullOrEmpty(path) ? sid : Path.GetFileName(path.TrimEnd('\\', '/'));
                account = folder + " (name unresolved)";
            }
            items.Add(new UserProfileInfo
            {
                Account = account, Sid = sid, ProfilePath = path, Loaded = loaded, IsCurrentUser = isCurrent,
                Access = access, AccessNote = note, LastUsedUtc = last, Selected = false
            });
        }
        items.Sort((a, b) => string.Compare(a.Account, b.Account, StringComparison.CurrentCultureIgnoreCase));
        progress?.Report(new ProgressInfo("Profile inventory finished", items.Count, items.Count));
        return new ProfileScanResult(items, excluded, issues);
    }
}
