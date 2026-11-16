namespace WMT.Core;

/// <summary>USR-004 user-data categories (opt-in, never scanned by default).</summary>
public static class UserDataCategories
{
    public static readonly string[] All = { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "Favorites" };
}

public sealed class DiscoveryOptions
{
    /// <summary>Per profile SID: user-data categories explicitly chosen by the user.</summary>
    public Dictionary<string, HashSet<string>> UserData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>USR-005: own folders on any accessible drive.</summary>
    public List<string> CustomPaths { get; set; } = new();
    public bool IncludeWindowsSettings { get; set; } = true;
    public bool IncludeRuntimes { get; set; } = true;
    public bool QueryPythonPackages { get; set; } = true;
    public TimeSpan PythonTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public EmptyFolderMode EmptyFolders { get; set; } = EmptyFolderMode.ExcludeEmpty;
    /// <summary>Keyboard layouts tab result (selection included); null when that tab was not used.</summary>
    public KeyboardSnapshot? Keyboard { get; set; }
    /// <summary>Windows Settings tab result (selection included); null = capture defaults during planning.</summary>
    public WindowsSettingsSnapshot? WindowsSettings { get; set; }
    /// <summary>PKG-008: never scanned (the backup destination / existing packages).</summary>
    public List<string> ExcludedPaths { get; set; } = new();
}

public sealed class DiscoveryContext
{
    public required IReadOnlyList<SoftwareItem> AllSoftware { get; init; }
    public required IReadOnlyList<SoftwareItem> SelectedSoftware { get; init; }
    public required IReadOnlyList<UserProfileInfo> SelectedProfiles { get; init; }
    public required DiscoveryOptions Options { get; init; }
    public required IRegistryReader Registry { get; init; }
    public required IProcessRunner Processes { get; init; }
    public required ActivityLog Log { get; init; }
    public IProgress<ProgressInfo>? Progress { get; init; }
    public CancellationToken Token { get; init; }
    public string? CurrentUserSid { get; init; }
    public OfficeEnvironment? Office { get; set; }

    public bool IsSelected(string providerId) => SelectedSoftware.Any(s => s.Providers.Contains(providerId));
    public IEnumerable<SoftwareItem> SelectedFor(string providerId) => SelectedSoftware.Where(s => s.Providers.Contains(providerId));

    /// <summary>Registry hive prefix for a profile: HKCU for the current user, HKU\SID when loaded, null otherwise (never mounted).</summary>
    public (string Hive, string Prefix)? UserHive(UserProfileInfo p)
    {
        if (CurrentUserSid != null && p.Sid.Equals(CurrentUserSid, StringComparison.OrdinalIgnoreCase)) return ("HKCU", "");
        if (p.Loaded) return ("HKU", p.Sid + "\\");
        return null;
    }
}

public sealed class ProviderOutput
{
    public List<ScanRoot> Roots { get; } = new();
    public List<Component> Components { get; } = new();
    public List<Dependency> Dependencies { get; } = new();
    public List<Issue> Issues { get; } = new();
    public List<RegistryValueRecord> Registry { get; } = new();
    public Dictionary<string, object> Manifests { get; } = new();
    public List<string> FunctionalTests { get; } = new();

    public void Merge(ProviderOutput o)
    {
        Roots.AddRange(o.Roots); Components.AddRange(o.Components); Dependencies.AddRange(o.Dependencies);
        Issues.AddRange(o.Issues); Registry.AddRange(o.Registry); FunctionalTests.AddRange(o.FunctionalTests);
        foreach (var kv in o.Manifests) Manifests[kv.Key] = kv.Value;
    }
}

public interface IDiscoveryProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>True when this provider should run for the current selection.</summary>
    bool Applies(DiscoveryContext ctx);
    /// <summary>Returns targeted scan roots, components and dependencies (no bulk file walking here).</summary>
    ProviderOutput Discover(DiscoveryContext ctx);
    /// <summary>Optional second pass over the files the indexer found under this provider's roots.</summary>
    void AnalyzeIndexed(DiscoveryContext ctx, IReadOnlyList<FileEntry> files, ProviderOutput output) { }
}

/// <summary>Resolves a profile's known folders (current user: shell folders; others: their loaded hive or defaults).</summary>
public sealed class ProfileFolders
{
    public string Profile = "", Roaming = "", Local = "", Documents = "", Desktop = "", Downloads = "", Pictures = "", Videos = "", Music = "", Favorites = "";
    public bool Verified;

    public string? For(string category) => category switch
    {
        "Desktop" => Desktop, "Documents" => Documents, "Downloads" => Downloads, "Pictures" => Pictures,
        "Videos" => Videos, "Music" => Music, "Favorites" => Favorites, _ => null
    };

    public static ProfileFolders Resolve(UserProfileInfo p, DiscoveryContext ctx)
    {
        var f = new ProfileFolders { Profile = p.ProfilePath };
        string D(string sub) => Path.Combine(p.ProfilePath, sub);
        f.Roaming = D(@"AppData\Roaming"); f.Local = D(@"AppData\Local"); f.Documents = D("Documents"); f.Desktop = D("Desktop");
        f.Downloads = D("Downloads"); f.Pictures = D("Pictures"); f.Videos = D("Videos"); f.Music = D("Music"); f.Favorites = D("Favorites");
        var hive = ctx.UserHive(p);
        if (hive == null) return f;
        var (h, prefix) = hive.Value;
        const string usf = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        string? R(string name)
        {
            var v = ctx.Registry.Value(h, prefix + usf, name)?.AsString();
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Replace("%USERPROFILE%", p.ProfilePath, StringComparison.OrdinalIgnoreCase);
            if (h == "HKCU") v = Environment.ExpandEnvironmentVariables(v);
            return v.Contains('%') ? null : v;
        }
        f.Roaming = R("AppData") ?? f.Roaming;
        f.Local = R("Local AppData") ?? f.Local;
        f.Documents = R("Personal") ?? f.Documents;
        f.Desktop = R("Desktop") ?? f.Desktop;
        f.Downloads = R("{374DE290-123F-4565-9164-39C4925E467B}") ?? f.Downloads;
        f.Pictures = R("My Pictures") ?? f.Pictures;
        f.Videos = R("My Video") ?? f.Videos;
        f.Music = R("My Music") ?? f.Music;
        f.Favorites = R("Favorites") ?? f.Favorites;
        f.Verified = true;
        return f;
    }
}

public static class Rules
{
    public static readonly HashSet<string> TempNames = new(StringComparer.OrdinalIgnoreCase) { "Thumbs.db", "ehthumbs.db", ".DS_Store" };

    public static bool IsTempOrLock(FileCandidate f) =>
        f.Name.StartsWith("~$", StringComparison.Ordinal) || f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
        || TempNames.Contains(f.Name) || f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    public static Func<FileCandidate, FileDecision?> Extensions(Classification cls, string? note, params string[] exts)
    {
        var set = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
        return f => IsTempOrLock(f) ? null : set.Contains(f.Extension) ? new FileDecision(cls, note) : null;
    }

    public static Func<FileCandidate, FileDecision?> AllUserFiles(string? note = null) =>
        f => IsTempOrLock(f) ? null : new FileDecision(Classification.Recommended, note);

    public static readonly HashSet<string> HeavyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".venv", "venv", ".conda", "site-packages", "$RECYCLE.BIN", "System Volume Information",
        ".cache", ".ipynb_checkpoints", ".idea", ".vs", "AppData"
    };
}
