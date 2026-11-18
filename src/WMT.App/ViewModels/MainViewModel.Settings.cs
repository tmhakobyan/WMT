using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using WMT.Core;

namespace WMT.App.ViewModels;

public sealed class SettingRow : ObservableObject
{
    readonly Action changed;
    public SettingRow(SettingItem item, Action changed) { Item = item; this.changed = changed; }
    public SettingItem Item { get; }
    public bool IsSelected { get => Item.Selected; set { if (Item.Selected != value) { Item.Selected = value; Raise(); changed(); } } }
    public string AdminText => Item.RequiresAdmin ? "Yes" : "";
    public string SignOutText => Item.RequiresSignOut ? "Yes" : "";
}

public sealed class SettingActionRow : ObservableObject
{
    public SettingActionRow(SettingAction a) { Action = a; }
    public SettingAction Action { get; }
    public bool Selected { get => Action.Selected; set { if (!Action.Blocked && Action.Selected != value) { Action.Selected = value; Raise(); } } }
    public bool CanApply => !Action.Blocked;
    public string AdminText => Action.RequiresAdmin ? "Yes" : "";
    public string SignOutText => Action.RequiresSignOut ? "Yes" : "";
}

public sealed partial class MainViewModel
{
    WindowsSettingsSnapshot? settings;
    string settingsStatus = "Not scanned", settingsGroup = "All", settingsRestoreStatus = "";
    readonly ObservableCollection<SettingRow> settingRows = new();
    RestoreJournal? lastSettingsJournal; string? lastSettingsJournalPath;
    public ICollectionView SettingsView { get; private set; } = null!;
    public ObservableCollection<SettingActionRow> SettingActionRows { get; } = new();
    public string[] SettingGroupChoices { get; } = new[] { "All" }.Concat(SettingGroups.All).ToArray();
    public RelayCommand DiscoverSettingsCommand { get; private set; } = null!;
    public RelayCommand SelectSettingsCommand { get; private set; } = null!;
    public RelayCommand SettingsDryRunCommand { get; private set; } = null!;
    public RelayCommand ApplySettingsCommand { get; private set; } = null!;
    public RelayCommand UndoSettingsCommand { get; private set; } = null!;
    public WindowsSettingsSnapshot? Settings => settings;
    public int SettingsCount => settingRows.Count;
    public string SettingsStatus { get => settingsStatus; private set => Set(ref settingsStatus, value); }
    public string SettingsRestoreStatus { get => settingsRestoreStatus; private set => Set(ref settingsRestoreStatus, value); }
    public string SettingsGroup { get => settingsGroup; set { if (Set(ref settingsGroup, value)) SettingsView.Refresh(); } }

    void InitSettings()
    {
        SettingsView = CollectionViewSource.GetDefaultView(settingRows);
        SettingsView.Filter = o => o is SettingRow r && (settingsGroup == "All" || r.Item.Group == settingsGroup);
        DiscoverSettingsCommand = new RelayCommand(async () => await DiscoverSettingsAsync(), () => IsIdle);
        SelectSettingsCommand = new RelayCommand(p =>
        {
            foreach (var r in SettingsView.Cast<SettingRow>()) r.IsSelected = (p as string) == "recommended" ? r.Item.Classification == Classification.Recommended : false;
        }, _ => IsIdle && settingRows.Count > 0);
        SettingsDryRunCommand = new RelayCommand(SettingsDryRun, () => IsIdle && loadedPackage?.WindowsSettings != null);
        ApplySettingsCommand = new RelayCommand(async () => await ApplySettingsAsync(null), () => IsIdle && SettingActionRows.Any(a => a.Selected));
        UndoSettingsCommand = new RelayCommand(UndoSettings, () => IsIdle && lastSettingsJournal != null && !lastSettingsJournal.UndoDone);
    }

    public async Task DiscoverSettingsAsync()
    {
        WindowsSettingsSnapshot? snap = null;
        var jr = await RunJob("Discover Windows settings", job =>
        {
            snap = new WindowsSettingsCapture(new SettingsCaptureEnvironment { Registry = Registry, Processes = Processes, Token = job.Token }).Capture(job.Progress);
            foreach (var i in snap.Issues.Where(i => i.Severity >= IssueSeverity.Warning)) job.Warnings.Add(i.ToString());
            return Task.CompletedTask;
        });
        if (!Succeeded(jr) || snap == null) { SettingsStatus = "Discovery failed or was cancelled — see log"; return; }
        var keep = settingRows.ToDictionary(r => r.Item.Id, r => r.IsSelected);
        settings = snap;
        settingRows.Clear();
        foreach (var i in snap.Items)
        {
            if (keep.TryGetValue(i.Id, out var s)) i.Selected = s;
            settingRows.Add(new SettingRow(i, PlanStale));
        }
        SettingsView.Refresh();
        Raise(nameof(SettingsCount));
        SettingsStatus = $"{snap.Items.Count} settings of {snap.Account} ({string.Join(", ", snap.Items.GroupBy(i => i.Group).Select(g => $"{g.Key} {g.Count()}"))}). " +
                         "Only the signed-in user's settings are read. Wi-Fi passwords are never exported." +
                         (snap.Issues.Count > 0 ? $" {snap.Issues.Count} note(s) in the log." : "");
        foreach (var i in snap.Issues) Log.Info("settings.note", i.ToString());
        PlanStale();
    }

