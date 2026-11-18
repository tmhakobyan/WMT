using System.Collections.ObjectModel;
using WMT.Core;

namespace WMT.App.ViewModels;

/// <summary>One file of the restore dry run. Only "new file" and "overwrite" rows can be ticked.</summary>
public sealed class RestoreFileRow : ObservableObject
{
    public RestoreFileRow(RestoreOp op) { Op = op; }
    public RestoreOp Op { get; }
    public bool CanRestore => Op.Action is RestoreAction.Create or RestoreAction.Overwrite;
    public bool Selected
    {
        get => Op.Selected;
        set { if (value && !CanRestore) value = false; if (Op.Selected != value) { Op.Selected = value; Raise(); } }
    }
    public string ActionText => Op.Action switch
    {
        RestoreAction.Create => "New file",
        RestoreAction.Overwrite => "Replace",
        RestoreAction.SkipIdentical => "Already the same",
        RestoreAction.SkipExisting => "Exists (different)",
        RestoreAction.Unmapped => "No target profile",
        RestoreAction.Blocked => "Not possible",
        _ => Op.Action.ToString()
    };
    public string Target => Op.Target ?? "";
    public string Category => Op.Category;
    public string Reason => Op.Reason;
    public void Refresh() => RaiseAll();
}

/// <summary>One HKCU registry value from the package. Off by default; blocked values cannot be ticked.</summary>
public sealed class RegistryRow : ObservableObject
{
    public RegistryRow(RegistryOp op) { Op = op; }
    public RegistryOp Op { get; }
    public bool CanApply => !Op.Blocked;
    public bool Selected
    {
        get => Op.Selected;
        set { if (value && !CanApply) value = false; if (Op.Selected != value) { Op.Selected = value; Raise(); } }
    }
    public string KeyPath => Op.KeyPath;
    public string ValueName => Op.ValueName;
    public string Type => Op.Type;
    public string? OldValue => Op.OldValue;
    public string? NewValue => Op.NewValue;
    public string BlockedText => Op.Blocked ? "Yes" : "";
    public string Warning => Op.Warning;
    public void Refresh() => RaiseAll();
}

public sealed partial class MainViewModel
{
    public ObservableCollection<RestoreFileRow> RestoreFileRows { get; } = new();
    public ObservableCollection<RegistryRow> RestoreRegistryRows { get; } = new();
    /// <summary>"files:all", "files:none", "registry:all", ... — the Select all / Clear buttons of the restore lists.</summary>
    public RelayCommand ListSelectCommand { get; private set; } = null!;
    public RelayCommand GoToTabCommand { get; private set; } = null!;
    int mainTabIndex;
    public int MainTabIndex { get => mainTabIndex; set => Set(ref mainTabIndex, value); }
    bool planStale, justRestored, prereqChecked;
    const string RestoredText = "Restored just now";

    void InitRestoreLists()
    {
        ListSelectCommand = new RelayCommand(p =>
        {
            var parts = (p as string ?? "").Split(':');
            if (parts.Length == 2) SetAll(parts[0], parts[1] == "all");
        }, _ => IsIdle);
        GoToTabCommand = new RelayCommand(p =>
        {
            MainTabIndex = (p as string) switch { "keyboard" => 2, "settings" => 3, _ => MainTabIndex };
        });
    }

    /// <summary>Ticks or unticks every row of a restore list that can be ticked.</summary>
    public void SetAll(string list, bool value)
    {
        switch (list)
        {
            case "files": foreach (var r in RestoreFileRows) r.Selected = value; break;
            case "registry":
                if (value && RestoreRegistryRows.Any(r => r.CanApply) &&
                    !Confirm($"Tick all {RestoreRegistryRows.Count(r => r.CanApply)} registry values?\n\nRegistry values are program settings of the old Windows. Normally you only apply the few you need; Undo can put the old values back."))
                    return;
                foreach (var r in RestoreRegistryRows) r.Selected = value; break;
            case "settings": foreach (var r in SettingActionRows) r.Selected = value; break;
            case "keyboard": foreach (var r in KeyboardRestoreRows) r.Selected = value && r.CanInstall; break;
        }
        RaiseRestoreHint();
    }

