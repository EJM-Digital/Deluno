# Installs rTorrent in the golden VM under WSL1 (no virtualization needed), mounts its XML-RPC behind a web server on 127.0.0.1 with a
# login made here, and records its replies through the same torrent scenario the other clients use (Record-Torrents.ps1), plus
# the calls only rTorrent's XML-RPC has: system.client_version, d.multicall2 with Deluno's exact field list, t.multicall, f.multicall,
# d.custom1 / d.custom1.set, d.directory, d.base_path, the load.* family, and the fault for an unknown hash (#1237).
# Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1, Install.Lib.ps1 and Record-Torrents.ps1.
#
# Where it comes from: Canonical's own Ubuntu 22.04 WSL root file system (cloud-images.ubuntu.com, checked against the SHA256SUMS file
# beside it), then Ubuntu's own packages for rtorrent and nginx (apt checks its signed repository itself). rTorrent has no Windows
# build; this is the real rTorrent 0.9.x, running on Linux's system calls the way WSL1 provides them.
#
# The one thing that may need a restart: turning the Windows Subsystem for Linux feature on. The run says "reboot" when it did,
# and the host script (Invoke-RecordRealApp.ps1) restarts the VM and runs the same command again; every step skips what is done.

$script:RtDistro = 'deluno-rt'

function ConvertTo-WslPath([string] $WindowsPath) {
    $full = [IO.Path]::GetFullPath($WindowsPath)
    '/mnt/' + $full.Substring(0, 1).ToLowerInvariant() + $full.Substring(2).Replace('\', '/')
}

function Invoke-Wsl([string] $Command, [switch] $AllowFailure) {
    $out = & wsl.exe -d $script:RtDistro -u root -- bash -c $Command 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw "wsl: '$($Command.Substring(0, [Math]::Min(60, $Command.Length)))...' failed with exit code $LASTEXITCODE" }
    $out
}

function Install-WslUbuntu {
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -ErrorAction SilentlyContinue
    if (-not $feature) { throw 'This Windows has no optional feature Microsoft-Windows-Subsystem-Linux, so WSL1 cannot be turned on here.' }
    if ($feature.State -ne 'Enabled') {
        Write-RunLog 'turning on the Windows Subsystem for Linux (WSL1)'
        $r = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -NoRestart -All
        return 'reboot'
    }
    $listed = (& wsl.exe --list --quiet 2>&1 | Out-String) -replace "`0", ''
    if ($listed -match [regex]::Escape($script:RtDistro)) { return 'ready' }
    $dir = Get-AppFolder 'rtorrent'
    $tar = Join-Path $dir 'ubuntu-jammy-wsl-amd64.rootfs.tar.gz'
    $base = 'https://cloud-images.ubuntu.com/wsl/jammy/current'
    $null = Save-Verified -Url "$base/ubuntu-jammy-wsl-amd64-ubuntu22.04lts.rootfs.tar.gz" -OutFile $tar -ChecksumUrl "$base/SHA256SUMS" -ChecksumName 'ubuntu-jammy-wsl-amd64-ubuntu22.04lts.rootfs.tar.gz'
    $null = & wsl.exe --set-default-version 1 2>&1
    $null = & wsl.exe --import $script:RtDistro (Join-Path $dir 'wsl') $tar --version 1 2>&1
    if ($LASTEXITCODE -ne 0) { throw "wsl --import of the Ubuntu root file system failed (exit code $LASTEXITCODE)" }
    'ready'
}

# XML-RPC as Deluno speaks it (RtorrentDownloadClient.cs): text/xml POST of a methodCall to the mount's address.
function ConvertTo-XmlRpcValue($Value) {
    if ($Value -is [byte[]]) { return "<value><base64>$([Convert]::ToBase64String($Value))</base64></value>" }
    if ($Value -is [bool]) { return "<value><boolean>$([int]$Value)</boolean></value>" }
    if ($Value -is [int] -or $Value -is [long]) { return "<value><i4>$Value</i4></value>" }
    "<value><string>$([Security.SecurityElement]::Escape([string]$Value))</string></value>"
}
function Send-XmlRpc {
    param($Client, [string] $Method, [object[]] $Params = @(), [string] $Path = '/RPC2')
    $xml = '<?xml version="1.0"?><methodCall><methodName>' + $Method + '</methodName><params>' + (($Params | ForEach-Object { '<param>' + (ConvertTo-XmlRpcValue $_) + '</param>' }) -join '') + '</params></methodCall>'
    Send-Recorded -Client $Client -Method POST -Path $Path -Body $xml -ContentType 'text/xml'
}

