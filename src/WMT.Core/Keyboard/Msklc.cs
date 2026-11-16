using System.Text;

namespace WMT.Core;

/// <summary>An installed copy of Microsoft Keyboard Layout Creator (MSKLC).</summary>
public sealed record MsklcInstall(string Folder, string? Exe, string? KbdUtool)
{
    public bool CanBuild => KbdUtool != null;
    public override string ToString() => Exe ?? KbdUtool ?? Folder;
}

/// <summary>Finds MSKLC and uses its command-line compiler (kbdutool.exe — the same compiler MSKLC's "Build DLL" runs)
/// to turn a .klc source into keyboard layout DLLs.</summary>
public static class Msklc
{
    public const string DownloadPage = "https://www.microsoft.com/download/details.aspx?id=102134";
    public const string FolderName = "Microsoft Keyboard Layout Creator 1.4";

    /// <summary>Looks in Program Files (x86), Program Files, the uninstall entry, and the WMT_MSKLC_DIR environment variable.</summary>
    public static MsklcInstall? Find(IEnumerable<SoftwareItem>? software = null, IEnumerable<string>? extraFolders = null)
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("WMT_MSKLC_DIR");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
        if (extraFolders != null) candidates.AddRange(extraFolders);
        foreach (var sf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var pf = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, FolderName));
        }
        foreach (var s in software ?? Enumerable.Empty<SoftwareItem>())
            if (s.Name.Contains("Keyboard Layout Creator", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.InstallLocation))
                candidates.Add(s.InstallLocation!);
        foreach (var dir in candidates.Distinct(PathUtil.Comparer))
        {
            var found = Inspect(dir);
            if (found != null) return found;
        }
        return null;
    }

    public static MsklcInstall? Inspect(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var exe = Path.Combine(dir, "MSKLC.exe");
            string? tool = null;
            foreach (var p in new[] { Path.Combine(dir, "bin", "i386", "kbdutool.exe"), Path.Combine(dir, "kbdutool.exe") })
                if (File.Exists(p)) { tool = p; break; }
            tool ??= Directory.EnumerateFiles(dir, "kbdutool.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!File.Exists(exe) && tool == null) return null;
            return new MsklcInstall(dir, File.Exists(exe) ? exe : null, tool);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public sealed record BuildResult(string? Dll64, string? DllWow64, string Output);

    /// <summary>Builds the 64-bit (System32) and WOW64 (SysWOW64) DLLs from a .klc. The .klc is copied to a fresh folder first,
    /// because kbdutool writes its output next to the working directory.</summary>
    public static async Task<BuildResult> BuildAsync(IProcessRunner proc, MsklcInstall msklc, string klcPath, string outDir, CancellationToken ct)
    {
        if (msklc.KbdUtool == null) throw new InvalidOperationException("kbdutool.exe was not found in the MSKLC folder.");
        var name = KeyboardLayoutDiscovery.KlcName(klcPath) ?? Path.GetFileNameWithoutExtension(klcPath);
        var log = new StringBuilder();
        string? Build(string arch, string flag, out Task<ProcessResult> task)
        {
            var dir = Path.Combine(outDir, arch);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, name + ".klc");
            File.Copy(klcPath, local, true);
            task = proc.RunAsync(msklc.KbdUtool, new[] { "-u", "-n", flag, local }, TimeSpan.FromMinutes(3), ct, workingDirectory: dir);
            return Path.Combine(dir, name + ".dll");
        }
        var d64 = Build("amd64", "-m", out var t64);
        var r64 = await t64;
        log.AppendLine("amd64: " + r64.Describe()).AppendLine(r64.StdOut).AppendLine(r64.StdErr);
        var dWow = Build("wow64", "-o", out var tWow);
        var rWow = await tWow;
        log.AppendLine("wow64: " + rWow.Describe()).AppendLine(rWow.StdOut).AppendLine(rWow.StdErr);
        return new BuildResult(File.Exists(d64) ? d64 : null, File.Exists(dWow) ? dWow : null, log.ToString());
    }

    /// <summary>Writes a .klc text the way MSKLC saves it (UTF-16 LE with BOM).</summary>
    public static void WriteKlc(string path, string klc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, klc, new UnicodeEncoding(false, true));
    }
}
