# Records qBittorrent, Transmission and Deluge through their own APIs, with torrents from the golden path's local
# Torznab feed (scripts/golden-path/torznab-feed) over a real public film (#1237). Runs INSIDE the VM after Record.Lib.ps1.
#
# No peer exists in the VM, so a torrent over a film that is already in its save folder completes at once (the client
# hashes it), and a torrent with no data stalls. Those two give: stalled, queued, paused, finished (single file),
# finished with a dead tracker, failed (the file removed and the torrent rechecked), removed with and without data,
# removed twice, an unknown id, and a client that is not answering. A download in flight and a folder torrent need a
# seeder and Make-Torrent over a folder, which the VM does not have; they are listed as missing.

function New-WorkFile([string] $App, [string] $Name, [string] $Source) {
    $dir = "C:\golden\recorded\$App"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $target = Join-Path $dir $Name
    Copy-Item -LiteralPath $Source -Destination $target -Force
    $target
}

# Runs the one scenario against an adapter (the client's own calls, below) and saves each state.
function Invoke-TorrentScenario {
    param([string] $App, $A, [string] $MediaFile)
    $v = & $A.Version
    $ver = & $A.VersionOf $v
    if (-not $ver) { Add-Missing "$App : it did not answer its version call"; return }
    Save-State $App $ver 'version' 'The client''s own version call.' @($v)
    Save-State $App $ver 'idle' 'Nothing added: the list of torrents when empty.' @((& $A.List $null))
    $workDir = "C:\golden\recorded\$App\save"
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null

    # stalled, queued, paused: torrents with no data and a tracker that does not exist.
    $d1 = Add-FeedRelease -Title "Recorded.Stalled.One.$App" -DummyMb 700
    $d2 = Add-FeedRelease -Title "Recorded.Stalled.Two.$App" -DummyMb 700
    & $A.Queue $true
    $add1 = & $A.Add $d1 $workDir $false
    $add2 = & $A.Add $d2 $workDir $false
    Start-Sleep -Seconds 8
    Save-State $App $ver 'stalled' 'A torrent with no peers and a tracker that does not exist, started: nothing arrives. (A torrent stuck just short of the end needs a seeder.)' @($add1, (& $A.List $d1.InfoHash))
    Save-State $App $ver 'queued' 'The client limited to one active download, two torrents added: the second waits in the queue.' @($add2, (& $A.List $d2.InfoHash), (& $A.List $null))
    & $A.Queue $false
    $pause = & $A.Pause $d1.InfoHash
    Start-Sleep -Seconds 3
    Save-State $App $ver 'paused' 'The first torrent paused by its hash.' @($pause, (& $A.List $d1.InfoHash))

    $rm1 = & $A.Remove $d1.InfoHash $false
    Save-State $App $ver 'removed-without-data' 'The torrent removed, its data kept.' @($rm1, (& $A.List $d1.InfoHash))
    $rm1b = & $A.Remove $d1.InfoHash $false
    Save-State $App $ver 'removed-twice' 'The same removal sent a second time.' @($rm1b, (& $A.List $d1.InfoHash))
    $none = & $A.Remove ('0' * 40) $false
    Save-State $App $ver 'unknown-id' 'A removal of a hash the client never had.' @($none, (& $A.List ('0' * 40)))
    $null = & $A.Remove $d2.InfoHash $true

    # finished (a single file already in the save folder), then removed with its data.
    $fileA = New-WorkFile $App "Recorded.$App.A.mkv" $MediaFile
    $a = Add-FeedRelease -Title "Recorded.Finished.$App" -File $fileA
    $addA = & $A.Add $a (Split-Path $fileA) $false
    $doneA = Wait-Until -Seconds 180 { & $A.IsComplete (& $A.List $a.InfoHash) }
    if ($doneA) {
        $files = if ($A.Files) { & $A.Files $a.InfoHash } else { $null }
        Save-State $App $ver 'finished-single-file' 'A torrent over one film already in its save folder: the client finds it complete.' @(@($addA, (& $A.List $a.InfoHash), $files) | Where-Object { $_ })
        $rmA = & $A.Remove $a.InfoHash $true
        Save-State $App $ver 'removed-with-data' 'The finished torrent removed together with its data.' @($rmA, (& $A.List $a.InfoHash))
    } else { Add-Missing "$App finished-single-file / removed-with-data: the film torrent was not complete after 3 minutes" }
    Add-Missing "$App finished-folder: needs a torrent over a folder (Make-Torrent.ps1 takes one file)"
    Add-Missing "$App downloading: needs a seeder in the VM so a transfer is in flight"

    # finished although its tracker is dead (a complete torrent must still be handed off), then failed: the file removed and rechecked.
    $fileB = New-WorkFile $App "Recorded.$App.B.mkv" $MediaFile
    $b = Add-FeedRelease -Title "Recorded.DeadTracker.$App" -File $fileB -DeadTracker
    $addB = & $A.Add $b (Split-Path $fileB) $false
    $doneB = Wait-Until -Seconds 180 { & $A.IsComplete (& $A.List $b.InfoHash) }
    if ($doneB) {
        Start-Sleep -Seconds 20
        Save-State $App $ver 'finished-tracker-error' 'A complete torrent whose tracker does not answer.' @($addB, (& $A.List $b.InfoHash))
        Remove-Item -LiteralPath $fileB -Force
        $recheck = & $A.Recheck $b.InfoHash
        Start-Sleep -Seconds 8
        Save-State $App $ver 'failed' 'The torrent''s file removed behind the client''s back, then a recheck: the client reports it cannot find its data.' @($recheck, (& $A.List $b.InfoHash))
    } else { Add-Missing "$App finished-tracker-error / failed: the dead-tracker film torrent was not complete after 3 minutes" }
    $null = & $A.Remove $b.InfoHash $true

    $none = & $A.NotAnswering
    Save-State $App $ver 'not-answering' 'The request sent to a port nothing listens on: the connection is refused.' @($none)
}

