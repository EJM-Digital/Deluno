<#
.SYNOPSIS
    Installs, starts and connects the two torrent clients the picker does not install, uTorrent and rTorrent, so every client scenario
    runs on them too (golden path scenario suite, #1276).

.DESCRIPTION
    Dot-source after Suite.Lib.ps1 (and Fixtures.Lib.ps1), then call Initialize-ExtraClients. Runs INSIDE the golden VM, in Windows
    PowerShell 5.1, in the signed-in desktop session. What it installs and from where is pinned in ExtraClients.Pins.ps1.

      - uTorrent Classic: the vendor's own installer (its Authenticode signature is checked; the vendor publishes no checksum, so the
        installed version is checked against the pin and the installer's SHA-256 goes to the log). Its Web UI is switched on through
        settings.dat at 127.0.0.1 only, with a login made here.
      - rTorrent: no Windows build exists. It runs from Ubuntu's own package inside WSL1 (no virtualization needed, which the VM does
        not have): Canonical's dated root file system is checked against the SHA-256 Canonical publishes, then apt installs the pinned
        rtorrent plus nginx, whose scgi mount is the HTTP XML-RPC address Deluno speaks to (127.0.0.1 only, a login made here). Its files
        are the Windows folder Deluno's libraries watch, reached as /mnt/c/..., so Deluno gets a path mapping for it.

    Both are then added to Deluno through the same API call the Download clients screen makes. The logins are the run's own throwaway
    secrets (masked in everything the suite writes). Turning the Windows Subsystem for Linux feature on needs ONE restart, which a run
    cannot make: Prepare-ExtraClients.ps1 does it once, in the checkpoint the suite restores.
#>

. (Join-Path $PSScriptRoot 'ExtraClients.Pins.ps1')

$script:Xc = @{
    Root     = 'C:\golden\extra-clients'
    Keeper   = $null
}

function Write-XcLog {
    param([string] $Text)
    try { Write-SuiteLog $Text } catch { Write-Host $Text }
}

function Get-XcFolder {
    param([string] $Name)
    $dir = Join-Path $script:Xc.Root $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dir
}

