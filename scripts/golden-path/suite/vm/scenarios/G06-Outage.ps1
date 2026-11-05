<#
    outage.failed-handoff-health: a failed hand-off writes "unreachable" on the processor only when nothing answered (Weir answering,
    refusing with a wrong key, and off), and the tray follows.

    Weir outage, with nothing in flight and with a hand-off in flight: the tray, System and the Dashboard go amber within a
    minute and come back within a minute (#1213, #1227). The suite quits Weir from its tray, then reads the three places every
    fraction of a second: the Deluno tray icon (hover text and dot), System and the Dashboard (the text a person reads, in an
    Edge tab each), and Deluno's own record of the Weir connection.
#>

$script:Outage = @{ Pages = $null }

function Open-OutagePages {
    if ($script:Outage.Pages) { return }
    Start-Edge
    $login = $script:S.Login
    $dash = New-CdpPage 'about:blank'; Open-DelunoPage -Page $dash -Path '/' -Login $login
    $sys = New-CdpPage 'about:blank'; Open-DelunoPage -Page $sys -Path '/system' -Login $login
    $script:Outage.Pages = @{ Dashboard = $dash; System = $sys }
}

function Close-OutagePages {
    if (-not $script:Outage.Pages) { return }
    foreach ($p in $script:Outage.Pages.Values) { Close-CdpPage -Page $p }
    $script:Outage.Pages = $null
}

# What the four places say right now about Weir.
function Read-WeirSignals {
    $trayRead = $null
    try { $trayRead = Get-TrayDot -Icon 'Deluno' } catch { }
    $sysText = ''; $dashText = ''
    try { $sysText = Get-PageText -Page $script:Outage.Pages.System } catch { }
    try { $dashText = Get-PageText -Page $script:Outage.Pages.Dashboard } catch { }
    $conn = $null
    try { $conn = Get-WeirAnswering } catch { }
    [pscustomobject]@{
        At = [DateTime]::UtcNow
        TrayHover = $(if ($trayRead) { $trayRead.Hover } else { $null }); TrayColour = $(if ($trayRead) { $trayRead.Colour } else { $null })
        TrayAmber = [bool]($trayRead -and ($trayRead.Hover -match "isn't answering|not answering" -or $trayRead.Colour -eq 'amber'))
        TrayGreen = [bool]($trayRead -and $trayRead.Found -and $trayRead.Hover -notmatch "isn't answering|not answering|Starting|setting up" -and $trayRead.Colour -eq 'green')
        SystemAmber = [bool]($sysText -match '(?s)PROCESSORS.{0,40}?0/1')
        SystemGreen = [bool]($sysText -match '(?s)PROCESSORS.{0,40}?1/1')
        DashboardAmber = [bool]($dashText -match '\d+ not answering|Processor\s*·?\s*not answering|Weir[^\n]{0,60}not answering')
        DashboardGreen = [bool]($dashText -match 'all answering' -and $dashText -notmatch 'not answering')
        ServerHealth = $(if ($conn) { "$($conn.healthStatus)" } else { $null })
    }
}

# Reads the signals until each has been seen (amber, or green) or the time runs out. Returns seconds-since-T0 per place, or $null.
function Watch-WeirSignals {
    param([Parameter(Mandatory)] [ValidateSet('amber', 'green')] [string] $Want, [Parameter(Mandatory)] [DateTime] $T0, [int] $TimeoutSec = 120)
    $first = @{ Tray = $null; System = $null; Dashboard = $null; Server = $null }
    $samples = New-Object System.Collections.ArrayList
    while (([DateTime]::UtcNow - $T0).TotalSeconds -lt $TimeoutSec) {
        $r = Read-WeirSignals
        [void]$samples.Add($r)
        $since = ($r.At - $T0).TotalSeconds
        $hit = @{
            Tray = $(if ($Want -eq 'amber') { $r.TrayAmber } else { $r.TrayGreen })
            System = $(if ($Want -eq 'amber') { $r.SystemAmber } else { $r.SystemGreen })
            Dashboard = $(if ($Want -eq 'amber') { $r.DashboardAmber } else { $r.DashboardGreen })
            Server = $(if ($Want -eq 'amber') { $r.ServerHealth -match 'degraded|unhealthy|down|unreachable' } else { $r.ServerHealth -match 'healthy' -and $r.ServerHealth -notmatch 'un' })
        }
        # A copy of the keys: writing to a hashtable while its own Keys are enumerated throws "Collection was modified" (rc.30.22).
        foreach ($k in @($first.Keys)) { if ($null -eq $first[$k] -and $hit[$k]) { $first[$k] = [math]::Round($since, 1) } }
        if (@($first.Values | Where-Object { $null -eq $_ }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 300 # reason: poll interval inside a loop bounded by its deadline
    }
    [pscustomobject]@{ Tray = $first.Tray; System = $first.System; Dashboard = $first.Dashboard; Server = $first.Server; Samples = $samples }
}

function Assert-Within {
    param([string] $Name, $Seconds, [int] $Limit = 60)
    Add-Check $Name ($null -ne $Seconds -and $Seconds -le $Limit) $(if ($null -ne $Seconds) { "after $Seconds s (limit $Limit s)" } else { "never within the watch time (limit $Limit s)" })
}

function Invoke-OutageOnce {
    param([Parameter(Mandatory)] [string] $Label)
    $before = Read-WeirSignals
    Add-Check "$Label : before the outage every place reads green" ($before.TrayGreen -and $before.SystemGreen -and $before.DashboardGreen) ("tray '{0}' {1}; system green {2}; dashboard green {3}" -f $before.TrayHover, $before.TrayColour, $before.SystemGreen, $before.DashboardGreen)
    $asked = [DateTime]::UtcNow
    $q = Stop-WeirApp
    # "Within a minute" is counted from the moment Weir was gone, as the come-back is counted from the moment it answered.
    $t0 = $q.GoneAt
    Add-Note ("$Label : Weir quit at T0, {0:N1} s after it was asked (via its tray menu: {1})." -f ($t0 - $asked).TotalSeconds, $q.ViaTray)
    $amber = Watch-WeirSignals -Want amber -T0 $t0 -TimeoutSec 120
    Assert-Within "$Label : the tray goes amber within a minute" $amber.Tray
    Assert-Within "$Label : System goes amber within a minute" $amber.System
    Assert-Within "$Label : the Dashboard goes amber within a minute" $amber.Dashboard
    Save-Evidence -Name "outage-$Label-amber.json" -Text ($amber | Select-Object Tray, System, Dashboard, Server | ConvertTo-Json)
    Save-Evidence -Name "outage-$Label-samples.json" -Text (@($amber.Samples | Select-Object -Last 40) | ConvertTo-Json -Depth 4)
    [pscustomobject]@{ T0 = $t0; Amber = $amber }
}

function Complete-Outage {
    param([Parameter(Mandatory)] [string] $Label)
    Start-WeirApp
    $startedAt = [DateTime]::UtcNow
    $up = Wait-Until -TimeoutSec 90 -IntervalSec 0.5 -Condition { Test-WeirAnswering }
    Add-Check "$Label : Weir answers again" $up.Ok "after $($up.Seconds) s"
    # "Within a minute" is counted from the moment Weir answered.
    $t1 = $startedAt.AddSeconds($up.Seconds)
    $green = Watch-WeirSignals -Want green -T0 $t1 -TimeoutSec 120
    Assert-Within "$Label : the tray comes back green within a minute of Weir answering" $green.Tray
    Assert-Within "$Label : System comes back within a minute" $green.System
    Assert-Within "$Label : the Dashboard comes back within a minute" $green.Dashboard
    Save-Evidence -Name "outage-$Label-green.json" -Text ($green | Select-Object Tray, System, Dashboard, Server | ConvertTo-Json)
}

# Whatever an outage scenario did, Weir is running again before the next scenario starts (an error in the first one left it
# down for the second in rc.30.22, so the second never had a hand-off reach Weir).
function Restore-WeirAfterOutage {
    if (Test-WeirAnswering) { return }
    try { Start-WeirApp } catch { Write-SuiteLog "   could not start Weir again: $($_.Exception.Message)" }
    Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { Test-WeirAnswering } | Out-Null
}

function Invoke-GroupOutage {
    try {
        Open-OutagePages
        Invoke-Scenario -Id 'outage.idle' -Title 'Weir quits with nothing in flight: the tray, System and the Dashboard go amber within a minute and come back' -Body {
            try {
                Invoke-OutageOnce -Label 'idle' | Out-Null
                Complete-Outage -Label 'idle'
            }
            finally { Restore-WeirAfterOutage }
        }
        Invoke-Scenario -Id 'outage.inflight' -Title 'Weir quits with a hand-off in flight: the same places go amber and come back, and the hand-off ends clearly' -Body {
            # A hand-off is in flight while Weir holds it unfinished. A film is cleaned in seconds (rc.30.22: the hand-off was
            # sent, cleaned and imported inside 20 s, before the check looked), so Weir is paused: it takes the hand-off and
            # keeps it, and is quit with it held.
            $film = New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Size 'big' -Label 'outage'
            Set-WeirPause -Paused $true
            try {
                Send-Film -Film $film -Kind 'qbittorrent' | Out-Null
                Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
                $w = Wait-Until -TimeoutSec 180 -IntervalSec 1 -Condition {
                    $h = Get-FilmHandoff -Film $film
                    if ($h -and $h.status -in 'waiting', 'submitted', 'accepted', 'started') { $h } else { $null }
                }
                Add-Check 'A hand-off is in flight when Weir quits' $w.Ok $(if ($w.Ok) { "status $($w.Value.status), processor said '$($w.Value.processor.state)'" } else { $h = Get-FilmHandoff -Film $film; if ($h) { Save-Evidence -Name 'outage-inflight-handoff.json' -Text ($h | ConvertTo-Json -Depth 4) }; $(if ($h) { "the hand-off was already $($h.status)" } else { 'no hand-off was made in 3 minutes' }) })
                if (-not $w.Ok) { return }
                try {
                    Invoke-OutageOnce -Label 'inflight' | Out-Null
                    Complete-Outage -Label 'inflight'
                }
                finally { Restore-WeirAfterOutage }
            }
            finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
            # The hand-off must end in a place a person can see: finished, or failed and offered again; never silently open for good.
            $end = Wait-Until -TimeoutSec 300 -IntervalSec 2 -Condition {
                $h = Get-FilmHandoff -Film $film
                if ($h -and ($h.status -in 'completed', 'failed', 'rejected', 'lost', 'timed-out', 'stalled', 'cancelled')) { $h } else { $null }
            }
            Add-Check 'The in-flight hand-off ended clearly after Weir came back (finished, or failed and offered again)' $end.Ok $(if ($end.Ok) { "status $($end.Value.status) after $($end.Seconds) s" } else { 'still open after 5 minutes' })
            if ($end.Ok -and $end.Value.status -ne 'completed') {
                $r = Try-Deluno -Method POST -Path "/api/integrations/processors/handoffs/$($end.Value.id)/retry"
                Add-Check 'Send again works on it' ($r.Status -ge 200 -and $r.Status -lt 300) "HTTP $($r.Status)"
                $s = Wait-HandoffSettled -Film $film -TimeoutSec 240
                Add-Check 'It then settles and imports' $s.Ok ''
            }
        }
        Invoke-Scenario -Id 'outage.failed-handoff-health' -Title 'A failed hand-off writes "unreachable" on the processor only when nothing answered: Weir answering stays healthy, Weir refusing the hand-off (it answered) leaves its stored health alone and the tray quiet, and Weir off is unreachable and amber' -Body {
            $lib = Get-Library -MediaType 'movies'
            $real = Get-WeirAnswering
            Add-Check 'Deluno holds a healthy connection to the real Weir' ($null -ne $real -and $real.healthStatus -eq 'healthy') $(if ($real) { "$($real.name): $($real.healthStatus)" } else { 'no connection' })
            if (-not $real -or $real.healthStatus -ne 'healthy') { return }
            $temp = $null; $films = @(); $moved = $false
            function Set-LibraryProcessor {
                param([Parameter(Mandatory)] $Library, [Parameter(Mandatory)] [string] $ConnectionId, [Parameter(Mandatory)] [string] $Name)
                Try-Deluno -Method PUT -Path "/api/libraries/$($Library.id)/workflow" -Body @{ importWorkflow = $Library.importWorkflow; processorName = $Name; processorOutputPath = $Library.processorOutputPath; processorTimeoutMinutes = $Library.processorTimeoutMinutes; processorFailureMode = $Library.processorFailureMode; processorConnectionId = $ConnectionId } | Out-Null
            }
            try {
                # ---- Weir answers: the hand-off is accepted and the connection reads healthy.
                $f1 = New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'hh1'
                $films += $f1
                Send-Film -Film $f1 -Kind 'qbittorrent' | Out-Null
                $acc = Wait-HandoffSettled -Film $f1 -TimeoutSec 300
                Add-Check 'Weir answering: the hand-off is accepted and settles' $acc.Ok ''
                Add-Check 'Weir answering: the stored health of the connection is healthy' ((Get-WeirAnswering).healthStatus -eq 'healthy') "$((Get-WeirAnswering).healthStatus)"

                # ---- Weir refuses: a second connection to the same Weir with a wrong key. Weir answers 401, so nothing is stored as "unreachable".
                $made = Try-Deluno -Method POST -Path '/api/integrations/processors/connections' -Body @{ name = 'Golden refused Weir'; provider = 'generic'; submissionUrl = $real.submissionUrl; authHeaderName = $real.authHeaderName; secret = 'golden-not-the-key'; isEnabled = $true }
                if ($made.Status -ne 200 -or -not $made.Data.id) { throw "The temporary connection was not made: HTTP $($made.Status) $($made.Raw)" }
                $temp = $made.Data
                Set-LibraryProcessor -Library $lib -ConnectionId $temp.id -Name $temp.name
                $moved = $true
                $f2 = New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'hh2'
                $films += $f2
                Send-Film -Film $f2 -Kind 'qbittorrent' | Out-Null
                $ref = Wait-Until -TimeoutSec 300 -IntervalSec 2 -Condition {
                    $h = Get-FilmHandoff -Film $f2
                    if ($h -and $h.status -in 'failed', 'rejected') { $h } else { $null }
                }
                Save-Evidence -Name 'outage-failed-handoff-refused.json' -Text ($ref | ConvertTo-Json -Depth 5)
                Add-Check 'Weir refusing: the hand-off ends failed or rejected, in words' ($ref.Ok -and -not [string]::IsNullOrWhiteSpace([string]$ref.Value.failureMessage)) $(if ($ref.Ok) { "$($ref.Value.status): $($ref.Value.failureMessage)" } else { 'it did not fail' })
                $tempNow = @(Invoke-Deluno -Path '/api/integrations/processors/connections') | Where-Object { $_.id -eq $temp.id } | Select-Object -First 1
                Add-Check 'Weir refusing: the connection is not stored as unreachable (Weir answered)' ($tempNow -and "$($tempNow.healthStatus)" -ne 'unreachable') "stored health: $($tempNow.healthStatus)"
                $realNow = Get-WeirAnswering
                Add-Check 'Weir refusing: the real Weir connection is still healthy' ($realNow.healthStatus -eq 'healthy') "$($realNow.healthStatus)"
                $tray = Get-TrayDot -Icon 'Deluno'
                Add-Check 'Weir refusing: the tray does not say a processor is not answering' ($tray.Found -and $tray.Hover -notmatch 'Golden refused Weir' ) "colour $($tray.Colour), hover '$($tray.Hover)'"
                Set-LibraryProcessor -Library $lib -ConnectionId $real.id -Name $real.name
                $moved = $false

                # ---- Weir off: nothing answers, so the connection is stored as unreachable and the tray goes amber.
                $q = Stop-WeirApp
                $f3 = New-FilmFor -Kind 'qbittorrent' -Layout 'file' -Label 'hh3'
                $films += $f3
                Send-Film -Film $f3 -Kind 'qbittorrent' | Out-Null
                $unr = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { $c = Get-WeirAnswering; if ($c -and "$($c.healthStatus)" -match 'unreachable') { $c } else { $null } }
                Add-Check 'Weir off: the failed hand-off stores the connection as unreachable' $unr.Ok $(if ($unr.Ok) { "after $($unr.Seconds) s" } else { "stored health: $((Get-WeirAnswering).healthStatus)" })
                $amber = Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { $d = Get-TrayDot -Icon 'Deluno'; if ($d.Found -and ($d.Colour -eq 'amber' -or $d.Hover -match "isn't answering|not answering")) { $d } else { $null } }
                Add-Check 'Weir off: the tray goes amber' $amber.Ok $(if ($amber.Ok) { "hover '$($amber.Value.Hover)'" } else { 'it stayed quiet' })
                Start-WeirApp
                $up = Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { Test-WeirAnswering }
                Add-Check 'Weir answers again' $up.Ok ''
                $back = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition { (Get-WeirAnswering).healthStatus -eq 'healthy' }
                Add-Check 'Once Weir answers, the stored health is healthy again' $back.Ok "$((Get-WeirAnswering).healthStatus)"
            }
            finally {
                Restore-WeirAfterOutage
                if ($moved) { Set-LibraryProcessor -Library $lib -ConnectionId $real.id -Name $real.name }
                if ($temp) { Try-Deluno -Method DELETE -Path "/api/integrations/processors/connections/$($temp.id)" | Out-Null }
                foreach ($f in $films) {
                    $h = Get-FilmHandoff -Film $f
                    if ($h -and $h.status -in 'failed', 'rejected', 'waiting') { Try-Deluno -Method POST -Path "/api/integrations/processors/handoffs/$($h.id)/dismiss" | Out-Null }
                    if ($f.MovieId) { Try-Deluno -Method DELETE -Path "/api/movies/$($f.MovieId)" | Out-Null }
                }
            }
        }
    }
    finally { Restore-WeirAfterOutage; Close-OutagePages }
}
