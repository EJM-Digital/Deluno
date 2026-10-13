<#
.SYNOPSIS
    The controlled films the scenario suite sends through every client: made from one small test video, offered by the local
    Torznab feed and the Usenet stub, and sent to a chosen client (golden path scenario suite).

.DESCRIPTION
    Dot-source this file after Suite.Lib.ps1. Nothing here touches product code. A torrent film is a hard link of the test video
    named like a release, a torrent hashed over it, and the data placed in the client's incomplete folder first so the client
    finds it and completes at once; a Usenet film is the same video as yEnc articles for the stub news server plus an .nzb.
    Windows PowerShell 5.1.

    Titles are made up (no metadata exists for them), so Deluno never finds a better copy by itself and every send is the
    one the scenario asked for.
#>

$script:Fx = @{
    Feed     = 'C:\golden\feed'
    FeedPort = 8765
    Stub     = 'C:\golden\usenet-stub'
    NewznabPort = 1180
    Hang     = 'C:\golden\suite\hang.txt'
    Slow     = 'C:\golden\suite\slow.txt'
    # The category Deluno puts every film's torrent in (movies). Where a client keeps the data of an unfinished torrent is asked of
    # the client itself (Get-ClientDataFolder, Get-ClientTorrent), never written down here: qBittorrent keeps a torrent of a category in
    # a folder named for the category inside its incomplete folder, and which folder it has is its own to say (rc.30.22, rc.30.30).
    Category = 'deluno-movies'
    Sessions = @{}
    Base     = @{}
    IndexerIds = @{}
    Counter  = 0
}

function Get-RepoScript {
    param([Parameter(Mandatory)] [string] $RelativePath)
    $p = Join-Path 'C:\golden' $RelativePath
    if (-not (Test-Path $p)) { throw "$p is not in the VM." }
    $p
}

# A short unique token for release and title names, so no two films ever collide.
function New-FilmToken {
    $script:Fx.Counter++
    '{0}{1:d2}' -f (-join ((97..122) | Get-Random -Count 4 | ForEach-Object { [char]$_ })), $script:Fx.Counter
}

# Makes (once) the test videos: a small one (about 90 MB, quick for Weir) and a big one (about 375 MB, so Weir is visibly busy
# and a Usenet download takes minutes when it is slowed). Both come from ffmpeg, as the Usenet stub's own film does.
function Initialize-BaseVideos {
    $maker = Get-RepoScript 'usenet-stub\New-UsenetRelease.ps1'
    foreach ($spec in @(
            @{ Key = 'small'; Seconds = 125; Rate = 6; Min = 60 },
            @{ Key = 'big'; Seconds = 300; Rate = 10; Min = 300 })) {
        $dir = Join-Path (Get-SuiteRoot) "base\$($spec.Key)"
        $meta = Join-Path $dir 'release.json'
        if (-not (Test-Path $meta)) {
            & $maker -OutDirectory $dir -Name "Gpbase$($spec.Key).2008.1080p.WEB-DL.x264-GOLDEN" -DurationSeconds $spec.Seconds -VideoMegabitsPerSecond $spec.Rate -MinimumMegabytes $spec.Min | Out-Null
        }
        $info = Get-Content -Raw $meta | ConvertFrom-Json
        $script:Fx.Base[$spec.Key] = (Join-Path $dir $info.file)
    }
    # A tiny extra (a featurette), well under Weir's minimum file size, for the "extra left out of the hand-off" film.
    $extra = Join-Path (Get-SuiteRoot) 'base\extra.mkv'
    if (-not (Test-Path $extra)) {
        $ff = @('C:\golden\suite\ffmpeg.path') | Where-Object { Test-Path $_ } | ForEach-Object { Get-Content -Raw $_ } | Select-Object -First 1
        if (-not $ff) { $ff = (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Deluno\current\tools\ffmpeg') -Filter ffmpeg.exe -Recurse | Select-Object -First 1).FullName }
        & $ff -y -v error -f lavfi -i 'testsrc2=size=640x360:rate=24:duration=8' -f lavfi -i 'sine=frequency=330:sample_rate=48000:duration=8' -c:v mpeg4 -b:v 600k -c:a aac -b:a 64k -shortest $extra
        if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not make the extra.' }
    }
    $script:Fx.Base['extra'] = $extra
    $script:Fx.Base.GetEnumerator() | ForEach-Object { Write-SuiteLog ("   base video {0}: {1:N1} MB" -f $_.Key, ((Get-Item $_.Value).Length / 1MB)) }
}

# Starts the local Torznab feed and the (restartable) news server with hang and slow lists, and adds the feed to Deluno.
function Start-FeedAndStubs {
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    New-Item -ItemType Directory -Force -Path $script:Fx.Feed | Out-Null
    foreach ($f in $script:Fx.Hang, $script:Fx.Slow) { if (-not (Test-Path $f)) { New-Item -ItemType File -Force -Path $f | Out-Null } }

    # The feed.
    $feedArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Get-RepoScript 'feed\Start-TorznabFeed.ps1') + '" -Port ' + $script:Fx.FeedPort + ' -Root "' + $script:Fx.Feed + '"'
    Register-ScheduledTask -TaskName 'GoldenSuiteFeed' -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $feedArgs) -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName 'GoldenSuiteFeed'

    # The news server again, now with the hang and slow lists (the harness started one without them; same port, same articles).
    Stop-ScheduledTask -TaskName 'GoldenPathUsenetNntp' -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'Start-NntpStub' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    $nntpArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Get-RepoScript 'usenet-stub\Start-NntpStub.ps1') + '" -Root "' + (Join-Path $script:Fx.Stub 'release') + '" -Port 1119 -HangFile "' + $script:Fx.Hang + '" -SlowFile "' + $script:Fx.Slow + '" -ArticleDelayMs 2500'
    Register-ScheduledTask -TaskName 'GoldenSuiteNntp' -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $nntpArgs) -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName 'GoldenSuiteNntp'

    $up = Wait-Until -TimeoutSec 40 -IntervalSec 1 -Condition {
        try { (Invoke-WebRequest "http://127.0.0.1:$($script:Fx.FeedPort)/api?t=caps" -UseBasicParsing -TimeoutSec 3).Content -like '*<caps>*' } catch { $false }
    }
    if (-not $up.Ok) { throw 'The Torznab feed did not start.' }
    $nntp = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition {
        try { $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', 1119); $l = (New-Object IO.StreamReader($c.GetStream())).ReadLine(); $c.Close(); $l -like '200*' } catch { $false }
    }
    if (-not $nntp.Ok) { throw 'The news server did not answer after it was restarted.' }
}