# ---------------------------------------------------------------- qBittorrent

function Invoke-RecordQbittorrent($p) {
    $q = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.QbittorrentPort)" -DefaultHeaders @{ Referer = "http://127.0.0.1:$($p.QbittorrentPort)" }
    $login = Send-Recorded -Client $q -Method POST -Path '/api/v2/auth/login' -Form @{ username = $p.QbittorrentUser; password = $p.QbittorrentPassword }
    if ($login.reply.status -ne 200 -or $login.reply.bodyText -notmatch 'Ok') { Add-Missing 'qbittorrent: sign-in failed (pass -QbittorrentUser and -QbittorrentPassword; it is the one client with a generated login)'; return }
    $adapter = @{
        Version = { Send-Recorded -Client $q -Path '/api/v2/app/version' }
        VersionOf = { param($e) $e.reply.bodyText.Trim().TrimStart('v') }
        List = { param($h) Send-Recorded -Client $q -Path ('/api/v2/torrents/info' + $(if ($h) { "?hashes=$h" })) }
        Files = { param($h) Send-Recorded -Client $q -Path "/api/v2/torrents/files?hash=$h" }
        Add = { param($t, $dir, $paused) Send-Recorded -Client $q -Method POST -Path '/api/v2/torrents/add' -Multipart @{ torrents = $t.Bytes; savepath = $dir; paused = "$paused".ToLower(); stopped = "$paused".ToLower() } }
        Pause = { param($h) $r = Send-Recorded -Client $q -Method POST -Path '/api/v2/torrents/stop' -Form @{ hashes = $h }; if ($r.reply.status -eq 404) { $r = Send-Recorded -Client $q -Method POST -Path '/api/v2/torrents/pause' -Form @{ hashes = $h } }; $r }
        Remove = { param($h, $data) Send-Recorded -Client $q -Method POST -Path '/api/v2/torrents/delete' -Form @{ hashes = $h; deleteFiles = "$data".ToLower() } }
        Recheck = { param($h) Send-Recorded -Client $q -Method POST -Path '/api/v2/torrents/recheck' -Form @{ hashes = $h } }
        Queue = { param($on) $null = Send-Recorded -Client $q -Method POST -Path '/api/v2/app/setPreferences' -Form @{ json = (@{ queueing_enabled = $true; max_active_downloads = $(if ($on) { 1 } else { 5 }) } | ConvertTo-Json -Compress) } }
        IsComplete = { param($e) $t = @(Get-ReplyJson $e)[0]; $t -and $t.progress -ge 1 }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path '/api/v2/torrents/info' }
    }
    Save-State qbittorrent ((& $adapter.VersionOf (& $adapter.Version))) 'sign-in' 'POST /api/v2/auth/login: the reply sets a session cookie (removed from the recording).' @($login)
    Invoke-TorrentScenario -App 'qbittorrent' -A $adapter -MediaFile $p.VmMediaFile
}

