using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class BackupTests
{
    static (string Src, MigrationPlan Plan) Source()
    {
        var src = T.Temp("src");
        T.Write(Path.Combine(src, "Templates", "Normal.dotm"), "normal");
        T.Write(Path.Combine(src, "Templates", "Sub", "Normal.dotm"), "same name, other folder");
        T.Write(Path.Combine(src, "Բառարան", "CUSTOM.DIC"), "բառ");
        for (int i = 0; i < 20; i++) T.Write(Path.Combine(src, "Data", $"file{i}.txt"), new string('x', i * 100));
        return (src, T.PlanFor(src));
    }

    [Fact] public void Normal_backup_real_names_inside_destination_only_and_sources_untouched()
    {
        var (src, plan) = Source();
        var before = T.HashTree(src);
        var dest = T.Temp("dest");
        var r = T.Backup(plan, dest);
        Assert.Equal("Complete", r.Status); Assert.Equal(23, r.Copied); Assert.Equal(0, r.Failed);
        Assert.Equal(before, T.HashTree(src));
        var pkg = Assert.Single(Directory.GetDirectories(dest));
        Assert.StartsWith("Migration-", Path.GetFileName(pkg));
        Assert.Empty(Directory.GetFiles(dest));
        var rel = PathUtil.ToPackageDataPath(Path.Combine(src, "Բառարան", "CUSTOM.DIC"));
        Assert.Equal("բառ", File.ReadAllText(Path.Combine(pkg, rel)));
        var files = Json.ReadFile<List<FileEntry>>(Path.Combine(pkg, "Manifests", "Files.json"));
        Assert.Equal(files.Count, files.Select(f => f.PackagePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, f => { Assert.Equal(FileCopyStatus.Copied, f.CopyStatus); Assert.Equal(64, f.Sha256!.Length); });
        Assert.Empty(Directory.EnumerateFiles(pkg, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(pkg, "*.bin", SearchOption.AllDirectories));
        foreach (var m in new[] { "Package.json", "Software.json", "Profiles.json", "Plan.json", "Files.json", "Dependencies.json", "Office-Environment.json", "Python-Environments.json", "Metadata-Hashes.json", @"Registry-REVIEW-ONLY\Registry.json" })
            Assert.True(File.Exists(Path.Combine(pkg, "Manifests", m)), m);
        foreach (var rep in new[] { "Readiness.html", "Recovery-Plan.html", "Recovery-Plan.txt", "PYTHON-RECOVERY-PLAN.txt", "Issues.json", "Backup-Report.html" })
            Assert.True(File.Exists(Path.Combine(pkg, "Reports", rep)), rep);
        Assert.True(File.Exists(Path.Combine(pkg, "README.html")) && File.Exists(Path.Combine(pkg, "Logs", "activity.log")));
        var mh = Json.ReadFile<MetadataHashes>(Path.Combine(pkg, "Manifests", "Metadata-Hashes.json"));
        Assert.Contains(@"Manifests\Files.json", mh.Files.Keys);
        Assert.Contains(@"Logs\activity.log", mh.Files.Keys);
    }

    [Fact] public void Destination_rules()
    {
        var (src, plan) = Source();
        Assert.Contains(BackupEngine.ValidateDestination(Path.Combine(T.Temp(), "does-not-exist"), plan), i => i.Severity == IssueSeverity.Blocker);
        Assert.Contains(BackupEngine.ValidateDestination(Path.Combine(src, "Data"), plan), i => i.Severity == IssueSeverity.Blocker && i.Message.Contains("inside"));
        Assert.DoesNotContain(BackupEngine.ValidateDestination(T.Temp(), plan), i => i.Severity == IssueSeverity.Blocker);
        Assert.Throws<InvalidOperationException>(() => T.Backup(plan, Path.Combine(src, "Data")));
    }

    [Fact] public void Locked_file_makes_package_Incomplete_then_retry_copies_only_failed()
    {
        var (src, plan) = Source();
        var lockedPath = Path.Combine(src, "Data", "file5.txt");
        var dest = T.Temp("dest");
        BackupResult r;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = T.Backup(plan, dest);
        Assert.Equal("Incomplete", r.Status); Assert.Equal(1, r.Failed); Assert.Equal(22, r.Copied);
        var pkg = r.PackagePath;
        var files = Json.ReadFile<List<FileEntry>>(Path.Combine(pkg, "Manifests", "Files.json"));
        var failed = files.Single(f => f.CopyStatus == FileCopyStatus.Failed);
        Assert.Equal(lockedPath, failed.SourcePath); Assert.Equal(3, failed.Attempts);
        Assert.False(File.Exists(Path.Combine(pkg, failed.PackagePath!)));
        Assert.False(Verifier.Verify(pkg, null, CancellationToken.None).Passed);
        var good = Path.Combine(pkg, files.First(f => f.CopyStatus == FileCopyStatus.Copied).PackagePath!);
        var sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(good, sentinel);
        var r2 = new BackupEngine(T.Log()) { RetryDelay = TimeSpan.Zero }.RetryFailed(pkg, null, CancellationToken.None);
        Assert.Equal("Complete", r2.Status);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(good));
        File.SetLastWriteTimeUtc(good, File.GetLastWriteTimeUtc(files.First(f => f.CopyStatus == FileCopyStatus.Copied).SourcePath));
        Assert.True(Verifier.Verify(pkg, null, CancellationToken.None).Passed);
    }

    [Fact] public void Hash_mismatch_is_retried_and_never_recorded_as_success()
    {
        var (_, plan) = Source();
        int corruptions = 0;
        var r = T.Backup(plan, T.Temp("dest"), setup: e => e.AfterPartialWritten = (f, partial) =>
        {
            if (f.SourcePath.EndsWith("file3.txt") && corruptions++ == 0) File.AppendAllText(partial, "CORRUPT");
        });
        Assert.Equal("Complete", r.Status);
        Assert.Equal(2, corruptions); // attempt 1 corrupted and rejected, attempt 2 clean and accepted
        var r2 = T.Backup(plan, T.Temp("dest2"), setup: e => e.AfterPartialWritten = (f, partial) => { if (f.SourcePath.EndsWith("file3.txt")) File.AppendAllText(partial, "X"); });
        Assert.Equal("Incomplete", r2.Status);
        var files = Json.ReadFile<List<FileEntry>>(Path.Combine(r2.PackagePath, "Manifests", "Files.json"));
        var bad = files.Single(f => f.SourcePath.EndsWith("file3.txt"));
        Assert.Equal(FileCopyStatus.Failed, bad.CopyStatus); Assert.Null(bad.Sha256); Assert.Contains("SHA-256", bad.CopyError);
    }

    [Fact] public void Source_changed_after_scan_is_flagged_not_silent()
    {
        var (src, plan) = Source();
        File.AppendAllText(Path.Combine(src, "Data", "file1.txt"), "changed");
        var r = T.Backup(plan, T.Temp("dest"));
        Assert.Equal(1, r.ChangedSinceScan);
        Assert.Contains(r.Issues, i => i.Message.Contains("changed after the scan"));
        var files = Json.ReadFile<List<FileEntry>>(Path.Combine(r.PackagePath, "Manifests", "Files.json"));
        var f = files.Single(x => x.SourcePath.EndsWith("file1.txt"));
        Assert.True(f.ChangedSinceScan);
        Assert.Equal(Hashing.Sha256File(f.SourcePath), f.Sha256);
    }

    [Fact] public void Cancel_leaves_a_consistent_incomplete_package()
    {
        var (_, plan) = Source();
        using var cts = new CancellationTokenSource();
        int n = 0;
        var e = new BackupEngine(T.Log()) { AfterPartialWritten = (_, _) => { if (++n == 5) cts.Cancel(); } };
        var r = e.Create(new BackupInputs { Plan = plan, AllSoftware = new List<SoftwareItem>(), AllProfiles = new List<UserProfileInfo>() }, T.Temp("dest"), null, cts.Token);
        Assert.True(r.Cancelled); Assert.Equal("Incomplete", r.Status);
        Assert.Empty(Directory.EnumerateFiles(r.PackagePath, "*.partial", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(r.PackagePath, "Manifests", "Metadata-Hashes.json")));
    }
}

public class VerifyTests
{
    static string Package()
    {
        var src = T.Temp("vsrc");
        for (int i = 0; i < 10; i++) T.Write(Path.Combine(src, "d", $"f{i}.txt"), "content " + i);
        return T.Backup(T.PlanFor(src), T.Temp("vdest")).PackagePath;
    }
    static List<FileEntry> Files(string pkg) => Json.ReadFile<List<FileEntry>>(Path.Combine(pkg, "Manifests", "Files.json"));

    [Fact] public void Clean_package_passes_and_verify_is_read_only()
    {
        var pkg = Package();
        var before = T.HashTree(pkg);
        var v = Verifier.Verify(pkg, null, CancellationToken.None);
        Assert.True(v.Passed, string.Join("\n", v.Errors)); Assert.Equal(10, v.FilesChecked);
        Assert.Equal(before, T.HashTree(pkg));
    }

    [Fact] public void Tampered_byte_fails()
    {
        var pkg = Package();
        var f = Path.Combine(pkg, Files(pkg)[0].PackagePath!);
        var b = File.ReadAllBytes(f); b[0] ^= 0xFF; File.WriteAllBytes(f, b);
        var v = Verifier.Verify(pkg, null, CancellationToken.None);
        Assert.False(v.Passed); Assert.Equal(1, v.HashMismatch);
    }

    [Fact] public void Missing_file_fails()
    {
        var pkg = Package(); File.Delete(Path.Combine(pkg, Files(pkg)[1].PackagePath!));
        var v = Verifier.Verify(pkg, null, CancellationToken.None);
        Assert.False(v.Passed); Assert.Equal(1, v.Missing);
    }

    [Fact] public void Extra_untracked_file_fails()
    {
        var pkg = Package(); T.Write(Path.Combine(pkg, "Data", "C", "intruder.txt"));
        var v = Verifier.Verify(pkg, null, CancellationToken.None);
        Assert.False(v.Passed); Assert.Equal(1, v.Extra);
    }

    static void EditManifest(string pkg, Action<List<FileEntry>> edit)
    {
        var p = Path.Combine(pkg, "Manifests", "Files.json");
        var list = Files(pkg); edit(list); Json.WriteFile(p, list);
    }

    [Fact] public void Duplicate_entry_fails_and_metadata_change_is_detected()
    {
        var pkg = Package();
        EditManifest(pkg, l => l.Add(l[0]));
        var v = Verifier.Verify(pkg, null, CancellationToken.None);
        Assert.False(v.Passed); Assert.True(v.Duplicates > 0); Assert.True(v.MetadataProblems > 0);
    }

    [Theory]
    [InlineData(@"Data\..\..\evil.txt")]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"Data\C\x:stream")]
    public void Malicious_relative_paths_fail(string bad)
    {
        var pkg = Package();
        EditManifest(pkg, l => l[0].PackagePath = bad);
        var v = Verifier.Verify(pkg, null, CancellationToken.None);
        Assert.False(v.Passed); Assert.True(v.UnsafePaths > 0);
    }

    [Fact] public void Package_without_metadata_hashes_fails()
    {
        var pkg = Package(); File.Delete(Path.Combine(pkg, "Manifests", "Metadata-Hashes.json"));
        Assert.False(Verifier.Verify(pkg, null, CancellationToken.None).Passed);
    }
}

