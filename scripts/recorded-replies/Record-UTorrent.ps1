# Installs uTorrent (Classic, for Windows) in the golden VM, switches its Web UI on at 127.0.0.1 with a login made here, and records
# its replies through the same torrent scenario the other clients use (Record-Torrents.ps1: version, idle, stalled, queued, paused,
# removed, magnet without metadata, finished single file and folder, duplicate, refused file, dead tracker, failed, not answering),
# plus the calls only uTorrent's Web UI has: the token page, positional list rows, getprops / setprops (label), getfiles, getsettings
# and a wrong login (#1237). Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1, Install.Lib.ps1 and Record-Torrents.ps1.
#
# Where it comes from: uTorrent's own download address (download-new.utorrent.com), the same file utorrent.com offers. BitTorrent Inc.
# publishes no checksum for it, so the signature on the installer is checked instead (Authenticode, signed by the vendor) and the
# file's SHA-256 is written to each recording's note. The installer is a web installer; it needs the internet, and its offers are
# declined by the silent switch. If the silent switch does not install, the run stops and says so (nothing else is tried).

function ConvertTo-Bencode($Value) {
    if ($Value -is [int] -or $Value -is [long]) { return "i${Value}e" }
    if ($Value -is [string]) { $bytes = [Text.Encoding]::UTF8.GetBytes($Value); return "$($bytes.Length):$Value" }
    throw "Cannot bencode $($Value.GetType())"
}

# uTorrent reads settings.dat when it starts: a bencoded dictionary with its keys in order. The Web UI is off until this says otherwise.
# The .fileguard entry is left out on purpose (uTorrent accepts a file without it and writes its own on exit).
function New-UTorrentSettings([string] $Path, [int] $Port, [string] $User, [string] $Password, [string] $DownloadDir) {
    $pairs = [ordered]@{
        'bind_ip' = '127.0.0.1'; 'check_update' = 0; 'dir_active_download' = $DownloadDir; 'dir_active_download_flag' = 1
        'gui.show_notifications' = 0; 'webui.enable' = 1; 'webui.enable_guest' = 0; 'webui.password' = $Password; 'webui.port' = $Port
        'webui.restrict' = '127.0.0.1'; 'webui.username' = $User; 'dht' = 0; 'lsd' = 0; 'pex' = 0; 'upnp' = 0; 'natpmp' = 0
    }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('d')
    $keys = [string[]]@($pairs.Keys); [Array]::Sort($keys, [StringComparer]::Ordinal)   # a bencoded dictionary's keys are in raw byte order
    foreach ($k in $keys) { [void]$sb.Append((ConvertTo-Bencode $k)); [void]$sb.Append((ConvertTo-Bencode $pairs[$k])) }
    [void]$sb.Append('e')
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::UTF8.GetBytes($sb.ToString()))
}

# What the Web UI's list rows are, by position (the labels are uTorrent's own; the recording keeps the raw rows).
function Get-UtRows($Exchange) {
    $j = Get-ReplyJson $Exchange
    if ($j -and $j.torrents) { return @($j.torrents) }
    @()
}

