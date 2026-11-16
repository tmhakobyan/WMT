using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WMT.App.Services;
using WMT.Core;

namespace WMT.App.ViewModels;

public sealed partial class MainViewModel
{
    MigrationPlan? plan;
    FolderNode? currentFolder;
    string planStatus = "No plan yet. Select programs and profiles, then press 'Generate targeted plan'.", planSummary = "", explorerSearch = "", viewMode = "Details", sortMode = "Name", pageText = "", currentPath = "", detailsText = "";
    bool sortDescending, planIsStale;
    int pageIndex, pageCount = 1;
    CancellationTokenSource? thumbCts;
    readonly List<WeakReference<TreeNodeVM>> treeVms = new();
    ExplorerItemVM? selectedExplorerItem;

    public MigrationPlan? Plan => plan;
    public ObservableCollection<TreeNodeVM> TreeRoots { get; } = new();
    public ObservableCollection<ExplorerItemVM> ExplorerItems { get; } = new();
    public ObservableCollection<Dependency> VisibleDependencies { get; } = new();
    public ObservableCollection<Component> VisibleComponents { get; } = new();
    public ObservableCollection<Issue> PlanIssues { get; } = new();
    public RelayCommand GeneratePlanCommand { get; private set; } = null!;
    public RelayCommand UpCommand { get; private set; } = null!;
    public RelayCommand NextPageCommand { get; private set; } = null!;
    public RelayCommand PrevPageCommand { get; private set; } = null!;
    public RelayCommand OpenItemCommand { get; private set; } = null!;
    public RelayCommand SelectRecommendedCommand { get; private set; } = null!;
    public RelayCommand ClearPlanSelectionCommand { get; private set; } = null!;
    public string[] ViewModes { get; } = { "Details", "Small Icons", "Large Icons", "Extra Large Icons" };
    public string[] SortModes { get; } = { "Name", "Type", "Size", "Date" };

    public const string ClassificationLegend =
        "Recommended — keep: your own files with a clear reason to restore by copying (selected by default).\n" +
        "Review — you decide: may be useful, may be re-created by the program (not selected by default).\n" +
        "InstallerManaged — created by an installer: the install information is kept, the files are not copied.\n" +
        "Unsupported — cannot be restored automatically: manual steps are listed.";
    public string Legend => ClassificationLegend;

    void InitPlan()
    {
        GeneratePlanCommand = new RelayCommand(async () => await GeneratePlanAsync(), () => IsIdle);
        UpCommand = new RelayCommand(() => { if (currentFolder?.Parent != null) NavigateTo(currentFolder.Parent, false); }, () => currentFolder?.Parent != null);
        NextPageCommand = new RelayCommand(() => { pageIndex++; RenderPage(); }, () => pageIndex < pageCount - 1);
        PrevPageCommand = new RelayCommand(() => { pageIndex--; RenderPage(); }, () => pageIndex > 0);
        OpenItemCommand = new RelayCommand(p => { if (p is ExplorerItemVM vm && vm.Item.IsFolder && vm.Item.Folder != null) NavigateTo(vm.Item.Folder, false); });
        SelectRecommendedCommand = new RelayCommand(() => { plan?.EnsureTree().SelectByClassification(Classification.Recommended, true); RefreshSelectionViews(); }, () => IsIdle && plan != null);
        ClearPlanSelectionCommand = new RelayCommand(() => { if (plan == null) return; var t = plan.EnsureTree(); foreach (var c in t.Categories) t.SetSelected(c, false); RefreshSelectionViews(); }, () => IsIdle && plan != null);
    }

