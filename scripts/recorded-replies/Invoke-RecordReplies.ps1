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
.PARAMETER VmMediaFile     A real public film inside the VM (Sintel, which is already in the VM, for example).
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
    [string] $VmMediaFile = 'C:\Media\Movies\Sintel (2010)\Sintel (2010) [WEB 1080p].mkv',
    [int] $NewznabPort = 1180,
    [int] $NntpPort = 1119,
    [int] $SabnzbdPort = 8080, [string] $SabnzbdConfigPath,
    [int] $NzbgetPort = 6789,
    [int] $QbittorrentPort = 8081,
    [int] $TransmissionPort = 9091,
    [int] $DelugePort = 8112,
    [string] $WeirUrl = 'http://127.0.0.1:9347', [string] $WeirVersion = 'rc.13', [string] $WeirSource = 'deluno',
    # Records only these extra states and changes nothing else: 'controls' (resume of a known paused job, a pause of an unknown id, a wrong login, for the
    # apps in -Apps), 'nzbget-config' (NZBGet paused over errors in its configuration, #1257) and 'weir-states' (the Weir states Record-Weir.ps1 does not reach).
    # No MISSING.md is rewritten in this mode: the lines it prints are added to that file by hand.
    [ValidateSet('controls', 'nzbget-config', 'weir-states')] [string[]] $Extra,
    [switch] $NoSwarm,   # skip the two-client transfers (about 20 of the 35 minutes)
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
    DelunoVersion = $DelunoVersion; PerState = $PerState; NoSwarm = [bool]$NoSwarm
}
# Script text the VM needs (the torrent-making scripts, the local tracker, the Usenet stub as it is in this repository). Sent as
# plain values beside the small JSON of settings, not inside it: Windows PowerShell 5.1 serialises that much text far too slowly.
$texts = @{
    FeedFiles = [hashtable[]]@(@('Add-Release.ps1', 'Make-Torrent.ps1') | ForEach-Object { @{ name = [string]$_; text = [string](Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts\golden-path\torznab-feed\$_")) } })
    TrackerScript = [string](Get-Content -Raw -LiteralPath (Join-Path $here 'Start-LocalTracker.ps1'))
    StubFiles = [hashtable[]]@(@('UsenetStub.Lib.ps1', 'New-UsenetRelease.ps1', 'Start-NntpStub.ps1', 'Start-NewznabStub.ps1') | ForEach-Object { @{ name = [string]$_; text = [string](Get-Content -Raw -LiteralPath (Join-Path $repoRoot "scripts\golden-path\usenet-stub\$_")) } })
}


# The in-VM program: the helper library, the recorders, then the calls. It prints a JSON document on its last line.
$libs = 'Record.Lib.ps1', 'Record.Secrets.ps1', 'Record-Usenet.ps1', 'Record-Torrents.ps1', 'Record-Weir.ps1', 'Record-WeirStates.ps1', 'Record-Controls.ps1', 'Collect-DelunoSide.ps1' |
    ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $here $_) }
$calls = [System.Text.StringBuilder]::new()
$map = [ordered]@{ sabnzbd = 'Invoke-RecordSabnzbd'; nzbget = 'Invoke-RecordNzbget'; weir = 'Invoke-RecordWeir' }
foreach ($app in $map.Keys) {
    if (-not $Extra -and $Apps -contains $app) {
        $null = $calls.AppendLine("Write-RunLog 'start $app'; try { $($map[$app]) `$p } catch { Add-Missing ('$app stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }")
    }
}
if (-not $Extra -and ($Apps -contains 'sabnzbd' -or $Apps -contains 'nzbget')) { $null = $calls.AppendLine('try { Remove-UsenetStubReleases } catch { }') }
$torrentApps = @($Apps | Where-Object { $_ -in 'qbittorrent', 'transmission', 'deluge' })
if ($Extra -contains 'controls') {
    if ($Apps -contains 'sabnzbd') { $null = $calls.AppendLine("Write-RunLog 'start sabnzbd controls'; try { Invoke-RecordSabnzbdControls `$p } catch { Add-Missing ('sabnzbd controls stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }") }
    if ($Apps -contains 'nzbget') { $null = $calls.AppendLine("Write-RunLog 'start nzbget controls'; try { Invoke-RecordNzbgetControls `$p } catch { Add-Missing ('nzbget controls stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }") }
    if ($torrentApps.Count -gt 0) { $null = $calls.AppendLine("Write-RunLog 'start torrent controls'; try { Invoke-RecordTorrentControls `$p @('$($torrentApps -join "','")') } catch { Add-Missing ('torrent controls stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }") }
}
if ($Extra -contains 'nzbget-config') { $null = $calls.AppendLine("Write-RunLog 'start nzbget config error'; try { Invoke-RecordNzbgetRefusedLogin `$p; Invoke-RecordNzbgetConfigError `$p } catch { Add-Missing ('nzbget config error stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }") }
if ($Extra -contains 'weir-states') { $null = $calls.AppendLine("Write-RunLog 'start weir states'; try { Invoke-RecordWeirStates `$p } catch { Add-Missing ('weir states stopped: ' + `$_.Exception.Message + ' at line ' + `$_.InvocationInfo.ScriptLineNumber) }") }
if (-not $Extra -and $torrentApps.Count -gt 0) {
    $null = $calls.AppendLine("Write-RunLog 'start torrent clients'; try { Invoke-RecordTorrentClients `$p @('$($torrentApps -join "','")') } catch { Add-Missing ('torrent clients stopped: ' + `$_.Exception.Message) }")
}
if ($DelunoSide) { $null = $calls.AppendLine('try { Invoke-CollectDelunoSide $p } catch { Add-Missing (''deluno side stopped: '' + $_.Exception.Message) }') }
# A script block body cannot start with param() after other statements, so the parameters arrive as one JSON argument.
$program = ($libs -join "`n") + "`n`$p = `$args[0] | ConvertFrom-Json`nforeach (`$k in `$args[1].Keys) { `$p | Add-Member -NotePropertyName `$k -NotePropertyValue `$args[1][`$k] -Force }`n" + $calls.ToString() +
    "`n[pscustomobject]@{ recordings = @(`$script:Recordings); missing = @(`$script:Missing); logs = `$script:Logs; computerName = `$env:COMPUTERNAME; userName = `$env:USERNAME } | ConvertTo-Json -Depth 30 -Compress`n"

$rigCred = Import-Clixml $RigCredentialFile
$vmCred = Import-Clixml $VmCredentialFile
$rig = New-PSSession -ComputerName $RigHost -Credential $rigCred
try {
    if ($env:RECORD_DUMP) {   # for debugging the capture itself: write what would be sent to the VM and stop
        Set-Content -LiteralPath (Join-Path $env:RECORD_DUMP 'program.ps1') -Value $program -Encoding utf8
        Set-Content -LiteralPath (Join-Path $env:RECORD_DUMP 'params.json') -Value ($vmParams | ConvertTo-Json -Depth 4 -Compress) -Encoding utf8
        return
    }
    Write-Host "Recording $($Apps -join ', ') on $VmName. This takes about 30 to 40 minutes: the film downloads through the Usenet clients."
    $raw = Invoke-Command -Session $rig -ArgumentList $program, ($vmParams | ConvertTo-Json -Depth 4 -Compress), $texts, $VmName, $vmCred -ScriptBlock {
        param($text, $json, $moreText, $name, $cred)
        $sb = [scriptblock]::Create($text)
        Invoke-Command -VMName $name -Credential $cred -ArgumentList $json, $moreText -ScriptBlock $sb
    }
} finally { Remove-PSSession $rig -ErrorAction SilentlyContinue }

$resultText = @($raw)[-1]
$result = $resultText | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null

# Machine and user names to remove on top of the scrubber's own patterns: the VM's name and its sign-in name.
$names = @($VmName, $vmCred.UserName.Split('\')[-1], $result.computerName, $result.userName) | Where-Object { $_ } | Select-Object -Unique
$nameArgs = $names | ForEach-Object { '--name'; $_ }

$written = 0
$writtenFiles = [System.Collections.Generic.List[string]]::new()
foreach ($recording in $result.recordings) {
    $folder = Join-Path $OutDirectory (Join-Path $recording.app $recording.version)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $file = Join-Path $folder ($recording.state + '.json')
    # UTF-8 without a byte-order mark: Set-Content -Encoding utf8 writes one in Windows PowerShell 5.1, which the JSON readers refuse.
    [IO.File]::WriteAllText($file, (($recording | ConvertTo-Json -Depth 30) + "`n"), (New-Object Text.UTF8Encoding($false)))
    $written++
    $writtenFiles.Add($file)
}
$logNames = @($result.logs.PSObject.Properties.Name | Where-Object { $_ })
if ($logNames.Count -gt 0) {
    $weirVersion = @($result.recordings | Where-Object { $_.app -eq 'weir' } | Select-Object -First 1).version
    if (-not $weirVersion) { $weirVersion = $WeirVersion }
    $logFolder = Join-Path $OutDirectory (Join-Path 'weir' (Join-Path $weirVersion 'logs'))
    New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
    foreach ($name in $logNames) {
        $logFile = Join-Path $logFolder $name
        [IO.File]::WriteAllText($logFile, ([string]$result.logs.$name + "`n"), (New-Object Text.UTF8Encoding($false)))
        $writtenFiles.Add($logFile)
    }
}

# Scrub, tidy (JSON text bodies become JSON), then check nothing private is left. The check fails the run, loudly.
# Only the files this run wrote: the recordings made earlier are not rewritten.
& node (Join-Path $here 'scrub.mjs') @($writtenFiles) @nameArgs
if ($LASTEXITCODE -ne 0) { throw 'scrub.mjs failed' }
& node (Join-Path $here 'scrub.mjs') @($writtenFiles) @nameArgs --check
if ($LASTEXITCODE -ne 0) { throw 'A recording still carries something private after scrubbing. Nothing should be committed; see the list above.' }

if ($Extra) {
    Write-Host "Wrote $written recordings to $OutDirectory (extra states only; MISSING.md untouched)."
    foreach ($line in @($result.missing | Sort-Object -Unique)) { Write-Host "MISSING $line" -ForegroundColor Yellow }
    return
}
$missingFile = Join-Path $OutDirectory 'MISSING.md'
$lines = @('# Recordings the golden VM could not produce', '', "Written by scripts/recorded-replies/Invoke-RecordReplies.ps1 on $((Get-Date).ToString('yyyy-MM-dd')). Each line says what is needed to record it.", '')
$lines += @($result.missing | Sort-Object -Unique | ForEach-Object { "- $_" })
# Apps the golden VM does not have, listed every run until they are installed there.
$lines += @(
    '- weir failed with failureClass not_taken (Weir #956, Deluno #1250): Weir answers it when a second connection of the same kind hands off a file whose processing pass belongs to the first; the VM has one Deluno connection to Weir, so it cannot be produced there. It needs a second processor connection in Deluno pointing at the same Weir and watched folder',
    '- rtorrent: not installed in the VM; install it there (or on a machine the script can reach) and add a recorder',
    '- utorrent: not installed in the VM; same',
    '- emby, jellyfin, plex: not installed in the VM; their library, item and playback replies need a server with real media',
    '- torznab and newznab feeds: only the golden path stubs exist in the VM, and a stub is not a real indexer; a real indexer''s caps and search replies need its API key'
)
[IO.File]::WriteAllText($missingFile, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

Write-Host "Wrote $written recordings to $OutDirectory; $(@($result.missing).Count) states are missing (see MISSING.md)."
