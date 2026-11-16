namespace WMT.Core;

/// <summary>DSC-002 component classification.</summary>
public enum Classification
{
    /// <summary>Keep: user-owned content with a clear restore-by-copy rationale.</summary>
    Recommended,
    /// <summary>User decides: may be useful, may be regenerated or installer-created.</summary>
    Review,
    /// <summary>Created/managed by an installer: keep install information, do not copy.</summary>
    InstallerManaged,
    /// <summary>Automatic restore is not possible; manual action only.</summary>
    Unsupported
}

/// <summary>DSC-003 / SYS-001 confidence.</summary>
public enum Confidence { Confirmed, Inferred, Unknown }

/// <summary>OBJ-006 lifecycle. Each state is distinct; one never implies a later one.</summary>
public enum LifecycleState { Discovered, Planned, Copied, Verified, Restored, FunctionallyValidated }

/// <summary>RDY-001.</summary>
public enum ReadinessState { Ready, NeedsReview, Blocked, NotTested }

/// <summary>OBJ-003 (a)-(e).</summary>
public enum MigrationMethod { Reinstall, CopyFile, ApplySetting, RebuildDependency, ManualCheck }

/// <summary>NFR-002.</summary>
public enum JobState { Queued, Running, Completed, CompletedWithWarnings, Failed, Cancelled }

public enum AccessState { Accessible, Inaccessible, Missing, Unknown }

/// <summary>PY-002/003 capture status.</summary>
public enum CaptureStatus { Captured, Partial, Failed, NotAttempted }

public enum IssueSeverity { Info, Warning, Error, Blocker }

public enum FileCopyStatus { Pending, Copied, Failed, Skipped }

/// <summary>IDX-003 empty-folder handling.</summary>
public enum EmptyFolderMode
{
    /// <summary>Default: empty and only-excluded folders are left out.</summary>
    ExcludeEmpty,
    /// <summary>Keep empty folders found inside application/configuration roots (not bulk user-data folders).</summary>
    KeepEmptyInApplicationRoots,
    /// <summary>Keep every truly empty folder found.</summary>
    KeepAllEmpty
}

/// <summary>Folder state used by the index (IDX-004: inaccessible is never "empty").</summary>
public enum FolderState { Normal, Empty, OnlyExcluded, Inaccessible, SkippedReparsePoint, SkippedPackage }
