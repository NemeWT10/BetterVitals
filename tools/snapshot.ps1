<#
.SYNOPSIS
  UI snapshots of BetterVitals for visual QA, rendered from the app's own visual tree (never from the screen).

.DESCRIPTION
  Builds the app and HiddenDesktopRunner, then runs BetterVitals.exe --snapshots on a private, invisible Windows
  desktop (the user's desktop, mouse and keyboard are never involved). The app drives itself (UI\Dev\SnapshotMode.cs):
  flyout, tray menu, every panel page, the style gallery, the explanation bubble, a toast and the icon sheet, in every
  theme and language, written to tests\output\snap\png together with report.txt (missing Loc keys, Loc table and
  layout problems). Everything is verified afterwards.

.EXAMPLE
  tools\snapshot.ps1                       # 5 themes × PL / EN
  tools\snapshot.ps1 -Language pl -Theme daylight -NoBuild
  tools\snapshot.ps1 -TrayProbe            # tray memory probe instead of snapshots
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [ValidateSet('all', 'pl', 'en')] [string] $Language = 'all',
    [ValidateSet('all', 'aurora', 'graphite', 'daylight', 'neon', 'sunset')] [string] $Theme = 'all',
    [int] $TimeoutSec = 600,
    [switch] $NoBuild,
    [switch] $TrayProbe
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'tests\output\snap'
$png = Join-Path $out 'png'
$data = Join-Path $out 'data'
$runner = Join-Path $root 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\win-x64\HiddenDesktopRunner.exe'
$exe = Join-Path $root "src\BetterVitals\bin\$Configuration\net10.0-windows\win-x64\BetterVitals.exe"

if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'tools\HiddenDesktopRunner\HiddenDesktopRunner.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'HiddenDesktopRunner build failed' }
    & dotnet build (Join-Path $root 'src\BetterVitals\BetterVitals.csproj') -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'BetterVitals build failed' }
}
foreach ($f in @($runner, $exe)) { if (-not (Test-Path $f)) { throw "missing: $f" } }

# fresh state; only ever inside tests\output
if (Test-Path $out) {
    $resolved = (Resolve-Path $out).Path
    if (-not $resolved.StartsWith((Join-Path $root 'tests\output'), [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to clean $resolved" }
    Remove-Item $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force $png, $data | Out-Null

$sw = [Diagnostics.Stopwatch]::StartNew()
if ($TrayProbe) {
    & $runner --timeout $TimeoutSec -- $exe --tray-probe --data-dir $data
}
else {
    & $runner --timeout $TimeoutSec -- $exe --snapshots $png $Language $Theme --data-dir $data
}
$code = $LASTEXITCODE
Write-Host ("exit code {0}, {1:n1} s" -f $code, $sw.Elapsed.TotalSeconds)

$log = Join-Path $data 'local\logs\app.log'
if ($TrayProbe) {
    $probe = Join-Path $data 'local\logs\tray-probe.txt'
    if (Test-Path $probe) { Get-Content $probe } else { Write-Host 'tray-probe.txt missing'; if (Test-Path $log) { Get-Content $log -Tail 25 }; exit 1 }
    exit $code
}

# verify
$problems = @()
$index = Join-Path $png 'index.txt'
$failed = Join-Path $png 'FAILED.txt'
if ($code -ne 0) { $problems += "app exit code $code" }
if (-not (Test-Path $index)) { $problems += 'index.txt missing (app did not finish)' }
if (Test-Path $failed) { $problems += 'snapshot failures:'; $problems += (Get-Content $failed) }
$pngs = @(Get-ChildItem $png -Filter *.png -ErrorAction SilentlyContinue)
if ($pngs.Count -eq 0) { $problems += 'no PNG files' }
foreach ($f in $pngs) { if ($f.Length -lt 3000) { $problems += "suspiciously small: $($f.Name) ($($f.Length) bytes)" } }
if (Test-Path $log) {
    $errors = @(Select-String -Path $log -Pattern 'ERROR|SNAPSHOT FAIL' | ForEach-Object { $_.Line })
    if ($errors.Count -gt 0) { $problems += 'app.log errors:'; $problems += ($errors | Select-Object -First 20) }
}

Write-Host ("{0} PNG files in {1}" -f $pngs.Count, $png)
$report = Join-Path $png 'report.txt'
if (Test-Path $report) { Get-Content $report | Select-Object -First 40 }
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Yellow }
    if (Test-Path $log) { Write-Host '--- app.log (tail) ---'; Get-Content $log -Tail 25 }
    exit 1
}
Write-Host 'snapshot OK'
