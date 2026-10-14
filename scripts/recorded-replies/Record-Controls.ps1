# Records the controls Deluno uses on a job it already has, and how each client answers a wrong login (#1237, #1251, #1257).
# Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1, Record-Usenet.ps1 and Record-Torrents.ps1. It is the small
# companion of the full run: it leaves the clients as it found them and writes only these states.
#
#   resumed            a job that was paused, resumed by its own id (the list before and after is kept beside it)
#   pause-unknown-id   a pause of an id or hash the client never had (qBittorrent, Transmission, Deluge; SABnzbd and NZBGet have it in unknown-id)
#   a wrong login      NZBGet login-refused (the real 401), Transmission login-refused (the 401 and its WWW-Authenticate),
#                      qBittorrent api-key-refused (a bad API key sent as a bearer token), Deluge unauthenticated-call (a call after a failed login)
#   NZBGet config      loadconfig-clean, config-error-paused (status and log while NZBGet is paused over errors in its configuration),
#                      config-error-cleared (status after the bad line is gone and NZBGet reloaded)

# NZBGet's own login. Deluno writes one into nzbget.conf when it wires NZBGet; an install that was never wired keeps the shipped one
# (a public default, build-rules.md). Read inside the VM and never printed; Save-State removes it from whatever it writes.
function Get-NzbgetLogin {
    $user = 'nzbget'; $password = 'tegbzn6789'
    foreach ($path in 'C:\ProgramData\NZBGet\nzbget.conf', 'C:\Program Files\NZBGet\nzbget.conf') {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $u = Select-String -LiteralPath $path -Pattern '^\s*ControlUsername\s*=\s*(.*?)\s*$' | Select-Object -First 1
        $w = Select-String -LiteralPath $path -Pattern '^\s*ControlPassword\s*=\s*(.*?)\s*$' | Select-Object -First 1
        if ($u -and $u.Matches[0].Groups[1].Value) { $user = $u.Matches[0].Groups[1].Value }
        if ($w -and $w.Matches[0].Groups[1].Value) { $password = $w.Matches[0].Groups[1].Value }
        break
    }
    Register-Secret $password
    New-Object pscredential($user, (ConvertTo-SecureString $password -AsPlainText -Force))
}

function New-NzbgetRpc($p, [pscredential] $Cred, [string[]] $KeepHeaders = @('content-type')) {
    $client = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.NzbgetPort)" -BasicAuth $Cred -KeepHeaders $KeepHeaders
    $script:RpcId = 100
    { param($method, $params = @()) $script:RpcId++; Send-Recorded -Client $client -Method POST -Path '/jsonrpc' -Json ([ordered]@{ method = $method; params = @($params); id = $script:RpcId }) }.GetNewClosure()
}

# ---------------------------------------------------------------- SABnzbd and NZBGet: resume a known paused job

function Invoke-RecordSabnzbdControls($p) {
    $key = Find-SabApiKey $p.SabnzbdConfigPath
    Register-Secret $key
    $sab = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.SabnzbdPort)"
    function Api($query) { Send-Recorded -Client $sab -Path "/api?$query&output=json&apikey=$key" }
    $nzbUrl = [uri]::EscapeDataString((Get-StubNzbUrl $p.NewznabPort))
    $ver = (Get-ReplyJson (Api 'mode=version')).version
    if (-not $ver) { Add-Missing 'sabnzbd resumed: it did not answer mode=version'; return }
    $wasPaused = [bool](Get-ReplyJson (Api 'mode=queue')).queue.paused
    $id = $null
    try {
        # The whole queue paused so the job does not start, then the job itself paused, then resumed by its own id.
        $null = Api 'mode=pause'
        $add = Api "mode=addurl&name=$nzbUrl&nzbname=Recorded.Control.Sab"
        $id = @((Get-ReplyJson $add).nzo_ids)[0]
        Start-Sleep -Seconds 3
        $pause = Api "mode=queue&name=pause&value=$id"
        $before = Api 'mode=queue'
        $resume = Api "mode=queue&name=resume&value=$id"
        $after = Api 'mode=queue'
        Save-State sabnzbd $ver 'resumed' 'A job paused by its id (the whole queue was paused so it did not start), then resumed by its id: the queue before and after.' @($add, $pause, $before, $resume, $after)
    } finally {
        if ($id) { $null = Api "mode=queue&name=delete&value=$id&del_files=1" }
        if (-not $wasPaused) { $null = Api 'mode=resume' }
    }
}

