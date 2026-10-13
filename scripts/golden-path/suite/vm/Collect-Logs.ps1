<#
.SYNOPSIS
    Gathers, inside the VM, everything a run must keep: Deluno's and Weir's full logs, the request lines for /processors/events,
    the tray watcher's log, the suite's own log, results and evidence, into one zip (golden path scenario suite).

.DESCRIPTION
    Files in use are copied with a shared read, so a running Deluno or Weir does not block it. Throwaway logins are removed from
    every text file that is kept (the suite's secrets.json itself is never included). Output: <Root>\collect.zip.
    Windows PowerShell 5.1 (Compress-Archive).
#>
[CmdletBinding()]
param([string] $Root = 'C:\golden\suite', [int] $Hours = 12)
$ErrorActionPreference = 'Continue'
$out = Join-Path $Root 'collect'
Remove-Item -LiteralPath $out, (Join-Path $Root 'collect.zip') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $out 'deluno'), (Join-Path $out 'weir'), (Join-Path $out 'suite') | Out-Null

$secrets = @()
$secretFile = Join-Path $Root 'secrets.json'
if (Test-Path $secretFile) { $o = Get-Content -Raw $secretFile | ConvertFrom-Json; $secrets = @($o.PSObject.Properties | ForEach-Object { [string]$_.Value } | Where-Object { $_.Length -ge 6 }) }

function Copy-Shared {
    param([string] $From, [string] $To)
    try {
        $in = [IO.File]::Open($From, 'Open', 'Read', 'ReadWrite,Delete')
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path $To) | Out-Null
            $outFs = [IO.File]::Create($To)
            try { $in.CopyTo($outFs) } finally { $outFs.Dispose() }
        } finally { $in.Dispose() }
    } catch { Add-Content -Path (Join-Path $out 'collect-problems.txt') -Value "$From : $($_.Exception.Message)" }
}

function Copy-Logs {
    param([string[]] $Roots, [string] $Dest, [string] $Prefix)
    $cutoff = (Get-Date).AddHours(-$Hours)
    foreach ($r in $Roots | Where-Object { $_ -and (Test-Path $_) }) {
        Get-ChildItem -Path $r -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.log', '.jsonl', '.txt' -and $_.LastWriteTime -gt $cutoff -and $_.Length -lt 500MB -and $_.FullName -notmatch '\\(components|current|packages|node_modules|cache|Cache)\\' } |
            ForEach-Object {
                $rel = $_.FullName.Substring($r.Length).TrimStart('\') -replace '[\\:]', '_'
                Copy-Shared -From $_.FullName -To (Join-Path $Dest ($Prefix + '_' + $rel))
            }
    }
}

# Deluno's logs: its logs folder under the install and under the data root, wherever the data root is (per user or per machine).
$delunoRoots = @((Join-Path $env:LOCALAPPDATA 'Deluno\logs'), (Join-Path $env:LOCALAPPDATA 'DelunoData\logs'), (Join-Path $env:ProgramData 'Deluno\logs'), (Join-Path $env:ProgramData 'DelunoData\logs'), (Join-Path $env:APPDATA 'Deluno\logs'))
Copy-Logs -Roots $delunoRoots -Dest (Join-Path $out 'deluno') -Prefix 'deluno'
# Weir's logs, wherever it keeps them.
Copy-Logs -Roots @((Join-Path $env:LOCALAPPDATA 'Weir'), (Join-Path $env:ProgramData 'Weir'), (Join-Path $env:APPDATA 'Weir')) -Dest (Join-Path $out 'weir') -Prefix 'weir'
# The suite's own record.
foreach ($name in 'logs', 'results') { if (Test-Path (Join-Path $Root $name)) { Copy-Item -Path (Join-Path $Root $name) -Destination (Join-Path $out 'suite') -Recurse -Force -ErrorAction SilentlyContinue } }
Copy-Item -Path 'C:\golden\picker.log' -Destination (Join-Path $out 'suite\picker.log') -ErrorAction SilentlyContinue

# The request lines for /processors/events, from every Deluno log kept. Deluno writes one line for each answer it gives to
# Weir's /processors/events call (#1242); routine web-request lines are Warning and above only (#1152), so they alone often leave
# nothing. The match is wide on purpose (the route written with / or \ or as "processors-events", or "processor events"), and what
# was searched is written next to the lines, so an empty file says why it is empty.
$events = New-Object System.Collections.ArrayList
$eventsPattern = '(?i)processors?[\\/_ -]+events'
$delunoKept = @(Get-ChildItem -Path (Join-Path $out 'deluno') -File -ErrorAction SilentlyContinue)
$scanned = 0
foreach ($f in $delunoKept) {
    try {
        foreach ($line in [IO.File]::ReadLines($f.FullName)) { $scanned++; if ($line -match $eventsPattern) { [void]$events.Add($line) } }
    } catch { }
}
Set-Content -Path (Join-Path $out 'processors-events-requests.log') -Value $events -Encoding utf8
$diag = New-Object System.Collections.ArrayList
[void]$diag.Add("Deluno log files kept: $($delunoKept.Count) ($(($delunoKept | Select-Object -First 8 | ForEach-Object { $_.Name }) -join ', '))")
[void]$diag.Add("Deluno log lines read: $scanned; lines naming the /processors/events route: $($events.Count)")
foreach ($r in $delunoRoots) { [void]$diag.Add("Looked in: $r ($(if (Test-Path $r) { 'exists' } else { 'not there' }))") }
$jsonl = @($delunoKept | Where-Object { $_.Extension -eq '.jsonl' })
if ($delunoKept.Count -eq 0) { [void]$diag.Add('MISSING: no Deluno log file was found in any folder above, so there is nothing to search. Deluno keeps its log as deluno-<date>.jsonl under its data folder.') }
elseif ($jsonl.Count -eq 0) { [void]$diag.Add('MISSING: Deluno log files were kept but none is the structured log (deluno-<date>.jsonl).') }
elseif ($events.Count -eq 0) { [void]$diag.Add('MISSING: Deluno''s log has lines, but none names /processors/events. Either Weir did not call it in this window, or this Deluno build writes no line for its answers.') }
Set-Content -Path (Join-Path $out 'processors-events-requests.diag.txt') -Value $diag -Encoding utf8

# No throwaway login in anything kept.
if ($secrets.Count -gt 0) {
    Get-ChildItem -Path $out -Recurse -File | Where-Object { $_.Length -lt 200MB } | ForEach-Object {
        try {
            $text = [IO.File]::ReadAllText($_.FullName)
            $changed = $false
            foreach ($s in $secrets) { if ($text.Contains($s)) { $text = $text.Replace($s, '***'); $changed = $true } }
            if ($changed) { [IO.File]::WriteAllText($_.FullName, $text) }
        } catch { }
    }
}
Compress-Archive -Path (Join-Path $out '*') -DestinationPath (Join-Path $Root 'collect.zip') -Force
$files = @(Get-ChildItem -Path $out -Recurse -File)
"collected $($files.Count) files, $([math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)) MB, $($events.Count) /processors/events request lines; $($delunoKept.Count) Deluno log files searched"
