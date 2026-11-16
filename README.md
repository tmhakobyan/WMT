# Workstation Migration Toolkit

Windows 10 → Windows 11 workstation migration: targeted discovery of installed programs and user profiles, a selectable migration plan,
verified backup with real file names, sandbox test restore, readiness report and controlled restore.

- Download: see **Releases** (`WMT-<version>.zip`).
- Build: .NET 8 SDK on Windows — `dotnet test tests/WMT.Tests` and `./build/Build-Release.ps1`.
- CI: `.github/workflows/build.yml` runs the tests and a scripted GUI self-test on every push.

Documentation: `docs/Help.txt` (Armenian, also embedded in the program), `docs/SCHEMA.md`, `docs/KNOWN-ISSUES.md`, `docs/CHANGELOG.md`.
