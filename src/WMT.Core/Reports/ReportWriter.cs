using System.Net;
using System.Text;

namespace WMT.Core;

/// <summary>Human-readable reports (HTML + TXT). Everything needed after reinstalling Windows lives inside the package (RDY-004).</summary>
public static class ReportWriter
{
    const string Css = "body{font-family:'Segoe UI',Sylfaen,Arial,sans-serif;margin:24px;max-width:1100px;color:#222}h1{font-size:22px}h2{font-size:17px;margin-top:28px;border-bottom:1px solid #ccc}table{border-collapse:collapse;width:100%;font-size:13px}td,th{border:1px solid #ddd;padding:4px 6px;text-align:left;vertical-align:top}th{background:#f3f3f3}.Ready,.ok{color:#0a7a2f;font-weight:600}.Blocked,.bad{color:#b00020;font-weight:600}.NeedsReview,.warn{color:#9a6200;font-weight:600}.NotTested{color:#555;font-weight:600}code,pre{background:#f6f6f6;padding:2px 4px;font-family:Consolas,monospace;font-size:12px;white-space:pre-wrap}.note{color:#555;font-size:13px}";

    public static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    static string Page(string title, string body) =>
        $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{H(title)}</title><style>{Css}</style></head><body><h1>{H(title)}</h1>{body}<p class=\"note\">Workstation Migration Toolkit {H(Toolkit.Version)} · generated {DateTime.Now:yyyy-MM-dd HH:mm}</p></body></html>";

    static string Table(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows, bool rawCells = false)
    {
        var sb = new StringBuilder("<table><tr>");
        foreach (var h in headers) sb.Append("<th>").Append(H(h)).Append("</th>");
        sb.Append("</tr>");
        foreach (var r in rows)
        {
            sb.Append("<tr>");
            foreach (var c in r) sb.Append("<td>").Append(rawCells ? c : H(c)).Append("</td>");
            sb.Append("</tr>");
        }
        return sb.Append("</table>").ToString();
    }

    public static void WritePackageReports(LoadedPackage pkg, MigrationPlan plan, BackupResult result, List<Issue> issues)
    {
        var r = pkg.Layout.Reports;
        var rec = RecoveryPlan(pkg, plan);
        Json.WriteText(Path.Combine(r, "Recovery-Plan.txt"), rec.Text);
        Json.WriteText(Path.Combine(r, "Recovery-Plan.html"), rec.Html);
        Json.WriteText(Path.Combine(r, "PYTHON-RECOVERY-PLAN.txt"), PythonPlanText(pkg));
        Json.WriteText(Path.Combine(r, "Backup-Report.html"), BackupReport(pkg, result, issues));
        Json.WriteText(Path.Combine(r, "Software.html"), SoftwareReport(pkg));
        Json.WriteText(Path.Combine(r, "Functional-Test-Checklist.html"), FunctionalChecklist(pkg));
        var readiness = ReadinessEvaluator.Evaluate(pkg, null, null, new Confirmations());
        Json.WriteText(Path.Combine(r, "Readiness.html"), ReadinessHtml(readiness, "State at the end of the backup: Verify and Test Restore have not been run yet."));
        Json.WriteText(Path.Combine(pkg.Layout.Root, "README.txt"), PackageReadmeText(pkg));
        Json.WriteText(Path.Combine(pkg.Layout.Root, "README.html"), Page("Migration package — how to use it", "<pre>" + H(PackageReadmeText(pkg)) + "</pre>"));
    }