# Adds the feed to Deluno as a Torznab indexer and remembers the ids of the feed and of the Newznab stub.
function Initialize-Indexers {
    $feedUrl = "http://127.0.0.1:$($script:Fx.FeedPort)"
    $all = @(Invoke-Deluno -Path '/api/indexers')
    # The public feed: a private one on the same address (the keep-seeding group adds one, #1240) is never the suite's feed.
    $feed = $all | Where-Object { $_.baseUrl -like "$feedUrl*" -and $_.privacy -ne 'private' } | Select-Object -First 1
    if (-not $feed) {
        $feed = Invoke-Deluno -Method POST -Path '/api/indexers' -Body @{
            name = 'Golden feed'; protocol = 'torznab'; privacy = 'public'; baseUrl = $feedUrl; apiKey = 'golden'
            priority = 25; categories = '2000,2040,5000,5040'; mediaScope = 'both'; isEnabled = $true; keepName = $true; routeToLibraries = $true
            # Nothing automatic (#936): the suite sends each release itself, so the feed must never be grabbed from on its own.
            rssEnabled = $false; automaticSearchEnabled = $false
        }
    }
    $script:Fx.IndexerIds.torrent = $feed.id
    $nz = $all | Where-Object { $_.baseUrl -like "http://127.0.0.1:$($script:Fx.NewznabPort)*" } | Select-Object -First 1
    if (-not $nz) { throw 'The Newznab stub is not an indexer in Deluno; the harness points Deluno at it (Set-UsenetStubInDeluno.ps1).' }
    $script:Fx.IndexerIds.usenet = $nz.id
}

function Add-SuiteTorrentRelease {
    param([string] $Title, [int] $Cat = 2000, [string] $File, [string] $Folder, [int] $DummyMb = 0)
    $adder = Get-RepoScript 'feed\Add-Release.ps1'
    $a = @{ Title = $Title; Cat = $Cat; Root = $script:Fx.Feed }
    if ($File) { $a.File = $File } elseif ($Folder) { $a.Folder = $Folder } else { $a.DummyMb = $DummyMb }
    & $adder @a | Out-Null
    $line = Get-Content (Join-Path $script:Fx.Feed 'releases.jsonl') | Where-Object { $_.Trim() } | Select-Object -Last 1 | ConvertFrom-Json
    $line
}

# Damages the last piece of a copy of a file (it is zeroed), so a torrent hashed over the good file is one piece short here.
# -Truncate cuts those bytes off instead: Transmission takes a file of the right size as complete without hashing it (rc.30.24:
# its "short" film read 100% and was imported), so for Transmission the file must be visibly short.
function Copy-FileMissingLastPiece {
    param([string] $From, [string] $To, [int] $Bytes = 300KB, [switch] $Truncate)
    New-Item -ItemType Directory -Force -Path (Split-Path $To) | Out-Null
    Copy-Item -LiteralPath $From -Destination $To -Force
    $fs = [IO.File]::Open($To, 'Open', 'ReadWrite')
    try {
        if ($Truncate) { $fs.SetLength($fs.Length - $Bytes) }
        else { $fs.Seek(-$Bytes, 'End') | Out-Null; $fs.Write((New-Object byte[] $Bytes), 0, $Bytes) }
    } finally { $fs.Dispose() }
}

