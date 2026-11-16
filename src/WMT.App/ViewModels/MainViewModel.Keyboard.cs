using System.Collections.ObjectModel;
using System.IO;
using WMT.Core;

namespace WMT.App.ViewModels;

public sealed class KeyboardRow : ObservableObject
{
    readonly Action changed;
    public KeyboardRow(KeyboardLayoutInfo info, Action changed) { Info = info; this.changed = changed; }
    public KeyboardLayoutInfo Info { get; }
    public bool IsSelected { get => Info.Selected; set { if (Info.Selected != value) { Info.Selected = value; Raise(); changed(); } } }
    public string CustomText => Info.IsCustom ? "Yes" : "No (Windows)";
    public string EnabledText => Info.EnabledForCurrentUser ? "Yes" : "No";
    public string SourceText => Info.BackupSource;
}

public sealed class KeyboardRestoreRow : ObservableObject
{
    bool selected; string action = "";
    public required KeyboardLayoutInfo Info { get; init; }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Action { get => action; set => Set(ref action, value); }
}

public sealed partial class MainViewModel
{
    KeyboardSnapshot? keyboard;
    string keyboardStatus = "Not scanned", keyboardInputText = "";
    public ObservableCollection<KeyboardRow> KeyboardRows { get; } = new();
    public ObservableCollection<KeyboardRestoreRow> KeyboardRestoreRows { get; } = new();
    public RelayCommand DiscoverKeyboardCommand { get; private set; } = null!;
    public RelayCommand SelectKeyboardCommand { get; private set; } = null!;
    public RelayCommand InstallLayoutsCommand { get; private set; } = null!;
    public RelayCommand RestoreLanguagesCommand { get; private set; } = null!;
    public KeyboardSnapshot? Keyboard => keyboard;
    public string KeyboardStatus { get => keyboardStatus; private set => Set(ref keyboardStatus, value); }
    public string KeyboardInputText { get => keyboardInputText; private set => Set(ref keyboardInputText, value); }

    void InitKeyboard()
    {
        DiscoverKeyboardCommand = new RelayCommand(async () => await DiscoverKeyboardAsync(), () => IsIdle);
        SelectKeyboardCommand = new RelayCommand(p =>
        {
            foreach (var r in KeyboardRows) r.IsSelected = (p as string) == "custom" ? r.Info.IsCustom : false;
        }, _ => IsIdle && KeyboardRows.Count > 0);
        InstallLayoutsCommand = new RelayCommand(async () => await InstallLayoutsAsync(), () => IsIdle && KeyboardRestoreRows.Any(r => r.Selected));
        RestoreLanguagesCommand = new RelayCommand(async () => await RestoreLanguagesAsync(), () => IsIdle && loadedPackage?.WindowsSettings?.Items.Any(i => i.Kind == SettingKinds.LanguageList) == true);
    }

