<#
.SYNOPSIS
  Resource measurement of BetterVitals (perf-guard): the published single-file exe on a hidden desktop, real sensors.

.DESCRIPTION
  Publishes the exe (unless -NoBuild / -Exe), copies it alone into tests\output\perf\bin (it then runs like a downloaded
  copy) and runs it with --tray-probe --perf on a private, invisible desktop through HiddenDesktopRunner — never on the
  user's desktop, no tray icon, test data folders under tests\output\perf. Scenarios:
    tray     default settings (Key preset recorded every 2 s): tray rest → panel open (all sensors 1 s) → tray again
    overlay  the same with Overlay.Enabled = true and the default overlay items (one window)
    overlay2 the same, the default items split into the main and the second overlay window
  The app writes perf-probe.txt (UI\Dev\TrayProbe.cs); this script copies it to tests\output\perf\<Label>-<scenario>.txt
  and prints the summary lines.

.EXAMPLE
  tools\perf.ps1                                  # publish, both scenarios, 90 s windows, real sensors
  tools\perf.ps1 -NoBuild -Scenario tray -Window 120 -Label tc-off
  tools\perf.ps1 -FakeSensors                     # the fake PC instead of LHM / ETW / native
  tools\perf.ps1 -Scenario overlay2 -TrayOnly     # two overlay windows, the tray window only
  tools\perf.ps1 -Scenario tray -DeviceChanges 10 # ten device changes at the end (re-discovery cost / growth)
#>
param(
    [ValidateSet('all', 'tray', 'overlay', 'overlay2')] [string] $Scenario = 'all',
    [int] $Window = 90,
    [string] $Label = 'run',
    [string] $Exe,
    [switch] $NoBuild,
    [switch] $FakeSensors,
    [switch] $TrayOnly,
    [int] $DeviceChanges = 1
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'tests\output\perf'
$runner = Join-Path $root 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\win-x64\HiddenDesktopRunner.exe'

if (-not (Test-Path $runner)) {
    & dotnet build (Join-Path $root 'tools\HiddenDesktopRunner\HiddenDesktopRunner.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'HiddenDesktopRunner build failed' }
}
if (-not $Exe) {
    $publish = Join-Path $root 'artifacts\publish'
    if (-not $NoBuild) {
        & dotnet publish (Join-Path $root 'src\BetterVitals') -c Release -r win-x64 -p:PublishSingleFile=true -o $publish -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
    }
    $Exe = Join-Path $publish 'BetterVitals.exe'
}
if (-not (Test-Path $Exe)) { throw "missing: $Exe" }

# the exe alone in its own folder, like a download
$bin = Join-Path $out "bin-$Label"
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item $Exe (Join-Path $bin 'BetterVitals.exe') -Force
$exeCopy = Join-Path $bin 'BetterVitals.exe'

$scenarios = if ($Scenario -eq 'all') { @('tray', 'overlay') } else { @($Scenario) }
foreach ($s in $scenarios) {
    $data = Join-Path $out "data-$Label-$s"
    if (Test-Path $data) {
        $resolved = (Resolve-Path $data).Path
        if (-not $resolved.StartsWith($out, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to clean $resolved" }
        Remove-Item $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Force (Join-Path $data 'roaming') | Out-Null
    if ($s -like 'overlay*') {
        # the default items (FPS, CPU / GPU temperature…) are filled by the app once the sensors are known
        '{ "Overlay": { "Enabled": true } }' | Set-Content -Encoding utf8 (Join-Path $data 'roaming\settings.json')
    }

    $appArgs = @('--tray-probe', '--perf', "$Window", '--data-dir', $data)
    if (-not $FakeSensors) { $appArgs += '--real-sensors' }
    if ($TrayOnly) { $appArgs += '--perf-tray-only' }
    if ($DeviceChanges -gt 1) { $appArgs += @('--perf-device-changes', "$DeviceChanges") }
    if ($s -eq 'overlay2') { $appArgs += @('--perf-overlay-windows', '2') }
    $timeout = 200 + 3 * $Window
    Write-Host "== $Label / $s ($Window s windows, timeout $timeout s)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $runner --timeout $timeout -- $exeCopy @appArgs | Out-Null
    $code = $LASTEXITCODE
    Write-Host ("exit code {0}, {1:n0} s" -f $code, $sw.Elapsed.TotalSeconds)

    $probe = Join-Path $data 'local\logs\perf-probe.txt'
    $log = Join-Path $data 'local\logs\app.log'
    if (-not (Test-Path $probe)) {
        Write-Host 'perf-probe.txt missing'
        if (Test-Path $log) { Get-Content $log -Tail 25 }
        continue
    }
    $dest = Join-Path $out "$Label-$s.txt"
    Copy-Item $probe $dest -Force
    if (Test-Path $log) {
        $errors = @(Select-String -Path $log -Pattern '\[ERROR\]|\[WARN\]' | ForEach-Object { $_.Line })
        if ($errors.Count -gt 0) { Add-Content $dest ''; Add-Content $dest '## app.log warnings / errors'; Add-Content $dest $errors }
    }
    Get-Content $dest | Where-Object { $_ -match '^(## |memory|CPU|wake-ups|engine|disk|GC|process|start|panel closed|device change|latency|end)' }
    Write-Host "-> $dest"
}
