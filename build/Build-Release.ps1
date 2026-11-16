param([string]$Version = '4.1.0-beta', [string]$BuildId = 'local')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$name = "WMT-$Version"
$out  = Join-Path $root "release\$name"
if (Test-Path (Join-Path $root 'release')) { Remove-Item (Join-Path $root 'release') -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

$common = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-p:DebugSymbols=false',"-p:InformationalVersion=$Version+$BuildId")
dotnet publish "$root\src\WMT.App\WMT.App.csproj" @common -o "$out"
if ($LASTEXITCODE -ne 0) { throw 'publish WMT.App failed' }
$cli = Join-Path $root 'release\_cli'
dotnet publish "$root\src\WMT.Cli\WMT.Cli.csproj" @common -o "$cli"
if ($LASTEXITCODE -ne 0) { throw 'publish WMT.Cli failed' }
Copy-Item "$cli\wmt-cli.exe" "$out\wmt-cli.exe"
Remove-Item $cli -Recurse -Force

Set-Content -Path "$out\Launch-WMT.cmd" -Encoding ASCII -Value "@echo off`r`nstart `"`" `"%~dp0WMT.exe`""
Set-Content -Path "$out\Launch-WMT-Diagnostics.cmd" -Encoding ASCII -Value "@echo off`r`nstart `"`" `"%~dp0WMT.exe`" --diagnostics"
foreach ($d in 'CHANGELOG.md','KNOWN-ISSUES.md','SCHEMA.md') { Copy-Item "$root\docs\$d" "$out\$d" }
if (Test-Path "$root\TEST-REPORT.md") { Copy-Item "$root\TEST-REPORT.md" "$out\TEST-REPORT.md" }

# README.txt and README.html are generated from the same Help text that is embedded in the program (UI-011).
$help = [IO.File]::ReadAllText("$root\docs\Help.txt", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText("$out\README.txt", $help, (New-Object Text.UTF8Encoding($true)))
$enc = [System.Net.WebUtility]::HtmlEncode($help)
$html = "<!DOCTYPE html><html lang=`"hy`"><head><meta charset=`"utf-8`"><title>Workstation Migration Toolkit $Version — Օգնություն</title>" +
        "<style>body{font-family:'Segoe UI',Sylfaen,sans-serif;max-width:980px;margin:2em auto;padding:0 1em;line-height:1.45}pre{white-space:pre-wrap;font-family:inherit}</style></head>" +
        "<body><pre>$enc</pre></body></html>"
[IO.File]::WriteAllText("$out\README.html", $html, (New-Object Text.UTF8Encoding($false)))

$sums = Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name }
[IO.File]::WriteAllLines("$out\SHA256SUMS.txt", $sums, (New-Object Text.UTF8Encoding($false)))

$zip = Join-Path $root "release\$name.zip"
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Built $zip"
