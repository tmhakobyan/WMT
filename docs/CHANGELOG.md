# Changelog

## 4.2.0-beta — clearer restore, Select all / Clear, right-click "only this item"
- Restore page reorganised into numbered steps (1. target profile, 2. Dry run, 3. review, 4. Restore selected) with a banner showing the whole order on the new Windows (programs → keyboard layouts → Windows settings → files → Python) and buttons that open the Keyboard Layouts / Windows Settings tabs.
- A "Next — …" line always says what to do now (open package, choose profile, run Dry run again after a profile change, review and restore).
- Select all / Clear buttons on every restore list (files, registry, Windows settings, keyboard layouts).
- Right-click a row: "Restore / Apply / Install only this item" (or "only these N items" for highlighted rows), "Tick/Untick highlighted rows", "Select all", "Clear all". For a file that already exists with different content it offers to replace it (the current file is saved for Undo).
- Check boxes in these lists toggle with one click; rows that cannot be restored have a disabled box; the Action column uses plain words (New file, Replace, Already the same, Exists (different), No target profile, Not possible).
- "Restore selected" runs the prerequisite check automatically; restored rows are marked "Restored just now".

## 4.1.0-beta — keyboard layouts restore without manual MSKLC work
- **.klc generation**: for every custom layout WMT reads the installed DLL's KBDTABLES (the same data MSKLC's "Load Existing Keyboard" reads) and writes an MSKLC source (.klc, UTF-16): shift states, characters, Caps Lock / SGCaps, dead keys (incl. chained), ligatures, key names. Saved inside every package (`Reports\Keyboard`) and on demand (**Save .klc files** → Documents\WMT Keyboard Layouts). **Open in MSKLC** opens it in Microsoft Keyboard Layout Creator, or offers Microsoft's download page.
- **Install from the saved DLL**: on the new Windows, *Install selected layouts* copies the layout DLL to System32 (and SysWOW64) and registers the KLID exactly like an MSKLC setup package (one UAC prompt). Existing different files/registrations are never overwritten; Layout Id conflicts get a free id. Layout DLLs are now Recommended in the plan and a DLL in the package makes the layout "Ready".
- **Rebuild from .klc**: when the package has only a .klc and MSKLC is installed, WMT runs MSKLC's compiler (kbdutool) to build the 64-bit and WOW64 DLLs, then installs them.
- MSKLC detection (Program Files, uninstall entry); `wmt-cli klc <dll> <out.klc> [KLID]`.
- CI: .klc export of every Windows keyboard DLL, and a round trip through MSKLC's compiler (generated .klc → kbdutool → DLL → same .klc); real install/uninstall of a test layout on the CI machine.

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