function Invoke-RecordNzbgetControls($p) {
    $cred = Get-NzbgetLogin
    $rpc = New-NzbgetRpc $p $cred @('content-type', 'www-authenticate')
    $ver = (Get-ReplyJson (& $rpc 'version')).result
    if (-not $ver) { Add-Missing 'nzbget resumed / login-refused: it did not answer version with its login'; return }
    $nzbUrl = Get-StubNzbUrl $p.NewznabPort
    $wasPaused = [bool](Get-ReplyJson (& $rpc 'status')).result.DownloadPaused
    $id = $null
    try {
        $null = & $rpc 'pausedownload'
        $add = & $rpc 'append' @('Recorded.Control.Nzbget.nzb', $nzbUrl, '', 0, $false, $false, '', 0, 'SCORE', @())
        $id = [int](Get-ReplyJson $add).result
        Start-Sleep -Seconds 4
        $pause = & $rpc 'editqueue' @('GroupPause', '', @($id))
        $before = & $rpc 'listgroups'
        $resume = & $rpc 'editqueue' @('GroupResume', '', @($id))
        $after = & $rpc 'listgroups'
        Save-State nzbget $ver 'resumed' 'A job paused by its id (the whole download was paused so it did not start), then resumed by its id (GroupResume): the groups before and after.' @($add, $pause, $before, $resume, $after)
    } finally {
        if ($id) { $null = & $rpc 'editqueue' @('GroupFinalDelete', '', @($id)) }
        if (-not $wasPaused) { $null = & $rpc 'resumedownload' }
    }
}

# ---------------------------------------------------------------- the torrent clients

