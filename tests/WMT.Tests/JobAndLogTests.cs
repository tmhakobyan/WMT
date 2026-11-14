using System.Text;
using System.Text.Json;
using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class JobRunnerTests
{
    [Fact] public async Task Success_is_completed_with_exit_0_and_marker_in_log()
    {
        var log = T.NewLog();
        var r = await new JobRunner(log).RunAsync("ok", _ => Task.CompletedTask, null, CancellationToken.None);
        Assert.Equal(JobState.Completed, r.State); Assert.Equal(0, r.ExitCode);
        Assert.Contains("job.completed", File.ReadAllText(log.FilePath));
    }

    [Fact] public async Task Warnings_give_completed_with_warnings()
    {
        var r = await new JobRunner(T.NewLog()).RunAsync("w", c => { c.Warnings.Add("x"); return Task.CompletedTask; }, null, CancellationToken.None);
        Assert.Equal(JobState.CompletedWithWarnings, r.State); Assert.Equal(3, r.ExitCode);
    }

    [Fact] public async Task Exception_gives_failed_exit_1()
    {
        var r = await new JobRunner(T.NewLog()).RunAsync("f", _ => throw new InvalidOperationException("boom"), null, CancellationToken.None);
        Assert.Equal(JobState.Failed, r.State); Assert.Equal(1, r.ExitCode); Assert.Equal("boom", r.Error);
    }

    [Fact] public async Task Cancellation_gives_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var t = new JobRunner(T.NewLog()).RunAsync("c", c => Task.Delay(Timeout.Infinite, c.Token), null, cts.Token);
        cts.CancelAfter(100);
        var r = await t;
        Assert.Equal(JobState.Cancelled, r.State); Assert.Equal(130, r.ExitCode);
    }

    [Fact] public async Task Log_write_failure_is_never_a_silent_success()
    {
        var file = Path.Combine(T.TempDir(), "iam-a-file"); File.WriteAllText(file, "x");
        var log = new ActivityLog(Path.Combine(file, "sub", "bad.log")); // parent is a file -> every write fails
        var r = await new JobRunner(log).RunAsync("ok", _ => Task.CompletedTask, null, CancellationToken.None);
        Assert.True(log.HasFailures);
        Assert.Equal(JobState.CompletedWithWarnings, r.State);
    }
}

public class LogTests
{
    [Fact] public async Task Concurrent_writers_and_incremental_reader_lose_nothing()
    {
        var log = T.NewLog(); var tail = new LogTail(log.FilePath);
        var lines = new List<string>();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        { for (int i = 0; i < 200; i++) log.Write("info", "t", $"w{w}-{i}"); })).ToArray();
        while (!Task.WhenAll(writers).IsCompleted) { lines.AddRange(tail.ReadNew()); await Task.Delay(5); }
        await Task.WhenAll(writers);
        lines.AddRange(tail.ReadNew());
        Assert.Equal(800, lines.Count);
        Assert.Equal(800, lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()).Distinct().Count());
        Assert.False(log.HasFailures);
        Assert.Empty(tail.ReadNew());
    }

    [Fact] public void Partial_line_is_not_consumed_until_newline_arrives()
    {
        var path = Path.Combine(T.TempDir(), "p.log");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("{\"a\":1}"));
        var tail = new LogTail(path);
        Assert.Empty(tail.ReadNew());
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(new byte[] { 10 });
        Assert.Single(tail.ReadNew());
    }

    [Fact] public void Secrets_are_redacted()
    {
        var log = T.NewLog();
        log.Write("info", "t", "password=hunter2 and token: abc123");
        var text = File.ReadAllText(log.FilePath);
        Assert.DoesNotContain("hunter2", text); Assert.DoesNotContain("abc123", text);
    }

    [Fact] public void Armenian_text_round_trips_in_the_log()
    {
        var log = T.NewLog(); log.Write("info", "t", "Բարև, աշխարհ");
        Assert.Contains("Բարև, աշխարհ", File.ReadAllText(log.FilePath, Encoding.UTF8));
    }
}
