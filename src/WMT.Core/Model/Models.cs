using System.Text.Json.Serialization;

namespace WMT.Core;

/// <summary>Progress report. Total == null means unknown total (indeterminate progress).</summary>
public sealed record ProgressInfo(string Phase, long Count, long? Total, string? Detail = null);

public sealed class Issue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public IssueSeverity Severity { get; set; }
    public string Area { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
    public string? RequiredAction { get; set; }

    public Issue() { }
    public Issue(IssueSeverity severity, string area, string message, string? path = null, string? action = null)
    { Severity = severity; Area = area; Message = message; Path = path; RequiredAction = action; }
    public override string ToString() => $"[{Severity}] {Area}: {Message}" + (Path != null ? $" ({Path})" : "");
}

/// <summary>SW-002 software row. Unknown values are the literal "Unknown", never guessed.</summary>
public sealed class SoftwareItem
{
    public const string UnknownText = "Unknown";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = UnknownText;
    public string Version { get; set; } = UnknownText;
    public string Build { get; set; } = UnknownText;
    public string Architecture { get; set; } = UnknownText;
    public string InstallationType { get; set; } = UnknownText;
    public string InstallLocation { get; set; } = UnknownText;
    public string DetectedSource { get; set; } = "";
    /// <summary>Uninstall registry key name (MSI ProductCode for Windows Installer packages).</summary>
    public string UninstallKey { get; set; } = "";
    /// <summary>Application / Runtime / Office / Python / OCR / Development / Portable / System.</summary>
    public string Category { get; set; } = "Application";
    public string RecoveryMethod { get; set; } = "Reinstall";
    public string DependencyStatus { get; set; } = "Not analyzed";
    public Confidence Confidence { get; set; } = Confidence.Unknown;
    /// <summary>Provider IDs that know how to do targeted discovery for this item.</summary>
    public List<string> Providers { get; set; } = new();
    public string? OfficialSource { get; set; }
    public bool Selected { get; set; }

    [JsonIgnore] public string SelectionKey => $"{Name}|{Version}|{Architecture}".ToLowerInvariant();
}

/// <summary>USR-002 profile row.</summary>
public sealed class UserProfileInfo
{
    public string Account { get; set; } = "";
    public string Sid { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public bool Loaded { get; set; }
    public bool IsCurrentUser { get; set; }
    public AccessState Access { get; set; } = AccessState.Unknown;
    public string AccessNote { get; set; } = "";
    public DateTime? LastUsedUtc { get; set; }
    public bool Selected { get; set; }
}

/// <summary>A folder that a provider asks the indexer to walk (WF-004: only these are physically scanned).</summary>
public sealed class ScanRoot
{
    public string Path { get; set; } = "";
    /// <summary>Category shown as top-level node, e.g. "Microsoft Word / Templates".</summary>
    public string Category { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? ProfileSid { get; set; }
    public bool Recursive { get; set; } = true;
    public int MaxDepth { get; set; } = 64;
    /// <summary>True for bulk user-data roots (Documents etc.); affects empty-folder mode.</summary>
    public bool IsUserData { get; set; }
    /// <summary>Directory names (case-insensitive) never entered below this root.</summary>
    public HashSet<string> ExcludedDirectoryNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Full directory paths never entered below this root (e.g. a virtual environment inside a project).</summary>
    public HashSet<string> ExcludedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Decides class/note for a file; null result = not eligible (excluded).</summary>
    [JsonIgnore] public Func<FileCandidate, FileDecision?> Rule { get; set; } = _ => new FileDecision(Classification.Recommended, null);
    public string Note { get; set; } = "";
    /// <summary>When set, only these file names directly in <see cref="Path"/> are indexed (no folder enumeration).</summary>
    public List<string>? OnlyFiles { get; set; }
}

public readonly record struct FileCandidate(string FullPath, string Name, string Extension, long Length, DateTime LastWriteUtc, FileAttributes Attributes, string RelativeToRoot);

public sealed record FileDecision(Classification Classification, string? Note, string? ComponentId = null);

/// <summary>DSC-005 component: anything that is migrated, reinstalled, applied or checked.</summary>
public sealed class Component
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Application { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Kind { get; set; } = "";
    public Classification Classification { get; set; }
    public MigrationMethod MigrationMethod { get; set; }
    public string? SourcePath { get; set; }
    public string DestinationRule { get; set; } = "";
    public List<string> RequiredPrerequisites { get; set; } = new();
    public string VerificationMethod { get; set; } = "";
    public List<string> ManualActions { get; set; } = new();
    /// <summary>Why the tool decided this (DSC-005 "basis").</summary>
    public string Basis { get; set; } = "";
    public Confidence Confidence { get; set; } = Confidence.Unknown;
    public string? ProfileSid { get; set; }
    public LifecycleState State { get; set; } = LifecycleState.Discovered;
    public string? OfficialSource { get; set; }
}

/// <summary>DSC-003 dependency edge Application → Component → Dependency → RequiredAction.</summary>
public sealed class Dependency
{
    public string Application { get; set; } = "";
    public string Component { get; set; } = "";
    public string DependsOn { get; set; } = "";
    public string Type { get; set; } = "";
    public Confidence Confidence { get; set; } = Confidence.Unknown;
    public string RequiredAction { get; set; } = "";
    public string ValidationTest { get; set; } = "";
    public string Evidence { get; set; } = "";
}

/// <summary>REG-001 review-only registry value.</summary>
public sealed class RegistryValueRecord
{
    public string Id { get; set; } = "";
    public string Hive { get; set; } = "HKCU";
    public string KeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    /// <summary>RegistryValueKind name: String, ExpandString, DWord, QWord, MultiString, Binary.</summary>
    public string Type { get; set; } = "";
    /// <summary>String form: DWord/QWord decimal; Binary hex; MultiString joined by \0 escaped as JSON array in Values.</summary>
    public string? Data { get; set; }
    public List<string>? MultiData { get; set; }
    public string? ProfileSid { get; set; }
    public string Application { get; set; } = "";
    public string RestoreRecommendation { get; set; } = "Review";
    public string DefaultAction { get; set; } = "ReviewOnly";
    public bool MachineSpecific { get; set; }
    public string Note { get; set; } = "";
}