function Invoke-RecordTorrentControls($p, [string[]] $Apps) {
    Assert-FreeSpace 4 'the torrent client controls'
    New-Item -ItemType Directory -Force -Path 'C:\golden\recorded\feed' | Out-Null
    foreach ($f in $p.FeedFiles) { Set-Content -LiteralPath (Join-Path 'C:\golden\recorded\feed' $f.name) -Value $f.text -Encoding utf8 }
    $adapters = [ordered]@{}
    if ($Apps -contains 'qbittorrent') { $a = New-QbitAdapter $p; if ($a) { $adapters.qbittorrent = $a } }
    if ($Apps -contains 'transmission') { $a = New-TransmissionAdapter $p; if ($a) { $adapters.transmission = $a } }
    if ($Apps -contains 'deluge') { $a = New-DelugeAdapter $p; if ($a) { $adapters.deluge = $a } }
    foreach ($app in $adapters.Keys) {
        $A = $adapters[$app]
        $ver = & $A.VersionOf (& $A.Version)
        if (-not $ver) { Add-Missing "$app resumed: it did not answer its version call"; continue }
        $dir = "C:\golden\recorded\$app\save"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        try {
            # A torrent with no data and a tracker that does not exist: it sits in the list, so it can be paused and resumed at leisure.
            $t = Add-FeedRelease -Title "Recorded.Control.$app" -DummyMb 700
            $add = & $A.Add $t $dir $false
            Start-Sleep -Seconds 4
            $pause = & $A.Pause $t.InfoHash
            Start-Sleep -Seconds 3
            $paused = & $A.List $t.InfoHash
            $resume = & $A.Resume $t.InfoHash
            Start-Sleep -Seconds 3
            $resumed = & $A.List $t.InfoHash
            Save-State $app $ver 'resumed' 'A torrent paused by its hash and resumed by its hash: the list as the client answered after the pause and after the resume.' @($add, $pause, $paused, $resume, $resumed)

            $unknown = '0' * 40
            $exchanges = @((& $A.Pause $unknown), (& $A.List $unknown))
            if ($app -eq 'transmission') {
                # Transmission also takes its small numeric id.
                $exchanges = @($exchanges[0], (& $global:TrRpc 'torrent-stop' @{ ids = @(999999) }), $exchanges[1])
            }
            Save-State $app $ver 'pause-unknown-id' 'A pause of a hash (and, for Transmission, a numeric id) the client never had.' $exchanges
        } finally {
            $null = & $A.Remove $t.InfoHash $true
            Remove-Item "C:\golden\recorded\$app" -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # A wrong login, each client its own way.
    if ($adapters.qbittorrent) {
        $ver = & $adapters.qbittorrent.VersionOf (& $adapters.qbittorrent.Version)
        $bearer = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.QbittorrentPort)" -DefaultHeaders @{ Referer = "http://127.0.0.1:$($p.QbittorrentPort)" }
        $badKey = 'not-a-real-api-key'
        $headers = @{ Authorization = "Bearer $badKey" }
        $refused = @((Send-Recorded -Client $bearer -Path '/api/v2/torrents/info' -Headers $headers), (Send-Recorded -Client $bearer -Method POST -Path '/api/v2/torrents/stop' -Headers $headers -Form @{ hashes = ('0' * 40) }))
        if (@($refused | Where-Object { $_.reply.status -in 401, 403 }).Count -gt 0) {
            Save-State qbittorrent $ver 'api-key-refused' 'Calls made with a bad API key sent as a bearer token (and no sign-in cookie): a torrent list and a stop.' $refused
        } else { Add-Missing "qbittorrent api-key-refused: a bad bearer token got HTTP $($refused[0].reply.status), not a refusal" }
    }
    if ($adapters.transmission) { Invoke-RecordTransmissionRefusedLogin $p $adapters.transmission }
    if ($adapters.deluge) {
        $ver = & $adapters.deluge.VersionOf (& $adapters.deluge.Version)
        $fresh = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.DelugePort)"
        $script:FreshDelugeId = 0
        $rpc = { param($method, $params = @()) $script:FreshDelugeId++; Send-Recorded -Client $fresh -Method POST -Path '/json' -Json ([ordered]@{ method = $method; params = @($params); id = $script:FreshDelugeId }) }.GetNewClosure()
        $failed = & $rpc 'auth.login' @('not-the-password')
        $exchanges = @($failed, (& $rpc 'web.connected'), (& $rpc 'web.update_ui' @(@('name', 'state', 'progress'), @{})), (& $rpc 'core.pause_torrent' @(, @('0' * 40))))
        Save-State deluge $ver 'unauthenticated-call' 'Calls made after a failed login, so with no session: a connected check, a torrent list and a pause.' $exchanges
    }

    # The adapters also saved their sign-in states while they logged in; those are already recorded, so only the controls stay.
    $keepStates = @{ qbittorrent = @('resumed', 'pause-unknown-id', 'api-key-refused'); transmission = @('resumed', 'pause-unknown-id', 'login-refused'); deluge = @('resumed', 'pause-unknown-id', 'unauthenticated-call') }
    $null = $script:Recordings.RemoveAll([Predicate[object]]{ param($r) $keepStates.ContainsKey($r.app) -and ($keepStates[$r.app] -notcontains $r.state) })
}