# ---------------------------------------------------------------- Transmission

function Invoke-RecordTransmission($p) {
    $headers = @{ Value = @{} }
    $cred = if ($p.TransmissionUser) { New-Object pscredential($p.TransmissionUser, (ConvertTo-SecureString $p.TransmissionPassword -AsPlainText -Force)) }
    $t = if ($cred) { New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.TransmissionPort)" -BasicAuth $cred -KeepHeaders @('content-type', 'x-transmission-session-id') } else { New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.TransmissionPort)" -KeepHeaders @('content-type', 'x-transmission-session-id') }
    $script:tid = 0
    $fields = @('id', 'hashString', 'name', 'status', 'percentDone', 'rateDownload', 'rateUpload', 'error', 'errorString', 'downloadDir', 'totalSize', 'leftUntilDone', 'peersConnected', 'peersSendingToUs', 'isStalled', 'isFinished', 'trackerStats', 'files', 'addedDate', 'downloadedEver', 'uploadedEver', 'eta', 'queuePosition', 'labels')
    function Rpc($method, $arguments = @{}) {
        $script:tid++
        Send-Recorded -Client $t -Method POST -Path '/transmission/rpc' -Headers $headers.Value -Json ([ordered]@{ method = $method; arguments = $arguments; tag = $script:tid })
    }
    # The first call is refused with 409 and the session id to send back: that reply is itself recorded.
    $handshake = Rpc 'session-get'
    if ($handshake.reply.status -eq 409) {
        $id = $handshake.reply.headers['x-transmission-session-id']
        if (-not $id) { $m = [regex]::Match([string]$handshake.reply.bodyText, 'X-Transmission-Session-Id:\s*(\S+?)<'); $id = $m.Groups[1].Value }
        $headers.Value = @{ 'X-Transmission-Session-Id' = $id }
    }
    $session = Rpc 'session-get'
    $ver = ([string](Get-ReplyJson $session).arguments.version -split ' ')[0]
    if (-not $ver) { Add-Missing 'transmission: it did not answer session-get (pass -TransmissionUser/-TransmissionPassword if it asks for a login)'; return }
    Save-State transmission $ver 'session-handshake' 'The first call, refused with 409 and the session id to send back (the id is removed from the recording).' @($handshake, $session)
    $adapter = @{
        Version = { Rpc 'session-get' }
        VersionOf = { param($e) ([string](Get-ReplyJson $e).arguments.version -split ' ')[0] }
        List = { param($h) Rpc 'torrent-get' $(if ($h) { @{ fields = $fields; ids = @($h) } } else { @{ fields = $fields } }) }
        Add = { param($tor, $dir, $paused) Rpc 'torrent-add' @{ metainfo = [Convert]::ToBase64String($tor.Bytes); 'download-dir' = $dir; paused = [bool]$paused } }
        Pause = { param($h) Rpc 'torrent-stop' @{ ids = @($h) } }
        Remove = { param($h, $data) Rpc 'torrent-remove' @{ ids = @($h); 'delete-local-data' = [bool]$data } }
        Recheck = { param($h) Rpc 'torrent-verify' @{ ids = @($h) } }
        Queue = { param($on) $null = Rpc 'session-set' @{ 'download-queue-enabled' = $true; 'download-queue-size' = $(if ($on) { 1 } else { 5 }) } }
        IsComplete = { param($e) $x = @((Get-ReplyJson $e).arguments.torrents)[0]; $x -and $x.percentDone -ge 1 }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/transmission/rpc' -Json ([ordered]@{ method = 'session-get'; arguments = @{}; tag = 1 }) }
    }
    Invoke-TorrentScenario -App 'transmission' -A $adapter -MediaFile $p.VmMediaFile
}

