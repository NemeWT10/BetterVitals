<#
.SYNOPSIS
  The screenshots of the public README, rendered from the app's real windows on a fake PC (never the screen).

.DESCRIPTION
  Builds tools\ReadmeMedia and HiddenDesktopRunner, then renders the pictures on a private, invisible Windows desktop
  (the user's desktop, mouse and keyboard are never involved). The sensors are FakeSensorSource on a pinned clock and
  the history is the app's preview history, so every run gives the same pictures. Output: artifacts\readme-media
  (PNG files, report.txt, index.html to look at them). -Publish copies the PNG files into media\ for the README.

.EXAMPLE
  tools\readme-media.ps1                      # all pictures, then open artifacts\readme-media\index.html
  tools\readme-media.ps1 -Only hero,history -NoBuild
  tools\readme-media.ps1 -Publish             # render, then copy into media\
#>
param(
    [string[]] $Only = @(),
    [int] $TimeoutSec = 300,
    [switch] $NoBuild,
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\readme-media'
$media = Join-Path $root 'media'
$runner = Join-Path $root 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\win-x64\HiddenDesktopRunner.exe'
$exe = Join-Path $root 'tools\ReadmeMedia\bin\Release\net10.0-windows\win-x64\ReadmeMedia.exe'
# the README's order; alt texts as the README uses them
$pictures = [ordered]@{
    'hero'           = 'The tray flyout with CPU and GPU temperature and load next to the Overview page with key values and sparklines'
    'history'        = 'The History page: six hours of CPU, GPU and GPU Hot Spot temperatures with an evening game selected and its min, average and max'
    'overlay'        = 'The overlay in a game, a row per component: FPS with a small graph; CPU temperature and core clocks; GPU temperature, GPU Hot Spot and GPU clock'
    'overlay-editor' = 'The overlay editor in simple mode: GPU values picked as tiles, a live preview on a monitor and a close-up of the overlay'
    'sensors'        = 'The Sensors page with History and Overlay switches per sensor and the explanation bubble for GPU Hot Spot'
}

$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$unknown = @($Only | Where-Object { -not $pictures.Contains($_) })
if ($unknown.Count -gt 0) { throw "Unknown picture(s): $($unknown -join ', '). Pictures: $($pictures.Keys -join ', ')" }

if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'tools\HiddenDesktopRunner\HiddenDesktopRunner.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'HiddenDesktopRunner build failed' }
    & dotnet build (Join-Path $root 'tools\ReadmeMedia\ReadmeMedia.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'ReadmeMedia build failed' }
}
foreach ($f in @($runner, $exe)) { if (-not (Test-Path $f)) { throw "missing: $f (run without -NoBuild)" } }

# a full run starts clean (only ever inside artifacts\readme-media)
New-Item -ItemType Directory -Force $out | Out-Null
if ($Only.Count -eq 0) { Get-ChildItem $out -Filter *.png | Remove-Item -Force }

$arguments = @('--timeout', $TimeoutSec, '--', $exe, $out)
if ($Only.Count -gt 0) { $arguments += 'only=' + ($Only -join ',') }
$watch = [Diagnostics.Stopwatch]::StartNew()
& $runner @arguments
$code = $LASTEXITCODE
Write-Host ("exit code {0}, {1:n1} s" -f $code, $watch.Elapsed.TotalSeconds)

$report = ''
$reportFile = Join-Path $out 'report.txt'
if (Test-Path $reportFile) { $report = Get-Content $reportFile -Raw -Encoding UTF8 }
$problems = @()
if ($code -ne 0) { $problems += "ReadmeMedia exit code $code" }
foreach ($name in $pictures.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    $png = Join-Path $out "$name.png"
    if (-not (Test-Path $png)) { $problems += "missing: $name.png"; continue }
    $kb = [Math]::Round((Get-Item $png).Length / 1KB)
    if ($kb -gt 400) { $problems += "$name.png is $kb KB (over 400 KB)" }
}

# a page to look at them
$cards = foreach ($name in $pictures.Keys) {
    $png = Join-Path $out "$name.png"
    if (-not (Test-Path $png)) { continue }
    $line = ($report -split "`n" | Where-Object { $_ -like "$name.png:*" } | Select-Object -First 1)
    $info = if ($line) { [Net.WebUtility]::HtmlEncode($line.Trim()) } else { "$name.png" }
    $alt = [Net.WebUtility]::HtmlEncode($pictures[$name])
    "<figure><img src=""$name.png"" alt=""$alt""><figcaption><b>$info</b><br>$alt</figcaption></figure>"
}
$html = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>README pictures</title>
<style>
  body { margin: 0; padding: 24px; background: #0e1016; color: #e5e7eb; font: 14px 'Segoe UI', sans-serif; }
  h1 { font-weight: 600; font-size: 20px; margin: 0 0 18px; }
  figure { margin: 0 0 32px; max-width: 1600px; }
  img { max-width: 100%; border-radius: 6px; }
  figcaption { margin-top: 8px; color: #9ca3af; }
  figcaption b { color: #e5e7eb; font-weight: 600; }
  pre { white-space: pre-wrap; color: #9ca3af; font-size: 12px; }
</style></head>
<body><h1>README pictures</h1>
$($cards -join "`n")
<pre>$([Net.WebUtility]::HtmlEncode($report))</pre>
</body></html>
"@
$index = Join-Path $out 'index.html'
[IO.File]::WriteAllText($index, $html, (New-Object Text.UTF8Encoding $false))
Write-Host $report
Write-Host "Look at them: $index"

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Yellow }
    exit 1
}

if ($Publish) {
    New-Item -ItemType Directory -Force $media | Out-Null
    foreach ($name in $pictures.Keys) {
        $png = Join-Path $out "$name.png"
        if (Test-Path $png) {
            Copy-Item $png (Join-Path $media "$name.png") -Force
            Write-Host "media\$name.png"
        }
    }
    Write-Host 'Copied into media\ (not committed).'
}