    public static string PackageReadmeText(LoadedPackage pkg)
    {
        var i = pkg.Info;
        return $@"WORKSTATION MIGRATION PACKAGE
=============================
Source computer : {i.SourceMachine}  ({i.SourceOS})
Created (UTC)   : {i.CreatedUtc:yyyy-MM-dd HH:mm:ss}
Status          : {i.PackageStatus} — {i.CopiedFileCount} of {i.SelectedFileCount} selected files copied ({PathUtil.FormatBytes(i.SelectedBytes)}), {i.FailedFileCount} failed
Disk check      : {i.DestinationDiskCheck}

WHAT IS INSIDE
Data\        Your files with their REAL names, extensions and folders: Data\C\Users\<name>\... is C:\Users\<name>\...
             You can open or copy them with Windows Explorer without this program.
Manifests\   Machine-readable lists (JSON, UTF-8): Files.json (every file with source path, size, SHA-256),
             Software.json, Profiles.json, Plan.json, Dependencies.json, Office-Environment.json,
             Python-Environments.json, Windows-Settings.json, Registry-REVIEW-ONLY\Registry.json (review only,
             never imported automatically), Metadata-Hashes.json (checksums of all metadata, written last).
Reports\     Recovery-Plan.html/.txt (ordered steps for the new Windows), Readiness.html, Backup-Report.html,
             PYTHON-RECOVERY-PLAN.txt and Python\*-requirements.txt, Software.html, Functional-Test-Checklist.html, Issues.json.
Logs\        activity.log of the backup.
Status\      Results written later (Verify, Test Restore, Restore journals, confirmations). Not covered by Metadata-Hashes.json.

RESTORING BY HAND
1. Install Windows 11, create your user account and install the programs listed in Reports\Recovery-Plan.html.
2. Copy files from Data\<drive>\... to the same place. For files under Data\C\Users\<old name>\ use your NEW profile folder.
3. Python: follow Reports\PYTHON-RECOVERY-PLAN.txt. Do not copy old virtual environments.
4. Registry values are for review only: re-create settings manually or with the toolkit's controlled restore.

INTEGRITY
{Toolkit.IntegrityNote}
Run 'Verify backup' in the toolkit, or compare SHA-256 values from Manifests\Files.json (e.g. with: certutil -hashfile <file> SHA256).

SECURITY
This folder contains personal files and settings. Store it where only you can read it. Passwords, tokens,
license keys and activation data were NOT exported.
";
    }

