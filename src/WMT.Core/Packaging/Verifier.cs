namespace WMT.Core;

public sealed class VerifyResult
{
    public string PackagePath { get; set; } = "";
    public string PackageId { get; set; } = "";
    public bool Passed { get; set; }
    /// <summary>Verified / Failed.</summary>
    public string Status => Passed ? "Verified" : "Failed";
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public string ToolkitVersion { get; set; } = Toolkit.Version;
    public string MetadataFingerprint { get; set; } = "";
    public int FilesChecked { get; set; }
    public long BytesChecked { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int Missing { get; set; }
    public int HashMismatch { get; set; }
    public int SizeMismatch { get; set; }
    public int Extra { get; set; }
    public int Duplicates { get; set; }
    public int UnsafePaths { get; set; }
    public int MetadataProblems { get; set; }
    public int NotCopied { get; set; }
    public string Note { get; set; } = Toolkit.IntegrityNote;
}

/// <summary>VER-001..003: independent, strictly read-only check. It never writes into the package.</summary>
public static class Verifier
{
    public static VerifyResult Verify(string packageRoot, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var l = new PackageLayout(packageRoot);
        var r = new VerifyResult { PackagePath = l.Root };
        void Err(string s) { if (r.Errors.Count < 5000) r.Errors.Add(s); }

        if (!File.Exists(l.PackageJson)) { Err("Manifests\\Package.json is missing: not a package or the package is damaged."); return r; }
        PackageInfo info;
        try { info = Json.ReadFile<PackageInfo>(l.PackageJson); r.PackageId = info.PackageId; }
        catch (Exception ex) { Err("Package.json unreadable: " + ex.Message); return r; }
        if (info.SchemaVersion != 1) Err($"Unsupported package schema version {info.SchemaVersion}.");

        // 1. metadata integrity
        if (!File.Exists(l.MetadataHashesJson)) { Err("Metadata-Hashes.json is missing (package was not finalized)."); r.MetadataProblems++; }
        else
        {
            r.MetadataFingerprint = Hashing.Sha256File(l.MetadataHashesJson);
            MetadataHashes mh;
            try { mh = Json.ReadFile<MetadataHashes>(l.MetadataHashesJson); }
            catch (Exception ex) { Err("Metadata-Hashes.json unreadable: " + ex.Message); r.MetadataProblems++; mh = new MetadataHashes(); }
            foreach (var (rel, hash) in mh.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (!PathUtil.IsSafeRelative(rel)) { Err("Unsafe metadata path: " + rel); r.UnsafePaths++; continue; }
                var full = PathUtil.SafeCombine(l.Root, rel);
                if (!File.Exists(full)) { Err("Metadata file missing: " + rel); r.MetadataProblems++; continue; }
                if (!Hashing.Sha256File(full, ct).Equals(hash, StringComparison.OrdinalIgnoreCase)) { Err("Metadata file changed after the backup: " + rel); r.MetadataProblems++; }
            }
            foreach (var rel in BackupEngine.EnumerateMetadataFiles(l))
                if (!mh.Files.ContainsKey(rel)) { Err("Metadata file not covered by Metadata-Hashes.json: " + rel); r.MetadataProblems++; }
        }

        // 2. file manifest
        List<FileEntry> files;
        try { files = Json.ReadFile<List<FileEntry>>(l.FilesJson); }
        catch (Exception ex) { Err("Files.json unreadable: " + ex.Message); return Done(r); }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("Verifying files", i++, files.Count, f.PackagePath));
            if (!ids.Add(f.Id)) { Err("Duplicate file ID in Files.json: " + f.Id); r.Duplicates++; }
            if (string.IsNullOrEmpty(f.PackagePath) || !PathUtil.IsSafeRelative(f.PackagePath, "Data"))
            { Err($"Unsafe or empty package path for {f.Id}: '{f.PackagePath}'"); r.UnsafePaths++; continue; }
            if (!paths.Add(f.PackagePath)) { Err("Duplicate package path in Files.json: " + f.PackagePath); r.Duplicates++; continue; }
            if (!f.Selected) continue;
            if (f.CopyStatus != FileCopyStatus.Copied) { Err($"Selected file was not copied ({f.CopyStatus}): {f.SourcePath}"); r.NotCopied++; continue; }
            var full = PathUtil.SafeCombine(l.Root, f.PackagePath);
            expected.Add(full);
            var fi = new FileInfo(full);
            if (!fi.Exists) { Err("Missing in package: " + f.PackagePath); r.Missing++; continue; }
            if (fi.Length != f.Size) { Err($"Size differs ({fi.Length} ≠ {f.Size}): {f.PackagePath}"); r.SizeMismatch++; continue; }
            if (string.IsNullOrEmpty(f.Sha256)) { Err("No SHA-256 recorded: " + f.PackagePath); r.HashMismatch++; continue; }
            var h = Hashing.Sha256File(full, ct);
            if (!h.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) { Err("Content changed (SHA-256 mismatch): " + f.PackagePath); r.HashMismatch++; continue; }
            r.FilesChecked++; r.BytesChecked += fi.Length;
        }

