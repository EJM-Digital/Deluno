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
    # Every call to a client is bounded: one call, and one read (a call, and one more after signing in again), in seconds.
    ClientCallSec = 20
    ClientReadSec = 90
    LastCall = $null
    LastCallAt = $null
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
    $specs = @(
        @{ Key = 'small'; Seconds = 125; Rate = 6; Min = 60 },
        @{ Key = 'big'; Seconds = 300; Rate = 10; Min = 300 })
    # A TV episode is found by a search, and the search judges its size against the WEB 1080p episode floor (420 MB for a 45 minute
    # episode, more for a longer one), so the TV episode's video is about 500 MB. Made only when that scenario is chosen.
    if (-not (Get-Command Test-ScenarioSelected -ErrorAction SilentlyContinue) -or (Test-ScenarioSelected 'film.tv-episode')) {
        $specs += @{ Key = 'tv'; Seconds = 125; Rate = 32; Min = 480 }
    }
    foreach ($spec in $specs) {
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
    # A 4:3 film (960x720), the shape of an old film, about 90 MB so Weir does not skip it: a processor must not stretch it or pad it.
    $fourThree = Join-Path (Get-SuiteRoot) 'base\fourthree.mkv'
    if (-not (Test-Path $fourThree)) {
        $ff43 = @('C:\golden\suite\ffmpeg.path') | Where-Object { Test-Path $_ } | ForEach-Object { Get-Content -Raw $_ } | Select-Object -First 1
        if (-not $ff43) { $ff43 = (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Deluno\current\tools\ffmpeg') -Filter ffmpeg.exe -Recurse | Select-Object -First 1).FullName }
        & $ff43 -y -v error -f lavfi -i 'testsrc2=size=960x720:rate=24:duration=125' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=125' -vf 'noise=alls=25:allf=t+u,setsar=1' -c:v mpeg4 -b:v 6M -g 48 -c:a aac -b:a 128k -shortest $fourThree
        if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not make the 4:3 film.' }
    }
    $script:Fx.Base['fourthree'] = $fourThree
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
    Start-Sleep -Seconds 1 # reason: settle after starting or stopping a process; no event to wait on
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
        [ValidateSet('small', 'big', 'fourthree', 'tv')] [string] $Size = 'small',
        [switch] $Extra,
        [string] $Label = 'film',
        # A real title and year (so the metadata has a poster for it), and for a TV episode its S01E01 mark.
        [string] $RealTitle, [int] $RealYear = 2020, [string] $Episode,
        # What follows the year in the release name (the group is added).
        [string] $NameTags = '1080p.WEB-DL.x264'
    )
    $token = New-FilmToken
    $title = ('Gp{0}{1}' -f $Label, $token).Replace('-', '')
    $year = 2020
    $release = "$title.$year.$NameTags-GOLDEN"
    if ($RealTitle) {
        $title = $RealTitle; $year = $RealYear
        $mark = $(if ($Episode) { $Episode } else { "$year" })
        $release = "{0}.{1}.1080p.WEB-DL.x264-GP{2}" -f ($RealTitle -replace '[^A-Za-z0-9]+', '.'), $mark, $token.Replace('-', '')
    }
    $source = Join-Path (Get-SuiteRoot) "films\$release"
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    $base = $script:Fx.Base[$Size]
    $rel = $null
    # A TV episode is offered in the feed's TV category, so the episode's own search finds it.
    $cat = $(if ($Episode) { 5040 } else { 2000 })
    # The data goes where the client itself says it keeps the data of an unfinished torrent, before Deluno sends the torrent. A
    # dummy film has no data, so the client is not asked.
    $placeIn = $null
    if ($Variant -ne 'dummy') { $placeIn = Get-ClientDataFolder -Kind $Client -For $release }
    if ($Variant -eq 'dummy') {
        $rel = Add-SuiteTorrentRelease -Title $release -DummyMb 40
    }
    else {
        if ($Layout -eq 'file') {
            $good = Join-Path $source "$release.mkv"
            New-Item -ItemType HardLink -Path $good -Target $base | Out-Null
            $rel = Add-SuiteTorrentRelease -Title $release -Cat $cat -File $good
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
            $rel = Add-SuiteTorrentRelease -Title $release -Cat $cat -Folder $folder
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
        [switch] $HangLast, [switch] $HangAll, [switch] $Slow,
        # Posts a small featurette.mkv beside the film in the same job (it must stay out of the hand-off, and stay in the client's folder).
        [switch] $Extra
    )
    $token = New-FilmToken
    $title = ('Gp{0}{1}' -f $Label, $token).Replace('-', '')
    $year = 2020
    $release = "$title.$year.1080p.WEB-DL.x264-GOLDEN"
    $dir = Join-Path (Get-SuiteRoot) "films\$release-nzb"
    $releaseArgs = @{ OutDirectory = $dir; Name = $release; SourceFile = $script:Fx.Base[$Size]; MinimumMegabytes = 1; PartSizeKilobytes = 1500 }
    if ($Extra) { $releaseArgs.ExtraVideoFile = $script:Fx.Base['extra'] }
    & (Get-RepoScript 'usenet-stub\New-UsenetRelease.ps1') @releaseArgs | Out-Null
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

# Every torrent client has its data placement confirmed against the client's own answer once it holds the torrent; qBittorrent,
# Deluge, uTorrent and rTorrent are then also told to check it (Transmission checks on its own, and is told to verify only when the
# data had to move).
function Test-ClientNeedsSettle {
    param([Parameter(Mandatory)] [string] $Kind)
    $Kind -in 'qbittorrent', 'transmission', 'deluge', 'utorrent', 'rtorrent'
}

# ---------------------------------------------------------------- asking the clients themselves

function ConvertTo-FxPath {
    param([string] $Path)
    if (-not $Path) { return $null }
    $p = $Path.Replace('/', [string][char]92)
    if ($p.Length -gt 3) { $p = $p.TrimEnd([char]92) }
    $p
}

# ---------------------------------------------------------------- the clients' own APIs (14 Oct run: a hang with no log line)
#
# Every call to a download client's own web interface goes through Invoke-FxHttp, and nothing else in the suite asks a client:
#   - the address must be http://127.0.0.1:<port>. An address that is empty or is a file path is refused before anything is sent: with
#     a file path, Windows PowerShell treats it as a file:// address, ignores -TimeoutSec, and a POST to it waits forever on the
#     file stream while it OVERWRITES the file (the 14 Oct hang: Deluge's call used a variable it did not own, $base, and found the
#     test video's path from the film being made; the call never returned and the video was cut to the 7-byte request body);
#   - each call has a time limit ($script:Fx.ClientCallSec); on expiry the error names the client, the call, the film and what it
#     was waiting for;
#   - a line goes to the suite log before the call (client, call, film), so a hang explains itself. A call repeated within 15 s
#     with the same words is written once (the polling loops would otherwise write a line a second).

# What the keep-seeding group (G10) adds is its own names; the ports and the Transmission session belong to every group.
$script:Ks = @{
    Category    = 'golden-keep-seeding'
    PrivateName = 'Golden private feed'
    # uTorrent and rTorrent are installed by the suite itself (ExtraClients.Lib.ps1, whose pins hold the same two ports).
    Ports       = @{ qbittorrent = 8081; transmission = 9091; deluge = 8112; utorrent = 8087; rtorrent = 8086 }
    # The fields of rTorrent's d.multicall2 the suite reads, in this order (Get-ClientTorrent, Get-KsClientTorrent).
    RtFields    = @('d.hash=', 'd.name=', 'd.complete=', 'd.bytes_done=', 'd.size_bytes=', 'd.is_active=', 'd.is_hash_checking=', 'd.directory=', 'd.is_multi_file=', 'd.custom1=')
    TrSession   = $null
    TrAuth      = $null
}

function Write-FxCall {
    param([string] $Client, [string] $Call, [string] $Subject)
    $sig = "$Client|$Call|$Subject"
    $now = Get-Date
    if ($script:Fx.LastCall -eq $sig -and $script:Fx.LastCallAt -and ($now - $script:Fx.LastCallAt).TotalSeconds -lt 15) { return }
    $script:Fx.LastCall = $sig
    $script:Fx.LastCallAt = $now
    if (Get-Command Write-SuiteLog -ErrorAction SilentlyContinue) {
        Write-SuiteLog ("   asking {0}: {1}{2}" -f $Client, $Call, $(if ($Subject) { " ($Subject)" } else { '' }))
    }
}

# One call to a client's web interface, bounded and logged. Returns what Invoke-RestMethod (or, with -Web, Invoke-WebRequest) returns.
# An HTTP error answer (a status) is rethrown as it is, so the caller can read the status; anything else (no answer, refused,
# timed out) becomes a plain error naming the step.
function Invoke-FxHttp {
    param(
        [Parameter(Mandatory)] [string] $Client,
        [Parameter(Mandatory)] [string] $Call,
        [Parameter(Mandatory)] [string] $Uri,
        [string] $Subject = '',
        [string] $Waiting = 'its web interface to answer',
        [string] $Method,
        $Body,
        [string] $ContentType,
        [hashtable] $Headers,
        $WebSession,
        [switch] $Web
    )
    if ($Uri -notmatch '^http://127\.0\.0\.1:\d+(/|$)') {
        throw ("{0}: the step '{1}'{2} was not sent, because its address is not a local web address ('{3}'). Waiting for {4}." -f $Client, $Call, $(if ($Subject) { " for $Subject" } else { '' }), $Uri, $Waiting)
    }
    $limit = [int]$script:Fx.ClientCallSec
    Write-FxCall -Client $Client -Call $Call -Subject $Subject
    $p = @{ Uri = $Uri; UseBasicParsing = $true; TimeoutSec = $limit }
    if ($Method) { $p.Method = $Method }
    if ($null -ne $Body) { $p.Body = $Body }
    if ($ContentType) { $p.ContentType = $ContentType }
    if ($Headers) { $p.Headers = $Headers }
    if ($WebSession) { $p.WebSession = $WebSession }
    try {
        if ($Web) { Invoke-WebRequest @p } else { Invoke-RestMethod @p }
    }
    catch {
        if ($_.Exception.Response) { throw }
        throw ("{0}: the step '{1}'{2} did not complete within {3} s: waiting for {4}. {5}" -f $Client, $Call, $(if ($Subject) { " for $Subject" } else { '' }), $limit, $Waiting, (Hide-Secrets $_.Exception.Message))
    }
}

# The client's logged-in session (qBittorrent's cookie, Deluge's address and cookie); kept, and made again when -Fresh is asked for.
# Needs nothing from any group: the ports and the saved logins are read here.
function Get-FxClientSession {
    param([Parameter(Mandatory)] [string] $Kind, [switch] $Fresh, [string] $Subject = '')
    if ($Fresh) { $script:Fx.Sessions.Remove($Kind) }
    if (-not $script:Fx.Sessions.ContainsKey($Kind)) {
        $script:Fx.Sessions[$Kind] = switch ($Kind) { 'qbittorrent' { Open-KsQbit -Subject $Subject } 'deluge' { Open-KsDeluge -Subject $Subject } 'utorrent' { Open-KsUTorrent -Subject $Subject } default { $null } }
    }
    $script:Fx.Sessions[$Kind]
}

# Runs a read against a client, which gets the session and the film's name; a session the client no longer knows is made again
# once. $Call names the step for the log and for an error. Every call inside is bounded (Invoke-FxHttp), the read is tried twice at
# most, and a second failure says what each try said. A read that has already used its whole time ($script:Fx.ClientReadSec) is not
# tried again.
function Invoke-FxClientRead {
    param(
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [string] $Call,
        [string] $Subject = '',
        [Parameter(Mandatory)] [scriptblock] $Read
    )
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { return (& $Read (Get-FxClientSession -Kind $Kind -Subject $Subject) $Subject) }
    catch {
        $first = $_.Exception.Message
        if ($sw.Elapsed.TotalSeconds -ge $script:Fx.ClientReadSec) { throw "${Kind}: the step '$Call' for $Subject gave up after $([int]$sw.Elapsed.TotalSeconds) s. $first" }
        try { return (& $Read (Get-FxClientSession -Kind $Kind -Fresh -Subject $Subject) $Subject) }
        catch { throw "${Kind}: the step '$Call' for $Subject failed twice (after signing in again). First: $first Then: $($_.Exception.Message)" }
    }
}

# The login the installer saved for a client, read from its data folders (DPAPI), masked at once.

function Find-KsDataRoots {
    $roots = foreach ($base in $env:ProgramData, $env:LOCALAPPDATA, $env:APPDATA) {
        if ($base -and (Test-Path $base)) { Get-ChildItem -Path $base -Filter 'Deluno*' -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName }
    }
    @($roots)
}

# DPAPI text as Deluno writes it: dpapi:v1:<base64>, entropy "Deluno.Platform.Secrets:<purpose>"; the tray writes it with the machine
# scope, Deluno's own store with the user's, so both are tried.
function Unprotect-KsSecret {
    param([string] $Blob, [string] $Purpose)
    if (-not $Blob -or -not $Blob.StartsWith('dpapi:v1:')) { return $null }
    Add-Type -AssemblyName System.Security
    try { $cipher = [Convert]::FromBase64String($Blob.Substring('dpapi:v1:'.Length)) } catch { return $null }
    $entropy = [Text.Encoding]::UTF8.GetBytes('Deluno.Platform.Secrets:' + $Purpose)
    foreach ($scope in 'LocalMachine', 'CurrentUser') {
        try { return [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($cipher, $entropy, [Security.Cryptography.DataProtectionScope]::$scope)) } catch { }
    }
    $null
}

# The login the installer generated for a client kind ("qbittorrent"), or $null when it made none. The password is masked at once.
function Get-KsComponentLogin {
    param([Parameter(Mandatory)] [string] $Kind)
    foreach ($root in Find-KsDataRoots) {
        $file = Get-ChildItem -Path $root -Filter 'component-credentials.json' -Recurse -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $file) { continue }
        $entries = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        foreach ($name in $entries.PSObject.Properties.Name) {
            if ($name -ne $Kind) { continue }
            $password = Unprotect-KsSecret $entries.$name.Password "bundled-component:$name"
            if ($password) {
                if ($script:S) { $script:S.Secrets["ks-$Kind-password"] = $password; $script:S.Secrets["ks-$Kind-user"] = [string]$entries.$name.Username }
                return [pscustomobject]@{ User = [string]$entries.$name.Username; Password = $password }
            }
        }
    }
    $null
}

function Open-KsQbit {
    param([string] $Subject = '')
    $base = "http://127.0.0.1:$($script:Ks.Ports.qbittorrent)"
    $login = Get-KsComponentLogin -Kind 'qbittorrent'
    if (-not $login) { throw 'qBittorrent''s saved login was not found in the VM, so the scenario cannot read the client.' }
    $sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Invoke-FxHttp -Client 'qbittorrent' -Call 'sign in' -Subject $Subject -Uri "$base/api/v2/auth/login" -Method Post -Body @{ username = $login.User; password = $login.Password } -Headers @{ Referer = $base } -WebSession $sess -Web -Waiting 'the web interface to accept the saved login'
    if ($r.Content -notmatch 'Ok') { throw 'qBittorrent refused the saved login.' }
    [pscustomobject]@{ Base = $base; Session = $sess }
}

# Deluge's web interface, signed in and connected to the daemon. The session is a plain object (address and cookie), not a script
# block: a script block reads $base and $sess from whoever calls it, and found someone else's (the 14 Oct hang).
function Open-KsDeluge {
    param([string] $Subject = '')
    $d = [pscustomobject]@{ Base = ('http://127.0.0.1:' + $script:Ks.Ports.deluge + '/json'); Session = (New-Object Microsoft.PowerShell.Commands.WebRequestSession) }
    # Deluge keeps its shipped login (build-rules.md); the web UI must be connected to the daemon before it answers core calls.
    $login = Invoke-KsDeluge -Session $d -Method 'auth.login' -Params @('deluge') -Subject $Subject
    if ($login.result -ne $true) { throw 'Deluge''s web interface refused its shipped login.' }
    if (-not (Invoke-KsDeluge -Session $d -Method 'web.connected' -Params @() -Subject $Subject).result) {
        $hosts = (Invoke-KsDeluge -Session $d -Method 'web.get_hosts' -Params @() -Subject $Subject).result
        if ($hosts) { Invoke-KsDeluge -Session $d -Method 'web.connect' -Params @($hosts[0][0]) -Subject $Subject | Out-Null; Start-Sleep -Seconds 2 } # reason: lets Deluge reconnect to its host
    }
    $d
}

# One Deluge JSON call on an opened session. Returns the reply ({ result, error, id }).
function Invoke-KsDeluge {
    param([Parameter(Mandatory)] $Session, [Parameter(Mandatory)] [string] $Method, [object[]] $Params = @(), [string] $Subject = '')
    $body = @{ method = $Method; params = $Params; id = 1 } | ConvertTo-Json -Depth 6 -Compress
    Invoke-FxHttp -Client 'deluge' -Call $Method -Subject $Subject -Uri $Session.Base -Method Post -ContentType 'application/json' -Body $body -WebSession $Session.Session -Waiting 'the web interface to answer'
}

# The login the suite made for uTorrent or rTorrent when it installed them (ExtraClients.Lib.ps1); both are masked everywhere.
function Get-KsExtraLogin {
    param([Parameter(Mandatory)] [ValidateSet('utorrent', 'rtorrent')] [string] $Kind)
    [pscustomobject]@{ User = (Get-SuiteSecret "$Kind-user"); Password = (Get-SuiteSecret "$Kind-password") }
}
function Get-KsExtraAuthHeader {
    param([Parameter(Mandatory)] [string] $Kind)
    $login = Get-KsExtraLogin -Kind $Kind
    @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("$($login.User):$($login.Password)")) }
}