    public static (string Text, string Html) RecoveryPlan(LoadedPackage pkg, MigrationPlan? plan)
    {
        var t = new StringBuilder();
        var h = new StringBuilder();
        int step = 0;
        void Step(string title) { step++; t.AppendLine().AppendLine($"STEP {step}. {title}").AppendLine(new string('-', 60)); h.Append($"<h2>Step {step}. {H(title)}</h2>"); }

        t.AppendLine("RECOVERY PLAN — " + pkg.Info.SourceMachine).AppendLine("Order matters: programs and runtimes first, then files, then settings, then tests.");

        Step("Install Windows 11 and create the user accounts");
        var profRows = pkg.Profiles.Select(p => new[] { p.Account, p.Sid, p.ProfilePath, pkg.Files.Count(f => f.ProfileSid == p.Sid && f.Selected).ToString() }).ToList();
        foreach (var p in profRows) t.AppendLine($"  Source profile {p[0]} ({p[2]}): {p[3]} files. Choose its target profile at restore time (SIDs differ on the new system).");
        h.Append("<p>Old SIDs and paths are never applied automatically: choose <b>Source → Target</b> profile mapping in the toolkit.</p>")
         .Append(Table(new[] { "Source account", "SID", "Old profile path", "Files" }, profRows));

        Step("Install prerequisites (runtimes, Office, Python, engines)");
        var installs = pkg.Components.Where(c => c.MigrationMethod == MigrationMethod.Reinstall).OrderBy(c => c.Kind == "Runtime" ? 0 : c.Id == "office-product" ? 1 : 2).ToList();
        foreach (var c in installs)
            t.AppendLine($"  [{c.Kind}] {c.Name} — {c.DestinationRule}{(c.OfficialSource != null ? " — " + c.OfficialSource : "")}{(c.ManualActions.Count > 0 ? " — " + string.Join(" ", c.ManualActions) : "")}");
        h.Append(Table(new[] { "Kind", "Name", "How", "Official source", "Notes" },
            installs.Select(c => new[] { c.Kind, c.Name, c.DestinationRule, c.OfficialSource ?? "—", string.Join(" ", c.ManualActions) })));
        if (File.Exists(pkg.Layout.M("Winget-Export.json")))
        {
            t.AppendLine("  Optional: many programs can be reinstalled with:  winget import -i \"Manifests\\Winget-Export.json\"  (review the list first).");
            h.Append("<p>Optional: <code>winget import -i \"Manifests\\Winget-Export.json\"</code> reinstalls programs available in winget (review first).</p>");
        }

        Step("Keyboard layouts (before restoring the language list)");
        var kbd = pkg.Keyboard?.Layouts.Where(l => l.Selected && l.IsCustom).ToList() ?? new();
        if (kbd.Count == 0) { t.AppendLine("  No custom keyboard layouts selected."); h.Append("<p>No custom keyboard layouts selected.</p>"); }
        foreach (var l in kbd)
        {
            t.AppendLine($"  {l.LayoutText} ({l.Klid}, {l.Language}): {KbdHow(pkg, l)}");
        }
        h.Append(Table(new[] { "Layout", "KLID", "Language", "Best source", "How it is reinstalled" },
            kbd.Select(l => new[] { l.LayoutText, l.Klid, l.Language, l.BackupSource, KbdHow(pkg, l) })));

        Step("Restore files");
        var cats = pkg.Files.Where(f => f.Selected).GroupBy(f => f.Category).OrderBy(g => g.Key).ToList();
        foreach (var g in cats) t.AppendLine($"  {g.Key}: {g.Count()} files ({PathUtil.FormatBytes(g.Sum(f => f.Size))}) — {g.First().DestinationRule}");
        h.Append(Table(new[] { "Category", "Files", "Size", "Destination rule" }, cats.Select(g => new[] { g.Key, g.Count().ToString(), PathUtil.FormatBytes(g.Sum(f => f.Size)), g.First().DestinationRule })));

        Step("Recreate Python environments");
        t.AppendLine("  See PYTHON-RECOVERY-PLAN.txt for exact commands.");
        h.Append("<pre>" + H(PythonPlanText(pkg)) + "</pre>");

        Step("Word templates, add-ins and dependencies");
        var word = pkg.Components.Where(c => c.Application == "Microsoft Word" && c.MigrationMethod != MigrationMethod.Reinstall).ToList();
        foreach (var c in word) t.AppendLine($"  {c.Name}: {c.DestinationRule}. {string.Join(" ", c.ManualActions)}");
        h.Append(Table(new[] { "Item", "Kind", "Actions" }, word.Select(c => new[] { c.Name, c.Kind, string.Join(" ", c.ManualActions) })));
        var deps = pkg.Dependencies.OrderBy(d => d.Confidence).ToList();
        h.Append("<h3>Dependencies (Application → Component → Dependency → Required action)</h3>")
         .Append(Table(new[] { "Application", "Component", "Depends on", "Type", "Confidence", "Required action" },
            deps.Select(d => new[] { d.Application, d.Component, d.DependsOn, d.Type, d.Confidence.ToString(), d.RequiredAction })));
        foreach (var d in deps.Where(d => d.Confidence != Confidence.Confirmed)) t.AppendLine($"  [{d.Confidence}] {d.Component} → {d.DependsOn}: {d.RequiredAction}");

        Step("Review registry values (never imported automatically)");
        t.AppendLine($"  {pkg.Registry.Count} values in Manifests\\Registry-REVIEW-ONLY\\Registry.json. Apply only those you need, one by one, with the toolkit (journaled, undoable) or by hand.");
        h.Append(Table(new[] { "Key", "Value", "Type", "Data", "Note" }, pkg.Registry.Take(500).Select(x => new[] { "HKCU\\" + x.KeyPath, x.ValueName, x.Type, x.Data ?? string.Join("; ", x.MultiData ?? new()), x.Note })));

        Step("Windows settings (Windows Settings tab → Restore from package)");
        var items = pkg.WindowsSettings?.Items.Where(i => i.Selected).ToList() ?? new();
        foreach (var i in items) t.AppendLine($"  [{i.Group}] {i.Name}: {i.RestoreMethod}{(i.RequiresAdmin ? " (administrator)" : "")}{(i.RequiresSignOut ? " (sign out/in)" : "")}");
        h.Append(Table(new[] { "Group", "Setting", "Value", "How it is restored" }, items.Select(i => new[] { i.Group, i.Name, i.Summary, i.RestoreMethod + (i.RequiresAdmin ? " · administrator" : "") + (i.RequiresSignOut ? " · sign out/in" : "") })));

        Step("Test that everything works (functional checklist)");
        foreach (var f in pkg.FunctionalTests) t.AppendLine("  [ ] " + f);
        h.Append("<ul>" + string.Concat(pkg.FunctionalTests.Select(f => "<li>☐ " + H(f) + "</li>")) + "</ul>");

        return (t.ToString(), Page("Recovery plan — " + pkg.Info.SourceMachine, h.ToString()));
    }