function New-UTorrentAdapter($p, [string] $Version, $Login) {
    $port = $p.UTorrentPort
    $cred = New-Object pscredential($Login.User, (ConvertTo-SecureString $Login.Password -AsPlainText -Force))
    $global:UtClient = New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -BasicAuth $cred
    $global:UtToken = $null
    $global:UtHash = $null
    $global:UtTokenCall = { Send-Recorded -Client $global:UtClient -Path '/gui/token.html' }
    $tokenReply = & $global:UtTokenCall
    if ($tokenReply.reply.status -ne 200) { Add-Missing "utorrent: the Web UI's token page answered $($tokenReply.reply.status) with the login this run made"; return $null }
    $global:UtToken = ([regex]::Match([string]$tokenReply.reply.bodyText, '>([^<>]+)</div>')).Groups[1].Value.Trim()
    Register-Secret $global:UtToken
    $global:UtTokenReply = $tokenReply
    $global:UtCall = { param($query, $method = 'GET', $multipart = $null)
        $path = "/gui/?token=$($global:UtToken)&$query"
        if ($multipart) { Send-Recorded -Client $global:UtClient -Method POST -Path $path -Multipart $multipart } else { Send-Recorded -Client $global:UtClient -Method $method -Path $path }
    }
    $set = { param($name, $value) $null = & $global:UtCall "action=setsetting&s=$name&v=$([Uri]::EscapeDataString([string]$value))" }
    @{
        Ver = $null; Token = $tokenReply
        Version = { & $global:UtCall 'list=1' }
        VersionOf = { param($e) $Version }.GetNewClosure()
        List = { param($h) $global:UtHash = $h; & $global:UtCall 'list=1' }
        Files = { param($h) & $global:UtCall "action=getfiles&hash=$h" }
        Add = { param($t, $dir, $paused)
            & $set 'dir_active_download' $dir; & $set 'dir_active_download_flag' 'true'
            & $global:UtCall 'action=add-file' 'POST' @{ torrent_file = $t.Bytes } }.GetNewClosure()
        AddMagnet = { param($m, $dir) & $set 'dir_active_download' $dir; & $global:UtCall "action=add-url&s=$([Uri]::EscapeDataString($m))" }.GetNewClosure()
        AddBroken = { param($dir) & $set 'dir_active_download' $dir; & $global:UtCall 'action=add-file' 'POST' @{ torrent_file = [Text.Encoding]::UTF8.GetBytes('this is not a torrent') } }.GetNewClosure()
        Pause = { param($h) & $global:UtCall "action=pause&hash=$h" }
        Resume = { param($h) & $global:UtCall "action=unpause&hash=$h" }
        Remove = { param($h, $data) & $global:UtCall ("action=" + $(if ($data) { 'removedata' } else { 'remove' }) + "&hash=$h") }
        Recheck = { param($h) & $global:UtCall "action=recheck&hash=$h" }
        # uTorrent queues by active-download count: one at a time, the rest wait (status bit 0x40 "queued").
        Queue = { param($on) & $set 'max_active_downloads' $(if ($on) { 1 } else { 5 }); & $set 'max_active_torrent' $(if ($on) { 1 } else { 8 }) }.GetNewClosure()
        AllowLocalPeers = { param($on) }
        SetUploadLimit = { param($h, $bps) }
        Extras = { param($t, $dir)
            $h = $t.InfoHash.ToUpperInvariant(); $v = $Version
            Save-State utorrent $v 'settings' 'The Web UI settings (getsettings): download folder, queue limits and the Web UI itself. uTorrent''s Web UI has no free-space call.' @((& $global:UtCall 'action=getsettings'))
            Save-State utorrent $v 'props' 'getprops on a finished torrent: seed ratio, seed time, trackers and the per-torrent limits.' @((& $global:UtCall "action=getprops&hash=$h"))
            $label = & $global:UtCall "action=setprops&hash=$h&s=label&v=recorded-movies"
            Save-State utorrent $v 'label-set' 'A label put on a finished torrent (setprops s=label), then the list showing it (label is at index 11).' @($label, (& $global:UtCall 'list=1'))
            $unknown = & $global:UtCall "action=setprops&hash=$('0' * 40)&s=label&v=recorded-movies"
            Save-State utorrent $v 'label-unknown-hash' 'setprops s=label for a hash uTorrent never had.' @($unknown, (& $global:UtCall "action=getprops&hash=$('0' * 40)"))
            Save-State utorrent $v 'start-stop-force' 'stop, start and forcestart of a finished torrent, each followed by the list.' @(
                (& $global:UtCall "action=stop&hash=$h"), (& $global:UtCall 'list=1'), (& $global:UtCall "action=start&hash=$h"), (& $global:UtCall 'list=1'), (& $global:UtCall "action=forcestart&hash=$h"), (& $global:UtCall 'list=1'))
            Save-State utorrent $v 'files-unknown-hash' 'getfiles for a hash uTorrent never had.' @((& $global:UtCall "action=getfiles&hash=$('0' * 40)"))
        }.GetNewClosure()
        Progress = { param($e) $row = @(Get-UtRows $e | Where-Object { $_[0] -ieq $global:UtHash })[0]; if ($row) { [double]$row[4] / 1000 } else { 0 } }
        IsComplete = { param($e) $row = @(Get-UtRows $e | Where-Object { $_[0] -ieq $global:UtHash })[0]; [bool]($row -and [int]$row[4] -ge 1000) }
        NotAnswering = { Send-Recorded -Client (New-RecordedClient -BaseUrl 'http://127.0.0.1:1') -Path '/gui/?list=1' }
    }
}

