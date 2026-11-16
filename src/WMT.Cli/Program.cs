using System.Text;
using WMT.Core;

// Headless interface (NFR-010): every core operation can run without the GUI.
// Exit codes: 0 OK, 3 completed with warnings, 1 failed / verification failed, 64 usage error.
Console.OutputEncoding = new UTF8Encoding(false);
var log = new ActivityLog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WMT", "Logs", $"cli-{DateTime.Now:yyyyMMdd}.log"));
var reg = new WindowsRegistry();
var proc = new ProcessRunner();
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
List<string> Opts(string name) { var l = new List<string>(); for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) l.Add(args[i + 1]); return l; }
var progress = new Progress<ProgressInfo>(p => { if (args.Contains("--verbose")) Console.Error.WriteLine($"[{p.Phase}] {p.Count}/{p.Total?.ToString() ?? "?"}"); });
var ct = CancellationToken.None;
try
{
    switch (args.FirstOrDefault()?.ToLowerInvariant())
    {
        case "software":
        {
            var r = new SoftwareDiscovery(new WindowsUninstallSource(reg), proc).Scan(progress, ct, includeWinget: args.Contains("--winget"));
            Console.WriteLine(Json.Serialize(new { r.Items, r.FilteredCount, r.DuplicateCount, r.Issues, r.WingetPackageCount }));
            return r.Issues.Any(i => i.Severity >= IssueSeverity.Warning) ? 3 : 0;
        }
        case "profiles":
        {
            var r = new ProfileDiscovery(reg, new WindowsAccountResolver()).Scan(progress, ct);
            Console.WriteLine(Json.Serialize(r));
            return 0;
        }
        case "office":
        {
            var sw = new SoftwareDiscovery(new WindowsUninstallSource(reg)).Scan(null, ct, includePortable: false);
            var o = OfficeFingerprint.Read(reg, sw.Items);
            Console.WriteLine(OfficeFingerprint.Format(o));
            return 0;
        }
        case "plan":
        case "backup":
        {
            var sw = new SoftwareDiscovery(new WindowsUninstallSource(reg), proc).Scan(progress, ct);
            var prof = new ProfileDiscovery(reg, new WindowsAccountResolver()).Scan(progress, ct);
            var wantProfiles = Opts("--profile");
            var profiles = prof.Items.Where(p => wantProfiles.Any(w => w.Equals("current", StringComparison.OrdinalIgnoreCase) ? p.IsCurrentUser : w.Equals(p.Sid, StringComparison.OrdinalIgnoreCase))).ToList();
            var filter = Opt("--software");
            var selected = filter == null ? new List<SoftwareItem>() : sw.Items.Where(s => System.Text.RegularExpressions.Regex.IsMatch(s.Name, filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            foreach (var s in selected) s.Selected = true;
            foreach (var p in profiles) p.Selected = true;
            var opts = new DiscoveryOptions { CustomPaths = Opts("--custom"), IncludeWindowsSettings = !args.Contains("--no-windows-settings") };
            var ud = Opt("--userdata");
            if (ud != null) foreach (var p in profiles) opts.UserData[p.Sid] = new HashSet<string>(ud.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var dest = Opt("--dest");
            if (dest != null) opts.ExcludedPaths.Add(dest);
            var ctx = new DiscoveryContext { AllSoftware = sw.Items, SelectedSoftware = selected, SelectedProfiles = profiles, Options = opts, Registry = reg, Processes = proc, Log = log, Progress = progress, Token = ct, CurrentUserSid = ProfileDiscovery.CurrentUserSid() };
            var plan = new PlanBuilder().Build(ctx);
            var (n, bytes, folders) = plan.SelectedTotals();
            Console.WriteLine($"Plan: {plan.Index.Files.Count} eligible files, {n} selected ({PathUtil.FormatBytes(bytes)}), {folders} empty folders kept, {plan.Components.Count} components, {plan.Dependencies.Count} dependencies, {plan.Issues.Count} issues.");
            foreach (var i in plan.Issues.Where(i => i.Severity >= IssueSeverity.Warning)) Console.WriteLine("  " + i);
            if (args[0] == "plan") return 0;
            if (dest == null) { Console.Error.WriteLine("--dest <existing folder> is required"); return 64; }
            var r = new BackupEngine(log).Create(new BackupInputs { Plan = plan, AllSoftware = sw.Items, AllProfiles = prof.Items, WingetExportJson = sw.WingetExportJson }, dest, progress, ct);
            Console.WriteLine($"Backup {r.Status}: {r.Copied} copied, {r.Failed} failed → {r.PackagePath}");
            return r.Status == "Complete" ? 0 : 1;
        }
        case "retry":
        {
            var r = new BackupEngine(log).RetryFailed(args[1], progress, ct);
            Console.WriteLine($"Retry: {r.Status}: {r.Copied} copied, {r.Failed} failed");
            return r.Status == "Complete" ? 0 : 1;
        }
        case "verify":
        {
            var v = Verifier.Verify(args[1], progress, ct);
            if (!args.Contains("--no-record")) PackageStatusStore.Save(args[1], "Verify", v);
            Console.WriteLine($"Verify: {v.Status} ({v.FilesChecked} files)");
            foreach (var e in v.Errors.Take(50)) Console.WriteLine("  " + e);
            return v.Passed ? 0 : 1;
        }
        case "test-restore":
        {
            var t = TestRestore.Run(args[1], args[2], progress, ct);
            PackageStatusStore.Save(args[1], "TestRestore", t);
            Console.WriteLine($"Test restore: {(t.Passed ? "PASSED" : "FAILED")} ({t.Restored} files)");
            foreach (var e in t.Errors.Take(50)) Console.WriteLine("  " + e);
            return t.Passed ? 0 : 1;
        }
        case "readiness":
        {
            var pkg = LoadedPackage.Load(args[1]);
            var rep = ReadinessEvaluator.Evaluate(pkg, PackageStatusStore.Latest<VerifyResult>(args[1], "Verify"), PackageStatusStore.Latest<TestRestoreResult>(args[1], "TestRestore"),
                PackageStatusStore.Latest<Confirmations>(args[1], "Confirmations") ?? new Confirmations());
            Console.WriteLine(rep.Overall);
            foreach (var i in rep.Items) Console.WriteLine($"  [{i.State}] {i.Area}: {i.Name} — {i.Reason}");
            return rep.MigrationReady ? 0 : 3;
        }
        case "klc":
        {
            // klc <layout.dll> <out.klc> [KLID] — writes an MSKLC source of an installed layout DLL.
            var dll = args[1]; var klid = args.Length > 3 ? args[3] : "00000409";
            var r = KlcExporter.Export(dll, Path.GetFileNameWithoutExtension(dll), Path.GetFileNameWithoutExtension(dll), klid);
            Msklc.WriteKlc(args[2], r.Klc);
            Console.WriteLine($"{Path.GetFileName(dll)}: {r.KeyRows} keys, {r.DeadKeys} dead-key entries, {r.ShiftStates} shift states" + (r.Warnings.Count > 0 ? " — " + string.Join("; ", r.Warnings) : ""));
            return r.KeyRows > 0 ? 0 : 3;
        }
        default:
            Console.Error.WriteLine("Usage: wmt-cli software [--winget] | profiles | office |\n" +
                "       plan|backup --profile current|<SID> [--software <regex>] [--custom <dir>]... [--userdata Documents,Desktop] [--dest <existing dir>] |\n" +
                "       retry <package> | verify <package> | test-restore <package> <empty dir> | readiness <package> |\n" +
                "       klc <layout.dll> <out.klc> [KLID]");
            return 64;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAILED: " + ex.Message);
    log.Error("cli.failed", ex.ToString());
    return 1;
}