    public string PlanStatus { get => planStatus; private set => Set(ref planStatus, value); }
    public string PlanSummary { get => planSummary; private set => Set(ref planSummary, value); }
    public string CurrentPath { get => currentPath; private set => Set(ref currentPath, value); }
    public string PageText { get => pageText; private set => Set(ref pageText, value); }
    public string DetailsText { get => detailsText; private set => Set(ref detailsText, value); }
    public bool PlanIsStale { get => planIsStale; private set => Set(ref planIsStale, value); }
    public bool IsDetailsView => viewMode == "Details";
    public bool IsIconView => viewMode != "Details";
    public int IconSize => viewMode switch { "Small Icons" => 16, "Large Icons" => 80, "Extra Large Icons" => 192, _ => 16 };
    public double TileWidth => viewMode switch { "Small Icons" => 220, "Large Icons" => 120, "Extra Large Icons" => 220, _ => 200 };
    int PageSize => viewMode switch { "Details" => 500, "Small Icons" => 400, "Large Icons" => 150, _ => 40 };

    public string ExplorerSearch { get => explorerSearch; set { if (Set(ref explorerSearch, value)) { pageIndex = 0; RenderPage(); } } }
    public string ViewMode
    {
        get => viewMode;
        set { if (Set(ref viewMode, value)) { Raise(nameof(IsDetailsView)); Raise(nameof(IsIconView)); Raise(nameof(IconSize)); Raise(nameof(TileWidth)); pageIndex = 0; RenderPage(); } }
    }
    public string SortMode { get => sortMode; set { if (Set(ref sortMode, value)) RenderPage(); } }
    public bool SortDescending { get => sortDescending; set { if (Set(ref sortDescending, value)) RenderPage(); } }
    public ExplorerItemVM? SelectedExplorerItem { get => selectedExplorerItem; set { if (Set(ref selectedExplorerItem, value)) UpdateDetails(); } }

    void PlanStale()
    {
        if (plan != null && !PlanIsStale) { PlanIsStale = true; PlanStatus = "The selection changed after the plan was generated. Generate the plan again before backing up."; }
    }

    DiscoveryContext BuildContext(JobContext job, IReadOnlyList<SoftwareItem> software, IReadOnlyList<UserProfileInfo> profiles, DiscoveryOptions opts) => new()
    {
        AllSoftware = AllSoftware, SelectedSoftware = software, SelectedProfiles = profiles, Options = opts, Registry = Registry, Processes = Processes,
        Log = Log, Progress = job.Progress, Token = job.Token, CurrentUserSid = ProfileDiscovery.CurrentUserSid(), Office = SourceOffice
    };

