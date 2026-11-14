namespace WMT.Core;

public enum Confidence { Confirmed, Inferred, Unknown }
public enum AccessState { Accessible, Unreadable, Missing }
public enum JobState { Queued, Running, Completed, CompletedWithWarnings, Failed, Cancelled }

/// <summary>Progress report. Total == null means the total is unknown (indeterminate progress).</summary>
public sealed record ProgressInfo(string Phase, long Count, long? Total);

public sealed record SoftwareItem(
    string Name, string Publisher, string Version, string Architecture,
    string Source, string Type, string RecoveryMethod, Confidence Confidence, string InstallLocation);

public sealed record UserProfileItem(
    string Account, string Sid, string ProfilePath, bool Loaded,
    AccessState Access, string AccessNote, DateTime? LastUsedUtc);

public sealed record SoftwareScanResult(
    IReadOnlyList<SoftwareItem> Items, int FilteredCount, int DuplicateCount, IReadOnlyList<string> Warnings);

public sealed record ProfileScanResult(
    IReadOnlyList<UserProfileItem> Items, int ExcludedSystemCount, IReadOnlyList<string> Warnings);

public interface ISoftwareScanner { SoftwareScanResult Scan(IProgress<ProgressInfo>? progress, CancellationToken ct); }
public interface IProfileScanner { ProfileScanResult Scan(IProgress<ProgressInfo>? progress, CancellationToken ct); }
