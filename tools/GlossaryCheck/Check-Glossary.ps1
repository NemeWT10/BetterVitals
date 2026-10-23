<#
.SYNOPSIS
  Validates src/BetterVitals.Core/Glossary/glossary.json and checks that every known sensor name has an explanation.

.DESCRIPTION
  Pattern rules (same as the app loader): a match pattern is compared with the whole sensor name, case-insensitively.
  "{n}" = one or more digits, "*" = any text (also empty). Everything else is literal. The FIRST matching entry wins.
  An entry applies to a sensor when kind is equal and (entry has no "hw" or hw is equal).

  Checks: JSON shape and encoding (UTF-8 without BOM), allowed kinds / hw values, text lengths
  (what <= 220, watch <= 160 characters per language), fallback for every kind, duplicates, shadowed entries,
  coverage of lhm-sensor-names.txt and own-sensor-names.txt ("Kind|Hw|Name" per line, '#' = comment).

.PARAMETER GlossaryDir
  Folder with glossary.json and lhm-sensor-names.txt (default: src\BetterVitals.Core\Glossary next to this tool).

.PARAMETER ShowUnused
  Also list entries that are never the first match for any known name (informational).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\GlossaryCheck\Check-Glossary.ps1
#>
param(
    [string]$GlossaryDir = (Join-Path $PSScriptRoot '..\..\src\BetterVitals.Core\Glossary'),
    [switch]$ShowUnused
)

$ErrorActionPreference = 'Stop'
$GlossaryDir = [System.IO.Path]::GetFullPath($GlossaryDir)

$allowedKinds = @('Voltage','Current','Power','Clock','Temperature','Load','Frequency','Fan','Flow','Control','Level',
                  'Factor','Data','SmallData','Throughput','TimeSpan','Timing','Energy','Noise','Conductivity','Humidity',
                  'Fps','FrameTime')
$allowedHw = @('Cpu','Gpu','Motherboard','Memory','Storage','Network','Battery','Psu','Cooler','Controller','Game')
$maxWhat = 220
$maxWatch = 160

$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
function Err([string]$m) { $script:errors.Add($m) }
function Warn([string]$m) { $script:warnings.Add($m) }

function ConvertTo-GlossaryRegex([string]$pattern) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('^')
    $i = 0
    while ($i -lt $pattern.Length) {
        if ($pattern.Substring($i).StartsWith('{n}')) { [void]$sb.Append('\d+'); $i += 3 }
        elseif ($pattern[$i] -eq '*') { [void]$sb.Append('.*'); $i++ }
        else { [void]$sb.Append([regex]::Escape([string]$pattern[$i])); $i++ }
    }
    [void]$sb.Append('$')
    return $sb.ToString()
}

function Get-PatternSamples([string]$pattern) {
    $variants = @(
        @{ n = '1';  s = '' },
        @{ n = '12'; s = 'x' },
        @{ n = '7';  s = 'Foo Bar 7' },
        @{ n = '3';  s = 'x y' }
    )
    $out = @()
    foreach ($v in $variants) {
        $out += $pattern.Replace('{n}', $v.n).Replace('*', $v.s)
    }
    return $out
}

# ---------------------------------------------------------------- load JSON
$jsonPath = Join-Path $GlossaryDir 'glossary.json'
if (-not (Test-Path $jsonPath)) { Write-Host "ERROR: $jsonPath not found"; exit 1 }
$bytes = [System.IO.File]::ReadAllBytes($jsonPath)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { Err 'glossary.json has a UTF-8 BOM (must be UTF-8 without BOM)' }
$text = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes)
if ($text.Contains("`t")) { Err 'glossary.json contains TAB characters (use 2-space indent)' }
try { $doc = $text | ConvertFrom-Json } catch { Write-Host "ERROR: JSON does not parse: $($_.Exception.Message)"; exit 1 }