# Makes a torrent film and puts its data where the client will find it.
#   -Client   qbittorrent | transmission | deluge
#   -Layout   file (one file, saved straight into the category folder) | folder (its own folder, with the film and a sample)
#   -Variant  complete | short (the last piece is missing: stuck just short of the end) | dummy (no data and no peers)
#   -Extra    adds a small featurette to a folder release (it must stay out of the hand-off, and stay in the client's folder)
function New-TorrentFilm {
    param(
        [Parameter(Mandatory)] [string] $Client,
        [ValidateSet('file', 'folder')] [string] $Layout = 'file',
        [ValidateSet('complete', 'short', 'dummy')] [string] $Variant = 'complete',
        [ValidateSet('small', 'big')] [string] $Size = 'small',
        [switch] $Extra,
        [string] $Label = 'film'
    )
    $token = New-FilmToken
    $title = ('Gp{0}{1}' -f $Label, $token).Replace('-', '')
    $year = 2020
    $release = "$title.$year.1080p.WEB-DL.x264-GOLDEN"
    $source = Join-Path (Get-SuiteRoot) "films\$release"
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    $base = $script:Fx.Base[$Size]
    $rel = $null
    # The data goes where the client itself says it keeps the data of an unfinished torrent, before Deluno sends the torrent. A
    # dummy film has no data, so the client is not asked.
    $placeIn = $null
    if ($Variant -ne 'dummy') { $placeIn = Get-ClientDataFolder -Kind $Client }
    if ($Variant -eq 'dummy') {
        $rel = Add-SuiteTorrentRelease -Title $release -DummyMb 40
    }
    else {
        if ($Layout -eq 'file') {
            $good = Join-Path $source "$release.mkv"
            New-Item -ItemType HardLink -Path $good -Target $base | Out-Null
            $rel = Add-SuiteTorrentRelease -Title $release -File $good
            $placeAt = $placeIn
            New-Item -ItemType Directory -Force -Path $placeAt | Out-Null
            if ($Variant -eq 'short') { Copy-FileMissingLastPiece -From $good -To (Join-Path $placeAt "$release.mkv") -Truncate:($Client -eq 'transmission') }
            else { New-Item -ItemType HardLink -Path (Join-Path $placeAt "$release.mkv") -Target $base | Out-Null }
        }
        else {
            $folder = Join-Path $source $release
            New-Item -ItemType Directory -Force -Path $folder, (Join-Path $folder 'Sample') | Out-Null
            New-Item -ItemType HardLink -Path (Join-Path $folder "$release.mkv") -Target $base | Out-Null
            Copy-Item -LiteralPath $script:Fx.Base['extra'] -Destination (Join-Path $folder 'Sample\sample.mkv')
            Set-Content -Path (Join-Path $folder "$release.nfo") -Value 'golden suite release' -Encoding ascii
            if ($Extra) {
                New-Item -ItemType Directory -Force -Path (Join-Path $folder 'Featurettes') | Out-Null
                Copy-Item -LiteralPath $script:Fx.Base['extra'] -Destination (Join-Path $folder 'Featurettes\featurette.mkv')
            }
            $rel = Add-SuiteTorrentRelease -Title $release -Folder $folder
            $dest = Join-Path $placeIn $release
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object {
                $rp = $_.FullName.Substring($folder.Length + 1)
                $to = Join-Path $dest $rp
                New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
                if ($_.Name -eq "$release.mkv") {
                    if ($Variant -eq 'short') { Copy-FileMissingLastPiece -From $_.FullName -To $to -Truncate:($Client -eq 'transmission') } else { New-Item -ItemType HardLink -Path $to -Target $base | Out-Null }
                } else { Copy-Item -LiteralPath $_.FullName -Destination $to }
            }
        }
    }
    # What was placed: the item the client looks for by name, and every file in it with its size, so a later check can say whether
    # the files are there and whole.
    $item = $(if ($Layout -eq 'file') { "$release.mkv" } else { $release })
    $files = @()
    if ($placeIn) { $files = @(Get-FilmFilesIn -Folder $placeIn -Item $item) }
    [pscustomobject]@{
        Title = $title; Year = $year; Release = $release; Protocol = 'torrent'; Client = $Client; Layout = $Layout; Size = $rel.size
        DataFolder = $placeIn; DataItem = $item; DataFiles = $files; SettleKey = $null
        Hash = $rel.infohash; Url = "http://127.0.0.1:$($script:Fx.FeedPort)/dl/$($rel.torrent)"; IndexerId = $script:Fx.IndexerIds.torrent; Folder = $(if ($Layout -eq 'folder') { $release } else { $null })
    }
}

# Makes a Usenet film: the test video as articles on the stub news server and an .nzb the feed serves. -HangLast leaves the last
# article unanswered (stuck at 99.99%); -Slow answers every article after a pause (a crawl); -HangAll answers none (stuck at 0 bytes).
function New-UsenetFilm {
    param(
        [ValidateSet('small', 'big')] [string] $Size = 'small',
        [string] $Label = 'film',
        [switch] $HangLast, [switch] $HangAll, [switch] $Slow
    )
    $token = New-FilmToken
    $title = ('Gp{0}{1}' -f $Label, $token).Replace('-', '')
    $year = 2020
    $release = "$title.$year.1080p.WEB-DL.x264-GOLDEN"
    $dir = Join-Path (Get-SuiteRoot) "films\$release-nzb"
    & (Get-RepoScript 'usenet-stub\New-UsenetRelease.ps1') -OutDirectory $dir -Name $release -SourceFile $script:Fx.Base[$Size] -MinimumMegabytes 1 -PartSizeKilobytes 1500 | Out-Null
    $articles = Join-Path $dir 'articles'
    Copy-Item -Path (Join-Path $articles '*.art') -Destination (Join-Path $script:Fx.Stub 'release\articles') -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $script:Fx.Feed 'torrents') | Out-Null
    $nzbFile = "$release.nzb"
    Copy-Item -LiteralPath (Join-Path $dir $nzbFile) -Destination (Join-Path $script:Fx.Feed "torrents\$nzbFile") -Force
    $ids = @(Get-ChildItem -LiteralPath $articles -Filter '*.art' | ForEach-Object { $_.BaseName })
    # The last article is the last <segment> of the .nzb (the files are read in order).
    $nzbText = Get-Content -Raw (Join-Path $dir $nzbFile)
    $segs = [regex]::Matches($nzbText, '<segment[^>]*>([^<]+)</segment>')
    $last = $segs[$segs.Count - 1].Groups[1].Value.Trim()
    if ($HangLast) { Add-Content -Path $script:Fx.Hang -Value $last -Encoding ascii }
    if ($HangAll) { Add-Content -Path $script:Fx.Hang -Value $ids -Encoding ascii }
    if ($Slow) { Add-Content -Path $script:Fx.Slow -Value $ids -Encoding ascii }
    $meta = Get-Content -Raw (Join-Path $dir 'release.json') | ConvertFrom-Json
    [pscustomobject]@{
        Title = $title; Year = $year; Release = $release; Protocol = 'usenet'; Client = $null; Layout = 'folder'; Size = [long]$meta.sizeBytes
        Url = "http://127.0.0.1:$($script:Fx.FeedPort)/dl/$nzbFile"; IndexerId = $script:Fx.IndexerIds.usenet; Folder = $release
    }
}

