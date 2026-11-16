using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using WMT.Core;

namespace WMT.App.ViewModels;

public sealed record PreviewRow(string Category, int Files, string Size, string Classes, string Destination);

public sealed partial class MainViewModel
{
    string destination = "", diskCheckText = "", backupStatus = "", packagePath = "", verifyStatus = "Not run", testStatus = "Not run", sandbox = "", readinessOverall = "Not evaluated";
    string restoreStatus = "", driveMap = "", previewText = "";
    bool allowOverwrite;
    RestorePlan? restorePlan;
    RestoreJournal? lastJournal; string? lastJournalPath;
    LoadedPackage? loadedPackage;

    public ObservableCollection<PreviewRow> PreviewRows { get; } = new();
    public ObservableCollection<Issue> PreviewIssues { get; } = new();
    public ObservableCollection<ReadinessRow> ReadinessRows { get; } = new();
    public ObservableCollection<ProfileMapRow> ProfileMapRows { get; } = new();
    public ObservableCollection<Issue> PrerequisiteIssues { get; } = new();
    public ObservableCollection<RestoreOp> RestoreFileOps { get; } = new();
    public ObservableCollection<RegistryOp> RestoreRegistryOps { get; } = new();
    public ObservableCollection<PythonEnvironmentInfo> PackagePythonEnvs { get; } = new();
    public ObservableCollection<CheckRow> FunctionalChecks { get; } = new();
    public PythonEnvironmentInfo? SelectedPythonEnv { get; set; }

    public RelayCommand BrowseDestinationCommand { get; private set; } = null!;
    public RelayCommand PreviewCommand { get; private set; } = null!;
    public RelayCommand CreateBackupCommand { get; private set; } = null!;
    public RelayCommand RetryFailedCommand { get; private set; } = null!;
    public RelayCommand OpenPackageCommand { get; private set; } = null!;
    public RelayCommand VerifyCommand { get; private set; } = null!;
    public RelayCommand BrowseSandboxCommand { get; private set; } = null!;
    public RelayCommand TestRestoreCommand { get; private set; } = null!;
    public RelayCommand ReadinessCommand { get; private set; } = null!;
    public RelayCommand ExportReadinessCommand { get; private set; } = null!;
    public RelayCommand OpenReportCommand { get; private set; } = null!;
    public RelayCommand PrereqCommand { get; private set; } = null!;
    public RelayCommand DryRunCommand { get; private set; } = null!;
    public RelayCommand RestoreCommand { get; private set; } = null!;
    public RelayCommand UndoRestoreCommand { get; private set; } = null!;
    public RelayCommand RecreatePythonCommand { get; private set; } = null!;
    public RelayCommand OpenLinkCommand { get; private set; } = null!;
    public RelayCommand SaveFunctionalCommand { get; private set; } = null!;

