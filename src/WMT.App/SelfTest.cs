using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WMT.App.ViewModels;
using WMT.Core;

namespace WMT.App;

/// <summary>Scripted GUI run used by CI ("WMT.exe --selftest &lt;dir&gt;"): drives the real window through the main workflow on
/// SYNTHETIC data only, measures UI responsiveness and saves screenshots. Never restores into real folders.</summary>
public sealed class SelfTest
{
    readonly MainWindow w; readonly MainViewModel vm; readonly ActivityLog log; readonly string dir;
    readonly List<object> steps = new();
    bool allPassed = true;
    double maxStallMs;

    public SelfTest(MainWindow w, MainViewModel vm, ActivityLog log, string dir) { this.w = w; this.vm = vm; this.log = log; this.dir = dir; }

    void Check(string name, bool ok, string detail = "")
    {
        steps.Add(new { Step = name, Passed = ok, Detail = detail });
        if (!ok) allPassed = false;
        log.Info("selftest.step", $"{(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    async Task Settle(int ms = 400)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    async Task Shot(string name)
    {
        await Settle();
        try
        {
            var root = (FrameworkElement)w.Content;
            var dpi = VisualTreeHelper.GetDpi(w);
            int width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
            using var fs = File.Create(Path.Combine(dir, "screenshots", name + ".png"));
            enc.Save(fs);
        }
        catch (Exception ex) { Check("screenshot " + name, false, ex.Message); }
    }

    /// <summary>Measures the longest gap between 50-ms UI timer ticks while <paramref name="action"/> runs (UI-009, AT-15).</summary>
    async Task<double> MeasureStall(Func<Task> action)
    {
        var sw = Stopwatch.StartNew(); double last = 0, worst = 0;
        var t = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) => { var now = sw.Elapsed.TotalMilliseconds; worst = Math.Max(worst, now - last); last = now; };
        t.Start();
        try { await action(); } finally { t.Stop(); }
        maxStallMs = Math.Max(maxStallMs, worst);
        return worst;
    }

    string CreateFixture()
    {
        var f = Path.Combine(dir, "fixture", "Projects");
        if (Directory.Exists(Path.Combine(dir, "fixture"))) Directory.Delete(Path.Combine(dir, "fixture"), true);
        Directory.CreateDirectory(f);
        var images = Directory.CreateDirectory(Path.Combine(f, "Images"));
        for (int i = 0; i < 24; i++)
        {
            var bmp = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(i * 10), (byte)(255 - i * 9), (byte)(i * 5 + 60))), null, new Rect(0, 0, 320, 240));
                dc.DrawEllipse(Brushes.White, null, new Point(160, 120), 40 + i * 2, 30 + i);
            }
            bmp.Render(dv);
            BitmapEncoder enc = i % 2 == 0 ? new PngBitmapEncoder() : new JpegBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(images.FullName, $"image-{i:00}{(i % 2 == 0 ? ".png" : ".jpg")}"));
            enc.Save(fs);
        }
        var deep = Directory.CreateDirectory(Path.Combine(f, "Ծրագրեր", "Level2", "Level3"));
        for (int i = 0; i < 150; i++) File.WriteAllText(Path.Combine(deep.FullName, $"Ֆայլ-{i:000}.txt"), "Բարև " + i, Encoding.UTF8);
        File.WriteAllText(Path.Combine(f, "script.py"), "print('hello')\n");
        File.WriteAllText(Path.Combine(f, "notes.docx"), "not a real docx");
        Directory.CreateDirectory(Path.Combine(f, "EmptyFolder"));
        return f;
    }

    public async Task<int> RunAsync()
    {
        vm.Confirm = msg => { log.Info("selftest.confirm", msg); return true; };
        vm.Inform = msg => log.Info("selftest.inform", msg);
        vm.PickFolder = _ => null;
        Directory.CreateDirectory(dir);
        var fixture = CreateFixture();
        var dest = Directory.CreateDirectory(Path.Combine(dir, "dest")).FullName;
        var sandbox = Path.Combine(dir, "sandbox");
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        Directory.CreateDirectory(sandbox);
        w.Width = 1400; w.Height = 900;
        await Settle(800);

        Check("AT-01 nothing scanned at start", vm.SoftwareCount == 0 && vm.Profiles.Count == 0 && vm.SoftwareStatus == "Not scanned" && vm.Plan == null);
        await Shot("01-start");

        var stall = await MeasureStall(() => vm.DiscoverSoftwareAsync());
        Check("AT-02 software discovered", vm.SoftwareCount > 0, $"{vm.SoftwareCount} programs; UI max stall {stall:0} ms");
        await Shot("02-software");
        vm.SoftwareGroup = "Category";
        await Shot("02b-software-grouped");
        vm.SoftwareGroup = "None";

        w.Tabs.SelectedIndex = 1;
        await vm.DiscoverProfilesAsync();
        vm.SelectProfiles("current");
        Check("AT-02 profiles discovered", vm.Profiles.Count > 0 && vm.Profiles.Count(p => p.IsSelected) == 1, $"{vm.Profiles.Count} profiles");
        vm.AddCustomPath(fixture);
        await Shot("03-profiles");

        w.Tabs.SelectedIndex = 2;
        stall = await MeasureStall(() => vm.DiscoverKeyboardAsync());
        Check("KBD keyboard layouts discovered", vm.Keyboard != null && vm.KeyboardRows.Count >= 1, $"{vm.KeyboardRows.Count} layouts ({vm.KeyboardRows.Count(r => r.Info.IsCustom)} custom); UI max stall {stall:0} ms");
        Check("KBD MSKLC status shown", vm.MsklcStatus.Contains("Keyboard Layout Creator"), vm.MsklcStatus);
        Check("KBD .klc generated for every custom layout with a DLL", vm.KeyboardRows.Where(r => r.Info.IsCustom && r.Info.Dll64 != null).All(r => r.Info.GeneratedKlc != null),
            string.Join("; ", vm.KeyboardRows.Where(r => r.Info.IsCustom).Select(r => $"{r.Info.Klid}: {r.KlcText}")));
        await Shot("03b-keyboard-layouts");
        w.Tabs.SelectedIndex = 3;
        stall = await MeasureStall(() => vm.DiscoverSettingsAsync());
        Check("WIN Windows settings discovered", vm.Settings != null && vm.SettingsCount > 3, $"{vm.SettingsCount} settings; groups: {string.Join(", ", vm.Settings?.Items.GroupBy(i => i.Group).Select(g => g.Key + " " + g.Count()) ?? Array.Empty<string>())}; UI max stall {stall:0} ms");
        Check("WIN no Wi-Fi password exported", vm.Settings?.Items.Where(i => i.Kind == SettingKinds.WiFi).All(i => !(i.Payload ?? "").Contains("keyMaterial")) != false);
        await Shot("03c-windows-settings");

        w.Tabs.SelectedIndex = 4;
        stall = await MeasureStall(() => vm.GeneratePlanAsync());
        var plan = vm.Plan;
        int fixtureFiles = plan?.Index.Files.Count(f => f.SourcePath.StartsWith(fixture, StringComparison.OrdinalIgnoreCase)) ?? 0;
        Check("AT-03 targeted plan from custom folder", plan != null && fixtureFiles == 24 + 150 + 2, $"{fixtureFiles} fixture files; UI max stall {stall:0} ms");
        await Shot("04-plan-details");

        if (plan != null)
        {
            var tree = plan.EnsureTree();
            var imagesNode = tree.Categories.SelectMany(c => tree.DescendantFolders(c)).FirstOrDefault(n => n.Name == "Images");
            if (imagesNode != null)
            {
                vm.NavigateTo(imagesNode, false);
                foreach (var mode in new[] { "Small Icons", "Large Icons", "Extra Large Icons" })
                {
                    vm.ViewMode = mode;
                    await Settle(1500);
                    await Shot("05-view-" + mode.Replace(' ', '-').ToLowerInvariant());
                }
                int thumbs = vm.ExplorerItems.Count(e => e.Image is BitmapImage);
                Check("AT-06 thumbnails in icon view", thumbs > 0, $"{thumbs} real thumbnails on page");
                vm.ViewMode = "Details";
                vm.SortMode = "Size"; vm.SortDescending = true;
                Check("AT-06 sort by size", vm.ExplorerItems.Count > 1 && vm.ExplorerItems[0].Item.Size >= vm.ExplorerItems[^1].Item.Size);
                vm.SortMode = "Name"; vm.SortDescending = false;
                var first = vm.ExplorerItems.First(e => !e.Item.IsFolder);
                first.IsChecked = false;
                var cat = imagesNode; while (cat.Parent != null) cat = cat.Parent;
                Check("AT-07 tri-state partial after unchecking one file", cat.CheckState == null && imagesNode.CheckState == null);
                vm.ViewMode = "Large Icons"; vm.ViewMode = "Details";
                Check("AT-07 selection kept after view change", plan.Index.Files[first.Item.FileIndex].Selected == false);
                vm.ExplorerSearch = "image-0";
                Check("AT-06 search", vm.ExplorerItems.Count > 0 && vm.ExplorerItems.All(e => e.Name.Contains("image-0")));
                vm.ExplorerSearch = "";
                vm.UpCommand.Execute(null);
                Check("AT-06 Up navigates to parent", vm.CurrentPath.Equals(fixture, StringComparison.OrdinalIgnoreCase), vm.CurrentPath);
                first.IsChecked = true;
                await Shot("06-plan-after-navigation");
            }
            else Check("fixture Images folder in tree", false);
        }

        w.Tabs.SelectedIndex = 5; w.RecoveryTabs.SelectedIndex = 0;
        vm.Destination = dest;
        vm.BuildPreview();
        await Shot("07-backup-preview");
        stall = await MeasureStall(() => vm.CreateBackupAsync());
        Check("AT-09/10 backup complete", vm.HasPackage && vm.BackupStatus.StartsWith("Complete"), vm.BackupStatus + $"; UI max stall {stall:0} ms");
        Check("AT-09 package inside destination", vm.PackagePath.StartsWith(dest, StringComparison.OrdinalIgnoreCase) && Directory.GetDirectories(dest).Length == 1);
        await Shot("08-backup-done");

        w.RecoveryTabs.SelectedIndex = 1;
        await vm.VerifyCommandExecuteAsync();
        Check("AT-12 verify passes on clean package", vm.VerifyStatus.StartsWith("Verified"), vm.VerifyStatus);
        vm.Sandbox = sandbox;
        await vm.TestRestoreCommandExecuteAsync();
        Check("AT-13 test restore", vm.TestStatus.StartsWith("PASSED"), vm.TestStatus);
        await Shot("09-verify-test");

        w.RecoveryTabs.SelectedIndex = 2;
        vm.RefreshReadiness();
        Check("RDY not ready without independent copy", vm.ReadinessOverall.StartsWith("NOT READY"), vm.ReadinessOverall);
        await Shot("10-readiness");

        w.RecoveryTabs.SelectedIndex = 3;
        await vm.DryRunCommandExecuteAsync();
        Check("AT-14 dry run writes nothing", vm.RestoreFileRows.Count > 0 && vm.RestoreFileRows.All(o => o.Op.Action is RestoreAction.SkipIdentical or RestoreAction.Unmapped or RestoreAction.Blocked or RestoreAction.SkipExisting or RestoreAction.Create),
            vm.RestoreStatus);
        Check("RST next-step hint shown", vm.RestoreNextStep.StartsWith("Next"), vm.RestoreNextStep);
        await Shot("11-restore-dryrun");

        // Restore into a test profile folder: Select all / Clear, then right-click → "Restore only this item", then Undo.
        var target = Path.Combine(dir, "restore-target");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        foreach (var r in vm.ProfileMapRows) r.Target = target;
        Check("RST hint asks for a new dry run after profile change", vm.RestoreNextStep.Contains("Dry run"), vm.RestoreNextStep);
        await vm.DryRunCommandExecuteAsync();
        int restorable = vm.RestoreFileRows.Count(f => f.CanRestore);
        vm.SetAll("files", false);
        bool cleared = vm.RestoreFileRows.All(f => !f.Selected);
        vm.SetAll("files", true);
        Check("RST Select all / Clear", restorable > 1 && cleared && vm.RestoreFileRows.Count(f => f.Selected) == restorable && vm.RestoreFileRows.Where(f => !f.CanRestore).All(f => !f.Selected),
            $"{restorable} restorable of {vm.RestoreFileRows.Count}");
        Check("RST right-click menu attached", w.FilesGrid.ContextMenu?.Items.Count == 7);
        var one = vm.RestoreFileRows.First(f => f.CanRestore);
        var other = vm.RestoreFileRows.Last(f => f.CanRestore);
        other.Selected = false;
        await vm.RunOnlyAsync("files", new List<object> { one });
        Check("RST restore only this item", File.Exists(one.Op.Target!) && one.Op.Reason == "Restored just now" && !one.Selected
              && vm.RestoreFileRows.Count(f => f.Op.Reason == "Restored just now") == 1 && !other.Selected && vm.RestoreFileRows.Count(f => f.Selected) == restorable - 2,
            $"{one.Op.Target} | {vm.RestoreStatus}");
        await Shot("11a-restore-only-this");
        vm.UndoRestoreCommand.Execute(null);
        Check("RST undo removes the restored file", !File.Exists(one.Op.Target!), vm.RestoreStatus);

        var help = new HelpWindow { Owner = w };
        help.Show();
        await Settle(500);
        Check("Help loads (Armenian)", help.HelpText.Text.Contains("Օգնություն") || help.HelpText.Text.Contains("ՕԳՆՈՒԹՅՈՒՆ"));
        help.Close();

        w.Tabs.SelectedIndex = 3;
        vm.SettingsDryRunCommand.Execute(null);
        Check("WIN settings dry run from package", vm.SettingActionRows.Count > 0, vm.SettingsRestoreStatus);
        await Shot("11b-settings-dryrun");
        w.Tabs.SelectedIndex = 2;
        await Shot("11c-keyboard-restore-section");

        w.Tabs.SelectedIndex = 4;
        w.Width = 900; w.Height = 600;
        await Settle(800);
        await Shot("12-small-window");
        w.WindowState = WindowState.Maximized;
        await Settle(800);
        await Shot("13-maximized");

        Check("AT-15 UI responsive during long operations", maxStallMs < 1500, $"max UI stall {maxStallMs:0} ms");
        Json.WriteFile(Path.Combine(dir, "selftest.json"), new
        {
            Version = Toolkit.Version, Build = Toolkit.BuildId, Os = Environment.OSVersion.VersionString, Utc = DateTime.UtcNow,
            AllPassed = allPassed, MaxUiStallMs = maxStallMs, Steps = steps
        });
        return allPassed ? 0 : 1;
    }
}