# Which client Deluno has for a kind ("qbittorrent", "sabnzbd", ...).
function Get-ClientByKind {
    param([Parameter(Mandatory)] [string] $Kind)
    $c = Get-DownloadClients | Where-Object { $_.protocol -eq $Kind } | Select-Object -First 1
    if (-not $c) { throw "Deluno has no $Kind connection." }
    $c
}

# Routes the film library to exactly one client, so the next send goes where the scenario means it to.
function Set-LibraryClient {
    param([Parameter(Mandatory)] [string] $Kind, [string] $MediaType = 'movies')
    $lib = Get-Library -MediaType $MediaType
    $client = Get-ClientByKind -Kind $Kind
    $sources = @()
    foreach ($id in @($script:Fx.IndexerIds.torrent, $script:Fx.IndexerIds.usenet, $script:Fx.IndexerIds.private) | Where-Object { $_ }) { $sources += @{ indexerId = $id; priority = ($sources.Count + 1); requiredTags = ''; excludedTags = '' } }
    Invoke-Deluno -Method PUT -Path "/api/libraries/$($lib.id)/routing" -Body @{ sources = $sources; downloadClients = @(@{ downloadClientId = $client.id; priority = 1 }) } | Out-Null
    $client
}

# Adds the film to Deluno (once) and sends its release to the chosen client, as "Send anyway" on the title's page does.
# Returns the film with SentUtc, MovieId and the client's id.
function Send-Film {
    param(
        [Parameter(Mandatory)] $Film,
        [Parameter(Mandatory)] [string] $Kind,
        [string] $Quality = 'WEB 1080p'
    )
    $client = Set-LibraryClient -Kind $Kind
    $movie = Invoke-Deluno -Method POST -Path '/api/movies' -Body @{ title = $Film.Title; releaseYear = $Film.Year; imdbId = $null; monitored = $true }
    $sent = [DateTime]::UtcNow
    $r = Try-Deluno -Method POST -Path "/api/movies/$($movie.id)/grab" -Body @{
        releaseName = $Film.Release; indexerId = $Film.IndexerId; indexerName = $(if ($Film.IndexerName) { $Film.IndexerName } elseif ($Film.Protocol -eq 'usenet') { 'Golden Newznab stub' } else { 'Golden feed' })
        candidateQuality = $Quality; downloadUrl = $Film.Url; sizeBytes = $Film.Size; seeders = 50; force = $true
        overrideReason = 'Golden scenario suite: a controlled release'; duplicateAction = $null
    }
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "Sending $($Film.Release) to $Kind answered $($r.Status). $($r.Raw)") }
    $Film | Add-Member -NotePropertyName SentUtc -NotePropertyValue $sent -Force
    $Film | Add-Member -NotePropertyName MovieId -NotePropertyValue $movie.id -Force
    $Film | Add-Member -NotePropertyName ClientId -NotePropertyValue $client.id -Force
    $Film | Add-Member -NotePropertyName ClientKind -NotePropertyValue $Kind -Force
    Write-SuiteLog ("   sent {0} to {1}" -f $Film.Release, $Kind)
    # The data was put where the client said it keeps an unfinished torrent. Once the client holds the torrent its own answer is read
    # again, the files are checked there (and moved if the client looks elsewhere), and only then is the client told to check them.
    if (Test-ClientNeedsSettle -Kind $Kind) { Invoke-FilmSettle -Film $Film -WaitForRowSec 60 | Out-Null }
    $Film
}

# Every torrent client has its data placement confirmed against the client's own answer once it holds the torrent; qBittorrent and
# Deluge are then also told to check it (Transmission checks on its own, and is told to verify only when the data had to move).
function Test-ClientNeedsSettle {
    param([Parameter(Mandatory)] [string] $Kind)
    $Kind -in 'qbittorrent', 'transmission', 'deluge'
}

# ---------------------------------------------------------------- asking the clients themselves

function ConvertTo-FxPath {
    param([string] $Path)
    if (-not $Path) { return $null }
    $p = $Path.Replace('/', [string][char]92)
    if ($p.Length -gt 3) { $p = $p.TrimEnd([char]92) }
    $p
}

