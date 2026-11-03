<#
    The Subtitles wanted list is paged (#1439).

    subtitleswanted.paging: the rig has no titles missing subtitles, so the scenario makes them. About 130 tiny real video files
    (made with the ffmpeg Deluno ships) go into a scratch Movies library folder in the VM, the library takes them in with Import
    existing, and a wanted subtitle language is set on it so every one is missing subtitles. Then, through the API and the screen
    (the VM's Edge over DevTools, as layout.390-folder-chain does; nothing opens on the workstation):
      - the total is shown ("N short of a language");
      - the first page lists 100 titles, and the API's pages together reach every one, each once;
      - Load more brings the rest;
      - Search all queues every title (all, not only the ones loaded), on the API and from the button.
    It then removes the titles, the library and the files, so the VM is as it was found. It runs alone.
#>

$script:WantedPagingCount = 130

function Invoke-GroupSubtitlesWanted {
    Invoke-Scenario -Id 'subtitleswanted.paging' -Title 'Subtitles wanted: 130 titles missing a language are counted, listed 100 to a page, all reachable with Load more, and Search all queues every one' -Body {
        $token = New-FilmToken
        $count = $script:WantedPagingCount
        $name = "Wanted paging $token"
        $root = "C:\Media\WantedPaging$token"
        $libId = $null; $ids = @()
        try {
            # ---------------------------------------------------------- about 130 tiny real videos in a scratch library folder
            $ff = @('C:\golden\suite\ffmpeg.path') | Where-Object { Test-Path $_ } | ForEach-Object { Get-Content -Raw $_ } | Select-Object -First 1
            if (-not $ff) { $ff = (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Deluno\current\tools\ffmpeg') -Filter ffmpeg.exe -Recurse | Select-Object -First 1).FullName }
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            $seed = Join-Path $root '_seed.mkv'
            & $ff -y -v error -f lavfi -i 'testsrc2=size=160x90:rate=10:duration=1' -f lavfi -i 'sine=frequency=330:sample_rate=16000:duration=1' -c:v mpeg4 -b:v 50k -c:a aac -b:a 16k -shortest $seed
            if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not make the tiny test video.' }
            for ($i = 1; $i -le $count; $i++) {
                $title = 'Paging {0:D3} {1}' -f $i, $token
                $dir = Join-Path $root "$title (2008)"
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                Copy-Item -LiteralPath $seed -Destination (Join-Path $dir "$title (2008).mkv") -Force
            }
            Remove-Item -LiteralPath $seed -Force

            # ---------------------------------------------------------- the library takes them in with Import existing
            $baseline = [int](Try-Deluno -Path '/api/subtitles/wanted?pageSize=1').Data.totalCount
            $profile = (Get-Library -MediaType 'movies').qualityProfileId
            $lib = Invoke-Deluno -Method POST -Path '/api/libraries' -Body @{
                name = $name; mediaType = 'movies'; purpose = 'Main library'; rootPath = $root
                qualityProfileId = $profile; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
                removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
            }
            $libId = $lib.id
            $go = Try-Deluno -Method POST -Path "/api/libraries/$libId/import-existing" -TimeoutSec 120
            $done = Wait-Until -TimeoutSec 900 -IntervalSec 5 -Condition {
                $p = Try-Deluno -Path "/api/libraries/$libId/import-existing"
                if ($p.Status -eq 200 -and $p.Data -and $p.Data.run -and "$($p.Data.run.status)" -match '(?i)^(completed|complete|done|failed|idle|finished|succeeded|cancelled|paused)$') { $p } else { $null }
            }
            Save-Evidence -Name 'subtitleswanted-import-run.json' -Text $(if ($done.Ok) { $done.Value.Raw } else { $go.Raw })
            $run = if ($done.Ok) { $done.Value.Data.run } else { $null }
            Add-Check "Import existing took in all $count tiny films" ($go.Status -in 200, 202 -and $run -and $run.importedCount -eq $count) $(if ($run) { "processed $($run.processedCount), imported $($run.importedCount), skipped $($run.skippedCount)" } else { "HTTP $($go.Status); the run did not end" })
            if (-not ($run -and $run.importedCount -eq $count)) { return }

            # ---------------------------------------------------------- a wanted language: every one is now missing subtitles
            $set = Try-Deluno -Method PUT -Path "/api/libraries/$libId/subtitles" -Body @{ languages = @('en'); mode = 'all' }
            Add-Check 'A wanted subtitle language is set on the library' ($set.Status -in 200, 204) "HTTP $($set.Status) $($set.Raw)"
            $expected = $baseline + $count
            $first = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition {
                $r = Try-Deluno -Path '/api/subtitles/wanted?pageSize=100'
                if ($r.Status -eq 200 -and $r.Data.totalCount -ge $expected) { $r } else { $null }
            }

            # ---------------------------------------------------------- the API
            $p1 = if ($first.Ok) { $first.Value } else { Try-Deluno -Path '/api/subtitles/wanted?pageSize=100' }
            Save-Evidence -Name 'subtitleswanted-api-page1.json' -Text $p1.Raw
            Add-Check "The API's total counts every title missing a language ($expected)" ($p1.Data.totalCount -eq $expected) "total $($p1.Data.totalCount), expected $expected"
            Add-Check 'The first page lists 100 and says there are more' (@($p1.Data.items).Count -eq 100 -and $p1.Data.hasMore -eq $true -and $p1.Data.nextPageToken) "items $(@($p1.Data.items).Count), hasMore $($p1.Data.hasMore)"
            $seen = New-Object System.Collections.Generic.List[string]
            $mine = @()
            $pg = $p1
            $pages = 0
            while ($pg -and $pages -lt 20) {
                $pages++
                foreach ($row in @($pg.Data.items)) { $seen.Add("$($row.kind)-$($row.mediaId)"); if ($row.libraryId -eq $libId) { $mine += $row } }
                if (-not $pg.Data.nextPageToken) { break }
                $pg = Try-Deluno -Path ('/api/subtitles/wanted?pageSize=100&pageToken=' + [uri]::EscapeDataString($pg.Data.nextPageToken))
            }
            $ids = @($mine | ForEach-Object { $_.mediaId })
            Add-Check 'Moving through the pages reaches every title exactly once' ($seen.Count -eq $expected -and @($seen | Select-Object -Unique).Count -eq $expected) "pages $pages, titles $($seen.Count), distinct $(@($seen | Select-Object -Unique).Count)"
            Add-Check "All $count of the scratch library's titles are on the list" ($ids.Count -eq $count) "found $($ids.Count)"

            # ---------------------------------------------------------- the screen
            Start-Edge
            $page = New-CdpPage 'about:blank'
            try {
                Open-DelunoPage -Page $page -Path '/settings/subtitles-wanted' -Login $script:S.Login
                $rowsJs = 'document.querySelectorAll(''[aria-label^="Search subtitles for"]'').length'
                $null = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { if ([int](Invoke-CdpEval -Page $page -Script $rowsJs) -gt 0) { $true } else { $null } }
                $text = Get-PageText -Page $page
                Add-Check "The screen shows the total ($expected short of a language)" ($text -match "$expected short of a language") "no '$expected short of a language' on the page"
                Add-Check 'The first page lists 100 titles and says "Showing 100 of N"' ([int](Invoke-CdpEval -Page $page -Script $rowsJs) -eq 100 -and $text -match "Showing 100 of $expected") "rows $(Invoke-CdpEval -Page $page -Script $rowsJs)"
                Click-PageText -Page $page -Pattern '^Load more$' | Out-Null
                $all = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { if ([int](Invoke-CdpEval -Page $page -Script $rowsJs) -eq $expected) { $true } else { $null } }
                $after = Get-PageText -Page $page
                Add-Check 'Load more brings the rest, and the button goes away' ($all.Ok -and $after -notmatch 'Load more') "rows $(Invoke-CdpEval -Page $page -Script $rowsJs)"
                Click-PageText -Page $page -Pattern '^Search all$' | Out-Null
                $toast = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match "Queued $expected searches") { $t } else { $null } }
                Add-Check "Search all on the screen queues every title ($expected)" $toast.Ok $(if ($toast.Ok) { 'toast read' } else { "no 'Queued $expected searches' toast" })
            }
            finally { Close-CdpPage -Page $page }

            # ---------------------------------------------------------- Search all on the API: every one, whatever was loaded
            $sa = Try-Deluno -Method POST -Path '/api/subtitles/wanted/search-all' -Body @{ all = $true } -TimeoutSec 300
            Save-Evidence -Name 'subtitleswanted-search-all.json' -Text $sa.Raw
            Add-Check "Search all with no list queues every title ($expected), not only a page" ($sa.Status -eq 200 -and $sa.Data.queued -eq $expected) "HTTP $($sa.Status), queued $($sa.Data.queued)"
        }
        finally {
            # The titles, the library and its files go, whatever happened.
            if ($ids.Count -gt 0) { Try-Deluno -Method POST -Path '/api/movies/bulk' -Body @{ movieIds = $ids; operation = 'remove'; deleteFiles = $true } -TimeoutSec 600 | Out-Null }
            if ($libId) { Try-Deluno -Method DELETE -Path "/api/libraries/$libId" | Out-Null }
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