    void InitBackup()
    {
        BrowseDestinationCommand = new RelayCommand(() => { var p = PickFolder("Existing folder for the backup package (e.g. G:\\Backup)"); if (p != null) Destination = p; }, () => IsIdle);
        PreviewCommand = new RelayCommand(BuildPreview, () => IsIdle && plan != null);
        CreateBackupCommand = new RelayCommand(async () => await CreateBackupAsync(), () => IsIdle && plan != null && !string.IsNullOrWhiteSpace(Destination));
        RetryFailedCommand = new RelayCommand(async () => await RetryFailedAsync(), () => IsIdle && HasPackage);
        OpenPackageCommand = new RelayCommand(() => { var p = PickFolder("Open a migration package folder (Migration-…)"); if (p != null) LoadPackage(p); }, () => IsIdle);
        VerifyCommand = new RelayCommand(async () => await VerifyAsync(), () => IsIdle && HasPackage);
        BrowseSandboxCommand = new RelayCommand(() => { var p = PickFolder("Empty test folder for Test Restore"); if (p != null) Sandbox = p; }, () => IsIdle);
        TestRestoreCommand = new RelayCommand(async () => await TestRestoreAsync(), () => IsIdle && HasPackage && !string.IsNullOrWhiteSpace(Sandbox));
        ReadinessCommand = new RelayCommand(RefreshReadiness, () => IsIdle && HasPackage);
        ExportReadinessCommand = new RelayCommand(ExportReadiness, () => IsIdle && HasPackage);
        OpenReportCommand = new RelayCommand(p => OpenInPackage(p as string ?? "Reports\\Recovery-Plan.html"), _ => HasPackage);
        PrereqCommand = new RelayCommand(async () => await CheckPrerequisitesAsync(), () => IsIdle && HasPackage);
        DryRunCommand = new RelayCommand(async () => await DryRunAsync(), () => IsIdle && HasPackage);
        RestoreCommand = new RelayCommand(async () => await RestoreAsync(), () => IsIdle && restorePlan != null);
        UndoRestoreCommand = new RelayCommand(UndoRestore, () => IsIdle && lastJournal != null && !lastJournal.UndoDone);
        RecreatePythonCommand = new RelayCommand(async () => await RecreatePythonAsync(), () => IsIdle && SelectedPythonEnv != null && HasPackage);
        OpenLinkCommand = new RelayCommand(p => OpenLink(p as string), _ => true);
        SaveFunctionalCommand = new RelayCommand(SaveFunctional, () => HasPackage && FunctionalChecks.Count > 0);
    }

    public bool HasPackage => loadedPackage != null;
    public Task VerifyCommandExecuteAsync() => VerifyAsync();
    public Task TestRestoreCommandExecuteAsync() => TestRestoreAsync();
    public Task DryRunCommandExecuteAsync() => DryRunAsync();
    public string Destination { get => destination; set { if (Set(ref destination, value)) { DiskCheckText = ""; ClearBackupPreview(); } } }
    public string DiskCheckText { get => diskCheckText; private set => Set(ref diskCheckText, value); }
    public string BackupStatus { get => backupStatus; private set => Set(ref backupStatus, value); }
    public string PackagePath { get => packagePath; private set => Set(ref packagePath, value); }
    public string VerifyStatus { get => verifyStatus; private set => Set(ref verifyStatus, value); }
    public string TestStatus { get => testStatus; private set => Set(ref testStatus, value); }
    public string Sandbox { get => sandbox; set => Set(ref sandbox, value); }
    public string ReadinessOverall { get => readinessOverall; private set => Set(ref readinessOverall, value); }
    public string RestoreStatus { get => restoreStatus; private set => Set(ref restoreStatus, value); }
    public string DriveMap { get => driveMap; set => Set(ref driveMap, value); }
    public bool AllowOverwrite { get => allowOverwrite; set => Set(ref allowOverwrite, value); }
    public string PreviewText { get => previewText; private set => Set(ref previewText, value); }

    void ClearBackupPreview() { PreviewRows.Clear(); PreviewIssues.Clear(); PreviewText = ""; }