# The client's logged-in session (qBittorrent's cookie, Deluge's caller); kept, and made again when -Fresh is asked for.
function Get-FxClientSession {
    param([Parameter(Mandatory)] [string] $Kind, [switch] $Fresh)
    if ($Fresh) { $script:Fx.Sessions.Remove($Kind) }
    if (-not $script:Fx.Sessions.ContainsKey($Kind)) {
        $script:Fx.Sessions[$Kind] = switch ($Kind) { 'qbittorrent' { Open-KsQbit } 'deluge' { Open-KsDeluge } default { $null } }
    }
    $script:Fx.Sessions[$Kind]
}

# Runs a read against a client; a session the client no longer knows is made again once.
function Invoke-FxClientRead {
    param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [scriptblock] $Read)
    try { return (& $Read (Get-FxClientSession -Kind $Kind)) }
    catch { return (& $Read (Get-FxClientSession -Kind $Kind -Fresh)) }
}

# Where the client keeps the data of an unfinished torrent of the films' category, from the client's own settings, to put a film's
# data there before Deluno sends the torrent. Throws when the client will not say.
function Get-ClientDataFolder {
    param([Parameter(Mandatory)] [string] $Kind)
    $folder = $null
    switch ($Kind) {
        'qbittorrent' {
            $folder = Invoke-FxClientRead -Kind $Kind -Read {
                param($q)
                $h = @{ Referer = $q.Base }
                $prefs = Invoke-RestMethod -Uri "$($q.Base)/api/v2/app/preferences" -Headers $h -WebSession $q.Session -UseBasicParsing -TimeoutSec 20
                $cats = Invoke-RestMethod -Uri "$($q.Base)/api/v2/torrents/categories" -Headers $h -WebSession $q.Session -UseBasicParsing -TimeoutSec 20
                $cat = $cats.PSObject.Properties | Where-Object { $_.Name -eq $script:Fx.Category } | ForEach-Object { $_.Value } | Select-Object -First 1
                # The category's own incomplete folder if it has one; else the incomplete folder with the category's name inside it
                # (automatic management); else the folder the category saves to.
                if ($cat -and $cat.download_path -is [string] -and $cat.download_path.Trim()) { $cat.download_path }
                elseif ($prefs.temp_path_enabled -and $prefs.temp_path) { Join-Path (ConvertTo-FxPath $prefs.temp_path) $script:Fx.Category }
                elseif ($cat -and $cat.savePath) { $cat.savePath }
                else { $prefs.save_path }
            }
        }
        'deluge' {
            $folder = Invoke-FxClientRead -Kind $Kind -Read {
                param($call)
                $r = & $call 'core.get_config_value' @('download_location')
                if ($r.error) { throw "Deluge answered $($r.error.message)" }
                [string]$r.result
            }
        }
        'transmission' {
            $s = (Invoke-KsTransmission -Method 'session-get' -Arguments @{ fields = @('incomplete-dir', 'incomplete-dir-enabled', 'download-dir') }).arguments
            $folder = if ($s.'incomplete-dir-enabled' -and $s.'incomplete-dir') { $s.'incomplete-dir' } else { $s.'download-dir' }
        }
        default { throw "$Kind is not a torrent client." }
    }
    $folder = ConvertTo-FxPath ([string]$folder)
    if (-not $folder) { throw "$Kind did not say where it keeps the data of an unfinished torrent." }
    $folder
}

# What the client itself says about a torrent right now: whether it holds it, whether it is checking its files, how far it is, and the
# folder it is looking in for the data. Never asks Deluno.
function Get-ClientTorrent {
    param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [string] $Hash)
    $none = [pscustomobject]@{ Kind = $Kind; Present = $false; State = $null; Progress = 0.0; Checking = $false; DataFolder = $null; SavePath = $null; DownloadPath = $null; ContentPath = $null }
    $hash = $Hash.ToLowerInvariant()
    switch ($Kind) {
        'qbittorrent' {
            $row = Invoke-FxClientRead -Kind $Kind -Read {
                param($q)
                @(Invoke-RestMethod -Uri "$($q.Base)/api/v2/torrents/info?hashes=$hash" -Headers @{ Referer = $q.Base } -WebSession $q.Session -UseBasicParsing -TimeoutSec 20) | Where-Object { $_ } | Select-Object -First 1
            }
            if (-not $row) { return $none }
            $progress = [double]$row.progress
            $dl = $(if ($row.download_path -is [string]) { ConvertTo-FxPath $row.download_path } else { $null })
            $save = ConvertTo-FxPath ([string]$row.save_path)
            # An unfinished torrent is kept in its incomplete folder when it has one; a finished one is in its save path.
            $folder = $(if ($progress -lt 1 -and $dl) { $dl } else { $save })
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = [string]$row.state; Progress = $progress; Checking = ([string]$row.state -like 'checking*'); DataFolder = $folder; SavePath = $save; DownloadPath = $dl; ContentPath = (ConvertTo-FxPath ([string]$row.content_path)) }
        }
        'deluge' {
            $s = Invoke-FxClientRead -Kind $Kind -Read {
                param($call)
                $r = & $call 'core.get_torrent_status' @($hash, @('state', 'progress', 'save_path', 'name'))
                if ($r.error) { throw "Deluge answered $($r.error.message)" }
                $r.result
            }
            $has = $s -and @($s.PSObject.Properties).Count -gt 0 -and $s.state
            if (-not $has) { return $none }
            $save = ConvertTo-FxPath ([string]$s.save_path)
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = [string]$s.state; Progress = ([double]$s.progress / 100); Checking = ([string]$s.state -eq 'Checking'); DataFolder = $save; SavePath = $save; DownloadPath = $null; ContentPath = $null }
        }
        'transmission' {
            $r = Invoke-KsTransmission -Method 'torrent-get' -Arguments @{ ids = @($hash); fields = @('hashString', 'status', 'percentDone', 'downloadDir', 'name') }
            $t = @($r.arguments.torrents) | Where-Object { $_ } | Select-Object -First 1
            if (-not $t) { return $none }
            $ses = (Invoke-KsTransmission -Method 'session-get' -Arguments @{ fields = @('incomplete-dir', 'incomplete-dir-enabled') }).arguments
            $progress = [double]$t.percentDone
            $dl = $(if ($ses.'incomplete-dir-enabled' -and $ses.'incomplete-dir') { ConvertTo-FxPath ([string]$ses.'incomplete-dir') } else { $null })
            $save = ConvertTo-FxPath ([string]$t.downloadDir)
            $folder = $(if ($progress -lt 1 -and $dl) { $dl } else { $save })
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = [string]$t.status; Progress = $progress; Checking = ([int]$t.status -in 1, 2); DataFolder = $folder; SavePath = $save; DownloadPath = $dl; ContentPath = $null }
        }
        default { throw "$Kind is not a torrent client." }
    }
}