public class TestRestoreTests
{
    [Fact] public void Byte_identical_restore_into_empty_sandbox_only()
    {
        var src = T.Temp("trsrc");
        for (int i = 0; i < 5; i++) T.Write(Path.Combine(src, $"Ֆ{i}.txt"), "Բարև " + i);
        var pkg = T.Backup(T.PlanFor(src), T.Temp("trdest")).PackagePath;
        var sandbox = T.Temp("sandbox");
        var r = TestRestore.Run(pkg, sandbox, null, CancellationToken.None);
        Assert.True(r.Passed, string.Join("\n", r.Errors)); Assert.Equal(5, r.Restored);
        foreach (var f in Directory.GetFiles(src))
        {
            var restored = Path.Combine(sandbox, PathUtil.ToPackageDataPath(f)["Data\\".Length..]);
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(restored));
        }
        Assert.NotEmpty(TestRestore.ValidateSandbox(pkg, sandbox));
        Assert.NotEmpty(TestRestore.ValidateSandbox(pkg, Path.Combine(pkg, "Data")));
        var again = TestRestore.Run(pkg, sandbox, null, CancellationToken.None);
        Assert.False(again.Passed);
    }
}

public class RestoreTests
{
    const string Sid = "S-1-5-21-1-2-3-1001";

