using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WMT.Core;

public sealed class CommandRecord
{
    public string Command { get; set; } = "";
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public string? StartError { get; set; }
    public string StdErr { get; set; } = "";
    public double Seconds { get; set; }
    public CaptureStatus Status { get; set; }
}

/// <summary>PY-001..008 record for one interpreter or virtual environment.</summary>
public sealed class PythonEnvironmentInfo
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Version { get; set; } = SoftwareItem.UnknownText;
    public string Architecture { get; set; } = SoftwareItem.UnknownText;
    public string? BaseInterpreter { get; set; }
    public string? BasePrefix { get; set; }
    public Dictionary<string, string> PyvenvCfg { get; set; } = new();
    public string? ProjectPath { get; set; }
    public string? ProfileSid { get; set; }
    public List<string> RequirementsFiles { get; set; } = new();
    public List<string> LaunchScripts { get; set; } = new();
    public string? PipVersion { get; set; }
    public CaptureStatus FreezeStatus { get; set; } = CaptureStatus.NotAttempted;
    public string? Freeze { get; set; }
    public CaptureStatus ListStatus { get; set; } = CaptureStatus.NotAttempted;
    public List<PipPackage> Packages { get; set; } = new();
    public CaptureStatus InspectStatus { get; set; } = CaptureStatus.NotAttempted;
    public List<CommandRecord> Commands { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> GpuNotes { get; set; } = new();
    public List<string> ModelPaths { get; set; } = new();
    public List<string> NonReinstallablePackages { get; set; } = new();
    public CaptureStatus Overall { get; set; } = CaptureStatus.NotAttempted;
    public bool Selected { get; set; } = true;
}

public sealed class PipPackage { public string Name { get; set; } = ""; public string Version { get; set; } = ""; }

public sealed class PythonInventory
{
    public List<PythonEnvironmentInfo> Environments { get; set; } = new();
    public List<string> GpuInfo { get; set; } = new();
    public string? TesseractVersion { get; set; }
    public string? TesseractPath { get; set; }
    public List<string> Notes { get; set; } = new();
}

/// <summary>Python interpreters, virtual environments, projects, OCR engines and models.</summary>
public sealed class PythonProvider : IDiscoveryProvider
{
    public const string ProviderId = "python";
    public const string OcrProviderId = "ocr";
    public string Id => ProviderId;
    public string DisplayName => "Python / OCR";