        // 3. untracked data / leftovers
        if (Directory.Exists(l.Data))
            foreach (var f in Directory.EnumerateFiles(l.Data, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (expected.Contains(Path.GetFullPath(f))) continue;
                Err((f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ? "Unfinished copy left in package: " : "File in package not listed in the manifest: ") + Path.GetRelativePath(l.Root, f));
                r.Extra++;
            }
        foreach (var name in Directory.EnumerateFileSystemEntries(l.Root))
        {
            var n = Path.GetFileName(name);
            if (n is "Data" or "Manifests" or "Reports" or "Logs" or "Status" or "README.html" or "README.txt") continue;
            r.Warnings.Add("Unexpected item at package root: " + n);
        }

        // 4. folders and declared counts
        try
        {
            if (File.Exists(l.DirectoriesJson))
                foreach (var d in Json.ReadFile<List<DirectoryRecord>>(l.DirectoriesJson).Where(d => d.Included))
                {
                    if (d.PackagePath == null || !PathUtil.IsSafeRelative(d.PackagePath, "Data")) { Err("Unsafe folder path in Directories.json: " + d.PackagePath); r.UnsafePaths++; continue; }
                    if (!Directory.Exists(PathUtil.SafeCombine(l.Root, d.PackagePath))) { Err("Empty folder missing in package: " + d.PackagePath); r.Missing++; }
                }
        }
        catch (Exception ex) { Err("Directories.json unreadable: " + ex.Message); }
        int selected = files.Count(f => f.Selected), copied = files.Count(f => f.Selected && f.CopyStatus == FileCopyStatus.Copied);
        if (info.SelectedFileCount != selected) Err($"Package.json declares {info.SelectedFileCount} selected files, Files.json has {selected}.");
        if (info.CopiedFileCount != copied) Err($"Package.json declares {info.CopiedFileCount} copied files, Files.json has {copied}.");
        if (!info.PackageStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase)) Err($"Package status is '{info.PackageStatus}', not Complete.");
        return Done(r);
    }

    static VerifyResult Done(VerifyResult r)
    {
        r.Passed = r.Errors.Count == 0;
        r.CheckedUtc = DateTime.UtcNow;
        return r;
    }
}