# ---------------------------------------------------------------- the data on disk

# Every file of an item (a single file, or a folder with everything in it) under a folder, as relative path and size.
function Get-FilmFilesIn {
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] [string] $Item)
    $p = Join-Path $Folder $Item
    if (-not (Test-Path -LiteralPath $p)) { return @() }
    $it = Get-Item -LiteralPath $p
    if (-not $it.PSIsContainer) { return @([pscustomobject]@{ Rel = $Item; Length = [long]$it.Length }) }
    $root = $Folder.TrimEnd([char]92)
    @(Get-ChildItem -LiteralPath $p -Recurse -File | ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($root.Length + 1); Length = [long]$_.Length } })
}

# Whether every file the suite placed for a film is in a folder, and whole (the size it was placed at). A film with no data (a
# dummy) expects nothing.
function Test-FilmDataAt {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] [string] $Folder)
    $expected = @($Film.DataFiles | Where-Object { $_ })
    $missing = @(); $wrong = @()
    foreach ($f in $expected) {
        $p = Join-Path $Folder $f.Rel
        if (-not (Test-Path -LiteralPath $p)) { $missing += $f.Rel; continue }
        $len = (Get-Item -LiteralPath $p).Length
        if ($len -ne $f.Length) { $wrong += ('{0} is {1} bytes, not {2}' -f $f.Rel, $len, $f.Length) }
    }
    $problems = @()
    if ($missing) { $problems += 'missing ' + ($missing -join ', ') }
    if ($wrong) { $problems += 'wrong size: ' + ($wrong -join '; ') }
    $text = $(if ($expected.Count -eq 0) { "nothing is expected in $Folder" }
        elseif (-not $problems) { "all $($expected.Count) file(s) are in $Folder and whole" }
        else { "in ${Folder}: " + ($problems -join '; ') })
    [pscustomobject]@{ Ok = (-not $problems); Folder = $Folder; Expected = $expected.Count; Missing = $missing; WrongSize = $wrong; Text = $text }
}

# Moves the film's data (the item the client looks for) from the folder it was last put in to another. Returns whether it moved.
function Move-FilmDataToFolder {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] [string] $Folder)
    $from = ConvertTo-FxPath $Film.DataFolder
    $to = ConvertTo-FxPath $Folder
    if (-not $from -or $from -ieq $to -or -not $Film.DataItem) { return $false }
    $placed = Join-Path $from $Film.DataItem
    if (-not (Test-Path -LiteralPath $placed)) { return $false }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    $target = Join-Path $to $Film.DataItem
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Move-Item -LiteralPath $placed -Destination $target
    $Film | Add-Member -NotePropertyName DataFolder -NotePropertyValue $to -Force
    Write-SuiteLog ("   {0}: the client looks in {1}, not {2}; the data was moved there." -f $Film.Release, $to, $from)
    $true
}

