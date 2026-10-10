# Records qBittorrent, Transmission and Deluge through their own APIs, with torrents from the golden path's local
# Torznab feed (scripts/golden-path/torznab-feed) over a real public film (#1237). Runs INSIDE the VM after Record.Lib.ps1.
#
# What the VM gives it, with no internet:
#   * a torrent over a film already in its save folder completes at once (the client hashes it): finished, single file;
#     a torrent over a FOLDER (the film and a small .nfo beside it, Make-Torrent.ps1 -Folder) is the folder job;
#   * a torrent with no data and a dead tracker stalls, queues, pauses and is removed;
#   * a real swarm of two clients: one seeds the film (upload rate-limited), another downloads it over the local tracker
#     (Start-LocalTracker.ps1). That gives a transfer in flight, and, when the seeder is stopped near the end, a
#     transfer stalled just short of the end.

function New-WorkFile([string] $App, [string] $Name, [string] $Source) {
    $dir = "C:\golden\recorded\$App"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $target = Join-Path $dir $Name
    Copy-Item -LiteralPath $Source -Destination $target -Force
    $target
}

# Runs the one-client scenario against an adapter (the client's own calls, below) and saves each state.
function Invoke-TorrentScenario {
    param([string] $App, $A, [string] $MediaFile)
    $v = & $A.Version
    $ver = & $A.VersionOf $v
    if (-not $ver) { Add-Missing "$App : it did not answer its version call"; return }
    $A.Ver = $ver
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
    Save-State $App $ver 'stalled' 'A torrent with no peers and a tracker that does not exist, started: nothing ever arrives.' @($add1, (& $A.List $d1.InfoHash))
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

    # finished folder: the film and a small extra file beside it, in a folder (the #1199 case).
    $folder = "C:\golden\recorded\$App\Recorded.$App.Folder"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item -LiteralPath $MediaFile -Destination (Join-Path $folder "Recorded.$App.Folder.mkv") -Force
    Set-Content -LiteralPath (Join-Path $folder "Recorded.$App.Folder.nfo") -Value ('Golden stub extra file ' * 200) -Encoding ascii
    $fo = Add-FeedRelease -Title "Recorded.Folder.$App" -Folder $folder
    $addFo = & $A.Add $fo (Split-Path $folder) $false
    $doneFo = Wait-Until -Seconds 180 { & $A.IsComplete (& $A.List $fo.InfoHash) }
    if ($doneFo) {
        $filesFo = if ($A.Files) { & $A.Files $fo.InfoHash } else { $null }
        Save-State $App $ver 'finished-folder' 'A torrent over a folder (a film and a small extra file) already in its save folder: complete, two files.' @(@($addFo, (& $A.List $fo.InfoHash), $filesFo) | Where-Object { $_ })
    } else { Add-Missing "$App finished-folder: the folder torrent was not complete after 3 minutes" }
    $null = & $A.Remove $fo.InfoHash $true

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

# One client seeds a real film, rate-limited; another downloads it over the local tracker. Records the downloader in flight,
# then stops the seeder near the end and records it stalled just short of 100%.
function Invoke-TorrentSwarm {
    param([string] $SeedApp, $Seed, [string] $LeechApp, $Leech, [string] $MediaFile, [string] $TrackerUrl)
    $ver = $Leech.Ver
    if (-not $ver -or -not $Seed.Ver) { Add-Missing "$LeechApp downloading / stalled-near-end: $SeedApp or $LeechApp was not recorded, so there is no swarm"; return }
    $base = "C:\golden\recorded\swarm\$SeedApp-$LeechApp"
    $seedDir = "$base\seed"; $leechDir = "$base\leech"
    New-Item -ItemType Directory -Force -Path $seedDir, $leechDir | Out-Null
    $file = Join-Path $seedDir "Recorded.Swarm.$SeedApp.$LeechApp.mkv"
    Copy-Item -LiteralPath $MediaFile -Destination $file -Force
    $t = Add-FeedRelease -Title "Recorded.Swarm.$SeedApp.$LeechApp" -File $file -Tracker $TrackerUrl
    & $Seed.AllowLocalPeers $true
    & $Leech.AllowLocalPeers $true
    try {
        $null = & $Seed.Add $t $seedDir $false
        $seeded = Wait-Until -Seconds 180 { & $Seed.IsComplete (& $Seed.List $t.InfoHash) }
        if (-not $seeded) { Add-Missing "$LeechApp downloading / stalled-near-end: $SeedApp did not finish hashing the film to seed it"; return }
        # Slow enough that the transfer takes about two minutes, so there is time to see it in flight and to stop it near the end.
        $null = & $Seed.SetUploadLimit $t.InfoHash ([long][math]::Max(65536, $t.Size / 120))
        $null = & $Leech.Add $t $leechDir $false
        $flying = Wait-Until -Seconds 180 -EverySeconds 1 { $p = & $Leech.Progress (& $Leech.List $t.InfoHash); if ($p -gt 0.03 -and $p -lt 0.9) { $p } }
        if ($flying) { Save-State $LeechApp $ver 'downloading' "A real transfer in flight: $SeedApp seeds the film (upload limited) and $LeechApp downloads it over the local tracker." @((& $Leech.List $t.InfoHash), (& $Leech.List $t.InfoHash)) }
        else { Add-Missing "$LeechApp downloading: no peer connection formed between $SeedApp and $LeechApp (the clients may refuse peers on one address)" }
        $near = Wait-Until -Seconds 400 -EverySeconds 1 { $p = & $Leech.Progress (& $Leech.List $t.InfoHash); if ($p -ge 0.97) { $p } }
        if ($near) {
            $null = & $Seed.Pause $t.InfoHash
            Start-Sleep -Seconds 30
            $left = & $Leech.Progress (& $Leech.List $t.InfoHash)
            if ($left -lt 1) { Save-State $LeechApp $ver 'stalled-near-end' "The seeder stopped when the download was past 97%: it sticks short of 100% with no peer and never finishes." @((& $Leech.List $t.InfoHash), (& $Leech.List $t.InfoHash)) }
            else { Add-Missing "$LeechApp stalled-near-end: the download finished before $SeedApp could be stopped" }
        } elseif ($flying) { Add-Missing "$LeechApp stalled-near-end: the download never got past 97% in 400 s" }
    } finally {
        $null = & $Leech.Remove $t.InfoHash $true
        $null = & $Seed.Remove $t.InfoHash $true
        & $Seed.AllowLocalPeers $false
        & $Leech.AllowLocalPeers $false
    }
}

# ---------------------------------------------------------------- adapters: each client's own calls

function New-QbitAdapter($p) {
    $global:QbClient = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.QbittorrentPort)" -DefaultHeaders @{ Referer = "http://127.0.0.1:$($p.QbittorrentPort)" }
    # The one client with a generated login: read where the installer saved it, inside the VM (Record.Secrets.ps1).
    $stored = Get-ComponentLogin 'qbittorrent'
    if (-not $stored) { Add-Missing 'qbittorrent: the installer''s saved login was not found in the VM (component-credentials.json under Deluno''s data folder)'; return $null }
    $login = Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/auth/login' -Form @{ username = $stored.User; password = $stored.Password }
    if ($login.reply.status -ne 200 -or $login.reply.bodyText -notmatch 'Ok') { Add-Missing 'qbittorrent: sign-in with the installer''s saved login failed'; return $null }
    $a = @{
        Login = $login; Ver = $null
        Version = { Send-Recorded -Client $global:QbClient -Path '/api/v2/app/version' }
        VersionOf = { param($e) $e.reply.bodyText.Trim().TrimStart('v') }
        List = { param($h) Send-Recorded -Client $global:QbClient -Path ('/api/v2/torrents/info' + $(if ($h) { "?hashes=$h" })) }
        Files = { param($h) Send-Recorded -Client $global:QbClient -Path "/api/v2/torrents/files?hash=$h" }
        Add = { param($t, $dir, $paused) Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/torrents/add' -Multipart @{ torrents = $t.Bytes; savepath = $dir; paused = "$paused".ToLower(); stopped = "$paused".ToLower() } }
        Pause = { param($h) $r = Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/torrents/stop' -Form @{ hashes = $h }; if ($r.reply.status -eq 404) { $r = Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/torrents/pause' -Form @{ hashes = $h } }; $r }
        Remove = { param($h, $data) Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/torrents/delete' -Form @{ hashes = $h; deleteFiles = "$data".ToLower() } }
        Recheck = { param($h) Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/torrents/recheck' -Form @{ hashes = $h } }
        Queue = { param($on) $null = Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/app/setPreferences' -Form @{ json = (@{ queueing_enabled = $true; max_active_downloads = $(if ($on) { 1 } else { 5 }) } | ConvertTo-Json -Compress) } }
        AllowLocalPeers = { param($on) $null = Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/app/setPreferences' -Form @{ json = (@{ enable_multi_connections_from_same_ip = [bool]$on } | ConvertTo-Json -Compress) } }
        SetUploadLimit = { param($h, $bytesPerSecond) Send-Recorded -Client $global:QbClient -Method POST -Path '/api/v2/torrents/setUploadLimit' -Form @{ hashes = $h; limit = $bytesPerSecond } }
        Progress = { param($e) $t = @(Get-ReplyJson $e)[0]; if ($t) { [double]$t.progress } else { 0 } }
        IsComplete = { param($e) $t = @(Get-ReplyJson $e)[0]; $t -and $t.progress -ge 1 }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path '/api/v2/torrents/info' }
    }
    $a
}

function New-TransmissionAdapter($p) {
    $global:TrHeaders = @{ Value = @{} }
    # Transmission keeps its shipped login unless Deluno saved one for it; either way it is read inside the VM.
    $stored = Get-ComponentLogin '^transmission$'
    $cred = if ($stored) { New-Object pscredential($stored.User, (ConvertTo-SecureString $stored.Password -AsPlainText -Force)) }
    $keep = @('content-type', 'x-transmission-session-id')
    $global:TrClient = if ($cred) { New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.TransmissionPort)" -BasicAuth $cred -KeepHeaders $keep } else { New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.TransmissionPort)" -KeepHeaders $keep }
    $global:TrId = 0
    $global:TrFields = @('id', 'hashString', 'name', 'status', 'percentDone', 'rateDownload', 'rateUpload', 'error', 'errorString', 'downloadDir', 'totalSize', 'leftUntilDone', 'peersConnected', 'peersSendingToUs', 'isStalled', 'isFinished', 'trackerStats', 'files', 'addedDate', 'downloadedEver', 'uploadedEver', 'eta', 'queuePosition', 'labels')
    $global:TrRpc = {
        param($method, $arguments = @{})
        $global:TrId++
        Send-Recorded -Client $global:TrClient -Method POST -Path '/transmission/rpc' -Headers $global:TrHeaders.Value -Json ([ordered]@{ method = $method; arguments = $arguments; tag = $global:TrId })
    }
    # The first call is refused with 409 and the session id to send back: that reply is itself recorded.
    $handshake = & $global:TrRpc 'session-get'
    if ($handshake.reply.status -eq 409) {
        $id = $handshake.reply.headers['x-transmission-session-id']
        if (-not $id) { $m = [regex]::Match([string]$handshake.reply.bodyText, 'X-Transmission-Session-Id:\s*(\S+?)<'); $id = $m.Groups[1].Value }
        Register-Secret $id
        $global:TrHeaders.Value = @{ 'X-Transmission-Session-Id' = $id }
    }
    $session = & $global:TrRpc 'session-get'
    $ver = ([string](Get-ReplyJson $session).arguments.version -split ' ')[0]
    if (-not $ver) { Add-Missing 'transmission: it did not answer session-get (it may want a login that is not the saved one)'; return $null }
    Save-State transmission $ver 'session-handshake' 'The first call, refused with 409 and the session id to send back (the id is removed from the recording).' @($handshake, $session)
    @{
        Ver = $null
        Version = { & $global:TrRpc 'session-get' }
        VersionOf = { param($e) ([string](Get-ReplyJson $e).arguments.version -split ' ')[0] }
        List = { param($h) & $global:TrRpc 'torrent-get' $(if ($h) { @{ fields = $global:TrFields; ids = @($h) } } else { @{ fields = $global:TrFields } }) }
        Add = { param($tor, $dir, $paused) & $global:TrRpc 'torrent-add' @{ metainfo = [Convert]::ToBase64String($tor.Bytes); 'download-dir' = $dir; paused = [bool]$paused } }
        Pause = { param($h) & $global:TrRpc 'torrent-stop' @{ ids = @($h) } }
        Remove = { param($h, $data) & $global:TrRpc 'torrent-remove' @{ ids = @($h); 'delete-local-data' = [bool]$data } }
        Recheck = { param($h) & $global:TrRpc 'torrent-verify' @{ ids = @($h) } }
        Queue = { param($on) $null = & $global:TrRpc 'session-set' @{ 'download-queue-enabled' = $true; 'download-queue-size' = $(if ($on) { 1 } else { 5 }) } }
        AllowLocalPeers = { param($on) }
        SetUploadLimit = { param($h, $bytesPerSecond) & $global:TrRpc 'torrent-set' @{ ids = @($h); uploadLimited = $true; uploadLimit = [int][math]::Max(1, [math]::Ceiling($bytesPerSecond / 1024)) } }
        Progress = { param($e) $x = @((Get-ReplyJson $e).arguments.torrents)[0]; if ($x) { [double]$x.percentDone } else { 0 } }
        IsComplete = { param($e) $x = @((Get-ReplyJson $e).arguments.torrents)[0]; $x -and $x.percentDone -ge 1 }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/transmission/rpc' -Json ([ordered]@{ method = 'session-get'; arguments = @{}; tag = 1 }) }
    }
}

function New-DelugeAdapter($p) {
    $global:DlClient = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.DelugePort)"
    $global:DlId = 0
    $global:DlRpc = { param($method, $params = @()) $global:DlId++; Send-Recorded -Client $global:DlClient -Method POST -Path '/json' -Json ([ordered]@{ method = $method; params = @($params); id = $global:DlId }) }
    $login = & $global:DlRpc 'auth.login' @('deluge')   # Deluge keeps the login it ships with (build-rules.md)
    if ((Get-ReplyJson $login).result -ne $true) { Add-Missing 'deluge: the web login with its shipped password failed'; return $null }
    $connected = & $global:DlRpc 'web.connected'
    if ((Get-ReplyJson $connected).result -ne $true) {
        $hosts = & $global:DlRpc 'web.get_hosts'
        $hostId = @((Get-ReplyJson $hosts).result)[0][0]
        $null = & $global:DlRpc 'web.connect' @($hostId)
    }
    $versionCall = & $global:DlRpc 'daemon.get_version'
    $ver = [string](Get-ReplyJson $versionCall).result
    if (-not $ver) { Add-Missing 'deluge: it did not answer daemon.get_version'; return $null }
    Save-State deluge $ver 'sign-in' 'auth.login and web.connected (the session cookie is removed from the recording).' @($login, $connected)
    $global:DlKeys = @('name', 'state', 'progress', 'total_size', 'total_done', 'save_path', 'download_location', 'move_completed', 'move_completed_path', 'download_payload_rate', 'upload_payload_rate', 'num_seeds', 'num_peers', 'time_added', 'tracker_status', 'message', 'is_finished', 'paused', 'queue', 'files')
    @{
        Ver = $null
        Version = { & $global:DlRpc 'daemon.get_version' }
        VersionOf = { param($e) [string](Get-ReplyJson $e).result }
        List = { param($h) & $global:DlRpc 'core.get_torrents_status' @($(if ($h) { @{ id = @($h) } } else { @{} }), $global:DlKeys) }
        Add = { param($tor, $dir, $paused) & $global:DlRpc 'core.add_torrent_file' @("$($tor.Title).torrent", [Convert]::ToBase64String($tor.Bytes), @{ download_location = $dir; add_paused = [bool]$paused; move_completed = $false }) }
        Pause = { param($h) & $global:DlRpc 'core.pause_torrents' @(, @($h)) }
        Remove = { param($h, $data) & $global:DlRpc 'core.remove_torrent' @($h, [bool]$data) }
        Recheck = { param($h) & $global:DlRpc 'core.force_recheck' @(, @($h)) }
        Queue = { param($on) $null = & $global:DlRpc 'core.set_config' @(@{ max_active_downloading = $(if ($on) { 1 } else { 5 }); queue_new_to_top = $false }) }
        AllowLocalPeers = { param($on) $null = & $global:DlRpc 'core.set_config' @(@{ allow_multiple_connections_per_ip = [bool]$on }) }
        SetUploadLimit = { param($h, $bytesPerSecond) & $global:DlRpc 'core.set_torrent_options' (@(, @($h)) + @(@{ max_upload_speed = [double][math]::Max(1, $bytesPerSecond / 1024) })) }
        Progress = { param($e) $x = (Get-ReplyJson $e).result; $first = if ($x) { $x.PSObject.Properties | Select-Object -First 1 }; if ($first) { [double]$x.($first.Name).progress / 100 } else { 0 } }
        IsComplete = { param($e) $x = (Get-ReplyJson $e).result; $first = if ($x) { $x.PSObject.Properties | Select-Object -First 1 }; $first -and $x.($first.Name).progress -ge 100 }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method POST -Path '/json' -Json ([ordered]@{ method = 'web.connected'; params = @(); id = 1 }) }
    }
}

