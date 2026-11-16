param([string]$Exe, [string]$OutDir, [int]$TimeoutMinutes = 20)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$p = Start-Process -FilePath $Exe -ArgumentList @('--selftest', $OutDir) -PassThru
if (-not $p.WaitForExit($TimeoutMinutes * 60 * 1000)) { Stop-Process -Id $p.Id -Force; Write-Host "SELFTEST TIMEOUT"; exit 124 }
$code = $p.ExitCode
Write-Host "Self-test exit code: $code"
$json = Join-Path $OutDir 'selftest.json'
if (Test-Path $json) { Get-Content $json -Raw | Write-Host } else { Write-Host 'selftest.json missing' }
$log = Join-Path $OutDir 'selftest-activity.log'
if (Test-Path $log) { Write-Host '--- last log lines ---'; Get-Content $log -Tail 60 | Write-Host }
exit $code
