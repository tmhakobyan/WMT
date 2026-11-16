# Known issues and limitations — 4.0.0-beta

Tested automatically on GitHub Actions (Windows Server). See TEST-REPORT.md for the exact list of executed tests.

**Not tested yet (needs you, on the real computer)**
1. Windows 10 and Windows 11 desktops: CI uses Windows Server; layout/DPI on your monitor is untested.
2. Real Microsoft Office Professional Plus 2016 with your own templates, macros, dictionaries and add-ins.
3. Real Python/CALFA/PaddleOCR environments and Tesseract; GPU/CUDA compatibility.
4. A full real restore on a freshly installed Windows 11 and the functional checklist.
5. UNC/network destinations (supported; only the path logic is tested).

**Known limitations**
6. Per-user programs and settings of users who are not signed in are not visible: their registry hives are never mounted automatically (by design).
7. Microsoft Store (AppX) apps are not listed.
8. Default-app associations cannot be restored automatically (Windows protects them): they are listed for manual setup.
9. Registry restore writes only to HKEY_CURRENT_USER of the signed-in user, one value at a time; binary values are review-only.
10. VBA analysis is static: dependencies hidden in strings built at run time, password-protected VBA projects, or p-code-only projects are reported as Unknown.
11. Restoring files into Program Files/Windows is blocked; use the program's installer.
12. The program is not code-signed: Windows SmartScreen may warn on first start ("More info" → "Run anyway").
13. Recreating Python environments installs packages from PyPI; packages from local files or Git (listed in the plan) need their source.
14. Python packages are captured per environment; conda-specific packages are not exported in conda format.
