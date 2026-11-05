<#
    Rows of the coverage register that touch another app, which only the real clients, the real Weir and a real Windows can prove (#1329, #1278).
    Each scenario runs alone (-Scenarios <id>); nothing here depends on another scenario having run.

      maintenance.lane-passes          the planning.maintenance lane runs every pass it owns on a real install, with a real client and Weir
      removal.queue-flags.<client>     the removeFromClient, blocklist and skipRedownload flags on a real torrent client and a real Usenet client
      sharing.completion.<client>      a real torrent client removes, or only stops, a finished imported torrent and the library file stays whole
      import.direct-job                a library that does not hand to a processor: the real client's finished film is queued for import with the catalogue's details
      import.unmatched-output          a cleaned file in Weir's output folder that matches no hand-off is listed once and never imported
      import.marker-held-open          a restart mark another program holds open is left for the next pass and the staging file is never deleted
      redownload.arrives-through-the-workflow   after Weir asks for a title again, the new copy goes through the client, Weir and the library
      install.sabnzbd-folders          the real SABnzbd the picker installed has its two folders and a category with a folder each
      install.sign-ins-recorded        the picker's install record and sign-ins: every installed client recorded, SABnzbd's own key kept, Deluge given no login
      hub.connection-used-processor    a real hand-off to Weir pushes ConnectionUsed with today's count
      weir.connect-variations          Connect Weir with missing details, a wrong password and when already connected
#>

# ---------------------------------------------------------------- helpers

# The sharing choices as the settings screen holds them, so a scenario can put them back.
function Get-SharingForSuite {
    $s = Invoke-Deluno -Path '/api/settings'
    [pscustomobject]@{ Mode = $s.sharingMode; ForHours = $s.sharingForHours; UntilRatio = $s.sharingUntilRatio; Completion = $s.sharingCompletionAction }
}

function Set-SharingForSuite {
    param([Parameter(Mandatory)] [string] $Mode, [Parameter(Mandatory)] [string] $Completion, $ForHours = $null, $UntilRatio = $null)
    Invoke-Deluno -Method PATCH -Path '/api/settings' -Body @{ sharingMode = $Mode; sharingCompletionAction = $Completion; sharingForHours = $ForHours; sharingUntilRatio = $UntilRatio } | Out-Null
}

# Removes a finished download the way the external API does (Radarr's DELETE /queue/{id} and its three flags).
function Remove-FilmByFlags {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] [bool] $RemoveFromClient, [Parameter(Mandatory)] [bool] $Blocklist, [bool] $SkipRedownload = $false)
    $row = Find-QueueRow -Name $Film.Release -IncludeHistory
    if (-not $row) { throw "The client shows no row for $($Film.Release)." }
    $q = 'removeFromClient={0}&blocklist={1}&skipRedownload={2}' -f $RemoveFromClient.ToString().ToLowerInvariant(), $Blocklist.ToString().ToLowerInvariant(), $SkipRedownload.ToString().ToLowerInvariant()
    Try-Deluno -Method DELETE -Path "/api/queue/$($Film.ClientId)/$([uri]::EscapeDataString([string]$row.id))?$q"
}

# Whether the client itself still holds a film's download: a torrent by the client's own answer, a Usenet job by the files left.
function Test-ClientHoldsFilm {
    param([Parameter(Mandatory)] $Film)
    if ($Film.Protocol -eq 'torrent') { return [bool](Get-ClientTorrent -Kind $Film.ClientKind -Hash $Film.Hash -For $Film.Release).Present }
    @(Get-FilmLeftovers -Film $Film).Count -gt 0
}

# The folder a refine-before-import library hands its downloads to, read back from Deluno.
function Set-MoviesWorkflow {
    param([Parameter(Mandatory)] $Library, [Parameter(Mandatory)] [string] $Workflow)
    Try-Deluno -Method PUT -Path "/api/libraries/$($Library.id)/workflow" -Body @{
        importWorkflow = $Workflow; processorConnectionId = $Library.processorConnectionId; processorName = $Library.processorName
        processorOutputPath = $Library.processorOutputPath; processorTimeoutMinutes = $Library.processorTimeoutMinutes
        processorFailureMode = $Library.processorFailureMode; cleanupMode = $Library.cleanupMode; removeEmptySourceFolders = $Library.removeEmptySourceFolders
    }
}

# SABnzbd's own settings file, wherever the install put it.
function Get-SabnzbdIni {
    $found = @(foreach ($base in $env:LOCALAPPDATA, $env:ProgramData, $env:APPDATA) {
            if ($base) { Get-ChildItem -Path (Join-Path $base 'sabnzbd') -Filter 'sabnzbd.ini' -ErrorAction SilentlyContinue }
        }) | Select-Object -First 1
    if ($found) { Get-Content -Raw -LiteralPath $found.FullName } else { '' }
}

