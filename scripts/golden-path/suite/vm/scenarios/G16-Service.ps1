<#
    Deluno as a Windows service, on a real Windows (coverage issues 1278 and 1295).

    service.install-run-uninstall   from the tray (Advanced > Run as a Windows service... > Windows Service - Local System > Apply): the tray stops
                                    its host and leaves, the service "Deluno" exists (auto start, description, restart-on-failure actions, runs as
                                    LocalSystem), answers on the port with the same sign-in rules, comes back by itself after it is killed, and
                                    --uninstall-service removes it. The tray is then started again, as a person would.
    service.wrong-password          --install-service with a named user and a wrong password: the service is created but does not run.

    Deluno runs elevated in the suite VM, so the service manager's permission prompts (one per sc.exe call when the tray is not elevated)
    do not appear; the prompt itself is covered by the firewall group. The service opens on the data folder the tray used
    (it has the account, so first-run setup is not asked again; register row SVC-06, #1370).
#>

function Get-DelunoServiceFacts {
    $svc = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue
    if (-not $svc) { return $null }
    $q = & sc.exe qfailure Deluno | Out-String
    $f = & sc.exe qfailureflag Deluno | Out-String
    $d = & sc.exe qdescription Deluno | Out-String
    [pscustomobject]@{
        State = [string]$svc.State; StartMode = [string]$svc.StartMode; Account = [string]$svc.StartName; Path = [string]$svc.PathName
        Display = [string]$svc.DisplayName; ProcessId = [int]$svc.ProcessId
        Failure = $q; FailureFlag = $f; Description = $d
    }
}

function Remove-DelunoServiceNow {
    $exe = Get-DelunoExe
    if ($exe) { & $exe.FullName --uninstall-service | Out-Null }
    Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { -not (Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue) } | Out-Null
}

# Starts the tray again as the suite's session user (elevated, as the install left it) and waits until Deluno answers with the suite's login.
function Restore-DelunoTray {
    if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) {
        $exe = Get-DelunoExe
        Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
    }
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
    if ($up.Ok) { Connect-Deluno }
    $up.Ok
}

