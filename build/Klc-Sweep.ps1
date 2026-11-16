param([Parameter(Mandatory)][string]$Cli, [Parameter(Mandatory)][string]$Out)
# Exports every keyboard layout DLL of this Windows to .klc with wmt-cli (each in its own process, so a bad DLL cannot
# stop the sweep). When MSKLC's compiler is available (WMT_MSKLC_DIR), each .klc is compiled with kbdutool and the
# rebuilt DLL is exported again: identical .klc = the generated source describes the layout completely.
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Cli = (Resolve-Path $Cli).Path
$sys = [Environment]::GetFolderPath('System')
$klids = @{}
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Keyboard Layouts' | ForEach-Object {
    $f = (Get-ItemProperty $_.PSPath).'Layout File'
    if ($f -and -not $klids.ContainsKey($f.ToLower())) { $klids[$f.ToLower()] = $_.PSChildName }
}
$tool = $null
if ($env:WMT_MSKLC_DIR) { $tool = Get-ChildItem $env:WMT_MSKLC_DIR -Recurse -Filter kbdutool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 }
$rows = @(); $exportOk = 0; $exportFail = 0; $rtOk = 0; $rtFail = 0; $rtBuildFail = 0
$klcDir = Join-Path $Out 'klc'; New-Item -ItemType Directory -Force -Path $klcDir | Out-Null
foreach ($dll in Get-ChildItem $sys -Filter 'kbd*.dll' | Sort-Object Name) {
    $name = $dll.BaseName
    $klid = $klids[$dll.Name.ToLower()]; if (-not $klid) { $klid = '00000409' }
    $klc = Join-Path $klcDir "$name.klc"
    $msg = & $Cli klc $dll.FullName $klc $klid 2>&1 | Out-String
    $code = $LASTEXITCODE
    $row = [pscustomobject]@{ Dll = $dll.Name; Klid = $klid; Export = ''; RoundTrip = ''; Detail = $msg.Trim() }
    if ($code -ne 0 -or -not (Test-Path $klc)) { $row.Export = "FAIL($code)"; $exportFail++; $rows += $row; continue }
    $row.Export = 'ok'; $exportOk++
    if ($tool) {
        $work = Join-Path $env:RUNNER_TEMP "rt\$name"; New-Item -ItemType Directory -Force -Path $work | Out-Null
        Copy-Item $klc (Join-Path $work "$name.klc") -Force
        Push-Location $work
        $b = & $tool.FullName -u -n -m "$name.klc" 2>&1 | Out-String
        Pop-Location
        $built = Join-Path $work "$name.dll"
        if (-not (Test-Path $built)) { $row.RoundTrip = 'BUILD FAILED'; $row.Detail += ' | ' + ($b.Trim() -replace '\s+', ' '); $rtBuildFail++ }
        else {
            $again = Join-Path $work "$name.rebuilt.klc"
            & $Cli klc $built $again $klid 2>&1 | Out-Null
            if ((Test-Path $again) -and ((Get-Content $klc -Raw) -ceq (Get-Content $again -Raw))) { $row.RoundTrip = 'identical'; $rtOk++ }
            else {
                $row.RoundTrip = 'DIFFERS'; $rtFail++
                $d = Compare-Object (Get-Content $klc) (Get-Content $again) -CaseSensitive | Select-Object -First 4 | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
                $row.Detail += ' | ' + ($d -join ' ; ')
            }
        }
    }
    $rows += $row
}
$summary = "Keyboard DLLs: $($exportOk + $exportFail); exported: $exportOk; export failed: $exportFail" +
    $(if ($tool) { "; MSKLC round trip identical: $rtOk; differs: $rtFail; kbdutool build failed: $rtBuildFail" } else { '; MSKLC not available: no round trip' })
$rows | Format-Table -AutoSize -Wrap | Out-String -Width 400 | Set-Content (Join-Path $Out 'klc-sweep.txt') -Encoding utf8
Add-Content (Join-Path $Out 'klc-sweep.txt') "`n$summary"
$rows | Where-Object { $_.Export -ne 'ok' -or ($tool -and $_.RoundTrip -ne 'identical') } | Format-Table -AutoSize -Wrap | Out-String -Width 400 | Write-Host
Write-Host $summary