# The rows of a list answer: each row's values as text, from the XML-RPC array of arrays.
function Get-RtRows($Exchange) {
    if ($Exchange.reply.status -ne 200 -or -not $Exchange.reply.bodyText) { return @() }
    try { $doc = [xml]$Exchange.reply.bodyText } catch { return @() }
    $rows = $doc.SelectNodes('/methodResponse/params/param/value/array/data/value/array/data')
    @(foreach ($row in $rows) { , @(foreach ($v in $row.SelectNodes('value')) { if ($v.FirstChild -and $v.FirstChild.NodeType -eq 'Element') { $v.FirstChild.InnerText } else { $v.InnerText } }) })
}

$script:RtFields = @('d.hash=', 'd.name=', 'd.is_active=', 'd.is_hash_checking=', 'd.complete=', 'd.bytes_done=', 'd.size_bytes=', 'd.down.rate=', 'd.up.rate=', 'd.ratio=', 'd.message=', 'd.directory=', 'd.peers_connected=')

function New-RtorrentAdapter($p, [string] $Version, $Login) {
    $cred = New-Object pscredential($Login.User, (ConvertTo-SecureString $Login.Password -AsPlainText -Force))
    $global:RtClient = New-RecordedClient -BaseUrl "http://127.0.0.1:$($p.RtorrentPort)" -BasicAuth $cred
    $global:RtHash = $null
    $global:RtSave = Join-Path (Get-AppFolder 'rtorrent') 'save'
    $global:RtRpc = { param($method, $params = @()) Send-XmlRpc -Client $global:RtClient -Method $method -Params $params }
    $setDir = { param($dir) $null = & $global:RtRpc 'directory.default.set' @((ConvertTo-WslPath $dir)) }
    @{
        Ver = $null
        Version = { & $global:RtRpc 'system.client_version' }
        VersionOf = { param($e) $Version }.GetNewClosure()
        List = { param($h) $global:RtHash = $h; & $global:RtRpc 'd.multicall2' (@('', 'main') + $script:RtFields) }
        Files = { param($h) & $global:RtRpc 'f.multicall' @($h.ToUpperInvariant(), '', 'f.path=') }
        Add = { param($t, $dir, $paused) & $setDir $dir; & $global:RtRpc $(if ($paused) { 'load.raw' } else { 'load.raw_start' }) @('', [byte[]]$t.Bytes) }.GetNewClosure()
        AddMagnet = { param($m, $dir) & $setDir $dir; & $global:RtRpc 'load.start' @('', $m) }.GetNewClosure()
        AddBroken = { param($dir) & $setDir $dir; & $global:RtRpc 'load.raw_start' @('', [byte[]][Text.Encoding]::UTF8.GetBytes('this is not a torrent')) }.GetNewClosure()
        Pause = { param($h) & $global:RtRpc 'd.pause' @($h.ToUpperInvariant()) }
        Resume = { param($h) & $global:RtRpc 'd.resume' @($h.ToUpperInvariant()) }
        # Deluno removes with d.erase only; rTorrent keeps the data, so "with data" is the recorder tidying the folder afterwards.
        Remove = { param($h, $data) & $global:RtRpc 'd.erase' @($h.ToUpperInvariant()) }
        Recheck = { param($h) & $global:RtRpc 'd.check_hash' @($h.ToUpperInvariant()) }
        # rTorrent has no download queue: this limits how many it downloads at once, and the list shows what that did.
        Queue = { param($on) $null = & $global:RtRpc 'throttle.max_downloads.global.set' @($(if ($on) { 1 } else { 0 })) }
        AllowLocalPeers = { param($on) }
        SetUploadLimit = { param($h, $bps) }
        Extras = { param($t, $dir)
            $h = $t.InfoHash.ToUpperInvariant(); $v = $Version
            Save-State rtorrent $v 'settings' 'The default download folder and the free disk space of a torrent''s folder, as rTorrent answers them.' @((& $global:RtRpc 'directory.default'), (& $global:RtRpc 'd.free_diskspace' @($h)))
            Save-State rtorrent $v 'directory-and-base-path' 'd.directory and d.base_path of a finished torrent (the path Deluno imports from).' @((& $global:RtRpc 'd.directory' @($h)), (& $global:RtRpc 'd.base_path' @($h)))
            $set = & $global:RtRpc 'd.custom1.set' @($h, 'recorded-movies')
            Save-State rtorrent $v 'label-set' 'd.custom1.set (rTorrent''s label), then d.custom1 reads it back.' @($set, (& $global:RtRpc 'd.custom1' @($h)))
            Save-State rtorrent $v 'trackers' 't.multicall with t.is_enabled and t.message, as Deluno reads a torrent''s trackers.' @((& $global:RtRpc 't.multicall' @($h, '', 't.is_enabled=', 't.message=')))
            $zero = '0' * 40
            Save-State rtorrent $v 'unknown-hash-fault' 'd.is_active, d.pause, d.resume, d.erase, d.custom1, d.base_path, f.multicall and t.multicall for a hash rTorrent never had: each answers an XML-RPC fault.' @(
                (& $global:RtRpc 'd.is_active' @($zero)), (& $global:RtRpc 'd.pause' @($zero)), (& $global:RtRpc 'd.resume' @($zero)), (& $global:RtRpc 'd.erase' @($zero)),
                (& $global:RtRpc 'd.custom1' @($zero)), (& $global:RtRpc 'd.base_path' @($zero)), (& $global:RtRpc 'f.multicall' @($zero, '', 'f.path=')), (& $global:RtRpc 't.multicall' @($zero, '', 't.is_enabled=', 't.message=')))
            Save-State rtorrent $v 'load-url-unreachable' 'load.start of a link nothing serves (the load.normal / load.start family takes an address rTorrent fetches itself).' @((& $global:RtRpc 'load.start' @('', 'http://127.0.0.1:1/none.torrent')), (& $global:RtRpc 'd.multicall2' (@('', 'main') + $script:RtFields)))
            Save-State rtorrent $v 'load-not-started' 'load.raw (added, not started) of a torrent the client already holds, then the list.' @((& $global:RtRpc 'load.raw' @('', [byte[]]$t.Bytes)), (& $global:RtRpc 'd.multicall2' (@('', 'main') + $script:RtFields)))
        }.GetNewClosure()
        Progress = { param($e) $row = @(Get-RtRows $e | Where-Object { $_[0] -ieq $global:RtHash })[0]; if ($row -and [double]$row[6] -gt 0) { [double]$row[5] / [double]$row[6] } else { 0 } }
        IsComplete = { param($e) $row = @(Get-RtRows $e | Where-Object { $_[0] -ieq $global:RtHash })[0]; [bool]($row -and $row[4] -eq '1') }
        NotAnswering = { Send-XmlRpc -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Method 'system.client_version' }
    }
}

