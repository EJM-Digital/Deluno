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
    # The library is routed to one client at a time and the other films were sent since (rc.30.22: a qBittorrent film sent
    # again while the library pointed at NZBGet answered 400, "no download client that can take a release from Golden feed").
    # "Search now" on the title sends to the client the title's library is routed to, so route it back first.
    Set-LibraryClient -Kind $Film.ClientKind | Out-Null
    $r = Try-Deluno -Method POST -Path "/api/movies/$($Film.MovieId)/grab" -Body @{
        releaseName = $Film.Release; indexerId = $Film.IndexerId; indexerName = $(if ($Film.Protocol -eq 'usenet') { 'Golden Newznab stub' } else { 'Golden feed' })
        candidateQuality = 'WEB 1080p'; downloadUrl = $Film.Url; sizeBytes = $Film.Size; seeders = 50; force = $true
        overrideReason = 'Golden scenario suite: sending the same release again'; duplicateAction = $null
    }
    $Film.SentUtc = [DateTime]::UtcNow
    $r
}

# After a torrent is added again to a client that deleted its data, the client needs its data checked. The removal is waited for
# first: a queue row still there is the removed download, and a client that kept it (rc.30.23: Deluge, which has the same id for
# the same torrent) made "finished" true at once, so the second send was never watched.
function Start-AgainDownload {
    param([Parameter(Mandatory)] $Film)
    if ($Film.Protocol -eq 'torrent') { Wait-Until -TimeoutSec 30 -IntervalSec 0.5 -Condition { -not (Find-QueueRow -Name $Film.Release) } | Out-Null }
    $r = Send-FilmAgain -Film $Film
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "Sending $($Film.Release) again answered $($r.Status). $($r.Raw)") }
    # The client must be told to look at the data again as soon as its row is there, as the first send was.
    if (Test-ClientNeedsSettle -Kind $Film.ClientKind) { Invoke-FilmSettle -Film $Film -WaitForRowSec 60 | Out-Null }
    $w = Wait-FilmFinished -Film $Film -TimeoutSec 180
    if (-not $w.Ok) { throw "$($Film.Release) did not finish in $($Film.ClientKind) after being sent again (its client row and folders are in the evidence)." }
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
    # A resend reuses the film's hand-off row under a new id, so the first send's id is no longer listed once the new send exists:
    # that is the first send closed (it is kept as a replaced send and has its own closing line). Left open means it is still listed in an open state.
    $firstNow = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -eq $First.id } | Select-Object -First 1
    Add-Check "$What : the first send is closed, not left open" (-not $firstNow -or $firstNow.status -notin 'submitted', 'waiting', 'accepted', 'started') $(if ($firstNow) { $firstNow.status } else { 'replaced by the new send' })
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
                try { Start-AgainDownload -Film $f } catch { Write-SuiteLog "   $($f.Release): $(Hide-Secrets $_.Exception.Message)" }
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

    # ---- during and after: one client at a time. The first send is held at Weir (paused) until Weir is let go, so the moment is
    # made, not waited for: a film is cleaned in about a second and Deluno hears of it at once (rc.30.23: five clients, three tries
    # each, the hand-off was already imported when the check first looked, 60 s after the send).
    foreach ($timing in 'during', 'after') {
        foreach ($kind in Get-AllKinds) {
            Invoke-Scenario -Id "resend.$timing.$kind" -Title "I don't want it, then a resend to $kind, while Weir is $(if ($timing -eq 'during') { 'working on' } else { 'done with' }) the first send" -Body {
                $reached = $null; $film = $null; $first = $null; $what = ''
                for ($try = 1; $try -le 3 -and -not $reached; $try++) {
                    $film = New-FilmFor -Kind $kind -Layout 'file' -Label "re$timing" -Size $(if ($timing -eq 'during') { 'big' } else { 'small' })
                    try {
                        Set-WeirPause -Paused $true
                        Send-Film -Film $film -Kind $kind | Out-Null
                        Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
                        $queued = Wait-Until -TimeoutSec 120 -IntervalSec 0.5 -Condition { Get-FilmHandoff -Film $film }
                        if (-not $queued.Ok) { $what = 'the first hand-off never appeared'; Add-Note "Attempt ${try}: $what."; continue }
                        # Weir takes the first send now. During: removed and sent again straight away, while it works. After: once its
                        # cleaned file is in its output, before Deluno has queued the import.
                        Set-WeirPause -Paused $false
                        if ($timing -eq 'after') {
                            Wait-Until -TimeoutSec 60 -IntervalSec 0.05 -Condition { @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($film.Title) }).Count -gt 0 } | Out-Null
                        }
                        $h = Get-FilmHandoff -Film $film
                        $open = $h -and -not $h.importJobId -and ($timing -eq 'after' -or $h.status -ne 'completed')
                        if ($open) { $reached = $true; $first = $h }
                        else { $what = "status '$($h.status)', import $(if ($h.importJobId) { 'queued' } else { 'not queued' })"; Add-Note "Attempt ${try}: the $timing moment was missed ($what); trying a new film." }
                    }
                    finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
                }
                Add-Check "The $timing moment was reached with the first send still open" ([bool]$reached) $(if ($first) { "processor '$($first.processor.state)', status '$($first.status)'" } else { "missed three times ($what)" })
                if (-not $reached) { return }
                Remove-FilmDownload -Film $film -Reason 'unwanted' | Out-Null
                Start-Sleep -Seconds 1
                Start-AgainDownload -Film $film
                Assert-ResendSettled -Film $film -First $first -What "$timing/$kind"
            }
        }
    }
}
