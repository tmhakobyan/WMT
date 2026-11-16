using System.Diagnostics;
using System.Text;

namespace WMT.Core;

public sealed record JobResult(
    string OperationId, string Name, JobState State, DateTime StartedUtc, DateTime EndedUtc,
    int ExitCode, IReadOnlyList<string> Warnings, string? Error, string EndReason);

public sealed class JobContext
{
    readonly IProgress<ProgressInfo>? progress;
    public JobContext(IProgress<ProgressInfo>? progress, CancellationToken token, string id, ActivityLog log)
    { this.progress = progress; Token = token; OperationId = id; Log = log; }
    public CancellationToken Token { get; }
    public string OperationId { get; }
    public ActivityLog Log { get; }
    public List<string> Warnings { get; } = new();
    public DateTime LastHeartbeatUtc { get; private set; } = DateTime.UtcNow;
    public void Report(ProgressInfo p) { LastHeartbeatUtc = DateTime.UtcNow; progress?.Report(p); }
    public IProgress<ProgressInfo> Progress => new ProgressAdapter(this);
    sealed class ProgressAdapter : IProgress<ProgressInfo>
    {
        readonly JobContext c; public ProgressAdapter(JobContext c) { this.c = c; }
        public void Report(ProgressInfo value) => c.Report(value);
    }
}

/// <summary>NFR-001/002: runs work off the UI thread; the final state comes from the actual outcome,
/// never from "the task ended". Exit codes: 0 Completed, 3 CompletedWithWarnings, 1 Failed, 130 Cancelled.</summary>
public sealed class JobRunner
{
    readonly ActivityLog log;
    public JobRunner(ActivityLog log) { this.log = log; }

    public async Task<JobResult> RunAsync(string name, Func<JobContext, Task> work, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var id = DateTime.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var started = DateTime.UtcNow;
        var ctx = new JobContext(progress, ct, id, log);
        int failuresBefore = log.FailedWrites;
        log.Info("job.queued", name, id);
        JobState state; string? error = null; string reason;
        try
        {
            log.Info("job.running", name, id);
            await Task.Run(() => work(ctx), ct).ConfigureAwait(false);
            state = ctx.Warnings.Count > 0 ? JobState.CompletedWithWarnings : JobState.Completed;
            reason = state == JobState.Completed ? "Work finished without warnings" : $"Work finished with {ctx.Warnings.Count} warning(s)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { state = JobState.Cancelled; reason = "Cancelled by user"; }
        catch (Exception ex) { state = JobState.Failed; error = ex.Message; reason = "Exception: " + ex.GetType().Name; log.Error("job.exception", ex.ToString(), id); }

        if (state is JobState.Completed && log.FailedWrites > failuresBefore)
        {
            state = JobState.CompletedWithWarnings;
            ctx.Warnings.Add("Log write failed during this operation: " + log.LastError);
            reason = "Finished, but the log could not be written";
        }
        int code = state switch { JobState.Completed => 0, JobState.CompletedWithWarnings => 3, JobState.Cancelled => 130, _ => 1 };
        foreach (var w in ctx.Warnings) log.Warn("job.warning", w, id);
        log.Write(state == JobState.Failed ? "error" : "info", "job.completed", $"{name}: state={state} exit={code} reason={reason}", id);
        return new JobResult(id, name, state, started, DateTime.UtcNow, code, ctx.Warnings.ToArray(), error, reason);
    }
}

public sealed record ProcessResult(int? ExitCode, string StdOut, string StdErr, bool TimedOut, bool StartFailed, string? StartError, TimeSpan Duration)
{
    public bool Succeeded => !TimedOut && !StartFailed && ExitCode == 0;
    public string Describe() =>
        StartFailed ? "could not start: " + StartError :
        TimedOut ? $"timed out after {Duration.TotalSeconds:0}s" :
        $"exit code {ExitCode}";
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? workingDirectory = null, IDictionary<string, string?>? environment = null, Action<string>? onOutputLine = null);
}

/// <summary>Runs a child process with timeout + cancellation; captures stdout/stderr/exit code (PY-002).
/// Exit of the process alone never means success: the caller must check <see cref="ProcessResult.Succeeded"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? workingDirectory = null, IDictionary<string, string?>? environment = null, Action<string>? onOutputLine = null)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        psi.Environment["PIP_NO_INPUT"] = "1";
        if (environment != null) foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) stdout.AppendLine(e.Data); onOutputLine?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (stderr) stderr.AppendLine(e.Data); onOutputLine?.Invoke(e.Data); } };
        try
        {
            if (!p.Start()) return new ProcessResult(null, "", "", false, true, "Process.Start returned false", sw.Elapsed);
        }
        catch (Exception ex) { return new ProcessResult(null, "", "", false, true, ex.Message, sw.Elapsed); }
        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        bool timedOut = false;
        try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(5000); } catch { }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        try { p.WaitForExit(); } catch { }
        int? code = null;
        try { if (p.HasExited) code = p.ExitCode; } catch { }
        string o, e2; lock (stdout) o = stdout.ToString(); lock (stderr) e2 = stderr.ToString();
        return new ProcessResult(timedOut ? null : code, o, e2, timedOut, false, null, sw.Elapsed);
    }
}