# uTorrent's Web UI, signed in: the token page holds the token every later call sends, and sets the cookie it goes with.
function Open-KsUTorrent {
    param([string] $Subject = '')
    $base = 'http://127.0.0.1:' + $script:Ks.Ports.utorrent
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $headers = Get-KsExtraAuthHeader -Kind 'utorrent'
    $page = Invoke-FxHttp -Client 'utorrent' -Call 'token' -Subject $Subject -Uri "$base/gui/token.html" -Headers $headers -WebSession $session -Web
    $token = ([regex]::Match([string]$page.Content, '>([^<>]+)</div>')).Groups[1].Value.Trim()
    if (-not $token) { throw 'uTorrent''s token page held no token (its Web UI login was refused or the page changed).' }
    if ($script:S) { $script:S.Secrets['ks-utorrent-token'] = $token }
    [pscustomobject]@{ Base = $base; Session = $session; Headers = $headers; Token = $token }
}

# One uTorrent Web UI call (the query after gui/?token=...&), on an opened session. Returns the reply parsed, or $null when it is not JSON.
function Invoke-KsUTorrent {
    param([Parameter(Mandatory)] $Session, [Parameter(Mandatory)] [string] $Query, [string] $Subject = '')
    $r = Invoke-FxHttp -Client 'utorrent' -Call ($Query -replace '&.*$', '') -Subject $Subject -Uri "$($Session.Base)/gui/?token=$($Session.Token)&$Query" -Headers $Session.Headers -WebSession $Session.Session -Web
    $text = [string]$r.Content
    if ($text.TrimStart().StartsWith('{')) { $text | ConvertFrom-Json } else { $null }
}

