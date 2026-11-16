param([string]$Trx, [string]$Out = 'TEST-REPORT.md', [string]$SelfTest = '')
$ErrorActionPreference = 'Stop'
$lines = @("# Test report — Workstation Migration Toolkit", "",
  "Executed by GitHub Actions on: $([Environment]::OSVersion.VersionString), $((Get-Date).ToUniversalTime().ToString('u'))", "")
if (Test-Path $Trx) {
  [xml]$x = Get-Content $Trx -Raw
  $c = $x.TestRun.ResultSummary.Counters
  $ns = New-Object Xml.XmlNamespaceManager($x.NameTable); $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
  $results = $x.SelectNodes('//t:UnitTestResult', $ns)
  $lines += "## Automated tests (xunit, executed)"; $lines += ""
  $lines += "Total: $($c.total) · Passed: $($c.passed) · Failed: $($c.failed) · Not executed: $($c.notExecuted)"; $lines += ""
  $lines += "| Test | Outcome | Duration |"; $lines += "|---|---|---|"
  foreach ($r in ($results | Sort-Object testName)) { $lines += "| $($r.testName) | $($r.outcome) | $($r.duration) |" }
  foreach ($r in ($results | Where-Object outcome -eq 'Failed')) {
    $lines += ""; $lines += "### FAILED: $($r.testName)"; $lines += '```'; $lines += $r.Output.ErrorInfo.Message; $lines += $r.Output.ErrorInfo.StackTrace; $lines += '```'
  }
} else { $lines += "**No test results found: the automated tests did NOT run.**" }
$lines += ""
if ($SelfTest -and (Test-Path $SelfTest)) {
  $s = Get-Content $SelfTest -Raw | ConvertFrom-Json
  $lines += "## GUI self-test (real WPF window driven by WMT.exe --selftest on the CI Windows machine)"; $lines += ""
  $lines += "All passed: **$($s.AllPassed)** · Max UI stall during long operations: $([math]::Round($s.MaxUiStallMs)) ms · OS: $($s.Os)"; $lines += ""
  $lines += "| Step | Result | Detail |"; $lines += "|---|---|---|"
  foreach ($st in $s.Steps) { $lines += "| $($st.Step) | $(if ($st.Passed) {'PASS'} else {'FAIL'}) | $($st.Detail) |" }
} else { $lines += "## GUI self-test"; $lines += ""; $lines += "**Not executed or produced no result.**" }
$lines += ""
$lines += "## Not tested automatically"
$lines += "- Real Windows 10 and Windows 11 desktops (CI runs on Windows Server); high-DPI monitors other than the CI default."
$lines += "- Real Microsoft Office 2016 with your templates, real Python/CALFA/PaddleOCR environments, Tesseract."
$lines += "- Real restore onto a freshly installed Windows 11 and the functional checklist (Word macros, OCR runs)."
$lines += "- UNC/network destinations (path logic is unit-tested; no network share in CI)."
$lines | Set-Content $Out -Encoding UTF8
