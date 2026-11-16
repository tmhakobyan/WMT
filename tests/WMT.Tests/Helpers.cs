using System.Text;
using WMT.Core;

namespace WMT.Tests;

static class T
{
    public static string Temp(string? name = null)
    {
        var d = Path.Combine(Path.GetTempPath(), "wmt-tests", (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }
    public static ActivityLog Log() => new(Path.Combine(Temp("log"), "test.log"));
    public static string Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var c = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(c)) return c;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    public static UserProfileInfo Profile(string path, string sid = "S-1-5-21-1-2-3-1001") =>
        new() { Account = "PC\\tester", Sid = sid, ProfilePath = path, Access = AccessState.Accessible, Loaded = true, IsCurrentUser = true, Selected = true };

    public static DiscoveryContext Ctx(IEnumerable<SoftwareItem>? selected = null, IEnumerable<UserProfileInfo>? profiles = null, DiscoveryOptions? opts = null,
        IRegistryReader? reg = null, IProcessRunner? proc = null, string? currentSid = "S-1-5-21-1-2-3-1001")
    {
        var sel = (selected ?? Array.Empty<SoftwareItem>()).ToList();
        return new DiscoveryContext
        {
            AllSoftware = sel, SelectedSoftware = sel, SelectedProfiles = (profiles ?? Array.Empty<UserProfileInfo>()).ToList(),
            Options = opts ?? new DiscoveryOptions { IncludeRuntimes = false, IncludeWindowsSettings = false }, Registry = reg ?? new MemoryRegistry(),
            Processes = proc ?? new FakeProcess(), Log = Log(), Token = CancellationToken.None, CurrentUserSid = currentSid
        };
    }

    /// <summary>Plan over a set of plain folders (custom-path provider only).</summary>
    public static MigrationPlan PlanFor(params string[] folders)
    {
        var ctx = Ctx(opts: new DiscoveryOptions { CustomPaths = folders.ToList(), IncludeRuntimes = false, IncludeWindowsSettings = false });
        return new PlanBuilder(new IDiscoveryProvider[] { new CustomPathsProvider() }).Build(ctx);
    }

    public static BackupResult Backup(MigrationPlan plan, string dest, ActivityLog? log = null, Action<BackupEngine>? setup = null)
    {
        var e = new BackupEngine(log ?? Log()) { RetryDelay = TimeSpan.FromMilliseconds(5) };
        setup?.Invoke(e);
        return e.Create(new BackupInputs { Plan = plan, AllSoftware = new List<SoftwareItem>(), AllProfiles = new List<UserProfileInfo>() }, dest, null, CancellationToken.None);
    }

    public static Dictionary<string, string> HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), f => Hashing.Sha256File(f) + "|" + File.GetLastWriteTimeUtc(f).Ticks);
}

sealed class FakeUninstall : IUninstallSource
{
    public List<UninstallRecord> Recs { get; } = new();
    public List<Issue> Issues { get; } = new();
    public (List<UninstallRecord>, List<Issue>) Read(CancellationToken ct) => (Recs.ToList(), Issues.ToList());
    public FakeUninstall Add(string? name, string? ver = "1.0", string arch = "x64", bool sys = false, string? parent = null, string source = "HKLM 64-bit", string? publisher = "Pub", bool perUser = false)
    { Recs.Add(new UninstallRecord(source, arch, name ?? "key", name, publisher, ver, null, sys, parent, false, perUser)); return this; }
}

sealed class FakeProcess : IProcessRunner
{
    public Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; set; } = (_, _) => new ProcessResult(null, "", "", false, true, "not found", TimeSpan.Zero);
    public List<string> Calls { get; } = new();
    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, string? workingDirectory = null,
        IDictionary<string, string?>? environment = null, Action<string>? onOutputLine = null)
    {
        var a = args.ToList();
        Calls.Add(fileName + " " + string.Join(' ', a));
        return Task.FromResult(Handler(fileName, a));
    }
    public static ProcessResult Ok(string stdout) => new(0, stdout, "", false, false, null, TimeSpan.FromMilliseconds(5));
    public static ProcessResult Fail(int code, string stderr) => new(code, "", stderr, false, false, null, TimeSpan.FromMilliseconds(5));
}
