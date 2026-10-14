# Helpers the install-and-record scripts share (#1237). Runs INSIDE the golden VM (Windows PowerShell 5.1, PowerShell Direct)
# after Record.Lib.ps1 and Record.Secrets.ps1. It downloads only from an app's own release source, checks a checksum wherever
# the vendor publishes one, starts an app on 127.0.0.1, and makes the secrets a recording needs inside the VM, so none is ever
# a parameter, printed or put on a command line.
#
# The throwaway VM is the only place these installs happen: the apps may add their own firewall rules there, and nothing on
# the rig host or the workstation is changed.

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$script:AppsRoot = 'C:\golden\recorded-apps'
$script:Downloads = [System.Collections.Generic.List[string]]::new()   # "what was downloaded, from where, and how it was checked", in the run log and the recordings' notes

function Get-AppFolder([string] $App) {
    $dir = Join-Path $script:AppsRoot $App
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dir
}

# The internet check comes first: every download below needs it, and a clear sentence beats a failed download in the middle.
function Assert-Internet {
    try { $null = Invoke-WebRequest -Uri 'https://api.github.com/rate_limit' -UseBasicParsing -TimeoutSec 20 }
    catch { throw "The VM cannot reach the internet ($($_.Exception.Message)). The install scripts download each app from its own release source." }
}

# An app's newest release asset on GitHub: its name, address and the SHA-256 GitHub itself records for the file (the "digest" of the asset).
function Get-GitHubAsset {
    param([Parameter(Mandatory)] [string] $Repo, [Parameter(Mandatory)] [string] $NamePattern, [string] $Tag)
    $uri = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$Repo/releases/latest" }
    $release = Invoke-RestMethod -Uri $uri -Headers @{ 'User-Agent' = 'deluno-recorder'; Accept = 'application/vnd.github+json' } -UseBasicParsing
    $asset = @($release.assets | Where-Object { $_.name -match $NamePattern }) | Select-Object -First 1
    if (-not $asset) { throw "$Repo release $($release.tag_name) has no file matching $NamePattern" }
    [pscustomobject]@{ Name = $asset.name; Url = $asset.browser_download_url; Sha256 = ([string]$asset.digest -replace '^sha256:', ''); Tag = [string]$release.tag_name; Release = $release }
}

# Downloads a file and checks it against the checksum its vendor publishes (-Sha256 or -Sha1, or a -ChecksumUrl whose text holds one
# line naming the file). When the vendor publishes none, -NoChecksumReason says why and the file's own SHA-256 is written to the note,
# so the next run can pin it. Returns the local path.
function Save-Verified {
    param(
        [Parameter(Mandatory)] [string] $Url, [Parameter(Mandatory)] [string] $OutFile, [string] $Sha256, [string] $Sha1,
        [string] $ChecksumUrl, [string] $ChecksumName, [string] $NoChecksumReason
    )
    New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        Write-RunLog "downloading $Url"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -TimeoutSec 3600
    }
    if ($ChecksumUrl) {
        $text = (Invoke-WebRequest -Uri $ChecksumUrl -UseBasicParsing).Content
        if ($text -is [byte[]]) { $text = [Text.Encoding]::ASCII.GetString($text) }
        $line = @($text -split "`n" | Where-Object { $_ -match [regex]::Escape($ChecksumName) }) | Select-Object -First 1
        if (-not $line) { throw "The checksum file $ChecksumUrl does not name $ChecksumName" }
        $Sha256 = ($line.Trim() -split '\s+')[0]
    }
    $actual256 = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Sha256) {
        if ($actual256 -ne $Sha256.ToLowerInvariant()) { Remove-Item -LiteralPath $OutFile -Force; throw "The download of $Url does not match the published SHA-256 ($Sha256). It was removed." }
        $how = 'SHA-256 matched the vendor''s published checksum'
    } elseif ($Sha1) {
        $actual1 = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA1).Hash.ToLowerInvariant()
        if ($actual1 -ne $Sha1.ToLowerInvariant()) { Remove-Item -LiteralPath $OutFile -Force; throw "The download of $Url does not match the published SHA-1 ($Sha1). It was removed." }
        $how = 'SHA-1 matched the vendor''s published checksum'
    } else {
        $how = "the vendor publishes no checksum ($NoChecksumReason); the file's SHA-256 is $actual256"
    }
    $line = "$([IO.Path]::GetFileName($OutFile)) from $(([Uri]$Url).Host): $how"
    $script:Downloads.Add($line); Write-RunLog $line
    $OutFile
}