    /// <summary>WF-006 Dry run / Preview: counts, sizes, paths, dependencies, warnings, actions. Nothing is written.</summary>
    public void BuildPreview()
    {
        if (plan == null) return;
        ClearBackupPreview();
        foreach (var g in plan.Index.Files.Where(f => f.Selected).GroupBy(f => f.Category).OrderBy(g => g.Key))
            PreviewRows.Add(new PreviewRow(g.Key, g.Count(), PathUtil.FormatBytes(g.Sum(f => f.Size)),
                string.Join(", ", g.GroupBy(f => f.Classification).Select(c => $"{c.Key} {c.Count()}")), g.First().DestinationRule));
        var issues = new List<Issue>();
        if (!string.IsNullOrWhiteSpace(Destination))
        {
            issues.AddRange(BackupEngine.ValidateDestination(Destination, plan));
            var disk = DiskInfo.Compare(Destination);
            DiskCheckText = disk.Detail;
            if (disk.SamePhysicalDisk != false)
                issues.Add(new Issue(IssueSeverity.Warning, "Independence", "A backup on the same physical disk as Windows (for example another partition such as G:) is NOT an independent backup. Copy the package to another device before reinstalling Windows."));
        }
        else issues.Add(new Issue(IssueSeverity.Warning, "Destination", "Choose an existing destination folder."));
        if (keyboard == null) issues.Add(new Issue(IssueSeverity.Warning, "Keyboard layouts", "The Keyboard Layouts tab was not used: custom keyboard layouts are NOT in this backup."));
        if (settings == null) issues.Add(new Issue(IssueSeverity.Warning, "Windows settings", "The Windows Settings tab was not used: Windows settings are NOT in this backup."));
        if (PlanIsStale) issues.Add(new Issue(IssueSeverity.Warning, "Plan", "The selection changed after the plan was generated: generate the plan again."));
        issues.AddRange(plan.Issues.Where(i => i.Severity >= IssueSeverity.Warning));
        foreach (var i in issues.OrderByDescending(i => i.Severity)) PreviewIssues.Add(i);
        var (n, bytes, folders) = plan.SelectedTotals();
        var reinstall = plan.Components.Count(c => c.MigrationMethod == MigrationMethod.Reinstall);
        var manual = plan.Components.Count(c => c.ManualActions.Count > 0);
        PreviewText = $"Will copy {n:N0} files ({PathUtil.FormatBytes(bytes)}) and keep {folders} empty folders into {(string.IsNullOrWhiteSpace(Destination) ? "<destination>" : Destination)}\\Migration-{Environment.MachineName}-<UTC time>-<id>\\Data\\…\n" +
            $"Plus manifests and reports. {reinstall} items are recorded for reinstallation (not copied), {manual} need manual actions, " +
            $"{plan.Dependencies.Count} dependencies ({plan.Dependencies.Count(d => d.Confidence == Confidence.Unknown)} unknown), {plan.Registry.Count} registry values exported for review only.";
    }

    public async Task CreateBackupAsync()
    {
        if (plan == null) return;
        if (PlanIsStale && !Confirm("The selection changed after the plan was generated. Back up the OLD plan anyway?")) return;
        BuildPreview();
        var blockers = PreviewIssues.Where(i => i.Severity == IssueSeverity.Blocker).ToList();
        if (blockers.Count > 0) { Inform("Backup cannot start:\n\n" + string.Join("\n", blockers.Select(b => "• " + b.Message))); return; }
        var (n, bytes, _) = plan.SelectedTotals();
        if (!Confirm($"Create a backup of {n:N0} files ({PathUtil.FormatBytes(bytes)}) in\n{Destination}?\n\nSource files are only read, never changed.")) return;
        BackupResult? r = null;
        var dest = Destination; var p = plan; var sw = AllSoftware.ToList(); var profs = Profiles.Select(x => x.Item).ToList(); var wg = WingetExport;
        var jr = await RunJob("Create backup", job =>
        {
            r = new BackupEngine(Log).Create(new BackupInputs { Plan = p, AllSoftware = sw, AllProfiles = profs, WingetExportJson = wg }, dest, job.Progress, job.Token);
            if (r.Status != "Complete") job.Warnings.Add($"Backup is INCOMPLETE: {r.Failed} failed, {r.Skipped} not processed.");
            return Task.CompletedTask;
        });
        if (r == null) { BackupStatus = "Backup did not start or failed — see log"; return; }
        BackupStatus = $"{r.Status}: {r.Copied:N0} copied, {r.Failed} failed, {r.Skipped} not processed, {r.ChangedSinceScan} changed since scan.{(r.Cancelled ? " Cancelled by user." : "")} → {r.PackagePath}";
        LoadPackage(r.PackagePath);
    }