function Invoke-RecordUTorrent($p) {
    Assert-FreeSpace 8 'uTorrent'
    Assert-Internet
    $dir = Get-AppFolder 'utorrent'
    $installer = Join-Path $dir 'utorrent_installer.exe'
    $null = Save-Verified -Url 'https://download-new.utorrent.com/endpoint/utorrent/os/windows/track/stable/' -OutFile $installer -NoChecksumReason 'BitTorrent Inc. lists none for the download button'
    Assert-SignedBy $installer 'BitTorrent|Rainberry|uTorrent'

    $exe = Join-Path $env:APPDATA 'uTorrent\uTorrent.exe'
    if (-not (Test-Path $exe)) {
        # The installer's own silent switch. It offers bundled software; the silent switch takes none of the offers.
        Write-RunLog 'installing uTorrent (silent)'
        $proc = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru
        $found = Wait-Until -Seconds 600 -EverySeconds 5 { if (Test-Path $exe) { $true } elseif ($proc.HasExited -and $proc.ExitCode -ne 0) { throw "the uTorrent installer stopped with exit code $($proc.ExitCode) under /S" } }
        if (-not $found) { Add-Missing 'utorrent: the installer did not put uTorrent.exe in %APPDATA%\uTorrent within 10 minutes under the /S switch; install it by hand in the VM and run this script again (it skips the install when the file is there)'; return }
        Start-Sleep -Seconds 5
    }
    Stop-AppByName 'uTorrent'   # it may have started itself after the install: settings.dat is only read at start
    Start-Sleep -Seconds 3
    $version = (Get-Item $exe).VersionInfo.FileVersion -replace ',', '.' -replace '\s', ''
    if (-not $version) { $version = (Get-Item $exe).VersionInfo.ProductVersion }

    $login = [pscustomobject]@{ User = 'rec' + (New-VmSecret 4); Password = (New-VmSecret 12) }
    Register-Secret $login.User
    $port = $p.UTorrentPort
    $save = Join-Path $dir 'save'; New-Item -ItemType Directory -Force -Path $save | Out-Null
    New-UTorrentSettings -Path (Join-Path $env:APPDATA 'uTorrent\settings.dat') -Port $port -User $login.User -Password $login.Password -DownloadDir $save
    Remove-DesktopTask 'RecorderUTorrent'
    Start-InDesktopSession -TaskName 'RecorderUTorrent' -File $exe -WorkingDirectory (Split-Path $exe)
    $up = Wait-Until -Seconds 90 -EverySeconds 3 { try { $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/gui/token.html" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop; $r.StatusCode } catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } } }
    if (-not $up) { Add-Missing "utorrent: its Web UI did not answer on 127.0.0.1:$port after 90 s (settings.dat may have been refused, or no one is signed in to the VM's desktop)"; return }

    # A wrong login first: the 401 itself is a state.
    $badCred = New-Object pscredential($login.User, (ConvertTo-SecureString 'not-the-password' -AsPlainText -Force))
    $refused = Send-Recorded -Client (New-RecordedClient -BaseUrl "http://127.0.0.1:$port" -BasicAuth $badCred) -Path '/gui/token.html'
    $noLogin = Send-Recorded -Client (New-RecordedClient -BaseUrl "http://127.0.0.1:$port") -Path '/gui/token.html'

    New-Item -ItemType Directory -Force -Path 'C:\golden\recorded\feed' | Out-Null
    foreach ($f in $p.FeedFiles) { Set-Content -LiteralPath (Join-Path 'C:\golden\recorded\feed' $f.name) -Value $f.text -Encoding utf8 }
    $a = New-UTorrentAdapter $p $version $login
    if (-not $a) { return }
    Save-State utorrent $version 'token' ('uTorrent''s token page (gui/token.html): the HTML that holds the token every later call sends. The token and the Web UI login are removed from the recording. Installed from the vendor''s download address.' + (Get-DownloadNote)) @($a.Token)
    Save-State utorrent $version 'login-refused' 'The token page with a wrong password, and with no login at all: HTTP 401.' @($refused, $noLogin)
    try { Invoke-TorrentScenario -App 'utorrent' -A $a -MediaFile $p.VmMediaFile } catch { Add-Missing "utorrent stopped: $($_.Exception.Message)" }
    Remove-Item 'C:\golden\recorded\utorrent' -Recurse -Force -ErrorAction SilentlyContinue
    Stop-AppByName 'uTorrent'
    Remove-DesktopTask 'RecorderUTorrent'
}
