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
    # What Deluno says to the person about telling the processor (imported, will not import) is checked for contradiction and
    # for a doubled full stop. "Could not tell the processor yet, will keep trying" is a step on the way, not a closing line.
    $closing = @($lines | Where-Object { $_ -match "Told the processor what happened|Could not tell the processor" })
    foreach ($c in @($closing | Where-Object { $_ -match "imported the file" -and $_ -match "will not import|will not be imported" })) {
        $flat = $c -replace '\s+', ' '
        [void]$problems.Add('says both imported and will not import: ' + $flat.Substring(0, [math]::Min(240, $flat.Length)))
    }
    foreach ($c in @($closing | Where-Object { $_ -match '[a-z0-9]\.\.(\s|"|\\|$)' })) {
        $flat = $c -replace '\s+', ' '
        [void]$problems.Add('ends in a doubled full stop: ' + $flat.Substring(0, [math]::Min(240, $flat.Length)))
    }
    # One closing line for each hand-off (each send, by its own id). A resent release has the replaced send's one line
    # ("an earlier send needed no report") and the new send's one line: two lines for two sends, never two for one. A hand-off
    # row is reused for a resend under a new id, so the replaced send is no longer in the hand-off list: its id still counts.
    $final = @($lines | Where-Object { $_ -match '"category":"processing\.outcome-(reported|not-reported|superseded)"' })
    $perSend = @{}
    foreach ($f in $final) { $id = if ($f -match 'HandoffId\W+([0-9a-f]{32})') { $Matches[1] } else { 'unknown' }; $perSend[$id] = 1 + [int]$perSend[$id] }
    foreach ($id in @($perSend.Keys | Where-Object { $perSend[$_] -gt 1 })) { [void]$problems.Add("$($perSend[$id]) closing lines for the one send $id") }
    @($problems)
}

# The closing lines of one film read as one story, as checks.
function Test-OneStory {
    param([Parameter(Mandatory)] $Film, [string] $What = $Film.Release)
    $p = @(Get-StoryProblems -Film $Film)
    Add-Check "$What : the closing lines tell one story (no imported-and-will-not-import, no doubled full stop, one line per hand-off)" ($p.Count -eq 0) ($p -join " || ")
}

# Makes sure the story check has a finished hand-off to read. When an earlier scenario of this run already made one, nothing is added (a full
# run reads all of theirs). Otherwise a film is sent through every client, imported, and given time to write its closing line.
function Ensure-StoryFilms {
    $have = @($script:AllFilms | Where-Object { $_.Protocol -and @(Find-Handoffs -Name $_.Release | Where-Object { $_.status -eq 'completed' }).Count -gt 0 })
    if ($have.Count -gt 0) { return }
    Write-SuiteLog '   no hand-off was made earlier in this run: sending one film through every client for the story check.'
    $sent = @{}
    foreach ($kind in Get-AllKinds) {
        try { $sent[$kind] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Label 'story') -Kind $kind }
        catch { Write-SuiteLog "   could not send the $kind story film: $(Hide-Secrets $_.Exception.Message)" }
    }
    foreach ($kind in $sent.Keys) {
        $film = $sent[$kind]
        Wait-FilmImported -Film $film | Out-Null
        # The closing line is written when Deluno has told the processor what happened, a little after the import.
        Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition {
            @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match '"category":"processing\.outcome-(reported|not-reported|superseded)"' }).Count -gt 0
        } | Out-Null
    }
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
        # In a full run this reads every hand-off the earlier groups made. Run alone (or after a few scenarios) there may be none
        # to read, so one film is sent through each client and followed to its closing line first.
        Ensure-StoryFilms
        $films = @($script:AllFilms | Where-Object { $_.Protocol })
        $checked = 0
        $bad = New-Object System.Collections.ArrayList
        foreach ($film in $films) {
            if (@(Find-Handoffs -Name $film.Release).Count -eq 0) { continue }
            $checked++
            $found = @(Get-StoryProblems -Film $film)
            foreach ($p in $found) { [void]$bad.Add("$($film.Release): $p") }
            if ($found.Count) {
                # Every Activity line of that film and every hand-off row of it, so the next run shows which two lines were told
                # (rc.30.22 recorded neither).
                $lines = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) } | ForEach-Object { $_ -replace '\s+', ' ' })
                Save-Evidence -Name "story-$($film.Title).txt" -Text (("problems: " + ($found -join ' || ')) + "`n`nactivity lines:`n" + ($lines -join "`n") + "`n`nhand-offs:`n" + (@(Find-Handoffs -Name $film.Release) | ConvertTo-Json -Depth 4))
            }
        }
        Add-Check "At least one hand-off was checked" ($checked -gt 0) "$checked films with a hand-off"
        Add-Check "No hand-off has a closing line that tells two stories, ends in a doubled full stop, or repeats" ($bad.Count -eq 0) (($bad | Select-Object -First 5) -join " || ")
    }
}
