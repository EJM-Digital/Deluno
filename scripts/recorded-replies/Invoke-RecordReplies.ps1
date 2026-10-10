<#
.SYNOPSIS
    Re-records the real replies of every download client and Weir on the golden VM into tests/fixtures/recorded (#1237).

.DESCRIPTION
    One entry point. It reaches the VM the way scripts/golden-path/Invoke-GoldenPath.ps1 does (PowerShell remoting to the
    Hyper-V host, then PowerShell Direct into the VM, using the same two DPAPI credential files) and drives each app
    through its own API into every state it can be put in there: SABnzbd, NZBGet, qBittorrent, Transmission and Deluge
    (downloads from the VM's local Torznab feed and Usenet stub, scripts/golden-path), and Weir through its intake API.
    The VM must already be running with the apps installed and the stubs up (Invoke-GoldenPath.ps1 -Interactive -UsenetStub),
    and it must be IDLE: this script does not restore a checkpoint, so it never interrupts a golden run, and it must not
    be run during one.

    Each recording lands in tests/fixtures/recorded/<app>/<version>/<state>.json, where <version> is what the app itself
    reports. A new version of an app therefore gets its own folder from the same single run, and the old one is kept.
    Everything is passed through scrub.mjs before it is written (API keys, passwords, cookies, LAN addresses, machine and
    user names removed). States the VM cannot produce are listed in tests/fixtures/recorded/MISSING.md with what is needed.

    No login is ever a parameter, printed or sent back. Inside the VM the script reads qBittorrent's generated login
    (and Transmission's, if Deluno saved one) from Deluno's component-credentials file, Weir's webhook secret from Deluno's
    connection to Weir, and SABnzbd's API key from sabnzbd.ini; NZBGet and Deluge keep their shipped logins. Every value
    read is also removed from the recordings by exact match, behind scrub.mjs (Record.Secrets.ps1).

    Weir: records as "weir" (and keeps its log files beside the recordings). Use a Deluno-linked workflow, or a Weir-only
    one with "remove original after success" OFF, on the VM's own folders: hand-offs name copies of the film, never an original.

    Deluno's side (-DelunoSide): turns the proxy files left by Start-RecordingProxy.ps1 into tests/fixtures/recorded/deluno.

.PARAMETER RigHost         The Hyper-V host that holds the VM. Never committed; pass it on the command line.
.PARAMETER VmMediaFile     A real public film inside the VM (the golden path's Big Buck Bunny, for example).
.PARAMETER Apps            Which to record. Default: all of sabnzbd, nzbget, qbittorrent, transmission, deluge, weir.
.EXAMPLE
    ./scripts/recorded-replies/Invoke-RecordReplies.ps1 -RigHost <host> -VmMediaFile C:\golden\media\bunny.mkv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RigHost,
    [string] $VmName = 'Deluno-GoldenPath',
    [string] $RigCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-rig-admin.xml'),
    [string] $VmCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-goldenvm-admin.xml'),
    [string] $OutDirectory,
    [ValidateSet('sabnzbd', 'nzbget', 'qbittorrent', 'transmission', 'deluge', 'weir')] [string[]] $Apps = @('sabnzbd', 'nzbget', 'qbittorrent', 'transmission', 'deluge', 'weir'),
    [string] $VmMediaFile = 'C:\golden\media\film.mkv',
    [int] $NewznabPort = 1180,
    [int] $NntpPort = 1119,
    [int] $SabnzbdPort = 8080, [string] $SabnzbdConfigPath,
    [int] $NzbgetPort = 6789,
    [int] $QbittorrentPort = 8081,
    [int] $TransmissionPort = 9091,
    [int] $DelugePort = 8112,
    [string] $WeirUrl = 'http://127.0.0.1:9347', [string] $WeirVersion = 'rc.13', [string] $WeirSource = 'deluno',
    [switch] $DelunoSide, [string] $DelunoVersion = 'current', [int] $PerState = 3
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot 'tests\fixtures\recorded' }
$here = $PSScriptRoot

$vmParams = [ordered]@{
    VmMediaFile = $VmMediaFile; NewznabPort = $NewznabPort; NntpPort = $NntpPort
    SabnzbdPort = $SabnzbdPort; SabnzbdConfigPath = $SabnzbdConfigPath
    NzbgetPort = $NzbgetPort; QbittorrentPort = $QbittorrentPort; TransmissionPort = $TransmissionPort; DelugePort = $DelugePort
    WeirUrl = $WeirUrl; WeirVersion = $WeirVersion; WeirSource = $WeirSource
    # The Usenet stub as it is in this repository (hang file, folder jobs); the VM's copy is brought up to it.
    TrackerScript = (Get-Content -Raw -LiteralPath (Join-Path $here 'Start-LocalTracker.ps1'))
    StubFiles = @('UsenetStub.Lib.ps1', 'New-UsenetRelease.ps1', 'Start-NntpStub.ps1', 'Start-NewznabStub.ps1') | ForEach-Object { [ordered]@{ name = $_; text = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts\golden-path\usenet-stub\$_")) } }
    DelunoVersion = $DelunoVersion; PerState = $PerState
}

# The in-VM program: the helper library, the recorders, then the calls. It prints a JSON document on its last line.
$libs = 'Record.Lib.ps1', 'Record.Secrets.ps1', 'Record-Usenet.ps1', 'Record-Torrents.ps1', 'Record-Weir.ps1', 'Collect-DelunoSide.ps1' |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $here $_) }
$calls = [System.Text.StringBuilder]::new()
$map = [ordered]@{ sabnzbd = 'Invoke-RecordSabnzbd'; nzbget = 'Invoke-RecordNzbget'; weir = 'Invoke-RecordWeir' }
foreach ($app in $map.Keys) {
    if ($Apps -contains $app) {
        $null = $calls.AppendLine("try { $($map[$app]) `$p } catch { Add-Missing ('$app stopped: ' + `$_.Exception.Message) }")
    }
}
$torrentApps = @($Apps | Where-Object { $_ -in 'qbittorrent', 'transmission', 'deluge' })
if ($torrentApps.Count -gt 0) {
    $null = $calls.AppendLine("try { Invoke-RecordTorrentClients `$p @('$($torrentApps -join "','")') } catch { Add-Missing ('torrent clients stopped: ' + `$_.Exception.Message) }")
}
if ($DelunoSide) { $null = $calls.AppendLine('try { Invoke-CollectDelunoSide $p } catch { Add-Missing (''deluno side stopped: '' + $_.Exception.Message) }') }
# A script block body cannot start with param() after other statements, so the parameters arrive as one JSON argument.
$program = ($libs -join "`n") + "`n`$p = `$args[0] | ConvertFrom-Json`n" + $calls.ToString() +
    "`n[pscustomobject]@{ recordings = @(`$script:Recordings); missing = @(`$script:Missing); logs = `$script:Logs } | ConvertTo-Json -Depth 30 -Compress`n"

$rigCred = Import-Clixml $RigCredentialFile
$vmCred = Import-Clixml $VmCredentialFile
$rig = New-PSSession -ComputerName $RigHost -Credential $rigCred
try {
    Write-Host "Recording $($Apps -join ', ') on $VmName. This takes about 30 to 40 minutes: the film downloads through the Usenet clients."
    $raw = Invoke-Command -Session $rig -ArgumentList $program, ($vmParams | ConvertTo-Json -Compress), $VmName, $vmCred -ScriptBlock {
        param($text, $json, $name, $cred)
        $sb = [scriptblock]::Create($text)
        Invoke-Command -VMName $name -Credential $cred -ArgumentList $json -ScriptBlock $sb
    }
} finally { Remove-PSSession $rig -ErrorAction SilentlyContinue }

$resultText = @($raw)[-1]
$result = $resultText | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null

# Machine and user names to remove on top of the scrubber's own patterns: the VM's name and its sign-in name.
$names = @($VmName, $vmCred.UserName.Split('\')[-1]) | Where-Object { $_ }
$nameArgs = $names | ForEach-Object { '--name'; $_ }

$written = 0
foreach ($recording in $result.recordings) {
    $folder = Join-Path $OutDirectory (Join-Path $recording.app $recording.version)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $file = Join-Path $folder ($recording.state + '.json')
    ($recording | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath $file -Encoding utf8
    $written++
}
foreach ($name in @($result.logs.PSObject.Properties.Name)) {
    $weirVersion = @($result.recordings | Where-Object { $_.app -eq 'weir' } | Select-Object -First 1).version
    if (-not $weirVersion) { $weirVersion = $WeirVersion }
    $logFolder = Join-Path $OutDirectory "weir\$weirVersion\logs"
    New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
    Set-Content -LiteralPath (Join-Path $logFolder $name) -Value $result.logs.$name -Encoding utf8
}

# Scrub, tidy (JSON text bodies become JSON), then check nothing private is left. The check fails the run, loudly.
& node (Join-Path $here 'scrub.mjs') $OutDirectory @nameArgs
if ($LASTEXITCODE -ne 0) { throw 'scrub.mjs failed' }
& node (Join-Path $here 'scrub.mjs') $OutDirectory @nameArgs --check
if ($LASTEXITCODE -ne 0) { throw 'A recording still carries something private after scrubbing. Nothing should be committed; see the list above.' }

$missingFile = Join-Path $OutDirectory 'MISSING.md'
$lines = @('# Recordings the golden VM could not produce', '', "Written by scripts/recorded-replies/Invoke-RecordReplies.ps1 on $((Get-Date).ToString('yyyy-MM-dd')). Each line says what is needed to record it.", '')
$lines += @($result.missing | Sort-Object -Unique | ForEach-Object { "- $_" })
# Apps the golden VM does not have, listed every run until they are installed there.
$lines += @(
    '- rtorrent: not installed in the VM; install it there (or on a machine the script can reach) and add a recorder',
    '- utorrent: not installed in the VM; same',
    '- emby, jellyfin, plex: not installed in the VM; their library, item and playback replies need a server with real media',
    '- torznab and newznab feeds: only the golden path stubs exist in the VM, and a stub is not a real indexer; a real indexer''s caps and search replies need its API key'
)
$lines | Set-Content -LiteralPath $missingFile -Encoding utf8

Write-Host "Wrote $written recordings to $OutDirectory; $(@($result.missing).Count) states are missing (see MISSING.md)."
