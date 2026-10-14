<#
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
    }
    finally { Restore-WeirAfterOutage; Close-OutagePages }
}