# ---------------------------------------------------------------- the whole torrent side

function Invoke-RecordTorrentClients($p, [string[]] $Apps) {
    $all = @{}
    if ($Apps -contains 'qbittorrent') { $a = New-QbitAdapter $p; if ($a) { $all.qbittorrent = $a } }
    if ($Apps -contains 'transmission') { $a = New-TransmissionAdapter $p; if ($a) { $all.transmission = $a } }
    if ($Apps -contains 'deluge') { $a = New-DelugeAdapter $p; if ($a) { $all.deluge = $a } }
    if ($all.qbittorrent) {
        $ver = & $all.qbittorrent.VersionOf (& $all.qbittorrent.Version)
        Save-State qbittorrent $ver 'sign-in' 'POST /api/v2/auth/login: the reply sets a session cookie (removed from the recording).' @($all.qbittorrent.Login)
    }
    foreach ($app in 'qbittorrent', 'transmission', 'deluge') {
        if ($all[$app]) { try { Invoke-TorrentScenario -App $app -A $all[$app] -MediaFile $p.VmMediaFile } catch { Add-Missing "$app stopped: $($_.Exception.Message)" } }
    }

    # The swarm: a local tracker, then each downloader against a seeder.
    $pairs = @(@('qbittorrent', 'transmission'), @('qbittorrent', 'deluge'), @('transmission', 'qbittorrent')) | Where-Object { $all[$_[0]] -and $all[$_[1]] }
    if ($pairs.Count -eq 0) { return }
    $trackerPort = 18999
    New-Item -ItemType Directory -Force -Path 'C:\golden\recorded' | Out-Null
    Set-Content -LiteralPath 'C:\golden\recorded\Start-LocalTracker.ps1' -Value $p.TrackerScript -Encoding utf8
    $tracker = Start-Process -FilePath powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\golden\recorded\Start-LocalTracker.ps1', '-Port', $trackerPort
    try {
        Start-Sleep -Seconds 3
        foreach ($pair in $pairs) {
            try { Invoke-TorrentSwarm -SeedApp $pair[0] -Seed $all[$pair[0]] -LeechApp $pair[1] -Leech $all[$pair[1]] -MediaFile $p.VmMediaFile -TrackerUrl "http://127.0.0.1:$trackerPort/announce" }
            catch { Add-Missing "$($pair[1]) downloading / stalled-near-end (seeded by $($pair[0])) stopped: $($_.Exception.Message)" }
        }
    } finally { if ($tracker -and -not $tracker.HasExited) { Stop-Process -Id $tracker.Id -Force -ErrorAction SilentlyContinue } }
}