# C:\Downloads\x -> /mnt/c/Downloads/x, and back.
function ConvertTo-XcWslPath {
    param([string] $WindowsPath)
    $full = [IO.Path]::GetFullPath($WindowsPath)
    '/mnt/' + $full.Substring(0, 1).ToLowerInvariant() + $full.Substring(2).Replace('\', '/')
}
function ConvertFrom-XcWslPath {
    param([string] $WslPath)
    if ($WslPath -match '^/mnt/([a-z])(/.*)?$') { return ($Matches[1].ToUpperInvariant() + ':' + $(if ($Matches[2]) { $Matches[2].Replace('/', '\') } else { '\' })) }
    $WslPath
}

# Downloads a file and checks its SHA-256 against the pin. A mismatch deletes the file and stops.
function Save-XcPinned {
    param([Parameter(Mandatory)] [string] $Url, [Parameter(Mandatory)] [string] $OutFile, [string] $Sha256)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        Write-XcLog "   downloading $Url"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -TimeoutSec 3600
    }
    $actual = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Sha256) {
        if ($actual -ne $Sha256.ToLowerInvariant()) {
            Remove-Item -LiteralPath $OutFile -Force
            throw "The download of $Url does not match the pinned SHA-256 ($Sha256). It was removed."
        }
        Write-XcLog "   $([IO.Path]::GetFileName($OutFile)): SHA-256 matches the pin ($actual)"
    }
    else { Write-XcLog "   $([IO.Path]::GetFileName($OutFile)): no SHA-256 is pinned for it; this download's is $actual" }
    $actual
}

# ---------------------------------------------------------------- uTorrent

function ConvertTo-XcBencode {
    param($Value)
    if ($Value -is [int] -or $Value -is [long]) { return "i${Value}e" }
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]$Value)
    "$($bytes.Length):$Value"
}

# uTorrent reads settings.dat when it starts: a bencoded dictionary with its keys in byte order. Its Web UI is on the BitTorrent port
# (webui.port alone is ignored in 3.6), so both are the same port, bound to the loopback address only.
function New-XcUTorrentSettings {
    param([string] $Path, [int] $Port, [string] $User, [string] $Password, [string] $DataFolder)
    $pairs = @{
        'bind_ip' = '127.0.0.1'; 'check_update' = 0; 'dir_active_download' = $DataFolder; 'dir_active_download_flag' = 1
        'dir_completed_download_flag' = 0; 'gui.show_notifications' = 0; 'webui.enable' = 1; 'webui.enable_guest' = 0
        'webui.password' = $Password; 'webui.port' = $Port; 'webui.restrict' = '127.0.0.1'; 'webui.username' = $User
        'bind_port' = $Port; 'dht' = 0; 'lsd' = 0; 'pex' = 0; 'upnp' = 0; 'natpmp' = 0
    }
    $keys = [string[]]@($pairs.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('d')
    foreach ($k in $keys) { [void]$sb.Append((ConvertTo-XcBencode $k)); [void]$sb.Append((ConvertTo-XcBencode $pairs[$k])) }
    [void]$sb.Append('e')
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::UTF8.GetBytes($sb.ToString()))
}

function Get-XcUTorrentExe { Join-Path $env:APPDATA 'uTorrent\uTorrent.exe' }

# The vendor's installer, signature-checked, run silently. Returns the installed version.
function Install-XcUTorrent {
    $pin = $script:XcPins.UTorrent
    $exe = Get-XcUTorrentExe
    if (-not (Test-Path -LiteralPath $exe)) {
        $installer = Join-Path (Get-XcFolder 'utorrent') 'utorrent_installer.exe'
        $sha = Save-XcPinned -Url $pin.Url -OutFile $installer -Sha256 $pin.Sha256
        $sig = Get-AuthenticodeSignature -LiteralPath $installer
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch $pin.Signer) {
            throw "uTorrent's installer is not signed by a publisher matching $($pin.Signer) (signature status $($sig.Status)). It was not run."
        }
        Write-XcLog "   uTorrent installer: Authenticode signature valid, signed by $($sig.SignerCertificate.Subject.Split(',')[0]); SHA-256 $sha"
        Add-XcNote "uTorrent installer SHA-256 $sha (no checksum is published by the vendor; its Authenticode signature was valid)"
        Write-XcLog '   installing uTorrent (silent)'
        $proc = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru
        $found = Wait-Until -TimeoutSec 600 -IntervalSec 5 -Condition {
            if (Test-Path -LiteralPath $exe) { return $true }
            if ($proc.HasExited -and $proc.ExitCode -ne 0) { throw "uTorrent's installer stopped with exit code $($proc.ExitCode) under /S" }
            $null
        }
        if (-not $found.Ok) { throw 'uTorrent''s installer put no uTorrent.exe in %APPDATA%\uTorrent within 10 minutes under the /S switch.' }
        Start-Sleep -Seconds 5 # reason: lets the installer finish and any first start of uTorrent settle before it is stopped
        # It may have started itself after the install; settings.dat is read only at a start.
        foreach ($p in @(Get-Process -Name 'uTorrent' -ErrorAction SilentlyContinue)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 3 # reason: lets uTorrent let go of settings.dat before it is written
    }
    $version = ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -replace ',', '.' -replace '\s', '')
    if (-not $version) { $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion }
    if ($version -ne $pin.Version) {
        throw "uTorrent $version is installed, not the pinned $($pin.Version). The vendor's download address now serves another build: pin the new version (and its recordings) deliberately in ExtraClients.Pins.ps1."
    }
    $version
}

function Start-XcUTorrent {
    param([string] $User, [string] $Password)
    $exe = Get-XcUTorrentExe
    $port = [int]$script:XcPins.Ports.utorrent
    foreach ($p in @(Get-Process -Name 'uTorrent' -ErrorAction SilentlyContinue)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2 # reason: lets the old uTorrent exit before settings.dat is written
    New-Item -ItemType Directory -Force -Path $script:XcPins.DataFolder | Out-Null
    New-XcUTorrentSettings -Path (Join-Path $env:APPDATA 'uTorrent\settings.dat') -Port $port -User $User -Password $Password -DataFolder $script:XcPins.DataFolder
    $null = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Minimized -PassThru
    $up = Wait-Until -TimeoutSec 90 -IntervalSec 3 -Condition {
        try { (Invoke-WebRequest -Uri "http://127.0.0.1:$port/gui/token.html" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop).StatusCode }
        catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null } }
    }
    if (-not $up.Ok) { throw "uTorrent's Web UI did not answer on 127.0.0.1:$port within 90 s (settings.dat may have been refused, or the desktop session is not signed in)." }
}

