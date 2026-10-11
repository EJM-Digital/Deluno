<#
    "I don't want it", then a resend of the same release to the same client, in each timing:
      before  - Weir has not started the first send (Weir is paused);
      during  - Weir is working on the first send;
      after   - Weir has finished the first send but Deluno has not queued its import.
    The new hand-off must settle on its own with one import, the first send's late report must be quiet, and nothing may be
    left unmatched in Weir's output (#1208, #1226). Every client runs every timing.
#>

# Sends a film's release again, to the same client, as "Search now" on the title does after "I don't want it".
function Send-FilmAgain {
    param([Parameter(Mandatory)] $Film)
    Restore-FilmData -Film $Film
    $r = Try-Deluno -Method POST -Path "/api/movies/$($Film.MovieId)/grab" -Body @{
        releaseName = $Film.Release; indexerId = $Film.IndexerId; indexerName = $(if ($Film.Protocol -eq 'usenet') { 'Golden Newznab stub' } else { 'Golden feed' })
        candidateQuality = 'WEB 1080p'; downloadUrl = $Film.Url; sizeBytes = $Film.Size; seeders = 50; force = $true
        overrideReason = 'Golden scenario suite: sending the same release again'; duplicateAction = $null
    }
    $Film.SentUtc = [DateTime]::UtcNow
    $r
}

# After a torrent is added again to a client that deleted its data, the client needs its data checked.
function Start-AgainDownload {
    param([Parameter(Mandatory)] $Film)
    $r = Send-FilmAgain -Film $Film
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "Sending $($Film.Release) again answered $($r.Status). $($r.Raw)") }
    Wait-FilmFinished -Film $Film -TimeoutSec 180 | Out-Null
}

# What must be true once a resend has settled.
function Assert-ResendSettled {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] $First, [int] $TimeoutSec = 180, [string] $What)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 1 -Condition {
        $second = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -ne $First.id } | Select-Object -First 1
        if ($second -and $second.status -eq 'completed' -and $second.importJobId) { $second } else { $null }
    }
    $second = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -ne $First.id } | Select-Object -First 1
    Add-Check "$What : the new hand-off settles on its own" $w.Ok $(if ($second) { "status $($second.status), processor $($second.processor.state), output $($second.outputPath), after $($w.Seconds) s" } else { 'there is no second hand-off' })
    $lib = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { Find-Imported -Title $Film.Title }
    Add-Check "$What : the film is in the library" $lib.Ok ''
    $files = @(Get-ChildItem -Path 'C:\Media\Movies' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Film.Title) -and $_.Extension -in '.mkv', '.mp4' })
    Add-Check "$What : exactly one copy is in the library" ($files.Count -eq 1) "$($files.Count) files"
    $leftInWeir = @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Film.Title) })
    Add-Check "$What : nothing of it waits in Weir's output" ($leftInWeir.Count -eq 0) (($leftInWeir | ForEach-Object { $_.FullName }) -join '; ')
    $firstNow = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -eq $First.id } | Select-Object -First 1
    Add-Check "$What : the first send is closed, not left open" ($firstNow -and $firstNow.status -notin 'submitted', 'waiting', 'accepted', 'started') $(if ($firstNow) { $firstNow.status } else { 'gone' })
    $unmatched = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Unmatched in processor output' })
    Add-Check "$What : no 'unmatched in processor output' problem was raised" ($unmatched.Count -eq 0) "$($unmatched.Count) rows"
    $import = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Imported into the library' })
    Add-Check "$What : it was imported once" ($import.Count -eq 1) "$($import.Count) 'Imported into the library' rows"
    Test-OneStory -Film $Film -What $What
}

function Invoke-GroupResend {
    # ---- before: Weir paused, every client in one window.
    $films = @{}
    $firsts = @{}
    try {
        Set-WeirPause -Paused $true
        foreach ($kind in Get-AllKinds) {
            $f = New-FilmFor -Kind $kind -Layout 'file' -Label 'rebefore'
            $films[$kind] = Send-Film -Film $f -Kind $kind
        }
        foreach ($kind in Get-AllKinds) {
            $f = $films[$kind]
            Wait-FilmFinished -Film $f -TimeoutSec 240 | Out-Null
            $w = Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { Get-FilmHandoff -Film $f }
            $firsts[$kind] = $w.Value
        }
        foreach ($kind in Get-AllKinds) {
            $f = $films[$kind]
            if ($firsts[$kind]) {
                Remove-FilmDownload -Film $f -Reason 'unwanted' | Out-Null
                Start-Sleep -Seconds 1
                Start-AgainDownload -Film $f
            }
        }
        # Every client has its second hand-off before Weir is resumed.
        Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition {
            $missing = 0
            foreach ($k in Get-AllKinds) {
                if (-not $firsts[$k]) { continue }
                $firstId = $firsts[$k].id
                $another = @(Find-Handoffs -Name $films[$k].Release) | Where-Object { $_.id -ne $firstId }
                if (-not $another) { $missing++ }
            }
            $missing -eq 0
        } | Out-Null
    }
    finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "resend.before.$kind" -Title "I don't want it, then a resend to $kind, before Weir starts the first send" -Body {
            if (-not $firsts[$kind]) { throw "The first hand-off for $kind never appeared." }
            Assert-ResendSettled -Film $films[$kind] -First $firsts[$kind] -What "before/$kind"
        }
    }

    # ---- during and after: one client at a time.
    foreach ($timing in 'during', 'after') {
        foreach ($kind in Get-AllKinds) {
            Invoke-Scenario -Id "resend.$timing.$kind" -Title "I don't want it, then a resend to $kind, while Weir is $(if ($timing -eq 'during') { 'working on' } else { 'done with' }) the first send" -Body {
                $reached = $null; $film = $null; $first = $null
                for ($try = 1; $try -le 3 -and -not $reached; $try++) {
                    $film = New-FilmFor -Kind $kind -Layout 'file' -Label "re$timing"
                    Send-Film -Film $film -Kind $kind | Out-Null
                    Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
                    $w = Wait-Until -TimeoutSec 240 -IntervalSec 0.2 -Condition {
                        $h = Get-FilmHandoff -Film $film
                        if (-not $h) { return $null }
                        $state = "$($h.processor.state)"
                        if ($timing -eq 'during' -and $state -match 'process|running|analys|clean|start|work' -and $h.status -ne 'completed') { return $h }
                        if ($timing -eq 'after' -and ($state -match 'complet|done|finish' -or $h.status -eq 'completed') -and -not $h.importJobId) { return $h }
                        if ($h.importJobId) { return $h }   # too late: the import was queued before the moment was seen
                        $null
                    }
                    if ($w.Ok -and -not $w.Value.importJobId) { $reached = $true; $first = $w.Value }
                    else { Add-Note "Attempt ${try}: the $timing moment was missed (processor said '$($w.Value.processor.state)', status '$($w.Value.status)'); trying a new film." }
                }
                Add-Check "The $timing moment was reached with the first send still open" ([bool]$reached) $(if ($first) { "processor '$($first.processor.state)', status '$($first.status)'" } else { 'missed three times' })
                if (-not $reached) { return }
                Remove-FilmDownload -Film $film -Reason 'unwanted' | Out-Null
                Start-Sleep -Seconds 1
                Start-AgainDownload -Film $film
                Assert-ResendSettled -Film $film -First $first -What "$timing/$kind"
            }
        }
    }
}
