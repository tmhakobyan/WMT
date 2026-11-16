namespace WMT.Core;

public enum RestoreAction { Create, SkipIdentical, Overwrite, SkipExisting, Unmapped, Blocked }

public sealed class RestoreOp
{
    public string FileId { get; set; } = "";
    public string Category { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public string SourceOriginal { get; set; } = "";
    public string? Target { get; set; }
    public RestoreAction Action { get; set; }
    public string Reason { get; set; } = "";
    public string? Sha256 { get; set; }
    public bool Selected { get; set; } = true;
}

public sealed class RegistryOp
{
    public string RecordId { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string Type { get; set; } = "";
    public string? OldValue { get; set; }
    public string? OldType { get; set; }
    public string? NewValue { get; set; }
    public List<string>? NewMulti { get; set; }
    public bool Remapped { get; set; }
    public string Warning { get; set; } = "";
    /// <summary>Registry writes default to OFF; each value must be ticked individually (REG-003, RST-004).</summary>
    public bool Selected { get; set; }
    public bool Blocked { get; set; }
}

public sealed class RestoreRequest
{
    public required string PackageRoot { get; init; }
    /// <summary>USR-006: source SID → target profile path chosen by the user. Never filled automatically.</summary>
    public Dictionary<string, string> ProfileMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Optional drive remapping for non-profile paths, e.g. "D:" → "E:".</summary>
    public Dictionary<string, string> DriveMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowOverwrite { get; init; }
    /// <summary>Target profile that equals the currently signed-in user (only that user's registry can be written).</summary>
    public string? CurrentUserProfilePath { get; init; }
}

public sealed class RestorePlan
{
    public List<RestoreOp> Files { get; set; } = new();
    public List<RegistryOp> Registry { get; set; } = new();
    public List<Issue> Prerequisites { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int Count(RestoreAction a) => Files.Count(f => f.Action == a);
}

public sealed class JournalEntry
{
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string? BackupOfPrevious { get; set; }
    public string? RestoredSha256 { get; set; }
    public string? RegKey { get; set; }
    public string? RegName { get; set; }
    public bool RegExisted { get; set; }
    public string? RegOldType { get; set; }
    public string? RegOldData { get; set; }
    public List<string>? RegOldMulti { get; set; }
    public DateTime Utc { get; set; } = DateTime.UtcNow;
}

public sealed class RestoreJournal
{
    public string JournalId { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
    public string PackageRoot { get; set; } = "";
    public List<JournalEntry> Entries { get; set; } = new();
    public List<string> CreatedDirectories { get; set; } = new();
    public int Restored { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool UndoDone { get; set; }
}

/// <summary>RST-002..005 / REG-003: dry run first; default never overwrites; overwrites keep the previous file;
/// every change is journaled and can be undone.</summary>
public sealed class RestoreEngine
{
    readonly ActivityLog log;
    readonly IRegistryReader regReader;
    readonly IRegistryWriter regWriter;
    public RestoreEngine(ActivityLog log, IRegistryReader reader, IRegistryWriter writer) { this.log = log; regReader = reader; regWriter = writer; }

    static readonly string[] ProtectedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    };

    public RestorePlan DryRun(LoadedPackage pkg, RestoreRequest req)
    {
        var plan = new RestorePlan();
        var profiles = pkg.Profiles.ToDictionary(p => p.Sid, StringComparer.OrdinalIgnoreCase);
        foreach (var f in pkg.Files.Where(f => f.Selected && f.CopyStatus == FileCopyStatus.Copied))
        {
            var op = new RestoreOp { FileId = f.Id, Category = f.Category, PackagePath = f.PackagePath!, SourceOriginal = f.SourcePath, Sha256 = f.Sha256 };
            plan.Files.Add(op);
            string? target = null;
            if (f.ProfileSid != null && profiles.TryGetValue(f.ProfileSid, out var sp) && PathUtil.IsInsideOrSame(f.SourcePath, sp.ProfilePath))
            {
                if (!req.ProfileMap.TryGetValue(f.ProfileSid, out var tp) || string.IsNullOrWhiteSpace(tp))
                { op.Action = RestoreAction.Unmapped; op.Reason = $"Choose a target profile for {sp.Account}"; op.Selected = false; continue; }
                target = Path.Combine(tp, Path.GetRelativePath(sp.ProfilePath, f.SourcePath));
            }
            else
            {
                target = f.SourcePath;
                var drive = Path.GetPathRoot(target)?.TrimEnd('\\');
                if (drive != null && req.DriveMap.TryGetValue(drive, out var nd) && !string.IsNullOrWhiteSpace(nd))
                    target = nd.TrimEnd('\\') + target.Substring(drive.Length);
                var root = Path.GetPathRoot(target);
                if (string.IsNullOrEmpty(root) || (!root.StartsWith(@"\\") && !Directory.Exists(root)))
                { op.Action = RestoreAction.Blocked; op.Reason = $"Drive {root} does not exist; add a drive mapping"; op.Selected = false; op.Target = target; continue; }
            }
            op.Target = target;
            if (ProtectedRoots.Any(pr => !string.IsNullOrEmpty(pr) && PathUtil.IsInsideOrSame(target, pr)))
            { op.Action = RestoreAction.Blocked; op.Reason = "System/program folder: restore manually with the program's installer"; op.Selected = false; continue; }
            if (File.Exists(target))
            {
                string? h = null;
                try { h = Hashing.Sha256File(target); } catch { }
                if (h != null && h.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) { op.Action = RestoreAction.SkipIdentical; op.Reason = "Identical file already present"; op.Selected = false; }
                else if (req.AllowOverwrite) { op.Action = RestoreAction.Overwrite; op.Reason = "Existing file will be saved to the journal, then replaced"; }
                else { op.Action = RestoreAction.SkipExisting; op.Reason = "A different file exists; overwrite is off"; op.Selected = false; }
            }
            else { op.Action = RestoreAction.Create; op.Reason = "New file"; }
        }

        // Registry: only for the profile that is the current user on this computer.
        foreach (var rec in pkg.Registry)
        {
            var op = new RegistryOp { RecordId = rec.Id, KeyPath = rec.KeyPath, ValueName = rec.ValueName, Type = rec.Type, NewValue = rec.Data, NewMulti = rec.MultiData, Warning = rec.Note };
            string? targetProfile = rec.ProfileSid != null && req.ProfileMap.TryGetValue(rec.ProfileSid, out var tp) ? tp : null;
            if (targetProfile == null || req.CurrentUserProfilePath == null || !PathUtil.SamePath(targetProfile, req.CurrentUserProfilePath))
            { op.Blocked = true; op.Warning = "Only the signed-in user's registry can be changed: map this profile to the current user and sign in as that user."; }
            else if (rec.ProfileSid != null && profiles.TryGetValue(rec.ProfileSid, out var sp) && op.NewValue != null && op.NewValue.Contains(sp.ProfilePath, StringComparison.OrdinalIgnoreCase))
            { op.NewValue = op.NewValue.Replace(sp.ProfilePath, targetProfile, StringComparison.OrdinalIgnoreCase); op.Remapped = true; }
            var old = regReader.Value("HKCU", rec.KeyPath, rec.ValueName);
            op.OldValue = old?.AsString(); op.OldType = old?.Kind;
            if (old != null && old.Kind == rec.Type && (old.AsString() ?? "") == (op.NewValue ?? (op.NewMulti != null ? string.Join("; ", op.NewMulti) : "")))
            { op.Warning = "Same value already set"; op.Blocked = true; }
            if (rec.Type is "Binary" or "Unknown" or "None") { op.Blocked = true; op.Warning = "Binary values are review-only"; }
            plan.Registry.Add(op);
        }
        return plan;
    }

    public RestoreJournal Execute(LoadedPackage pkg, RestorePlan plan, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var journal = new RestoreJournal { PackageRoot = pkg.Layout.Root };
        var jdir = Path.Combine(pkg.Layout.Status, "Restore-" + journal.JournalId);
        Directory.CreateDirectory(jdir);
        var jpath = Path.Combine(jdir, "journal.json");
        int i = 0, total = plan.Files.Count(f => f.Selected) + plan.Registry.Count(r => r.Selected && !r.Blocked);
        try
        {
            foreach (var op in plan.Files.Where(f => f.Selected && f.Action is RestoreAction.Create or RestoreAction.Overwrite))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo("Restoring files", i++, total, op.Target));
                try
                {
                    var src = PathUtil.SafeCombine(pkg.Layout.Root, op.PackagePath);
                    var target = op.Target!;
                    var dir = Path.GetDirectoryName(target)!;
                    foreach (var d in MissingDirs(dir)) { Directory.CreateDirectory(d); journal.CreatedDirectories.Add(d); }
                    var entry = new JournalEntry { Kind = "File", Target = target };
                    if (File.Exists(target))
                    {
                        if (op.Action != RestoreAction.Overwrite) throw new IOException("Target appeared after the dry run; not overwritten");
                        var keep = Path.Combine(jdir, "Previous", (journal.Entries.Count + 1).ToString("D6") + "_" + Path.GetFileName(target));
                        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                        File.Copy(target, keep, overwrite: false);
                        entry.BackupOfPrevious = keep;
                    }
                    var tmp = target + ".wmt-restore";
                    File.Copy(src, tmp, overwrite: true);
                    var h = Hashing.Sha256File(tmp, ct);
                    if (!h.Equals(op.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(tmp); throw new IOException("SHA-256 mismatch while restoring"); }
                    File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src));
                    File.Move(tmp, target, overwrite: op.Action == RestoreAction.Overwrite);
                    entry.RestoredSha256 = h;
                    journal.Entries.Add(entry);
                    journal.Restored++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { journal.Failed++; journal.Errors.Add($"{op.Target}: {ex.Message}"); log.Error("restore.file_failed", $"{op.Target}: {ex.Message}"); }
                finally { Json.WriteFile(jpath, journal); }
            }
            foreach (var r in plan.Registry.Where(r => r.Selected && !r.Blocked))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo("Applying selected registry values", i++, total, r.KeyPath + "\\" + r.ValueName));
                try
                {
                    var old = regReader.Value("HKCU", r.KeyPath, r.ValueName);
                    var e = new JournalEntry { Kind = "Registry", RegKey = r.KeyPath, RegName = r.ValueName, RegExisted = old != null, RegOldType = old?.Kind };
                    if (old?.Data is string[] arr) e.RegOldMulti = arr.ToList(); else e.RegOldData = old == null ? null : RegistryText.ToText(old.Kind, old.Data);
                    journal.Entries.Add(e);
                    Json.WriteFile(jpath, journal);
                    regWriter.SetValue("HKCU", r.KeyPath, r.ValueName, r.Type, RegistryText.FromText(r.Type, r.NewValue, r.NewMulti));
                    journal.Restored++;
                    log.Info("restore.registry", $"Set HKCU\\{r.KeyPath}\\{r.ValueName}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { journal.Failed++; journal.Errors.Add($"HKCU\\{r.KeyPath}\\{r.ValueName}: {ex.Message}"); }
                finally { Json.WriteFile(jpath, journal); }
            }
        }
        finally { Json.WriteFile(jpath, journal); }
        return journal;
    }

