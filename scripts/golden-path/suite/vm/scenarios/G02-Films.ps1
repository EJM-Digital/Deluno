<#
    A film through each client: sent, downloaded, handed to Weir, cleaned and imported; one film carries a small extra that is
    left out of the hand-off and stays in the client's folder. All five films are sent first, so the clients and Weir work at
    once; each client's result then reads its own film.
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

function Invoke-GroupFilms {
    # Send everything first. A torrent client gets a film in its own folder; qBittorrent's carries the small extra.
    foreach ($kind in Get-AllKinds) {
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
            $act = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) })
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
}