# Waits until the client holds the torrent and says where it looks, checks the film's files are there whole and moves them when
# they are not, and says what it found. -WaitSec 0 reads the client once. Ok is true only when the files are verified at the
# folder the client names, and then Key names that verified state (folder and result), so a later look can tell whether anything
# changed since the client was last told to check.
function Resolve-FilmPlacement {
    param([Parameter(Mandatory)] $Film, [int] $WaitSec = 60)
    $w = Wait-Until -TimeoutSec $WaitSec -IntervalSec 1 -Condition {
        $i = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash
        if ($i.Present -and $i.DataFolder) { $i }
    }
    if (-not $w.Ok) {
        return [pscustomobject]@{ Ok = $false; Moved = $false; Key = $null; Info = $null; Check = $null; Text = "$($Film.ClientKind) does not hold $($Film.Release) (or does not say where it looks) after $WaitSec s" }
    }
    $info = $w.Value
    $check = Test-FilmDataAt -Film $Film -Folder $info.DataFolder
    $moved = $false
    if (-not $check.Ok) {
        # Files the client is reading cannot be moved: wait for its check to end, on its own word (a single look leaves them alone).
        if ($info.Checking -and $WaitSec -le 0) {
            return [pscustomobject]@{ Ok = $false; Moved = $false; Key = $null; Info = $info; Check = $check; Text = "$($Film.ClientKind) is checking its files; they are left where they are ($($check.Text))" }
        }
        if ($info.Checking) {
            $idle = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { -not (Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash).Checking }
            if ($idle.Ok) { $info = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash }
        }
        try { $moved = Move-FilmDataToFolder -Film $Film -Folder $info.DataFolder }
        catch { Write-SuiteLog ("   {0}: could not move the data to where the client looks: {1}" -f $Film.Release, (Hide-Secrets $_.Exception.Message)) }
        $check = Test-FilmDataAt -Film $Film -Folder $info.DataFolder
    }
    [pscustomobject]@{
        Ok = $check.Ok; Moved = $moved; Info = $info; Check = $check
        Key = $(if ($check.Ok) { '{0}|verified' -f $info.DataFolder.ToLowerInvariant() } else { $null })
        Text = $check.Text
    }
}

# Puts a film's data where its client looks, verifies it, and then tells the client to check it: Re-check for qBittorrent,
# force_recheck plus move_storage for Deluge, torrent-verify for Transmission (only when the data had to move; it checks on its
# own otherwise). Nothing is sent until the files are verified at the client's own folder. With -IfChanged nothing is sent unless
# that verified state differs from the one the client was last told to check, and not while the client is checking. Returns
# whether the client was told to check.
function Invoke-FilmSettle {
    param([Parameter(Mandatory)] $Film, [int] $WaitForRowSec = 0, [switch] $IfChanged)
    if (-not (Test-ClientNeedsSettle -Kind $Film.ClientKind)) { return $false }
    if ($WaitForRowSec -gt 0 -and $Film.ClientKind -eq 'qbittorrent') {
        $w = Wait-Until -TimeoutSec $WaitForRowSec -IntervalSec 2 -Condition { Find-QueueRow -Name $Film.Release }
        if (-not $w.Ok) { Write-SuiteLog ("   {0}: no queue row to re-check after {1} s" -f $Film.Release, $WaitForRowSec); return $false }
    }
    $p = Resolve-FilmPlacement -Film $Film -WaitSec $(if ($IfChanged) { 0 } else { 60 })
    if (-not $p.Ok) {
        if (-not $IfChanged) { Write-SuiteLog ("   {0}: the client is not told to check yet: {1}" -f $Film.Release, $p.Text) }
        return $false
    }
    if ($IfChanged -and ($p.Info.Checking -or $p.Key -eq $Film.SettleKey)) { return $false }
    $ok = $false
    try {
        switch ($Film.ClientKind) {
            'deluge' { $ok = [bool](Invoke-DelugeSettle -Film $Film) }
            'qbittorrent' { $ok = [bool](Invoke-Recheck -Film $Film) }
            'transmission' { if ($p.Moved) { $ok = [bool](Invoke-TransmissionVerify -Film $Film) } }
        }
    }
    catch { Write-SuiteLog ("   {0}: the settle step failed: {1}" -f $Film.Release, (Hide-Secrets $_.Exception.Message)) }
    # Only a check that was sent counts as "told": a failed send is tried again on the next look. Transmission checks on its own, so
    # the verified state is remembered whether or not it was told.
    if ($ok -or $Film.ClientKind -eq 'transmission') { $Film | Add-Member -NotePropertyName SettleKey -NotePropertyValue $p.Key -Force }
    Write-SuiteLog ("   {0}: {1} sent={2}; {3}" -f $Film.Release, $(if ($Film.ClientKind -eq 'deluge') { 'force_recheck + move_storage' } elseif ($Film.ClientKind -eq 'transmission') { 'verify' } else { 'Re-check' }), $ok, $p.Text)
    $ok
}

# What a film that did not finish needs said: where the client looks, where the data was last put, and whether the files are there
# and whole in each.
function Get-FilmPlacementReport {
    param([Parameter(Mandatory)] $Film)
    $lines = New-Object System.Collections.ArrayList
    $info = $null
    if ($Film.Hash) { try { $info = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash } catch { [void]$lines.Add("The client could not be asked: $(Hide-Secrets $_.Exception.Message)") } }
    if ($info -and $info.Present) {
        [void]$lines.Add(("{0} looks for the data in {1} (save path {2}, incomplete path {3}, content path {4}); it is {5} at {6:N1}%." -f $Film.ClientKind, $info.DataFolder, $info.SavePath, $info.DownloadPath, $info.ContentPath, $info.State, ($info.Progress * 100)))
        [void]$lines.Add('At the folder the client looks in: ' + (Test-FilmDataAt -Film $Film -Folder $info.DataFolder).Text)
    }
    elseif ($info) { [void]$lines.Add("$($Film.ClientKind) does not hold the torrent.") }
    if ($Film.DataFolder) { [void]$lines.Add('Where the suite last put the data: ' + (Test-FilmDataAt -Film $Film -Folder $Film.DataFolder).Text) }
    [void]$lines.Add("Last told to check at: $(if ($Film.SettleKey) { $Film.SettleKey } else { 'never' })")
    @($lines) -join "`n"
}

