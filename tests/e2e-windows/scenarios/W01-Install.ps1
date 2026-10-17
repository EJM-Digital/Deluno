<#
    The install leg: install-first-run and outage.tray, on one fresh runner.

    install-first-run is the golden path's own scenario (scenarios/G01-Install.ps1, run unchanged): the tray watcher started before
    Setup, the Deluno icon reading "setting up" with a grey blinking dot while the picker runs, Weir installed through the picker and
    answering on this PC only. Checks are added for what the VM's host script judged around it: Setup and the picker finished, and the
    installed build is the commit under test.

    outage.tray is the tray half of the golden path's outage group (scenarios/G06-Outage.ps1), which cannot be made on Linux: the
    Deluno tray icon's hover text and dot, read from the taskbar through UI Automation, go amber within a minute of Weir quitting and
    green within a minute of it answering again, together with Deluno's own record of the Weir connection. The System and Dashboard
    readings of the same watch are made by the Linux outage group.
#>

# Deluno's first account and the Weir connection (Deluno makes Weir's own account), through the same calls the guided setup makes.
function Initialize-DelunoWithWeir {
    $status = Wait-DelunoUp
    New-SuiteSecret 'deluno-password' | Out-Null
    New-SuiteSecret 'weir-password' | Out-Null
    New-SuiteSecret 'weir-username' | Out-Null
    if ($status.requiresSetup) {
        $r = Try-Deluno -Method POST -Path '/api/auth/bootstrap' -Body @{ username = 'golden'; displayName = 'Windows suite'; password = (Get-SuiteSecret 'deluno-password') }
        if ($r.Status -ne 200) { throw (Hide-Secrets "Creating the first account answered $($r.Status). $($r.Raw)") }
    }
    Connect-Deluno
    $weir = Try-Deluno -Method POST -Path '/api/system/bundled-components/weir/connect' -Body @{ username = (Get-SuiteSecret 'weir-username'); password = (Get-SuiteSecret 'weir-password') } -TimeoutSec 120
    if ($weir.Status -ne 200 -or -not $weir.Data.healthy) { throw (Hide-Secrets "Connecting Weir answered $($weir.Status). $($weir.Raw)") }
    Write-SuiteLog ("   connect weir: {0}" -f $weir.Data.message)
}

# What the tray and Deluno's own record say about Weir right now.
function Read-TrayWeirSignals {
    try { Set-TrayIconsPromoted } catch { }
    $trayRead = $null
    try { $trayRead = Get-TrayDot -Icon 'Deluno' } catch { }
    $conn = $null
    try { $conn = Get-WeirAnswering } catch { }
    [pscustomobject]@{
        At = [DateTime]::UtcNow
        TrayFound = [bool]($trayRead -and $trayRead.Found)
        TrayHover = $(if ($trayRead) { $trayRead.Hover } else { $null }); TrayColour = $(if ($trayRead) { $trayRead.Colour } else { $null })
        TrayAmber = [bool]($trayRead -and ($trayRead.Hover -match "isn't answering|not answering" -or $trayRead.Colour -eq 'amber'))
        TrayGreen = [bool]($trayRead -and $trayRead.Found -and $trayRead.Hover -notmatch "isn't answering|not answering|Starting|setting up" -and $trayRead.Colour -eq 'green')
        ServerHealth = $(if ($conn) { "$($conn.healthStatus)" } else { $null })
    }
}

