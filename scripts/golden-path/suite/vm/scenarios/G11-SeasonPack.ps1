<#
    A season pack that replaces episodes whose old files sit outside every library (#1188 follow-up, proof owed in #1196).

    seasonpack.no-library: when a season-pack replacement finds that the file it replaced matches no library, Deluno leaves that
    old file where it is (the pack path moves it aside while it places the new files, and leaves it at the aside name) instead of
    deleting it for good, and says why. A person's media only ever leaves through the recycle bin, and the bin needs a library to
    put the file under (ImportPipelineService.ExecuteTvPackAsync).

    The replacement arrives the way a real one does, never through a hand-made import call (Deluno refuses that, rightly: "Episode
    S01E01 already owns a different file. Replace it through an episode-scoped review"). Only a stored dispatch carries replacement
    targets for a pack, and only Deluno's own season search stores one (SeasonSearchExecutor -> SeriesSearchBaselineResolver.
    EvaluateSeasonPackCandidate -> RecordDownloadDispatchAsync(replacementTargets)). So:
      1. a TV library of its own, routed to a torrent client and the suite's Torznab feed, and a show in it (a real show, whose
         episodes come from its metadata: Deluno never invents an episode the catalogue lacks);
      2. season 1, episodes 1 and 2, imported from single files (Deluno records the installed-file evaluation as it imports);
      3. the library's folder is changed to another folder (Settings > Libraries), so the two files sit outside every TV library;
      4. a season pack of the same show, in a better quality, is offered by the feed with its data in the client; the season is
         searched from the TV workspace (POST /api/series/{id}/seasons/1/search), Deluno sees a proven upgrade for both installed
         episodes, sends the pack as a replacement and stores the targets with the dispatch; the client finishes it, and Deluno's own
         completed-download import runs the replacement.
    The old files are hashed before step 4. Afterwards they must still be on disk, byte for byte, and Deluno's log must say they were
    left because no library matched them and that nothing was deleted. The new files are placed under the library's new folder.
#>

# Waits for a queued import job to finish; returns the job (CompletedUtc set or a final status), or $null when it never finished.
function Wait-ImportJobDone {
    param([Parameter(Mandatory)] [string] $JobId, [int] $TimeoutSec = 300)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 2 -Condition {
        $j = Try-Deluno -Path "/api/jobs/$JobId"
        if ($j.Status -eq 200 -and $j.Data -and ($j.Data.completedUtc -or "$($j.Data.status)" -match '(?i)complet|succe|fail|dead|cancel')) { $j.Data } else { $null }
    }
    if ($w.Ok) { $w.Value } else { $null }
}

# The baseline: one episode file imported as the library's first copy (nothing is replaced).
function Queue-TvImport {
    param([Parameter(Mandatory)] [string] $SourcePath, [Parameter(Mandatory)] $Show)
    $body = @{
        preview = @{ sourcePath = $SourcePath; mediaType = 'tv'; title = $Show.Title; year = $Show.Year; seriesId = $Show.SeriesId; libraryId = $Show.LibraryId }
        transferMode = 'copy'; overwrite = $false; allowCopyFallback = $true
    }
    Try-Deluno -Method POST -Path '/api/filesystem/import/jobs' -Body $body -TimeoutSec 120
}

# A season pack the feed offers (a folder of two episodes, in the better quality), with its data placed where the torrent client keeps
# an unfinished torrent, so the client completes it at once when Deluno sends it. Shaped like the films the other groups send, so the
# same settle and wait helpers read it.
function New-PackRelease {
    param([Parameter(Mandatory)] [string] $Dotted, [Parameter(Mandatory)] [string] $Token, [Parameter(Mandatory)] [string] $Client)
    $release = "$Dotted.S01.1080p.WEB-DL.x264-GP$($Token.Replace('-', ''))"
    $source = Join-Path (Get-SuiteRoot) "films\$release"
    $folder = Join-Path $source $release
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    foreach ($ep in 1, 2) {
        New-Item -ItemType HardLink -Path (Join-Path $folder ("{0}.S01E{1:d2}.1080p.WEB-DL.x264-GP{2}.mkv" -f $Dotted, $ep, $Token.Replace('-', ''))) -Target $script:Fx.Base['big'] | Out-Null
    }
    $rel = Add-SuiteTorrentRelease -Title $release -Cat 5040 -Folder $folder
    $placeIn = Get-ClientDataFolder -Kind $Client -For $release
    $dest = Join-Path $placeIn $release
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in Get-ChildItem -LiteralPath $folder -File) { New-Item -ItemType HardLink -Path (Join-Path $dest $f.Name) -Target $script:Fx.Base['big'] | Out-Null }
    [pscustomobject]@{
        Title = $Dotted; Release = $release; Protocol = 'torrent'; Client = $Client; ClientKind = $Client; Layout = 'folder'; Size = $rel.size
        DataFolder = $placeIn; DataItem = $release; DataFiles = @(Get-FilmFilesIn -Folder $placeIn -Item $release); SettleKey = $null
        Hash = $rel.infohash; Url = "http://127.0.0.1:$($script:Fx.FeedPort)/dl/$($rel.torrent)"; IndexerId = $script:Fx.IndexerIds.torrent; Folder = $release
    }
}

