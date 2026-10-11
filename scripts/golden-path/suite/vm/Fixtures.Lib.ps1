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
    # Where each client looks for a torrent's data while it is still "incomplete". qBittorrent, with automatic management on and
    # an incomplete folder, keeps a torrent of a category in a folder named for the category inside it (rc.30.22: the queue row's
    # sourcePath read ...\qBittorrent\deluno-movies\<release>, and data put one level up was never found, so no qBittorrent film
    # ever finished). The others use the folder itself.
    Incomplete = @{ qbittorrent = 'C:\Downloads\Incomplete\qBittorrent\deluno-movies'; transmission = 'C:\Downloads\Incomplete\Transmission'; deluge = 'C:\Downloads\Incomplete\Deluge' }
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
    $feed = $all | Where-Object { $_.baseUrl -like "$feedUrl*" } | Select-Object -First 1
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
function Copy-FileMissingLastPiece {
    param([string] $From, [string] $To, [int] $Bytes = 300KB)
    New-Item -ItemType Directory -Force -Path (Split-Path $To) | Out-Null
    Copy-Item -LiteralPath $From -Destination $To -Force
    $fs = [IO.File]::Open($To, 'Open', 'ReadWrite')
    try { $fs.Seek(-$Bytes, 'End') | Out-Null; $fs.Write((New-Object byte[] $Bytes), 0, $Bytes) } finally { $fs.Dispose() }
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
    if ($Variant -eq 'dummy') {
        $rel = Add-SuiteTorrentRelease -Title $release -DummyMb 40
    }
    else {
        if ($Layout -eq 'file') {
            $good = Join-Path $source "$release.mkv"
            New-Item -ItemType HardLink -Path $good -Target $base | Out-Null
            $rel = Add-SuiteTorrentRelease -Title $release -File $good
            $placeAt = $script:Fx.Incomplete[$Client]
            New-Item -ItemType Directory -Force -Path $placeAt | Out-Null
            if ($Variant -eq 'short') { Copy-FileMissingLastPiece -From $good -To (Join-Path $placeAt "$release.mkv") }
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
            $dest = Join-Path $script:Fx.Incomplete[$Client] $release
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            Get-ChildItem -LiteralPath $folder -Recurse -File | ForEach-Object {
                $rp = $_.FullName.Substring($folder.Length + 1)
                $to = Join-Path $dest $rp
                New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
                if ($_.Name -eq "$release.mkv") {
                    if ($Variant -eq 'short') { Copy-FileMissingLastPiece -From $_.FullName -To $to } else { New-Item -ItemType HardLink -Path $to -Target $base | Out-Null }
                } else { Copy-Item -LiteralPath $_.FullName -Destination $to }
            }
        }
    }
    [pscustomobject]@{
        Title = $title; Year = $year; Release = $release; Protocol = 'torrent'; Client = $Client; Layout = $Layout; Size = $rel.size
        Url = "http://127.0.0.1:$($script:Fx.FeedPort)/dl/$($rel.torrent)"; IndexerId = $script:Fx.IndexerIds.torrent; Folder = $(if ($Layout -eq 'folder') { $release } else { $null })
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
    foreach ($id in @($script:Fx.IndexerIds.torrent, $script:Fx.IndexerIds.usenet) | Where-Object { $_ }) { $sources += @{ indexerId = $id; priority = ($sources.Count + 1); requiredTags = ''; excludedTags = '' } }
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
        releaseName = $Film.Release; indexerId = $Film.IndexerId; indexerName = $(if ($Film.Protocol -eq 'usenet') { 'Golden Newznab stub' } else { 'Golden feed' })
        candidateQuality = $Quality; downloadUrl = $Film.Url; sizeBytes = $Film.Size; seeders = 50; force = $true
        overrideReason = 'Golden scenario suite: a controlled release'; duplicateAction = $null
    }
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "Sending $($Film.Release) to $Kind answered $($r.Status). $($r.Raw)") }
    $Film | Add-Member -NotePropertyName SentUtc -NotePropertyValue $sent -Force
    $Film | Add-Member -NotePropertyName MovieId -NotePropertyValue $movie.id -Force
    $Film | Add-Member -NotePropertyName ClientId -NotePropertyValue $client.id -Force
    $Film | Add-Member -NotePropertyName ClientKind -NotePropertyValue $Kind -Force
    Write-SuiteLog ("   sent {0} to {1}" -f $Film.Release, $Kind)
    # qBittorrent and Deluge do not pick up data placed in their Incomplete folder on their own: ask them to check it now.
    if (Test-ClientNeedsSettle -Kind $Kind) { Invoke-FilmSettle -Film $Film -WaitForRowSec 60 | Out-Null }
    $Film
}

# The two torrent clients that must be told to look at data already in place (qBittorrent: Deluno's Re-check; Deluge: its web
# API). Transmission checks on its own.
function Test-ClientNeedsSettle {
    param([Parameter(Mandatory)] [string] $Kind)
    $Kind -in 'qbittorrent', 'deluge'
}