# What a failed film needs in its evidence: the client's queue row, where the client looks and where the data is, and what is in the
# client's folders.
function Save-FilmClientEvidence {
    param([Parameter(Mandatory)] $Film)
    try {
        $row = Find-QueueRow -Name $Film.Release -IncludeHistory
        $report = $(try { Get-FilmPlacementReport -Film $Film } catch { "placement could not be read: $($_.Exception.Message)" })
        $folders = foreach ($root in 'C:\Downloads\Incomplete', 'C:\Downloads\Completed') {
            if (Test-Path $root) { Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 200 | ForEach-Object { '{0}  {1}' -f $_.FullName, $(if ($_.PSIsContainer) { '<dir>' } else { "$($_.Length) bytes" }) } }
        }
        Save-Evidence -Name ("film-client-{0}-{1}.txt" -f $Film.ClientKind, $Film.Title) -Text ("placement:`n{0}`n`nqueue row:`n{1}`n`nfolders:`n{2}" -f $report, ($row | ConvertTo-Json -Depth 4), ($folders -join "`n"))
    } catch { }
}

# Deluno's Re-check action on a client's entry (a client that did not take the data first time round).
function Invoke-Recheck {
    param([Parameter(Mandatory)] $Film)
    $row = Find-QueueRow -Name $Film.Release
    if (-not $row) { return $false }
    $r = Try-Deluno -Method POST -Path "/api/download-clients/$($row.clientId)/queue/actions" -Body @{ action = 'recheck'; queueItemId = $row.id }
    $r.Status -ge 200 -and $r.Status -lt 300
}

# Transmission checks its files on its own; when the data had to be moved to where it looks, it is told to verify.
function Invoke-TransmissionVerify {
    param([Parameter(Mandatory)] $Film)
    Invoke-KsTransmission -Method 'torrent-verify' -Arguments @{ ids = @($Film.Hash.ToLowerInvariant()) } | Out-Null
    $true
}

# Waits until a client's check of a torrent has ended, on the client's own word: it was seen checking and no longer is, or the
# torrent is whole. Returns the client's last reading ($null when it never held the torrent).
function Wait-ClientCheckDone {
    param([Parameter(Mandatory)] $Film, [int] $TimeoutSec = 30)
    $state = @{ Seen = $false; Last = $null }
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 0.5 -Condition {
        $i = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash
        $state.Last = $i
        if (-not $i.Present) { return $null }
        if ($i.Checking) { $state.Seen = $true; return $null }
        if ($state.Seen -or $i.Progress -ge 1) { $i }
    }
    if ($w.Ok) { $w.Value } else { $state.Last }
}

# Deluge only moves a finished torrent on a completion event, so a torrent that finds its data already there is rechecked and
# moved by hand, which is what its Label plugin would have done (core.force_recheck, core.move_storage). The move waits for the
# check to end on Deluge's own word.
function Invoke-DelugeSettle {
    param([Parameter(Mandatory)] $Film, [string] $Destination = 'C:\Downloads\Completed\Movies')
    $hash = $Film.Hash.ToLowerInvariant()
    $call = Get-FxClientSession -Kind 'deluge'
    $r = & $call 'core.force_recheck' @(, @($hash))
    if ($r.error) { throw "Deluge would not check $($Film.Release): $($r.error.message)" }
    $done = Wait-ClientCheckDone -Film $Film
    Write-SuiteLog ("   {0}: Deluge's check ended: {1}" -f $Film.Release, $(if ($done) { "$($done.State) at $([math]::Round($done.Progress * 100, 1))%" } else { 'it no longer holds the torrent' }))
    $r = & $call 'core.move_storage' @(@($hash), $Destination)
    if ($r.error) { throw "Deluge would not move $($Film.Release): $($r.error.message)" }
    $true
}

# Waits until the client reports the film finished and returns the queue/history row. A torrent that is not finishing is looked at
# again when what the client says changes (it names another folder, or the files are not where it looks), never on a timer: the
# client is told to check only once the files are verified at the folder it names.
function Wait-FilmFinished {
    param([Parameter(Mandatory)] $Film, [int] $TimeoutSec = 180)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 2 -Condition {
        $row = Find-QueueRow -Name $Film.Release -IncludeHistory
        if (-not $row) {
            # A finished Usenet job is no longer in the queue, but its hand-off proves it finished.
            $h = Find-Handoffs -Name $Film.Release | Select-Object -First 1
            if ($h) { return $h }
            return $null
        }
        if ($row.isHistory) { return $row }
        if ($row.status -in 'completed', 'processing', 'processed', 'waitingForProcessor', 'importReady', 'importQueued', 'imported' -or [double]$row.progress -ge 100) { return $row }
        if ($Film.Protocol -eq 'torrent' -and (Test-ClientNeedsSettle -Kind $Film.ClientKind)) {
            Invoke-FilmSettle -Film $Film -IfChanged | Out-Null
        }
        $null
    }
    if (-not $w.Ok) {
        Save-FilmClientEvidence -Film $Film
        if ($Film.Protocol -eq 'torrent') {
            $explain = $(try { Get-FilmPlacementReport -Film $Film } catch { "placement could not be read: $($_.Exception.Message)" })
            Add-Note ("$($Film.Release) did not finish in $($Film.ClientKind) in $TimeoutSec s. " + ($explain -replace "`n", ' '))
            $w | Add-Member -NotePropertyName Explain -NotePropertyValue $explain -Force
        }
    }
    $w
}