if ($doc.version -ne 1) { Err "version must be 1 (found '$($doc.version)')" }
$entries = @($doc.entries)
if ($entries.Count -eq 0) { Err 'no entries' }

# ---------------------------------------------------------------- validate entries
$prepared = New-Object System.Collections.Generic.List[object]
$idx = 0
foreach ($e in $entries) {
    $idx++
    $label = "#$idx [$($e.kind)|$($e.hw)|$($e.match)]"
    $props = @($e.PSObject.Properties.Name)
    foreach ($p in $props) {
        if (@('kind','hw','match','what','watch') -notcontains $p) { Err "$label unknown property '$p'" }
    }
    if ($allowedKinds -notcontains $e.kind) { Err "$label invalid kind" }
    $hw = $null
    if ($props -contains 'hw') {
        if ($allowedHw -notcontains $e.hw) { Err "$label invalid hw" } else { $hw = [string]$e.hw }
    }
    if ([string]::IsNullOrWhiteSpace($e.match)) { Err "$label empty match"; continue }
    if ($e.match -ne $e.match.Trim()) { Err "$label match has leading/trailing whitespace" }
    $stripped = $e.match.Replace('{n}', '')
    if ($stripped.Contains('{') -or $stripped.Contains('}')) { Err "$label match contains an invalid brace token (only {n} is allowed)" }
    foreach ($field in @('what','watch')) {
        if ($field -eq 'watch' -and -not ($props -contains 'watch')) { continue }
        if ($field -eq 'what' -and -not ($props -contains 'what')) { Err "$label missing what"; continue }
        $obj = $e.$field
        $limit = if ($field -eq 'what') { $maxWhat } else { $maxWatch }
        foreach ($lang in @('en','pl')) {
            $t = $obj.$lang
            if ([string]::IsNullOrWhiteSpace($t)) { Err "$label $field.$lang is empty"; continue }
            if ($t.Length -gt $limit) { Err "$label $field.$lang is $($t.Length) chars (max $limit)" }
            if ($t -ne $t.Trim()) { Err "$label $field.$lang has leading/trailing whitespace" }
            if ($t -match "[\r\n\t]") { Err "$label $field.$lang contains a control character" }
            if ($t.Contains('  ')) { Warn "$label $field.$lang contains a double space" }
        }
        if ($obj.en -eq $obj.pl) { Warn "$label $field.en equals $field.pl" }
        if ($field -eq 'what' -and $obj.pl -cmatch '^[\x00-\x7F]*$' -and $obj.pl.Length -gt 120) { Warn "$label what.pl has no Polish diacritics (check the text)" }
    }
    $rxText = ConvertTo-GlossaryRegex $e.match
    $rx = New-Object System.Text.RegularExpressions.Regex($rxText, [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')
    $prepared.Add([pscustomobject]@{ Index = $idx; Kind = [string]$e.kind; Hw = $hw; Match = [string]$e.match; Rx = $rx; Used = 0; Label = $label })
}

# ---------------------------------------------------------------- fallbacks
if ($null -eq $doc.fallbacks) { Err 'missing fallbacks' } else {
    $fbNames = @($doc.fallbacks.PSObject.Properties.Name)
    foreach ($k in $allowedKinds) {
        if ($fbNames -notcontains $k) { Err "fallback missing for kind '$k'"; continue }
        foreach ($lang in @('en','pl')) {
            $t = $doc.fallbacks.$k.$lang
            if ([string]::IsNullOrWhiteSpace($t)) { Err "fallback $k.$lang is empty" }
            elseif ($t.Length -gt $maxWhat) { Err "fallback $k.$lang is $($t.Length) chars (max $maxWhat)" }
        }
    }
    foreach ($k in $fbNames) { if ($allowedKinds -notcontains $k) { Err "fallback for unknown kind '$k'" } }
}

# ---------------------------------------------------------------- duplicates and shadowing
$seenKeys = @{}
foreach ($p in $prepared) {
    $key = ('{0}|{1}|{2}' -f $p.Kind, $p.Hw, $p.Match.ToLowerInvariant())
    if ($seenKeys.ContainsKey($key)) { Err "$($p.Label) duplicates entry #$($seenKeys[$key])" } else { $seenKeys[$key] = $p.Index }
}
$byKind = @{}
foreach ($p in $prepared) { if (-not $byKind.ContainsKey($p.Kind)) { $byKind[$p.Kind] = New-Object System.Collections.Generic.List[object] }; $byKind[$p.Kind].Add($p) }
foreach ($kind in $byKind.Keys) {
    $list = $byKind[$kind]
    for ($j = 1; $j -lt $list.Count; $j++) {
        $later = $list[$j]
        $samples = Get-PatternSamples $later.Match
        for ($i = 0; $i -lt $j; $i++) {
            $earlier = $list[$i]
            # an earlier entry can only shadow when it applies to at least every hardware the later one applies to
            if ($null -ne $earlier.Hw -and $earlier.Hw -ne $later.Hw) { continue }
            $all = $true
            foreach ($s in $samples) { if (-not $earlier.Rx.IsMatch($s)) { $all = $false; break } }
            if ($all) { Err "$($later.Label) is shadowed by earlier entry #$($earlier.Index) [$($earlier.Kind)|$($earlier.Hw)|$($earlier.Match)]"; break }
        }
    }
}

# ---------------------------------------------------------------- coverage
function Test-Names([string]$file, [string]$title) {
    if (-not (Test-Path $file)) { Err "$title list not found: $file"; return }
    $total = 0; $matched = 0; $lineNo = 0
    $unmatched = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadAllLines($file, (New-Object System.Text.UTF8Encoding($false)))) {
        $lineNo++
        $l = $line.Trim()
        if ($l.Length -eq 0 -or $l.StartsWith('#')) { continue }
        $parts = $l.Split('|', 3)
        if ($parts.Count -ne 3) { Err "$title line ${lineNo}: expected Kind|Hw|Name"; continue }
        $kind = $parts[0]; $hw = $parts[1]; $name = $parts[2]
        if ($allowedKinds -notcontains $kind) { Err "$title line ${lineNo}: invalid kind '$kind'"; continue }
        if ($allowedHw -notcontains $hw) { Err "$title line ${lineNo}: invalid hw '$hw'"; continue }
        $total++
        $hit = $null
        if ($byKind.ContainsKey($kind)) {
            foreach ($p in $byKind[$kind]) {
                if ($null -ne $p.Hw -and $p.Hw -ne $hw) { continue }
                if ($p.Rx.IsMatch($name)) { $hit = $p; break }
            }
        }
        if ($null -eq $hit) { $unmatched.Add("$kind|$hw|$name") } else { $matched++; $hit.Used++ }
    }
    Write-Host ("{0}: {1} / {2} names matched" -f $title, $matched, $total)
    foreach ($u in $unmatched) { Err "$title has no entry for: $u" }
}
Test-Names (Join-Path $GlossaryDir 'lhm-sensor-names.txt') 'LHM names'
Test-Names (Join-Path $PSScriptRoot 'own-sensor-names.txt') 'Own names'

# ---------------------------------------------------------------- report
$unused = @($prepared | Where-Object { $_.Used -eq 0 })
Write-Host ("Entries: {0}   unused (never first match for a known name): {1}" -f $prepared.Count, $unused.Count)
if ($ShowUnused) { foreach ($u in $unused) { Write-Host "  unused: $($u.Label)" } }
foreach ($w in $warnings) { Write-Host "WARN:  $w" }
foreach ($er in $errors) { Write-Host "ERROR: $er" }
if ($errors.Count -gt 0) { Write-Host ("FAILED with {0} error(s)" -f $errors.Count); exit 1 }
Write-Host 'OK'
exit 0