    async Task RetryFailedAsync()
    {
        if (loadedPackage == null) return;
        BackupResult? r = null; var root = loadedPackage.Layout.Root;
        var jr = await RunJob("Retry failed files", job => { r = new BackupEngine(Log).RetryFailed(root, job.Progress, job.Token); return Task.CompletedTask; });
        if (r != null) BackupStatus = $"Retry: {r.Status}: {r.Copied:N0} copied, {r.Failed} still failed.";
        LoadPackage(root);
    }

    public void LoadPackage(string path)
    {
        try
        {
            loadedPackage = LoadedPackage.Load(path);
            PackagePath = loadedPackage.Layout.Root;
            var v = PackageStatusStore.Latest<VerifyResult>(PackagePath, "Verify");
            var fp = loadedPackage.MetadataFingerprint();
            VerifyStatus = v == null ? "Not run" : (v.MetadataFingerprint != fp ? "Outdated (package changed since last Verify)" : $"{v.Status} on {v.CheckedUtc.ToLocalTime():g}: {v.FilesChecked:N0} files, {v.Errors.Count} errors");
            var t = PackageStatusStore.Latest<TestRestoreResult>(PackagePath, "TestRestore");
            TestStatus = t == null ? "Not run" : (t.MetadataFingerprint != fp ? "Outdated" : $"{(t.Passed ? "PASSED" : "FAILED")} on {t.CheckedUtc.ToLocalTime():g}: {t.Restored:N0} files");
            ProfileMapRows.Clear();
            var choices = Profiles.Select(p => p.Item.ProfilePath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (choices.Count == 0) { var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); if (!string.IsNullOrEmpty(me)) choices.Add(me); }
            choices.Insert(0, "");
            foreach (var p in loadedPackage.Profiles)
                ProfileMapRows.Add(new ProfileMapRow { Source = p, FileCount = loadedPackage.Files.Count(f => f.Selected && f.ProfileSid == p.Sid), Choices = choices, Target = null });
            PackagePythonEnvs.Clear();
            foreach (var e in loadedPackage.Python?.Environments.Where(e => e.Kind != "Interpreter") ?? Enumerable.Empty<PythonEnvironmentInfo>()) PackagePythonEnvs.Add(e);
            FunctionalChecks.Clear();
            foreach (var f in loadedPackage.FunctionalTests) FunctionalChecks.Add(new CheckRow { Text = f });
            restorePlan = null; RestoreFileOps.Clear(); RestoreRegistryOps.Clear(); PrerequisiteIssues.Clear();
            SettingActionRows.Clear(); RefreshKeyboardRestore();
            Raise(nameof(HasPackage));
            RefreshReadiness();
        }
        catch (Exception ex) { Inform("Cannot open package: " + ex.Message); }
    }

    async Task VerifyAsync()
    {
        if (loadedPackage == null) return;
        VerifyResult? v = null; var root = loadedPackage.Layout.Root;
        await RunJob("Verify backup", job =>
        {
            v = Verifier.Verify(root, job.Progress, job.Token);
            if (!v.Passed) job.Warnings.Add($"Verify FAILED with {v.Errors.Count} error(s).");
            return Task.CompletedTask;
        });
        if (v == null) return;
        var saved = PackageStatusStore.Save(root, "Verify", v);
        Json.WriteText(Path.ChangeExtension(saved, ".html"), ReportWriter.VerifyHtml(v));
        LoadPackage(root);
        if (!v.Passed) Inform("Verify FAILED:\n\n" + string.Join("\n", v.Errors.Take(15)) + (v.Errors.Count > 15 ? $"\n… and {v.Errors.Count - 15} more (see {saved})" : ""));
    }

