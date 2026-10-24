<#
.SYNOPSIS
  The UI's own cost (flyout, every panel page, memory after the panel closed) on a hidden desktop, fake sensors.

.DESCRIPTION
  Runs the Release build (or -Exe) with --ui-probe on a private, invisible desktop through HiddenDesktopRunner — never
  on the user's desktop, no tray icon, test data under tests\output\ui-perf. The probe (UI\Dev\UiProbe.cs) measures the
  default fake PC and a ~400-sensor one and writes ui-probe.txt; this script copies it to
  tests\output\ui-perf\<Label>.txt and prints it.

.EXAMPLE
  tools\ui-probe.ps1 -Label before
  tools\ui-probe.ps1 -Label after -Window 30 -NoWide
#>
param(
    [string] $Label = 'run',
    [int] $Window = 20,
    [string] $Exe,
    [switch] $NoBuild,
    [switch] $NoWide,
    [switch] $FlyoutOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'tests\output\ui-perf'
$runner = Join-Path $root 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\win-x64\HiddenDesktopRunner.exe'
if (-not (Test-Path $runner)) {
    & dotnet build (Join-Path $root 'tools\HiddenDesktopRunner\HiddenDesktopRunner.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'HiddenDesktopRunner build failed' }
}
if (-not $Exe) {
    if (-not $NoBuild) {
        & dotnet build (Join-Path $root 'src\BetterVitals\BetterVitals.csproj') -c Release -m:2 -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    }
    $Exe = Join-Path $root 'src\BetterVitals\bin\Release\net10.0-windows\win-x64\BetterVitals.exe'
}
if (-not (Test-Path $Exe)) { throw "missing: $Exe" }

$data = Join-Path $out "data-$Label"
if (Test-Path $data) {
    $resolved = (Resolve-Path $data).Path
    if (-not $resolved.StartsWith($out, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to clean $resolved" }
    Remove-Item $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force $data | Out-Null

$appArgs = @('--ui-probe', "$Window", '--data-dir', $data)
if ($NoWide) { $appArgs += @('--ui-probe-wide', '0') }
if ($FlyoutOnly) { $appArgs += '--ui-probe-flyout' }
$timeout = 120 + 30 * $Window
Write-Host "== $Label ($Window s windows, timeout $timeout s)"
$sw = [Diagnostics.Stopwatch]::StartNew()
& $runner --timeout $timeout -- $Exe @appArgs | Out-Null
Write-Host ("exit code {0}, {1:n0} s" -f $LASTEXITCODE, $sw.Elapsed.TotalSeconds)
$report = Get-ChildItem $data -Recurse -Filter 'ui-probe.txt' | Select-Object -First 1
if (-not $report) { throw "no ui-probe.txt under $data (see the logs there)" }
Copy-Item $report.FullName (Join-Path $out "$Label.txt") -Force
Get-Content $report.FullName