    public static string PythonPlanText(LoadedPackage pkg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PYTHON RECOVERY PLAN").AppendLine("====================");
        var inv = pkg.Python ?? new PythonInventory();
        if (inv.Environments.Count == 0) { sb.AppendLine("No Python environments were included in this package."); return sb.ToString(); }
        foreach (var g in inv.GpuInfo) sb.AppendLine("GPU: " + g);
        if (inv.TesseractVersion != null) sb.AppendLine($"Tesseract: {inv.TesseractVersion} ({inv.TesseractPath})");
        foreach (var e in inv.Environments)
        {
            sb.AppendLine().AppendLine($"== {e.Kind}: {e.RootPath}");
            sb.AppendLine($"   Python {e.Version} {e.Architecture}; capture status: {e.Overall} (freeze {e.FreezeStatus}, list {e.ListStatus})");
            if (e.BaseInterpreter != null) sb.AppendLine("   Base interpreter: " + e.BaseInterpreter);
            if (e.ProjectPath != null) sb.AppendLine("   Project folder: " + e.ProjectPath);
            var url = PythonProvider.PythonInstallerUrl(e.Version, e.Architecture);
            if (url != null) sb.AppendLine("   1. Install Python from the official site: " + url);
            if (e.Kind != "Interpreter")
            {
                var req = "Reports\\Python\\" + BackupEngine.PythonReqFileName(e);
                var m = System.Text.RegularExpressions.Regex.Match(e.Version, @"^(\d+\.\d+)");
                var tag = m.Success ? "-" + m.Groups[1].Value + (e.Architecture == "x86" ? "-32" : "") : "-3";
                sb.AppendLine($"   2. py {tag} -m venv \"{e.RootPath}\"   (use your NEW profile path if it changed)");
                if (e.Freeze != null)
                {
                    sb.AppendLine($"   3. \"{e.RootPath}\\Scripts\\python.exe\" -m pip install --upgrade pip");
                    sb.AppendLine($"   4. \"{e.RootPath}\\Scripts\\python.exe\" -m pip install -r \"{req}\"");
                    sb.AppendLine("   5. Compare: python -m pip freeze --all  with the captured file.");
                }
                else sb.AppendLine("   !! No package list was captured: the environment CANNOT be recreated automatically.");
            }
            foreach (var n in e.NonReinstallablePackages) sb.AppendLine("   !! Not on PyPI (needs its source): " + n);
            foreach (var g in e.GpuNotes) sb.AppendLine("   GPU: " + g + " — check driver/CUDA manually; versions are not guessed.");
            foreach (var err in e.Errors) sb.AppendLine("   ERROR: " + err);
            foreach (var n in e.Notes) sb.AppendLine("   Note: " + n);
            foreach (var r in e.RequirementsFiles) sb.AppendLine("   Project requirement file: " + r);
            foreach (var s in e.LaunchScripts) sb.AppendLine("   Launch script: " + s);
            sb.AppendLine("   Manual check: run the project's own script on a sample and compare with the old output.");
        }
        return sb.ToString();
    }

    static string BackupReport(LoadedPackage pkg, BackupResult r, List<Issue> issues)
    {
        var sb = new StringBuilder();
        sb.Append($"<p>Status: <b class=\"{(r.Status == "Complete" ? "ok" : "bad")}\">{H(r.Status)}</b> — copied {r.Copied}, failed {r.Failed}, not processed {r.Skipped}, changed since scan {r.ChangedSinceScan}.</p>");
        sb.Append("<h2>Failed</h2>").Append(Table(new[] { "Source", "Error", "Attempts" }, pkg.Files.Where(f => f.CopyStatus == FileCopyStatus.Failed).Select(f => new[] { f.SourcePath, f.CopyError ?? "", f.Attempts.ToString() })));
        sb.Append("<h2>Not processed</h2>").Append(Table(new[] { "Source", "Status" }, pkg.Files.Where(f => f.CopyStatus is FileCopyStatus.Pending or FileCopyStatus.Skipped).Select(f => new[] { f.SourcePath, f.CopyStatus.ToString() })));
        sb.Append("<h2>Manual actions required</h2>").Append(Table(new[] { "Item", "Action" },
            pkg.Components.Where(c => c.ManualActions.Count > 0).Select(c => new[] { c.Name, string.Join(" ", c.ManualActions) })));
        sb.Append("<h2>Issues</h2>").Append(Table(new[] { "Severity", "Area", "Message", "Path", "Action" }, issues.Select(i => new[] { i.Severity.ToString(), i.Area, i.Message, i.Path ?? "", i.RequiredAction ?? "" })));
        sb.Append("<h2>Copied</h2>").Append(Table(new[] { "Category", "Source", "Package path", "Size", "SHA-256" },
            pkg.Files.Where(f => f.CopyStatus == FileCopyStatus.Copied).Take(20000).Select(f => new[] { f.Category, f.SourcePath, f.PackagePath ?? "", PathUtil.FormatBytes(f.Size), f.Sha256 ?? "" })));
        return Page("Backup report", sb.ToString());
    }

