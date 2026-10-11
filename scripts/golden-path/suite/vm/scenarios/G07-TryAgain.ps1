<#
    Try again on a failed hand-off (#1204, #1229): with Weir down, a film's hand-off fails; Weir comes back; the drawer's
    "Send to Weir ... again" is pressed and the drawer is read every 200 ms until the film is imported. At no step may the old
    failure show again, and the drawer's header must follow the real hand-off. Every client's film goes through it; the five
    hand-offs fail in one outage.
#>

$script:OldFailure = 'Needs your action|hand-?off failed|Could not send to|Rejected Action|could not submit'

function Invoke-GroupTryAgain {
    $films = @{}
    $failed = @{}
    Stop-WeirApp | Out-Null
    try {
        foreach ($kind in Get-AllKinds) {
            $f = New-FilmFor -Kind $kind -Layout 'file' -Label 'retry'
            $films[$kind] = Send-Film -Film $f -Kind $kind
        }
        foreach ($kind in Get-AllKinds) {
            $f = $films[$kind]
            Wait-FilmFinished -Film $f -TimeoutSec 240 | Out-Null
            $w = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition {
                $h = Get-FilmHandoff -Film $f
                if ($h -and $h.status -in 'failed', 'rejected') { $h } else { $null }
            }
            $failed[$kind] = $w.Value
        }
    }
    finally { Start-WeirApp }
    $up = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { Test-WeirAnswering }
    # Give Deluno one check to notice Weir is back before the first press (a person would not press at once).
    Start-Sleep -Seconds 20

    Start-Edge
    $page = New-CdpPage 'about:blank'
    try {
        foreach ($kind in Get-AllKinds) {
            Invoke-Scenario -Id "tryagain.$kind" -Title "Try again on a failed $kind hand-off: no old failure at any step, the drawer follows the hand-off" -Body {
                $f = $films[$kind]
                Add-Check 'Weir is answering again' $up.Ok ''
                $h = $failed[$kind]
                Add-Check 'The hand-off failed while Weir was down' ($null -ne $h) $(if ($h) { "status $($h.status): $($h.failureMessage)" } else { 'it never failed' })
                if (-not $h) { return }
                Open-DelunoPage -Page $page -Path '/queue' -Login $script:S.Login
                $opened = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { Click-PageText -Page $page -Pattern ([regex]::Escape($f.Title)) -TimeoutSec 0 -RowOnly }
                Add-Check 'The download opens in Transfers' $opened.Ok ''
                if (-not $opened.Ok) { return }
                $btn = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { (Get-DrawerText -Page $page) -match 'Send to .* again' }
                Add-Check 'The drawer offers "Send to Weir ... again"' $btn.Ok ''
                if (-not $btn.Ok) {
                    # What the person would have seen, and what the server said at that moment, so the next run can tell a drawer that
                    # lacks the button from a drawer that was never the right one (rc.30.22 recorded neither).
                    $now = Get-FilmHandoff -Film $f
                    Save-Evidence -Name "tryagain-$kind-nobutton.txt" -Text ("page: {0}`nhand-off now: {1}`n`ndrawer or page text:`n{2}" -f (Invoke-CdpEval -Page $page -Script 'location.href'), $(if ($now) { "$($now.status) / $($now.failureMessage)" } else { 'none' }), (Get-DrawerText -Page $page))
                    return
                }
                $before = Get-DrawerText -Page $page
                Add-Check 'Before the press the drawer shows the failure' ($before -match $script:OldFailure) ''

                $pressedAt = [DateTime]::UtcNow
                $clicked = Click-PageText -Page $page -Pattern '^Send to .* again'
                Add-Check 'Try again was pressed' $clicked ''
                $samples = New-Object System.Collections.ArrayList
                $serverMoved = $null; $headerMoved = $null; $sawSending = $null
                $imported = $false
                while (([DateTime]::UtcNow - $pressedAt).TotalSeconds -lt 180) {
                    $t = Get-DrawerText -Page $page
                    $now = [DateTime]::UtcNow
                    [void]$samples.Add([pscustomobject]@{ At = $now; Text = ($t -replace '\s+', ' ') })
                    $cur = Get-FilmHandoff -Film $f
                    if (-not $serverMoved -and $cur -and $cur.status -notin 'failed', 'rejected') { $serverMoved = $now }
                    if (-not $sawSending -and $t -match 'Sending to .* again') { $sawSending = $now }
                    if ($serverMoved -and -not $headerMoved -and $t -notmatch 'Sending to .* again') { $headerMoved = $now }
                    if ($cur -and $cur.status -eq 'completed' -and $cur.importJobId -and (Find-Imported -Title $f.Title)) { $imported = $true; Start-Sleep -Seconds 2; break }
                    Start-Sleep -Milliseconds 200
                }
                Add-Check 'The film was imported after Try again' $imported ''
                $bad = @($samples | Where-Object { $_.Text -match $script:OldFailure })
                Add-Check 'No old failure showed at any step after the press' ($bad.Count -eq 0) ("{0} samples every ~200 ms; {1} showed the old failure{2}" -f $samples.Count, $bad.Count, $(if ($bad.Count) { ' first at +' + [math]::Round(($bad[0].At - $pressedAt).TotalSeconds, 1) + ' s: ' + $bad[0].Text.Substring(0, [math]::Min(160, $bad[0].Text.Length)) } else { '' }))
                Add-Check 'The header said "Sending to Weir ... again" right after the press' ($null -ne $sawSending -and ($sawSending - $pressedAt).TotalSeconds -le 3) $(if ($sawSending) { "at +{0} s" -f [math]::Round(($sawSending - $pressedAt).TotalSeconds, 1) } else { 'never' })
                if ($serverMoved) {
                    $lag = if ($headerMoved) { ($headerMoved - $serverMoved).TotalSeconds } else { 999 }
                    Add-Check 'The drawer follows the real hand-off (its header moves on within 10 s of Deluno submitting)' ($lag -le 10) ("hand-off submitted at +{0} s; header moved on {1}" -f [math]::Round(($serverMoved - $pressedAt).TotalSeconds, 1), $(if ($headerMoved) { 'after ' + [math]::Round($lag, 1) + ' s' } else { 'never' }))
                }
                $steps = @($samples | ForEach-Object { if ($_.Text -match '(Sending to [^.]{0,40} again|Waiting for processor|Import queued|Imported|Cleaning|Processing)') { $Matches[1] } } | Select-Object -Unique)
                Add-Note ("steps read in the drawer: " + ($steps -join ' > '))
                Save-Evidence -Name "tryagain-$kind.json" -Text (@($samples | Select-Object @{ n = 't'; e = { [math]::Round(($_.At - $pressedAt).TotalSeconds, 2) } }, Text) | ConvertTo-Json -Depth 3)
            }
        }
    }
    finally { Close-CdpPage -Page $page }
}
