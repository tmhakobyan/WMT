using System.Security.Cryptography;

namespace WMT.Core;

public sealed class BackupResult
{
    public string PackagePath { get; set; } = "";
    public string Status { get; set; } = "";
    public int Selected { get; set; }
    public int Copied { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int ChangedSinceScan { get; set; }
    public List<Issue> Issues { get; set; } = new();
    public bool Cancelled { get; set; }
}

public sealed class BackupInputs
{
    public required MigrationPlan Plan { get; init; }
    public required IReadOnlyList<SoftwareItem> AllSoftware { get; init; }
    public required IReadOnlyList<UserProfileInfo> AllProfiles { get; init; }
    public string? WingetExportJson { get; init; }
}

/// <summary>PKG + BKP requirements: copy only inside the chosen existing folder, temp file + SHA-256 compare + atomic finalize,
/// bounded retries, Incomplete status, retry-failed-only, Metadata-Hashes.json written last.</summary>
public sealed class BackupEngine
{
    readonly ActivityLog log;
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Test hook: called after a file has been copied to .partial and before it is verified.</summary>
    public Action<FileEntry, string>? AfterPartialWritten { get; set; }

    public BackupEngine(ActivityLog log) { this.log = log; }

    /// <summary>PKG-001/PKG-008/NFR-006 checks. Errors block the backup.</summary>
    public static List<Issue> ValidateDestination(string destination, MigrationPlan plan)
    {
        var issues = new List<Issue>();
        if (string.IsNullOrWhiteSpace(destination)) { issues.Add(new Issue(IssueSeverity.Blocker, "Destination", "Choose a destination folder.")); return issues; }
        string dest;
        try { dest = PathUtil.Canonical(destination); }
        catch (Exception ex) { issues.Add(new Issue(IssueSeverity.Blocker, "Destination", "Invalid path: " + ex.Message, destination)); return issues; }
        if (!Directory.Exists(dest)) { issues.Add(new Issue(IssueSeverity.Blocker, "Destination", "The folder must already exist; the tool never creates or changes parent folders.", dest)); return issues; }
        if (PackageLayout.IsPackage(dest)) issues.Add(new Issue(IssueSeverity.Blocker, "Destination", "This folder is itself a migration package; choose the folder that should contain packages.", dest));
        foreach (var root in plan.Index.Files.Select(f => f.RootPath).Distinct(PathUtil.Comparer))
        {
            if (PathUtil.IsInsideOrSame(dest, root))
                issues.Add(new Issue(IssueSeverity.Blocker, "Destination", $"Destination is inside a selected source folder ({root}). Choose a folder outside every source.", dest));
        }
        var need = plan.Index.Files.Where(f => f.Selected).Sum(f => f.Size);
        try
        {
            var root = Path.GetPathRoot(dest);
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\"))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                var required = (long)(need * 1.02) + 50L * 1024 * 1024;
                if (free < required)
                    issues.Add(new Issue(IssueSeverity.Blocker, "Destination", $"Not enough free space: need about {PathUtil.FormatBytes(required)}, available {PathUtil.FormatBytes(free)}.", dest));
            }
        }
        catch (Exception ex) { issues.Add(new Issue(IssueSeverity.Warning, "Destination", "Free space could not be checked: " + ex.Message, dest)); }
        try
        {
            var probe = Path.Combine(dest, ".wmt-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x"); File.Delete(probe);
        }
        catch (Exception ex) { issues.Add(new Issue(IssueSeverity.Blocker, "Destination", "Cannot write to the destination: " + ex.Message, dest)); }
        if (dest.StartsWith(@"\\")) issues.Add(new Issue(IssueSeverity.Info, "Destination", "Network (UNC) destination: supported; copying is slower and depends on the network connection.", dest));
        return issues;
    }

