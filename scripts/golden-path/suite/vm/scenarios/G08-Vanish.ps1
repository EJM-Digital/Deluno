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
    try {
        Start-AutomationPause
        foreach ($kind in Get-AllKinds) { $films[$kind] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Label 'gone') -Kind $kind }
        foreach ($kind in Get-AllKinds) { Wait-FilmFinished -Film $films[$kind] -TimeoutSec 240 | Out-Null }
        foreach ($kind in Get-AllKinds) { $n = Remove-FilmFiles -Film $films[$kind]; $removedAt[$kind] = [DateTime]::UtcNow; Write-SuiteLog "   $kind : deleted $n items" }
        $seen = @{}
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline -and $seen.Count -lt 5) {
            foreach ($row in @(Get-TelemetryRows -IncludeHistory)) {
                foreach ($kind in Get-AllKinds) {
                    if (-not $seen.ContainsKey($kind) -and ($row.releaseName -like "*$($films[$kind].Release)*" -or $row.title -like "*$($films[$kind].Title)*") -and $row.status -eq 'filesGone') {
                        $seen[$kind] = [math]::Round(([DateTime]::UtcNow - $removedAt[$kind]).TotalSeconds, 1)
                    }
                }
            }
            Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Seconds 5
    }
    finally { Stop-AutomationPause }
    Start-Sleep -Seconds 45
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
    try {
        Set-WeirPause -Paused $true
        foreach ($kind in Get-AllKinds) { $films2[$kind] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Label 'gonewait') -Kind $kind }
        foreach ($kind in Get-AllKinds) {
            Wait-FilmFinished -Film $films2[$kind] -TimeoutSec 240 | Out-Null
            Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { Get-FilmHandoff -Film $films2[$kind] } | Out-Null
        }
        foreach ($kind in Get-AllKinds) { Remove-FilmFiles -Film $films2[$kind] | Out-Null; $removedAt[$kind] = [DateTime]::UtcNow }
        Start-Sleep -Seconds 60
    }
    finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
    Start-Sleep -Seconds 30
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "vanish.waiting.$kind" -Title "A $kind download's files vanish while the hand-off waits: it ends as source gone, nothing imported" -Body {
            $f = $films2[$kind]
            $h = Get-FilmHandoff -Film $f
            Add-Check 'There was a hand-off' ($null -ne $h) ''
            if (-not $h) { return }
            Add-Check 'The hand-off ends as "source gone" (a plain reason, a new copy offered)' ($h.status -eq 'source-gone') "status $($h.status): $($h.failureMessage)"
            Add-Check 'Nothing of it was imported' ($null -eq (Find-Imported -Title $f.Title)) ''
            $out = @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($f.Title) })
            Add-Check 'Nothing of it waits in Weir''s output' ($out.Count -eq 0) (($out | ForEach-Object { $_.FullName }) -join '; ')
            $line = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($f.Title) -and $_ -match 'gone|no longer' })
            Add-Check 'Activity says in plain words that the files are gone' ($line.Count -ge 1) ''
        }
    }
}
