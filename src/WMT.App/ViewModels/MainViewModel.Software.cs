using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using WMT.Core;

namespace WMT.App.ViewModels;

public sealed partial class MainViewModel
{
    readonly ObservableCollection<SoftwareRow> softwareRows = new();
    string softwareStatus = "Not scanned", softwareSearch = "", softwareCategory = "All", softwareGroup = "None", softwareSelectionText = "", officeText = "Not scanned";
    bool includeWinget = true;
    public List<SoftwareItem> AllSoftware { get; private set; } = new();
    public string? WingetExport { get; private set; }
    public OfficeEnvironment? SourceOffice { get; private set; }

    public ICollectionView SoftwareView { get; private set; } = null!;
    public RelayCommand DiscoverSoftwareCommand { get; private set; } = null!;
    public RelayCommand SelectSoftwareCommand { get; private set; } = null!;
    public RelayCommand OfficeDetailsCommand { get; private set; } = null!;
    public RelayCommand ExportSoftwareCommand { get; private set; } = null!;
    public string[] SoftwareCategories { get; } = { "All", "Application", "Office", "Python", "OCR", "Runtime", "Development", "Portable" };
    public string[] SoftwareGroups { get; } = { "None", "Publisher", "Category", "Recovery method" };

    void InitSoftware()
    {
        SoftwareView = CollectionViewSource.GetDefaultView(softwareRows);
        SoftwareView.Filter = o => o is SoftwareRow r && MatchesSoftware(r);
        DiscoverSoftwareCommand = new RelayCommand(async () => await DiscoverSoftwareAsync(), () => IsIdle);
        SelectSoftwareCommand = new RelayCommand(p => SelectSoftware(p as string ?? ""), _ => IsIdle && softwareRows.Count > 0);
        OfficeDetailsCommand = new RelayCommand(async () => await ShowOfficeAsync(), () => IsIdle);
        ExportSoftwareCommand = new RelayCommand(ExportSoftware, () => IsIdle && softwareRows.Count > 0);
    }

    public int SoftwareCount => softwareRows.Count;
    public string SoftwareStatus { get => softwareStatus; private set => Set(ref softwareStatus, value); }
    public string SoftwareSelectionText { get => softwareSelectionText; private set => Set(ref softwareSelectionText, value); }
    public string OfficeText { get => officeText; private set => Set(ref officeText, value); }
    public bool IncludeWinget { get => includeWinget; set => Set(ref includeWinget, value); }
    public string SoftwareSearch { get => softwareSearch; set { if (Set(ref softwareSearch, value)) RefreshSoftwareView(); } }
    public string SoftwareCategory { get => softwareCategory; set { if (Set(ref softwareCategory, value)) RefreshSoftwareView(); } }
    public string SoftwareGroup
    {
        get => softwareGroup;
        set
        {
            if (!Set(ref softwareGroup, value)) return;
            SoftwareView.GroupDescriptions.Clear();
            var prop = value switch { "Publisher" => "Item.Publisher", "Category" => "Item.Category", "Recovery method" => "Item.RecoveryMethod", _ => null };
            if (prop != null) SoftwareView.GroupDescriptions.Add(new PropertyGroupDescription(prop));
        }
    }

