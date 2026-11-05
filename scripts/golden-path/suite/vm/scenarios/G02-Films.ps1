<#
    A film through each client: sent, downloaded, handed to Weir, cleaned and imported; one film carries a small extra that is
    left out of the hand-off and stays in the client's folder. All five films are sent first, so the clients and Weir work at
    once; each client's result then reads its own film. film.4x3 and film.tv-episode each send their own film (a real old 4:3
    film, and a real show's first episode searched from the show) and check the poster, the shape, the originals and Activity.
#>
$script:FilmByKind = @{}

# Weir's log files as one text (the suite also keeps them whole at the end of the run).
function Get-WeirLogText {
    $roots = @((Join-Path $env:LOCALAPPDATA 'Weir'), (Join-Path $env:ProgramData 'Weir'), (Join-Path $env:APPDATA 'Weir')) | Where-Object { Test-Path $_ }
    $sb = New-Object System.Text.StringBuilder
    foreach ($r in $roots) {
        Get-ChildItem -Path $r -Recurse -File -Include '*.log', '*.jsonl', '*.txt' -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 200MB } | ForEach-Object {
            try { [void]$sb.AppendLine((Get-Content -Raw -LiteralPath $_.FullName -ErrorAction Stop)) } catch { }
        }
    }
    $sb.ToString()
}