    public BackupResult Create(BackupInputs input, string destination, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var plan = input.Plan;
        var blockers = ValidateDestination(destination, plan).Where(i => i.Severity == IssueSeverity.Blocker).ToList();
        if (blockers.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, blockers.Select(b => b.Message)));
        var dest = PathUtil.Canonical(destination);
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = PackageLayout.NewName(Environment.MachineName, DateTime.UtcNow, id);
        var layout = new PackageLayout(Path.Combine(dest, name));
        Directory.CreateDirectory(layout.Root);
        Directory.CreateDirectory(layout.Manifests); Directory.CreateDirectory(layout.Reports); Directory.CreateDirectory(layout.Logs); Directory.CreateDirectory(layout.Data);
        var pkgLog = new ActivityLog(layout.Log);
        log.Mirror = pkgLog;
        try
        {
            var disk = DiskInfo.Compare(dest);
            var info = new PackageInfo { PackageId = id, DestinationDiskCheck = disk.Detail, SamePhysicalDiskAsWindows = disk.SamePhysicalDisk };
            info.BackupRuns.Add($"{DateTime.UtcNow:o} create");
            log.Info("backup.start", $"Package {layout.Root}; disk check: {disk.Detail}");

            var files = plan.Index.Files.Where(f => f.Selected).Select(Clone).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                f.PackagePath = PathUtil.ToPackageDataPath(f.SourcePath);
                if (!used.Add(f.PackagePath)) throw new InvalidOperationException("Two files map to the same package path: " + f.PackagePath);
                f.CopyStatus = FileCopyStatus.Pending;
            }
            var dirs = plan.Index.Directories.Where(d => d.Included).Select(d => new DirectoryRecord
            {
                Path = d.Path, Category = d.Category, RootPath = d.RootPath, ProfileSid = d.ProfileSid, State = d.State, Note = d.Note,
                InApplicationRoot = d.InApplicationRoot, Included = true, PackagePath = PathUtil.ToPackageDataPath(d.Path)
            }).ToList();