    static readonly string[] ModelExts = { ".traineddata", ".onnx", ".pdmodel", ".pdiparams", ".pdparams", ".pdopt", ".pt", ".pth", ".safetensors", ".h5", ".tflite", ".ckpt", ".pb", ".caffemodel", ".weights" };
    static readonly string[] SourceExts = { ".py", ".pyw", ".ipynb", ".pyx", ".pyi" };
    static readonly string[] ConfigExts = { ".toml", ".cfg", ".ini", ".yaml", ".yml", ".json", ".env.example", ".txt", ".md", ".rst", ".lock", ".spec" };
    static readonly string[] ScriptExts = { ".bat", ".cmd", ".ps1", ".sh", ".vbs", ".lnk" };
    static readonly HashSet<string> ProjectSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", "node_modules", "site-packages", ".ipynb_checkpoints", ".idea", ".vs", ".cache", ".eggs", "*.egg-info" };
    static readonly Regex PyLauncherLine = new(@"(?:-V:|-)(?<tag>[\w\.\-]+)\s*\*?\s+(?<path>[A-Za-z]:\\.+python[w]?\.exe)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Applies(DiscoveryContext ctx) => ctx.IsSelected(ProviderId) || ctx.IsSelected(OcrProviderId);

    public ProviderOutput Discover(DiscoveryContext ctx)
    {
        var o = new ProviderOutput();
        var inv = new PythonInventory();
        var envs = new Dictionary<string, PythonEnvironmentInfo>(StringComparer.OrdinalIgnoreCase);

        // ---- interpreters
        foreach (var exe in FindInterpreters(ctx, inv))
            if (!envs.ContainsKey(PathUtil.Canonical(exe)))
                envs[PathUtil.Canonical(exe)] = new PythonEnvironmentInfo { Kind = "Interpreter", Executable = exe, RootPath = Path.GetDirectoryName(exe) ?? exe };

        // ---- virtual environments in selected profiles / custom paths
        var searchRoots = ctx.SelectedProfiles.Where(p => p.Access == AccessState.Accessible).Select(p => (p.ProfilePath, (string?)p.Sid, 4))
            .Concat(ctx.Options.CustomPaths.Select(c => (c, (string?)null, 6))).ToList();
        foreach (var (root, sid, depth) in searchRoots)
        {
            foreach (var cfg in FindFiles(root, "pyvenv.cfg", depth, ctx.Token))
            {
                var dir = Path.GetDirectoryName(cfg)!;
                var exe = Path.Combine(dir, "Scripts", "python.exe");
                var env = new PythonEnvironmentInfo { Kind = "Virtual environment", RootPath = dir, Executable = exe, ProfileSid = sid, PyvenvCfg = ReadCfg(cfg) };
                env.BaseInterpreter = env.PyvenvCfg.TryGetValue("executable", out var bx) ? bx : env.PyvenvCfg.TryGetValue("home", out var h) ? Path.Combine(h, "python.exe") : null;
                if (env.PyvenvCfg.TryGetValue("version", out var cv) || env.PyvenvCfg.TryGetValue("version_info", out cv)) env.Notes.Add("pyvenv.cfg version: " + cv);
                var parentName = Path.GetFileName(dir);
                env.ProjectPath = Regex.IsMatch(parentName, @"^\.?(venv|env|virtualenv)[\w\-\.]*$", RegexOptions.IgnoreCase) ? Path.GetDirectoryName(dir) : dir;
                envs[PathUtil.Canonical(exe)] = env;
            }
            foreach (var condaName in new[] { "anaconda3", "miniconda3", "Anaconda3", "Miniconda3", "miniforge3" })
            {
                var cr = Path.Combine(root, condaName);
                if (!Directory.Exists(Path.Combine(cr, "envs"))) continue;
                try
                {
                    foreach (var e in Directory.EnumerateDirectories(Path.Combine(cr, "envs")))
                    {
                        var exe = Path.Combine(e, "python.exe");
                        if (File.Exists(exe)) envs[PathUtil.Canonical(exe)] = new PythonEnvironmentInfo { Kind = "Conda environment", RootPath = e, Executable = exe, ProfileSid = sid };
                    }
                }
                catch { }
            }
        }

        // ---- query each environment (timeouts, exit codes, stderr)
        int n = 0;
        foreach (var env in envs.Values)
        {
            ctx.Token.ThrowIfCancellationRequested();
            ctx.Progress?.Report(new ProgressInfo("Querying Python environments", n++, envs.Count, env.Executable));
            env.Id = PathUtil.StableId(env.Executable);
            Query(ctx, env);
            inv.Environments.Add(env);
        }

        // ---- GPU / CUDA (never invented)
        DetectGpu(ctx, inv);
        foreach (var env in inv.Environments) AnnotateGpu(env, inv);

        // ---- projects, models and launch scripts as targeted scan roots
        var projectRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var venvPaths = inv.Environments.Where(e => e.Kind != "Interpreter").Select(e => PathUtil.Canonical(e.RootPath)).ToList();
        foreach (var env in inv.Environments.Where(e => e.ProjectPath != null))
        {
            var proj = PathUtil.Canonical(env.ProjectPath!);
            if (IsSystemLocation(proj) || !projectRoots.Add(proj)) continue;
            var profile = ctx.SelectedProfiles.FirstOrDefault(p => env.ProfileSid != null && p.Sid == env.ProfileSid);
            if (profile != null && PathUtil.SamePath(proj, profile.ProfilePath)) continue; // a venv directly in the profile root: do not treat the whole profile as a project
            var root = new ScanRoot { Path = proj, Category = "Python projects / " + Path.GetFileName(proj), ProviderId = Id, ProfileSid = env.ProfileSid, MaxDepth = 10, Rule = ProjectRule,
                Note = $"Project of environment {env.RootPath}; virtual environments, site-packages and caches are not copied (PY-004)" };
            foreach (var d in ProjectSkipDirs) root.ExcludedDirectoryNames.Add(d);
            foreach (var v in venvPaths) root.ExcludedPaths.Add(v);
            o.Roots.Add(root);
            try
            {
                foreach (var f in Directory.EnumerateFiles(proj, "*requirements*.txt", SearchOption.TopDirectoryOnly).Concat(
                         new[] { "pyproject.toml", "setup.py", "setup.cfg", "environment.yml", "Pipfile", "poetry.lock" }.Select(x => Path.Combine(proj, x)).Where(File.Exists)))
                    env.RequirementsFiles.Add(f);
                foreach (var f in Directory.EnumerateFiles(proj, "*.*", SearchOption.TopDirectoryOnly).Where(f => ScriptExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
                    env.LaunchScripts.Add(f);
            }
            catch { }
        }

        // ---- OCR: Tesseract and model caches
        DiscoverOcr(ctx, inv, o);

        // ---- components, dependencies, issues
        foreach (var env in inv.Environments)
        {
            var name = env.Kind == "Interpreter" ? $"Python {env.Version} ({env.Architecture})" : $"{env.Kind}: {env.RootPath}";
            var comp = new Component
            {
                Id = "py-" + env.Id, Name = name, Application = "Python", ProviderId = Id, Kind = env.Kind,
                Classification = env.Kind == "Interpreter" ? Classification.InstallerManaged : Classification.Recommended,
                MigrationMethod = env.Kind == "Interpreter" ? MigrationMethod.Reinstall : MigrationMethod.RebuildDependency,
                SourcePath = env.RootPath, ProfileSid = env.ProfileSid,
                DestinationRule = env.Kind == "Interpreter" ? "Install the same version and architecture from python.org" : "Recreate the environment at the mapped path; install captured packages",
                VerificationMethod = env.Kind == "Interpreter" ? "python --version prints " + env.Version : "pip freeze on the new environment equals the captured list",
                Basis = $"Overall capture status: {env.Overall}", Confidence = env.Overall == CaptureStatus.Captured ? Confidence.Confirmed : env.Overall == CaptureStatus.Partial ? Confidence.Inferred : Confidence.Unknown,
                OfficialSource = PythonInstallerUrl(env.Version, env.Architecture),
            };
            if (env.Kind != "Interpreter")
                comp.RequiredPrerequisites.Add($"Python {VersionOrBase(env)} {env.Architecture}");
            foreach (var e in env.Errors) comp.ManualActions.Add("Resolve: " + e);
            if (env.NonReinstallablePackages.Count > 0) comp.ManualActions.Add("Packages from local files/VCS need their source: " + string.Join(", ", env.NonReinstallablePackages.Take(10)));
            o.Components.Add(comp);
            if (env.Overall is CaptureStatus.Failed or CaptureStatus.Partial)
                o.Issues.Add(new Issue(env.Overall == CaptureStatus.Failed ? IssueSeverity.Blocker : IssueSeverity.Warning, "Python",
                    $"{name}: package list capture is {env.Overall}. It cannot be marked fully restorable (PY-003).", env.RootPath,
                    "Fix the environment (see errors) and run discovery again, or document the packages manually."));
            if (env.Kind != "Interpreter")
                o.Dependencies.Add(new Dependency { Application = "Python", Component = env.RootPath, DependsOn = $"Python {VersionOrBase(env)} {env.Architecture}", Type = "Interpreter",
                    Confidence = env.Version != SoftwareItem.UnknownText ? Confidence.Confirmed : Confidence.Unknown, RequiredAction = "Install this exact Python version",
                    ValidationTest = "python --version", Evidence = env.BaseInterpreter ?? "pyvenv.cfg" });
            foreach (var g in env.GpuNotes)
                o.Dependencies.Add(new Dependency { Application = "Python", Component = env.RootPath, DependsOn = g, Type = "GPU/CUDA", Confidence = Confidence.Inferred,
                    RequiredAction = "Check NVIDIA driver/CUDA compatibility manually before installing GPU wheels", ValidationTest = "Run a GPU inference test", Evidence = "Package versions" });
            if (env.Packages.Any(p => p.Name.Equals("pytesseract", StringComparison.OrdinalIgnoreCase)))
                o.Dependencies.Add(new Dependency { Application = "Python", Component = env.RootPath, DependsOn = "Tesseract OCR engine" + (inv.TesseractVersion != null ? " " + inv.TesseractVersion : ""), Type = "External program",
                    Confidence = inv.TesseractPath != null ? Confidence.Confirmed : Confidence.Unknown, RequiredAction = "Install Tesseract and its language data", ValidationTest = "tesseract --list-langs", Evidence = "pytesseract installed" });
            if (env.Packages.Any(p => p.Name.StartsWith("paddle", StringComparison.OrdinalIgnoreCase)))
                o.Dependencies.Add(new Dependency { Application = "Python", Component = env.RootPath, DependsOn = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", Type = "Runtime",
                    Confidence = Confidence.Inferred, RequiredAction = "Install VC++ redistributable", ValidationTest = "import paddle", Evidence = "Paddle wheels use the MSVC runtime" });
            if (env.Packages.Any(p => p.Name.Contains("calfa", StringComparison.OrdinalIgnoreCase)))
                env.Notes.Add("CALFA package detected: keep its model files and launch scripts; verify model paths in its configuration.");
        }
        o.Manifests["Python-Environments"] = inv;
        o.FunctionalTests.Add("Python: for each restored environment run 'python -c \"import sys; print(sys.version)\"' and the project's own test/launch script.");
        o.FunctionalTests.Add("OCR: run Tesseract/PaddleOCR/CALFA on a sample Armenian page; compare output with the old system.");
        return o;
    }

    static string VersionOrBase(PythonEnvironmentInfo e)
    {
        if (e.Version != SoftwareItem.UnknownText) return e.Version;
        if (e.PyvenvCfg.TryGetValue("version", out var v)) return v + " (from pyvenv.cfg)";
        return SoftwareItem.UnknownText;
    }

    public static string? PythonInstallerUrl(string version, string arch)
    {
        var m = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)$");
        if (!m.Success) return null;
        var suffix = arch == "x64" ? "-amd64" : arch == "ARM64" ? "-arm64" : arch == "x86" ? "" : null;
        if (suffix == null) return $"https://www.python.org/downloads/release/python-{version.Replace(".", "")}/";
        return $"https://www.python.org/ftp/python/{version}/python-{version}{suffix}.exe";
    }

    static bool IsSystemLocation(string path) =>
        path.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\AppData\Local\Programs\Python", StringComparison.OrdinalIgnoreCase);

    static FileDecision? ProjectRule(FileCandidate f)
    {
        if (Rules.IsTempOrLock(f) || f.Extension.Equals(".pyc", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".pyo", StringComparison.OrdinalIgnoreCase)) return null;
        if (f.RelativeToRoot.Contains(@"\site-packages\", StringComparison.OrdinalIgnoreCase)) return null;
        var ext = f.Extension;
        if (ModelExts.Contains(ext, StringComparer.OrdinalIgnoreCase) || f.Name.EndsWith(".pdiparams.info", StringComparison.OrdinalIgnoreCase))
            return new(Classification.Recommended, "Model file (OCR/ML)");
        if (SourceExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return new(Classification.Recommended, "Own Python source");
        if (ScriptExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return new(Classification.Recommended, "Launch script");
        if (ConfigExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return new(Classification.Recommended, "Project configuration/requirements");
        if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase)) return new(Classification.Review, "Log file");
        return new(Classification.Review, "Project data file");
    }

    IEnumerable<string> FindInterpreters(DiscoveryContext ctx, PythonInventory inv)
    {
        var found = new List<string>();
        foreach (var (hive, view) in new[] { ("HKCU", RegView.Default), ("HKLM", RegView.Reg64), ("HKLM", RegView.Reg32) })
            foreach (var company in ctx.Registry.SubKeys(hive, @"Software\Python", view))
            {
                if (company.Equals("PyLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var tag in ctx.Registry.SubKeys(hive, $@"Software\Python\{company}", view))
                {
                    var ip = $@"Software\Python\{company}\{tag}\InstallPath";
                    var exe = ctx.Registry.Value(hive, ip, "ExecutablePath", view)?.AsString();
                    if (string.IsNullOrEmpty(exe)) { var dir = ctx.Registry.Value(hive, ip, "", view)?.AsString(); if (!string.IsNullOrEmpty(dir)) exe = Path.Combine(dir, "python.exe"); }
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) found.Add(exe);
                }
            }
        foreach (var s in ctx.AllSoftware.Where(s => s.Category == "Python" && s.InstallLocation != SoftwareItem.UnknownText))
        {
            var exe = Path.Combine(s.InstallLocation, "python.exe");
            if (File.Exists(exe)) found.Add(exe);
        }
        try
        {
            var r = ctx.Processes.RunAsync("py", new[] { "-0p" }, TimeSpan.FromSeconds(20), ctx.Token).GetAwaiter().GetResult();
            if (r.Succeeded)
                foreach (Match m in PyLauncherLine.Matches(r.StdOut)) { var p = m.Groups["path"].Value.Trim(); if (File.Exists(p)) found.Add(p); }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "python.exe");
                if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) { if (File.Exists(exe)) inv.Notes.Add("python.exe in WindowsApps is the Microsoft Store alias, not a real interpreter; ignored."); continue; }
                if (File.Exists(exe)) found.Add(exe);
            }
            catch { }
        }
        foreach (var p in ctx.SelectedProfiles)
            foreach (var condaName in new[] { "anaconda3", "miniconda3", "Anaconda3", "Miniconda3", "miniforge3" })
            {
                var exe = Path.Combine(p.ProfilePath, condaName, "python.exe");
                if (File.Exists(exe)) found.Add(exe);
            }
        return found.Distinct(PathUtil.Comparer);
    }

    static IEnumerable<string> FindFiles(string root, string fileName, int maxDepth, CancellationToken ct)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            try
            {
                var f = Path.Combine(dir, fileName);
                if (File.Exists(f)) { result.Add(f); continue; }
                if (depth >= maxDepth) continue;
                foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (sub.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase) || sub.Name.StartsWith('$')
                        || sub.Name is "node_modules" or ".git" or "site-packages" or "__pycache__" or "Windows" or "Program Files" or "Program Files (x86)") continue;
                    stack.Push((sub.FullName, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        return result;
    }

    static Dictionary<string, string> ReadCfg(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var i = line.IndexOf('=');
                if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }
        catch { }
        return d;
    }

    CommandRecord Run(DiscoveryContext ctx, PythonEnvironmentInfo env, string[] args, TimeSpan timeout, out ProcessResult result)
    {
        result = ctx.Processes.RunAsync(env.Executable, args, timeout, ctx.Token).GetAwaiter().GetResult();
        var rec = new CommandRecord
        {
            Command = Path.GetFileName(env.Executable) + " " + string.Join(' ', args), ExitCode = result.ExitCode, TimedOut = result.TimedOut,
            StartError = result.StartError, StdErr = Redaction.Apply(Truncate(result.StdErr, 4000)), Seconds = result.Duration.TotalSeconds,
            Status = result.Succeeded ? CaptureStatus.Captured : CaptureStatus.Failed
        };
        env.Commands.Add(rec);
        if (!result.Succeeded) env.Errors.Add($"'{rec.Command}' failed: {result.Describe()}" + (string.IsNullOrWhiteSpace(result.StdErr) ? "" : " — " + Truncate(result.StdErr.Trim(), 300)));
        return rec;
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    const string ProbeScript = "import sys,platform,json,struct;print(json.dumps({'v':platform.python_version(),'bits':struct.calcsize('P')*8,'exe':sys.executable,'base':getattr(sys,'base_prefix',sys.prefix),'prefix':sys.prefix,'machine':platform.machine()}))";

    void Query(DiscoveryContext ctx, PythonEnvironmentInfo env)
    {
        if (!File.Exists(env.Executable))
        {
            env.Errors.Add("Interpreter not found: " + env.Executable + (env.BaseInterpreter != null ? " (base interpreter " + env.BaseInterpreter + ")" : ""));
            env.Overall = CaptureStatus.Failed;
            env.Architecture = env.BaseInterpreter != null && File.Exists(env.BaseInterpreter) ? PeInfo.Architecture(env.BaseInterpreter) : SoftwareItem.UnknownText;
            return;
        }
        env.Architecture = PeInfo.Architecture(env.Executable);
        var t = ctx.Options.PythonTimeout;
        Run(ctx, env, new[] { "-I", "-c", ProbeScript }, TimeSpan.FromSeconds(30), out var probe);
        if (probe.Succeeded)
        {
            try
            {
                using var doc = JsonDocument.Parse(probe.StdOut.Trim().Split('\n').Last());
                var r = doc.RootElement;
                env.Version = r.GetProperty("v").GetString() ?? SoftwareItem.UnknownText;
                var bits = r.GetProperty("bits").GetInt32();
                var machine = r.TryGetProperty("machine", out var mm) ? mm.GetString() ?? "" : "";
                env.Architecture = machine.Contains("ARM64", StringComparison.OrdinalIgnoreCase) ? "ARM64" : bits == 64 ? "x64" : "x86";
                env.BasePrefix = r.GetProperty("base").GetString();
            }
            catch (Exception ex) { env.Errors.Add("Version probe output not understood: " + ex.Message); }
        }
        if (!ctx.Options.QueryPythonPackages) { env.Overall = probe.Succeeded ? CaptureStatus.Partial : CaptureStatus.Failed; env.Notes.Add("Package queries were disabled."); return; }

        Run(ctx, env, new[] { "-m", "pip", "--version" }, TimeSpan.FromSeconds(30), out var pipv);
        if (pipv.Succeeded) env.PipVersion = pipv.StdOut.Trim();
        var list = Run(ctx, env, new[] { "-m", "pip", "list", "--format=json", "--disable-pip-version-check" }, t, out var listR);
        if (listR.Succeeded)
        {
            try
            {
                using var doc = JsonDocument.Parse(listR.StdOut.Trim().Split('\n').Last(l => l.TrimStart().StartsWith("[")));
                foreach (var p in doc.RootElement.EnumerateArray())
                    env.Packages.Add(new PipPackage { Name = p.GetProperty("name").GetString() ?? "", Version = p.GetProperty("version").GetString() ?? "" });
                env.ListStatus = CaptureStatus.Captured;
            }
            catch (Exception ex) { env.ListStatus = CaptureStatus.Failed; env.Errors.Add("pip list output not understood: " + ex.Message); }
        }
        else env.ListStatus = CaptureStatus.Failed;

        Run(ctx, env, new[] { "-m", "pip", "freeze", "--all" }, t, out var freezeR);
        if (freezeR.Succeeded)
        {
            env.Freeze = freezeR.StdOut.Replace("\r\n", "\n");
            env.FreezeStatus = CaptureStatus.Captured;
            foreach (var line in env.Freeze.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var l = line.Trim();
                if (l.StartsWith("-e ") || l.Contains(" @ file:", StringComparison.OrdinalIgnoreCase) || l.Contains(" @ git+", StringComparison.OrdinalIgnoreCase) || l.StartsWith("git+"))
                    env.NonReinstallablePackages.Add(l);
            }
            if (env.NonReinstallablePackages.Count > 0)
            {
                env.FreezeStatus = CaptureStatus.Partial;
                env.Notes.Add($"{env.NonReinstallablePackages.Count} package(s) come from local files or version control and cannot be reinstalled from PyPI alone.");
            }
        }
        else env.FreezeStatus = CaptureStatus.Failed;

        if (pipv.Succeeded && PipSupportsInspect(env.PipVersion))
        {
            Run(ctx, env, new[] { "-m", "pip", "inspect" }, t, out var insp);
            env.InspectStatus = insp.Succeeded ? CaptureStatus.Captured : CaptureStatus.Failed;
            if (!insp.Succeeded) env.Errors.RemoveAt(env.Errors.Count - 1); // optional command: failure is recorded in Commands only
        }

        env.Overall = !probe.Succeeded ? CaptureStatus.Failed
            : env.FreezeStatus == CaptureStatus.Captured ? CaptureStatus.Captured
            : env.FreezeStatus == CaptureStatus.Partial || env.ListStatus == CaptureStatus.Captured ? CaptureStatus.Partial
            : CaptureStatus.Failed;
    }

    static bool PipSupportsInspect(string? pipVersion)
    {
        var m = Regex.Match(pipVersion ?? "", @"pip (\d+)\.(\d+)");
        if (!m.Success) return false;
        int maj = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
        return maj > 22 || (maj == 22 && min >= 2);
    }

    void DetectGpu(DiscoveryContext ctx, PythonInventory inv)
    {
        try
        {
            var r = ctx.Processes.RunAsync("nvidia-smi", new[] { "--query-gpu=name,driver_version", "--format=csv,noheader" }, TimeSpan.FromSeconds(20), ctx.Token).GetAwaiter().GetResult();
            if (r.Succeeded) foreach (var l in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)) inv.GpuInfo.Add("NVIDIA GPU: " + l.Trim());
            else inv.GpuInfo.Add("nvidia-smi not available (" + r.Describe() + "): NVIDIA GPU/driver unknown; check manually if you use GPU OCR.");
        }
        catch (OperationCanceledException) { throw; }
        catch { inv.GpuInfo.Add("GPU information could not be read; check manually."); }
        var cuda = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cuda)) inv.GpuInfo.Add("CUDA_PATH = " + cuda);
    }

    static void AnnotateGpu(PythonEnvironmentInfo env, PythonInventory inv)
    {
        foreach (var p in env.Packages)
        {
            if (p.Name.Equals("paddlepaddle-gpu", StringComparison.OrdinalIgnoreCase)) env.GpuNotes.Add($"paddlepaddle-gpu {p.Version} (needs matching CUDA/cuDNN and NVIDIA driver)");
            else if (p.Name.Equals("paddlepaddle", StringComparison.OrdinalIgnoreCase)) env.Notes.Add($"paddlepaddle {p.Version} (CPU build)");
            else if ((p.Name.Equals("torch", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("onnxruntime-gpu", StringComparison.OrdinalIgnoreCase)) && Regex.IsMatch(p.Version + p.Name, @"cu\d+|gpu", RegexOptions.IgnoreCase))
                env.GpuNotes.Add($"{p.Name} {p.Version} (GPU build)");
        }
    }

    void DiscoverOcr(DiscoveryContext ctx, PythonInventory inv, ProviderOutput o)
    {
        string? dir = null;
        foreach (var view in new[] { RegView.Reg64, RegView.Reg32 })
            dir ??= ctx.Registry.Value("HKLM", @"SOFTWARE\Tesseract-OCR", "InstallDir", view)?.AsString() ?? ctx.Registry.Value("HKLM", @"SOFTWARE\Tesseract-OCR", "Path", view)?.AsString();
        dir ??= ctx.AllSoftware.FirstOrDefault(s => s.Name.Contains("Tesseract", StringComparison.OrdinalIgnoreCase) && s.InstallLocation != SoftwareItem.UnknownText)?.InstallLocation;
        foreach (var guess in new[] { @"C:\Program Files\Tesseract-OCR", @"C:\Program Files (x86)\Tesseract-OCR" }) if (dir == null && Directory.Exists(guess)) dir = guess;
        if (dir != null && File.Exists(Path.Combine(dir, "tesseract.exe")))
        {
            inv.TesseractPath = Path.Combine(dir, "tesseract.exe");
            try
            {
                var r = ctx.Processes.RunAsync(inv.TesseractPath, new[] { "--version" }, TimeSpan.FromSeconds(20), ctx.Token).GetAwaiter().GetResult();
                var text = (r.StdOut + r.StdErr).Split('\n').FirstOrDefault(l => l.Contains("tesseract", StringComparison.OrdinalIgnoreCase))?.Trim();
                inv.TesseractVersion = text ?? "Unknown (" + r.Describe() + ")";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { inv.TesseractVersion = "Unknown: " + ex.Message; }
            o.Components.Add(new Component
            {
                Id = "tesseract", Name = "Tesseract OCR " + inv.TesseractVersion, Application = "OCR", ProviderId = OcrProviderId, Kind = "OCR engine",
                Classification = Classification.InstallerManaged, MigrationMethod = MigrationMethod.Reinstall, SourcePath = dir,
                DestinationRule = "Install Tesseract (same major version) on the new system", OfficialSource = "https://github.com/UB-Mannheim/tesseract/wiki",
                VerificationMethod = "tesseract --list-langs shows your languages (e.g. hye)", Basis = "tesseract.exe found", Confidence = Confidence.Confirmed
            });
            var tessdata = Path.Combine(dir, "tessdata");
            if (Directory.Exists(tessdata))
                o.Roots.Add(new ScanRoot { Path = tessdata, Category = "OCR / Tesseract language data", ProviderId = OcrProviderId, Recursive = true, MaxDepth = 3,
                    Rule = f => f.Extension.Equals(".traineddata", StringComparison.OrdinalIgnoreCase)
                        ? new FileDecision(f.Name is "eng.traineddata" or "osd.traineddata" ? Classification.InstallerManaged : Classification.Review,
                            "Language model: keep if you added or trained it yourself; official models can be downloaded again")
                        : f.Extension.Equals(".config", StringComparison.OrdinalIgnoreCase) || f.RelativeToRoot.StartsWith("configs", StringComparison.OrdinalIgnoreCase)
                            ? new FileDecision(Classification.Review, "Tesseract config") : null,
                    Note = "Installer folder: only language models and configs are listed" });
        }
        var prefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrEmpty(prefix) && Directory.Exists(prefix) && (dir == null || !PathUtil.IsInsideOrSame(prefix, dir)))
            o.Roots.Add(new ScanRoot { Path = prefix, Category = "OCR / TESSDATA_PREFIX models", ProviderId = OcrProviderId, MaxDepth = 4,
                Rule = Rules.Extensions(Classification.Recommended, "Model in your custom TESSDATA_PREFIX folder", ".traineddata", ".config", ".user-words", ".user-patterns") });
        foreach (var p in ctx.SelectedProfiles.Where(p => p.Access == AccessState.Accessible))
        {
            foreach (var (sub, label) in new[] { (".paddleocr", "PaddleOCR models"), (".paddlex", "PaddleX models"), (".EasyOCR", "EasyOCR models"), (@".cache\calfa", "CALFA cache") })
            {
                var d = Path.Combine(p.ProfilePath, sub);
                if (!Directory.Exists(d)) continue;
                o.Roots.Add(new ScanRoot { Path = d, Category = "OCR / " + label, ProviderId = OcrProviderId, ProfileSid = p.Sid, MaxDepth = 8,
                    Rule = f => Rules.IsTempOrLock(f) ? null : new FileDecision(Classification.Review, "Downloaded/cached model folder: re-downloadable unless you placed your own models here"),
                    Note = "Model cache: Review" });
            }
        }
        if (inv.TesseractPath == null && ctx.IsSelected(OcrProviderId))
            o.Issues.Add(new Issue(IssueSeverity.Info, "OCR", "Tesseract was not found on this computer."));
    }
}
