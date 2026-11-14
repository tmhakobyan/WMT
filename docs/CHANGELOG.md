# Changelog

## 3.0.0-m1 — Discovery/UI shell (first v3 build)
- New implementation: C# / .NET 8 / WPF (MVVM), self-contained single-file EXE. Replaces PowerShell + WinForms v2.1.2.
- Software tab: Registry 32/64-bit + HKCU Uninstall discovery, filtering of system components/patches/duplicates, search, classification (Application / Runtime / Office / Python), recovery method column.
- User profiles tab: real Windows profiles from ProfileList + SID, loaded/accessible/last-used, system profiles hidden, no SID auto-mapping.
- Office details: fingerprint (family/build/arch/install type/proofing) + comparison rules (tested).
- Async discovery on a job runner with Queued/Running/Completed/CompletedWithWarnings/Failed/Cancelled states, correlation ID, exit codes, real cancellation; UI always returns to idle.
- Append-only JSON-lines log, concurrent-safe, incremental reader (never re-reads the whole log), secret redaction, log-write failure surfaces as a warning.
- Draggable splitter between content and log; status bar with phase/count/progress/last activity/60 s "investigate" notice/Cancel.
- In-app Help (Armenian), README.html/.txt, 2-click launcher, diagnostics launcher.
- Removed v2.1.2 architectural defects: `HasExited` as success, whole-log polling, UI-thread waits, regex path splitting.
- Not included (by design, later milestones): Plan, Explorer tree, Backup, Verify, Restore.
