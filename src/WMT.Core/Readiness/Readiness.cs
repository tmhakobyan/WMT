using System.Text.RegularExpressions;

namespace WMT.Core;

public sealed class ReadinessItem
{
    public string Key { get; set; } = "";
    public string Area { get; set; } = "";
    public string Name { get; set; } = "";
    public ReadinessState State { get; set; }
    public string Reason { get; set; } = "";
    public string Action { get; set; } = "";
    /// <summary>True when the user can resolve this item by an explicit confirmation (e.g. "I have the Office media").</summary>
    public bool Confirmable { get; set; }
    public string ConfirmLabel { get; set; } = "";
    public bool ConfirmedByUser { get; set; }
}

public sealed class ReadinessReport
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string PackagePath { get; set; } = "";
    public bool MigrationReady { get; set; }
    public string Overall { get; set; } = "";
    public List<ReadinessItem> Items { get; set; } = new();
}

public sealed class Confirmations
{
    public HashSet<string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>RDY-001/002, NFR-009: "Migration Ready" only when every item is Ready; incomplete information never shows Ready.</summary>
public static class ReadinessEvaluator
{
    public static ReadinessReport Evaluate(LoadedPackage pkg, VerifyResult? verify, TestRestoreResult? test, Confirmations conf)
    {
        var rep = new ReadinessReport { PackagePath = pkg.Layout.Root };
        var fp = pkg.MetadataFingerprint();
        void Add(ReadinessItem i)
        {
            if (i.Confirmable && conf.Keys.Contains(i.Key) && i.State != ReadinessState.Ready)
            { i.ConfirmedByUser = true; i.Reason += " — confirmed by user"; i.State = ReadinessState.Ready; }
            rep.Items.Add(i);
        }

        Add(new ReadinessItem
        {
            Key = "package.complete", Area = "Backup", Name = "All selected files copied",
            State = pkg.Info.PackageStatus == "Complete" ? ReadinessState.Ready : ReadinessState.Blocked,
            Reason = $"Package status {pkg.Info.PackageStatus}: {pkg.Info.CopiedFileCount}/{pkg.Info.SelectedFileCount} copied, {pkg.Info.FailedFileCount} failed",
            Action = pkg.Info.PackageStatus == "Complete" ? "" : "Use 'Retry failed files', then Verify again"
        });
        Add(new ReadinessItem
        {
            Key = "verify", Area = "Backup", Name = "Backup verified (SHA-256)",
            State = verify == null ? ReadinessState.NotTested : verify.Passed && verify.MetadataFingerprint == fp ? ReadinessState.Ready : ReadinessState.Blocked,
            Reason = verify == null ? "Verify has not been run" : verify.MetadataFingerprint != fp ? "The package changed after the last Verify" : verify.Passed ? $"Verified {verify.FilesChecked} files on {verify.CheckedUtc:u}" : $"Verify FAILED with {verify.Errors.Count} error(s)",
            Action = verify?.Passed == true && verify.MetadataFingerprint == fp ? "" : "Run Verify backup"
        });
        Add(new ReadinessItem
        {
            Key = "testrestore", Area = "Backup", Name = "Test restore in a sandbox",
            State = test == null ? ReadinessState.NotTested : test.Passed && test.MetadataFingerprint == fp ? ReadinessState.Ready : ReadinessState.Blocked,
            Reason = test == null ? "Test restore has not been run" : test.MetadataFingerprint != fp ? "The package changed after the last test restore" : test.Passed ? $"{test.Restored} files restored byte-identical" : $"Test restore FAILED ({test.Failed} failures)",
            Action = test?.Passed == true && test.MetadataFingerprint == fp ? "" : "Run Test restore into an empty folder"
        });
        var same = pkg.Info.SamePhysicalDiskAsWindows;
        Add(new ReadinessItem
        {
            Key = "independent.copy", Area = "Backup", Name = "Independent copy on another physical device",
            State = ReadinessState.Blocked, Confirmable = true, ConfirmLabel = "I have copied this package to another physical drive/device",
            Reason = same == true ? "This package is on the SAME physical disk as Windows (NFR-009)" : same == false ? pkg.Info.DestinationDiskCheck ?? "Different disk" : "Disk could not be determined",
            Action = "Copy the whole package folder to an external drive or another computer, then confirm"
        });

        foreach (var c in pkg.Components.Where(c => c.MigrationMethod == MigrationMethod.Reinstall))
        {
            bool office = c.Id == "office-product";
            bool known = c.OfficialSource != null;
            Add(new ReadinessItem
            {
                Key = "installer." + c.Id, Area = "Installers", Name = c.Name,
                State = office ? ReadinessState.Blocked : known ? ReadinessState.Ready : ReadinessState.NeedsReview,
                Confirmable = !known || office,
                ConfirmLabel = office ? "I have the Office installation media and license" : "I have the installer for this program",
                Reason = office ? "Office media/license are never exported (OFF-009)" : known ? "Official source: " + c.OfficialSource : "No official installer source is known",
                Action = office ? "Make sure you can install the same Office release and architecture" : known ? "" : "Find the installer before wiping Windows 10"
            });
        }

        foreach (var l in pkg.Keyboard?.Layouts.Where(l => l.Selected && l.IsCustom) ?? Enumerable.Empty<KeyboardLayoutInfo>())
        {
            bool installer = KeyboardRestore.InstallerInPackage(pkg, l) != null;
            bool dll = KeyboardRestore.DllsInPackage(pkg, l).Dll64 != null;
            bool klc = KeyboardRestore.KlcInPackage(pkg, l) != null;
            var st = installer || dll ? ReadinessState.Ready : klc ? ReadinessState.NeedsReview : ReadinessState.Blocked;
            Add(new ReadinessItem
            {
                Key = "keyboard." + l.Klid, Area = "Keyboard layouts", Name = $"{l.LayoutText} ({l.Klid})", State = st,
                Confirmable = st != ReadinessState.Ready, ConfirmLabel = "I can rebuild this layout with MSKLC from its .klc / I accept losing it",
                Reason = installer ? "Installer is in the package" : dll ? "Layout DLL is in the package: WMT installs it directly" + (klc ? " (.klc also saved)" : "") :
                         klc ? "Only a .klc source is in the package: needs MSKLC on the new system" : "No installer, DLL or .klc source in the package",
                Action = installer || dll ? "" : klc ? "Install MSKLC on the new Windows (WMT then builds and installs the layout)" : "MSKLC → File → Load Existing Keyboard → Save As .klc; then back up again"
            });
        }

        foreach (var env in pkg.Python?.Environments ?? new())
        {
            var st = env.Overall switch { CaptureStatus.Captured => ReadinessState.Ready, CaptureStatus.Partial => ReadinessState.NeedsReview, _ => ReadinessState.Blocked };
            Add(new ReadinessItem
            {
                Key = "python." + env.Id, Area = "Python", Name = $"{env.Kind}: {env.RootPath} (Python {env.Version})",
                State = st, Confirmable = st != ReadinessState.Ready, ConfirmLabel = "This environment is not critical / I documented it manually",
                Reason = $"Package capture {env.Overall}" + (env.Errors.Count > 0 ? ": " + env.Errors[0] : "") + (env.NonReinstallablePackages.Count > 0 ? $"; {env.NonReinstallablePackages.Count} package(s) not reinstallable from PyPI" : ""),
                Action = st == ReadinessState.Ready ? "" : "Fix the environment and rebuild the plan, or document packages manually"
            });
        }

        foreach (var g in pkg.Issues.Where(i => i.Severity is IssueSeverity.Warning or IssueSeverity.Error or IssueSeverity.Blocker).GroupBy(i => i.Area))
        {
            bool addin = g.Key == "Word" && g.Any(i => Regex.IsMatch(i.Message, "cannot load|will not compile|Architecture mismatch", RegexOptions.IgnoreCase));
            var worst = g.Max(i => i.Severity);
            Add(new ReadinessItem
            {
                Key = "issues." + g.Key, Area = g.Key, Name = $"{g.Count()} issue(s) in {g.Key}",
                State = addin || worst == IssueSeverity.Blocker ? ReadinessState.Blocked : ReadinessState.NeedsReview,
                Confirmable = true, ConfirmLabel = addin ? "I resolved the add-in incompatibilities" : "I reviewed these issues",
                Reason = string.Join(" | ", g.Take(3).Select(i => i.Message)) + (g.Count() > 3 ? " …" : ""),
                Action = "See Reports\\Issues.json"
            });
        }

        foreach (var g in pkg.Dependencies.Where(d => d.Confidence == Confidence.Unknown).GroupBy(d => d.Application))
            Add(new ReadinessItem
            {
                Key = "deps." + g.Key, Area = "Dependencies", Name = $"{g.Count()} unknown dependencies of {g.Key}",
                State = ReadinessState.NeedsReview, Confirmable = true, ConfirmLabel = "I checked these dependencies",
                Reason = string.Join(", ", g.Take(5).Select(d => d.DependsOn)) + (g.Count() > 5 ? " …" : ""),
                Action = "Check each one; install what is needed"
            });

        int blocked = rep.Items.Count(i => i.State == ReadinessState.Blocked), notTested = rep.Items.Count(i => i.State == ReadinessState.NotTested), review = rep.Items.Count(i => i.State == ReadinessState.NeedsReview);
        rep.MigrationReady = blocked == 0 && notTested == 0 && review == 0;
        rep.Overall = rep.MigrationReady ? "Migration Ready" : $"NOT READY — {blocked} blocked, {notTested} not tested, {review} need review";
        return rep;
    }
}

/// <summary>RST-002: checks on the TARGET computer before a real restore.</summary>
public static class PrerequisiteChecker
{
    public static List<Issue> Check(LoadedPackage pkg, IReadOnlyList<SoftwareItem> current, OfficeEnvironment currentOffice, RestoreRequest req, VerifyResult? verify)
    {
        var list = new List<Issue>();
        var fp = pkg.MetadataFingerprint();
        if (verify == null || !verify.Passed || verify.MetadataFingerprint != fp)
            list.Add(new Issue(IssueSeverity.Blocker, "Package", "This package has not passed Verify in its current state.", pkg.Layout.Root, "Run Verify backup first."));
        foreach (var p in pkg.Profiles)
        {
            bool hasFiles = pkg.Files.Any(f => f.Selected && f.ProfileSid == p.Sid);
            if (!hasFiles) continue;
            if (!req.ProfileMap.TryGetValue(p.Sid, out var t) || string.IsNullOrWhiteSpace(t))
                list.Add(new Issue(IssueSeverity.Warning, "Profiles", $"No target profile chosen for {p.Account}; its files will be skipped.", null, "Choose a target profile."));
            else if (!Directory.Exists(t))
                list.Add(new Issue(IssueSeverity.Blocker, "Profiles", $"Target profile folder does not exist: {t}", t, "Sign in once as that user so Windows creates the profile."));
        }
        if (pkg.Office?.Installed == true && pkg.Files.Any(f => f.Category.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase)))
        {
            var c = OfficeFingerprint.Compare(pkg.Office, currentOffice);
            list.Add(new Issue(c.Level == CompatLevel.Blocked ? IssueSeverity.Blocker : c.Level == CompatLevel.Warning ? IssueSeverity.Warning : IssueSeverity.Info, "Office", c.Message, null, string.Join(" ", c.Risks)));
        }
        static string Family(string n) => Regex.Replace(n, @"[\d\.\-]+|\(x\d+\)|x64|x86|\s+", " ").Trim().ToLowerInvariant();
        foreach (var comp in pkg.Components.Where(c => c.Kind == "Runtime"))
        {
            bool present = current.Any(s => s.Name.Equals(comp.Name, StringComparison.OrdinalIgnoreCase)) || current.Any(s => Family(s.Name) == Family(comp.Name) && s.Category == "Runtime");
            if (!present) list.Add(new Issue(IssueSeverity.Warning, "Runtimes", $"Not installed here: {comp.Name}", null, comp.OfficialSource != null ? "Install from " + comp.OfficialSource : "Install it from its vendor"));
        }
        foreach (var env in (pkg.Python?.Environments ?? new()).Where(e => e.Kind != "Interpreter"))
        {
            var v = env.Version;
            if (v == SoftwareItem.UnknownText) { list.Add(new Issue(IssueSeverity.Warning, "Python", $"Python version of {env.RootPath} is unknown.", env.RootPath)); continue; }
            bool present = current.Any(s => s.Category == "Python" && s.Name.Contains(v, StringComparison.OrdinalIgnoreCase));
            if (!present) list.Add(new Issue(IssueSeverity.Warning, "Python", $"Python {v} ({env.Architecture}) is not installed here; required for {env.RootPath}.", null,
                "Install from " + (PythonProvider.PythonInstallerUrl(v, env.Architecture) ?? "python.org")));
        }
        foreach (var s in pkg.Software.Where(s => s.Selected && s.Category is not ("Runtime" or "Python")))
        {
            bool present = current.Any(c => c.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) || current.Any(c => Family(c.Name) == Family(s.Name));
            if (!present) list.Add(new Issue(s.Category == "Office" ? IssueSeverity.Warning : IssueSeverity.Info, "Programs", $"Not installed here: {s.Name}", null, "Install it before restoring its settings."));
        }
        return list;
    }
}