# The Authenticode signer of a file, for installers whose vendor publishes no checksum but signs them.
function Assert-SignedBy([string] $File, [string] $SubjectPattern) {
    $sig = Get-AuthenticodeSignature -LiteralPath $File
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch $SubjectPattern) {
        throw "$File is not signed by a publisher matching $SubjectPattern (status $($sig.Status))."
    }
    $line = "$([IO.Path]::GetFileName($File)): Authenticode signature valid, signed by $($sig.SignerCertificate.Subject.Split(',')[0])"
    $script:Downloads.Add($line); Write-RunLog $line
}

# Folder to extract into: the old copy is moved aside (never deleted), so a re-run starts clean.
function Expand-Fresh([string] $Zip, [string] $Destination) {
    if (Test-Path -LiteralPath $Destination) { Rename-Item -LiteralPath $Destination -NewName ((Split-Path $Destination -Leaf) + '.old-' + (Get-Date).ToString('HHmmss')) }
    Expand-Archive -LiteralPath $Zip -DestinationPath $Destination -Force
}

# 7-Zip's console extractor (7zr.exe) from the 7-Zip project's own GitHub release, for the apps that ship a .7z.
function Get-SevenZip {
    $exe = Join-Path (Get-AppFolder 'tools') '7zr.exe'
    if (-not (Test-Path $exe)) {
        $a = Get-GitHubAsset -Repo 'ip7z/7zip' -NamePattern '^7zr\.exe$'
        $null = Save-Verified -Url $a.Url -OutFile $exe -Sha256 $a.Sha256
    }
    $exe
}

# A random value made inside the VM and registered, so Save-State removes it from every recording by exact match.
function New-VmSecret([int] $Bytes = 16) {
    $b = New-Object byte[] $Bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    $value = ([BitConverter]::ToString($b) -replace '-', '').ToLowerInvariant()
    Register-Secret $value
    $value
}

# Keeps a value between scripts in the VM (the media server's login, for Seerr), DPAPI-protected for this user.
function Save-VmSecret([string] $Name, [string] $Value) {
    Add-Type -AssemblyName System.Security
    $protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Value), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    Set-Content -LiteralPath (Join-Path (Get-AppFolder 'secrets') "$Name.bin") -Value ([Convert]::ToBase64String($protected)) -Encoding ascii
    Register-Secret $Value
}
function Read-VmSecret([string] $Name) {
    $file = Join-Path (Get-AppFolder 'secrets') "$Name.bin"
    if (-not (Test-Path $file)) { return $null }
    Add-Type -AssemblyName System.Security
    $value = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String((Get-Content -Raw $file).Trim()), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    Register-Secret $value
    $value
}

# Starts a program hidden, with its output in a log file beside it. Environment variables are set for the child only.
function Start-AppProcess {
    param([Parameter(Mandatory)] [string] $File, [string[]] $Arguments = @(), [string] $WorkingDirectory, [hashtable] $Environment = @{}, [string] $LogName)
    $saved = @{}
    foreach ($k in $Environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$Environment[$k], 'Process') }
    try {
        $args2 = @{ FilePath = $File; WindowStyle = 'Hidden'; PassThru = $true }
        if ($Arguments.Count -gt 0) { $args2.ArgumentList = $Arguments }
        if ($WorkingDirectory) { $args2.WorkingDirectory = $WorkingDirectory }
        if ($LogName) { $log = Join-Path (Get-AppFolder 'logs') $LogName; $args2.RedirectStandardOutput = $log; $args2.RedirectStandardError = $log + '.err' }
        Start-Process @args2
    } finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') } }
}