# One XML-RPC value as PowerShell: numbers as long, boolean as bool, an array as an object[] (kept whole), text as text.
function ConvertFrom-KsXmlRpcValue {
    param($Node)
    $child = @($Node.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })[0]
    if (-not $child) { return [string]$Node.InnerText }
    switch ($child.Name) {
        'array' {
            $items = New-Object System.Collections.ArrayList
            foreach ($v in $child.SelectNodes('data/value')) { [void]$items.Add((ConvertFrom-KsXmlRpcValue $v)) }
            return , $items.ToArray()
        }
        { $_ -in 'i4', 'i8', 'int' } { return [long]$child.InnerText }
        'boolean' { return ($child.InnerText -eq '1') }
        default { return [string]$child.InnerText }
    }
}

# One rTorrent XML-RPC call through the web mount in front of it (login made by the suite). Parameters are text, whole numbers or bool.
# A fault is an error naming it.
function Invoke-KsRtorrent {
    param([Parameter(Mandatory)] [string] $Method, [object[]] $Params = @(), [string] $Subject = '')
    $values = foreach ($p in $Params) {
        if ($p -is [bool]) { "<param><value><boolean>$([int]$p)</boolean></value></param>" }
        elseif ($p -is [int] -or $p -is [long]) { "<param><value><i4>$p</i4></value></param>" }
        else { '<param><value><string>' + [Security.SecurityElement]::Escape([string]$p) + '</string></value></param>' }
    }
    $xml = '<?xml version="1.0"?><methodCall><methodName>' + $Method + '</methodName><params>' + (@($values) -join '') + '</params></methodCall>'
    $uri = 'http://127.0.0.1:' + $script:Ks.Ports.rtorrent + '/RPC2'
    $r = Invoke-FxHttp -Client 'rtorrent' -Call $Method -Subject $Subject -Uri $uri -Method Post -ContentType 'text/xml' -Headers (Get-KsExtraAuthHeader -Kind 'rtorrent') -Body $xml -Waiting 'its XML-RPC mount to answer' -Web
    $doc = New-Object System.Xml.XmlDocument
    $doc.LoadXml([string]$r.Content)
    $fault = $doc.SelectSingleNode('/methodResponse/fault')
    if ($fault) { throw "rTorrent answered $Method with a fault: $($fault.InnerText)" }
    ConvertFrom-KsXmlRpcValue $doc.SelectSingleNode('/methodResponse/params/param/value')
}