public sealed class TestRestoreResult
{
    public string PackagePath { get; set; } = "";
    public string Sandbox { get; set; } = "";
    public bool Passed { get; set; }
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public string MetadataFingerprint { get; set; } = "";
    public int Restored { get; set; }
    public int Failed { get; set; }
    public int Folders { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>RST-001: restores into an empty sandbox only, then re-hashes every file. Real Windows/Office paths are never touched.</summary>
public static class TestRestore
{
    public static List<string> ValidateSandbox(string packageRoot, string sandbox)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(sandbox)) { errors.Add("Choose an empty test folder."); return errors; }
        var s = PathUtil.Canonical(sandbox);
        if (!Directory.Exists(s)) errors.Add("The test folder must already exist.");
        else if (Directory.EnumerateFileSystemEntries(s).Any()) errors.Add("The test folder must be empty.");
        if (PathUtil.IsInsideOrSame(s, packageRoot) || PathUtil.IsInsideOrSame(packageRoot, s)) errors.Add("The test folder must be outside the package.");
        var root = Path.GetPathRoot(s) ?? "";
        if (PathUtil.SamePath(s, root)) errors.Add("A drive root cannot be used as a test folder.");
        foreach (var sys in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(sys) && PathUtil.IsInsideOrSame(s, sys)) errors.Add("System folders cannot be used as a test folder.");
        return errors;
    }

    public static TestRestoreResult Run(string packageRoot, string sandbox, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var l = new PackageLayout(packageRoot);
        var r = new TestRestoreResult { PackagePath = l.Root, Sandbox = sandbox };
        var errors = ValidateSandbox(l.Root, sandbox);
        if (errors.Count > 0) { r.Errors.AddRange(errors); return r; }
        var sb = PathUtil.Canonical(sandbox);
        r.MetadataFingerprint = File.Exists(l.MetadataHashesJson) ? Hashing.Sha256File(l.MetadataHashesJson) : "";
        var files = Json.ReadFile<List<FileEntry>>(l.FilesJson);
        int i = 0;
        foreach (var f in files.Where(f => f.Selected && f.CopyStatus == FileCopyStatus.Copied))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("Test restore", i++, files.Count, f.PackagePath));
            try
            {
                if (f.PackagePath == null || !PathUtil.IsSafeRelative(f.PackagePath, "Data")) throw new InvalidDataException("Unsafe package path " + f.PackagePath);
                var src = PathUtil.SafeCombine(l.Root, f.PackagePath);
                var rel = f.PackagePath.Substring("Data\\".Length);
                var dst = PathUtil.SafeCombine(sb, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: false);
                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                var h = Hashing.Sha256File(dst, ct);
                if (!h.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA-256 differs after restore");
                r.Restored++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r.Failed++; r.Errors.Add($"{f.PackagePath}: {ex.Message}"); }
        }
        if (File.Exists(l.DirectoriesJson))
            foreach (var d in Json.ReadFile<List<DirectoryRecord>>(l.DirectoriesJson).Where(d => d.Included && d.PackagePath != null))
            {
                try { Directory.CreateDirectory(PathUtil.SafeCombine(sb, d.PackagePath!.Substring("Data\\".Length))); r.Folders++; }
                catch (Exception ex) { r.Failed++; r.Errors.Add($"{d.PackagePath}: {ex.Message}"); }
            }
        r.Passed = r.Failed == 0 && r.Errors.Count == 0;
        r.CheckedUtc = DateTime.UtcNow;
        Json.WriteFile(Path.Combine(sb, "TEST-RESTORE-RESULT.json"), r);
        return r;
    }
}

/// <summary>Results recorded after the fact into Package\Status (outside the integrity-covered metadata).</summary>
public static class PackageStatusStore
{
    public static string Save<T>(string packageRoot, string prefix, T value)
    {
        var l = new PackageLayout(packageRoot);
        var path = Path.Combine(l.Status, $"{prefix}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.json");
        Json.WriteFile(path, value);
        return path;
    }

    public static T? Latest<T>(string packageRoot, string prefix) where T : class
    {
        var dir = new PackageLayout(packageRoot).Status;
        if (!Directory.Exists(dir)) return null;
        var f = Directory.EnumerateFiles(dir, prefix + "-*.json").OrderByDescending(x => x, StringComparer.Ordinal).FirstOrDefault();
        try { return f == null ? null : Json.ReadFile<T>(f); } catch { return null; }
    }
}