    async Task TestRestoreAsync()
    {
        if (loadedPackage == null) return;
        var errors = TestRestore.ValidateSandbox(loadedPackage.Layout.Root, Sandbox);
        if (errors.Count > 0) { Inform(string.Join("\n", errors)); return; }
        TestRestoreResult? t = null; var root = loadedPackage.Layout.Root; var sb = Sandbox;
        await RunJob("Test restore (sandbox)", job => { t = TestRestore.Run(root, sb, job.Progress, job.Token); if (!t.Passed) job.Warnings.Add("Test restore FAILED"); return Task.CompletedTask; });
        if (t == null) return;
        PackageStatusStore.Save(root, "TestRestore", t);
        LoadPackage(root);
    }

    Confirmations CurrentConfirmations() => loadedPackage == null ? new() : PackageStatusStore.Latest<Confirmations>(loadedPackage.Layout.Root, "Confirmations") ?? new Confirmations();

    public void RefreshReadiness()
    {
        ReadinessRows.Clear();
        if (loadedPackage == null) { ReadinessOverall = "Not evaluated"; return; }
        var root = loadedPackage.Layout.Root;
        var rep = ReadinessEvaluator.Evaluate(loadedPackage, PackageStatusStore.Latest<VerifyResult>(root, "Verify"), PackageStatusStore.Latest<TestRestoreResult>(root, "TestRestore"), CurrentConfirmations());
        foreach (var i in rep.Items.OrderBy(i => i.State == ReadinessState.Ready ? 1 : 0).ThenBy(i => i.Area)) ReadinessRows.Add(new ReadinessRow(i, OnConfirm));
        ReadinessOverall = rep.Overall;
    }

    void OnConfirm(ReadinessRow row, bool value)
    {
        if (loadedPackage == null || !row.Item.Confirmable) return;
        if (value && !Confirm($"Confirm: \"{row.Item.ConfirmLabel}\"?\n\n{row.Item.Name}\n{row.Item.Reason}")) { row.Raise(nameof(ReadinessRow.Confirmed)); return; }
        var c = CurrentConfirmations();
        if (value) c.Keys.Add(row.Item.Key); else c.Keys.Remove(row.Item.Key);
        c.UpdatedUtc = DateTime.UtcNow;
        PackageStatusStore.Save(loadedPackage.Layout.Root, "Confirmations", c);
        Log.Info("readiness.confirm", $"{(value ? "Confirmed" : "Withdrawn")}: {row.Item.Key}");
        RefreshReadiness();
    }

    void ExportReadiness()
    {
        if (loadedPackage == null) return;
        RefreshReadiness();
        var root = loadedPackage.Layout.Root;
        var rep = ReadinessEvaluator.Evaluate(loadedPackage, PackageStatusStore.Latest<VerifyResult>(root, "Verify"), PackageStatusStore.Latest<TestRestoreResult>(root, "TestRestore"), CurrentConfirmations());
        var json = PackageStatusStore.Save(root, "Readiness", rep);
        var html = Path.ChangeExtension(json, ".html");
        Json.WriteText(html, ReportWriter.ReadinessHtml(rep));
        OpenLink(html);
    }

    void OpenInPackage(string rel)
    {
        if (loadedPackage == null) return;
        var full = Path.Combine(loadedPackage.Layout.Root, rel);
        if (File.Exists(full) || Directory.Exists(full)) OpenLink(full); else Inform("Not found: " + full);
    }

