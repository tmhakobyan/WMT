using System.Collections.ObjectModel;

namespace WMT.Core.ViewModels;

/// <summary>UI-agnostic view model. Nothing is scanned until a Discover command runs.
/// All state returns to idle in a finally block (success, error or cancellation).</summary>
public sealed class MainViewModel : ObservableObject
{
    public static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(60);

    readonly ISoftwareScanner softwareScanner;
    readonly IProfileScanner profileScanner;
    readonly Func<IReadOnlyList<SoftwareItem>, OfficeInfo> officeReader;
    readonly ActivityLog log;
    readonly JobRunner runner;
    readonly LogTail tail;
    readonly Func<DateTime> utcNow;
    readonly List<SelectableSoftware> allSoftware = new();
    CancellationTokenSource? cts;
    DateTime lastActivityUtc;

    bool isBusy, isIndeterminate;
    string phase = "Idle", countText = "", lastActivityText = "", stopWarning = "";
    string softwareStatus = "Not scanned", profilesStatus = "Not scanned", officeDetails = "Not scanned";
    string softwareSearch = "", softwareSelectionText = "";
    double progressValue;

    public MainViewModel(ISoftwareScanner softwareScanner, IProfileScanner profileScanner,
        Func<IReadOnlyList<SoftwareItem>, OfficeInfo> officeReader, ActivityLog log,
        LogTail? tail = null, Func<DateTime>? utcNow = null)
    {
        this.softwareScanner = softwareScanner; this.profileScanner = profileScanner;
        this.officeReader = officeReader; this.log = log;
        this.tail = tail ?? new LogTail(log.FilePath, startAtEnd: true);
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        runner = new JobRunner(log);
        DiscoverSoftwareCommand = new RelayCommand(p => { _ = DiscoverSoftwareAsync(); }, () => IsIdle);
        DiscoverProfilesCommand = new RelayCommand(p => { _ = DiscoverProfilesAsync(); }, () => IsIdle);
        OfficeDetailsCommand = new RelayCommand(p => { _ = ShowOfficeDetailsAsync(); }, () => IsIdle);
        SelectSoftwareCommand = new RelayCommand(p => SelectSoftware(p as string ?? ""), () => IsIdle);
        SelectProfilesCommand = new RelayCommand(p => SelectProfiles(p as string ?? ""), () => IsIdle);
        CancelCommand = new RelayCommand(_ => Cancel(), () => IsBusy);
        log.Write("info", "app.started", "WMT v3.0 M1 started; nothing scanned yet.");
    }

