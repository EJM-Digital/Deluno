<#
.SYNOPSIS
    Installs one real app in the golden VM and records its replies into tests/fixtures/recorded (#1237). One command per app.

.DESCRIPTION
    The apps Invoke-RecordReplies.ps1 cannot record because the VM does not have them: uTorrent, rTorrent, a real Torznab indexer
    (Jackett), a real Newznab server (NZBHydra2), Jellyfin, Emby, Plex Media Server, Seerr and FlareSolverr.

    It reaches the VM exactly as Invoke-RecordReplies.ps1 does (PowerShell remoting to the Hyper-V host, then PowerShell Direct
    into the VM, with the same two DPAPI credential files), sends the in-VM program (Record.Lib.ps1, Record.Secrets.ps1,
    Install.Lib.ps1, the app's own Record-*.ps1), and writes what comes back to tests/fixtures/recorded/<app>/<version>/<state>.json,
    scrubbed by scrub.mjs. Each app downloads only from its own release source, checks the checksum its vendor publishes (or the
    signature, or says there is none), installs, starts on 127.0.0.1 and is driven into each state Deluno's tests need.
    Secrets (API keys, logins) are made inside the VM and never printed or passed on a command line.

    The VM must be running and IDLE. This script restores no checkpoint. Restore the 'clean' checkpoint first when the run should start
    from a clean machine. Run `-App jellyfin` before `-App seerr` (Seerr signs in with the Jellyfin administrator made in the VM).
    rTorrent turns on the Windows Subsystem for Linux, which needs one restart: this script restarts the VM and runs the app again by itself.

    The lines about what could not be recorded are printed (MISSING ...); they are added to tests/fixtures/recorded/MISSING.md by hand.

.PARAMETER RigHost      The Hyper-V host that holds the VM. Never committed; pass it on the command line.
.PARAMETER App          utorrent | rtorrent | jackett (Torznab) | nzbhydra2 (Newznab) | jellyfin | emby | plex | seerr | flaresolverr
.PARAMETER VmMediaFile  A real public film inside the VM (the suite's base video).
.EXAMPLE
    ./scripts/recorded-replies/Invoke-RecordRealApp.ps1 -RigHost <host> -App jackett
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RigHost,
    [Parameter(Mandatory)] [ValidateSet('utorrent', 'rtorrent', 'jackett', 'nzbhydra2', 'jellyfin', 'emby', 'plex', 'seerr', 'flaresolverr')] [string] $App,
    [string] $VmName = 'Deluno-GoldenPath',
    [string] $RigCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-rig-admin.xml'),
    [string] $VmCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-goldenvm-admin.xml'),
    [string] $OutDirectory,
    [string] $VmMediaFile = 'C:\Media\Movies\Sintel (2010)\Sintel (2010) [WEB 1080p].mkv',
    [int] $UTorrentPort = 8083, [int] $RtorrentPort = 8084, [int] $JackettPort = 9117, [int] $NzbhydraPort = 5076,
    [int] $JellyfinPort = 8096, [int] $EmbyPort = 8097, [int] $SeerrPort = 5055, [int] $FlareSolverrPort = 8191
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot 'tests\fixtures\recorded' }
$here = $PSScriptRoot

$vmParams = [ordered]@{
    VmMediaFile = $VmMediaFile; UTorrentPort = $UTorrentPort; RtorrentPort = $RtorrentPort; JackettPort = $JackettPort; NzbhydraPort = $NzbhydraPort
    JellyfinPort = $JellyfinPort; EmbyPort = $EmbyPort; SeerrPort = $SeerrPort; FlareSolverrPort = $FlareSolverrPort
}
$texts = @{
    FeedFiles = [hashtable[]]@(@('Add-Release.ps1', 'Make-Torrent.ps1') | ForEach-Object { @{ name = [string]$_; text = [string](Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts\golden-path\torznab-feed\$_")) } })
}

$files = @{
    utorrent = @('Record-Torrents.ps1', 'Record-UTorrent.ps1'); rtorrent = @('Record-Torrents.ps1', 'Record-Rtorrent.ps1')
    jackett = @('Record-IndexerFeeds.ps1'); nzbhydra2 = @('Record-IndexerFeeds.ps1')
    jellyfin = @('Record-MediaServers.ps1'); emby = @('Record-MediaServers.ps1'); plex = @('Record-MediaServers.ps1')
    seerr = @('Record-MediaServers.ps1', 'Record-Seerr.ps1'); flaresolverr = @('Record-FlareSolverr.ps1')
}
$call = @{
    utorrent = 'Invoke-RecordUTorrent $p'; rtorrent = 'Invoke-RecordRtorrent $p'; jackett = 'Invoke-RecordJackett $p'; nzbhydra2 = 'Invoke-RecordNzbhydra $p'
    jellyfin = 'Invoke-RecordEmbyFamily $p ''jellyfin'''; emby = 'Invoke-RecordEmbyFamily $p ''emby'''; plex = 'Invoke-RecordPlex $p'
    seerr = 'Invoke-RecordSeerr $p'; flaresolverr = 'Invoke-RecordFlareSolverr $p'
}
$libs = @('Record.Lib.ps1', 'Record.Secrets.ps1', 'Install.Lib.ps1') + $files[$App] | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $here $_) }
$program = ($libs -join "`n") + "`n`$p = `$args[0] | ConvertFrom-Json`nforeach (`$k in `$args[1].Keys) { `$p | Add-Member -NotePropertyName `$k -NotePropertyValue `$args[1][`$k] -Force }`n" +
    "Write-RunLog 'start $App'; try { $($call[$App]) } catch { Add-Missing ('$App stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }`n" +
    "[pscustomobject]@{ recordings = @(`$script:Recordings); missing = @(`$script:Missing); computerName = `$env:COMPUTERNAME; userName = `$env:USERNAME } | ConvertTo-Json -Depth 30 -Compress`n"

if ($env:RECORD_DUMP) {   # for debugging the capture itself: write what would be sent to the VM and stop
    Set-Content -LiteralPath (Join-Path $env:RECORD_DUMP 'program.ps1') -Value $program -Encoding utf8
    Set-Content -LiteralPath (Join-Path $env:RECORD_DUMP 'params.json') -Value ($vmParams | ConvertTo-Json -Depth 4 -Compress) -Encoding utf8
    return
}

$rigCred = Import-Clixml $RigCredentialFile
$vmCred = Import-Clixml $VmCredentialFile

function Invoke-InVmProgram {
    $rig = New-PSSession -ComputerName $RigHost -Credential $rigCred
    try {
        Invoke-Command -Session $rig -ArgumentList $program, ($vmParams | ConvertTo-Json -Depth 4 -Compress), $texts, $VmName, $vmCred -ScriptBlock {
            param($text, $json, $moreText, $name, $cred)
            $sb = [scriptblock]::Create($text)
            Invoke-Command -VMName $name -Credential $cred -ArgumentList $json, $moreText -ScriptBlock $sb
        }
    } finally { Remove-PSSession $rig -ErrorAction SilentlyContinue }
}

Write-Host "Recording $App on $VmName. It downloads and installs the app first; expect 5 to 40 minutes."
$result = (@(Invoke-InVmProgram)[-1]) | ConvertFrom-Json

# rTorrent: the Windows Subsystem for Linux was just turned on and needs a restart. Restart the VM, wait for it, run the same command again.
if (@($result.missing) -match '^REBOOT:') {
    Write-Host 'The VM needs a restart (Windows Subsystem for Linux was turned on). Restarting it and running again.'
    $rig = New-PSSession -ComputerName $RigHost -Credential $rigCred
    try {
        Invoke-Command -Session $rig -ArgumentList $VmName, $vmCred -ScriptBlock { param($name, $cred) Invoke-Command -VMName $name -Credential $cred -ScriptBlock { Restart-Computer -Force } }
        Start-Sleep -Seconds 60
        $deadline = (Get-Date).AddMinutes(10)
        do {
            Start-Sleep -Seconds 10
            $up = try { Invoke-Command -Session $rig -ArgumentList $VmName, $vmCred -ScriptBlock { param($name, $cred) Invoke-Command -VMName $name -Credential $cred -ScriptBlock { 'up' } } } catch { $null }
        } until ($up -eq 'up' -or (Get-Date) -gt $deadline)
        if ($up -ne 'up') { throw 'The VM did not come back within 10 minutes of the restart.' }
    } finally { Remove-PSSession $rig -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 20
    $result = (@(Invoke-InVmProgram)[-1]) | ConvertFrom-Json
}

New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$names = @($VmName, $vmCred.UserName.Split('\')[-1], $result.computerName, $result.userName) | Where-Object { $_ } | Select-Object -Unique
$nameArgs = $names | ForEach-Object { '--name'; $_ }
$writtenFiles = [System.Collections.Generic.List[string]]::new()
foreach ($recording in $result.recordings) {
    $folder = Join-Path $OutDirectory (Join-Path $recording.app $recording.version)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $file = Join-Path $folder ($recording.state + '.json')
    [IO.File]::WriteAllText($file, (($recording | ConvertTo-Json -Depth 30) + "`n"), (New-Object Text.UTF8Encoding($false)))
    $writtenFiles.Add($file)
}
if ($writtenFiles.Count -gt 0) {
    & node (Join-Path $here 'scrub.mjs') @($writtenFiles) @nameArgs
    if ($LASTEXITCODE -ne 0) { throw 'scrub.mjs failed' }
    & node (Join-Path $here 'scrub.mjs') @($writtenFiles) @nameArgs --check
    if ($LASTEXITCODE -ne 0) { throw 'A recording still carries something private after scrubbing. Nothing should be committed; see the list above.' }
}
Write-Host "Wrote $($writtenFiles.Count) recordings for $App to $OutDirectory."
foreach ($line in @($result.missing | Sort-Object -Unique)) { Write-Host "MISSING $line" -ForegroundColor Yellow }
