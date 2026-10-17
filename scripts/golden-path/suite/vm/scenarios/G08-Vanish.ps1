<#
    Files that vanish (#1210, #1186):
      before the hand-off   - automation is paused so a finished download waits; its files are deleted; Deluno must say
                              "Files are gone" within seconds and never hand it off;
      while waiting         - Weir is paused so the hand-off waits at Weir; the files are deleted; the hand-off must end in
                              the "source gone" state (a plain reason, a new copy offered), nothing is imported, and nothing
                              is left unmatched in Weir's output when Weir resumes.
    Every client runs both.
#>

function Remove-FilmFiles {
    param([Parameter(Mandatory)] $Film)
    $left = @(Get-FilmLeftovers -Film $Film | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($item in $left) { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    $left.Count
}

function Invoke-GroupVanish {
    # ---- before the hand-off
    $films = @{}
    $removedAt = @{}
    # Only the clients whose scenario was chosen are sent a film.
    $beforeKinds = @(Get-SelectedKinds -IdFormat 'vanish.before.{0}')
    if ($beforeKinds.Count -gt 0) {
    try {
        Start-AutomationPause
        foreach ($kind in $beforeKinds) { $films[$kind] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Label 'gone') -Kind $kind }
        foreach ($kind in $beforeKinds) { Wait-FilmFinished -Film $films[$kind] -TimeoutSec 240 | Out-Null }
        foreach ($kind in $beforeKinds) { $n = Remove-FilmFiles -Film $films[$kind]; $removedAt[$kind] = [DateTime]::UtcNow; Write-SuiteLog "   $kind : deleted $n items" }
        $seen = @{}
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline -and $seen.Count -lt $beforeKinds.Count) {
            foreach ($row in @(Get-TelemetryRows -IncludeHistory)) {
                foreach ($kind in $beforeKinds) {
                    if (-not $seen.ContainsKey($kind) -and ($row.releaseName -like "*$($films[$kind].Release)*" -or $row.title -like "*$($films[$kind].Title)*") -and $row.status -eq 'filesGone') {
                        $seen[$kind] = [math]::Round(([DateTime]::UtcNow - $removedAt[$kind]).TotalSeconds, 1)
                    }
                }
            }
            Start-Sleep -Milliseconds 500 # reason: poll interval inside a loop bounded by its deadline
        }
        Start-Sleep -Seconds 5 # reason: gives the client time to report the files gone before they are read back
        # A client that was never seen saying "Files are gone": keep the row Deluno held for it (its status and the sourcePath the
        # check judges), which the rc.30.22 run did not keep for SABnzbd and NZBGet.
        foreach ($kind in $beforeKinds) {
            if ($seen.ContainsKey($kind)) { continue }
            try { Save-Evidence -Name "vanish-before-$kind-row.json" -Text (Find-QueueRow -Name $films[$kind].Release -IncludeHistory | ConvertTo-Json -Depth 4) } catch { }
        }
    }
    finally { Stop-AutomationPause }
    Start-Sleep -Seconds 45 # reason: the elapsed time is the behaviour under test (the check runs after its interval)
    }
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "vanish.before.$kind" -Title "A $kind download's files vanish before the hand-off: Files are gone, never handed off" -Body {
            Add-Check 'Deluno said "Files are gone" within 30 s' ($seen.ContainsKey($kind) -and $seen[$kind] -le 30) $(if ($seen.ContainsKey($kind)) { "after $($seen[$kind]) s" } else { 'it never did' })
            $h = Get-FilmHandoff -Film $films[$kind]
            Add-Check 'It was never handed to Weir (not after automation resumed either)' ($null -eq $h) $(if ($h) { "a hand-off exists: $($h.status)" } else { '' })
            Add-Check 'Nothing of it was imported' ($null -eq (Find-Imported -Title $films[$kind].Title)) ''
        }
    }

    # ---- while the hand-off waits at Weir
    $films2 = @{}
    $waitKinds = @(Get-SelectedKinds -IdFormat 'vanish.waiting.{0}')
    if ($waitKinds.Count -gt 0) {
    try {
        Set-WeirPause -Paused $true
        foreach ($kind in $waitKinds) { $films2[$kind] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Label 'gonewait') -Kind $kind }
        foreach ($kind in $waitKinds) {
            Wait-FilmFinished -Film $films2[$kind] -TimeoutSec 240 | Out-Null
            Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { Get-FilmHandoff -Film $films2[$kind] } | Out-Null
        }
        foreach ($kind in $waitKinds) { Remove-FilmFiles -Film $films2[$kind] | Out-Null; $removedAt[$kind] = [DateTime]::UtcNow }
        # While Weir is still paused Deluno must say so itself, within one check (#1210): the row reads "Files are gone".
        $flaggedAfter = @{}
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline -and $flaggedAfter.Count -lt $waitKinds.Count) {
            foreach ($row in @(Get-TelemetryRows -IncludeHistory)) {
                foreach ($kind in $waitKinds) {
                    if (-not $flaggedAfter.ContainsKey($kind) -and ($row.releaseName -like "*$($films2[$kind].Release)*" -or $row.title -like "*$($films2[$kind].Title)*") -and $row.status -eq 'filesGone') {
                        $flaggedAfter[$kind] = [math]::Round(([DateTime]::UtcNow - $removedAt[$kind]).TotalSeconds, 1)
                    }
                }
            }
            Start-Sleep -Milliseconds 500 # reason: poll interval inside a loop bounded by its deadline
        }
        foreach ($kind in $waitKinds) {
            if ($flaggedAfter.ContainsKey($kind)) { continue }
            try { Save-Evidence -Name "vanish-waiting-$kind-row.json" -Text (Find-QueueRow -Name $films2[$kind].Release -IncludeHistory | ConvertTo-Json -Depth 4) } catch { }
        }
    }
    finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
    }
    # Weir waits for a file that is not there before it reports the source gone (about 11 minutes in rc.13), so the hand-off
    # is given that long plus a margin, and all five are watched at once.
    $goneAt = @{}
    $deadline = [DateTime]::UtcNow.AddMinutes(14)
    while ([DateTime]::UtcNow -lt $deadline -and $goneAt.Count -lt $waitKinds.Count) {
        foreach ($kind in $waitKinds) {
            if ($goneAt.ContainsKey($kind)) { continue }
            $h = Get-FilmHandoff -Film $films2[$kind]
            if ($h -and ($h.status -eq 'source-gone' -or ($h.status -eq 'dismissed' -and "$($h.failureMessage)" -match 'gone|no longer'))) { $goneAt[$kind] = [DateTime]::UtcNow }
        }
        Start-Sleep -Seconds 5 # reason: poll interval inside a loop bounded by its deadline
    }
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "vanish.waiting.$kind" -Title "A $kind download's files vanish while the hand-off waits: it ends as source gone, nothing imported" -Body {
            $f = $films2[$kind]
            $h = Get-FilmHandoff -Film $f
            Add-Check 'There was a hand-off' ($null -ne $h) ''
            if (-not $h) { return }
            Add-Check 'Deluno said "Files are gone" within 30 s, while Weir was still paused' ($flaggedAfter.ContainsKey($kind) -and $flaggedAfter[$kind] -le 30) $(if ($flaggedAfter.ContainsKey($kind)) { "after $($flaggedAfter[$kind]) s" } else { 'it never did' })
            Add-Check 'The hand-off ends as "source gone" once Weir reports it (a plain reason, a new copy offered)' $goneAt.ContainsKey($kind) "status $($h.status): $($h.failureMessage) (watched up to 14 min after Weir resumed)"
            Add-Check 'Nothing of it was imported' ($null -eq (Find-Imported -Title $f.Title)) ''
            $out = @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($f.Title) })
            Add-Check 'Nothing of it waits in Weir''s output' ($out.Count -eq 0) (($out | ForEach-Object { $_.FullName }) -join '; ')
            $line = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($f.Title) -and $_ -match 'gone|no longer' })
            Add-Check 'Activity says in plain words that the files are gone' ($line.Count -ge 1) ''
        }
    }
}
