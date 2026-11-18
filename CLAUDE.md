# WMT — Workstation Migration Toolkit (notes for Claude)

- The user (tmhakobyan) writes in Armenian and is not a developer: answer in Armenian, simple words, always give the
  direct download link `https://github.com/tmhakobyan/WMT/releases/download/v<version>/WMT-<version>.zip`.
- C# 12 / .NET 8: `src/WMT.Core` (logic), `src/WMT.App` (WPF MVVM, UI labels in English), `src/WMT.Cli`, `tests/WMT.Tests` (xunit).
  `docs/Help.txt` is the Armenian help shown in the app.
- **No .NET SDK in the cloud workspace.** The only compiler is GitHub Actions (`.github/workflows/build.yml`, windows-latest):
  tests → release build → GUI self-test (`WMT.exe --selftest`, screenshots) → keyboard DLL sweep → evidence →
  `ci-latest` prerelease (always) → user release `v<WMT_VERSION>` (only when tests and self-test pass).
  Before pushing C#/XAML changes, use the `csharp-compile-reviewer` agent and `python3 build/check-consistency.py`.
- Rules enforced by tests: every `Content=`/`Header=` label of MainWindow.xaml must appear in docs/Help.txt;
  the version must match in Directory.Build.props, Package.cs fallback, build/Build-Release.ps1, build.yml WMT_VERSION,
  newest docs/CHANGELOG.md section (the hook in .claude/settings.json checks both after each edit).
  When bumping the version add the old tag to the delete list in build.yml.
- Safety principles of the product: nothing is scanned at start; sources are only read; restore never overwrites by
  default and is journaled with Undo; registry restore is opt-in per value; secrets, tokens, Wi-Fi passwords and
  license keys are never exported; Windows 10 must not be wiped before Readiness = "Migration Ready".
- Do not touch the user's other repository `tmhakobyan/narrator`.
- Commits: `git -c commit.gpgsign=false commit`, then `git -c push.negotiate=true push origin main`.