    IEnumerable<string> SourceSearchFolders()
    {
        var list = new List<string>(CustomPaths);
        foreach (var p in Profiles.Where(p => p.IsSelected || p.Item.IsCurrentUser).Select(p => p.Item.ProfilePath).Where(p => !string.IsNullOrEmpty(p)))
            foreach (var sub in new[] { "Documents", "Desktop", "Downloads" }) list.Add(Path.Combine(p, sub));
        if (list.Count == 0)
            foreach (var f in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop }) list.Add(Environment.GetFolderPath(f));
        return list;
    }

    public async Task DiscoverKeyboardAsync()
    {
        KeyboardSnapshot? snap = null;
        var sw = AllSoftware.ToList(); var folders = SourceSearchFolders().ToList();
        var jr = await RunJob("Discover keyboard layouts", job =>
        {
            snap = new KeyboardLayoutDiscovery(Registry).Discover(sw, folders, job.Token);
            foreach (var i in snap.Issues) job.Warnings.Add(i.ToString());
            return Task.CompletedTask;
        });
        if (!Succeeded(jr) || snap == null) { KeyboardStatus = "Discovery failed or was cancelled — see log"; return; }
        var keep = KeyboardRows.Where(r => r.IsSelected).Select(r => r.Info.Klid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        keyboard = snap;
        KeyboardRows.Clear();
        foreach (var l in snap.Layouts)
        {
            l.Selected = keep.Count > 0 ? keep.Contains(l.Klid) : l.IsCustom;
            KeyboardRows.Add(new KeyboardRow(l, PlanStale));
        }
        int custom = snap.Layouts.Count(l => l.IsCustom);
        KeyboardStatus = $"{custom} custom layout(s), {snap.Layouts.Count - custom} Windows layout(s) in use. " +
            (sw.Count == 0 ? "Tip: run Discover software first so installers can be matched. " : "") +
            $"Searched for .klc files and setup packages in: {string.Join("; ", folders)}.";
        KeyboardInputText = snap.Input == null || snap.Input.Languages.Count == 0 ? "No language list found for the signed-in user." :
            "Your languages and keyboard order: " + string.Join("   ", snap.Input.Languages.Select(l => $"{l.Tag} [{string.Join(", ", l.InputTips)}]"));
        PlanStale();
    }

    void RefreshKeyboardRestore()
    {
        KeyboardRestoreRows.Clear();
        if (loadedPackage?.Keyboard == null) return;
        var kr = new KeyboardRestore(Processes, Registry);
        foreach (var l in loadedPackage.Keyboard.Layouts.Where(l => l.Selected && l.IsCustom))
            KeyboardRestoreRows.Add(new KeyboardRestoreRow { Info = l, Action = kr.Describe(loadedPackage, l), Selected = !kr.IsInstalled(l) && KeyboardRestore.InstallerInPackage(loadedPackage, l) != null });
    }

    async Task InstallLayoutsAsync()
    {
        if (loadedPackage == null) return;
        var rows = KeyboardRestoreRows.Where(r => r.Selected).ToList();
        if (!Confirm($"Install {rows.Count} keyboard layout(s)?\n\n" + string.Join("\n", rows.Select(r => "• " + r.Info.LayoutText + ": " + r.Action)) +
                     "\n\nWindows will ask for administrator permission. A restart may be needed before the layout appears.")) return;
        var pkg = loadedPackage; var kr = new KeyboardRestore(Processes, Registry);
        var results = new List<string>();
        await RunJob("Install keyboard layouts", async job =>
        {
            int n = 0;
            foreach (var r in rows)
            {
                job.Report(new ProgressInfo("Installing " + r.Info.LayoutText, n++, rows.Count));
                try { results.Add($"{r.Info.LayoutText}: {await kr.InstallAsync(pkg, r.Info, job.Token)}"); }
                catch (Exception ex) { results.Add($"{r.Info.LayoutText}: FAILED — {ex.Message}"); job.Warnings.Add(ex.Message); }
            }
        });
        RefreshKeyboardRestore();
        Inform(string.Join("\n", results) + "\n\nNext: Restore language list and keyboard order.");
    }

    async Task RestoreLanguagesAsync()
    {
        var item = loadedPackage?.WindowsSettings?.Items.FirstOrDefault(i => i.Kind == SettingKinds.LanguageList);
        if (loadedPackage == null || item == null) return;
        var req = SettingsRequest();
        var engine = SettingsEngine();
        var action = engine.DryRun(req).First(a => a.ItemId == item.Id);
        if (!Confirm("Restore languages and keyboard order?\n\n" + action.Description)) return;
        action.Selected = true; action.Blocked = false;
        RestoreJournal? j = null;
        await RunJob("Restore language list", async job => { j = await engine.ExecuteAsync(req, new[] { action }, job.Progress, job.Token); });
        if (j != null) Inform(j.Failed == 0 ? "Language list and keyboard order restored. Sign out and in if something is not shown yet." : "Failed: " + string.Join("\n", j.Errors));
    }
}
