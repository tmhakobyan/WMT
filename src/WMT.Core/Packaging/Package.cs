using System.Reflection;
using System.Runtime.InteropServices;

namespace WMT.Core;

public static class Toolkit
{
    public static string Version =>
        typeof(Toolkit).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "4.0.0";
    public static string BuildId => Environment.GetEnvironmentVariable("WMT_BUILD_ID") ?? (typeof(Toolkit).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");
    public const string IntegrityNote = "SHA-256 values are integrity checksums, not digital signatures. They detect accidental or intentional changes only when compared with a trusted copy of the manifests.";
}

/// <summary>PKG-002..007 package metadata (Manifests\Package.json).</summary>
public sealed class PackageInfo
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolkitVersion { get; set; } = Toolkit.Version;
    public string PackageId { get; set; } = "";
    public string SourceMachine { get; set; } = Environment.MachineName;
    public string SourceOS { get; set; } = RuntimeInformation.OSDescription;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    /// <summary>InProgress / Complete / Incomplete.</summary>
    public string PackageStatus { get; set; } = "InProgress";
    public int SelectedFileCount { get; set; }
    public int CopiedFileCount { get; set; }
    public int FailedFileCount { get; set; }
    public int ChangedSinceScanCount { get; set; }
    public long SelectedBytes { get; set; }
    public int IncludedEmptyFolders { get; set; }
    public string HashAlgorithm { get; set; } = "SHA-256";
    public string IntegrityNote { get; set; } = Toolkit.IntegrityNote;
    public string? DestinationDiskCheck { get; set; }
    public bool? SamePhysicalDiskAsWindows { get; set; }
    public List<string> BackupRuns { get; set; } = new();
    public string Layout { get; set; } = "Data\\<Drive>\\<original path>; Manifests\\*.json; Reports\\*; Logs\\activity.log; Status\\* (results written after the backup, not covered by Metadata-Hashes.json)";
}

public sealed class MetadataHashes
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Algorithm { get; set; } = "SHA-256";
    public string Note { get; set; } = Toolkit.IntegrityNote;
    public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Paths inside a package (all relative paths are validated with <see cref="PathUtil.SafeCombine"/>).</summary>
public sealed class PackageLayout
{
    public string Root { get; }
    public PackageLayout(string root) { Root = Path.GetFullPath(root); }
    public string Manifests => Path.Combine(Root, "Manifests");
    public string Reports => Path.Combine(Root, "Reports");
    public string Logs => Path.Combine(Root, "Logs");
    public string Data => Path.Combine(Root, "Data");
    public string Status => Path.Combine(Root, "Status");
    public string M(string name) => Path.Combine(Manifests, name);
    public string PackageJson => M("Package.json");
    public string FilesJson => M("Files.json");
    public string DirectoriesJson => M("Directories.json");
    public string MetadataHashesJson => M("Metadata-Hashes.json");
    public string Log => Path.Combine(Logs, "activity.log");

    /// <summary>Folders whose files are covered by Metadata-Hashes.json.</summary>
    public static readonly string[] MetadataFolders = { "Manifests", "Reports", "Logs" };
    public static readonly string[] MetadataRootFiles = { "README.html", "README.txt" };

    public static bool IsPackage(string dir) => File.Exists(Path.Combine(dir, "Manifests", "Package.json"));

    public static string NewName(string machine, DateTime utc, string id) =>
        $"Migration-{Sanitize(machine)}-{utc:yyyyMMdd'T'HHmmss'Z'}-{id}";

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
}

/// <summary>Everything the backup stores about the source, read back by Verify/Restore/Readiness.</summary>
public sealed class LoadedPackage
{
    public required PackageLayout Layout { get; init; }
    public required PackageInfo Info { get; init; }
    public required List<FileEntry> Files { get; init; }
    public List<DirectoryRecord> Directories { get; init; } = new();
    public List<UserProfileInfo> Profiles { get; init; } = new();
    public List<SoftwareItem> Software { get; init; } = new();
    public List<Component> Components { get; init; } = new();
    public List<Dependency> Dependencies { get; init; } = new();
    public List<RegistryValueRecord> Registry { get; init; } = new();
    public List<Issue> Issues { get; init; } = new();
    public List<string> FunctionalTests { get; init; } = new();
    public OfficeEnvironment? Office { get; init; }
    public PythonInventory? Python { get; init; }
    public WindowsSettingsSnapshot? WindowsSettings { get; init; }
    public KeyboardSnapshot? Keyboard { get; init; }

    public static LoadedPackage Load(string root)
    {
        var l = new PackageLayout(root);
        if (!File.Exists(l.PackageJson)) throw new FileNotFoundException("Not a migration package (Manifests\\Package.json missing).", l.PackageJson);
        T? Opt<T>(string name) where T : class => File.Exists(l.M(name)) ? Json.ReadFile<T>(l.M(name)) : null;
        var plan = Opt<MigrationPlan>("Plan.json");
        return new LoadedPackage
        {
            Layout = l,
            Info = Json.ReadFile<PackageInfo>(l.PackageJson),
            Files = File.Exists(l.FilesJson) ? Json.ReadFile<List<FileEntry>>(l.FilesJson) : new(),
            Directories = Opt<List<DirectoryRecord>>("Directories.json") ?? new(),
            Profiles = Opt<List<UserProfileInfo>>("Profiles.json") ?? new(),
            Software = Opt<List<SoftwareItem>>("Software.json") ?? new(),
            Components = plan?.Components ?? new(),
            Dependencies = Opt<List<Dependency>>("Dependencies.json") ?? new(),
            Registry = File.Exists(Path.Combine(l.Manifests, "Registry-REVIEW-ONLY", "Registry.json")) ? Json.ReadFile<List<RegistryValueRecord>>(Path.Combine(l.Manifests, "Registry-REVIEW-ONLY", "Registry.json")) : new(),
            Issues = File.Exists(Path.Combine(l.Reports, "Issues.json")) ? Json.ReadFile<List<Issue>>(Path.Combine(l.Reports, "Issues.json")) : new(),
            FunctionalTests = plan?.FunctionalTests ?? new(),
            Office = Opt<OfficeEnvironment>("Office-Environment.json"),
            Python = Opt<PythonInventory>("Python-Environments.json"),
            WindowsSettings = TryOpt<WindowsSettingsSnapshot>(l.M("Windows-Settings.json")),
            Keyboard = TryOpt<KeyboardSnapshot>(l.M("Keyboard-Layouts.json")),
        };
    }

    static T? TryOpt<T>(string path) where T : class
    {
        try { return File.Exists(path) ? Json.ReadFile<T>(path) : null; } catch { return null; }
    }

    /// <summary>Fingerprint of the package metadata: changes whenever Metadata-Hashes.json changes.</summary>
    public string MetadataFingerprint() => File.Exists(Layout.MetadataHashesJson) ? Hashing.Sha256File(Layout.MetadataHashesJson) : "none";
}