# ---------------------------------------------------------------- Deluge

function Invoke-RecordDeluge($p) {
    $d = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.DelugePort)"
    $script:did = 0
    function Rpc($method, $params = @()) { $script:did++; Send-Recorded -Client $d -Method POST -Path '/json' -Json ([ordered]@{ method = $method; params = @($params); id = $script:did }) }
    $login = Rpc 'auth.login' @($p.DelugePassword)
    if ((Get-ReplyJson $login).result -ne $true) { Add-Missing 'deluge: the web login failed (pass -DelugePassword)'; return }
    $connected = Rpc 'web.connected'
    if ((Get-ReplyJson $connected).result -ne $true) {
        $hosts = Rpc 'web.get_hosts'
        $hostId = @((Get-ReplyJson $hosts).result)[0][0]
        $null = Rpc 'web.connect' @($hostId)
    }
    $versionCall = Rpc 'daemon.get_version'
    $ver = [string](Get-ReplyJson $versionCall).result
    if (-not $ver) { Add-Missing 'deluge: it did not answer daemon.get_version'; return }
    Save-State deluge $ver 'sign-in' 'auth.login and web.connected (the session cookie is removed from the recording).' @($login, $connected)
    $keys = @('name', 'state', 'progress', 'total_size', 'total_done', 'save_path', 'download_location', 'move_completed', 'move_completed_path', 'download_payload_rate', 'upload_payload_rate', 'num_seeds', 'num_peers', 'time_added', 'tracker_status', 'message', 'is_finished', 'paused', 'queue', 'files')
    $adapter = @{
        Version = { Rpc 'daemon.get_version' }
        VersionOf = { param($e) [string](Get-ReplyJson $e).result }
        List = { param($h) Rpc 'core.get_torrents_status' @($(if ($h) { @{ id = @($h) } } else { @{} }), $keys) }
        Add = { param($tor, $dir, $paused) Rpc 'core.add_torrent_file' @("$($tor.Title).torrent", [Convert]::ToBase64String($tor.Bytes), @{ download_location = $dir; add_paused = [bool]$paused; move_completed = $false }) }
        Pause = { param($h) Rpc 'core.pause_torrents' @(, @($h)) }
        Remove = { param($h, $data) Rpc 'core.remove_torrent' @($h, [bool]$data) }
        Recheck = { param($h) Rpc 'core.force_recheck' @(, @($h)) }
        Queue = { param($on) $null = Rpc 'core.set_config' @(@{ max_active_downloading = $(if ($on) { 1 } else { 5 }); queue_new_to_top = $false }) }
        IsComplete = { param($e) $x = (Get-ReplyJson $e).result; $x -and ($x.PSObject.Properties | Select-Object -First 1 | ForEach-Object { $x.($_.Name).progress -ge 100 }) }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/json' -Json ([ordered]@{ method = 'web.connected'; params = @(); id = 1 }) }
    }
    Invoke-TorrentScenario -App 'deluge' -A $adapter -MediaFile $p.VmMediaFile
}
