# Changelog

## 4.0.0-beta — new implementation from the consolidated requirements
Built from scratch in C# / .NET 8 / WPF as one self-contained Windows x64 program (no installation, no PowerShell).

- **Software**: 64/32-bit, per-user and other loaded users' Uninstall entries; portable-app folders; optional winget export (supplementary; saved for `winget import`). Search, filter, group (publisher/category/recovery method), sort, multi-select, CSV/JSON export. Unknown values are shown as `Unknown`.
- **User Profiles**: real profiles by SID, loaded/accessible/last-used; never pre-selected; opt-in user-data folders; custom folders on any drive with nested/duplicate detection; Windows settings and runtimes options; empty-folder modes.
- **Targeted discovery providers**
  - Word/Office: Templates (Normal.dotm), user and system Startup, custom template/startup paths from the registry, Trusted Locations, UProof/Proof/Windows spelling dictionaries (CUSTOM.DIC, hy-AM), AutoCorrect, Ribbon/QAT (*.officeUI only; *_Rules/caches never entered), Building Blocks (Review), templates found in Documents/Desktop/Downloads; COM/VSTO add-ins; Office fingerprint and compatibility rules.
  - Static VBA analysis without running macros (own MS-CFB + MS-OVBA reader): references, Declare (PtrSafe), CreateObject ProgIDs, file paths in code, ActiveX, external links.
  - Python: PEP 514 registry, py launcher, PATH, conda; virtual environments in profiles/custom folders; exact version/arch via the interpreter; pip --version / list / freeze / inspect with timeouts, exit codes and stderr; Captured/Partial/Failed; non-PyPI packages flagged; GPU/CUDA notes without invented versions.
  - OCR: Tesseract (version, tessdata, TESSDATA_PREFIX), PaddleOCR/PaddleX/EasyOCR/CALFA model folders (Review), project models (.traineddata/.onnx/.pdparams…).
  - Windows settings: per-user fonts, non-secret environment variables, PATH entries, default-app associations (manual).
  - Runtimes as a reinstall list with official sources; review-only registry export (secrets/MRU skipped, machine-specific values marked).
- **Migration Plan**: one-pass index of provider roots only; tri-state category/folder/file selection that applies to never-opened folders; empty vs only-excluded vs inaccessible; junctions/symlinks not followed; destination/package folders never scanned; Explorer view with Details/Small/Large/Extra Large icons, system icons, real image thumbnails, Up, double-click, search, sort, paging; dependency graph and recovery steps panel; DSC-002 legend.
- **Backup**: only inside the chosen existing folder (`Migration-<machine>-<UTC>-<id>`), real names in `Data\<drive>\<path>`, `.partial` + SHA-256 compare + atomic rename, bounded retries, locked/changed files reported, Incomplete status, retry-failed-only, disk-full stop, free-space check, same-physical-disk warning, manifests + reports, Metadata-Hashes.json written last.
- **Verify** (read-only), **Test Restore** (empty sandbox, byte-identical), **Readiness** (Ready/NeedsReview/Blocked/NotTested; explicit confirmations), **Restore** (profile mapping, drive mapping, prerequisites, dry run, no overwrite by default, journal + Undo, per-value HKCU registry apply with old/new view), Python environment recreation with confirmation, functional checklist.
- Background jobs with state machine, cancel, heartbeat ("No recent progress — investigate" after 60 s), incremental log reading, draggable splitters, Armenian in-app Help, README.html/README.txt, diagnostic launcher, command-line interface (`wmt-cli.exe`).
- CI on Windows: unit/integration tests and a scripted GUI self-test with screenshots on every build.
