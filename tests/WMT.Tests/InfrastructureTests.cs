using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WMT.Core;
using Xunit;

namespace WMT.Tests;

public class ProcessAndJobTests
{
    [Fact] public async Task Exit_zero_is_success_exit_one_is_not_timeout_is_not()
    {
        var p = new ProcessRunner();
        var ok = await p.RunAsync("cmd.exe", new[] { "/c", "echo hello" }, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.True(ok.Succeeded); Assert.Contains("hello", ok.StdOut);
        var bad = await p.RunAsync("cmd.exe", new[] { "/c", "echo oops 1>&2 & exit 1" }, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.False(bad.Succeeded); Assert.Equal(1, bad.ExitCode); Assert.Contains("oops", bad.StdErr);
        var slow = await p.RunAsync("ping.exe", new[] { "-n", "30", "127.0.0.1" }, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.False(slow.Succeeded); Assert.True(slow.TimedOut); Assert.Null(slow.ExitCode);
        var missing = await p.RunAsync("definitely-not-a-program-xyz.exe", Array.Empty<string>(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(missing.StartFailed); Assert.False(missing.Succeeded);
    }

    [Fact] public async Task Job_states()
    {
        var r = new JobRunner(T.Log());
        Assert.Equal(JobState.Completed, (await r.RunAsync("a", _ => Task.CompletedTask, null, CancellationToken.None)).State);
        Assert.Equal(JobState.CompletedWithWarnings, (await r.RunAsync("b", c => { c.Warnings.Add("w"); return Task.CompletedTask; }, null, CancellationToken.None)).State);
        var f = await r.RunAsync("c", _ => throw new InvalidOperationException("boom"), null, CancellationToken.None);
        Assert.Equal(JobState.Failed, f.State); Assert.Equal(1, f.ExitCode); Assert.Equal("boom", f.Error);
        using var cts = new CancellationTokenSource(200);
        var c = await r.RunAsync("d", ctx => Task.Delay(Timeout.Infinite, ctx.Token), null, cts.Token);
        Assert.Equal(JobState.Cancelled, c.State); Assert.Equal(130, c.ExitCode);
    }

    [Fact] public async Task Log_failure_never_becomes_silent_success()
    {
        var file = T.Write(Path.Combine(T.Temp(), "a-file"));
        var log = new ActivityLog(Path.Combine(file, "x", "y.log"));
        var r = await new JobRunner(log).RunAsync("ok", _ => Task.CompletedTask, null, CancellationToken.None);
        Assert.True(log.FailedWrites > 0);
        Assert.Equal(JobState.CompletedWithWarnings, r.State);
    }
}

public class LogTests
{
    [Fact] public async Task Concurrent_writers_incremental_reader()
    {
        var log = T.Log(); var tail = new LogTail(log.FilePath);
        var got = new List<string>();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() => { for (int i = 0; i < 250; i++) log.Info("t", $"w{w}-{i}"); })).ToArray();
        while (!Task.WhenAll(writers).IsCompleted) { got.AddRange(tail.ReadNew()); await Task.Delay(3); }
        got.AddRange(tail.ReadNew());
        Assert.Equal(1000, got.Count);
        Assert.Equal(1000, got.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()).Distinct().Count());
        Assert.Empty(tail.ReadNew());
        Assert.Equal(0, log.FailedWrites);
    }

    [Fact] public void Partial_line_waits_for_newline()
    {
        var p = Path.Combine(T.Temp(), "p.log");
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes("{\"a\":1}"));
        var t = new LogTail(p);
        Assert.Empty(t.ReadNew());
        File.AppendAllText(p, "\n");
        Assert.Single(t.ReadNew());
    }

    [Fact] public void Secrets_redacted_and_armenian_kept()
    {
        var log = T.Log();
        log.Info("t", "password=hunter2 token: abc123 https://user:pw@example.com Բարև աշխարհ");
        var text = File.ReadAllText(log.FilePath, Encoding.UTF8);
        Assert.DoesNotContain("hunter2", text); Assert.DoesNotContain("abc123", text); Assert.DoesNotContain("user:pw", text);
        Assert.Contains("Բարև աշխարհ", text);
        Assert.True(Redaction.LooksSecretName("GITHUB_TOKEN")); Assert.False(Redaction.LooksSecretName("TESSDATA_PREFIX"));
    }
}

public class PathTests
{
    [Theory]
    [InlineData(@"C:\Users\a\AppData\Roaming\Microsoft\Templates\Normal.dotm", @"Data\C\Users\a\AppData\Roaming\Microsoft\Templates\Normal.dotm")]
    [InlineData(@"g:\Backup\x.txt", @"Data\G\Backup\x.txt")]
    [InlineData(@"\\server\share\dir\f.txt", @"Data\UNC\server\share\dir\f.txt")]
    public void Package_data_paths_round_trip(string src, string rel)
    {
        Assert.Equal(rel, PathUtil.ToPackageDataPath(src));
        Assert.Equal(src.Length > 1 && src[1] == ':' ? char.ToUpperInvariant(src[0]) + src[1..] : src, PathUtil.FromPackageDataPath(rel));
    }