function Invoke-GroupSeasonPack {
    Invoke-Scenario -Id 'seasonpack.no-library' -Title 'A season-pack replacement whose old files match no library leaves them in place, byte for byte, and says why' -Body {
        $token = New-FilmToken
        # A real show, with its episodes in the metadata: Deluno never invents an episode for a file whose number the show's catalogue
        # lacks ("unmatched"), so a made-up title cannot own episodes (the 15 Oct run's baseline import failed that way).
        $title = 'Breaking Bad'
        $dotted = 'Breaking.Bad'
        $year = 2008
        $root = Join-Path (Get-SuiteRoot) ("seasonpack\Pack{0}" -f $token.Replace('-', ''))
        $libRoot = Join-Path $root 'library-before'
        $movedRoot = Join-Path $root 'library-after'
        $downloads = Join-Path $root 'downloads'
        $oldSrc = Join-Path $downloads 'old'
        New-Item -ItemType Directory -Force -Path $libRoot, $movedRoot, $oldSrc | Out-Null
        $libId = $null; $seriesId = $null
        try {
            # ---------------------------------------------------------- 1. a TV library and a show in it
            $tvProfile = (Get-Library -MediaType 'tv').qualityProfileId
            $lib = Invoke-Deluno -Method POST -Path '/api/libraries' -Body @{
                name = "Pack walk $token"; mediaType = 'tv'; purpose = 'Main library'; rootPath = $libRoot; downloadsPath = $downloads
                qualityProfileId = $tvProfile; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
                removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
            }
            $libId = $lib.id
            $series = Invoke-Deluno -Method POST -Path '/api/series/' -Body @{ title = $title; startYear = $year; monitored = $true; libraryId = $libId }
            $seriesId = $series.id
            $show = [pscustomobject]@{ Title = $title; Year = $year; SeriesId = $seriesId; LibraryId = $libId }
            # The show's episodes come from its metadata; the import matches a file only to an episode the catalogue holds.
            $refresh = Try-Deluno -Method POST -Path "/api/series/$seriesId/metadata/refresh" -TimeoutSec 120
            $catalogued = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition {
                $inv = Try-Deluno -Path "/api/series/$seriesId/inventory"
                $have = @($inv.Data.episodes | Where-Object { $_.seasonNumber -eq 1 -and $_.episodeNumber -in 1, 2 })
                if ($have.Count -eq 2) { $have } else { $null }
            }
            Add-Check 'The show''s first two episodes are in its catalogue (from its metadata)' $catalogued.Ok $(if ($catalogued.Ok) { '' } else { "the metadata refresh answered HTTP $($refresh.Status) and the show still has no S01E01 and S01E02" })
            if (-not $catalogued.Ok) { return }

            # ---------------------------------------------------------- 2. two episodes imported, each from a single file
            foreach ($ep in 1, 2) {
                $file = Join-Path $oldSrc ("{0}.S01E{1:d2}.720p.WEB-DL.x264-GOLDEN.mkv" -f $dotted, $ep)
                Copy-Item -LiteralPath $script:Fx.Base['small'] -Destination $file
                $q = Queue-TvImport -SourcePath $file -Show $show
                if ($q.Status -lt 200 -or $q.Status -ge 300) { throw (Hide-Secrets "Queuing the import of S01E$ep answered $($q.Status). $($q.Raw)") }
            }
            $owned = Wait-Until -TimeoutSec 360 -IntervalSec 3 -Condition {
                $inv = Try-Deluno -Path "/api/series/$seriesId/inventory"
                $withFile = @($inv.Data.episodes | Where-Object { $_.hasFile -and $_.seasonNumber -eq 1 -and $_.episodeNumber -in 1, 2 })
                if ($withFile.Count -eq 2) { $withFile } else { $null }
            }
            Add-Check 'The show owns S01E01 and S01E02 in its library before the pack (the fixture)' $owned.Ok $(if ($owned.Ok) { 'both episodes have a file' } else { 'the baseline import did not give the show two episode files in 6 minutes' })
            if (-not $owned.Ok) { return }
            $oldFiles = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Filter '*.mkv' -ErrorAction SilentlyContinue | Sort-Object Name)
            Add-Check 'Two episode files are on disk under the library''s folder' ($oldFiles.Count -eq 2) (($oldFiles | ForEach-Object { $_.FullName }) -join '; ')
            if ($oldFiles.Count -ne 2) { return }
            $before = @{}
            foreach ($f in $oldFiles) { $before[$f.FullName] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }
            # ---------------------------------------------------------- 3. the library's folder changes: the old files match no library
            $put = Try-Deluno -Method PUT -Path "/api/libraries/$libId" -Body @{ name = "Pack walk $token"; rootPath = $movedRoot; downloadsPath = $downloads }
            Add-Check 'The library''s folder is changed to another folder, so the two old files sit outside every TV library' ($put.Status -ge 200 -and $put.Status -lt 300) "HTTP $($put.Status)"
            $others = @(Invoke-Deluno -Path '/api/libraries' | Where-Object { $_.mediaType -eq 'tv' -and $_.rootPath -and $oldFiles[0].FullName.StartsWith([IO.Path]::GetFullPath($_.rootPath), [StringComparison]::OrdinalIgnoreCase) })
            Add-Check 'No TV library''s folder contains the old files now' ($others.Count -eq 0) (($others | ForEach-Object { $_.name }) -join ', ')
            if ($others.Count -gt 0) { return }

            # ---------------------------------------------------------- 4. the season is searched; Deluno sends the pack as a replacement
            # The library takes the suite's torrent client and the feed, as the films' library does.
            $clientKind = 'transmission'
            $client = Get-ClientByKind -Kind $clientKind
            Invoke-Deluno -Method PUT -Path "/api/libraries/$libId/routing" -Body @{
                sources = @(@{ indexerId = $script:Fx.IndexerIds.torrent; priority = 1; requiredTags = ''; excludedTags = '' })
                downloadClients = @(@{ downloadClientId = $client.id; priority = 1 })
            } | Out-Null
            $pack = New-PackRelease -Dotted $dotted -Token $token -Client $clientKind
            $pack | Add-Member -NotePropertyName ClientId -NotePropertyValue $client.id -Force
            Write-SuiteLog ("   offered the pack {0} ({1} MB) to {2}" -f $pack.Release, [math]::Round($pack.Size / 1MB), $clientKind)
            $mark = Get-Date
            $s = Try-Deluno -Method POST -Path "/api/series/$seriesId/seasons/1/search" -TimeoutSec 240
            if ($s.Status -ge 200 -and $s.Status -lt 300) { Save-Evidence -Name 'seasonpack-search.json' -Text ($s.Data | ConvertTo-Json -Depth 8) }
            Add-Check 'The season is searched from the TV workspace' ($s.Status -ge 200 -and $s.Status -lt 300) $(if ($s.Status -ge 200 -and $s.Status -lt 300) { "outcome $($s.Data.outcome): $($s.Data.reason)" } else { Hide-Secrets "HTTP $($s.Status) $($s.Raw)" })
            if ($s.Status -lt 200 -or $s.Status -ge 300) { return }
            $comparisons = @($s.Data.replacementComparisons)
            Add-Check 'Deluno judged the pack against both installed episode files and sent it' ($s.Data.releaseName -eq $pack.Release -and $comparisons.Count -eq 2 -and "$($s.Data.dispatchStatus)" -and "$($s.Data.dispatchStatus)" -notmatch '(?i)fail|error|held|block|refus') (Hide-Secrets "release $($s.Data.releaseName); $($comparisons.Count) comparison(s); dispatch $($s.Data.dispatchStatus): $($s.Data.dispatchMessage); $($s.Data.reason)")
            if ($s.Data.releaseName -ne $pack.Release -or $comparisons.Count -ne 2) {
                # Why no pack was sent: what the feed was asked and what Activity says about this show, kept as evidence.
                $feedLog = Join-Path $script:Fx.Feed 'feed.log'
                $asked = if (Test-Path $feedLog) { (Get-Content $feedLog -Tail 15) -join "`n" } else { 'no feed log' }
                $act = (@(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($title) }) | Select-Object -First 12) -join "`n"
                Save-Evidence -Name 'seasonpack-why-no-pack.txt' -Text ("feed requests:`n$asked`n`nactivity:`n$act`n`nreleases:`n" + (Get-Content (Join-Path $script:Fx.Feed 'releases.jsonl') -Tail 3 | Out-String))
                return
            }

            # ---------------------------------------------------------- the client finishes it; Deluno's own import runs the replacement
            $fin = Wait-FilmFinished -Film $pack -TimeoutSec 240
            Add-Check 'The torrent client finished the pack' $fin.Ok ''
            if (-not $fin.Ok) { return }
            $placed = Wait-Until -TimeoutSec 360 -IntervalSec 3 -Condition {
                $n = @(Get-ChildItem -LiteralPath $movedRoot -Recurse -File -Filter '*.mkv' -ErrorAction SilentlyContinue)
                if ($n.Count -ge 2) { $n } else { $null }
            }
            Add-Check 'Deluno''s own import placed the pack''s two episode files under the library''s new folder' $placed.Ok $(if ($placed.Ok) { ($placed.Value | ForEach-Object { $_.FullName }) -join '; ' } else { 'no pack file under the new folder in 6 minutes' })
            $h = Find-Handoffs -Name $pack.Release | Select-Object -First 1
            if ($h) { Save-Evidence -Name 'seasonpack-handoff.json' -Text ($h | ConvertTo-Json -Depth 5) }

            # ---------------------------------------------------------- what is left
            $gone = @()
            $differ = @()
            $asides = @()
            foreach ($path in $before.Keys) {
                $dir = Split-Path $path
                $aside = @(Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ((Split-Path $path -Leaf) + '.deluno-pack-backup-*') })
                $inPlace = Test-Path -LiteralPath $path
                $kept = if ($inPlace) { Get-Item -LiteralPath $path } elseif ($aside.Count -gt 0) { $aside[0] } else { $null }
                if (-not $kept) { $gone += (Split-Path $path -Leaf); continue }
                $asides += $kept.Name
                if ((Get-FileHash -LiteralPath $kept.FullName -Algorithm SHA256).Hash -ne $before[$path]) { $differ += $kept.Name }
            }
            Add-Check 'Both old files are still on disk (left where they were, or at the aside name the pack path gave them)' ($gone.Count -eq 0) $(if ($gone.Count) { 'gone: ' + ($gone -join ', ') } else { 'kept as: ' + ($asides -join ', ') })
            Add-Check 'The old files are byte-identical to what they were before the pack' ($gone.Count -eq 0 -and $differ.Count -eq 0) $(if ($differ.Count) { 'changed: ' + ($differ -join ', ') } else { 'SHA-256 of both unchanged' })
            Add-Note "The pack's new files were placed under $movedRoot ($(@(Get-ChildItem -LiteralPath $movedRoot -Recurse -File -Filter '*.mkv' -ErrorAction SilentlyContinue).Count) file(s))."

            # Deluno says why. It writes it to its log as a warning, once for each replaced file.
            $said = Wait-Until -TimeoutSec 60 -IntervalSec 3 -Condition {
                $hits = New-Object System.Collections.ArrayList
                foreach ($f in Get-DelunoLogFiles) {
                    try { foreach ($line in [IO.File]::ReadLines($f.FullName)) { if ($line -match 'because no library matched it; nothing was deleted') { [void]$hits.Add($line) } } } catch { }
                }
                if ($hits.Count -gt 0) { @($hits) } else { $null }
            }
            $lines = if ($said.Ok) { @($said.Value) } else { @() }
            $mine = @($lines | Where-Object { $_ -match [regex]::Escape($title) })
            Add-Check 'Deluno says why: its log says the replaced file was left because no library matched it, and that nothing was deleted' ($mine.Count -ge 1) $(if ($mine.Count) { ($mine[0] -replace '\s+', ' ').Substring(0, [math]::Min(300, ($mine[0] -replace '\s+', ' ').Length)) } else { "no such line names $title among $($lines.Count) line(s) of that wording" })
            Add-Check 'It says so once for each replaced file' ($mine.Count -ge 2) "$($mine.Count) line(s)"
            $badLine = @($lines | Where-Object { $_ -match [regex]::Escape($title) -and $_ -match '(?i)moved to the recycle bin|removed the previous' })
            Add-Check 'Nothing says the old file was recycled or removed' ($badLine.Count -eq 0) ''
        }
        finally {
            # Best effort: the show and the library go; the files stay in the suite's folder as evidence.
            if ($seriesId) { Try-Deluno -Method POST -Path '/api/series/bulk' -Body @{ seriesIds = @($seriesId); operation = 'remove' } | Out-Null }
            if ($libId) { Try-Deluno -Method DELETE -Path "/api/libraries/$libId" | Out-Null }
        }
    }
}
