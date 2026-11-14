using WMT.Core;

namespace WMT.Tests;

static class T
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wmt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
    public static ActivityLog NewLog() => new(Path.Combine(TempDir(), "test.log"));

    public static UninstallRecord R(string? name, string? ver = "1.0", string? pub = "Pub", string arch = "x64",
        bool sys = false, string? parent = null, string source = "HKLM 64-bit") =>
        new(source, arch, name, pub, ver, null, sys, parent, name ?? "key");

    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }
}

sealed class FakeUninstall : IUninstallSource
{
    public List<UninstallRecord> Recs { get; } = new();
    public List<string> Warn { get; } = new();
    public UninstallReadResult Read(IProgress<ProgressInfo>? p, CancellationToken ct) => new(Recs.ToArray(), Warn.ToArray());
}

sealed class FakeProfiles : IProfileSource
{
    public List<ProfileRecord> Recs { get; } = new();
    public IReadOnlyList<ProfileRecord> Read() => Recs.ToArray();
}

sealed class FakeResolver : IAccountResolver
{
    public Dictionary<string, string> Map { get; } = new();
    public string? Resolve(string sid) => Map.TryGetValue(sid, out var v) ? v : null;
}