    static IEnumerable<string> MissingDirs(string dir)
    {
        var list = new List<string>();
        var d = dir;
        while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) { list.Add(d); d = Path.GetDirectoryName(d); }
        list.Reverse();
        return list;
    }

    /// <summary>RST-004 / REG-003 rollback: restored files are removed only if unchanged since restore; previous versions come back.</summary>
    public List<string> Undo(RestoreJournal journal, string journalPath)
    {
        var problems = new List<string>();
        foreach (var e in Enumerable.Reverse(journal.Entries))
        {
            try
            {
                if (e.Kind == "File")
                {
                    if (File.Exists(e.Target))
                    {
                        var h = Hashing.Sha256File(e.Target);
                        if (!h.Equals(e.RestoredSha256, StringComparison.OrdinalIgnoreCase)) { problems.Add("Changed after restore, left as is: " + e.Target); continue; }
                        File.Delete(e.Target);
                    }
                    if (e.BackupOfPrevious != null && File.Exists(e.BackupOfPrevious)) File.Copy(e.BackupOfPrevious, e.Target, overwrite: false);
                }
                else if (e.Kind == "Registry")
                {
                    if (!e.RegExisted) regWriter.DeleteValue("HKCU", e.RegKey!, e.RegName!);
                    else regWriter.SetValue("HKCU", e.RegKey!, e.RegName!, e.RegOldType!, RegistryText.FromText(e.RegOldType!, e.RegOldData, e.RegOldMulti));
                }
            }
            catch (Exception ex) { problems.Add($"{e.Target}{e.RegKey}: {ex.Message}"); }
        }
        foreach (var d in Enumerable.Reverse(journal.CreatedDirectories))
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        journal.UndoDone = true;
        Json.WriteFile(journalPath, journal);
        return problems;
    }
}

public static class RegistryText
{
    public static string? ToText(string kind, object? data) => data switch
    {
        null => null,
        byte[] b => Convert.ToHexString(b),
        string[] a => string.Join("\n", a),
        _ => Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture)
    };

    public static object? FromText(string kind, string? text, List<string>? multi)
    {
        switch (kind)
        {
            case "DWord": return unchecked((int)long.Parse(string.IsNullOrEmpty(text) ? "0" : text, System.Globalization.CultureInfo.InvariantCulture));
            case "QWord": return long.Parse(string.IsNullOrEmpty(text) ? "0" : text, System.Globalization.CultureInfo.InvariantCulture);
            case "MultiString": return (multi ?? (text ?? "").Split('\n').ToList()).ToArray();
            case "Binary": return Convert.FromHexString(text ?? "");
            default: return text ?? "";
        }
    }
}