    public DiscoveryOptions CurrentOptions()
    {
        var opts = new DiscoveryOptions
        {
            CustomPaths = CustomPaths.ToList(), IncludeWindowsSettings = IncludeWindowsSettings, IncludeRuntimes = IncludeRuntimes,
            EmptyFolders = EmptyFolderMode.StartsWith("Keep all") ? WMT.Core.EmptyFolderMode.KeepAllEmpty : EmptyFolderMode.StartsWith("Keep empty") ? WMT.Core.EmptyFolderMode.KeepEmptyInApplicationRoots : WMT.Core.EmptyFolderMode.ExcludeEmpty,
        };
        var cats = SelectedUserDataCategories();
        foreach (var p in Profiles.Where(p => p.IsSelected)) opts.UserData[p.Item.Sid] = new HashSet<string>(cats, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Destination)) opts.ExcludedPaths.Add(Destination);
        return opts;
    }

    public async Task GeneratePlanAsync()
    {
        var sw = softwareRows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        var pr = Profiles.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        var opts = CurrentOptions();
        if (sw.Count == 0 && opts.CustomPaths.Count == 0 && !opts.UserData.Any(kv => kv.Value.Count > 0))
        {
            Inform("Nothing is selected. Select programs in Software, and/or profiles with user-data folders or custom folders in User Profiles.");
            return;
        }
        MigrationPlan? result = null;
        var jr = await RunJob("Generate targeted plan", job =>
        {
            result = new PlanBuilder().Build(BuildContext(job, sw, pr, opts));
            foreach (var i in result.Issues.Where(i => i.Severity >= IssueSeverity.Error)) job.Warnings.Add(i.ToString());
            return Task.CompletedTask;
        });
        if (!Succeeded(jr) || result == null) { PlanStatus = jr?.State == JobState.Cancelled ? "Cancelled — previous plan kept" : "Plan failed — see log"; return; }
        plan = result;
        if (result.Office != null) SourceOffice = result.Office;
        PlanIsStale = false;
        foreach (var r in softwareRows) r.Raise(nameof(SoftwareRow.DependencyStatus));
        var tree = plan.EnsureTree();
        tree.NodeChanged += _ => { };
        treeVms.Clear();
        TreeRoots.Clear();
        foreach (var c in tree.Categories) TreeRoots.Add(new TreeNodeVM(c, this));
        PlanIssues.Clear();
        foreach (var i in plan.Issues.OrderByDescending(i => i.Severity)) PlanIssues.Add(i);
        var inacc = plan.Index.Directories.Count(d => d.State == FolderState.Inaccessible);
        PlanStatus = $"Plan ready: {plan.Index.Files.Count:N0} eligible files in {tree.Categories.Count} categories; {plan.Components.Count} components; {plan.Dependencies.Count} dependencies " +
                     $"({plan.Dependencies.Count(d => d.Confidence == Confidence.Unknown)} unknown); {inacc} inaccessible folders; {plan.Index.ExcludedFiles:N0} files excluded by rules; " +
                     $"{plan.Index.DuplicateFilesSkipped} duplicates and {plan.Index.ReparsePointsSkipped} links skipped.";
        RefreshSelectionViews();
        if (tree.Categories.Count > 0) NavigateTo(tree.Categories[0], false);
        else { ExplorerItems.Clear(); CurrentPath = ""; }
        ClearBackupPreview();
    }

    internal void RegisterTreeVm(TreeNodeVM vm) => treeVms.Add(new WeakReference<TreeNodeVM>(vm));

    public void SetFolderSelected(FolderNode node, bool selected)
    {
        if (plan == null || IsBusy) return;
        plan.EnsureTree().SetSelected(node, selected);
        RefreshSelectionViews();
    }

    public void SetFileSelected(int index, bool selected)
    {
        if (plan == null || IsBusy) return;
        plan.EnsureTree().SetFileSelected(index, selected);
        RefreshSelectionViews();
    }

    void RefreshSelectionViews()
    {
        if (plan == null) { PlanSummary = ""; return; }
        var t = plan.EnsureTree();
        PlanSummary = $"Allowed: {t.TotalEligible:N0} files ({PathUtil.FormatBytes(t.TotalEligibleBytes)}) · Selected: {t.TotalSelected:N0} files ({PathUtil.FormatBytes(t.TotalSelectedBytes)})";
        treeVms.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in treeVms) if (w.TryGetTarget(out var vm)) vm.Refresh();
        foreach (var e in ExplorerItems) e.Refresh();
        ClearBackupPreview();
    }

    public void NavigateTo(FolderNode node, bool fromTree)
    {
        if (plan == null) return;
        currentFolder = node;
        CurrentPath = node.FullPath ?? node.Name;
        pageIndex = 0;
        if (!string.IsNullOrEmpty(explorerSearch)) { explorerSearch = ""; Raise(nameof(ExplorerSearch)); }
        RenderPage();
        if (!fromTree) SyncTreeSelection(node);
        UpdateDependencyPanel(node);
    }

    void SyncTreeSelection(FolderNode node)
    {
        var chain = new Stack<FolderNode>();
        for (var n = node; n != null; n = n.Parent) chain.Push(n);
        IEnumerable<TreeNodeVM> level = TreeRoots;
        TreeNodeVM? vm = null;
        foreach (var n in chain)
        {
            vm = level.FirstOrDefault(v => ReferenceEquals(v.Node, n));
            if (vm == null) return;
            if (!ReferenceEquals(n, node)) { vm.IsExpanded = true; vm.EnsureChildren(); level = vm.Children; }
        }
        vm?.SetSelectedSilently(true);
    }

    void RenderPage()
    {
        thumbCts?.Cancel();
        ExplorerItems.Clear();
        if (plan == null || currentFolder == null) { PageText = ""; return; }
        var sort = sortMode switch { "Type" => ExplorerSort.Type, "Size" => ExplorerSort.Size, "Date" => ExplorerSort.Date, _ => ExplorerSort.Name };
        var all = ExplorerQuery.List(plan.EnsureTree(), currentFolder, explorerSearch, sort, sortDescending);
        var (page, pages) = ExplorerQuery.Page(all, pageIndex, PageSize);
        pageCount = pages; pageIndex = Math.Clamp(pageIndex, 0, pages - 1);
        var size = IconSize;
        foreach (var it in page) ExplorerItems.Add(new ExplorerItemVM(it, this, size));
        PageText = $"{all.Count:N0} items · page {pageIndex + 1} of {pages}" + (string.IsNullOrEmpty(explorerSearch) ? "" : " (search results in this folder and below)");
        if (size >= 80) LoadThumbnails(size);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    void LoadThumbnails(int size)
    {
        thumbCts = new CancellationTokenSource();
        var ct = thumbCts.Token;
        var targets = ExplorerItems.Where(e => !e.Item.IsFolder && e.Item.FullPath != null && ThumbnailService.IsImage(e.Item.FullPath)).ToList();
        if (targets.Count == 0) return;
        var dispatcher = Application.Current.Dispatcher;
        Task.Run(() =>
        {
            foreach (var t in targets)
            {
                if (ct.IsCancellationRequested) return;
                var img = ThumbnailService.Load(t.Item.FullPath!, size, ct);
                if (img != null && !ct.IsCancellationRequested) dispatcher.InvokeAsync(() => { if (!ct.IsCancellationRequested) t.Image = img; });
            }
        }, ct);
    }

    void UpdateDependencyPanel(FolderNode node)
    {
        VisibleDependencies.Clear(); VisibleComponents.Clear();
        if (plan == null) return;
        var cat = node; while (cat.Parent != null) cat = cat.Parent;
        var app = cat.Name.StartsWith("Microsoft Word") ? "Microsoft Word" : cat.Name.StartsWith("Python") || cat.Name.StartsWith("OCR") ? "Python" : cat.Name.StartsWith("Windows") ? "Windows" : null;
        var deps = app == null ? plan.Dependencies.Take(200) : plan.Dependencies.Where(d => d.Application == app || (app == "Python" && d.Application == "OCR"));
        foreach (var d in deps.OrderBy(d => d.Confidence).Take(500)) VisibleDependencies.Add(d);
        var comps = app == null ? plan.Components.Where(c => c.MigrationMethod != MigrationMethod.CopyFile) : plan.Components.Where(c => c.Application == app || c.Application == "Microsoft Office" && app == "Microsoft Word");
        foreach (var c in comps.Take(300)) VisibleComponents.Add(c);
        DetailsText = $"{node.Name}\n{node.FullPath}\nSelected {node.SelectedCount:N0} of {node.EligibleCount:N0} files ({PathUtil.FormatBytes(node.SelectedBytes)} of {PathUtil.FormatBytes(node.EligibleBytes)})" +
                      (node.Note != null ? "\n" + node.Note : "");
    }

    void UpdateDetails()
    {
        var it = selectedExplorerItem;
        if (it == null || plan == null) return;
        if (it.Item.IsFolder) { if (it.Item.Folder != null) UpdateDependencyPanel(it.Item.Folder); return; }
        var f = plan.Index.Files[it.Item.FileIndex];
        DetailsText = $"{Path.GetFileName(f.SourcePath)}\nSource: {f.SourcePath}\nCategory: {f.Category}\nClass: {f.Classification} — {f.Note}\n" +
                      $"Recovery method: {(f.Classification == Classification.InstallerManaged ? "Reinstall" : "Copy file")}\nDestination rule: {f.DestinationRule}\n" +
                      $"Size: {PathUtil.FormatBytes(f.Size)} · Modified {f.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}" +
                      (f.ProfileSid != null ? $"\nProfile SID: {f.ProfileSid}" : "");
    }
}
