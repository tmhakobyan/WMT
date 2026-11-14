param([string]$Name = 'WMT-v3.0.0-m1')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root "release\$Name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

$common = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=None','-p:DebugSymbols=false')
dotnet publish "$root\src\WMT.App\WMT.App.csproj" @common -o "$out"
if ($LASTEXITCODE -ne 0) { throw 'publish App failed' }
$tools = Join-Path $root 'release\_cli'
dotnet publish "$root\src\WMT.Cli\WMT.Cli.csproj" @common -o "$tools"
if ($LASTEXITCODE -ne 0) { throw 'publish CLI failed' }
Copy-Item "$tools\wmt-cli.exe" "$out\wmt-cli.exe"

Copy-Item "$root\build\Launch-WMT.cmd","$root\build\Launch-WMT-Diagnostics.cmd" $out
Copy-Item "$root\docs\CHANGELOG.md","$root\docs\KNOWN-ISSUES.md" $out
if (Test-Path "$root\TEST-REPORT.md") { Copy-Item "$root\TEST-REPORT.md" "$out\TEST-REPORT.md" } else { Copy-Item "$root\docs\TEST-REPORT.md" "$out\TEST-REPORT.md" }

# README.txt = Help.txt (single source); README.html generated from the same text.
$help = Get-Content "$root\docs\Help.txt" -Raw -Encoding UTF8
[IO.File]::WriteAllText("$out\README.txt", $help, (New-Object Text.UTF8Encoding($true)))
$enc = [System.Net.WebUtility]::HtmlEncode($help)
$html = "<!DOCTYPE html><html lang=`"hy`"><head><meta charset=`"utf-8`"><title>WMT v3.0.0-m1</title><style>body{font-family:'Segoe UI',Sylfaen,sans-serif;max-width:900px;margin:2em auto;padding:0 1em}pre{white-space:pre-wrap;font-family:inherit}</style></head><body><pre>$enc</pre></body></html>"
[IO.File]::WriteAllText("$out\README.html", $html, (New-Object Text.UTF8Encoding($false)))

$sums = Get-ChildItem $out -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($out.Length + 1)
}
[IO.File]::WriteAllLines("$out\SHA256SUMS.txt", $sums, (New-Object Text.UTF8Encoding($false)))

$zip = Join-Path $root "release\$Name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Built $zip"
