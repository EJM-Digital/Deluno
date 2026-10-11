<#
    Problems seen in several places, and Activity's closing lines.

    problems.dismiss-everywhere (#1228): a failed hand-off stands in Transfers (needs action), in the bell and, where Deluno
    raises it, on System. Dismissing it in one place clears every place within seconds, with nothing to reload.

    activity.one-story (#1226, #1230): every hand-off the suite made has closing lines that tell one story: never both
    "imported" and "will not import", no doubled full stops, one closing line for each hand-off.
#>

# Where a release name shows up among the places a person looks for problems. Each is read from the API behind the screen.
function Get-ProblemPlaces {
    param([Parameter(Mandatory)] [string] $Needle)
    $places = [ordered]@{}
    $has = { param($obj) $t = ($obj | ConvertTo-Json -Depth 8 -Compress); $t -match [regex]::Escape($Needle) }
    $places['Transfers: needs action (hand-offs that went wrong)'] = [bool](@(Get-Handoffs | Where-Object { $_.releaseName -like "*$Needle*" -and $_.status -in 'failed', 'rejected', 'timed-out', 'stalled', 'lost', 'source-gone' }).Count)
    foreach ($spec in @(@('The bell (attention summary)', '/api/attention/summary'), @('System: health checks', '/api/system/health/summary'), @('System: health notices', '/api/system/health'))) {
        $r = Try-Deluno -Path $spec[1]
        $places[$spec[0]] = ($r.Status -eq 200 -and $r.Raw -match [regex]::Escape($Needle))
    }
    $places
}

# What is wrong with the closing lines of one film (an empty list when they tell one story).
function Get-StoryProblems {
    param([Parameter(Mandatory)] $Film)
    $problems = New-Object System.Collections.ArrayList
    $lines = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) })
    $closing = @($lines | Where-Object { $_ -match "Told the processor what happened|Could not tell the processor" })
    foreach ($c in @($closing | Where-Object { $_ -match "imported the file" -and $_ -match "will not import|will not be imported" })) {
        $flat = $c -replace '\s+', ' '
        [void]$problems.Add('says both imported and will not import: ' + $flat.Substring(0, [math]::Min(240, $flat.Length)))
    }
    foreach ($c in @($closing | Where-Object { $_ -match '[a-z0-9]\.\.(\s|"|\\|$)' })) {
        $flat = $c -replace '\s+', ' '
        [void]$problems.Add('ends in a doubled full stop: ' + $flat.Substring(0, [math]::Min(240, $flat.Length)))
    }
    $handoffs = @(Find-Handoffs -Name $Film.Release)
    if ($closing.Count -gt [math]::Max(1, $handoffs.Count)) { [void]$problems.Add("$($closing.Count) closing lines for $($handoffs.Count) hand-offs") }
    @($problems)
}

# The closing lines of one film read as one story, as checks.
function Test-OneStory {
    param([Parameter(Mandatory)] $Film, [string] $What = $Film.Release)
    $p = @(Get-StoryProblems -Film $Film)
    Add-Check "$What : the closing lines tell one story (no imported-and-will-not-import, no doubled full stop, one line per hand-off)" ($p.Count -eq 0) ($p -join " || ")
}

function Invoke-GroupProblems {
    Invoke-Scenario -Id 'problems.dismiss-everywhere' -Title 'A problem shown in several places clears everywhere when it is dismissed in any one' -Body {
        Stop-WeirApp | Out-Null
        $film = New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'problem'
        try {
            Send-Film -Film $film -Kind 'qbittorrent' | Out-Null
            Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
            $failed = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { $h = Get-FilmHandoff -Film $film; if ($h -and $h.status -in 'failed', 'rejected') { $h } else { $null } }
        }
        finally { Start-WeirApp }
        Add-Check 'The hand-off failed while Weir was down (the problem exists)' $failed.Ok ''
        if (-not $failed.Ok) { return }
        $shown = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition {
            $p = Get-ProblemPlaces -Needle $film.Title
            if (@($p.Values | Where-Object { $_ }).Count -ge 2) { $p } else { $null }
        }
        $places = if ($shown.Ok) { $shown.Value } else { Get-ProblemPlaces -Needle $film.Title }
        $where = @($places.GetEnumerator() | Where-Object { $_.Value } | ForEach-Object { $_.Key })
        Add-Check 'The problem stands in at least two places' ($where.Count -ge 2) ("places: " + ($where -join ' | '))
        if ($where.Count -lt 1) { return }
        $t0 = [DateTime]::UtcNow
        $r = Try-Deluno -Method POST -Path "/api/integrations/processors/handoffs/$($failed.Value.id)/dismiss"
        Add-Check 'Dismiss in Transfers is accepted' ($r.Status -ge 200 -and $r.Status -lt 300) "HTTP $($r.Status)"
        $gone = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition {
            $p = Get-ProblemPlaces -Needle $film.Title
            if (@($p.Values | Where-Object { $_ }).Count -eq 0) { $p } else { $null }
        }
        $after = Get-ProblemPlaces -Needle $film.Title
        $still = @($after.GetEnumerator() | Where-Object { $_.Value } | ForEach-Object { $_.Key })
        Add-Check 'Every place clears within 15 s, with no reload' $gone.Ok $(if ($gone.Ok) { "all clear after $([math]::Round(([DateTime]::UtcNow - $t0).TotalSeconds, 1)) s" } else { "still shown in: " + ($still -join ' | ') })
        Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { Test-WeirAnswering } | Out-Null
    }

    Invoke-Scenario -Id 'activity.one-story' -Title 'Every hand-off''s closing lines in Activity tell one story' -Body {
        $films = @($script:AllFilms | Where-Object { $_.Protocol })
        $checked = 0
        $bad = New-Object System.Collections.ArrayList
        foreach ($film in $films) {
            if (@(Find-Handoffs -Name $film.Release).Count -eq 0) { continue }
            $checked++
            foreach ($p in @(Get-StoryProblems -Film $film)) { [void]$bad.Add("$($film.Release): $p") }
        }
        Add-Check "At least one hand-off was checked" ($checked -gt 0) "$checked films with a hand-off"
        Add-Check "No hand-off has a closing line that tells two stories, ends in a doubled full stop, or repeats" ($bad.Count -eq 0) (($bad | Select-Object -First 5) -join " || ")
    }
}