    /// <summary>Ticks or unticks the highlighted rows of a list (right-click menu).</summary>
    public void SetChecked(string list, IEnumerable<object> rows, bool value)
    {
        foreach (var o in rows)
            switch (o)
            {
                case RestoreFileRow f: f.Selected = value; break;
                case RegistryRow r: r.Selected = value; break;
                case SettingActionRow s: s.Selected = value; break;
                case KeyboardRestoreRow k: k.Selected = value && k.CanInstall; break;
            }
        RaiseRestoreHint();
    }

    /// <summary>Right-click → "Restore/Apply/Install only this": acts on the given rows only, whatever is ticked elsewhere.</summary>
    public async Task RunOnlyAsync(string list, IList<object> rows)
    {
        if (!IsIdle || rows.Count == 0) return;
        switch (list)
        {
            case "files": await RestoreOnlyAsync(rows.OfType<RestoreFileRow>().ToList(), new()); break;
            case "registry": await RestoreOnlyAsync(new(), rows.OfType<RegistryRow>().ToList()); break;
            case "settings":
            {
                var sel = rows.OfType<SettingActionRow>().ToList();
                var blocked = sel.Where(s => s.Action.Blocked).ToList();
                if (blocked.Count == sel.Count) { Inform("Nothing to apply:\n\n" + string.Join("\n", blocked.Select(b => "• " + b.Action.Name + ": " + b.Action.Description))); return; }
                await ApplySettingsAsync(sel.Where(s => !s.Action.Blocked).Select(s => s.Action).ToList());
                break;
            }
            case "keyboard":
            {
                var sel = rows.OfType<KeyboardRestoreRow>().ToList();
                if (sel.All(k => !k.CanInstall)) { Inform("Nothing to install:\n\n" + string.Join("\n", sel.Select(k => "• " + k.Info.LayoutText + ": " + k.Action))); return; }
                await InstallLayoutsAsync(sel.Where(k => k.CanInstall).ToList());
                break;
            }
        }
    }

    async Task RestoreOnlyAsync(List<RestoreFileRow> files, List<RegistryRow> regs)
    {
        if (loadedPackage == null || restorePlan == null) return;
        var notes = new List<string>();
        var fileRows = new List<RestoreFileRow>();
        foreach (var f in files)
        {
            if (f.CanRestore) { fileRows.Add(f); continue; }
            if (f.Op.Action == RestoreAction.SkipExisting && f.Op.Target != null &&
                Confirm($"A different file already exists here:\n{f.Op.Target}\n\nReplace it with the file from the backup?\nThe current file is saved first, so 'Undo last restore' can bring it back."))
            {
                f.Op.Action = RestoreAction.Overwrite;
                f.Op.Reason = "Replace chosen by you (the current file is saved for Undo)";
                f.Refresh();
                fileRows.Add(f);
                continue;
            }
            notes.Add($"• {(f.Target.Length > 0 ? f.Target : f.Op.SourceOriginal)}: {f.ActionText} — {f.Reason}");
        }
        foreach (var r in regs.Where(r => !r.CanApply)) notes.Add($"• HKCU\\{r.KeyPath}\\{r.ValueName}: blocked — {r.Warning}");
        var regRows = regs.Where(r => r.CanApply).ToList();
        if (fileRows.Count + regRows.Count == 0) { if (notes.Count > 0) Inform("Nothing to restore for this item:\n\n" + string.Join("\n", notes.Take(20))); return; }
        if (notes.Count > 0) Inform("These items are skipped:\n\n" + string.Join("\n", notes.Take(20)));

        // Execute exactly these rows: a small plan with the same operations, ticked.
        var keep = restorePlan.Files.Concat<object>(restorePlan.Registry).Where(o => o is RestoreOp f ? f.Selected : ((RegistryOp)o).Selected).ToList();
        foreach (var f in fileRows) f.Op.Selected = true;
        foreach (var r in regRows) r.Op.Selected = true;
        var sub = new RestorePlan { Files = fileRows.Select(f => f.Op).ToList(), Registry = regRows.Select(r => r.Op).ToList(), Prerequisites = restorePlan.Prerequisites };
        var j = await ExecuteRestoreAsync(sub, fileRows, regRows);
        // Ticks elsewhere in the lists stay as the user had them; the rows just restored are shown as done.
        var restoredFiles = j == null ? new HashSet<RestoreOp>() : fileRows.Where(f => f.Op.Reason == RestoredText).Select(f => f.Op).ToHashSet();
        var restoredRegs = j == null ? new HashSet<RegistryOp>() : regRows.Where(r => r.Op.Warning.StartsWith(RestoredText)).Select(r => r.Op).ToHashSet();
        foreach (var f in RestoreFileRows) { if (!restoredFiles.Contains(f.Op)) f.Op.Selected = keep.Contains(f.Op) && f.CanRestore; f.Refresh(); }
        foreach (var r in RestoreRegistryRows) { if (!restoredRegs.Contains(r.Op)) r.Op.Selected = keep.Contains(r.Op) && r.CanApply; r.Refresh(); }
        RaiseRestoreHint();
    }

