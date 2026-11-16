using System.Text.Json.Serialization;

namespace WMT.Core;

/// <summary>IDX-001/007 + PKG-005: one compact record per eligible file (also the Files.json manifest entry).</summary>
public sealed class FileEntry
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? ProfileSid { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public Classification Classification { get; set; }
    public string? Note { get; set; }
    public bool Selected { get; set; }
    public bool IsUserData { get; set; }
    public string DestinationRule { get; set; } = "";
    // ---- filled by Backup / Verify / Restore
    public string? PackagePath { get; set; }
    public string? Sha256 { get; set; }
    public FileCopyStatus CopyStatus { get; set; } = FileCopyStatus.Pending;
    public string? CopyError { get; set; }
    public int Attempts { get; set; }
    public bool ChangedSinceScan { get; set; }
    public DateTime? CopiedUtc { get; set; }
    public LifecycleState State { get; set; } = LifecycleState.Discovered;
}

public sealed class DirectoryRecord
{
    public string Path { get; set; } = "";
    public string Category { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string? ProfileSid { get; set; }
    public FolderState State { get; set; }
    public string? Note { get; set; }
    public bool InApplicationRoot { get; set; }
    /// <summary>For empty folders: whether the current empty-folder mode keeps them (recreated in the package).</summary>
    public bool Included { get; set; }
    public string? PackagePath { get; set; }
}

public sealed class FileIndex
{
    public List<FileEntry> Files { get; set; } = new();
    public List<DirectoryRecord> Directories { get; set; } = new();
    public List<Issue> Issues { get; set; } = new();
    public int DuplicateFilesSkipped { get; set; }
    public int DuplicateRootsSkipped { get; set; }
    public int ReparsePointsSkipped { get; set; }
    public int ExcludedFiles { get; set; }
    [JsonIgnore] public Dictionary<string, ScanRoot> RootsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>IDX-001..008: single pass over provider roots only; no GUI objects; loops, duplicates and inaccessible folders handled.</summary>
public sealed class Indexer
{
    readonly IReadOnlyList<string> excluded;
    readonly IProgress<ProgressInfo>? progress;
    readonly CancellationToken ct;
    readonly HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
    readonly FileIndex index = new();
    long scanned;

    public Indexer(IEnumerable<string> excludedPaths, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        excluded = excludedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathUtil.Canonical).ToList();
        this.progress = progress; this.ct = ct;
    }

    public FileIndex Build(IEnumerable<ScanRoot> roots, EmptyFolderMode mode)
    {
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = roots.OrderBy(r => r.IsUserData ? 1 : 0).ToList();
        foreach (var root in ordered)
        {
            ct.ThrowIfCancellationRequested();
            string canon;
            try { canon = PathUtil.Canonical(root.Path); }
            catch (Exception ex) { index.Issues.Add(new Issue(IssueSeverity.Warning, "Scan", "Invalid folder path: " + ex.Message, root.Path)); continue; }
            if (!seenRoots.Add(canon + "|" + root.Category)) { index.DuplicateRootsSkipped++; continue; }
            if (!Directory.Exists(canon)) continue;
            if (excluded.Any(e => PathUtil.IsInsideOrSame(canon, e)))
            {
                index.Directories.Add(new DirectoryRecord { Path = canon, Category = root.Category, RootPath = canon, State = FolderState.SkippedPackage, Note = "Backup destination/package: never used as a source (PKG-008)" });
                continue;
            }
            root.Path = canon;
            index.RootsByPath[canon + "|" + root.Category] = root;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Walk(root, canon, 0, visited);
        }
        ApplyEmptyMode(index, mode);
        progress?.Report(new ProgressInfo("Index finished", index.Files.Count, index.Files.Count));
        return index;
    }

    public static void ApplyEmptyMode(FileIndex index, EmptyFolderMode mode)
    {
        foreach (var d in index.Directories.Where(d => d.State == FolderState.Empty))
            d.Included = mode == EmptyFolderMode.KeepAllEmpty || (mode == EmptyFolderMode.KeepEmptyInApplicationRoots && d.InApplicationRoot);
    }