public sealed class PythonRestoreStep
{
    public string Description { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public string CommandLine => "\"" + FileName + "\" " + string.Join(' ', Arguments.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
}

/// <summary>PY-007: exact recreation steps; executed only after explicit confirmation in the GUI.</summary>
public static class PythonRestorePlanner
{
    public static string MapPath(LoadedPackage pkg, string path, string? sid, RestoreRequest req)
    {
        var sp = pkg.Profiles.FirstOrDefault(p => p.Sid == sid);
        if (sp != null && req.ProfileMap.TryGetValue(sp.Sid, out var tp) && PathUtil.IsInsideOrSame(path, sp.ProfilePath))
            return Path.Combine(tp, Path.GetRelativePath(sp.ProfilePath, path));
        var drive = Path.GetPathRoot(path)?.TrimEnd('\\');
        if (drive != null && req.DriveMap.TryGetValue(drive, out var nd)) return nd.TrimEnd('\\') + path.Substring(drive.Length);
        return path;
    }

    public static List<PythonRestoreStep> Steps(LoadedPackage pkg, PythonEnvironmentInfo env, string targetEnvPath)
    {
        var steps = new List<PythonRestoreStep>();
        var m = Regex.Match(env.Version, @"^(\d+)\.(\d+)");
        if (!m.Success) return steps;
        var tag = $"-{m.Groups[1].Value}.{m.Groups[2].Value}" + (env.Architecture == "x86" ? "-32" : "");
        steps.Add(new PythonRestoreStep { Description = $"Check that Python {env.Version} is installed", FileName = "py", Arguments = { tag, "--version" } });
        steps.Add(new PythonRestoreStep { Description = "Create the virtual environment", FileName = "py", Arguments = { tag, "-m", "venv", targetEnvPath } });
        var req = Path.Combine(pkg.Layout.Reports, "Python", BackupEngine.PythonReqFileName(env));
        if (File.Exists(req))
        {
            var py = Path.Combine(targetEnvPath, "Scripts", "python.exe");
            steps.Add(new PythonRestoreStep { Description = "Upgrade pip inside the new environment", FileName = py, Arguments = { "-m", "pip", "install", "--upgrade", "pip" } });
            steps.Add(new PythonRestoreStep { Description = "Install the captured packages from PyPI (official index)", FileName = py, Arguments = { "-m", "pip", "install", "-r", req } });
            steps.Add(new PythonRestoreStep { Description = "Compare installed packages with the captured list", FileName = py, Arguments = { "-m", "pip", "freeze", "--all" } });
        }
        return steps;
    }
}
