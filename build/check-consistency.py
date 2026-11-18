#!/usr/bin/env python3
"""Fast consistency checks for WMT (seconds instead of a full CI run).

1. docs/Help.txt mentions every Content="..."/Header="..." label of MainWindow.xaml (same rule as HelpTests).
2. The version is the same everywhere it is written.

Usage:
  python3 build/check-consistency.py          # run all checks, exit 1 on problems
  python3 build/check-consistency.py --hook   # Claude Code PostToolUse hook: reads the tool call JSON from stdin,
                                              # checks only when a relevant file was edited, exit 2 = report to Claude
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(rel):
    with open(os.path.join(ROOT, rel), encoding="utf-8-sig") as f:
        return f.read()


def help_labels():
    xaml = read("src/WMT.App/MainWindow.xaml")
    help_text = read("docs/Help.txt")
    labels = []
    for m in re.finditer(r'(?:Content|Header)="([^"{][^"]*)"', xaml):
        label = m.group(1).replace("&amp;", "&")
        if label not in labels:
            labels.append(label)
    missing = [l for l in labels if l not in help_text]
    return [f"docs/Help.txt does not mention the UI label '{l}' (HelpTests will fail)" for l in missing]


def versions():
    problems = []
    props = read("Directory.Build.props")
    ver = re.search(r"<Version>([^<]+)</Version>", props).group(1)
    info = re.search(r"<InformationalVersion>([^<]+)</InformationalVersion>", props).group(1)
    if not info.startswith(ver):
        problems.append(f"Directory.Build.props: InformationalVersion {info} does not start with Version {ver}")
    checks = [
        ("src/WMT.Core/Packaging/Package.cs", rf'\?\? "{re.escape(ver)}";', f'fallback version "{ver}"'),
        ("build/Build-Release.ps1", rf"\$Version = '{re.escape(info)}'", f"default -Version '{info}'"),
        (".github/workflows/build.yml", rf"WMT_VERSION: {re.escape(info)}\b", f"WMT_VERSION: {info}"),
        ("docs/CHANGELOG.md", rf"^## {re.escape(info)}\b", f"a '## {info}' section"),
    ]
    for rel, pattern, what in checks:
        if not re.search(pattern, read(rel), re.MULTILINE):
            problems.append(f"{rel}: expected {what} (version in Directory.Build.props is {info})")
    changelog = read("docs/CHANGELOG.md")
    first = re.search(r"^## (\S+)", changelog, re.MULTILINE)
    if first and first.group(1) != info:
        problems.append(f"docs/CHANGELOG.md: newest section is {first.group(1)}, but the version is {info}")
    return problems


RELEVANT = ("MainWindow.xaml", "Help.txt", "Directory.Build.props", "Package.cs", "Build-Release.ps1", "build.yml", "CHANGELOG.md")


def main():
    hook = "--hook" in sys.argv
    if hook:
        try:
            data = json.load(sys.stdin)
        except Exception:
            return 0
        path = (data.get("tool_input") or {}).get("file_path") or ""
        if not path.replace("\\", "/").endswith(RELEVANT):
            return 0
        if not os.path.abspath(path).startswith(ROOT):
            return 0
    problems = help_labels() + versions()
    if not problems:
        if not hook:
            print("OK: Help.txt covers every UI label; version is consistent.")
        return 0
    print("\n".join(problems), file=sys.stderr)
    return 2 if hook else 1


if __name__ == "__main__":
    sys.exit(main())
