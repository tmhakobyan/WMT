# Known issues / open limitations — 3.0.0-m1

1. **Nothing in this build has been compiled or run by its author.** The authoring environment had no .NET SDK or Windows. First real compile + test run happens on GitHub Actions (windows-latest). Expect possible compile errors on first run; send the Actions log back.
2. Windows GUI runtime behaviour (responsiveness, DPI, splitter, Armenian rendering, Win10/Win11) is unverified until tested on a real Windows PC.
3. Spec §4A gaps in M1: portable-app detection and the supplementary `winget list` source are not implemented.
4. `Architecture` is inferred from the registry view (x64/x86), not from the executable's PE header; HKCU entries are `Unknown`.
5. Profiles come from the `ProfileList` registry key (the data behind `Win32_UserProfile`), not from WMI directly. No WMI dependency in M1.
6. Worker is an in-process background task, not a separate process. The §9 "atomic completion marker" is implemented as a `job.completed` log event plus the returned result; a separate-process worker is deferred until Backup (M3) where it matters.
7. Last used = NTUSER.DAT last-write time (approximation); `unknown` when unavailable.
8. Log is written to `%LOCALAPPDATA%\WMT\Logs`, not inside a backup package (no packages exist yet).
9. Tests that depend on Windows path semantics (profile folder-name fallback) will not pass on Linux; run them on Windows.
10. Office: Word add-ins, templates, dictionaries, VBA dependencies, and Python/OCR environments are M2 scope.
