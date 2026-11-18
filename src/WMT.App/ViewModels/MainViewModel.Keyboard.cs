using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public string KlcText => !Info.IsCustom ? "—" : Info.GeneratedKlc != null ? "Yes" : Info.GeneratedKlcError != null ? "No: " + Info.GeneratedKlcError : "No";
}

public sealed class KeyboardRestoreRow : ObservableObject
{
    bool selected; string action = "";
    public required KeyboardLayoutInfo Info { get; init; }
    public bool CanInstall { get; set; }
    public bool Selected { get => selected; set => Set(ref selected, value && CanInstall); }
    public string Action { get => action; set => Set(ref action, value); }
}

public sealed partial class MainViewModel
{
    KeyboardSnapshot? keyboard;
    string keyboardStatus = "Not scanned", keyboardInputText = "", msklcStatus = "Microsoft Keyboard Layout Creator: checked when you discover layouts or open a package.";
    MsklcInstall? msklc; bool msklcChecked;
    public ObservableCollection<KeyboardRow> KeyboardRows { get; } = new();
    public ObservableCollection<KeyboardRestoreRow> KeyboardRestoreRows { get; } = new();
    public RelayCommand DiscoverKeyboardCommand { get; private set; } = null!;
    public RelayCommand SelectKeyboardCommand { get; private set; } = null!;
    public RelayCommand InstallLayoutsCommand { get; private set; } = null!;
    public RelayCommand RestoreLanguagesCommand { get; private set; } = null!;
    public RelayCommand SaveKlcCommand { get; private set; } = null!;
    public RelayCommand OpenInMsklcCommand { get; private set; } = null!;
    public string MsklcStatus { get => msklcStatus; private set => Set(ref msklcStatus, value); }
    /// <summary>Folder where "Save .klc files" writes (Documents\WMT Keyboard Layouts).</summary>
    public string KlcFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WMT Keyboard Layouts");
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
        InstallLayoutsCommand = new RelayCommand(async () => await InstallLayoutsAsync(null), () => IsIdle && KeyboardRestoreRows.Any(r => r.Selected));
        RestoreLanguagesCommand = new RelayCommand(async () => await RestoreLanguagesAsync(), () => IsIdle && loadedPackage?.WindowsSettings?.Items.Any(i => i.Kind == SettingKinds.LanguageList) == true);
        SaveKlcCommand = new RelayCommand(() => SaveKlcFiles(openFolder: true), () => IsIdle && KlcLayouts().Any(l => l.GeneratedKlc != null));
        OpenInMsklcCommand = new RelayCommand(OpenInMsklc, () => IsIdle && KlcLayouts().Any(l => l.GeneratedKlc != null || l.KlcSources.Count > 0));
    }

    public MsklcInstall? FindMsklc(bool refresh = false)
    {
        if (!msklcChecked || refresh)
        {
            msklc = Msklc.Find(AllSoftware);
            msklcChecked = true;
            MsklcStatus = msklc != null
                ? $"Microsoft Keyboard Layout Creator found: {msklc}" + (msklc.CanBuild ? " (WMT can use it to build layouts from .klc)" : "")
                : "Microsoft Keyboard Layout Creator is not installed. It is NOT needed to install layouts from the saved DLL; only to edit a layout or rebuild it from .klc.";
        }
        return msklc;
    }

    /// <summary>Selected custom layouts from discovery; on the new computer (no discovery) the layouts of the opened package.</summary>
    IEnumerable<KeyboardLayoutInfo> KlcLayouts()
    {
        var fromScan = KeyboardRows.Where(r => r.IsSelected && r.Info.IsCustom).Select(r => r.Info).ToList();
        if (fromScan.Count > 0 || KeyboardRows.Count > 0) return fromScan;
        return KeyboardRestoreRows.Where(r => r.Selected || KeyboardRestoreRows.All(x => !x.Selected)).Select(r => r.Info);
    }

    /// <summary>Writes the generated .klc of every selected custom layout. Existing files with other content are kept (a numbered copy is written).</summary>
    public List<string> SaveKlcFiles(bool openFolder)
    {
        var written = new List<string>();
        try
        {
            foreach (var l in KlcLayouts().Where(l => l.GeneratedKlc != null))
            {
                var path = Path.Combine(KlcFolder, KeyboardRestore.GeneratedKlcFileName(l));
                for (int n = 2; File.Exists(path) && File.ReadAllText(path) != l.GeneratedKlc; n++)
                    path = Path.Combine(KlcFolder, $"{l.KlcBaseName} ({n}).klc");
                Msklc.WriteKlc(path, l.GeneratedKlc!);
                written.Add(path);
            }
            Log.Info("keyboard.klc", "Saved .klc files: " + string.Join("; ", written));
        }
        catch (Exception ex) { Inform("Could not save the .klc files: " + ex.Message); return written; }
        if (openFolder && written.Count > 0)
        {
            Inform($"{written.Count} .klc file(s) saved in {KlcFolder}:\n" + string.Join("\n", written.Select(f => Path.GetFileName(f))) +
                   "\n\nThey are also saved inside every backup package (Reports\\Keyboard). You can open them in Microsoft Keyboard Layout Creator.");
            try { Process.Start(new ProcessStartInfo(KlcFolder) { UseShellExecute = true }); } catch { }
        }
        return written;
    }

    void OpenInMsklc()
    {
        var l = KlcLayouts().FirstOrDefault(x => x.GeneratedKlc != null || x.KlcSources.Count > 0);
        if (l == null) return;
        var m = FindMsklc(refresh: true);
        if (m?.Exe == null)
        {
            if (Confirm("Microsoft Keyboard Layout Creator is not installed on this computer.\n\nIt is free from Microsoft. Open its download page?\n\n" + Msklc.DownloadPage))
                try { Process.Start(new ProcessStartInfo(Msklc.DownloadPage) { UseShellExecute = true }); } catch { }
            return;
        }
        string? file = l.KlcSources.FirstOrDefault(File.Exists);
        if (file == null && loadedPackage != null) file = KeyboardRestore.KlcInPackage(loadedPackage, l);
        if (file == null) file = SaveKlcFiles(openFolder: false).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(l.KlcBaseName, StringComparison.OrdinalIgnoreCase));
        if (file == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(m.Exe, "\"" + file + "\"") { UseShellExecute = true, WorkingDirectory = m.Folder });
            Log.Info("keyboard.msklc", $"Opened {file} in MSKLC");
        }
        catch (Exception ex) { Inform("Could not start MSKLC: " + ex.Message + "\n\nOpen it yourself and use File → Open: " + file); }
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
        FindMsklc(refresh: true);
        int custom = snap.Layouts.Count(l => l.IsCustom);
        KeyboardStatus = $"{custom} custom layout(s), {snap.Layouts.Count - custom} Windows layout(s) in use. " +
            (custom > 0 ? $".klc generated for {snap.Layouts.Count(l => l.GeneratedKlc != null)} of {custom}. " : "") +
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
        var kr = NewKeyboardRestore();
        foreach (var l in loadedPackage.Keyboard.Layouts.Where(l => l.Selected && l.IsCustom))
        {
            var plan = kr.Plan(loadedPackage, l);
            bool can = plan.Method is KeyboardInstallMethod.Msi or KeyboardInstallMethod.Dll or KeyboardInstallMethod.BuildFromKlc;
            KeyboardRestoreRows.Add(new KeyboardRestoreRow { Info = l, Action = plan.Description, CanInstall = can, Selected = can });
        }
    }

    KeyboardRestore NewKeyboardRestore() => new(Processes, Registry, new ElevatedPowerShell(), FindMsklc());

    /// <summary>Installs the ticked layouts, or only the given ones (right-click → Install only this).</summary>
    async Task InstallLayoutsAsync(List<KeyboardRestoreRow>? only = null)
    {
        if (loadedPackage == null) return;
        var rows = only ?? KeyboardRestoreRows.Where(r => r.Selected).ToList();
        if (rows.Count == 0) return;
        var keptOff = KeyboardRestoreRows.Where(r => !r.Selected).Select(r => r.Info.Klid).ToHashSet();
        if (!Confirm($"Install {rows.Count} keyboard layout(s)?\n\n" + string.Join("\n", rows.Select(r => "• " + r.Info.LayoutText + ": " + r.Action)) +
                     "\n\nWindows will ask for administrator permission. A restart may be needed before the layout appears.")) return;
        var pkg = loadedPackage; var kr = NewKeyboardRestore();
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
        foreach (var r in KeyboardRestoreRows) if (keptOff.Contains(r.Info.Klid)) r.Selected = false;
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