# Tells uTorrent or rTorrent to check a torrent's files (the way a person presses Force Re-Check / Check hash). Returns $true when sent.
function Invoke-KsRecheck {
    param([Parameter(Mandatory)] [ValidateSet('utorrent', 'rtorrent')] [string] $Kind, [Parameter(Mandatory)] [string] $Hash, [string] $For = '')
    $upper = $Hash.ToUpperInvariant()
    if ($Kind -eq 'utorrent') {
        $null = Invoke-FxClientRead -Kind $Kind -Call 'recheck' -Subject $For -Read { param($u, $for) Invoke-KsUTorrent -Session $u -Query ('action=recheck&hash=' + $upper) -Subject $for }.GetNewClosure()
    }
    else { $null = Invoke-KsRtorrent -Method 'd.check_hash' -Params @($upper) -Subject $For }
    $true
}

function Invoke-KsTransmission {
    param([Parameter(Mandatory)] [string] $Method, [hashtable] $Arguments = @{}, [string] $Subject = '')
    $uri = 'http://127.0.0.1:' + $script:Ks.Ports.transmission + '/transmission/rpc'
    $body = @{ method = $Method; arguments = $Arguments } | ConvertTo-Json -Depth 6 -Compress
    $headers = @{}
    if ($script:Ks.TrSession) { $headers['X-Transmission-Session-Id'] = $script:Ks.TrSession }
    if ($script:Ks.TrAuth) { $headers['Authorization'] = $script:Ks.TrAuth }
    for ($try = 0; $try -lt 3; $try++) {
        try {
            return (Invoke-FxHttp -Client 'transmission' -Call $Method -Subject $Subject -Uri $uri -Method Post -ContentType 'application/json' -Headers $headers -Body $body -Waiting 'its RPC to answer')
        }
        catch {
            $resp = $_.Exception.Response
            if ($resp -and [int]$resp.StatusCode -eq 409) {
                $script:Ks.TrSession = $resp.Headers['X-Transmission-Session-Id']
                if ($script:S -and $script:Ks.TrSession) { $script:S.Secrets['ks-transmission-session'] = $script:Ks.TrSession }
                $headers['X-Transmission-Session-Id'] = $script:Ks.TrSession
                continue
            }
            if ($resp -and [int]$resp.StatusCode -eq 401 -and -not $script:Ks.TrAuth) {
                # Transmission keeps its shipped login unless Deluno saved one for it.
                $login = Get-KsComponentLogin -Kind 'transmission'
                if (-not $login) { throw 'Transmission wants a login and none was saved.' }
                $script:Ks.TrAuth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("$($login.User):$($login.Password)"))
                $headers['Authorization'] = $script:Ks.TrAuth
                continue
            }
            throw
        }
    }
    throw "transmission: the step '$Method' was refused 3 times (its session id or login was not accepted)."
}