# Transmission in the VM runs with RPC authentication off (the shipped state: it listens on 127.0.0.1 only, behind a host whitelist), so a wrong
# login is not refused there, and the RPC cannot switch authentication on (rpc-* settings are not session-set arguments). To record the real
# refusal a SECOND copy of the same transmission-daemon is started for a few seconds on another port, with its own throwaway config folder that
# requires a login; the running Transmission is not touched. The copy is stopped and its folder removed afterwards.
function Invoke-RecordTransmissionRefusedLogin($p, $A) {
    $ver = & $A.VersionOf (& $A.Version)
    $running = Get-CimInstance Win32_Process -Filter "Name='transmission-daemon.exe'" | Select-Object -First 1
    if (-not $running -or -not $running.ExecutablePath) { Add-Missing 'transmission login-refused: the running transmission-daemon was not found in the VM, so a second copy could not be started'; return }
    $work = 'C:\golden\recorded\transmission-auth'
    $port = 9092
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $work 'config'), (Join-Path $work 'downloads')
    $testUser = 'recorded-test'
    $testPassword = 'recorded-' + [guid]::NewGuid().ToString('N')
    Register-Secret $testPassword
    $settings = [ordered]@{
        'rpc-authentication-required' = $true; 'rpc-username' = $testUser; 'rpc-password' = $testPassword
        'rpc-port' = $port; 'rpc-bind-address' = '127.0.0.1'; 'rpc-whitelist-enabled' = $true; 'rpc-whitelist' = '127.0.0.1'
        'rpc-host-whitelist-enabled' = $false; 'peer-port' = 51999; 'dht-enabled' = $false; 'lpd-enabled' = $false; 'utp-enabled' = $false
        'download-dir' = (Join-Path $work 'downloads'); 'umask' = '022'
    }
    Set-Content -LiteralPath (Join-Path $work 'config\settings.json') -Value ($settings | ConvertTo-Json) -Encoding ascii
    $second = Start-Process -FilePath $running.ExecutablePath -ArgumentList '-f', '-g', (Join-Path $work 'config') -WindowStyle Hidden -PassThru
    try {
        $up = Wait-Until -Seconds 30 -EverySeconds 1 { try { $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $port); $c.Close(); $true } catch { } }
        if (-not $up) { Add-Missing 'transmission login-refused: the second transmission-daemon did not start listening in 30 s'; return }
        $keep = @('content-type', 'www-authenticate', 'x-transmission-session-id')
        $badCred = New-Object pscredential($testUser, (ConvertTo-SecureString 'not-the-password' -AsPlainText -Force))
        $bad = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -BasicAuth $badCred -KeepHeaders $keep
        $call = { param($headers) Send-Recorded -Client $bad -Method POST -Path '/transmission/rpc' -Headers $headers -Json ([ordered]@{ method = 'session-get'; arguments = @{}; tag = 1 }) }.GetNewClosure()
        $exchanges = @(& $call @{})
        # With a made-up session id too: the login is checked before the session id.
        $exchanges += (& $call @{ 'X-Transmission-Session-Id' = 'not-a-real-session-id' })
        $good = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -KeepHeaders $keep
        $exchanges += (Send-Recorded -Client $good -Method POST -Path '/transmission/rpc' -Json ([ordered]@{ method = 'session-get'; arguments = @{}; tag = 2 }))
        if (@($exchanges | Where-Object { $_.reply.status -eq 401 }).Count -gt 0) {
            Save-State transmission $ver 'login-refused' 'Calls made with a wrong login, and with none, to a Transmission whose RPC requires one (a second copy of the same daemon, started for this recording because the VM''s own Transmission has authentication off): it answers 401 and a WWW-Authenticate header.' $exchanges
        } else { Add-Missing "transmission login-refused: with authentication on, a wrong login got HTTP $(($exchanges | ForEach-Object { $_.reply.status }) -join ', '), never 401" }
    } finally {
        if ($second -and -not $second.HasExited) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 1
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# NZBGet lets the addresses in AuthorizedIP (127.0.0.1 here) in without a login, so a wrong login is not refused from inside the VM. To record
# the real 401 the option is emptied in nzbget.conf, NZBGet reloaded, the calls made, and the file put back byte for byte and reloaded again.
function Invoke-RecordNzbgetRefusedLogin($p) {
    $cred = Get-NzbgetLogin
    $rpc = New-NzbgetRpc $p $cred
    $ver = (Get-ReplyJson (& $rpc 'version')).result
    if (-not $ver) { Add-Missing 'nzbget login-refused: it did not answer version with its login'; return }
    $configFile = (@((Get-ReplyJson (& $rpc 'config')).result) | Where-Object { $_.Name -eq 'ConfigFile' } | Select-Object -First 1).Value
    if (-not $configFile -or -not (Test-Path -LiteralPath $configFile)) { $configFile = 'C:\ProgramData\NZBGet\nzbget.conf' }
    $original = [IO.File]::ReadAllBytes($configFile)
    $latin = [Text.Encoding]::GetEncoding(28591)
    $text = $latin.GetString($original)
    if ($text -notmatch '(?m)^AuthorizedIP=\S') { Add-Missing 'nzbget login-refused: AuthorizedIP is not set in nzbget.conf, so a wrong login was never let in by address; nothing to change'; return }
    $wasPaused = [bool](Get-ReplyJson (& $rpc 'status')).result.DownloadPaused
    $keep = @('content-type', 'www-authenticate')
    $waitUp = { Start-Sleep -Seconds 2; $null = Wait-Until -Seconds 90 -EverySeconds 2 { $r = & $rpc 'version'; if ($r.reply.status -eq 200) { $r } }; Start-Sleep -Seconds 3 }.GetNewClosure()
    try {
        [IO.File]::WriteAllBytes($configFile, $latin.GetBytes(([regex]::Replace($text, '(?m)^AuthorizedIP=.*$', 'AuthorizedIP='))))
        $null = & $rpc 'reload'
        & $waitUp
        $badCred = New-Object pscredential($cred.UserName, (ConvertTo-SecureString 'not-the-password' -AsPlainText -Force))
        $bad = New-NzbgetRpc $p $badCred $keep
        $refusedList = & $bad 'listgroups'
        $refusedStatus = & $bad 'status'
        $versionRefused = & $bad 'version'
        $none = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.NzbgetPort)" -KeepHeaders $keep
        $noLogin = Send-Recorded -Client $none -Method POST -Path '/jsonrpc' -Json ([ordered]@{ method = 'listgroups'; params = @(); id = 5 })
        if ($refusedList.reply.status -eq 401) {
            Save-State nzbget $ver 'login-refused' 'Calls made with a wrong login while NZBGet''s AuthorizedIP was emptied (the VM''s NZBGet lets 127.0.0.1 in without one): listgroups, status and version are all refused with a 401 and a WWW-Authenticate header, and so is a call with no login.' @($refusedList, $refusedStatus, $noLogin, $versionRefused)
        } else { Add-Missing "nzbget login-refused: with AuthorizedIP emptied, a wrong login got HTTP $($refusedList.reply.status) from listgroups, not 401" }
    } finally {
        [IO.File]::WriteAllBytes($configFile, $original)
        $null = & $rpc 'reload'
        & $waitUp
        $after = [bool](Get-ReplyJson (& $rpc 'status')).result.DownloadPaused
        if ($after -ne $wasPaused) { $null = & $rpc $(if ($wasPaused) { 'pausedownload' } else { 'resumedownload' }) }
        if ((Get-ReplyJson (& $rpc 'version')).result -ne $ver) { Add-Missing 'nzbget: NZBGet did not answer after nzbget.conf was put back; check it in the VM' }
    }
}