    [Theory]
    [InlineData(@"Data\C\a.txt", true)]
    [InlineData(@"Data\..\a.txt", false)]
    [InlineData(@"\Data\a.txt", false)]
    [InlineData(@"C:\a.txt", false)]
    [InlineData(@"Data\C\a.txt:ads", false)]
    [InlineData(@"Data\\a.txt", false)]
    [InlineData(@"Data\ .\a.txt", false)]
    public void Safe_relative(string rel, bool ok) => Assert.Equal(ok, PathUtil.IsSafeRelative(rel, "Data"));

    [Fact] public void Inside_checks_are_separator_aware()
    {
        Assert.True(PathUtil.IsInsideOrSame(@"C:\Users\a\b", @"C:\users\A"));
        Assert.False(PathUtil.IsInsideOrSame(@"C:\Users\ab", @"C:\Users\a"));
        Assert.Throws<InvalidDataException>(() => PathUtil.SafeCombine(@"C:\pkg", @"Data\..\..\x"));
    }

    [Fact] public void Pe_architecture_of_this_process()
        => Assert.Equal(Environment.Is64BitProcess ? "x64" : "x86", PeInfo.Architecture(Environment.ProcessPath!));
}

public class ManifestTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void Json_arrays_zero_one_many_round_trip_utf8(int n)
    {
        var list = Enumerable.Range(0, n).Select(i => new FileEntry { Id = "F" + i, SourcePath = $@"C:\Users\Ա\ֆայլ{i}.txt", Classification = Classification.Review }).ToList();
        var p = Path.Combine(T.Temp(), "files.json");
        Json.WriteFile(p, list);
        var raw = File.ReadAllBytes(p);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB);
        var text = Encoding.UTF8.GetString(raw);
        Assert.StartsWith("[", text.TrimStart());
        if (n > 0) Assert.Contains("ֆայլ0", text);
        Assert.Contains(n == 0 ? "[]" : "\"Review\"", text);
        Assert.Equal(n, Json.ReadFile<List<FileEntry>>(p).Count);
    }
}

public class HelpTests
{
    [Fact] public void Every_button_menu_tab_and_column_label_is_explained_in_Help()
    {
        var xaml = File.ReadAllText(T.RepoFile("src", "WMT.App", "MainWindow.xaml"));
        var help = File.ReadAllText(T.RepoFile("docs", "Help.txt"));
        var labels = Regex.Matches(xaml, "(?:Content|Header)=\"([^\"{][^\"]*)\"").Select(m => m.Groups[1].Value.Replace("&amp;", "&")).Distinct().ToList();
        Assert.True(labels.Count > 50);
        var missing = labels.Where(l => !help.Contains(l, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Help.txt does not mention: " + string.Join(", ", missing));
        Assert.Matches("[\u0531-\u0587]{6,}", help);
    }
}