            WriteInventory(layout, input, info, files, dirs);
            var result = CopyAll(layout, files, progress, ct);
            foreach (var d in dirs) Directory.CreateDirectory(PathUtil.SafeCombine(layout.Root, d.PackagePath!));
            Finalize(layout, input, info, files, dirs, result);
            return result;
        }
        finally { log.Mirror = null; }
    }

    /// <summary>BKP-003: processes only Failed/Pending entries of an existing package; verified copies are never rewritten.</summary>
    public BackupResult RetryFailed(string packageRoot, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var layout = new PackageLayout(packageRoot);
        var info = Json.ReadFile<PackageInfo>(layout.PackageJson);
        var files = Json.ReadFile<List<FileEntry>>(layout.FilesJson);
        var dirs = File.Exists(layout.DirectoriesJson) ? Json.ReadFile<List<DirectoryRecord>>(layout.DirectoriesJson) : new();
        var pkgLog = new ActivityLog(layout.Log);
        log.Mirror = pkgLog;
        try
        {
            info.BackupRuns.Add($"{DateTime.UtcNow:o} retry-failed");
            info.PackageStatus = "InProgress";
            Json.WriteFile(layout.PackageJson, info);
            var result = CopyAll(layout, files, progress, ct);
            foreach (var d in dirs.Where(d => d.Included && d.PackagePath != null)) Directory.CreateDirectory(PathUtil.SafeCombine(layout.Root, d.PackagePath!));
            var plan = File.Exists(layout.M("Plan.json")) ? Json.ReadFile<MigrationPlan>(layout.M("Plan.json")) : new MigrationPlan();
            var input = new BackupInputs
            {
                Plan = plan,
                AllSoftware = File.Exists(layout.M("Software.json")) ? Json.ReadFile<List<SoftwareItem>>(layout.M("Software.json")) : new(),
                AllProfiles = File.Exists(layout.M("Profiles.json")) ? Json.ReadFile<List<UserProfileInfo>>(layout.M("Profiles.json")) : new()
            };
            Finalize(layout, input, info, files, dirs, result, rewriteInventory: false);
            return result;
        }
        finally { log.Mirror = null; }
    }

    static FileEntry Clone(FileEntry f) => Json.Deserialize<FileEntry>(Json.Serialize(f))!;

    void WriteInventory(PackageLayout l, BackupInputs input, PackageInfo info, List<FileEntry> files, List<DirectoryRecord> dirs)
    {
        var plan = input.Plan;
        info.SelectedFileCount = files.Count;
        info.SelectedBytes = files.Sum(f => f.Size);
        info.IncludedEmptyFolders = dirs.Count;
        Json.WriteFile(l.PackageJson, info);
        Json.WriteFile(l.M("Software.json"), input.AllSoftware);
        Json.WriteFile(l.M("Profiles.json"), plan.SelectedProfiles);
        Json.WriteFile(l.M("Plan.json"), plan);
        Json.WriteFile(l.M("Dependencies.json"), plan.Dependencies);
        Json.WriteFile(l.M("Office-Environment.json"), plan.Office ?? OfficeEnvironment.Absent());
        Json.WriteFile(l.M("Python-Environments.json"), plan.Python ?? new PythonInventory());
        Json.WriteFile(l.M("Windows-Settings.json"), plan.WindowsSettings ?? new WindowsSettingsSnapshot());
        Json.WriteFile(l.M("Keyboard-Layouts.json"), plan.Keyboard ?? new KeyboardSnapshot());
        Json.WriteFile(l.M("VBA-Analysis.json"), plan.VbaAnalyses);
        Json.WriteFile(Path.Combine(l.Manifests, "Registry-REVIEW-ONLY", "Registry.json"), plan.Registry);
        if (input.WingetExportJson != null) Json.WriteText(l.M("Winget-Export.json"), input.WingetExportJson);
        Json.WriteFile(l.FilesJson, files);
        Json.WriteFile(l.DirectoriesJson, dirs);
        var pyDir = Path.Combine(l.Reports, "Python");
        foreach (var env in plan.Python?.Environments ?? new())
        {
            if (env.Freeze == null) continue;
            Json.WriteText(Path.Combine(pyDir, PythonReqFileName(env)), env.Freeze);
        }
    }

    public static string PythonReqFileName(PythonEnvironmentInfo env)
    {
        var baseName = new string((Path.GetFileName(env.ProjectPath ?? env.RootPath) ?? "python").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return $"{baseName}-{env.Id}-requirements.txt";
    }

    BackupResult CopyAll(PackageLayout l, List<FileEntry> files, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var r = new BackupResult { PackagePath = l.Root, Selected = files.Count };
        long totalBytes = files.Where(f => f.CopyStatus != FileCopyStatus.Copied).Sum(f => f.Size), doneBytes = 0;
        int i = 0, todo = files.Count(f => f.CopyStatus != FileCopyStatus.Copied);
        try
        {
            foreach (var f in files)
            {
                if (f.CopyStatus == FileCopyStatus.Copied) continue;
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo($"Copying {PathUtil.FormatBytes(doneBytes)} of {PathUtil.FormatBytes(totalBytes)}", i, todo, f.SourcePath));
                CopyOne(l, f, ct);
                if (f.CopyStatus == FileCopyStatus.Failed && f.CopyError?.StartsWith("DISK FULL") == true)
                {
                    r.Issues.Add(new Issue(IssueSeverity.Blocker, "Backup", "The destination disk is full. The backup stopped; free space and use Retry failed files.", l.Root));
                    break;
                }
                doneBytes += f.Size; i++;
            }
        }
        catch (OperationCanceledException) { r.Cancelled = true; log.Warn("backup.cancelled", "Backup cancelled; package marked Incomplete."); }
        r.Copied = files.Count(f => f.CopyStatus == FileCopyStatus.Copied);
        r.Failed = files.Count(f => f.CopyStatus == FileCopyStatus.Failed);
        r.Skipped = files.Count(f => f.CopyStatus is FileCopyStatus.Pending or FileCopyStatus.Skipped);
        r.ChangedSinceScan = files.Count(f => f.ChangedSinceScan);
        r.Status = r.Copied == files.Count ? "Complete" : "Incomplete";
        foreach (var f in files.Where(f => f.CopyStatus == FileCopyStatus.Failed))
            r.Issues.Add(new Issue(IssueSeverity.Error, "Backup", "Not copied: " + f.CopyError, f.SourcePath, "Close the program using it and use Retry failed files."));
        foreach (var f in files.Where(f => f.ChangedSinceScan && f.CopyStatus == FileCopyStatus.Copied))
            r.Issues.Add(new Issue(IssueSeverity.Warning, "Backup", "File changed after the scan; the current version was copied and hashed.", f.SourcePath));
        return r;
    }

    void CopyOne(PackageLayout l, FileEntry f, CancellationToken ct)
    {
        var target = PathUtil.SafeCombine(l.Root, f.PackagePath!);
        var partial = target + ".partial";
        var src = new FileInfo(f.SourcePath);
        if (!src.Exists) { Fail(f, "Source file no longer exists"); return; }
        if (src.Length != f.Size || Math.Abs((src.LastWriteTimeUtc - f.LastWriteUtc).TotalSeconds) > 2) f.ChangedSinceScan = true;
        if (File.Exists(target))
        {
            // A file from an earlier interrupted run: keep it only if it matches the current source exactly.
            try
            {
                var existing = Hashing.Sha256File(target, ct);
                var current = Hashing.Sha256File(f.SourcePath, ct);
                if (existing == current) { Succeed(f, existing, new FileInfo(f.SourcePath)); return; }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            Fail(f, "A different file already exists at the package path; it was not overwritten.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? lastError = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            f.Attempts++;
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
                var before = new FileInfo(f.SourcePath);
                if (!before.Exists) { Fail(f, "Source file disappeared during backup"); return; }
                string srcHash;
                using (var input = new FileStream(f.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buf = new byte[1 << 20]; int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); sha.AppendData(buf, 0, n); output.Write(buf, 0, n); }
                    output.Flush(true);
                    srcHash = Convert.ToHexString(sha.GetHashAndReset());
                }
                var after = new FileInfo(f.SourcePath);
                if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
                    throw new IOException("Source changed while it was being copied");
                AfterPartialWritten?.Invoke(f, partial);
                var copyHash = Hashing.Sha256File(partial, ct);
                if (!copyHash.Equals(srcHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"SHA-256 mismatch after copy (source {srcHash[..12]}…, copy {copyHash[..12]}…)");
                File.SetLastWriteTimeUtc(partial, after.LastWriteTimeUtc);
                try { File.SetCreationTimeUtc(partial, after.CreationTimeUtc); } catch { }
                File.Move(partial, target, overwrite: false);
                Succeed(f, srcHash, after);
                if (attempt > 1) log.Info("backup.retry_ok", $"Copied on attempt {attempt}: {f.SourcePath}");
                return;
            }
            catch (OperationCanceledException) { TryDelete(partial); throw; }
            catch (IOException ex) when (IsDiskFull(ex)) { TryDelete(partial); Fail(f, "DISK FULL: " + ex.Message); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(partial);
                lastError = (IsSharingViolation(ex) ? "File is locked by another program: " : "") + ex.Message;
                log.Warn("backup.attempt_failed", $"Attempt {attempt}/{MaxAttempts} failed for {f.SourcePath}: {lastError}");
                if (attempt < MaxAttempts) Thread.Sleep(RetryDelay * attempt);
            }
        }
        Fail(f, lastError ?? "Unknown error");
    }

    static void Succeed(FileEntry f, string hash, FileInfo src)
    {
        f.Sha256 = hash; f.CopyStatus = FileCopyStatus.Copied; f.CopyError = null; f.State = LifecycleState.Copied; f.CopiedUtc = DateTime.UtcNow;
        f.Size = src.Length; f.LastWriteUtc = src.LastWriteTimeUtc;
    }

    void Fail(FileEntry f, string error)
    {
        f.CopyStatus = FileCopyStatus.Failed; f.CopyError = error; f.Sha256 = null; f.State = LifecycleState.Planned;
        log.Error("backup.file_failed", $"{f.SourcePath}: {error}");
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;
    static bool IsSharingViolation(Exception ex) => (ex.HResult & 0xFFFF) is 0x20 or 0x21;

    void Finalize(PackageLayout l, BackupInputs input, PackageInfo info, List<FileEntry> files, List<DirectoryRecord> dirs, BackupResult r, bool rewriteInventory = true)
    {
        info.CopiedFileCount = r.Copied; info.FailedFileCount = r.Failed; info.ChangedSinceScanCount = r.ChangedSinceScan;
        info.SelectedFileCount = files.Count; info.SelectedBytes = files.Sum(f => f.Size);
        info.PackageStatus = r.Status;
        info.CompletedUtc = DateTime.UtcNow;
        Json.WriteFile(l.FilesJson, files);
        Json.WriteFile(l.DirectoriesJson, dirs);
        Json.WriteFile(l.PackageJson, info);
        var issues = input.Plan.Issues.Concat(r.Issues).ToList();
        Json.WriteFile(Path.Combine(l.Reports, "Issues.json"), issues);
        var pkg = LoadedPackage.Load(l.Root);
        ReportWriter.WritePackageReports(pkg, input.Plan, r, issues);
        log.Info("backup.finished", $"Status {r.Status}: {r.Copied} copied, {r.Failed} failed, {r.Skipped} not processed, {r.ChangedSinceScan} changed since scan");
        log.Mirror = null;
        WriteMetadataHashes(l);
    }

    /// <summary>Written last; Verify never rewrites it.</summary>
    public static void WriteMetadataHashes(PackageLayout l)
    {
        var mh = new MetadataHashes();
        foreach (var rel in EnumerateMetadataFiles(l))
            mh.Files[rel] = Hashing.Sha256File(Path.Combine(l.Root, rel));
        Json.WriteFile(l.MetadataHashesJson, mh);
    }

    public static IEnumerable<string> EnumerateMetadataFiles(PackageLayout l)
    {
        foreach (var folder in PackageLayout.MetadataFolders)
        {
            var dir = Path.Combine(l.Root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(l.Root, f);
                if (rel.Equals(Path.GetRelativePath(l.Root, l.MetadataHashesJson), StringComparison.OrdinalIgnoreCase)) continue;
                yield return rel;
            }
        }
        foreach (var name in PackageLayout.MetadataRootFiles)
            if (File.Exists(Path.Combine(l.Root, name))) yield return name;
    }
}