    SettingsRestoreRequest SettingsRequest() => new()
    {
        Package = loadedPackage!, Snapshot = loadedPackage!.WindowsSettings ?? new WindowsSettingsSnapshot(), Keyboard = loadedPackage.Keyboard,
        TargetProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    };

    SettingsRestoreEngine SettingsEngine() => new(Log, Registry, RegistryWriter, Processes, new ElevatedPowerShell());

    void SettingsDryRun()
    {
        SettingActionRows.Clear();
        if (loadedPackage?.WindowsSettings == null) { SettingsRestoreStatus = "Open a package first (Backup & Recovery → Open package)."; return; }
        foreach (var a in SettingsEngine().DryRun(SettingsRequest()).Where(a => loadedPackage.WindowsSettings.Items.First(i => i.Id == a.ItemId).Selected))
            SettingActionRows.Add(new SettingActionRow(a));
        SettingsRestoreStatus = $"Dry run: {SettingActionRows.Count(a => a.Selected)} change(s) proposed, {SettingActionRows.Count(a => a.Action.Blocked)} not needed or manual. Nothing was changed.";
    }

    /// <summary>Applies the ticked settings, or only the given ones (right-click → Apply only this).</summary>
    async Task ApplySettingsAsync(List<SettingAction>? only = null)
    {
        if (loadedPackage == null) return;
        var sel = only ?? SettingActionRows.Where(a => a.Selected).Select(a => a.Action).ToList();
        if (sel.Count == 0) { Inform("Nothing is ticked. Tick settings in the list (or 'Select all'), or right-click one → Apply only this item."); return; }
        var keptOff = SettingActionRows.Where(a => !a.Selected && !a.Action.Blocked).Select(a => a.Action.ItemId + "|" + a.Action.Name).ToHashSet();
        int admin = sel.Count(a => a.RequiresAdmin);
        if (!Confirm($"Apply {sel.Count} setting(s) for the signed-in user?\n\n" + string.Join("\n", sel.Take(25).Select(a => "• " + a.Name + ": " + a.Description)) + (sel.Count > 25 ? "\n…" : "") +
                     (admin > 0 ? $"\n\n{admin} of them need administrator permission (Windows will ask)." : "") + "\n\nOld registry values are saved; you can Undo.")) return;
        var req = SettingsRequest(); var engine = SettingsEngine();
        RestoreJournal? j = null;
        await RunJob("Apply Windows settings", async job => { j = await engine.ExecuteAsync(req, sel, job.Progress, job.Token); if (j.Failed > 0) job.Warnings.Add($"{j.Failed} setting(s) failed"); });
        if (j == null) return;
        lastSettingsJournal = j;
        lastSettingsJournalPath = Path.Combine(loadedPackage.Layout.Status, "Settings-" + j.JournalId, "journal.json");
        var status = $"Applied {j.Restored}, failed {j.Failed}." + (j.Errors.Count > 0 ? " " + string.Join(" | ", j.Errors.Take(5)) : "") + " Sign out and in to see every change.";
        SettingsDryRun();
        // Keep the user's unticks after the list is recalculated (applied settings now show "Already the same").
        foreach (var a in SettingActionRows) if (keptOff.Contains(a.Action.ItemId + "|" + a.Action.Name)) a.Selected = false;
        SettingsRestoreStatus = status;
    }

    void UndoSettings()
    {
        if (lastSettingsJournal == null || lastSettingsJournalPath == null) return;
        if (!Confirm("Undo the last settings restore? Registry values and copied files are put back; commands (Wi-Fi, printers, tasks) show their manual undo.")) return;
        var problems = new RestoreEngine(Log, Registry, RegistryWriter).Undo(lastSettingsJournal, lastSettingsJournalPath);
        SettingsRestoreStatus = problems.Count == 0 ? "Undone." : "Undone, with manual steps:\n" + string.Join("\n", problems);
    }
}