function Invoke-RecordRtorrent($p) {
    Assert-FreeSpace 10 'rTorrent'
    Assert-Internet
    $state = Install-WslUbuntu
    if ($state -eq 'reboot') { Add-Missing 'REBOOT: the Windows Subsystem for Linux was just turned on; the VM must restart and this command must run again'; $script:RebootNeeded = $true; return }
    $dir = Get-AppFolder 'rtorrent'
    $save = Join-Path $dir 'save'; New-Item -ItemType Directory -Force -Path $save | Out-Null

    # rTorrent and nginx from Ubuntu's own repository (apt verifies its signed package lists itself).
    $have = (Invoke-Wsl 'command -v rtorrent && command -v nginx && command -v tmux' -AllowFailure | Out-String)
    if ($have -notmatch 'rtorrent' -or $have -notmatch 'nginx') {
        Write-RunLog 'installing rtorrent and nginx in the WSL distribution'
        $null = Invoke-Wsl 'export DEBIAN_FRONTEND=noninteractive; apt-get update -y && apt-get install -y rtorrent nginx tmux'
    }

    # The login for the web mount, made here. nginx reads {SHA} lines, so the file is written from here and the password is on no command line.
    $login = [pscustomobject]@{ User = 'rec' + (New-VmSecret 4); Password = (New-VmSecret 12) }
    Register-Secret $login.User
    $sha = [Convert]::ToBase64String([Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($login.Password)))
    $htpasswd = Join-Path $dir 'htpasswd'
    [IO.File]::WriteAllText($htpasswd, "$($login.User):{SHA}$sha`n", (New-Object Text.UTF8Encoding($false)))
    $rc = Join-Path $dir 'rtorrent.rc'
    $rcText = @(
        "directory.default.set = $(ConvertTo-WslPath $save)", 'session.path.set = /root/.rtsession', 'network.scgi.open_port = 127.0.0.1:5000',
        'network.port_range.set = 6881-6889', 'dht.mode.set = disable', 'protocol.pex.set = no', 'trackers.use_udp.set = no', 'encoding.add = UTF-8',
        'throttle.max_downloads.global.set = 0', 'network.bind_address.set = 127.0.0.1'
    ) -join "`n"
    [IO.File]::WriteAllText($rc, $rcText + "`n", (New-Object Text.UTF8Encoding($false)))
    $nginx = Join-Path $dir 'rt-nginx.conf'
    $port = $p.RtorrentPort
    $nginxText = @"
worker_processes 1; pid /run/rt-nginx.pid; error_log /tmp/rt-nginx.err;
events { worker_connections 64; }
http { access_log off; client_body_temp_path /tmp/rt-body; scgi_temp_path /tmp/rt-scgi;
  server { listen 127.0.0.1:$port;
    location /RPC2 { include /etc/nginx/scgi_params; scgi_pass 127.0.0.1:5000; auth_basic "rTorrent"; auth_basic_user_file $(ConvertTo-WslPath $htpasswd); }
  } }
"@
    [IO.File]::WriteAllText($nginx, $nginxText, (New-Object Text.UTF8Encoding($false)))

    # Both run inside one long-lived wsl.exe so the distribution stays up; rTorrent needs a terminal, so it lives in a detached tmux.
    Stop-AppByName 'wsl'
    $null = & wsl.exe --terminate $script:RtDistro 2>&1
    $startCmd = "pkill -x rtorrent; pkill -x nginx; rm -f /root/.rtsession/rtorrent.lock; mkdir -p /root/.rtsession /tmp/rt-body /tmp/rt-scgi; nginx -c $(ConvertTo-WslPath $nginx); tmux new-session -d -s rt 'rtorrent -n -o import=$(ConvertTo-WslPath $rc)'; sleep infinity"
    $keeper = Start-AppProcess -File 'wsl.exe' -Arguments @('-d', $script:RtDistro, '-u', 'root', '--', 'bash', '-c', $startCmd) -LogName 'rtorrent-wsl.log'
    $up = Wait-Until -Seconds 90 -EverySeconds 3 {
        try { $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/RPC2" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop; $r.StatusCode } catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } }
    }
    if (-not $up) { Add-Missing "rtorrent: the XML-RPC mount did not answer on 127.0.0.1:$port after 90 s (see $(Get-AppFolder 'logs'))"; return }

    $cred = New-Object pscredential($login.User, (ConvertTo-SecureString $login.Password -AsPlainText -Force))
    $first = Send-XmlRpc -Client (New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -BasicAuth $cred) -Method 'system.client_version'
    $version = ([regex]::Match([string]$first.reply.bodyText, '<string>([^<]+)</string>')).Groups[1].Value
    if (-not $version) { Add-Missing "rtorrent: system.client_version did not answer (status $($first.reply.status)); rTorrent may not have started under tmux in WSL1"; return }
    $bad = New-Object pscredential($login.User, (ConvertTo-SecureString 'not-the-password' -AsPlainText -Force))
    $refused = Send-XmlRpc -Client (New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -BasicAuth $bad) -Method 'system.client_version'
    $noLogin = Send-XmlRpc -Client (New-RecordedClient -BaseUrl "http://127.0.0.1:$port") -Method 'system.client_version'
    Save-State rtorrent $version 'login-refused' 'system.client_version with a wrong password and with no login: HTTP 401 from the web mount (rTorrent itself has no login; the mount in front of it does).' @($refused, $noLogin)
    Save-State rtorrent $version 'client-version' ('system.client_version: the plain version string. rTorrent 0.9.x on Ubuntu 22.04 under WSL1, XML-RPC through nginx''s scgi mount.' + (Get-DownloadNote)) @($first)

    New-Item -ItemType Directory -Force -Path 'C:\golden\recorded\feed' | Out-Null
    foreach ($f in $p.FeedFiles) { Set-Content -LiteralPath (Join-Path 'C:\golden\recorded\feed' $f.name) -Value $f.text -Encoding utf8 }
    $a = New-RtorrentAdapter $p $version $login
    try { Invoke-TorrentScenario -App 'rtorrent' -A $a -MediaFile $p.VmMediaFile } catch { Add-Missing "rtorrent stopped: $($_.Exception.Message)" }
    Remove-Item 'C:\golden\recorded\rtorrent' -Recurse -Force -ErrorAction SilentlyContinue
    $null = Invoke-Wsl 'pkill -x rtorrent; pkill -x nginx' -AllowFailure
    if ($keeper -and -not $keeper.HasExited) { Stop-Process -Id $keeper.Id -Force -ErrorAction SilentlyContinue }
}