    /// <summary>Returns the number of eligible files in this subtree.</summary>
    int Walk(ScanRoot root, string dir, int depth, HashSet<string> visited)
    {
        ct.ThrowIfCancellationRequested();
        if (!visited.Add(dir)) return 0;
        if (excluded.Any(e => PathUtil.IsInsideOrSame(dir, e)))
        {
            Record(root, dir, FolderState.SkippedPackage, "Backup destination/package: never scanned");
            return 0;
        }
        if (depth > 0 && LooksLikePackage(dir))
        {
            Record(root, dir, FolderState.SkippedPackage, "Existing migration package: never scanned");
            return 0;
        }
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Record(root, dir, FolderState.Inaccessible, ex.GetType().Name + ": " + ex.Message);
            index.Issues.Add(new Issue(IssueSeverity.Warning, "Scan", "Folder not readable: shown as Inaccessible, not as empty.", dir));
            return 0;
        }
        if (entries.Count == 0)
        {
            if (depth > 0 || root.Recursive) Record(root, dir, FolderState.Empty, null);
            return 0;
        }
        int eligible = 0;
        bool hadSkippedChildren = false;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e is DirectoryInfo d)
            {
                if (!root.Recursive || depth >= root.MaxDepth) { hadSkippedChildren = true; continue; }
                if (root.ExcludedDirectoryNames.Contains(d.Name) || root.ExcludedPaths.Any(x => PathUtil.SamePath(x, d.FullName))) { hadSkippedChildren = true; continue; }
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    index.ReparsePointsSkipped++;
                    Record(root, d.FullName, FolderState.SkippedReparsePoint, "Junction/symbolic link: not followed (prevents loops and duplicates)");
                    continue;
                }
                eligible += Walk(root, d.FullName, depth + 1, visited);
            }
            else if (e is FileInfo f)
            {
                if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) && !f.Attributes.HasFlag((FileAttributes)0x400000))
                { index.ReparsePointsSkipped++; continue; }
                var rel = Path.GetRelativePath(root.Path, f.FullName);
                FileCandidate cand;
                try { cand = new FileCandidate(f.FullName, f.Name, f.Extension, f.Length, f.LastWriteTimeUtc, f.Attributes, rel); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    index.Issues.Add(new Issue(IssueSeverity.Warning, "Scan", "File metadata not readable: " + ex.Message, f.FullName));
                    continue;
                }
                FileDecision? dec;
                try { dec = root.Rule(cand); } catch { dec = new FileDecision(Classification.Review, "Rule error: review manually"); }
                if (dec == null) { index.ExcludedFiles++; continue; }
                if (!claimed.Add(f.FullName)) { index.DuplicateFilesSkipped++; continue; }
                index.Files.Add(new FileEntry
                {
                    Id = "F" + PathUtil.StableId(f.FullName),
                    Category = root.Category, ProviderId = root.ProviderId, SourcePath = f.FullName, RootPath = root.Path, RelativePath = rel,
                    ProfileSid = root.ProfileSid, Size = cand.Length, LastWriteUtc = cand.LastWriteUtc, Classification = dec.Classification,
                    Note = dec.Note, Selected = dec.Classification == Classification.Recommended, IsUserData = root.IsUserData,
                    DestinationRule = root.ProfileSid != null ? "Mapped target profile + path relative to the source profile" : "Same path (drive can be remapped at restore)",
                    State = LifecycleState.Planned
                });
                eligible++;
                if (++scanned % 500 == 0) progress?.Report(new ProgressInfo("Indexing " + root.Category, scanned, null, dir));
            }
        }
        if (eligible == 0 && depth > 0) Record(root, dir, FolderState.OnlyExcluded, hadSkippedChildren ? "Contains only excluded or not-entered items" : "Contains only excluded items");
        return eligible;
    }

    static bool LooksLikePackage(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith("Migration-", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, "Manifests", "Package.json"));
    }

    void Record(ScanRoot root, string path, FolderState state, string? note) =>
        index.Directories.Add(new DirectoryRecord { Path = path, Category = root.Category, RootPath = root.Path, ProfileSid = root.ProfileSid, State = state, Note = note, InApplicationRoot = !root.IsUserData });
}