    /// <summary>Marks rows that were just restored so the list shows what is done.</summary>
    void MarkRestored(RestoreJournal j, IEnumerable<RestoreFileRow> files, IEnumerable<RegistryRow> regs)
    {
        foreach (var f in files)
        {
            if (f.Op.Target == null || j.Errors.Any(e => e.StartsWith(f.Op.Target + ":", StringComparison.OrdinalIgnoreCase))) continue;
            f.Op.Action = RestoreAction.SkipIdentical; f.Op.Selected = false; f.Op.Reason = RestoredText;
            f.Refresh();
        }
        foreach (var r in regs)
        {
            if (j.Errors.Any(e => e.StartsWith($"HKCU\\{r.KeyPath}\\{r.ValueName}:", StringComparison.OrdinalIgnoreCase))) continue;
            r.Op.Selected = false; r.Op.OldValue = r.Op.NewValue; r.Op.Warning = RestoredText + (r.Op.Warning.Length > 0 ? " · " + r.Op.Warning : "");
            r.Refresh();
        }
    }

    void OnProfileTargetChanged()
    {
        if (restorePlan != null) planStale = true;
        RaiseRestoreHint();
    }

    void RaiseRestoreHint() => Raise(nameof(RestoreNextStep));

    /// <summary>One plain sentence telling what to do next on the Restore page.</summary>
    public string RestoreNextStep
    {
        get
        {
            if (loadedPackage == null) return "Next: open your backup package — tab '2. Verify and Test Restore' → Open package.";
            var unmapped = ProfileMapRows.Where(r => r.FileCount > 0 && string.IsNullOrWhiteSpace(r.Target)).ToList();
            if (unmapped.Count > 0) return $"Next — Step 1: in the table above choose the target profile for {string.Join(", ", unmapped.Select(r => r.Source.Account))} (usually your profile on this computer).";
            if (restorePlan == null) return "Next — Step 2: press 'Dry run'. It only shows what would happen; nothing is changed.";
            if (planStale) return "You changed a target profile — press 'Dry run' again so the lists use it.";
            int files = RestoreFileRows.Count(f => f.Selected), regs = RestoreRegistryRows.Count(r => r.Selected);
            var done = justRestored ? "Restore finished: check that your programs open your files (checklist at the bottom). 'Undo last restore' puts everything back. " : "";
            return done + $"Next — Step 3: review the lists below ({files} file(s) and {regs} registry value(s) ticked; right-click a row to restore only that one). " +
                   "Step 4: press 'Restore selected'.";
        }
    }
}
