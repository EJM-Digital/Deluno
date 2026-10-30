<#
    What a person does with the tray icon itself on a real Windows desktop (coverage issue 1278): the clicks, a second launch, the
    sign-in entry and the start options. G15 and G19 cover the menu and the dot; this group covers the icon's own mouse handling
    and the ways Deluno is started.

    tray.clicks-and-second-launch   a left click and a double click on the icon open Deluno once; a middle click opens nothing and a right
                                    click opens the menu; a click while Deluno starts says why in a balloon; a second launch shows the
                                    "already running" balloon and leaves one tray; the sign-in entry holds the quoted exe and no flags;
                                    a start after Quit says "Deluno is running" with the address; --port N serves one run on another port
                                    and is not saved.
#>

# Presses a mouse button on the Deluno icon's own rectangle, once or twice quickly (a double click).
function Send-TrayIconClick {
    param([ValidateSet('Left', 'Right', 'Middle')] [string] $Button = 'Left', [int] $Times = 1)
    [void](Set-TaskbarForeground)
    $btn = Find-TrayIcon -Icon 'Deluno'
    if (-not $btn -or $btn.Element.Current.IsOffscreen) { throw 'the Deluno tray icon is not on the taskbar, so it could not be clicked' }
    $r = $btn.Element.Current.BoundingRectangle
    [Golden.Mouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 200 # reason: UI settle after input; Windows raises no event for it
    $down, $up = switch ($Button) { 'Right' { 0x0008, 0x0010 } 'Middle' { 0x0020, 0x0040 } default { 0x0002, 0x0004 } }
    for ($i = 0; $i -lt $Times; $i++) {
        [Golden.Mouse]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
        [Golden.Mouse]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60 # reason: the gap between the two clicks of a double click, well inside Windows' double-click time
    }
}

# Edge is already open in this session, so a page Deluno opens is a short-lived "msedge.exe --single-argument <address>" that hands the address
# to the running Edge and ends within a moment: a poll misses it, so Windows' process-start events are watched. A start counts as a browser
# opened for the person when its parent is not another msedge.exe (Edge's own helper processes all have an msedge.exe parent).
function Start-EdgeLaunchWatch {
    Get-EventSubscriber -SourceIdentifier 'GoldenEdgeStart' -ErrorAction SilentlyContinue | Unregister-Event -ErrorAction SilentlyContinue
    Get-Event -SourceIdentifier 'GoldenEdgeStart' -ErrorAction SilentlyContinue | Remove-Event -ErrorAction SilentlyContinue
    Register-CimIndicationEvent -Query "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName='msedge.exe'" -SourceIdentifier 'GoldenEdgeStart' | Out-Null
}
function Get-EdgeLaunchCount {
    @(Get-Event -SourceIdentifier 'GoldenEdgeStart' -ErrorAction SilentlyContinue | Where-Object {
        $parent = Get-Process -Id ([int]$_.SourceEventArgs.NewEvent.ParentProcessID) -ErrorAction SilentlyContinue
        $parent -and $parent.ProcessName -ne 'msedge'
    }).Count
}
function Stop-EdgeLaunchWatch {
    Get-EventSubscriber -SourceIdentifier 'GoldenEdgeStart' -ErrorAction SilentlyContinue | Unregister-Event -ErrorAction SilentlyContinue
    Get-Event -SourceIdentifier 'GoldenEdgeStart' -ErrorAction SilentlyContinue | Remove-Event -ErrorAction SilentlyContinue
}

function Start-DelunoTrayNow {
    param([string] $Arguments = '')
    $exe = Get-DelunoExe
    Start-ProgramUnelevated -Path $exe.FullName -Arguments $Arguments -WorkingDirectory $exe.DirectoryName -Elevated
}

function Invoke-GroupTrayPlatform {
    Invoke-Scenario -Id 'tray.clicks-and-second-launch' -Title "Clicking the icon, a second launch and the start options: left and double click open Deluno once, a middle click opens nothing, a click while Deluno starts says why, a second launch leaves one tray and says Deluno is already running, the sign-in entry holds the quoted exe and no flags, a start after Quit says Deluno is running, --port serves one run on another port" -Body {
        Connect-Deluno
        $port = Get-LanPort
        $altPort = $port + 11
        Add-Type -AssemblyName System.Windows.Forms

        try {
            # ---- the sign-in entry
            $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            $readEntry = { param($key) $p = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue; if ($p) { $p.PSObject.Properties | Where-Object { [string]$_.Value -match 'Deluno\.exe' } | Select-Object -First 1 } }
            $hadEntry = [bool](& $readEntry $run)
            if (-not $hadEntry) { Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null }
            $entry = (Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { & $readEntry $run }).Value
            $exe = Get-DelunoExe
            Add-Check 'The sign-in entry holds the quoted path of Deluno.exe and no flags (so a start at sign-in never opens a browser)' ($entry -and [string]$entry.Value -match '^"[^"]+\\Deluno\.exe"$') $(if ($entry) { ([string]$entry.Value) -replace [regex]::Escape($env:LOCALAPPDATA), '%LOCALAPPDATA%' } else { 'no entry' })
            if (-not $hadEntry) { Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null }

            # ---- a left click opens Deluno
            Start-EdgeLaunchWatch
            Send-TrayIconClick -Button Left
            $opened = Wait-Until -TimeoutSec 30 -IntervalSec 0.3 -Condition { (Get-EdgeLaunchCount) -ge 1 }
            Start-Sleep -Seconds 3 # reason: lets the double-click window of the first click pass, and a wrong second open arrive
            Add-Check 'A left click on the icon opens Deluno in the browser, once' ($opened.Ok -and (Get-EdgeLaunchCount) -eq 1) "$(Get-EdgeLaunchCount) browser start(s)"

            # ---- a double click opens it once, not twice
            Start-Sleep -Seconds 2 # reason: the open gate keeps a second open out for a moment after the first
            Start-EdgeLaunchWatch
            Send-TrayIconClick -Button Left -Times 2
            $opened = Wait-Until -TimeoutSec 30 -IntervalSec 0.3 -Condition { (Get-EdgeLaunchCount) -ge 1 }
            Start-Sleep -Seconds 5 # reason: a second, wrong, open would arrive within a moment of the first
            Add-Check 'A double click opens Deluno once (the click that starts it and the double click do not open it twice)' ($opened.Ok -and (Get-EdgeLaunchCount) -eq 1) "$(Get-EdgeLaunchCount) browser start(s)"

            # ---- a middle click opens nothing, a right click opens the menu and nothing else
            Start-Sleep -Seconds 3 # reason: lets the open gate of the double click pass
            Start-EdgeLaunchWatch
            Send-TrayIconClick -Button Middle
            Start-Sleep -Seconds 6 # reason: nothing is expected to happen; the wait is the proof
            Add-Check 'A middle click on the icon opens nothing' ((Get-EdgeLaunchCount) -eq 0) "$(Get-EdgeLaunchCount) browser start(s)"
            Send-TrayIconClick -Button Right
            $menu = Wait-Until -TimeoutSec 10 -IntervalSec 0.5 -Condition { $i = @(Get-OpenMenuItems -Icon 'Deluno'); if ($i.Count -gt 0) { $i } else { $null } }
            Close-TrayMenu
            Start-Sleep -Seconds 2 # reason: a wrong open would arrive within a moment of the click
            Add-Check 'A right click opens the menu and opens no browser' ($menu.Ok -and (Get-EdgeLaunchCount) -eq 0) $(if ($menu.Ok) { ($menu.Value -join ' | ') } else { 'no menu' })
            Stop-EdgeLaunchWatch

            # ---- a second launch
            $toastBefore = Get-ToastTextCount -Text 'Deluno is already running'
            $before = @(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)
            $second = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
            $ended = $second.WaitForExit(40000)
            Add-Check 'A second Deluno started by hand ends by itself within 40 seconds' $ended ''
            if (-not $ended) { try { $second.Kill() } catch { } }
            Add-Check 'Only one tray runs: the same Deluno process is the only one left' (@(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue).Count -eq $before.Count -and $before.Count -ge 1) "$($before.Count) before, $(@(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue).Count) after"
            Add-Check 'The running copy says "Deluno is already running" in a balloon' (Wait-ToastShown -Text 'Deluno is already running' -Before $toastBefore -TimeoutSec 30) 'seen in Windows'' notification record'
            Add-Check 'Deluno still answers' (Test-DelunoAnswering) ''

            # ---- a click while Deluno starts says why
            $why = Get-ToastTextCount -Text "Deluno can't open yet"
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno$' | Out-Null
            # Clicked while the hover says Starting, again after a second while it still does (a click the gate counts as an open is not repeated for half a second).
            $starting = Wait-Until -TimeoutSec 60 -IntervalSec 0.2 -Condition {
                $d = Get-TrayDot -Icon 'Deluno' -NoOverflow
                if ($d.Found -and $d.Hover -match 'Starting') { Send-TrayIconClick -Button Left; $true } else { $null }
            }
            $said = Wait-ToastShown -Text "Deluno can't open yet" -Before $why -TimeoutSec 15
            for ($again = 0; $starting.Ok -and -not $said -and $again -lt 6; $again++) {
                $d = Get-TrayDot -Icon 'Deluno' -NoOverflow
                if (-not ($d.Found -and $d.Hover -match 'Starting')) { break }
                Send-TrayIconClick -Button Left
                $said = Wait-ToastShown -Text "Deluno can't open yet" -Before $why -TimeoutSec 8
            }
            Add-Check 'A click on the icon while Deluno starts says why in a balloon ("Deluno can''t open yet")' ($starting.Ok -and $said) $(if ($starting.Ok) { 'clicked while the hover read Starting' } else { 'the Starting state was not seen' })
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check 'Deluno is back after the restart' $up.Ok ''
            Connect-Deluno

            # ---- a start after Quit announces itself
            $runningBefore = Get-ToastTextCount -Text 'Deluno is running'
            Quit-DelunoFromTray | Out-Null
            Start-DelunoTrayNow
            # The icon is up at once, in the Starting state, never only after the server answers.
            $first = Wait-Until -TimeoutSec 90 -IntervalSec 0.3 -Condition { $d = Get-TrayDot -Icon 'Deluno' -NoOverflow; if ($d.Found) { $d } else { $null } }
            Add-Check 'After a launch the icon appears at once and its first reading is the Starting state ("Deluno - Starting...", not the green or red dot)' ($first.Ok -and $first.Value.Hover -match 'Starting' -and $first.Value.Colour -notin 'green', 'red') $(if ($first.Ok) { "colour $($first.Value.Colour), hover '$($first.Value.Hover)'" } else { 'no icon' })
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check 'Deluno starts again after Quit' $up.Ok ''
            Add-Check 'The first start says "Deluno is running" in a balloon' (Wait-ToastShown -Text 'Deluno is running' -Before $runningBefore -TimeoutSec 60) 'seen in Windows'' notification record'
            Connect-Deluno

            # ---- --port serves one run on another port and is not saved
            Quit-DelunoFromTray | Out-Null
            Start-DelunoTrayNow -Arguments "--port $altPort"
            $alt = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { try { (Invoke-WebRequest -Uri "http://localhost:$altPort/api/auth/bootstrap-status" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $false } }
            Add-Check "Started with --port $altPort, Deluno answers there" $alt.Ok ''
            Add-Check "The saved port ($port) does not answer in that run" ((Get-StatusWithHostHeader -HostHeader "localhost:$port" -Port $port) -eq 0) ''
            $saved = if (Test-Path (Get-TrayAppSettingsPath)) { Get-Content -Raw (Get-TrayAppSettingsPath) } else { '' }
            Add-Check 'The override is not saved in deluno.json' ($saved -notmatch "\b$altPort\b") ''
            Quit-DelunoFromTray | Out-Null
            $back = Restore-DelunoTray
            Add-Check "A plain start is back on the saved port ($port)" ($back -and (Get-LanPort) -eq $port) ''
        }
        finally {
            Stop-EdgeLaunchWatch
            Remove-InboundRulesFor -Port $altPort
            if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) { Start-DelunoTrayNow }
            $null = Restore-DelunoTray
        }
    }
}