# Presses the right action for the film's client once its queue row exists: Re-check for qBittorrent, force_recheck plus
# move_storage for Deluge. Returns whether the action was sent.
function Invoke-FilmSettle {
    param([Parameter(Mandatory)] $Film, [int] $WaitForRowSec = 0)
    if (-not (Test-ClientNeedsSettle -Kind $Film.ClientKind)) { return $false }
    if ($WaitForRowSec -gt 0) {
        $w = Wait-Until -TimeoutSec $WaitForRowSec -IntervalSec 2 -Condition { Find-QueueRow -Name $Film.Release }
        if (-not $w.Ok) { Write-SuiteLog ("   {0}: no queue row to re-check after {1} s" -f $Film.Release, $WaitForRowSec); return $false }
    }
    $ok = $false
    try { Move-FilmDataToClientPath -Film $Film | Out-Null } catch { Write-SuiteLog ("   {0}: could not move the data to where the client looks: {1}" -f $Film.Release, (Hide-Secrets $_.Exception.Message)) }
    try { $ok = if ($Film.ClientKind -eq 'deluge') { [bool](Invoke-DelugeSettle -Film $Film) } else { [bool](Invoke-Recheck -Film $Film) } }
    catch { Write-SuiteLog ("   {0}: the settle step failed: {1}" -f $Film.Release, (Hide-Secrets $_.Exception.Message)) }
    Write-SuiteLog ("   {0}: {1} sent={2}" -f $Film.Release, $(if ($Film.ClientKind -eq 'deluge') { 'force_recheck + move_storage' } else { 'Re-check' }), $ok)
    $ok
}

# The client says where it looks for the film's data (its queue row's sourcePath). When that is not the folder the suite put the
# data in, the data is moved there, and the log says so, so a wrong guess about a client's layout never again looks like a film that
# "never finishes". Returns $true when something was moved.
function Move-FilmDataToClientPath {
    param([Parameter(Mandatory)] $Film)
    if ($Film.Protocol -ne 'torrent' -or -not $script:Fx.Incomplete.ContainsKey($Film.ClientKind)) { return $false }
    $row = Find-QueueRow -Name $Film.Release
    if (-not $row -or -not $row.sourcePath) { return $false }
    $name = if ($Film.Layout -eq 'file') { "$($Film.Release).mkv" } else { $Film.Release }
    $placedIn = $script:Fx.Incomplete[$Film.ClientKind].TrimEnd([char]92)
    $lookIn = (Split-Path -Path ([string]$row.sourcePath).TrimEnd([char]92) -Parent).TrimEnd([char]92)
    if ($lookIn -ieq $placedIn) { return $false }
    $placed = Join-Path $placedIn $name
    if (-not (Test-Path -LiteralPath $placed)) { return $false }
    New-Item -ItemType Directory -Force -Path $lookIn | Out-Null
    $to = Join-Path $lookIn $name
    if (Test-Path -LiteralPath $to) { Remove-Item -LiteralPath $to -Recurse -Force }
    Move-Item -LiteralPath $placed -Destination $to
    Write-SuiteLog ("   {0}: the client looks in {1}, not {2}; the data was moved there." -f $Film.Release, $lookIn, $placedIn)
    $true
}

# What a failed film needs in its evidence: the client's queue row and what is in the client's folders.
function Save-FilmClientEvidence {
    param([Parameter(Mandatory)] $Film)
    try {
        $row = Find-QueueRow -Name $Film.Release -IncludeHistory
        $folders = foreach ($root in 'C:\Downloads\Incomplete', 'C:\Downloads\Completed') {
            if (Test-Path $root) { Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 200 | ForEach-Object { '{0}  {1}' -f $_.FullName, $(if ($_.PSIsContainer) { '<dir>' } else { "$($_.Length) bytes" }) } }
        }
        Save-Evidence -Name ("film-client-{0}-{1}.txt" -f $Film.ClientKind, $Film.Title) -Text ("queue row:`n{0}`n`nfolders:`n{1}" -f ($row | ConvertTo-Json -Depth 4), ($folders -join "`n"))
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

# Deluge only moves a finished torrent on a completion event, so a torrent that finds its data already there is rechecked and
# moved by hand, which is what its Label plugin would have done (core.force_recheck, core.move_storage).
function Invoke-DelugeSettle {
    param([Parameter(Mandatory)] $Film, [string] $Category = 'deluno-movies', [string] $Destination = 'C:\Downloads\Completed\Movies')
    $row = Find-QueueRow -Name $Film.Release
    if (-not $row) { return $false }
    $hash = $row.id
    $base = 'http://127.0.0.1:8112/json'
    $sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    function Call($method, $params) {
        (Invoke-RestMethod -Uri $base -Method Post -ContentType 'application/json' -WebSession $sess -UseBasicParsing -TimeoutSec 20 -Body (@{ method = $method; params = $params; id = 1 } | ConvertTo-Json -Depth 5 -Compress))
    }
    Call 'auth.login' @('deluge') | Out-Null
    # The web UI must be connected to the daemon before it answers core calls.
    if (-not (Call 'web.connected' @()).result) {
        $hosts = (Call 'web.get_hosts' @()).result
        if ($hosts) { Call 'web.connect' @($hosts[0][0]) | Out-Null; Start-Sleep -Seconds 2 }
    }
    Call 'core.force_recheck' @(, @($hash)) | Out-Null
    Start-Sleep -Seconds 3
    Call 'core.move_storage' @(@($hash), $Destination) | Out-Null
    $true
}

# Waits until the client reports the film finished (nudging a client that needs it) and returns the queue/history row.
function Wait-FilmFinished {
    param([Parameter(Mandatory)] $Film, [int] $TimeoutSec = 180)
    $state = @{ Nudged = 0 }
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
        if ($Film.Protocol -eq 'torrent' -and (Test-ClientNeedsSettle -Kind $Film.ClientKind) -and ([DateTime]::UtcNow - $Film.SentUtc).TotalSeconds -gt (20 * ($state.Nudged + 1))) {
            $state.Nudged++
            Invoke-FilmSettle -Film $Film | Out-Null
        }
        $null
    }
    if (-not $w.Ok) { Save-FilmClientEvidence -Film $Film }
    $w
}