# Where the client keeps the data of an unfinished torrent of the films' category, from the client's own settings, to put a film's
# data there before Deluno sends the torrent. Throws when the client will not say.
function Get-ClientDataFolder {
    param([Parameter(Mandatory)] [string] $Kind, [string] $For = '')
    $folder = $null
    switch ($Kind) {
        'qbittorrent' {
            $folder = Invoke-FxClientRead -Kind $Kind -Call 'where it keeps an unfinished torrent' -Subject $For -Read {
                param($q, $for)
                $h = @{ Referer = $q.Base }
                $prefs = Invoke-FxHttp -Client 'qbittorrent' -Call 'preferences' -Subject $for -Uri "$($q.Base)/api/v2/app/preferences" -Headers $h -WebSession $q.Session
                $cats = Invoke-FxHttp -Client 'qbittorrent' -Call 'categories' -Subject $for -Uri "$($q.Base)/api/v2/torrents/categories" -Headers $h -WebSession $q.Session
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
            $folder = Invoke-FxClientRead -Kind $Kind -Call 'core.get_config_value' -Subject $For -Read {
                param($d, $for)
                $r = Invoke-KsDeluge -Session $d -Method 'core.get_config_value' -Params @('download_location') -Subject $for
                if ($r.error) { throw "Deluge answered $($r.error.message)" }
                [string]$r.result
            }
        }
        'transmission' {
            $s = (Invoke-KsTransmission -Method 'session-get' -Arguments @{ fields = @('incomplete-dir', 'incomplete-dir-enabled', 'download-dir') } -Subject $For).arguments
            $folder = if ($s.'incomplete-dir-enabled' -and $s.'incomplete-dir') { $s.'incomplete-dir' } else { $s.'download-dir' }
        }
        'utorrent' {
            $folder = Invoke-FxClientRead -Kind $Kind -Call 'getsettings' -Subject $For -Read {
                param($u, $for)
                $settings = (Invoke-KsUTorrent -Session $u -Query 'action=getsettings' -Subject $for).settings
                $row = @($settings | Where-Object { $_ -and $_[0] -eq 'dir_active_download' })[0]
                if ($row) { [string]$row[2] }
            }
        }
        'rtorrent' { $folder = ConvertFrom-XcWslPath ([string](Invoke-KsRtorrent -Method 'directory.default' -Subject $For)) }
        default { throw "$Kind is not a torrent client." }
    }
    $folder = ConvertTo-FxPath ([string]$folder)
    if (-not $folder) { throw "$Kind did not say where it keeps the data of an unfinished torrent." }
    $folder
}

# What the client itself says about a torrent right now: whether it holds it, whether it is checking its files, how far it is, and the
# folder it is looking in for the data. Never asks Deluno.
function Get-ClientTorrent {
    param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [string] $Hash, [string] $For = '')
    if (-not $For) { $For = "hash $Hash" }
    $none = [pscustomobject]@{ Kind = $Kind; Present = $false; State = $null; Progress = 0.0; Checking = $false; DataFolder = $null; SavePath = $null; DownloadPath = $null; ContentPath = $null }
    $hash = $Hash.ToLowerInvariant()
    switch ($Kind) {
        'qbittorrent' {
            $row = Invoke-FxClientRead -Kind $Kind -Call 'torrents/info' -Subject $For -Read {
                param($q, $for)
                @(Invoke-FxHttp -Client 'qbittorrent' -Call 'torrents/info' -Subject $for -Uri "$($q.Base)/api/v2/torrents/info?hashes=$hash" -Headers @{ Referer = $q.Base } -WebSession $q.Session) | Where-Object { $_ } | Select-Object -First 1
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
            $s = Invoke-FxClientRead -Kind $Kind -Call 'core.get_torrent_status' -Subject $For -Read {
                param($d, $for)
                $r = Invoke-KsDeluge -Session $d -Method 'core.get_torrent_status' -Params @($hash, @('state', 'progress', 'save_path', 'name')) -Subject $for
                if ($r.error) { throw "Deluge answered $($r.error.message)" }
                $r.result
            }
            $has = $s -and @($s.PSObject.Properties).Count -gt 0 -and $s.state
            if (-not $has) { return $none }
            $save = ConvertTo-FxPath ([string]$s.save_path)
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = [string]$s.state; Progress = ([double]$s.progress / 100); Checking = ([string]$s.state -eq 'Checking'); DataFolder = $save; SavePath = $save; DownloadPath = $null; ContentPath = $null }
        }
        'transmission' {
            $r = Invoke-KsTransmission -Method 'torrent-get' -Arguments @{ ids = @($hash); fields = @('hashString', 'status', 'percentDone', 'downloadDir', 'name') } -Subject $For
            $t = @($r.arguments.torrents) | Where-Object { $_ } | Select-Object -First 1
            if (-not $t) { return $none }
            $ses = (Invoke-KsTransmission -Method 'session-get' -Arguments @{ fields = @('incomplete-dir', 'incomplete-dir-enabled') } -Subject $For).arguments
            $progress = [double]$t.percentDone
            $dl = $(if ($ses.'incomplete-dir-enabled' -and $ses.'incomplete-dir') { ConvertTo-FxPath ([string]$ses.'incomplete-dir') } else { $null })
            $save = ConvertTo-FxPath ([string]$t.downloadDir)
            $folder = $(if ($progress -lt 1 -and $dl) { $dl } else { $save })
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = [string]$t.status; Progress = $progress; Checking = ([int]$t.status -in 1, 2); DataFolder = $folder; SavePath = $save; DownloadPath = $dl; ContentPath = $null }
        }
        'utorrent' {
            # The Web UI's list rows are positional: 0 hash, 1 status bits, 2 name, 4 progress in thousandths, 11 label, 26 save path.
            $rows = Invoke-FxClientRead -Kind $Kind -Call 'list' -Subject $For -Read { param($u, $for) (Invoke-KsUTorrent -Session $u -Query 'list=1' -Subject $for).torrents }
            $row = @($rows | Where-Object { $_ -and ([string]$_[0]) -ieq $Hash })[0]
            if (-not $row) { return $none }
            $bits = [int]$row[1]
            $progress = [double]$row[4] / 1000
            $state = $(if ($bits -band 2) { 'checking' } elseif ($bits -band 32) { 'paused' } elseif (-not ($bits -band 1)) { 'stopped' } elseif ($bits -band 64) { 'queued' } elseif ($progress -ge 1) { 'seeding' } else { 'downloading' })
            # A folder torrent's save path names the folder itself; a single file's is the folder it sits in. The suite wants the folder the item is in.
            $save = ConvertTo-FxPath ([string]$row[26])
            $folder = $(if ($save -and (Split-Path $save -Leaf) -ieq [string]$row[2]) { ConvertTo-FxPath (Split-Path $save -Parent) } else { $save })
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = $state; Progress = $progress; Checking = [bool]($bits -band 2); DataFolder = $folder; SavePath = $save; DownloadPath = $null; ContentPath = $null }
        }
        'rtorrent' {
            $rows = Invoke-KsRtorrent -Method 'd.multicall2' -Params (@('', 'main') + $script:Ks.RtFields) -Subject $For
            $row = @($rows | Where-Object { $_ -and ([string]$_[0]) -ieq $Hash })[0]
            if (-not $row) { return $none }
            $progress = $(if ([double]$row[4] -gt 0) { [double]$row[3] / [double]$row[4] } else { 0.0 })
            $checking = ([long]$row[6] -ne 0)
            $state = $(if ($checking) { 'checking' } elseif ([long]$row[5] -eq 0) { 'stopped' } elseif ([long]$row[2] -ne 0) { 'seeding' } else { 'downloading' })
            # d.directory of a folder torrent names the folder itself; a single file's is the folder it sits in. rTorrent says /mnt/c/..., Windows says C:\...
            $save = ConvertTo-FxPath (ConvertFrom-XcWslPath ([string]$row[7]))
            $folder = $(if (([long]$row[8] -ne 0) -and $save) { ConvertTo-FxPath (Split-Path $save -Parent) } else { $save })
            [pscustomobject]@{ Kind = $Kind; Present = $true; State = $state; Progress = $progress; Checking = $checking; DataFolder = $folder; SavePath = $save; DownloadPath = $null; ContentPath = $null }
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
    $asked = @{ Error = $null }
    $w = Wait-Until -TimeoutSec $WaitSec -IntervalSec 1 -Condition {
        try { $i = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release }
        catch { $asked.Error = $_.Exception.Message; throw }
        if ($i.Present -and $i.DataFolder) { $i }
    }
    if (-not $w.Ok) {
        $why = $(if ($asked.Error) { "; the last answer was an error: $(Hide-Secrets $asked.Error)" } else { '' })
        return [pscustomobject]@{ Ok = $false; Moved = $false; Key = $null; Info = $null; Check = $null; Text = "$($Film.ClientKind) does not hold $($Film.Release) (or does not say where it looks) after $WaitSec s$why" }
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
            $idle = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { -not (Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release).Checking }
            if ($idle.Ok) { $info = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release }
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
    if ($IfChanged -and $p.Info.Checking -and $Film.ClientKind -eq 'qbittorrent' -and $p.Info.Progress -le 0) {
        # A check that has not moved for 45 s is told again: the full run of 3 Nov (resend.during.qbittorrent) left qBittorrent in
        # checkingUP at 0.0% for 180 s after the data had moved under its first check, and the scenario passed alone and when run again.
        # The data is verified at the folder the client looks in (above), so a fresh Re-check can only help; it is sent at most once per 45 s.
        if (-not $Film.PSObject.Properties['CheckingSince']) { $Film | Add-Member -NotePropertyName CheckingSince -NotePropertyValue ([DateTime]::UtcNow) -Force }
        if (([DateTime]::UtcNow - $Film.CheckingSince).TotalSeconds -lt 45) { return $false }
        $Film.CheckingSince = [DateTime]::UtcNow
        $again = $false
        try { $again = [bool](Invoke-Recheck -Film $Film) } catch { }
        Write-SuiteLog ("   {0}: still checking at 0.0% after 45 s; Re-check sent again={1}" -f $Film.Release, $again)
        return $again
    }
    if ($Film.PSObject.Properties['CheckingSince'] -and -not $p.Info.Checking) { $Film.PSObject.Properties.Remove('CheckingSince') }
    if ($IfChanged -and ($p.Info.Checking -or $p.Key -eq $Film.SettleKey)) { return $false }
    $ok = $false
    try {
        switch ($Film.ClientKind) {
            'deluge' { $ok = [bool](Invoke-DelugeSettle -Film $Film) }
            'qbittorrent' { $ok = [bool](Invoke-Recheck -Film $Film) }
            'transmission' { if ($p.Moved) { $ok = [bool](Invoke-TransmissionVerify -Film $Film) } }
            { $_ -in 'utorrent', 'rtorrent' } { $ok = [bool](Invoke-KsRecheck -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release) }
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
    if ($Film.Hash) { try { $info = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release } catch { [void]$lines.Add("The client could not be asked: $(Hide-Secrets $_.Exception.Message)") } }
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
        $i = Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release
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
    $d = Get-FxClientSession -Kind 'deluge' -Subject $Film.Release
    $r = Invoke-KsDeluge -Session $d -Method 'core.force_recheck' -Params @(, @($hash)) -Subject $Film.Release
    if ($r.error) { throw "Deluge would not check $($Film.Release): $($r.error.message)" }
    $done = Wait-ClientCheckDone -Film $Film
    Write-SuiteLog ("   {0}: Deluge's check ended: {1}" -f $Film.Release, $(if ($done) { "$($done.State) at $([math]::Round($done.Progress * 100, 1))%" } else { 'it no longer holds the torrent' }))
    $r = Invoke-KsDeluge -Session $d -Method 'core.move_storage' -Params @(@($hash), $Destination) -Subject $Film.Release
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