# ---------------------------------------------------------------- NZBGet paused over errors in its configuration (#1257)

function Invoke-RecordNzbgetConfigError($p) {
    $cred = Get-NzbgetLogin
    $rpc = New-NzbgetRpc $p $cred
    $ver = (Get-ReplyJson (& $rpc 'version')).result
    if (-not $ver) { Add-Missing 'nzbget config-error-paused: it did not answer version with its login'; return }
    $configFile = (@((Get-ReplyJson (& $rpc 'config')).result) | Where-Object { $_.Name -eq 'ConfigFile' } | Select-Object -First 1).Value
    if (-not $configFile -or -not (Test-Path -LiteralPath $configFile)) { $configFile = 'C:\ProgramData\NZBGet\nzbget.conf' }
    if (-not (Test-Path -LiteralPath $configFile)) { Add-Missing "nzbget config-error-paused: nzbget.conf was not found in the VM ($configFile)"; return }
    $original = [IO.File]::ReadAllBytes($configFile)
    $wasPaused = [bool](Get-ReplyJson (& $rpc 'status')).result.DownloadPaused
    [IO.File]::WriteAllBytes('C:\golden\recorded-nzbget.conf.original', $original)

    # Waits for NZBGet to answer again after a reload (it restarts itself).
    $waitUp = {
        $null = Wait-Until -Seconds 20 -EverySeconds 1 { $r = & $rpc 'version'; if ($r.reply.status -eq 0) { $true } }   # went down (it may be too quick to see)
        $up = Wait-Until -Seconds 90 -EverySeconds 2 { $r = & $rpc 'version'; if ($r.reply.status -eq 200) { $r } }
        Start-Sleep -Seconds 3
        $up
    }.GetNewClosure()
    try {
        $clean = & $rpc 'loadconfig'
        $cleanNames = @((Get-ReplyJson $clean).result | ForEach-Object { $_.Name })
        if (@($cleanNames | Where-Object { $_ -in 'ConfigFile', 'AppBin', 'AppDir', 'Version' }).Count -gt 0) {
            Add-Missing 'nzbget loadconfig-clean: the file in the VM already carries NZBGet''s read-only values, so it is not a clean file'
        } else {
            Save-State nzbget $ver 'loadconfig-clean' 'NZBGet''s loadconfig of a clean nzbget.conf: the options as the file holds them, with none of NZBGet''s read-only runtime values (ConfigFile, AppBin, AppDir, Version).' @($clean)
        }

        # One invalid line, then a reload: NZBGet logs the error and pauses all activity.
        [IO.File]::WriteAllBytes($configFile, $original + [Text.Encoding]::ASCII.GetBytes("AppBin=x`n"))
        $reload = & $rpc 'reload'
        $null = & $waitUp
        $statusBad = & $rpc 'status'
        $logBad = & $rpc 'log' @(0, 200)
        $pausedNow = [bool](Get-ReplyJson $statusBad).result.DownloadPaused
        $logText = ($logBad.reply.bodyText)
        if ($pausedNow -and $logText -match 'Invalid option') {
            Save-State nzbget $ver 'config-error-paused' 'One invalid line (AppBin=x) added to nzbget.conf, then reload: NZBGet logs "Invalid option" and pauses all activity (status DownloadPaused true; log with parameters 0 and 200).' @($reload, $statusBad, $logBad)
        } else {
            Add-Missing "nzbget config-error-paused: after the invalid line and a reload, DownloadPaused was $pausedNow and the log $(if ($logText -match 'Invalid option') { 'named' } else { 'did not name' }) an invalid option"
        }
    } finally {
        # The file exactly as it was (the invalid line removed), a reload, and the download state as it was.
        [IO.File]::WriteAllBytes($configFile, $original)
        $reload2 = & $rpc 'reload'
        $null = & $waitUp
        $statusAfter = & $rpc 'status'
        $steps = @($reload2, $statusAfter)
        if ([bool](Get-ReplyJson $statusAfter).result.DownloadPaused -ne $wasPaused) {
            $steps += (& $rpc $(if ($wasPaused) { 'pausedownload' } else { 'resumedownload' }))
            $steps += (& $rpc 'status')
        }
        $final = [bool](Get-ReplyJson $steps[-1]).result.DownloadPaused
        if (-not $final -and -not $wasPaused) {
            Save-State nzbget $ver 'config-error-cleared' 'The invalid line removed and NZBGet reloaded: status shows DownloadPaused false (a resumedownload is in the recording only if the reload alone did not clear it).' $steps
        } elseif ($final -eq $wasPaused) {
            Add-Missing 'nzbget config-error-cleared: NZBGet was paused before the run, so there is no recovered state to record'
        } else {
            Add-Missing 'nzbget config-error-cleared: NZBGet is still not back to its original download state; check it in the VM'
        }
        Remove-Item 'C:\golden\recorded-nzbget.conf.original' -Force -ErrorAction SilentlyContinue
    }
}
