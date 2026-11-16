# Package format (SchemaVersion 1)

```
<Destination>\Migration-<Machine>-<yyyyMMddTHHmmssZ>-<id>\
  Data\<Drive>\<original path>          files with real names (UNC: Data\UNC\server\share\...)
  Manifests\Package.json                 SchemaVersion, ToolkitVersion, PackageId, SourceMachine, SourceOS, CreatedUtc, CompletedUtc,
                                         PackageStatus (InProgress/Complete/Incomplete), counts, SamePhysicalDiskAsWindows, BackupRuns
  Manifests\Files.json                   one entry per selected file (below)
  Manifests\Directories.json             kept empty folders
  Manifests\Software.json                full software inventory (Selected flag)
  Manifests\Profiles.json                selected profiles (Account, Sid, ProfilePath, Access…)
  Manifests\Plan.json                    selection, options, Components, Dependencies, Issues, FunctionalTests
  Manifests\Dependencies.json            Application → Component → DependsOn → RequiredAction, Confidence
  Manifests\Office-Environment.json      Office fingerprint
  Manifests\Python-Environments.json     interpreters/venvs, versions, commands with exit codes/stderr, packages, status
  Manifests\Windows-Settings.json        env vars (non-secret), associations
  Manifests\VBA-Analysis.json            static VBA findings per template
  Manifests\Winget-Export.json           optional winget export
  Manifests\Registry-REVIEW-ONLY\Registry.json   KeyPath, ValueName, Type, Data/MultiData, ProfileSid, DefaultAction=ReviewOnly
  Manifests\Metadata-Hashes.json         SHA-256 of every file in Manifests, Reports, Logs and README.*; written last
  Reports\  Recovery-Plan.html/.txt, Readiness.html, Backup-Report.html, Software.html,
            Functional-Test-Checklist.html, PYTHON-RECOVERY-PLAN.txt, Python\<project>-<id>-requirements.txt, Issues.json
  Logs\activity.log                      JSON lines
  Status\                                written AFTER the backup (Verify-*, TestRestore-*, Confirmations-*, Readiness-*,
                                         Restore-*\journal.json, Functional-*); NOT covered by Metadata-Hashes.json
  README.html, README.txt
```

## Files.json entry
`Id, Category, ProviderId, SourcePath, RootPath, RelativePath, ProfileSid, Size, LastWriteUtc, Classification
(Recommended/Review/InstallerManaged/Unsupported), Note, Selected, IsUserData, DestinationRule, PackagePath, Sha256,
CopyStatus (Pending/Copied/Failed/Skipped), CopyError, Attempts, ChangedSinceScan, CopiedUtc, State
(Discovered/Planned/Copied/Verified/Restored/FunctionallyValidated)`

## Recovery actions (MigrationMethod)
Reinstall · CopyFile · ApplySetting · RebuildDependency · ManualCheck.

## Rules
- All JSON is UTF-8 without BOM, enums as strings, arrays are always arrays (also for 0 or 1 element).
- PackagePath must be a safe relative path starting with `Data\` (no `..`, no `:`, not rooted). Verify rejects anything else.
- SHA-256 values are integrity checksums, not digital signatures.
- Compatibility: readers must refuse unknown SchemaVersion values; future versions add fields only. Packages from the old
  PowerShell toolkit (`.bin` storage) are not read or rewritten by this version.