# ffprobe, from the same folder as the ffmpeg the suite uses (or Deluno's installed copy); $null when there is none.
function Get-SuiteFfprobe {
    $ff = @('C:goldensuitefmpeg.path') | Where-Object { Test-Path $_ } | ForEach-Object { (Get-Content -Raw $_).Trim() } | Select-Object -First 1
    $near = if ($ff) { Join-Path (Split-Path $ff -Parent) 'ffprobe.exe' } else { $null }
    if ($near -and (Test-Path $near)) { return $near }
    (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Delunocurrent	oolsfmpeg') -Filter ffprobe.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}

function Invoke-GroupFilms {
    # Send everything first. A torrent client gets a film in its own folder; qBittorrent's carries the small extra.
    # Only the films the chosen scenarios read: a client's own, and qBittorrent's for the extra that is left out.
    $needed = @(Get-AllKinds | Where-Object { (Test-ScenarioSelected "film.$_") -or ($_ -eq 'qbittorrent' -and (Test-ScenarioSelected 'film.extra-left-out')) })
    foreach ($kind in $needed) {
        try {
            $film = New-FilmFor -Kind $kind -Layout 'folder' -Extra:($kind -eq 'qbittorrent') -Label 'film'
            $script:FilmByKind[$kind] = Send-Film -Film $film -Kind $kind
        }
        catch { Write-SuiteLog "   could not send the $kind film: $($_.Exception.Message)"; $script:FilmByKind[$kind] = $null }
    }
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "film.$kind" -Title "A film through ${kind}: grabbed, downloaded, handed to Weir, imported" -Body {
            $film = $script:FilmByKind[$kind]
            if (-not $film) { throw "The $kind film could not be sent." }
            $r = Wait-FilmImported -Film $film
            Add-Check 'The client finished the download' $r.Finished ("{0} s after the send" -f $r.FinishedSec)
            Add-Check 'Deluno handed it to Weir' $r.HandedOff ("{0} s after the send" -f $r.HandedSec)
            Add-Check 'Weir cleaned it and the hand-off settled' $r.Settled $(if ($r.Handoff) { "status $($r.Handoff.status), processor said: $($r.Handoff.processor.state)" } else { 'no hand-off' })
            Add-Check 'Deluno imported the cleaned file into the library' $r.Imported $(if ($r.File) { "{0} ({1} MB) {2} s after the send" -f $r.File.FullName, [math]::Round($r.File.Length / 1MB, 1), $r.ImportedSec } else { 'no file in the library' })
            # Deluno never deletes at hand-off or import: every original (the video, the .nfo and any other sidecar) is still in the client's folder, whole.
            $held = @(Get-ChildItem -Path 'C:\Downloads' -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $film.Release } | ForEach-Object { Split-Path $_.FullName -Parent })
            if ($film.Protocol -eq 'torrent' -and $film.Layout -eq 'folder') {
                $whole = @($held | ForEach-Object { Test-FilmDataAt -Film $film -Folder $_ } | Where-Object { $_.Ok })
                Add-Check "Every original file ($(@($film.DataFiles).Count): video, .nfo and sidecars) is still in the client's folder, and whole" ($whole.Count -gt 0) $(if ($whole.Count) { $whole[0].Text } else { 'the release folder is gone or incomplete: ' + (($held | ForEach-Object { Test-FilmDataAt -Film $film -Folder $_ } | ForEach-Object { $_.Text }) -join ' | ') })
            }
            else {
                $videos = @($held | ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $_ $film.Release) -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match '^\.(mkv|mp4|avi)$' } })
                Add-Check 'The original video is still in the client''s folder' ($videos.Count -gt 0) $(if ($videos.Count) { $videos[0].FullName } else { 'no release folder with a video in C:\Downloads' })
            }
            # Activity is written as the import finishes, a moment after the file lands in the library (film.sabnzbd read it too early
            # on 20 Oct), so wait for all three entries, for up to 30 s, before judging.
            $act = @()
            Wait-Until -TimeoutSec 30 -IntervalSec 2 -Condition {
                $script:filmActivity = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) })
                @("Sent to the download client", "Sent to the processor", "Imported into the library") | ForEach-Object { $p = $_; @($script:filmActivity | Where-Object { $_ -match $p }).Count -ge 1 } | Where-Object { -not $_ } | Measure-Object | ForEach-Object { $_.Count -eq 0 }
            } | Out-Null
            $act = @($script:filmActivity)
            foreach ($pair in @(@('Sent to the download client'), @('Sent to the processor'), @('Imported into the library'))) {
                Add-Check "Activity says: $($pair[0])" (@($act | Where-Object { $_ -match $pair[0] }).Count -ge 1) ''
            }
            # Plain values only: a FileInfo walks its folder and every parent at depth 8, and that ran out of memory (rc.30.21).
            $timings = [ordered]@{ Finished = $r.Finished; FinishedSec = $r.FinishedSec; HandedOff = $r.HandedOff; HandedSec = $r.HandedSec; Settled = $r.Settled; Imported = $r.Imported; ImportedSec = $r.ImportedSec; File = $(if ($r.File) { "$($r.File.FullName) ($($r.File.Length) bytes)" } else { $null }) }
            $filmInfo = [ordered]@{ Title = $film.Title; Release = $film.Release; Kind = $kind }
            Save-Evidence -Name "film-$kind.json" -Text (@{ film = $filmInfo; timings = $timings; handoff = $r.Handoff } | ConvertTo-Json -Depth 5)
        }
    }

    Invoke-Scenario -Id 'film.extra-left-out' -Title 'A small extra in the release is left out of the hand-off and stays in the client''s folder' -Body {
        $film = $script:FilmByKind['qbittorrent']
        if (-not $film) { throw 'The qBittorrent film could not be sent.' }
        Wait-FilmImported -Film $film | Out-Null
        $clientFolder = Get-ChildItem -Path 'C:\Downloads\Completed' -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $film.Release } | Select-Object -First 1
        Add-Check 'The release folder is still in the client''s completed folder' ($null -ne $clientFolder) ''
        if ($clientFolder) {
            $extra = Get-ChildItem -Path $clientFolder.FullName -Recurse -File -Filter 'featurette.mkv' -ErrorAction SilentlyContinue | Select-Object -First 1
            Add-Check 'The extra (featurette.mkv) is still in the client''s folder' ($null -ne $extra) $(if ($extra) { $extra.FullName } else { '' })
        }
        Add-Check 'The film was imported' ($null -ne (Find-Imported -Title $film.Title)) ''
        $libExtras = @(Get-ChildItem -Path 'C:\Media\Movies' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'featurette' })
        Add-Check 'The extra is not in the library' ($libExtras.Count -eq 0) ''
        $weirOut = @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'featurette' })
        Add-Check 'The extra is not in Weir''s output' ($weirOut.Count -eq 0) ''
        $h = Get-FilmHandoff -Film $film
        Add-Check 'The hand-off settled' ($h -and $h.status -eq 'completed') $(if ($h) { $h.status } else { 'no hand-off' })
        $weirLog = Get-WeirLogText
        # Weir 1.0.0-rc.13 logs a cleaned file only as "Hand-off callback: reported completed to Deluno" and never names the film
        # (its recorded log in the rc.30.23 run), so the proof that it cleaned this film is the output path it gave the hand-off.
        Add-Check 'Weir cleaned the film (the hand-off carries the output path Weir gave it, named for the film)' ($h -and "$($h.outputPath)" -match [regex]::Escape($film.Title)) $(if ($h) { "$($h.outputPath)" } else { '' })
        Add-Check 'Weir''s log shows it reported a completed hand-off to Deluno' ($weirLog -match 'Hand-off callback: reported completed') ''
        Add-Check 'Weir''s log never names the extra' ($weirLog -notmatch 'featurette') ''
    }
    # ------------------------------------------------------------------------------------------------ a 4:3 film with a poster
    Invoke-Scenario -Id 'film.4x3' -Title 'A 4:3 film through qBittorrent and Weir: imported with its poster and its 4:3 shape, every original still in the client''s folder, and Activity telling the story' -Body {
        # A real old film (4:3, public domain) so the metadata has a poster for it. The data is the suite's own 4:3 video.
        $film = New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Size 'fourthree' -Label 'film43' -RealTitle 'Nosferatu' -RealYear 1922
        $film = Send-Film -Film $film -Kind 'qbittorrent'
        $r = Wait-FilmImported -Film $film
        Add-Check 'The client finished the download' $r.Finished ("{0} s after the send" -f $r.FinishedSec)
        Add-Check 'Deluno handed it to Weir' $r.HandedOff ("{0} s after the send" -f $r.HandedSec)
        Add-Check 'Weir cleaned it and the hand-off settled' $r.Settled $(if ($r.Handoff) { "status $($r.Handoff.status), processor said: $($r.Handoff.processor.state)" } else { 'no hand-off' })
        Add-Check 'Deluno imported the cleaned file into the library' $r.Imported $(if ($r.File) { "{0} ({1} MB)" -f $r.File.FullName, [math]::Round($r.File.Length / 1MB, 1) } else { 'no file in the library' })

        # The shape of the film survives the processor: the library's file is still 4:3 (not stretched to 16:9, not padded with bars).
        if ($r.File) {
            $probe = Get-SuiteFfprobe
            $dims = if ($probe) { (& $probe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 $r.File.FullName 2>$null) -split ',' } else { @() }
            $ratio = if ($dims.Count -ge 2 -and [double]$dims[1] -gt 0) { [double]$dims[0] / [double]$dims[1] } else { 0 }
            Add-Check 'The imported film is still 4:3 (width over height is 1.33, not 1.78)' ([math]::Abs($ratio - (4 / 3)) -lt 0.03) "ffprobe: $($dims -join 'x'), ratio $([math]::Round($ratio, 3))"
        }

        # The film has its poster: the metadata found it by its real title and year.
        $poster = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition {
            $m = Try-Deluno -Path "/api/movies/$($film.MovieId)"
            if ($m.Data -and -not [string]::IsNullOrWhiteSpace([string]$m.Data.posterUrl)) { $m.Data.posterUrl } else { $null }
        }
        Add-Check 'The film has a poster' $poster.Ok $(if ($poster.Ok) { [string]$poster.Value } else { 'no posterUrl on the movie after 2 minutes' })

        # Deluno never deletes at hand-off or import: every original (video, .nfo, the sample) is still in the client's folder, whole.
        $held = @(Get-ChildItem -Path 'C:\Downloads' -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $film.Release } | ForEach-Object { Split-Path $_.FullName -Parent })
        $whole = @($held | ForEach-Object { Test-FilmDataAt -Film $film -Folder $_ } | Where-Object { $_.Ok })
        Add-Check "Every original file ($(@($film.DataFiles).Count): video, .nfo and sidecars) is still in the client's folder, and whole" ($whole.Count -gt 0) $(if ($whole.Count) { $whole[0].Text } else { 'the release folder is gone or incomplete' })

        Wait-Until -TimeoutSec 30 -IntervalSec 2 -Condition { @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match 'Imported into the library' }).Count -ge 1 } | Out-Null
        $act = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) })
        foreach ($phrase in 'Sent to the download client', 'Sent to the processor', 'Imported into the library') {
            Add-Check "Activity says: $phrase" (@($act | Where-Object { $_ -match $phrase }).Count -ge 1) ''
        }
        Save-Evidence -Name 'film-4x3.json' -Text (@{ film = [ordered]@{ Title = $film.Title; Release = $film.Release }; handoff = $r.Handoff } | ConvertTo-Json -Depth 5)
    }

    # ------------------------------------------------------------------------------------------------ a TV episode with a poster
    Invoke-Scenario -Id 'film.tv-episode' -Title 'A TV episode through qBittorrent and Weir: searched from the show, imported under its show with the show''s poster, every original still in the client''s folder, and Activity telling the story' -Body {
        $show = 'Better Call Saul'; $year = 2015
        $film = New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'ep' -RealTitle $show -RealYear $year -Episode 'S01E01'
        $seriesId = $null
        try {
            # The show in the TV library, its episodes from the metadata, the library routed to the one client.
            $tvLib = Get-Library -MediaType 'tv'
            $client = Set-LibraryClient -Kind 'qbittorrent' -MediaType 'tv'
            $series = Invoke-Deluno -Method POST -Path '/api/series/' -Body @{ title = $show; startYear = $year; monitored = $true; libraryId = $tvLib.id }
            $seriesId = $series.id
            Try-Deluno -Method POST -Path "/api/series/$seriesId/metadata/refresh" -TimeoutSec 120 | Out-Null
            $ep = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition {
                $inv = Try-Deluno -Path "/api/series/$seriesId/inventory"
                @($inv.Data.episodes | Where-Object { $_.seasonNumber -eq 1 -and $_.episodeNumber -eq 1 }) | Select-Object -First 1
            }
            Add-Check 'The show''s first episode is in its catalogue (from its metadata)' $ep.Ok ''
            if (-not $ep.Ok) { return }

            # "Search" on the episode, as its row on the show's page does: the search finds the release on the feed and sends it.
            $film | Add-Member -NotePropertyName SentUtc -NotePropertyValue ([DateTime]::UtcNow) -Force
            $film | Add-Member -NotePropertyName ClientId -NotePropertyValue $client.id -Force
            $film | Add-Member -NotePropertyName ClientKind -NotePropertyValue 'qbittorrent' -Force
            $search = Try-Deluno -Method POST -Path "/api/series/$seriesId/episodes/search" -Body @{ episodeIds = @($ep.Value.episodeId) } -TimeoutSec 240
            Save-Evidence -Name 'film-tv-episode-search.json' -Text $search.Raw
            Add-Check 'Searching the episode found the release and sent it' ($search.Status -ge 200 -and $search.Status -lt 300 -and $search.Data.queuedCount -gt 0) "HTTP $($search.Status) $($search.Raw)"
            if (-not ($search.Status -ge 200 -and $search.Status -lt 300 -and $search.Data.queuedCount -gt 0)) { return }
            Invoke-FilmSettle -Film $film -WaitForRowSec 60 | Out-Null

            $tvRoot = [string]$tvLib.rootPath
            $fin = Wait-FilmFinished -Film $film -TimeoutSec 240
            Add-Check 'The client finished the download' $fin.Ok ''
            $handed = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { Get-FilmHandoff -Film $film }
            Add-Check 'Deluno handed it to Weir' $handed.Ok ''
            $settled = Wait-HandoffSettled -Film $film -TimeoutSec 300
            Add-Check 'Weir cleaned it and the hand-off settled' $settled.Ok $(if ($settled.Ok) { "status $($settled.Value.status), processor said: $($settled.Value.processor.state)" } else { 'no settled hand-off' })
            $file = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition {
                @(Get-ChildItem -Path $tvRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.mkv', '.mp4', '.avi' -and $_.FullName -match [regex]::Escape($show) -and $_.Name -match 'S01E01' }) | Select-Object -First 1
            }
            Add-Check 'Deluno imported the cleaned file into the TV library, under its show, as S01E01' $file.Ok $(if ($file.Ok) { [string]$file.Value.FullName } else { "no S01E01 file of $show under $tvRoot" })

            # The episode is the show's: it has its file, and the show has its poster.
            $inv = Try-Deluno -Path "/api/series/$seriesId/inventory"
            $mine = @($inv.Data.episodes | Where-Object { $_.seasonNumber -eq 1 -and $_.episodeNumber -eq 1 -and $_.hasFile })
            Add-Check 'The catalogue shows S01E01 with its file' ($mine.Count -eq 1) ''
            $poster = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition {
                $s = Try-Deluno -Path "/api/series/$seriesId"
                if ($s.Data -and -not [string]::IsNullOrWhiteSpace([string]$s.Data.posterUrl)) { $s.Data.posterUrl } else { $null }
            }
            Add-Check 'The show has its poster' $poster.Ok $(if ($poster.Ok) { [string]$poster.Value } else { 'no posterUrl on the show after 2 minutes' })

            # Weir's lanes were seen: the hand-off carries the output path Weir gave it, and Weir's log shows it reported to Deluno.
            $h = Get-FilmHandoff -Film $film
            Add-Check 'The hand-off carries the output path Weir gave it' ($h -and -not [string]::IsNullOrWhiteSpace([string]$h.outputPath)) $(if ($h) { [string]$h.outputPath } else { 'no hand-off' })
            Add-Check 'Weir''s log shows it reported a completed hand-off to Deluno' ((Get-WeirLogText) -match 'Hand-off callback: reported completed') ''

            # Every original (video, .nfo, the sample) is still in the client's folder, whole.
            $held = @(Get-ChildItem -Path 'C:\Downloads' -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $film.Release } | ForEach-Object { Split-Path $_.FullName -Parent })
            $whole = @($held | ForEach-Object { Test-FilmDataAt -Film $film -Folder $_ } | Where-Object { $_.Ok })
            Add-Check "Every original file ($(@($film.DataFiles).Count): video, .nfo and sidecars) is still in the client's folder, and whole" ($whole.Count -gt 0) $(if ($whole.Count) { $whole[0].Text } else { 'the release folder is gone or incomplete' })

            Wait-Until -TimeoutSec 30 -IntervalSec 2 -Condition { @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($show) -and $_ -match 'Imported into the library' }).Count -ge 1 } | Out-Null
            $act = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($show) })
            foreach ($phrase in 'Sent to the download client', 'Sent to the processor', 'Imported into the library') {
                Add-Check "Activity says: $phrase" (@($act | Where-Object { $_ -match $phrase }).Count -ge 1) ''
            }
            Save-Evidence -Name 'film-tv-episode.json' -Text (@{ film = [ordered]@{ Title = $film.Title; Release = $film.Release }; handoff = $h } | ConvertTo-Json -Depth 5)
        }
        finally {
            if ($seriesId) { Try-Deluno -Method POST -Path '/api/series/bulk' -Body @{ seriesIds = @($seriesId); operation = 'remove' } | Out-Null }
        }
    }
}