# ---------------------------------------------------------------- rTorrent (Ubuntu's package, under WSL1)

function Invoke-XcWsl {
    param([string] $Command, [switch] $AllowFailure)
    $out = & wsl.exe -d $script:XcPins.Wsl.Distro -u root -- bash -c $Command 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "wsl: '$($Command.Substring(0, [Math]::Min(60, $Command.Length)))...' failed with exit code $LASTEXITCODE"
    }
    ($out | Out-String) -replace "`0", ''
}

# Turns the feature on (which needs a restart a run cannot make) and imports the pinned root file system. Returns 'ready', or
# 'reboot' when the feature was only just enabled.
function Install-XcWsl {
    $wsl = $script:XcPins.Wsl
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -ErrorAction SilentlyContinue
    if (-not $feature) { throw 'This Windows has no optional feature Microsoft-Windows-Subsystem-Linux, so WSL1 cannot be turned on here.' }
    if ($feature.State -ne 'Enabled') {
        Write-XcLog '   turning on the Windows Subsystem for Linux (WSL1)'
        $null = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux -NoRestart -All
        return 'reboot'
    }
    $listed = ((& wsl.exe --list --quiet 2>&1 | Out-String) -replace "`0", '')
    if ($listed -match [regex]::Escape($wsl.Distro)) { return 'ready' }
    $dir = Get-XcFolder 'rtorrent'
    $tar = Join-Path $dir $wsl.RootfsName
    $null = Save-XcPinned -Url $wsl.RootfsUrl -OutFile $tar -Sha256 $wsl.RootfsSha256
    $null = & wsl.exe --set-default-version 1 2>&1
    $null = & wsl.exe --import $wsl.Distro (Join-Path $dir 'wsl') $tar --version 1 2>&1
    if ($LASTEXITCODE -ne 0) { throw "wsl --import of the Ubuntu root file system failed (exit code $LASTEXITCODE)" }
    'ready'
}

# rTorrent, nginx and tmux from Ubuntu's own repository (apt checks the signed package lists itself); rTorrent at the pinned version.
function Install-XcRtorrent {
    $state = Install-XcWsl
    if ($state -eq 'reboot') { return 'reboot' }
    $pin = $script:XcPins.Rtorrent
    $have = Invoke-XcWsl 'command -v rtorrent && command -v nginx && command -v tmux' -AllowFailure
    if ($have -notmatch 'rtorrent' -or $have -notmatch 'nginx' -or $have -notmatch 'tmux') {
        Write-XcLog "   installing $($pin.Package) and $($pin.Packages) in the WSL distribution"
        $null = Invoke-XcWsl "export DEBIAN_FRONTEND=noninteractive; apt-get update -y && apt-get install -y $($pin.Package) $($pin.Packages)"
    }
    $installed = (Invoke-XcWsl "dpkg-query -W -f='`${Version}' rtorrent").Trim()
    if ($installed -notlike "$($pin.Version)*") {
        throw "rTorrent $installed is installed in WSL, not the pinned $($pin.Version)."
    }
    $installed
}