    bool MatchesSoftware(SoftwareRow r)
    {
        if (softwareCategory != "All" && !r.Item.Category.Equals(softwareCategory, StringComparison.OrdinalIgnoreCase)) return false;
        var q = softwareSearch.Trim();
        if (q.Length == 0) return true;
        return r.Item.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Item.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.Item.Version.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Item.InstallLocation.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void RefreshSoftwareView() { SoftwareView.Refresh(); UpdateSoftwareSelectionText(); }

    void UpdateSoftwareSelectionText()
    {
        int shown = SoftwareView.Cast<object>().Count();
        SoftwareSelectionText = softwareRows.Count == 0 ? "" : $"Selected {softwareRows.Count(r => r.IsSelected)} of {softwareRows.Count} (showing {shown})";
        PlanStale();
    }

    public async Task DiscoverSoftwareAsync()
    {
        SoftwareScanResult? res = null; OfficeEnvironment? office = null;
        var wing = IncludeWinget;
        var jr = await RunJob("Discover software", ctx =>
        {
            var d = new SoftwareDiscovery(new WindowsUninstallSource(Registry), Processes);
            res = d.Scan(ctx.Progress, ctx.Token, includeWinget: wing);
            office = OfficeFingerprint.Read(Registry, res.Items);
            foreach (var i in res.Issues.Where(i => i.Severity >= IssueSeverity.Warning)) ctx.Warnings.Add(i.ToString());
            return Task.CompletedTask;
        });
        if (!Succeeded(jr) || res == null) { SoftwareStatus = jr?.State == JobState.Cancelled ? "Cancelled — list not changed" : "Failed — list not changed (see log)"; return; }
        var keep = softwareRows.Where(r => r.IsSelected).Select(r => r.Item.SelectionKey).ToHashSet();
        softwareRows.Clear();
        AllSoftware = res.Items;
        WingetExport = res.WingetExportJson;
        SourceOffice = office;
        foreach (var it in res.Items)
        {
            it.Selected = keep.Contains(it.SelectionKey);
            var row = new SoftwareRow(it);
            row.SelectionChanged += UpdateSoftwareSelectionText;
            softwareRows.Add(row);
        }
        RefreshSoftwareView();
        Raise(nameof(SoftwareCount));
        OfficeText = office != null ? OfficeFingerprint.Format(office) : "Not scanned";
        SoftwareStatus = $"{res.Items.Count} programs ({res.FilteredCount} system components/updates and {res.DuplicateCount} duplicates hidden)" +
            (wing ? $"; winget: {(res.WingetExportJson != null ? res.WingetPackageCount + " packages (supplementary)" : "not available")}" : "") +
            ". Missing entries do not mean a program is absent: portable/Store apps may not be listed.";
    }

    public void SelectSoftware(string mode)
    {
        foreach (var r in SoftwareView.Cast<SoftwareRow>().ToList())
            r.IsSelected = mode switch { "all" => true, "none" => false, "invert" => !r.IsSelected, _ => r.IsSelected };
        UpdateSoftwareSelectionText();
    }

    async Task ShowOfficeAsync()
    {
        OfficeEnvironment? o = null;
        var snapshot = AllSoftware.ToList();
        var jr = await RunJob("Office details", _ => { o = OfficeFingerprint.Read(Registry, snapshot); return Task.CompletedTask; });
        if (Succeeded(jr) && o != null)
        {
            SourceOffice = o;
            OfficeText = OfficeFingerprint.Format(o) + (snapshot.Count == 0 ? Environment.NewLine + "(Discover software first for the full list of language/proofing components.)" : "");
        }
    }

    void ExportSoftware()
    {
        var dir = PickFolder("Folder for the software list (CSV + JSON)");
        if (dir == null) return;
        var sb = new StringBuilder("Selected;Name;Publisher;Version;Build;Architecture;InstallationType;InstallLocation;DetectedSource;Category;RecoveryMethod;DependencyStatus;OfficialSource\r\n");
        static string C(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var s in AllSoftware)
            sb.Append(string.Join(';', new[] { s.Selected ? "1" : "0", C(s.Name), C(s.Publisher), C(s.Version), C(s.Build), C(s.Architecture), C(s.InstallationType), C(s.InstallLocation), C(s.DetectedSource), C(s.Category), C(s.RecoveryMethod), C(s.DependencyStatus), C(s.OfficialSource) })).Append("\r\n");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(dir, $"Software-{Environment.MachineName}-{stamp}.csv"), sb.ToString(), new UTF8Encoding(true));
        Json.WriteFile(Path.Combine(dir, $"Software-{Environment.MachineName}-{stamp}.json"), AllSoftware);
        Inform("Saved Software-*.csv and Software-*.json in " + dir);
    }
}