    static (string OldProfile, LoadedPackage Pkg) Package()
    {
        var oldProfile = T.Temp("oldprofile");
        T.Write(Path.Combine(oldProfile, @"AppData\Roaming\Microsoft\Templates\Normal.dotm"), "normal v1");
        T.Write(Path.Combine(oldProfile, @"AppData\Roaming\Microsoft\UProof\CUSTOM.DIC"), "words");
        var prof = T.Profile(oldProfile, Sid);
        var ctx = T.Ctx(profiles: new[] { prof }, opts: new DiscoveryOptions { CustomPaths = { oldProfile }, IncludeRuntimes = false, IncludeWindowsSettings = false });
        var plan = new PlanBuilder(new IDiscoveryProvider[] { new CustomPathsProvider() }).Build(ctx);
        plan.Registry.Add(new RegistryValueRecord { Id = "r1", KeyPath = @"Software\WMT-Test\Word", ValueName = "UserTemplates", Type = "String", Data = Path.Combine(oldProfile, "Templates"), ProfileSid = Sid });
        plan.Registry.Add(new RegistryValueRecord { Id = "r2", KeyPath = @"Software\WMT-Test\Word", ValueName = "Count", Type = "DWord", Data = "-1", ProfileSid = Sid });
        var dest = T.Temp("rdest");
        var r = new BackupEngine(T.Log()).Create(new BackupInputs { Plan = plan, AllSoftware = new List<SoftwareItem>(), AllProfiles = new List<UserProfileInfo> { prof } }, dest, null, CancellationToken.None);
        return (oldProfile, LoadedPackage.Load(r.PackagePath));
    }