# Starts rTorrent (in a detached tmux, since it wants a terminal) behind nginx's scgi mount at 127.0.0.1, inside one long-lived
# wsl.exe so the distribution stays up. Returns the version rTorrent itself reports.
function Start-XcRtorrent {
    param([string] $User, [string] $Password)
    $dir = Get-XcFolder 'rtorrent'
    $port = [int]$script:XcPins.Ports.rtorrent
    $data = ConvertTo-XcWslPath $script:XcPins.DataFolder
    New-Item -ItemType Directory -Force -Path $script:XcPins.DataFolder | Out-Null
    # nginx reads {SHA} lines, so the file is written from here and the password is on no command line.
    $sha = [Convert]::ToBase64String([Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($Password)))
    $htpasswd = Join-Path $dir 'htpasswd'
    [IO.File]::WriteAllText($htpasswd, "${User}:{SHA}$sha`n", (New-Object Text.UTF8Encoding($false)))
    $rc = Join-Path $dir 'rtorrent.rc'
    $rcText = @(
        "directory.default.set = $data", 'session.path.set = /root/.rtsession', 'network.scgi.open_port = 127.0.0.1:5000',
        'network.port_range.set = 6881-6889', 'dht.mode.set = disable', 'protocol.pex.set = no', 'trackers.use_udp.set = no', 'encoding.add = UTF-8',
        'throttle.max_downloads.global.set = 0', 'network.bind_address.set = 127.0.0.1'
    ) -join "`n"
    [IO.File]::WriteAllText($rc, $rcText + "`n", (New-Object Text.UTF8Encoding($false)))
    $nginx = Join-Path $dir 'rt-nginx.conf'
    $nginxText = @"
worker_processes 1; pid /run/rt-nginx.pid; error_log /tmp/rt-nginx.err;
events { worker_connections 64; }
http { access_log off; client_body_temp_path /tmp/rt-body; scgi_temp_path /tmp/rt-scgi;
  server { listen 127.0.0.1:$port;
    location /RPC2 { include /etc/nginx/scgi_params; scgi_pass 127.0.0.1:5000; auth_basic "rTorrent"; auth_basic_user_file $(ConvertTo-XcWslPath $htpasswd); }
  } }
"@
    [IO.File]::WriteAllText($nginx, $nginxText, (New-Object Text.UTF8Encoding($false)))
    $null = & wsl.exe --terminate $script:XcPins.Wsl.Distro 2>&1
    $startCmd = "pkill -x rtorrent; pkill -x nginx; rm -f /root/.rtsession/rtorrent.lock; mkdir -p /root/.rtsession /tmp/rt-body /tmp/rt-scgi; nginx -c $(ConvertTo-XcWslPath $nginx); tmux new-session -d -s rt 'rtorrent -n -o import=$(ConvertTo-XcWslPath $rc)'; sleep infinity"
    $log = Join-Path $dir 'rtorrent-wsl.log'
    $script:Xc.Keeper = Start-Process -FilePath 'wsl.exe' -ArgumentList @('-d', $script:XcPins.Wsl.Distro, '-u', 'root', '--', 'bash', '-c', ('"' + $startCmd + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    $auth = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${User}:$Password")) }
    $body = '<?xml version="1.0"?><methodCall><methodName>system.client_version</methodName><params></params></methodCall>'
    $up = Wait-Until -TimeoutSec 90 -IntervalSec 3 -Condition {
        try { (Invoke-WebRequest -Uri "http://127.0.0.1:$port/RPC2" -Method Post -Body $body -ContentType 'text/xml' -Headers $auth -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop).Content } catch { $null }
    }
    if (-not $up.Ok) { throw "rTorrent's XML-RPC mount did not answer on 127.0.0.1:$port within 90 s (see $dir)." }
    $version = ([regex]::Match([string]$up.Value, '<string>([^<]+)</string>')).Groups[1].Value
    if (-not $version) { throw "rTorrent answered system.client_version with no version: $($up.Value)" }
    $version
}

# ---------------------------------------------------------------- into Deluno

# Adds the client to Deluno the way the Download clients screen does (POST /api/download-clients), tests the connection, and for
# rTorrent adds the path mapping its /mnt/c/ paths need. Returns the client Deluno holds.
function Add-XcClientToDeluno {
    param([Parameter(Mandatory)] [ValidateSet('utorrent', 'rtorrent')] [string] $Kind, [string] $User, [string] $Password)
    $port = [int]$script:XcPins.Ports[$Kind]
    $name = $(if ($Kind -eq 'utorrent') { 'uTorrent' } else { 'rTorrent' })
    $existing = @(Get-DownloadClients | Where-Object { $_.protocol -eq $Kind })
    foreach ($e in $existing) { Try-Deluno -Method DELETE -Path "/api/download-clients/$($e.id)" | Out-Null }
    $body = @{
        name = $name; protocol = $Kind; host = '127.0.0.1'; port = $port; username = $User; password = $Password
        moviesCategory = 'deluno-movies'; tvCategory = 'deluno-tv'; priority = 10; isEnabled = $true
    }
    if ($Kind -eq 'rtorrent') { $body.urlBase = 'RPC2' }
    $client = Invoke-Deluno -Method POST -Path '/api/download-clients' -Body $body
    if ($Kind -eq 'rtorrent') {
        # rTorrent says /mnt/c/Downloads/...; Deluno reads the same folder as C:\Downloads\....
        $m = Try-Deluno -Method POST -Path "/api/download-clients/$($client.id)/path-mappings" -Body @{ remotePath = '/mnt/c/'; localPath = 'C:\'; isEnabled = $true; priority = 1 }
        if ($m.Status -lt 200 -or $m.Status -ge 300) { throw (Hide-Secrets "Adding rTorrent's path mapping answered $($m.Status). $($m.Raw)") }
    }
    $t = Try-Deluno -Method POST -Path "/api/download-clients/$($client.id)/test" -TimeoutSec 60
    if ($t.Status -lt 200 -or $t.Status -ge 300) { throw (Hide-Secrets "Deluno's test of $name answered $($t.Status). $($t.Raw)") }
    Write-XcLog ("   Deluno: {0} added and tested ({1})" -f $name, (Hide-Secrets ([string]$t.Raw).Substring(0, [Math]::Min(120, ([string]$t.Raw).Length))))
    $client
}

function Add-XcNote {
    param([string] $Text)
    try { Add-Note $Text } catch { Write-XcLog "   note: $Text" }
}

# What the suite calls after Deluno has connected the picker's clients: installs (if not already), starts and adds both. A client that
# cannot be made ready is said plainly in the log and left out, so the scenarios for it fail with that reason and the others still run.
function Initialize-ExtraClients {
    New-SuiteSecret 'utorrent-user' | Out-Null; New-SuiteSecret 'utorrent-password' | Out-Null
    New-SuiteSecret 'rtorrent-user' | Out-Null; New-SuiteSecret 'rtorrent-password' | Out-Null
    $failed = @()
    try {
        Write-XcLog 'uTorrent and rTorrent: setting up'
        $v = Install-XcUTorrent
        Start-XcUTorrent -User (Get-SuiteSecret 'utorrent-user') -Password (Get-SuiteSecret 'utorrent-password')
        Add-XcClientToDeluno -Kind 'utorrent' -User (Get-SuiteSecret 'utorrent-user') -Password (Get-SuiteSecret 'utorrent-password') | Out-Null
        Write-XcLog "   uTorrent $v is up on 127.0.0.1:$($script:XcPins.Ports.utorrent) and connected."
    }
    catch { $failed += "uTorrent: $(Hide-Secrets $_.Exception.Message)" }
    try {
        $state = Install-XcRtorrent
        if ($state -eq 'reboot') { throw 'The Windows Subsystem for Linux is not turned on in this checkpoint. It needs one restart a run cannot make: run Prepare-ExtraClients.ps1 once in the VM, restart it, run it again, and take the clean checkpoint again.' }
        $v = Start-XcRtorrent -User (Get-SuiteSecret 'rtorrent-user') -Password (Get-SuiteSecret 'rtorrent-password')
        Add-XcClientToDeluno -Kind 'rtorrent' -User (Get-SuiteSecret 'rtorrent-user') -Password (Get-SuiteSecret 'rtorrent-password') | Out-Null
        Write-XcLog "   rTorrent $v (Ubuntu package under WSL1) is up on 127.0.0.1:$($script:XcPins.Ports.rtorrent) and connected."
    }
    catch { $failed += "rTorrent: $(Hide-Secrets $_.Exception.Message)" }
    foreach ($f in $failed) {
        Write-XcLog "EXTRA CLIENT NOT READY - $f"
        Add-XcNote "EXTRA CLIENT NOT READY - $f"
    }
    $clients = Get-DownloadClients
    Write-XcLog ("Deluno: clients {0}" -f (($clients | ForEach-Object { "$($_.protocol)=$($_.healthStatus)" }) -join ', '))
}
