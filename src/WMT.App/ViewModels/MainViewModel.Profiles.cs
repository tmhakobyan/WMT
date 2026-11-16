using System.Collections.ObjectModel;
using WMT.Core;

namespace WMT.App.ViewModels;

public sealed partial class MainViewModel
{
    string profilesStatus = "Not scanned";
    bool udDesktop, udDocuments, udDownloads, udPictures, udVideos, udMusic, udFavorites;
    bool includeWindowsSettings = true, includeRuntimes = true;
    string emptyFolderMode = "Exclude empty folders (default)";
    string? selectedCustomPath;

    public ObservableCollection<ProfileRow> Profiles { get; } = new();
    public ObservableCollection<string> CustomPaths { get; } = new();
    public RelayCommand DiscoverProfilesCommand { get; private set; } = null!;
    public RelayCommand SelectProfilesCommand { get; private set; } = null!;
    public RelayCommand AddCustomPathCommand { get; private set; } = null!;
    public RelayCommand RemoveCustomPathCommand { get; private set; } = null!;
    public string[] EmptyFolderModes { get; } = { "Exclude empty folders (default)", "Keep empty folders inside program settings", "Keep all empty folders" };

    void InitProfiles()
    {
        DiscoverProfilesCommand = new RelayCommand(async () => await DiscoverProfilesAsync(), () => IsIdle);
        SelectProfilesCommand = new RelayCommand(p => SelectProfiles(p as string ?? ""), _ => IsIdle && Profiles.Count > 0);
        AddCustomPathCommand = new RelayCommand(AddCustomPath, () => IsIdle);
        RemoveCustomPathCommand = new RelayCommand(() => { if (SelectedCustomPath != null) { CustomPaths.Remove(SelectedCustomPath); PlanStale(); } }, () => IsIdle && SelectedCustomPath != null);
    }

    public string ProfilesStatus { get => profilesStatus; private set => Set(ref profilesStatus, value); }
    public string? SelectedCustomPath { get => selectedCustomPath; set => Set(ref selectedCustomPath, value); }
    public bool UdDesktop { get => udDesktop; set { if (Set(ref udDesktop, value)) PlanStale(); } }
    public bool UdDocuments { get => udDocuments; set { if (Set(ref udDocuments, value)) PlanStale(); } }
    public bool UdDownloads { get => udDownloads; set { if (Set(ref udDownloads, value)) PlanStale(); } }
    public bool UdPictures { get => udPictures; set { if (Set(ref udPictures, value)) PlanStale(); } }
    public bool UdVideos { get => udVideos; set { if (Set(ref udVideos, value)) PlanStale(); } }
    public bool UdMusic { get => udMusic; set { if (Set(ref udMusic, value)) PlanStale(); } }
    public bool UdFavorites { get => udFavorites; set { if (Set(ref udFavorites, value)) PlanStale(); } }
    public bool IncludeWindowsSettings { get => includeWindowsSettings; set { if (Set(ref includeWindowsSettings, value)) PlanStale(); } }
    public bool IncludeRuntimes { get => includeRuntimes; set { if (Set(ref includeRuntimes, value)) PlanStale(); } }
    public string EmptyFolderMode { get => emptyFolderMode; set { if (Set(ref emptyFolderMode, value)) PlanStale(); } }

    public HashSet<string> SelectedUserDataCategories()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (UdDesktop) s.Add("Desktop"); if (UdDocuments) s.Add("Documents"); if (UdDownloads) s.Add("Downloads");
        if (UdPictures) s.Add("Pictures"); if (UdVideos) s.Add("Videos"); if (UdMusic) s.Add("Music"); if (UdFavorites) s.Add("Favorites");
        return s;
    }

    public async Task DiscoverProfilesAsync()
    {
        ProfileScanResult? res = null;
        var jr = await RunJob("Discover user profiles", ctx =>
        {
            res = new ProfileDiscovery(Registry, new WindowsAccountResolver()).Scan(ctx.Progress, ctx.Token);
            foreach (var i in res.Issues.Where(i => i.Severity >= IssueSeverity.Warning)) ctx.Warnings.Add(i.ToString());
            return Task.CompletedTask;
        });
        if (!Succeeded(jr) || res == null) { ProfilesStatus = jr?.State == JobState.Cancelled ? "Cancelled — list not changed" : "Failed — list not changed"; return; }
        var keep = Profiles.Where(p => p.IsSelected).Select(p => p.Item.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Profiles.Clear();
        foreach (var it in res.Items)
        {
            it.Selected = keep.Contains(it.Sid);
            var row = new ProfileRow(it);
            row.SelectionChanged += PlanStale;
            Profiles.Add(row);
        }
        int notOk = res.Items.Count(i => i.Access != AccessState.Accessible);
        ProfilesStatus = $"{res.Items.Count} user profile(s); {res.ExcludedSystemCount} system/service profiles hidden; {notOk} not fully accessible. " +
                         "Nothing is selected automatically. Old SIDs are never mapped to new ones automatically.";
    }

    public void SelectProfiles(string mode)
    {
        foreach (var r in Profiles) r.IsSelected = mode switch { "all" => true, "none" => false, "invert" => !r.IsSelected, "current" => r.Item.IsCurrentUser, _ => r.IsSelected };
    }

    void AddCustomPath()
    {
        var p = PickFolder("Add your own folder (any drive)");
        if (p == null) return;
        AddCustomPath(p);
    }

    public void AddCustomPath(string p)
    {
        var c = PathUtil.Canonical(p);
        foreach (var existing in CustomPaths.ToList())
        {
            if (PathUtil.SamePath(existing, c)) { Inform("This folder is already in the list."); return; }
            if (PathUtil.IsInsideOrSame(c, existing)) { Inform($"This folder is already included through {existing}."); return; }
            if (PathUtil.IsInsideOrSame(existing, c)) CustomPaths.Remove(existing);
        }
        CustomPaths.Add(c);
        PlanStale();
    }
}