    [Fact] public void Unmapped_profile_is_never_guessed()
    {
        var (_, pkg) = Package();
        var plan = new RestoreEngine(T.Log(), new MemoryRegistry(), new MemoryRegistry()).DryRun(pkg, new RestoreRequest { PackageRoot = pkg.Layout.Root });
        Assert.All(plan.Files, f => { Assert.Equal(RestoreAction.Unmapped, f.Action); Assert.False(f.Selected); });
    }

    [Fact] public void Mapped_restore_dry_run_execute_overwrite_and_undo()
    {
        var (oldProfile, pkg) = Package();
        var newProfile = T.Temp("newprofile");
        var existing = T.Write(Path.Combine(newProfile, @"AppData\Roaming\Microsoft\Templates\Normal.dotm"), "NEW SYSTEM DEFAULT");
        var reg = new MemoryRegistry();
        var engine = new RestoreEngine(T.Log(), reg, reg);
        var req = new RestoreRequest { PackageRoot = pkg.Layout.Root, ProfileMap = { [Sid] = newProfile }, CurrentUserProfilePath = newProfile };

        var dry = engine.DryRun(pkg, req);
        Assert.False(File.Exists(Path.Combine(newProfile, @"AppData\Roaming\Microsoft\UProof\CUSTOM.DIC")));
        Assert.Equal("NEW SYSTEM DEFAULT", File.ReadAllText(existing));
        Assert.Equal(RestoreAction.SkipExisting, dry.Files.Single(f => f.Target == existing).Action);
        Assert.Equal(RestoreAction.Create, dry.Files.Single(f => f.Target!.EndsWith("CUSTOM.DIC")).Action);
        Assert.All(dry.Registry, r => Assert.False(r.Selected));
        var tmplOp = dry.Registry.Single(r => r.ValueName == "UserTemplates");
        Assert.True(tmplOp.Remapped); Assert.StartsWith(newProfile, tmplOp.NewValue);

        var req2 = new RestoreRequest { PackageRoot = pkg.Layout.Root, ProfileMap = { [Sid] = newProfile }, CurrentUserProfilePath = newProfile, AllowOverwrite = true };
        var plan = engine.DryRun(pkg, req2);
        Assert.Equal(RestoreAction.Overwrite, plan.Files.Single(f => f.Target == existing).Action);
        foreach (var r in plan.Registry.Where(r => !r.Blocked)) r.Selected = true;
        var j = engine.Execute(pkg, plan, null, CancellationToken.None);
        Assert.Equal(0, j.Failed);
        Assert.Equal("normal v1", File.ReadAllText(existing));
        Assert.Equal("words", File.ReadAllText(Path.Combine(newProfile, @"AppData\Roaming\Microsoft\UProof\CUSTOM.DIC")));
        Assert.Equal(-1, reg.Value("HKCU", @"Software\WMT-Test\Word", "Count")!.Data);
        Assert.StartsWith(newProfile, reg.Value("HKCU", @"Software\WMT-Test\Word", "UserTemplates")!.AsString());
        Assert.True(File.Exists(Path.Combine(oldProfile, @"AppData\Roaming\Microsoft\Templates\Normal.dotm")));

        var jpath = Path.Combine(pkg.Layout.Status, "Restore-" + j.JournalId, "journal.json");
        var problems = engine.Undo(j, jpath);
        Assert.Empty(problems);
        Assert.Equal("NEW SYSTEM DEFAULT", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(newProfile, @"AppData\Roaming\Microsoft\UProof\CUSTOM.DIC")));
        Assert.Null(reg.Value("HKCU", @"Software\WMT-Test\Word", "Count"));
    }

    [Fact] public void Registry_blocked_when_target_is_not_the_signed_in_user()
    {
        var (_, pkg) = Package();
        var plan = new RestoreEngine(T.Log(), new MemoryRegistry(), new MemoryRegistry()).DryRun(pkg,
            new RestoreRequest { PackageRoot = pkg.Layout.Root, ProfileMap = { [Sid] = T.Temp("other") }, CurrentUserProfilePath = T.Temp("me") });
        Assert.All(plan.Registry, r => Assert.True(r.Blocked));
    }

    [Fact] public void Prerequisites_block_unverified_package_and_report_missing_profile_target()
    {
        var (_, pkg) = Package();
        var issues = PrerequisiteChecker.Check(pkg, new List<SoftwareItem>(), OfficeEnvironment.Absent(), new RestoreRequest { PackageRoot = pkg.Layout.Root }, null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Blocker && i.Area == "Package");
        Assert.Contains(issues, i => i.Area == "Profiles");
        var v = Verifier.Verify(pkg.Layout.Root, null, CancellationToken.None);
        var ok = PrerequisiteChecker.Check(pkg, new List<SoftwareItem>(), OfficeEnvironment.Absent(), new RestoreRequest { PackageRoot = pkg.Layout.Root, ProfileMap = { [Sid] = T.Temp("np") } }, v);
        Assert.DoesNotContain(ok, i => i.Severity == IssueSeverity.Blocker);
    }
}

