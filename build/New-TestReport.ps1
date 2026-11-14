param([string]$Trx, [string]$Out = 'TEST-REPORT.md')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Trx)) { "# Test report`n`n**No .trx found: tests did NOT run.**" | Set-Content $Out -Encoding UTF8; exit 1 }
[xml]$x = Get-Content $Trx -Raw
$c = $x.TestRun.ResultSummary.Counters
$ns = New-Object Xml.XmlNamespaceManager($x.NameTable); $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$results = $x.SelectNodes('//t:UnitTestResult', $ns)
$lines = @("# Test report — 3.0.0-m1", "", "Executed on: $([Environment]::OSVersion.VersionString) at $((Get-Date).ToUniversalTime().ToString('u'))", "",
  "Total: $($c.total)  Passed: $($c.passed)  Failed: $($c.failed)  NotExecuted: $($c.notExecuted)", "", "| Test | Outcome |", "|---|---|")
foreach ($r in $results) { $lines += "| $($r.testName) | $($r.outcome) |" }
foreach ($r in ($results | Where-Object outcome -eq 'Failed')) {
  $lines += ""; $lines += "### FAIL: $($r.testName)"; $lines += '```'; $lines += $r.Output.ErrorInfo.Message; $lines += '```'
}
$lines += ""; $lines += "Not covered: WPF runtime/GUI, DPI, clean-machine EXE launch, Windows 10 vs 11 smoke test (manual)."
$lines | Set-Content $Out -Encoding UTF8