function Invoke-GroupService {
    Invoke-Scenario -Id 'service.install-run-uninstall' -Title "Run as a Windows service... from the tray: the tray leaves, the service exists with its auto start and restart-on-failure actions, answers with the same sign-in rules, comes back after it is killed, and --uninstall-service removes it" -Body {
        Remove-DelunoServiceNow
        $port = Get-LanPort
        $proc = Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc -or (Test-ProcessElevated -ProcessId $proc.Id) -ne $true) { Restart-DelunoElevated | Out-Null }
        try {
            $adv = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Advanced$'
            $item = (Wait-Until -TimeoutSec 5 -IntervalSec 0.3 -Condition { Get-OpenMenuItems | Where-Object { $_ -match '^Run as a Windows service' } | Select-Object -First 1 }).Value
            Add-Check 'Advanced shows "Run as a Windows service..."' ($adv -and $null -ne $item) $item
            if (-not $item) { return }
            # A dialog opens from the next item, so it is pressed with the mouse (Invoke would not return until the dialog closed).
            $uia = [System.Windows.Automation.AutomationElement]
            $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
            foreach ($el in $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
                if ($el.Current.Name -match '^Run as a Windows service') { $r = $el.Current.BoundingRectangle; Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)); break }
            }
            $dlg = Wait-DesktopWindow -NamePattern '^Startup Mode$' -Process 'Deluno' -TimeoutSec 20
            Add-Check 'The "Startup Mode" dialog opens with the three ways to start' ($null -ne $dlg -and (Get-WindowText -Window $dlg) -match 'Start at login' -and (Get-WindowText -Window $dlg) -match 'Local System' -and (Get-WindowText -Window $dlg) -match 'Run as user') $(if ($dlg) { Get-WindowText -Window $dlg } else { Format-DesktopWindows })
            if (-not $dlg) { return }
            $radio = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'RadioButton' -and $_.Name -match 'Local System' } | Select-Object -First 1
            $radio.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Click-WindowButton -Window $dlg -NamePattern '^Apply$' | Out-Null

            $left = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $proc.Id }) }
            Add-Check 'The tray stops its host and leaves (its icon is gone)' ($left.Ok -and -not (Find-TrayIcon -Icon 'Deluno')) "tray process $($proc.Id) ended: $($left.Ok)"
            $facts = (Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { $x = Get-DelunoServiceFacts; if ($x -and $x.State -eq 'Running') { $x } else { $null } }).Value
            if (-not $facts) { $facts = Get-DelunoServiceFacts }
            Save-Evidence -Name 'service-facts.txt' -Text ($facts | Format-List * | Out-String)
            Add-Check 'The service "Deluno" exists, starts automatically and runs' ($facts -and $facts.StartMode -eq 'Auto' -and $facts.State -eq 'Running') "$($facts.State) / $($facts.StartMode)"
            Add-Check 'It runs "Deluno.exe --service" as LocalSystem, named "Deluno Media Manager"' ($facts -and $facts.Path -match '--service' -and $facts.Account -eq 'LocalSystem' -and $facts.Display -eq 'Deluno Media Manager') "$($facts.Account) | $($facts.Display)"
            Add-Check 'It has its description' ($facts.Description -match 'media acquisition') ($facts.Description -replace '\s+', ' ')
            Add-Check 'Windows restarts it after a failure: restart after 60 s three times, reset after a day, also for a stop with an error code' ($facts.Failure -match '86400' -and ([regex]::Matches($facts.Failure, 'RESTART -- Delay = 60000').Count -ge 3) -and $facts.FailureFlag -match 'TRUE') (($facts.Failure -replace '\s+', ' ') + ' | ' + ($facts.FailureFlag -replace '\s+', ' '))
            $run = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
            Add-Check 'The tray''s sign-in entry is removed (the service starts Deluno now)' (-not ($run -and ($run.PSObject.Properties | Where-Object { [string]$_.Value -match 'Deluno\.exe' }))) ''

            $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
            Add-Check "The service answers on the configured port ($port)" $up.Ok ''
            if ($up.Ok) {
                $boot = Try-Deluno -Path '/api/auth/bootstrap-status'
                # The service keeps the data folder the tray used (#1370): the tray's Deluno has an account, so a service on its data does not ask for first-run setup.
                Add-Check "The service opens on the same data as the tray did: it has the account (requiresSetup is false), not an empty first-run" ($boot.Status -eq 200 -and $boot.Data.requiresSetup -eq $false) "bootstrap-status: HTTP $($boot.Status) requiresSetup=$($boot.Data.requiresSetup)"
                $code = try { (Invoke-WebRequest -Uri "http://localhost:$port/api/libraries" -UseBasicParsing -TimeoutSec 20).StatusCode } catch { try { [int]$_.Exception.Response.StatusCode } catch { 0 } }
                Add-Check 'The service has the same request pipeline: a protected route answers 401 without a sign-in' ($code -eq 401) "HTTP $code"
                Add-Check 'The service serves the web app' ((Invoke-WebRequest -Uri "http://localhost:$port/" -UseBasicParsing -TimeoutSec 20).Content -match '<div id="root"') ''
            }

            # Killed: Windows starts it again after the 60 s delay.
            $pid0 = $facts.ProcessId
            if ($pid0) {
                Stop-Process -Id $pid0 -Force
                $again = Wait-Until -TimeoutSec 240 -IntervalSec 5 -Condition { $x = Get-DelunoServiceFacts; if ($x -and $x.State -eq 'Running' -and $x.ProcessId -ne 0 -and $x.ProcessId -ne $pid0) { $x } else { $null } }
                Add-Check 'Killed, the service is started again by Windows (a new process within four minutes)' $again.Ok $(if ($again.Ok) { "process $pid0 -> $($again.Value.ProcessId) after $($again.Seconds) s" } else { 'still ' + (Get-DelunoServiceFacts).State })
            }
        }
        finally {
            Remove-DelunoServiceNow
            Add-Check '--uninstall-service removes the service' (-not (Get-DelunoServiceFacts)) ''
            Add-Check 'The tray starts again and Deluno answers with its own data' (Restore-DelunoTray) ''
        }
    }

    Invoke-Scenario -Id 'service.wrong-password' -Title "--install-service with a named user and a wrong password creates the service, which does not run" -Body {
        Remove-DelunoServiceNow
        $exe = Get-DelunoExe
        $user = 'goldensvc'
        & net.exe user $user 'Gp-Right-1x2y' /add | Out-Null
        try {
            $proc = Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Select-Object -First 1
            # The tray holds the port; the service is created and started with a password that is not the account's.
            & $exe.FullName --install-service --username ".\$user" --password 'Gp-Wrong-9' | Out-Null
            $made = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { Get-DelunoServiceFacts }
            Add-Check 'The service is created' $made.Ok ''
            Start-Sleep -Seconds 10 # reason: gives Windows time to try the logon and fail; there is no event to wait on
            $f = Get-DelunoServiceFacts
            Add-Check 'It is set to log on as the named user' ($f -and $f.Account -match $user) $f.Account
            Add-Check 'It does not run with a wrong password' ($f -and $f.State -ne 'Running') "state $($f.State)"
        }
        finally {
            Remove-DelunoServiceNow
            & net.exe user $user /delete | Out-Null
            Add-Check 'The test account and the service are gone, and the tray''s Deluno still answers' (-not (Get-DelunoServiceFacts) -and (Restore-DelunoTray)) ''
        }
    }
}
