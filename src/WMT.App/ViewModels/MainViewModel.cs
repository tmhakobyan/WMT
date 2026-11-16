using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WMT.Core;

namespace WMT.App.ViewModels;

/// <summary>Shared state, job plumbing and status bar (NFR-001/002, UI-008..010). Tab logic lives in the partial files.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(60);
    readonly JobRunner runner;
    readonly LogTail tail;
    CancellationTokenSource? cts;
    DateTime lastActivityUtc = DateTime.UtcNow;
    bool isBusy, isIndeterminate;
    string operation = "", phase = "Ready. Nothing has been scanned yet.", countText = "", lastActivityText = "", stopWarning = "";
    double progressValue;

    public ActivityLog Log { get; }
    public IRegistryReader Registry { get; }
    public IRegistryWriter RegistryWriter { get; }
    public IProcessRunner Processes { get; }
    public Func<string, bool> Confirm { get; set; } = msg => MessageBox.Show(msg, "WMT", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public Action<string> Inform { get; set; } = msg => MessageBox.Show(msg, "WMT", MessageBoxButton.OK, MessageBoxImage.Information);
    public Func<string, string?> PickFolder { get; set; } = title =>
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    };

    public MainViewModel(ActivityLog log, IRegistryReader registry, IRegistryWriter writer, IProcessRunner processes)
    {
        Log = log; Registry = registry; RegistryWriter = writer; Processes = processes;
        runner = new JobRunner(log);
        tail = new LogTail(log.FilePath, startAtEnd: true);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        InitSoftware(); InitProfiles(); InitKeyboard(); InitSettings(); InitPlan(); InitBackup();
        log.Info("app.started", $"WMT {Toolkit.Version} started (build {Toolkit.BuildId}). Nothing is scanned until you press a Discover button.");
    }

    public ObservableCollection<string> LogLines { get; } = new();
    public RelayCommand CancelCommand { get; }

    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) { Raise(nameof(IsIdle)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsIdle => !isBusy;
    public string Operation { get => operation; private set => Set(ref operation, value); }
    public string Phase { get => phase; private set => Set(ref phase, value); }
    public string CountText { get => countText; private set => Set(ref countText, value); }
    public string LastActivityText { get => lastActivityText; private set => Set(ref lastActivityText, value); }
    public string StopWarning { get => stopWarning; private set => Set(ref stopWarning, value); }
    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }
    public double ProgressValue { get => progressValue; private set => Set(ref progressValue, value); }

    /// <summary>Runs <paramref name="work"/> in the background; buttons are disabled and always restored in finally.</summary>
    public async Task<JobResult?> RunJob(string title, Func<JobContext, Task> work)
    {
        if (IsBusy) return null;
        IsBusy = true; Operation = title; Phase = "Starting…"; CountText = ""; IsIndeterminate = true; ProgressValue = 0; StopWarning = "";
        Touch();
        cts = new CancellationTokenSource();
        JobResult? jr = null;
        try
        {
            var progress = new Progress<ProgressInfo>(OnProgress);
            jr = await runner.RunAsync(title, work, progress, cts.Token);
        }
        catch (Exception ex) { Log.Error("job.unexpected", ex.ToString()); Phase = "Unexpected error: " + ex.Message; }
        finally
        {
            cts?.Dispose(); cts = null;
            IsBusy = false; IsIndeterminate = false; StopWarning = "";
        }
        if (jr != null)
        {
            Phase = jr.State switch
            {
                JobState.Completed => $"{title}: completed",
                JobState.CompletedWithWarnings => $"{title}: completed with {jr.Warnings.Count} warning(s) — see log",
                JobState.Cancelled => $"{title}: cancelled (partial results were not applied)",
                _ => $"{title}: FAILED — {jr.Error}"
            };
            ProgressValue = jr.State == JobState.Completed || jr.State == JobState.CompletedWithWarnings ? 100 : ProgressValue;
        }
        return jr;
    }

    public static bool Succeeded(JobResult? jr) => jr != null && jr.State is JobState.Completed or JobState.CompletedWithWarnings;

    void Touch()
    {
        lastActivityUtc = DateTime.UtcNow;
    }

    void OnProgress(ProgressInfo p)
    {
        if (!IsBusy) return;
        Touch();
        Phase = p.Phase + (p.Detail != null ? " — " + Shorten(p.Detail, 90) : "");
        CountText = p.Total is long t ? $"{p.Count:N0} / {t:N0}" : $"{p.Count:N0}";
        IsIndeterminate = p.Total is null;
        ProgressValue = p.Total is long tt && tt > 0 ? Math.Min(100.0, 100.0 * p.Count / tt) : 0;
        StopWarning = "";
    }

    static string Shorten(string s, int n) => s.Length <= n ? s : "…" + s[^(n - 1)..];

    /// <summary>Called every second by the window timer: heartbeat text, 60-s notice and incremental log reading.</summary>
    public void Tick(DateTime nowUtc)
    {
        if (IsBusy)
        {
            var idle = nowUtc - lastActivityUtc;
            LastActivityText = $"Last activity {idle.TotalSeconds:0} s ago";
            StopWarning = idle > HeartbeatLimit ? "No recent progress — investigate (the operation is not necessarily hung)" : "";
        }
        else LastActivityText = "";
        foreach (var l in tail.ReadNew()) LogLines.Add(LogTail.ToDisplay(l));
        if (LogLines.Count > 3000) while (LogLines.Count > 2000) LogLines.RemoveAt(0);
    }

    public void Cancel()
    {
        if (cts == null) return;
        Log.Info("job.cancel_requested", Operation);
        Phase = "Cancel requested… finishing the current step safely";
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.Invoke(a);
    }
}