    public void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !Confirm("Open this official page in your browser?\n\n" + target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Inform("Cannot open: " + ex.Message); }
    }

    RestoreRequest BuildRequest()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ProfileMapRows) if (!string.IsNullOrWhiteSpace(r.Target)) map[r.Source.Sid] = r.Target!;
        var drives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in DriveMap.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && kv[0].Trim().Length >= 2 && kv[1].Trim().Length >= 2) drives[kv[0].Trim().TrimEnd('\\')[..2].ToUpperInvariant()] = kv[1].Trim().TrimEnd('\\')[..2].ToUpperInvariant();
        }
        return new RestoreRequest { PackageRoot = loadedPackage!.Layout.Root, ProfileMap = map, DriveMap = drives, AllowOverwrite = AllowOverwrite, CurrentUserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    }

    async Task CheckPrerequisitesAsync()
    {
        if (loadedPackage == null) return;
        var pkg = loadedPackage; var req = BuildRequest();
        List<Issue>? list = null;
        await RunJob("Check restore prerequisites", job =>
        {
            var current = new SoftwareDiscovery(new WindowsUninstallSource(Registry)).Scan(job.Progress, job.Token, includePortable: false).Items;
            var office = OfficeFingerprint.Read(Registry, current);
            list = PrerequisiteChecker.Check(pkg, current, office, req, PackageStatusStore.Latest<VerifyResult>(pkg.Layout.Root, "Verify"));
            return Task.CompletedTask;
        });
        PrerequisiteIssues.Clear();
        foreach (var i in (list ?? new()).OrderByDescending(i => i.Severity)) PrerequisiteIssues.Add(i);
        RestoreStatus = list == null ? "Check failed" : $"{list.Count(i => i.Severity == IssueSeverity.Blocker)} blocker(s), {list.Count(i => i.Severity == IssueSeverity.Warning)} warning(s).";
    }

    async Task DryRunAsync()
    {
        if (loadedPackage == null) return;
        var pkg = loadedPackage; var req = BuildRequest();
        RestorePlan? rp = null;
        await RunJob("Restore dry run", _ => { rp = new RestoreEngine(Log, Registry, RegistryWriter).DryRun(pkg, req); return Task.CompletedTask; });
        restorePlan = rp;
        RestoreFileOps.Clear(); RestoreRegistryOps.Clear();
        if (rp == null) return;
        foreach (var o in rp.Files) RestoreFileOps.Add(o);
        foreach (var o in rp.Registry) RestoreRegistryOps.Add(o);
        var html = Path.Combine(pkg.Layout.Status, $"Restore-DryRun-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.html");
        Json.WriteText(html, ReportWriter.RestoreHtml(rp, null));
        RestoreStatus = $"Dry run: create {rp.Count(RestoreAction.Create)}, overwrite {rp.Count(RestoreAction.Overwrite)}, identical {rp.Count(RestoreAction.SkipIdentical)}, " +
                        $"existing kept {rp.Count(RestoreAction.SkipExisting)}, unmapped {rp.Count(RestoreAction.Unmapped)}, blocked {rp.Count(RestoreAction.Blocked)}; registry values available {rp.Registry.Count(r => !r.Blocked)} (none selected by default). Nothing was changed.";
    }

    async Task RestoreAsync()
    {
        if (loadedPackage == null || restorePlan == null) return;
        var blockers = PrerequisiteIssues.Where(i => i.Severity == IssueSeverity.Blocker).ToList();
        if (PrerequisiteIssues.Count == 0) { Inform("Run 'Check prerequisites' first."); return; }
        if (blockers.Count > 0) { Inform("Restore is blocked:\n\n" + string.Join("\n", blockers.Select(b => "• " + b.Message))); return; }
        int files = restorePlan.Files.Count(f => f.Selected && f.Action is RestoreAction.Create or RestoreAction.Overwrite);
        int over = restorePlan.Files.Count(f => f.Selected && f.Action == RestoreAction.Overwrite);
        int regs = restorePlan.Registry.Count(r => r.Selected && !r.Blocked);
        if (files + regs == 0) { Inform("Nothing is selected to restore."); return; }
        if (!Confirm($"Restore now?\n\n• {files} files ({over} overwrite — previous versions are saved for Undo)\n• {regs} registry values (HKCU of the signed-in user, previous values saved)\n\nA journal is written to the package Status folder; you can Undo.")) return;
        if (regs > 0 && !Confirm($"Registry: {regs} value(s) will be changed in HKEY_CURRENT_USER. Continue?")) return;
        RestoreJournal? j = null; var pkg = loadedPackage; var rp = restorePlan;
        await RunJob("Restore", job => { j = new RestoreEngine(Log, Registry, RegistryWriter).Execute(pkg, rp, job.Progress, job.Token); if (j.Failed > 0) job.Warnings.Add($"{j.Failed} item(s) failed"); return Task.CompletedTask; });
        if (j == null) return;
        lastJournal = j;
        lastJournalPath = Path.Combine(pkg.Layout.Status, "Restore-" + j.JournalId, "journal.json");
        Json.WriteText(Path.Combine(pkg.Layout.Status, "Restore-" + j.JournalId, "report.html"), ReportWriter.RestoreHtml(rp, j));
        RestoreStatus = $"Restored {j.Restored} item(s), {j.Failed} failed. Files are restored; programs are NOT yet confirmed working — complete the functional checklist below.";
    }

    void UndoRestore()
    {
        if (lastJournal == null || lastJournalPath == null) return;
        if (!Confirm("Undo the last restore? Restored files that you changed afterwards are kept.")) return;
        var problems = new RestoreEngine(Log, Registry, RegistryWriter).Undo(lastJournal, lastJournalPath);
        RestoreStatus = problems.Count == 0 ? "Last restore undone." : "Undo finished with problems:\n" + string.Join("\n", problems.Take(10));
    }

    async Task RecreatePythonAsync()
    {
        if (loadedPackage == null || SelectedPythonEnv == null) return;
        var env = SelectedPythonEnv;
        var target = PythonRestorePlanner.MapPath(loadedPackage, env.RootPath, env.ProfileSid, BuildRequest());
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) { Inform("Target folder is not empty; it will not be overwritten:\n" + target); return; }
        var steps = PythonRestorePlanner.Steps(loadedPackage, env, target);
        if (steps.Count == 0) { Inform("The Python version of this environment is unknown; follow PYTHON-RECOVERY-PLAN.txt manually."); return; }
        if (env.Overall != CaptureStatus.Captured && !Confirm($"The package list of this environment is {env.Overall}. The result will be incomplete. Continue?")) return;
        if (!Confirm("These commands will run (packages come from the official Python Package Index):\n\n" + string.Join("\n", steps.Select(s => "• " + s.CommandLine)))) return;
        var sb = new StringBuilder();
        await RunJob("Recreate Python environment", async job =>
        {
            int i = 0;
            foreach (var s in steps)
            {
                job.Report(new ProgressInfo(s.Description, i++, steps.Count));
                var r = await Processes.RunAsync(s.FileName, s.Arguments, TimeSpan.FromMinutes(45), job.Token, onOutputLine: l => { job.Report(new ProgressInfo(s.Description, i, steps.Count, l)); });
                sb.AppendLine($"> {s.CommandLine}\n{r.StdOut}\n{r.StdErr}\n[{r.Describe()}]");
                if (!r.Succeeded) throw new InvalidOperationException($"{s.Description} failed: {r.Describe()}\n{r.StdErr}");
            }
        });
        var log = Path.Combine(loadedPackage.Layout.Status, $"Python-{env.Id}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.log");
        Json.WriteText(log, sb.ToString());
        RestoreStatus = "Python environment step log: " + log;
    }

    void SaveFunctional()
    {
        if (loadedPackage == null) return;
        var res = new { SavedUtc = DateTime.UtcNow, Checks = FunctionalChecks.Select(c => new { c.Text, c.Done }).ToList(), AllDone = FunctionalChecks.All(c => c.Done) };
        var p = PackageStatusStore.Save(loadedPackage.Layout.Root, "Functional", res);
        RestoreStatus = res.AllDone ? "All functional checks confirmed: environment marked FunctionallyValidated (" + p + ")" : $"Functional checklist saved ({FunctionalChecks.Count(c => c.Done)}/{FunctionalChecks.Count} done).";
    }
}