# Starts a program in the signed-in desktop session (a tray program such as uTorrent or Plex needs one, and PowerShell Direct has none).
# The same way Invoke-GoldenPath.ps1 starts the installer: a scheduled task for the signed-in user, run at once.
function Start-InDesktopSession {
    param([Parameter(Mandatory)] [string] $TaskName, [Parameter(Mandatory)] [string] $File, [string] $Arguments = '', [string] $WorkingDirectory)
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $action = if ($Arguments) { New-ScheduledTaskAction -Execute $File -Argument $Arguments -WorkingDirectory $WorkingDirectory } else { New-ScheduledTaskAction -Execute $File -WorkingDirectory $WorkingDirectory }
    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}
function Remove-DesktopTask([string] $TaskName) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue }

function Stop-AppByName([string[]] $Names) {
    foreach ($n in $Names) { Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
}

# Waits until an address answers (any status below 500 counts, unless -Status is given). Returns the status or $null.
# The status an address answers with, redirects not followed (0 when nothing answers).
function Get-HttpStatus([string] $Url) {
    try {
        $req = [Net.HttpWebRequest]::Create($Url); $req.AllowAutoRedirect = $false; $req.Timeout = 10000; $req.ReadWriteTimeout = 10000
        $res = $req.GetResponse(); $code = [int]$res.StatusCode; $res.Close(); $code
    } catch [Net.WebException] { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } } catch { 0 }
}

# Waits until an address answers (any status below 500 counts, unless -Status is given). Returns the status or $null.
function Wait-Http {
    param([Parameter(Mandatory)] [string] $Url, [int] $Seconds = 120, [int[]] $Status)
    Wait-Until -Seconds $Seconds -EverySeconds 3 {
        $code = Get-HttpStatus $Url
        if ($Status) { if ($Status -contains $code) { $code } } elseif ($code -gt 0 -and $code -lt 500) { $code }
    }
}

# A real public film in the VM as a library: C:\golden\recorded-apps\media\Movies\Sintel (2010)\<file> and a TV show made from the same
# video (two episodes), so a media server has a movie and a series. Copies, never moves: the original is not touched.
function New-MediaLibraryFolders([string] $MediaFile) {
    $root = Get-AppFolder 'media'
    $movie = Join-Path $root 'Movies\Sintel (2010)'
    $show = Join-Path $root 'Shows\Recorded Show (2020)\Season 01'
    New-Item -ItemType Directory -Force -Path $movie, $show | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $movie 'Sintel (2010) [WEB 1080p].mkv'))) { Copy-Item -LiteralPath $MediaFile -Destination (Join-Path $movie 'Sintel (2010) [WEB 1080p].mkv') }
    foreach ($e in 1, 2) {
        $target = Join-Path $show ("Recorded Show (2020) - S01E0$e.mkv")
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $MediaFile -Destination $target }
    }
    [pscustomobject]@{ Root = $root; Movies = (Join-Path $root 'Movies'); Shows = (Join-Path $root 'Shows'); MovieFile = (Join-Path $movie 'Sintel (2010) [WEB 1080p].mkv') }
}

# The downloads line(s) for a recording's note.
function Get-DownloadNote { if ($script:Downloads.Count -gt 0) { ' Installed from: ' + ($script:Downloads -join '; ') + '.' } else { '' } }

# A request sent with several calls at once and saved as one state (used by the call-set recorders below).
# Each call: @{ m = 'GET'; p = '/path'; body = <json object or $null>; text = <raw text>; h = @{ header = value }; form = @{ } }
function Send-Calls {
    param($Client, [object[]] $Calls)
    foreach ($c in $Calls) {
        $a = @{ Client = $Client; Method = $(if ($c.m) { $c.m } else { 'GET' }); Path = $c.p }
        if ($c.h) { $a.Headers = $c.h }
        if ($c.ContainsKey('json')) { $a.Json = $c.json }
        elseif ($c.text) { $a.Body = $c.text; if ($c.type) { $a.ContentType = $c.type } }
        elseif ($c.form) { $a.Form = $c.form }
        Send-Recorded @a
    }
}
