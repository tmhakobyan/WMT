using Microsoft.Win32;
using System.Security.Principal;

namespace WMT.Core;

public sealed record ProfileRecord(string Sid, string? Path, bool Loaded, DateTime? LastUsedUtc);
public interface IProfileSource { IReadOnlyList<ProfileRecord> Read(); }
public interface IAccountResolver { string? Resolve(string sid); }

/// <summary>Reads Windows ProfileList (the registry source behind Win32_UserProfile). Read-only.</summary>
public sealed class WindowsProfileSource : IProfileSource
{
    public IReadOnlyList<ProfileRecord> Read()
    {
        var list = new List<ProfileRecord>();
        if (!OperatingSystem.IsWindows()) return list;
        using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (root == null) return list;
        foreach (var sid in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sid);
            var path = k?.GetValue("ProfileImagePath")?.ToString();
            bool loaded;
            try { using var u = Registry.Users.OpenSubKey(sid); loaded = u != null; } catch { loaded = false; }
            DateTime? last = null;
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var dat = new FileInfo(Path.Combine(path, "NTUSER.DAT"));
                    if (dat.Exists) last = dat.LastWriteTimeUtc;
                }
            }
            catch { }
            list.Add(new ProfileRecord(sid, path, loaded, last));
        }
        return list;
    }
}

public sealed class WindowsAccountResolver : IAccountResolver
{
    public string? Resolve(string sid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch { return null; }
    }
}

public sealed class ProfileDiscovery : IProfileScanner
{
    readonly IProfileSource source;
    readonly IAccountResolver resolver;
    readonly Func<string, AccessState> accessCheck;
    readonly string? currentUserProfile;

    public ProfileDiscovery(IProfileSource source, IAccountResolver resolver,
        Func<string, AccessState>? accessCheck = null, string? currentUserProfile = null)
    {
        this.source = source; this.resolver = resolver;
        this.accessCheck = accessCheck ?? DefaultAccess;
        this.currentUserProfile = currentUserProfile ?? Environment.GetEnvironmentVariable("USERPROFILE");
    }

    /// <summary>Real accounts: local/domain (S-1-5-21-*) and Entra ID/AAD (S-1-12-1-*).</summary>
    public static bool IsRealAccountSid(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
        sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);

    static AccessState DefaultAccess(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return AccessState.Missing;
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
            return AccessState.Accessible;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { return AccessState.Unreadable; }
    }

    public ProfileScanResult Scan(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var records = source.Read();
        var warnings = new List<string>();
        var items = new List<UserProfileItem>();
        int excluded = 0;
        long i = 0;
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("Checking profiles", i++, records.Count));
            if (!IsRealAccountSid(r.Sid)) { excluded++; continue; }
            var path = r.Path ?? "";
            var access = string.IsNullOrEmpty(path) ? AccessState.Missing : accessCheck(path);
            var isCurrent = !string.IsNullOrEmpty(currentUserProfile) &&
                string.Equals(path.TrimEnd('\\'), currentUserProfile!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            string note = access switch
            {
                AccessState.Unreadable => "Cannot read this profile: needs administrator rights or that user's permissions.",
                AccessState.Missing => "Profile folder not found (account may be deleted or on a disconnected drive).",
                _ when !isCurrent => "Another user's profile: reading some files may need administrator permission.",
                _ => ""
            };
            var account = resolver.Resolve(r.Sid);
            if (string.IsNullOrEmpty(account))
            {
                var folder = string.IsNullOrEmpty(path) ? r.Sid : Path.GetFileName(path.TrimEnd('\\', '/'));
                account = folder + " (name unresolved)";
            }
            items.Add(new UserProfileItem(account, r.Sid, path, r.Loaded, access, note, r.LastUsedUtc));
        }
        items.Sort((a, b) => string.Compare(a.Account, b.Account, StringComparison.OrdinalIgnoreCase));
        progress?.Report(new ProgressInfo("Profile discovery finished", items.Count, items.Count));
        return new ProfileScanResult(items, excluded, warnings);
    }
}