function Invoke-GroupGapRows {
    # ---------------------------------------------------------------- the maintenance lane
    Invoke-Scenario -Id 'maintenance.lane-passes' -Title 'The planning.maintenance lane runs every pass it owns on a real install with a real client and Weir: the outcome pass tells Weir what became of a film, and none of the ten passes has failed' -Body {
        $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'lane') -Kind 'qbittorrent'
        $r = Wait-FilmImported -Film $film
        Add-Check 'A film came in through the real client and the real Weir, so the lane had real work' ($r.Imported -and $r.Settled) "imported $($r.Imported), settled $($r.Settled)"
        $told = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition { @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match '"category":"processing\.outcome-(reported|not-reported|superseded)"' }).Count -gt 0 }
        Add-Check 'The outcome pass told Weir what became of the film (Activity has the closing line)' $told.Ok ''
        $keys = @('dispatch.cleanup', 'processor.outcomes', 'download.state', 'metadata.refresh', 'artwork.cache.cleanup', 'recycle.bin.cleanup', 'download.leftover.cleanup', 'blocked.release.cleanup', 'library.file.check', 'unmatched.import.giveUp')
        $ran = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition {
            $t = @(Invoke-Deluno -Path '/api/system/tasks')
            $missing = @($keys | Where-Object { $k = $_; @($t | Where-Object { $_.key -eq $k -and $_.lastResult -eq 'completed' }).Count -eq 0 })
            if ($missing.Count -eq 0) { $t } else { $null }
        }
        $tasks = @(Invoke-Deluno -Path '/api/system/tasks')
        Save-Evidence -Name 'maintenance-lane-tasks.json' -Text ($tasks | ConvertTo-Json -Depth 5)
        $state = ($keys | ForEach-Object { $k = $_; $t = $tasks | Where-Object { $_.key -eq $k } | Select-Object -First 1; "$k=$(if ($t) { $t.lastResult } else { 'unknown' })" }) -join ', '
        Add-Check 'Every pass of the lane has run and completed' $ran.Ok $state
        $failed = @($tasks | Where-Object { $_.key -in $keys -and $_.lastResult -eq 'failed' })
        Add-Check 'None of the lane''s passes has failed' ($failed.Count -eq 0) (($failed | ForEach-Object { "$($_.key): $($_.lastError)" }) -join '; ')
    }

    # ---------------------------------------------------------------- the removal flags
    foreach ($kind in 'qbittorrent', 'sabnzbd') {
        Invoke-Scenario -Id "removal.queue-flags.$kind" -Title "The removal flags on a real $kind download: removeFromClient=false only stops tracking, true deletes it with its files, blocklist refuses the release and searches again, skipRedownload suppresses the search" -Body {
            $films = [ordered]@{}
            try {
                Start-AutomationPause
                foreach ($case in 'keep', 'delete', 'block', 'skip') {
                    $films[$case] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'folder' -Label "flag$case") -Kind $kind
                }
                foreach ($case in @($films.Keys)) { Wait-FilmFinished -Film $films[$case] -TimeoutSec 300 | Out-Null }

                $keep = Remove-FilmByFlags -Film $films['keep'] -RemoveFromClient $false -Blocklist $false
                Save-Evidence -Name "removal-flags-keep-$kind.json" -Text $keep.Raw
                Add-Check 'removeFromClient=false is accepted and says Deluno only stopped tracking it' ($keep.Status -eq 200 -and "$($keep.Data.message)" -match 'stopped tracking it' -and "$($keep.Data.message)" -match 'still has it') "HTTP $($keep.Status): $($keep.Data.message)"
                Start-Sleep -Seconds 10 # reason: gives the client time to show it would have deleted the files, which is the thing that must not happen
                Add-Check 'The real client still holds the download and its files' (Test-ClientHoldsFilm -Film $films['keep']) ''
                Add-Check 'It was neither blocklisted nor searched for again' ($keep.Data.blocklisted -ne $true -and $keep.Data.searchStarted -ne $true) ''

                $delete = Remove-FilmByFlags -Film $films['delete'] -RemoveFromClient $true -Blocklist $false
                Save-Evidence -Name "removal-flags-delete-$kind.json" -Text $delete.Raw
                Add-Check 'removeFromClient=true is accepted' ($delete.Status -eq 200 -and $delete.Data.succeeded) "HTTP $($delete.Status): $($delete.Data.message)"
                $gone = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { -not (Test-ClientHoldsFilm -Film $films['delete']) }
                Add-Check 'The real client no longer holds it and its files are gone' $gone.Ok "after $($gone.Seconds) s"
                Add-Check 'Without blocklist nothing is blocklisted and no search starts' ($delete.Data.blocklisted -ne $true -and $delete.Data.searchStarted -ne $true) ''

                $block = Remove-FilmByFlags -Film $films['block'] -RemoveFromClient $true -Blocklist $true
                Save-Evidence -Name "removal-flags-blocklist-$kind.json" -Text $block.Raw
                Add-Check 'blocklist=true is accepted, blocklists the release and starts a new search' ($block.Status -eq 200 -and $block.Data.blocklisted -eq $true -and $block.Data.searchStarted -eq $true) "HTTP $($block.Status): $($block.Data.message)"
                $skip = Remove-FilmByFlags -Film $films['skip'] -RemoveFromClient $true -Blocklist $true -SkipRedownload $true
                Save-Evidence -Name "removal-flags-skip-$kind.json" -Text $skip.Raw
                Add-Check 'blocklist with skipRedownload blocklists the release and starts no search' ($skip.Status -eq 200 -and $skip.Data.blocklisted -eq $true -and $skip.Data.searchStarted -ne $true) "HTTP $($skip.Status): $($skip.Data.message)"
                $list = Try-Deluno -Path '/api/blocked-releases?pageSize=200'
                Add-Check 'The blocklist holds both blocklisted releases and neither of the others' ($list.Raw -match [regex]::Escape($films['block'].Release) -and $list.Raw -match [regex]::Escape($films['skip'].Release) -and $list.Raw -notmatch [regex]::Escape($films['keep'].Release) -and $list.Raw -notmatch [regex]::Escape($films['delete'].Release)) "HTTP $($list.Status)"
            }
            finally { Stop-AutomationPause }
        }
    }

    # ---------------------------------------------------------------- the sharing target
    foreach ($kind in 'qbittorrent', 'transmission', 'deluge') {
        Invoke-Scenario -Id "sharing.completion.$kind" -Title "A finished, imported $kind torrent whose sharing target is met is removed, or only stopped when that is chosen, and the library file stays whole" -Body {
            $before = Get-SharingForSuite
            try {
                # Tidy up at once: the target is met the moment the title is safely in the library.
                Set-SharingForSuite -Mode 'tidy-now' -Completion 'reclaim'
                $film = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'folder' -Label 'reclaim') -Kind $kind
                $r = Wait-FilmImported -Film $film
                Add-Check 'The film was imported first' $r.Imported ''
                if (-not $r.Imported) { return }
                $length = $r.File.Length
                $removed = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition { -not (Get-ClientTorrent -Kind $kind -Hash $film.Hash -For $film.Release).Present }
                Add-Check "The real $kind removed the finished torrent at the sharing target" $removed.Ok "after $($removed.Seconds) s"
                $lib = Find-Imported -Title $film.Title
                Add-Check 'The library file is whole after the client let go of its copy (the hard link was not broken)' ($lib -and $lib.Length -eq $length) $(if ($lib) { "$($lib.Length) of $length bytes" } else { 'the library file is gone' })
                if ($lib) {
                    $fs = [System.IO.File]::OpenRead($lib.FullName)
                    try { $buffer = New-Object byte[] 65536; $read = $fs.Read($buffer, 0, $buffer.Length) } finally { $fs.Dispose() }
                    Add-Check 'The library file can still be read' ($read -gt 0) "$read bytes read"
                }

                # The same, when the person chose to stop seeding and leave the entry and its files alone.
                Set-SharingForSuite -Mode 'tidy-now' -Completion 'stop'
                $kept = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'folder' -Label 'stopped') -Kind $kind
                $r2 = Wait-FilmImported -Film $kept
                Add-Check 'The second film was imported' $r2.Imported ''
                if (-not $r2.Imported) { return }
                $length2 = $r2.File.Length
                $stopped = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition {
                    $t = Get-ClientTorrent -Kind $kind -Hash $kept.Hash -For $kept.Release
                    if ($t.Present -and ("$($t.State)" -match '(?i)paused|stopped' -or "$($t.State)" -eq '0')) { $t } else { $null }
                }
                Add-Check "The real $kind still holds the torrent but has stopped it" $stopped.Ok $(if ($stopped.Ok) { "state $($stopped.Value.State)" } else { 'it did not stop, or it removed the torrent' })
                Add-Check 'Its files are still in the client''s folder' (@(Get-FilmLeftovers -Film $kept).Count -gt 0) ''
                $lib2 = Find-Imported -Title $kept.Title
                Add-Check 'The library file is whole' ($lib2 -and $lib2.Length -eq $length2) ''
            }
            finally { try { Set-SharingForSuite -Mode $before.Mode -Completion $before.Completion -ForHours $before.ForHours -UntilRatio $before.UntilRatio } catch { Write-SuiteLog "   could not put the sharing choices back: $($_.Exception.Message)" } }
        }
    }

    # ---------------------------------------------------------------- a direct (non-processor) import
    Invoke-Scenario -Id 'import.direct-job' -Title 'A library that does not hand to a processor: a real client''s finished film is queued for import with the catalogue''s details, transfer mode auto and copy fallback on, and Activity says Deluno queued it' -Body {
        $lib = Get-Library -MediaType movies
        $was = $lib.importWorkflow
        $switched = Set-MoviesWorkflow -Library $lib -Workflow 'standard'
        try {
            Add-Check 'The Movies library is switched to the standard workflow (no processor)' ($switched.Status -ge 200 -and $switched.Status -lt 300) "HTTP $($switched.Status) $($switched.Raw)"
            if ($switched.Status -lt 200 -or $switched.Status -ge 300) { return }
            $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'direct') -Kind 'qbittorrent'
            Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
            $queued = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition {
                $hit = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match '"category":"filesystem\.import\.auto-queued"' }) | Select-Object -First 1
                if ($hit) { $hit } else { $null }
            }
            Add-Check 'Activity says Deluno queued it for import' $queued.Ok ''
            if (-not $queued.Ok) { return }
            Save-Evidence -Name 'import-direct-activity.json' -Text $queued.Value
            $entry = $queued.Value | ConvertFrom-Json
            Add-Check 'The line names the library it is queued into' ("$($entry.message)" -match 'queued it for import into') "$($entry.message)"
            $jobId = "$($entry.relatedJobId)"
            Add-Check 'The Activity line points at the import job' ([bool]$jobId) $jobId
            if ($jobId) {
                $job = Try-Deluno -Path "/api/jobs/$jobId"
                Save-Evidence -Name 'import-direct-job.json' -Text $job.Raw
                Add-Check 'The job carries the catalogue''s title and year and the library' ($job.Raw -match [regex]::Escape($film.Title) -and $job.Raw -match [regex]::Escape($lib.id)) ''
                Add-Check 'The transfer mode is auto and the copy fallback is on' ($job.Raw -match 'transferMode\W+auto' -and $job.Raw -match 'allowCopyFallback\W+true') ''
                Add-Check 'Nothing authorised a replacement, so there is no overwrite' ($job.Raw -match 'overwrite\W+false' -or $job.Raw -notmatch 'overwrite\W+true') ''
            }
            Add-Check 'No hand-off to Weir was made' ($null -eq (Get-FilmHandoff -Film $film)) ''
            $in = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition { Find-Imported -Title $film.Title }
            Add-Check 'The film arrived in the library' $in.Ok ''
        }
        finally {
            $back = Set-MoviesWorkflow -Library $lib -Workflow $was
            if ($back.Status -lt 200 -or $back.Status -ge 300) { Write-SuiteLog "   could not put the Movies workflow back: HTTP $($back.Status) $($back.Raw)" }
        }
    }

    # ---------------------------------------------------------------- Weir's output folder
    Invoke-Scenario -Id 'import.unmatched-output' -Title 'A cleaned file in Weir''s output folder that matches no hand-off is listed once as unmatched in a processor''s output and is never imported on a guess' -Body {
        $lib = Get-Library -MediaType movies
        $out = $lib.processorOutputPath
        Add-Check 'The Movies library hands to Weir and has an output folder' ([bool]$out -and (Test-Path -LiteralPath $out)) "$out"
        if (-not $out) { return }
        $token = New-FilmToken
        $stem = "Stranger.Output.$token.2019.1080p.WEB-DL.x264-GOLDEN"
        $file = Join-Path $out "$stem.mkv"
        try {
            Copy-Item -LiteralPath $script:Fx.Base['small'] -Destination $file -Force
            (Get-Item -LiteralPath $file).LastWriteTime = (Get-Date).AddMinutes(-30)
            $seen = Wait-Until -TimeoutSec 300 -IntervalSec 5 -Condition {
                $lines = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($stem) -and $_ -match '"category":"processing\.output\.unmatched"' })
                if ($lines.Count -gt 0) { $lines } else { $null }
            }
            Add-Check 'Activity says the processor''s output did not match anything' $seen.Ok ''
            Start-Sleep -Seconds 150 # reason: five more planning passes, so a second listing would show; the file must be named once however many passes see it
            $lines = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($stem) -and $_ -match '"category":"processing\.output\.unmatched"' })
            Add-Check 'It is listed once, however many passes saw it' ($lines.Count -eq 1) "$($lines.Count) lines"
            $health = Try-Deluno -Path '/api/system/health'
            $rows = ([regex]::Matches($health.Raw, [regex]::Escape($stem))).Count
            Save-Evidence -Name 'import-unmatched-output-health.json' -Text $health.Raw
            Add-Check 'Health lists it as unmatched in a processor''s output' ($health.Raw -match '(?i)unmatched in a processor') "$rows mention(s) of the file"
            Add-Check 'It was never imported on a guess' ($null -eq (Find-Imported -Title $stem)) ''
            Add-Check 'The file is still in Weir''s output, untouched' (Test-Path -LiteralPath $file) $file
        }
        finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    }

    # ---------------------------------------------------------------- a restart mark another program holds
    Invoke-Scenario -Id 'import.marker-held-open' -Title 'A restart mark that another program holds open is left for the next pass: it is not taken for a damaged one, the half-copied file is not deleted, and once the program lets go it is recovered' -Body {
        $token = New-FilmToken
        $srcRoot = (Join-Path $script:VolDir "src_Held$token")
        $libRoot = "C:\Media\Held$token"
        $lib = $null; $movie = $null; $hold = $null
        try {
            $size = [long]30GB
            $free = Get-FreeBytes -Drive 'C'
            if ($free -lt [long]75GB) { $size = [long]8GB }
            $title = "Held Mark $token"
            $src = New-VolumeSource -Folder $srcRoot -Title $title
            $fs = [System.IO.File]::Open($src, 'Open', 'ReadWrite', 'None')
            try { $fs.SetLength($size) } finally { $fs.Dispose() }
            (Get-Item -LiteralPath $src).LastWriteTime = (Get-Date).AddHours(-1)
            $lib = New-VolumeLibrary -Name "Held $token" -Root $libRoot
            $movie = New-VolumeMovie -Title $title -Library $lib

            $pv = [ordered]@{ sourcePath = $src; fileName = (Split-Path $src -Leaf); mediaType = 'movies'; title = $title; year = 2008; genres = @(); tags = @(); libraryId = $lib.id }
            $job = Start-Job -ScriptBlock {
                param($url, $token, $body)
                try { Invoke-WebRequest -Uri "$url/api/filesystem/import/execute-batch" -Method Post -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body $body -UseBasicParsing -TimeoutSec 600 | Out-Null } catch { }
            } -ArgumentList $script:S.DelunoUrl, $script:S.Token, (@{ items = @(@{ preview = $pv; transferMode = 'copy'; overwrite = $false; allowCopyFallback = $true }) } | ConvertTo-Json -Depth 8)
            $caught = $null
            $until = (Get-Date).AddSeconds(180)
            while (-not $caught -and (Get-Date) -lt $until) {
                if (Test-Path -LiteralPath $libRoot) {
                    foreach ($p in [System.IO.Directory]::EnumerateFiles($libRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
                        if ((Split-Path $p -Leaf) -like '*.deluno-marker.json') { $caught = $p; break }
                    }
                }
                if (-not $caught) { Start-Sleep -Milliseconds 15 } # reason: a pause between looks inside a loop bounded by its deadline
            }
            Add-Check 'The copy was under way (its restart mark was on disk)' ([bool]$caught) "$caught"
            Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Stop-Process -Force
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            $dead = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue) -and -not (Test-DelunoAnswering) }
            Add-Check 'Deluno was killed mid-copy' $dead.Ok ''
            if (-not $caught) { return }
            $marker = Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -Filter '*.deluno-marker.json' -ErrorAction SilentlyContinue | Select-Object -First 1
            Add-Check 'The kill left the restart mark' ($null -ne $marker) ''
            if (-not $marker) { return }
            $staging = $marker.FullName.Substring(0, $marker.FullName.Length - '.deluno-marker.json'.Length)
            $stagingBefore = if (Test-Path -LiteralPath $staging) { (Get-Item -LiteralPath $staging -Force).Length } else { -1 }
            Add-Check 'The half-copied file is on disk beside it' ($stagingBefore -ge 0) $staging

            # Another program (this runner, a different process from Deluno) opens the mark and keeps it open, as a media server, an antivirus scan or a sync client does.
            $hold = [System.IO.File]::Open($marker.FullName, 'Open', 'ReadWrite', 'None')
            $up = Restore-DelunoTray
            Add-Check 'Deluno starts again and answers while the mark is held' $up ''
            if (-not $up) { return }
            Start-Sleep -Seconds 90 # reason: recovery runs at start and again every pass; this is several passes, bounded so a mark held for good is seen to be left alone
            Add-Check 'The mark is still there (it was not taken for a damaged one and deleted)' (Test-Path -LiteralPath $marker.FullName) ''
            $stagingNow = if (Test-Path -LiteralPath $staging) { (Get-Item -LiteralPath $staging -Force).Length } else { -1 }
            Add-Check 'The half-copied file was never deleted, the only copy of a move import' ($stagingNow -eq $stagingBefore) "was $stagingBefore bytes, now $stagingNow"
            Add-Check 'The source film is whole' ((Test-Path -LiteralPath $src) -and (Get-Item -LiteralPath $src).Length -eq $size) ''
            $damaged = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($title) -and $_ -match '(?i)damaged|unreadable|corrupt' })
            Add-Check 'Activity does not call the import damaged' ($damaged.Count -eq 0) (($damaged | Select-Object -First 1) -join '')

            $hold.Dispose(); $hold = $null
            $settled = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition {
                $rest = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -notin '.mkv', '.mp4', '.avi' })
                if ($rest.Count -eq 0) { $true } else { $null }
            }
            $files = @(Get-ChildItem -LiteralPath $libRoot -Recurse -File -Force -ErrorAction SilentlyContinue)
            Add-Check 'Once the program lets go, the next pass recovers it: no half-copied file or mark is left' $settled.Ok (($files | ForEach-Object { '{0} {1}' -f $_.Name, $_.Length }) -join '; ')
            $videos = @($files | Where-Object { $_.Extension -in '.mkv', '.mp4', '.avi' })
            Add-Check 'The film is never lost: whole in the library or whole at the source' ((($videos.Count -eq 1) -and ($videos[0].Length -eq $size)) -or ((Test-Path -LiteralPath $src) -and (Get-Item -LiteralPath $src).Length -eq $size)) ''
        }
        finally {
            if ($hold) { try { $hold.Dispose() } catch { } }
            if (-not (Test-DelunoAnswering)) { Restore-DelunoTray | Out-Null }
            Remove-VolumeMovie -Movie $movie
            Remove-VolumeLibrary -Library $lib
            Remove-Item -LiteralPath $srcRoot, $libRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ---------------------------------------------------------------- Weir asks for a title again
    Invoke-Scenario -Id 'redownload.arrives-through-the-workflow' -Title 'Weir asks Deluno to download an imported film again: the new copy goes through the real client, is handed to Weir because the library waits for processing, and is imported' -Body {
        $keyName = 'gaprows-redownload-key'
        $created = $null
        $film = New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'again2'
        $film = Send-Film -Film $film -Kind 'qbittorrent'
        $r = Wait-FilmImported -Film $film
        Add-Check 'The film came in the normal way first' ($r.Imported -and $r.Settled) "imported $($r.Imported)"
        if (-not $r.Imported) { return }
        $path = $r.File.FullName
        $firstHandoff = Get-FilmHandoff -Film $film
        try {
            $created = Invoke-Deluno -Method POST -Path '/api/api-keys' -Body @{ name = $keyName; scopes = 'read,imports' }
            $script:S.Secrets[$keyName] = [string]$created.apiKey
            Start-AutomationPause
            $status = 0; $raw = ''
            try {
                $resp = Invoke-WebRequest -Uri "$($script:S.DelunoUrl)/api/integrations/external/redownload" -Method Post -Headers @{ 'X-Api-Key' = (Get-SuiteSecret $keyName) } -ContentType 'application/json' -Body (@{ path = $path; reason = 'Keep another audio track (suite)'; tool = 'Weir' } | ConvertTo-Json) -UseBasicParsing -TimeoutSec 60
                $status = [int]$resp.StatusCode; $raw = [string]$resp.Content
            }
            catch { try { $status = [int]$_.Exception.Response.StatusCode } catch { } }
            Add-Check 'Deluno answers 202 with the file removed' ($status -eq 202 -and $raw -match '"fileRemoved":\s*true') "HTTP $status $raw"
            $gone = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { -not (Get-ClientTorrent -Kind 'qbittorrent' -Hash $film.Hash -For $film.Release).Present }
            Add-Check 'The client forgot the finished download' $gone.Ok ''
            Restore-FilmData -Film $film
        }
        finally { Stop-AutomationPause }
        $sent = Try-Deluno -Method POST -Path "/api/movies/$($film.MovieId)/grab" -Body @{
            releaseName = $film.Release; indexerId = $film.IndexerId; indexerName = 'Golden feed'; candidateQuality = 'WEB 1080p'
            downloadUrl = $film.Url; sizeBytes = $film.Size; seeders = 50; force = $true; overrideReason = 'Golden scenario suite: a controlled release'; duplicateAction = $null
        }
        Add-Check 'The same release is sent to the client again and taken' ($sent.Status -ge 200 -and $sent.Status -lt 300 -and $sent.Raw -match '"dispatchStatus":\s*"sent"') "HTTP $($sent.Status)"
        $film.SentUtc = [DateTime]::UtcNow
        $again = Wait-FilmImported -Film $film
        Add-Check 'The new copy finished in the real client' $again.Finished ''
        $second = Get-FilmHandoff -Film $film
        Add-Check 'It was handed to Weir, as the library''s workflow says (a new hand-off, not the first one)' ($again.HandedOff -and $second -and (-not $firstHandoff -or $second.id -ne $firstHandoff.id)) $(if ($second) { "hand-off $($second.id), status $($second.status)" } else { 'no hand-off' })
        Add-Check 'Weir finished it and Deluno imported the cleaned file into the library' ($again.Settled -and $again.Imported) "settled $($again.Settled), imported $($again.Imported)"
        try { if ($created.id) { Invoke-Deluno -Method DELETE -Path "/api/api-keys/$($created.id)" | Out-Null } } catch { }
    }

    # ---------------------------------------------------------------- what the picker installed
    Invoke-Scenario -Id 'install.sabnzbd-folders' -Title 'The real SABnzbd the picker installed has its two folders and a category with a folder each (Movies and TV), read from SABnzbd itself' -Body {
        $ini = Get-SabnzbdIni
        $key = if ($ini -match '(?m)^\s*api_key\s*=\s*(\w{16,})') { $Matches[1] } else { '' }
        Add-Check 'SABnzbd''s own settings hold an api key' ([bool]$key) ''
        if (-not $key) { return }
        $script:S.Secrets['gaprows-sab-key'] = $key
        $base = 'http://127.0.0.1:8080/api'
        $misc = Invoke-RestMethod -Uri "$base`?mode=get_config&section=misc&output=json&apikey=$key" -UseBasicParsing -TimeoutSec 30
        $cats = Invoke-RestMethod -Uri "$base`?mode=get_config&section=categories&output=json&apikey=$key" -UseBasicParsing -TimeoutSec 30
        Save-Evidence -Name 'install-sabnzbd-config.json' -Text ((@{ complete_dir = $misc.config.misc.complete_dir; download_dir = $misc.config.misc.download_dir; categories = @($cats.config.categories | ForEach-Object { @{ name = $_.name; dir = $_.dir } }) } | ConvertTo-Json -Depth 5))
        $complete = "$($misc.config.misc.complete_dir)"; $incomplete = "$($misc.config.misc.download_dir)"
        Add-Check 'SABnzbd has a folder for finished jobs and a different one for unfinished jobs' ($complete -and $incomplete -and $complete -ne $incomplete) "complete '$complete', incomplete '$incomplete'"
        $movies = @($cats.config.categories | Where-Object { "$($_.name)" -ieq 'Movies' }) | Select-Object -First 1
        $tv = @($cats.config.categories | Where-Object { "$($_.name)" -ieq 'TV' }) | Select-Object -First 1
        Add-Check 'It has a Movies category with a folder of its own' ($movies -and "$($movies.dir)" -match '(?i)Movies') $(if ($movies) { "$($movies.dir)" } else { 'no Movies category' })
        Add-Check 'It has a TV category with a folder of its own' ($tv -and "$($tv.dir)" -match '(?i)TV') $(if ($tv) { "$($tv.dir)" } else { 'no TV category' })
        Add-Check 'The two category folders differ' ($movies -and $tv -and "$($movies.dir)" -ne "$($tv.dir)") ''
        $lib = Get-Library -MediaType movies
        Add-Check 'The Movies category folder is the Movies library''s downloads folder' ($movies -and $lib -and ("$($movies.dir)".TrimEnd('\') -ieq "$($lib.downloadsPath)".TrimEnd('\'))) "category '$($movies.dir)', library '$($lib.downloadsPath)'"
    }

    Invoke-Scenario -Id 'install.sign-ins-recorded' -Title 'The picker''s install record lists every client it installed, SABnzbd''s own api key is kept from its settings, the generated sign-ins are kept encrypted, and Deluge is given no generated login' -Body {
        $recordFile = $null; $credFile = $null
        foreach ($root in Find-KsDataRoots) {
            if (-not $recordFile) { $recordFile = Get-ChildItem -Path $root -Filter 'installed-components.json' -Recurse -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1 }
            if (-not $credFile) { $credFile = Get-ChildItem -Path $root -Filter 'component-credentials.json' -Recurse -Depth 4 -ErrorAction SilentlyContinue | Select-Object -First 1 }
        }
        Add-Check 'The install record exists' ($null -ne $recordFile) ''
        Add-Check 'The sign-ins file exists' ($null -ne $credFile) ''
        if (-not $recordFile -or -not $credFile) { return }
        $record = @(Get-Content -Raw -LiteralPath $recordFile.FullName | ConvertFrom-Json)
        $kinds = @($record | ForEach-Object { "$($_.kind)".ToLowerInvariant() })
        Save-Evidence -Name 'install-record-kinds.txt' -Text ($kinds -join ', ')
        foreach ($kind in 'qbittorrent', 'transmission', 'deluge', 'sabnzbd', 'nzbget') {
            Add-Check "$kind is in the install record" ($kinds -contains $kind) ($kinds -join ', ')
        }
        $withPort = @($record | Where-Object { $_.port -gt 0 -and $_.host })
        Add-Check 'Each recorded app names its host and port' ($withPort.Count -eq $record.Count) "$($withPort.Count) of $($record.Count)"
        Add-Check 'Each recorded app was connected once (a wired time is set)' (@($record | Where-Object { -not $_.wiredUtc }).Count -eq 0) ''
        $creds = Get-Content -Raw -LiteralPath $credFile.FullName | ConvertFrom-Json
        $names = @($creds.PSObject.Properties.Name | ForEach-Object { $_.ToLowerInvariant() })
        Add-Check 'Deluge was given no generated login (it keeps its own)' ($names -notcontains 'deluge') ($names -join ', ')
        Add-Check 'qBittorrent''s generated sign-in is kept' ($names -contains 'qbittorrent') ($names -join ', ')
        $raw = Get-Content -Raw -LiteralPath $credFile.FullName
        Add-Check 'The kept passwords are encrypted, not written plainly' ($raw -match 'dpapi:v1:') ''
        $sab = Get-KsComponentLogin -Kind 'sabnzbd'
        $ini = Get-SabnzbdIni
        $key = if ($ini -match '(?m)^\s*api_key\s*=\s*(\w{16,})') { $Matches[1] } else { '' }
        $script:S.Secrets['gaprows-sab-ini-key'] = $key
        Add-Check 'SABnzbd''s own api key, read from its settings after it started, is the one Deluno kept' ($sab -and $key -and $sab.Password -eq $key) $(if ($sab) { 'a key is kept' } else { 'no key kept for SABnzbd' })
    }

    # ---------------------------------------------------------------- what the hub pushes for a hand-off to Weir
    Invoke-Scenario -Id 'hub.connection-used-processor' -Title 'A real hand-off to Weir pushes ConnectionUsed for the processor with today''s count, and the number it has right now falls when it finishes' -Body {
        $hub = $null
        try {
            $hub = Open-HubConnection -Subjects @('live', 'queue', 'activity')
            Add-Check 'The hub accepted the sign-in and the connection is open' ($hub.Ws.State -eq 'Open') "$($hub.Ws.State)"
            $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'used') -Kind 'qbittorrent'
            $until = (Get-Date).AddMinutes(9)
            $done = $false
            while ((Get-Date) -lt $until -and -not $done) {
                Read-HubMessages -Connection $hub -WaitMs 500
                $done = @($hub.Messages | Where-Object { $_.Target -eq 'ActivityEventAdded' -and "$($_.Args[0].message)" -match [regex]::Escape($film.Title) -and "$($_.Args[0].label)" -eq 'Imported into the library' }).Count -gt 0
            }
            for ($i = 0; $i -lt 10; $i++) { Read-HubMessages -Connection $hub -WaitMs 300 }
            $used = @($hub.Messages | Where-Object { $_.Target -eq 'ConnectionUsed' -and "$($_.Args[0].kind)" -eq 'processor' })
            Save-Evidence -Name 'hub-connection-used.json' -Text (($used | ForEach-Object { [pscustomobject]@{ at = $_.At.ToString('o'); data = $_.Args[0] } }) | ConvertTo-Json -Depth 5)
            Add-Check 'ConnectionUsed was pushed for the processor when the film was handed to it' ($used.Count -ge 1) "$($used.Count) pushes"
            if ($used.Count -eq 0) { return }
            Add-Check 'It names Weir' ("$($used[0].Args[0].name)" -match '(?i)weir') "$($used[0].Args[0].name)"
            Add-Check 'It carries today''s count, at least one' ([int]$used[0].Args[0].countToday -ge 1) "countToday $($used[0].Args[0].countToday)"
            Add-Check 'It says how many titles Weir has right now' ($null -ne $used[0].Args[0].busy) "busy $($used[0].Args[0].busy)"
            $last = $used[$used.Count - 1].Args[0]
            Add-Check 'When Weir finished, the number it has right now fell while the count for today stayed' ([int]$last.busy -lt [int]$used[0].Args[0].busy -or [int]$last.busy -eq 0) "first busy $($used[0].Args[0].busy), last busy $($last.busy), today $($last.countToday)"
        }
        finally { Close-HubConnection -Connection $hub }
    }

    # ---------------------------------------------------------------- Connect Weir
    Invoke-Scenario -Id 'weir.connect-variations' -Title 'Connect Weir against the real Weir: missing details are asked for and nothing is touched, a wrong password is told plainly and nothing is created, an already-connected Weir is left as it is, and the password reaches no log' -Body {
        $path = '/api/system/bundled-components/weir/connect'
        $before = @(Invoke-Deluno -Path '/api/integrations/processors/connections' | Where-Object { $_.name -match 'weir' })
        Add-Check 'Weir is connected to start with' ($before.Count -ge 1) "$($before.Count) connection(s)"
        $missing = Try-Deluno -Method POST -Path $path -Body @{ username = ''; password = '' } -TimeoutSec 120
        Add-Check 'Missing details are asked for in words' ($missing.Status -in 200, 400 -and "$($missing.Raw)" -match '(?i)username|password|enter|choose') "HTTP $($missing.Status) $($missing.Raw)"
        $badPassword = 'Wrong-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
        $script:S.Secrets['gaprows-weir-wrong'] = $badPassword
        $wrong = Try-Deluno -Method POST -Path $path -Body @{ username = (Get-SuiteSecret 'weir-username'); password = $badPassword } -TimeoutSec 120
        Save-Evidence -Name 'weir-connect-wrong-password.json' -Text $wrong.Raw
        Add-Check 'A wrong password is told plainly' ("$($wrong.Raw)" -match '(?i)password|sign in|did not|could not|wrong|already connected') "HTTP $($wrong.Status) $($wrong.Raw)"
        Add-Check 'The wrong password is not repeated in the answer' ("$($wrong.Raw)" -notmatch [regex]::Escape($badPassword)) ''
        $right = Try-Deluno -Method POST -Path $path -Body @{ username = (Get-SuiteSecret 'weir-username'); password = (Get-SuiteSecret 'weir-password') } -TimeoutSec 120
        Save-Evidence -Name 'weir-connect-already.json' -Text $right.Raw
        Add-Check 'An already-connected Weir is left as it is, and the answer says so' ($right.Status -eq 200 -and "$($right.Raw)" -match '(?i)already') "HTTP $($right.Status) $($right.Raw)"
        $after = @(Invoke-Deluno -Path '/api/integrations/processors/connections' | Where-Object { $_.name -match 'weir' })
        Add-Check 'None of the three made a second connection or changed the first' ($after.Count -eq $before.Count -and (@($after | ForEach-Object { $_.id }) -join ',') -eq (@($before | ForEach-Object { $_.id }) -join ',')) "$($after.Count) connection(s)"
        $weirOk = Test-WeirAnswering
        $signsIn = $true
        try { Connect-Weir } catch { $signsIn = $false }
        Add-Check 'The real Weir still answers and the suite''s own login still signs in' ($weirOk -and $signsIn) ''
        $leak = @(Get-DelunoLogFiles | Where-Object { (Select-String -LiteralPath $_.FullName -SimpleMatch -Pattern (Get-SuiteSecret 'weir-password') -Quiet -ErrorAction SilentlyContinue) -or (Select-String -LiteralPath $_.FullName -SimpleMatch -Pattern $badPassword -Quiet -ErrorAction SilentlyContinue) })
        Add-Check 'Neither password is in any of Deluno''s log files' ($leak.Count -eq 0) (($leak | ForEach-Object { $_.Name }) -join ', ')
    }
}