public class ReadinessTests
{
    [Fact] public void Not_ready_until_verified_tested_and_confirmed()
    {
        var src = T.Temp("rdsrc"); T.Write(Path.Combine(src, "a.txt"));
        var pkgPath = T.Backup(T.PlanFor(src), T.Temp("rddest")).PackagePath;
        var pkg = LoadedPackage.Load(pkgPath);
        var r0 = ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations());
        Assert.False(r0.MigrationReady);
        Assert.Contains(r0.Items, i => i.Key == "verify" && i.State == ReadinessState.NotTested);
        Assert.Contains(r0.Items, i => i.Key == "testrestore" && i.State == ReadinessState.NotTested);
        Assert.Contains(r0.Items, i => i.Key == "independent.copy" && i.State == ReadinessState.Blocked);

        var v = Verifier.Verify(pkgPath, null, CancellationToken.None);
        var t = TestRestore.Run(pkgPath, T.Temp("rdsb"), null, CancellationToken.None);
        var conf = new Confirmations();
        foreach (var i in ReadinessEvaluator.Evaluate(pkg, v, t, conf).Items.Where(i => i.Confirmable)) conf.Keys.Add(i.Key);
        var r1 = ReadinessEvaluator.Evaluate(pkg, v, t, conf);
        Assert.True(r1.MigrationReady, string.Join("\n", r1.Items.Where(i => i.State != ReadinessState.Ready).Select(i => i.Key + " " + i.Reason)));
        Assert.Equal("Migration Ready", r1.Overall);

        File.AppendAllText(Path.Combine(pkgPath, "Reports", "Readiness.html"), "<!-- edited -->");
        var bad = Verifier.Verify(pkgPath, null, CancellationToken.None);
        Assert.False(ReadinessEvaluator.Evaluate(pkg, bad, t, conf).MigrationReady);
    }

    [Fact] public void Failed_python_environment_blocks_until_user_marks_it_not_critical()
    {
        var src = T.Temp("rdpy"); T.Write(Path.Combine(src, "a.txt"));
        var pkgPath = T.Backup(T.PlanFor(src), T.Temp("rdpyd")).PackagePath;
        var basePkg = LoadedPackage.Load(pkgPath);
        var pkg = new LoadedPackage { Layout = basePkg.Layout, Info = basePkg.Info, Files = basePkg.Files, Python = new PythonInventory { Environments = { new PythonEnvironmentInfo { Id = "e1", Kind = "Virtual environment", RootPath = "C:\\x", Overall = CaptureStatus.Failed } } } };
        var item = ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations()).Items.Single(i => i.Key == "python.e1");
        Assert.Equal(ReadinessState.Blocked, item.State);
        Assert.Equal(ReadinessState.Ready, ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations { Keys = { "python.e1" } }).Items.Single(i => i.Key == "python.e1").State);
    }
}
