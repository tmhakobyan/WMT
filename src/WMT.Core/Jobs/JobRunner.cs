namespace WMT.Core;

public sealed record JobResult(
    Guid CorrelationId, string Name, JobState State, DateTime StartedUtc, DateTime EndedUtc,
    int ExitCode, IReadOnlyList<string> Warnings, string? Error);

public sealed class JobContext
{
    static readonly IProgress<ProgressInfo> Null = new Progress<ProgressInfo>();
    readonly IProgress<ProgressInfo>? progress;
    public JobContext(IProgress<ProgressInfo>? progress, CancellationToken token, Guid id)
    { this.progress = progress; Token = token; CorrelationId = id; }
    public CancellationToken Token { get; }
    public Guid CorrelationId { get; }
    public List<string> Warnings { get; } = new();
    public IProgress<ProgressInfo> Progress => progress ?? Null;
}

/// <summary>Runs work off the UI thread. Result state comes from the work's actual outcome
/// (completed / exception / cancellation), never from "the task ended".
/// Exit codes: Completed 0, Failed 1, CompletedWithWarnings 3, Cancelled 130.</summary>
public sealed class JobRunner
{
    readonly ActivityLog log;
    public JobRunner(ActivityLog log) { this.log = log; }

    public async Task<JobResult> RunAsync(string name, Func<JobContext, Task> work,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var started = DateTime.UtcNow;
        var ctx = new JobContext(progress, ct, id);
        int failuresBefore = log.FailedWrites;
        log.Write("info", "job.queued", name, id);
        JobState state;
        string? error = null;
        try
        {
            log.Write("info", "job.running", name, id);
            await Task.Run(() => work(ctx), ct).ConfigureAwait(false);
            state = ctx.Warnings.Count > 0 ? JobState.CompletedWithWarnings : JobState.Completed;
        }
        catch (OperationCanceledException) { state = JobState.Cancelled; }
        catch (Exception ex) { state = JobState.Failed; error = ex.Message; log.Write("error", "job.exception", ex.ToString(), id); }

        if (state == JobState.Completed && log.FailedWrites > failuresBefore)
        {
            state = JobState.CompletedWithWarnings;
            ctx.Warnings.Add("Log write failed during this job: " + log.LastError);
        }
        int code = state switch
        {
            JobState.Completed => 0,
            JobState.CompletedWithWarnings => 3,
            JobState.Cancelled => 130,
            _ => 1
        };
        var ended = DateTime.UtcNow;
        log.Write(state == JobState.Failed ? "error" : "info", "job.completed", $"{name} state={state} exit={code}", id);
        return new JobResult(id, name, state, started, ended, code, ctx.Warnings.ToArray(), error);
    }
}