    static string SoftwareReport(LoadedPackage pkg) => Page("Installed software (source computer)",
        Table(new[] { "Selected", "Name", "Publisher", "Version", "Build", "Arch", "Install type", "Install location", "Source", "Recovery method", "Dependency status" },
            pkg.Software.Select(s => new[] { s.Selected ? "✓" : "", s.Name, s.Publisher, s.Version, s.Build, s.Architecture, s.InstallationType, s.InstallLocation, s.DetectedSource, s.RecoveryMethod, s.DependencyStatus })));

    static string FunctionalChecklist(LoadedPackage pkg) => Page("Functional test checklist (after restore)",
        "<p>File restore and working programs are separate results (RST-005). Tick each line only after trying it.</p><ul>" +
        string.Concat(pkg.FunctionalTests.Select(f => "<li>☐ " + H(f) + "</li>")) + "</ul>" +
        Table(new[] { "Component", "Verification" }, pkg.Components.Where(c => !string.IsNullOrEmpty(c.VerificationMethod)).Select(c => new[] { c.Name, c.VerificationMethod })));

    public static string ReadinessHtml(ReadinessReport rep, string? note = null)
    {
        var body = $"<p>Overall: <b class=\"{(rep.MigrationReady ? "ok" : "bad")}\">{H(rep.Overall)}</b></p>" + (note != null ? $"<p class=\"note\">{H(note)}</p>" : "") +
            Table(new[] { "State", "Area", "Item", "Reason", "Action" },
                rep.Items.Select(i => new[] { $"<span class=\"{i.State}\">{i.State}</span>", H(i.Area), H(i.Name), H(i.Reason), H(i.Action) }), rawCells: true);
        return Page("Migration readiness", body);
    }

    public static string VerifyHtml(VerifyResult v) => Page("Verify result",
        $"<p>Result: <b class=\"{(v.Passed ? "ok" : "bad")}\">{H(v.Status)}</b> — {v.FilesChecked} files ({PathUtil.FormatBytes(v.BytesChecked)}) checked; missing {v.Missing}, hash mismatch {v.HashMismatch}, size mismatch {v.SizeMismatch}, extra {v.Extra}, duplicates {v.Duplicates}, unsafe paths {v.UnsafePaths}, metadata problems {v.MetadataProblems}, not copied {v.NotCopied}.</p><p class=\"note\">{H(v.Note)}</p>" +
        "<h2>Errors</h2><pre>" + H(string.Join("\n", v.Errors)) + "</pre><h2>Warnings</h2><pre>" + H(string.Join("\n", v.Warnings)) + "</pre>");

    public static string RestoreHtml(RestorePlan plan, RestoreJournal? journal) => Page(journal == null ? "Restore dry run" : "Restore result",
        (journal != null ? $"<p>Restored {journal.Restored}, failed {journal.Failed}.</p><pre>{H(string.Join("\n", journal.Errors))}</pre>" : "") +
        "<h2>Files</h2>" + Table(new[] { "Action", "Selected", "Target", "From package", "Reason" }, plan.Files.Select(f => new[] { f.Action.ToString(), f.Selected ? "✓" : "", f.Target ?? "", f.PackagePath, f.Reason })) +
        "<h2>Registry</h2>" + Table(new[] { "Selected", "Key", "Value", "Old", "New", "Note" }, plan.Registry.Select(r => new[] { r.Selected ? "✓" : "", "HKCU\\" + r.KeyPath, r.ValueName, r.OldValue ?? "(absent)", r.NewValue ?? string.Join("; ", r.NewMulti ?? new()), (r.Blocked ? "BLOCKED: " : "") + r.Warning })));

    static string KbdHow(LoadedPackage pkg, KeyboardLayoutInfo l) =>
        KeyboardRestore.InstallerInPackage(pkg, l) is string inst ? $"installer {inst} (Keyboard Layouts tab → Install selected layouts)" :
        KeyboardRestore.DllsInPackage(pkg, l).Dll64 is string dll ? $"WMT installs the saved DLL {dll} (Keyboard Layouts tab → Install selected layouts; MSKLC not needed)" :
        KeyboardRestore.KlcInPackage(pkg, l) != null ? "rebuild from .klc: install MSKLC, then Keyboard Layouts tab → Install selected layouts" :
        "NOTHING to reinstall from — save it as .klc with MSKLC before wiping Windows 10";
}