# Reads until the tray and the server record have each shown the wanted colour, or the time runs out. Seconds since T0, or $null.
function Watch-TrayWeirSignals {
    param([Parameter(Mandatory)] [ValidateSet('amber', 'green')] [string] $Want, [Parameter(Mandatory)] [DateTime] $T0, [int] $TimeoutSec = 120)
    $first = @{ Tray = $null; Server = $null }
    $samples = New-Object System.Collections.ArrayList
    while (([DateTime]::UtcNow - $T0).TotalSeconds -lt $TimeoutSec) {
        $r = Read-TrayWeirSignals
        [void]$samples.Add($r)
        $since = ($r.At - $T0).TotalSeconds
        $hit = @{
            Tray = $(if ($Want -eq 'amber') { $r.TrayAmber } else { $r.TrayGreen })
            Server = $(if ($Want -eq 'amber') { $r.ServerHealth -match 'degraded|unhealthy|down|unreachable' } else { $r.ServerHealth -match 'healthy' -and $r.ServerHealth -notmatch 'un' })
        }
        foreach ($k in @($first.Keys)) { if ($null -eq $first[$k] -and $hit[$k]) { $first[$k] = [math]::Round($since, 1) } }
        if (@($first.Values | Where-Object { $null -eq $_ }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 300 # reason: poll interval inside a loop bounded by its deadline
    }
    [pscustomobject]@{ Tray = $first.Tray; Server = $first.Server; Samples = $samples }
}

function Assert-Within {
    param([string] $Name, $Seconds, [int] $Limit = 60)
    Add-Check $Name ($null -ne $Seconds -and $Seconds -le $Limit) $(if ($null -ne $Seconds) { "after $Seconds s (limit $Limit s)" } else { "never within the watch time (limit $Limit s)" })
}

function Invoke-OutageTray {
    Invoke-Scenario -Id 'outage.tray' -Title 'Weir quits: the Deluno tray icon goes amber within a minute and comes back green within a minute' -Body {
        try {
            Initialize-DelunoWithWeir
            $tray = Get-RunnerPreflight
            Add-Note ("Runner: {0}; session {1}; interactive {2}; explorer {3}; taskbar buttons readable {4}; screen {5}." -f $tray.Os, $tray.SessionId, $tray.UserInteractive, $tray.Explorer, $tray.TaskbarButtons, $tray.Screen)
            # Right after Weir connects Deluno may still read "setting up" or amber for a moment; give it a minute to settle green.
            $settled = Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { $r = Read-TrayWeirSignals; if ($r.TrayGreen) { $r } else { $null } }
            Add-Check 'Before the outage the Deluno tray icon reads green' $settled.Ok $(if ($settled.Ok) { "hover '$($settled.Value.TrayHover)', after $($settled.Seconds) s" } else { 'the icon never read green; the last reading is in the evidence' })
            if (-not $settled.Ok) { Save-RunnerScreen 'tray-not-green'; Save-Evidence -Name 'outage-tray-last-reading.json' -Text ((Read-TrayWeirSignals) | ConvertTo-Json); return }

            $asked = [DateTime]::UtcNow
            $q = Stop-WeirApp
            # "Within a minute" is counted from the moment Weir was gone, as the come-back is counted from the moment it answered.
            $t0 = $q.GoneAt
            Add-Note ("Weir quit at T0, {0:N1} s after it was asked (via its tray menu: {1})." -f ($t0 - $asked).TotalSeconds, $q.ViaTray)
            $amber = Watch-TrayWeirSignals -Want amber -T0 $t0 -TimeoutSec 120
            Assert-Within 'The tray goes amber within a minute' $amber.Tray
            Assert-Within 'Deluno''s own record of the Weir connection goes unhealthy within a minute' $amber.Server
            Save-Evidence -Name 'outage-tray-amber.json' -Text (($amber | Select-Object Tray, Server) | ConvertTo-Json)
            Save-Evidence -Name 'outage-tray-samples.json' -Text (@($amber.Samples | Select-Object -Last 40) | ConvertTo-Json -Depth 4)
            if ($null -eq $amber.Tray) { Save-RunnerScreen 'tray-never-amber' }

            Start-WeirApp
            $startedAt = [DateTime]::UtcNow
            $up = Wait-Until -TimeoutSec 90 -IntervalSec 0.5 -Condition { Test-WeirAnswering }
            Add-Check 'Weir answers again' $up.Ok "after $($up.Seconds) s"
            $t1 = $startedAt.AddSeconds($up.Seconds)
            $green = Watch-TrayWeirSignals -Want green -T0 $t1 -TimeoutSec 120
            Assert-Within 'The tray comes back green within a minute of Weir answering' $green.Tray
            Assert-Within 'Deluno''s own record of the Weir connection comes back within a minute' $green.Server
            Save-Evidence -Name 'outage-tray-green.json' -Text (($green | Select-Object Tray, Server) | ConvertTo-Json)
        }
        finally {
            # Whatever happened, Weir is running again when the leg ends.
            if (-not (Test-WeirAnswering)) {
                try { Start-WeirApp } catch { Write-SuiteLog "   could not start Weir again: $($_.Exception.Message)" }
            }
        }
    }
}

function Invoke-LegInstall {
    param([Parameter(Mandatory)] [string] $Setup, [string] $Commit = '')
    Initialize-RunnerDesktop
    $pre = Get-RunnerPreflight
    Write-SuiteLog ("Runner: {0}; session {1}; interactive {2}; explorer {3}; taskbar buttons readable {4}; screen {5}." -f $pre.Os, $pre.SessionId, $pre.UserInteractive, $pre.Explorer, $pre.TaskbarButtons, $pre.Screen)
    Save-Evidence -Name 'runner-preflight.json' -Text ($pre | ConvertTo-Json)
    Save-RunnerScreen 'before-setup'

    Start-TrayWatch
    $install = $null
    try { $install = Install-CandidateWithPicker -Setup $Setup }
    finally { Stop-TrayWatch }
    Save-RunnerScreen 'after-picker'

    Invoke-GroupInstall
    Add-LateChecks -Id 'install-first-run' -Body {
        Add-Check 'Setup.exe (not silent) ran to its end' ($install -and $null -ne $install.SetupExit -and $install.SetupExit -eq 0) $(if ($install) { "exit $($install.SetupExit) after $($install.Seconds) s in all" } else { 'the install did not run' })
        Add-Check 'The picker driver finished (it followed the picker to its finish screen)' ($install -and $install.PickerFinished -and $install.PickerExit -eq 0) $(if ($install) { "finished $($install.PickerFinished), exit $($install.PickerExit)" } else { '' })
        $exe = Get-InstalledDelunoExe
        $build = if ($exe) { $exe.VersionInfo.ProductVersion } else { '' }
        if ($Commit) { Add-Check 'The installed build is the commit under test' (Test-BuildMatchesCommit -ProductVersion $build -Commit $Commit) "Deluno.exe reports '$build'; commit $Commit" }
        else { Add-Note "Installed build: '$build'." }
        Add-Check 'Deluno answers on its port' (Test-DelunoAnswering) ''
    }

    Invoke-OutageTray
}