    public ObservableCollection<SelectableSoftware> FilteredSoftware { get; } = new();
    public ObservableCollection<SelectableProfile> Profiles { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public RelayCommand DiscoverSoftwareCommand { get; }
    public RelayCommand DiscoverProfilesCommand { get; }
    public RelayCommand OfficeDetailsCommand { get; }
    public RelayCommand SelectSoftwareCommand { get; }
    public RelayCommand SelectProfilesCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) { Raise(nameof(IsIdle)); RaiseAllCanExecute(); } } }
    public bool IsIdle => !isBusy;
    public string Phase { get => phase; private set => Set(ref phase, value); }
    public string CountText { get => countText; private set => Set(ref countText, value); }
    public string LastActivityText { get => lastActivityText; private set => Set(ref lastActivityText, value); }
    public string StopWarning { get => stopWarning; private set => Set(ref stopWarning, value); }
    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }
    public double ProgressValue { get => progressValue; private set => Set(ref progressValue, value); }
    public string SoftwareStatus { get => softwareStatus; private set => Set(ref softwareStatus, value); }
    public string ProfilesStatus { get => profilesStatus; private set => Set(ref profilesStatus, value); }
    public string OfficeDetailsText { get => officeDetails; private set => Set(ref officeDetails, value); }
    public string SoftwareSelectionText { get => softwareSelectionText; private set => Set(ref softwareSelectionText, value); }
    public string SoftwareSearch
    {
        get => softwareSearch;
        set { if (Set(ref softwareSearch, value)) ApplyFilter(); }
    }

    void RaiseAllCanExecute()
    {
        DiscoverSoftwareCommand.RaiseCanExecuteChanged(); DiscoverProfilesCommand.RaiseCanExecuteChanged();
        OfficeDetailsCommand.RaiseCanExecuteChanged(); SelectSoftwareCommand.RaiseCanExecuteChanged();
        SelectProfilesCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
    }

    // ---- job plumbing ----------------------------------------------------------------------
    async Task<JobResult?> Run(string id, string title, Func<JobContext, Task> work)
    {
        if (IsBusy) return null;
        IsBusy = true;
        StopWarning = ""; Phase = title; CountText = ""; IsIndeterminate = true; ProgressValue = 0;
        Touch();
        cts = new CancellationTokenSource();
        JobResult? jr = null;
        try
        {
            var progress = new Progress<ProgressInfo>(OnProgress);
            jr = await runner.RunAsync(id, work, progress, cts.Token);
        }
        catch (Exception ex)
        {
            log.Write("error", "job.unexpected", ex.ToString());
            Phase = "Unexpected error: " + ex.Message;
        }
        finally
        {
            cts?.Dispose(); cts = null;
            IsBusy = false;
            IsIndeterminate = false; StopWarning = "";
        }
        if (jr != null)
        {
            Phase = jr.State switch
            {
                JobState.Completed => $"{title}: completed",
                JobState.CompletedWithWarnings => $"{title}: completed with {jr.Warnings.Count} warning(s)",
                JobState.Cancelled => $"{title}: cancelled",
                _ => $"{title}: FAILED - {jr.Error}"
            };
            foreach (var w in jr.Warnings) log.Write("warn", "job.warning", w, jr.CorrelationId);
        }
        return jr;
    }

    void Touch()
    {
        lastActivityUtc = utcNow();
        LastActivityText = "Last activity: " + lastActivityUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    void OnProgress(ProgressInfo p)
    {
        if (!IsBusy) return;
        Touch();
        Phase = p.Phase;
        CountText = p.Total is long t ? $"{p.Count}/{t}" : p.Count.ToString();
        IsIndeterminate = p.Total is null;
        ProgressValue = p.Total is long tt && tt > 0 ? Math.Min(100.0, 100.0 * p.Count / tt) : 0;
        StopWarning = "";
    }

    /// <summary>Called by a UI timer. No progress for 60 s shows "investigate", never "hung".</summary>
    public void CheckHeartbeat(DateTime nowUtc)
    {
        if (IsBusy && nowUtc - lastActivityUtc > HeartbeatLimit)
            StopWarning = "No recent progress — investigate";
    }

    public void Cancel()
    {
        if (cts == null) return;
        log.Write("info", "job.cancel_requested", Phase);
        Phase = "Cancel requested…";
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void PollLog()
    {
        foreach (var l in tail.ReadNew()) LogLines.Add(LogTail.ToDisplay(l));
        if (LogLines.Count > 2000) while (LogLines.Count > 1500) LogLines.RemoveAt(0);
    }

    // ---- software --------------------------------------------------------------------------
    public async Task DiscoverSoftwareAsync()
    {
        SoftwareScanResult? res = null;
        var jr = await Run("discover-software", "Discovering software", ctx =>
        {
            res = softwareScanner.Scan(ctx.Progress, ctx.Token);
            ctx.Warnings.AddRange(res.Warnings);
            return Task.CompletedTask;
        });
        if (jr == null) return;
        if (res != null && jr.State is JobState.Completed or JobState.CompletedWithWarnings)
        {
            var keep = allSoftware.Where(s => s.IsSelected).Select(s => s.Key).ToHashSet();
            allSoftware.Clear();
            foreach (var it in res.Items)
            {
                var s = new SelectableSoftware(it) { IsSelected = false };
                s.IsSelected = keep.Contains(s.Key);
                s.SelectionChanged += UpdateSoftwareSelectionText;
                allSoftware.Add(s);
            }
            ApplyFilter();
            SoftwareStatus = $"Scanned: {res.Items.Count} programs ({res.FilteredCount} system/update entries and " +
                $"{res.DuplicateCount} duplicates hidden; {res.Warnings.Count} warning(s)). Portable apps without an uninstall entry are not detected in M1.";
        }
        else SoftwareStatus = jr.State == JobState.Cancelled ? "Cancelled — list not updated" : $"Failed: {jr.Error} — list not updated";
    }

    void ApplyFilter()
    {
        FilteredSoftware.Clear();
        var q = softwareSearch.Trim();
        foreach (var s in allSoftware)
        {
            if (q.Length == 0 || Has(s.Item.Name, q) || Has(s.Item.Publisher, q) || Has(s.Item.Version, q) || Has(s.Item.Type, q))
                FilteredSoftware.Add(s);
        }
        UpdateSoftwareSelectionText();
    }
    static bool Has(string v, string q) => v.Contains(q, StringComparison.OrdinalIgnoreCase);

    void UpdateSoftwareSelectionText() =>
        SoftwareSelectionText = allSoftware.Count == 0 ? "" :
            $"Selected {allSoftware.Count(s => s.IsSelected)} of {allSoftware.Count} (showing {FilteredSoftware.Count})";

    /// <summary>mode: all | none | invert. Applies to the currently filtered rows; hidden rows keep their selection.</summary>
    public void SelectSoftware(string mode)
    {
        if (IsBusy) return;
        foreach (var s in FilteredSoftware.ToList())
            s.IsSelected = mode switch { "all" => true, "none" => false, "invert" => !s.IsSelected, _ => s.IsSelected };
    }

    public void SelectProfiles(string mode)
    {
        if (IsBusy) return;
        foreach (var p in Profiles.ToList())
            p.IsSelected = mode switch { "all" => true, "none" => false, "invert" => !p.IsSelected, _ => p.IsSelected };
    }

    // ---- profiles --------------------------------------------------------------------------
    public async Task DiscoverProfilesAsync()
    {
        ProfileScanResult? res = null;
        var jr = await Run("discover-profiles", "Discovering user profiles", ctx =>
        {
            res = profileScanner.Scan(ctx.Progress, ctx.Token);
            ctx.Warnings.AddRange(res.Warnings);
            return Task.CompletedTask;
        });
        if (jr == null) return;
        if (res != null && jr.State is JobState.Completed or JobState.CompletedWithWarnings)
        {
            var keep = Profiles.Where(p => p.IsSelected).Select(p => p.Item.Sid).ToHashSet();
            Profiles.Clear();
            foreach (var it in res.Items)
                Profiles.Add(new SelectableProfile(it) { IsSelected = keep.Contains(it.Sid) });
            int notOk = res.Items.Count(i => i.Access != AccessState.Accessible);
            ProfilesStatus = $"Scanned: {res.Items.Count} user profile(s); {res.ExcludedSystemCount} system/service profile(s) hidden; " +
                $"{notOk} not fully accessible. Old SIDs are never auto-mapped to new ones.";
        }
        else ProfilesStatus = jr.State == JobState.Cancelled ? "Cancelled — list not updated" : $"Failed: {jr.Error} — list not updated";
    }

    // ---- office ----------------------------------------------------------------------------
    public async Task ShowOfficeDetailsAsync()
    {
        var snapshot = allSoftware.Select(s => s.Item).ToList();
        OfficeInfo? info = null;
        var jr = await Run("office-details", "Reading Office details", ctx =>
        {
            info = officeReader(snapshot);
            return Task.CompletedTask;
        });
        if (jr == null) return;
        if (info != null && jr.State is JobState.Completed or JobState.CompletedWithWarnings)
        {
            var text = OfficeFingerprint.Format(info);
            if (snapshot.Count == 0) text += Environment.NewLine + "Note: software has not been scanned, so the list of proofing components may be incomplete.";
            OfficeDetailsText = text;
        }
        else OfficeDetailsText = jr.State == JobState.Cancelled ? "Cancelled" : $"Failed: {jr.Error}";
    }
}
