<#
    Deluno as a Windows service, on a real Windows (coverage issues 1278 and 1295).

    service.install-run-uninstall   from the tray (Advanced > Run as a Windows service... > Windows Service - Local System > Apply): the tray stops
                                    its host and leaves, the service "Deluno" exists (auto start, description, restart-on-failure actions, runs as
                                    LocalSystem), answers on the port with the same sign-in rules, comes back by itself after it is killed, and
                                    --uninstall-service removes it. The tray is then started again, as a person would.
    service.let-any-address-in      With no tray: Allowed Hosts turns an address away, allowedHostsForceDisabled in the service's settings file lets it in after a
                                    service restart without touching the saved choice, and removing it turns the address away again.
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
            # A click that lands while the submenu is still moving into place closes the menu and opens nothing (rc.30.36 run,
            # 28 Oct), so the menu is opened and the item pressed again, up to three times, until the dialog is up.
            $uia = [System.Windows.Automation.AutomationElement]
            $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
            $dlg = $null
            for ($try = 1; $try -le 3 -and -not $dlg; $try++) {
                if ($try -gt 1) {
                    Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Advanced$' | Out-Null
                    Wait-Until -TimeoutSec 5 -IntervalSec 0.3 -Condition { Get-OpenMenuItems | Where-Object { $_ -match '^Run as a Windows service' } | Select-Object -First 1 } | Out-Null
                }
                foreach ($el in $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
                    if ($el.Current.Name -match '^Run as a Windows service') { $r = $el.Current.BoundingRectangle; Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)); break }
                }
                $dlg = Wait-DesktopWindow -NamePattern '^Startup Mode$' -Process 'Deluno' -TimeoutSec 10
            }
            Add-Check 'The "Startup Mode" dialog opens with the three ways to start' ($null -ne $dlg -and (Get-WindowText -Window $dlg) -match 'Start at login' -and (Get-WindowText -Window $dlg) -match 'Local System' -and (Get-WindowText -Window $dlg) -match 'Run as user') $(if ($dlg) { Get-WindowText -Window $dlg } else { Format-DesktopWindows })
            if (-not $dlg) { return }
            $credLabel = { param($w) @(Get-WindowControls -Window $w | Where-Object { $_.Name -match '^Windows account credentials' -and -not $_.Offscreen }).Count }
            Add-Check 'The account and password boxes are not shown until "Run as user" is chosen' ((& $credLabel $dlg) -eq 0) ''
            $asUser = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'RadioButton' -and $_.Name -match 'Run as user' } | Select-Object -First 1
            $asUser.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $shown = Wait-Until -TimeoutSec 5 -IntervalSec 0.3 -Condition { (& $credLabel $dlg) -gt 0 }
            Add-Check '"Run as user" shows the account and password boxes' $shown.Ok ''
            # The Enter key (Apply is the dialog's default button), not the button's Invoke: Invoke waits for the click handler, which is held by the message box it opens.
            $apply = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Button' -and $_.Name -match '^Apply$' } | Select-Object -First 1
            $apply.Element.SetFocus()
            Start-Sleep -Milliseconds 400 # reason: UI settle after input; Windows raises no event for it
            (New-Object -ComObject WScript.Shell).SendKeys('{ENTER}') # the shell's SendKeys queues the key and returns, where the .NET one waits for the dialog to be idle
            # The message box belongs to the dialog, so UI Automation lists it under the dialog, not on the desktop.
            $warnText = Wait-Until -TimeoutSec 10 -IntervalSec 0.5 -Condition { $c = @(Get-WindowControls -Window $dlg | Where-Object { $_.Name -match 'Please enter a Windows username' }); if ($c.Count -gt 0) { $c[0] } else { $null } }
            Add-Check 'Apply with no account name is refused in words ("Please enter a Windows username.")' $warnText.Ok $(if ($warnText.Ok) { $warnText.Value.Name } else { Get-WindowText -Window $dlg })
            $ok = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Button' -and $_.Name -match '^OK$' } | Select-Object -First 1
            if ($ok) { try { $ok.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { } }
            $closed = Wait-Until -TimeoutSec 5 -IntervalSec 0.5 -Condition { @(Get-WindowControls -Window $dlg | Where-Object { $_.Name -match 'Please enter a Windows username' }).Count -eq 0 }
            if (-not $closed.Ok) { (New-Object -ComObject WScript.Shell).SendKeys('{ENTER}'); $closed = Wait-Until -TimeoutSec 10 -IntervalSec 0.5 -Condition { @(Get-WindowControls -Window $dlg | Where-Object { $_.Name -match 'Please enter a Windows username' }).Count -eq 0 } }
            Add-Check 'The message is closed with OK and the dialog is still there to use' ($closed.Ok -and $null -ne (Wait-DesktopWindow -NamePattern '^Startup Mode$' -Process 'Deluno' -TimeoutSec 5)) ''
            Add-Check 'The service was not made by that refusal' (-not (Get-DelunoServiceFacts)) ''
            $radio = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'RadioButton' -and $_.Name -match 'Local System' } | Select-Object -First 1
            $radio.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Click-WindowButton -Window $dlg -NamePattern '^Apply$' | Out-Null

            $left = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $proc.Id }) }
            # The notification area drops a removed icon a moment after the process ends; the quit check (G19) waits for it the same way.
            $iconGone = Wait-Until -TimeoutSec 15 -IntervalSec 1 -Condition { -not (Find-TrayIcon -Icon 'Deluno') }
            $still = if ($iconGone.Ok) { '' } else { "; still shown: '$((Find-TrayIcon -Icon 'Deluno').Name)'" }
            Add-Check 'The tray stops its host and leaves (its icon is gone)' ($left.Ok -and $iconGone.Ok) "tray process $($proc.Id) ended: $($left.Ok)$still"
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

    Invoke-Scenario -Id 'service.let-any-address-in' -Title "With no tray, an address Allowed Hosts turns away is let in again by the service's settings file (allowedHostsForceDisabled), and Allowed Hosts comes back when the override is removed" -Body {
        if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
        $exe = Get-DelunoExe
        $foreign = 'golden-suite.example.test'
        $shared = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Deluno\data'
        $sharedFile = Join-Path $shared 'deluno.json'
        $hadShared = Test-Path $sharedFile
        # The service runs as LocalSystem, so the settings file it reads (and is copied forward to on its first start) is the system profile's.
        $sysFile = Join-Path $env:windir 'System32\config\systemprofile\AppData\Local\DelunoConfig\deluno.json'
        $hadSys = Test-Path $sysFile
        $sysBefore = if ($hadSys) { Get-Content -Raw $sysFile } else { $null }
        $sharedBefore = if ($hadShared) { Get-Content -Raw $sharedFile } else { $null }
        $trayFile = Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json'
        $port = Get-LanPort
        $savedEnabled = $null; $savedHosts = @()
        try {
            $quit = [bool](Quit-DelunoFromTray)
            Add-Check 'The tray is quit first: nothing but the service will be running Deluno' ($quit -and @(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue).Count -eq 0) ''
            New-Item -ItemType Directory -Force -Path $shared | Out-Null
            # What the tray's Run as a Windows service dialog does first: the service runs as another account, so it is told where this person's data is.
            if (Test-Path $trayFile) { Copy-Item -LiteralPath $trayFile -Destination $sharedFile -Force }
            else { Set-Content -Path $sharedFile -Value (@{ port = $port; dataRoot = (Join-Path $env:LOCALAPPDATA 'DelunoData') } | ConvertTo-Json) -Encoding utf8 }
            & $exe.FullName --install-service | Out-Null
            $svc = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition { $s = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue; if ($s -and $s.State -eq 'Running' -and $s.ProcessId -gt 0) { $s } else { $null } }
            Add-Check 'Deluno runs as the Windows service, with no tray icon' ($svc.Ok -and -not (Find-TrayIcon -Icon 'Deluno')) ''
            if (-not $svc.Ok) { return }
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
            Add-Check 'The service answers' $up.Ok ''
            if (-not $up.Ok) { return }
            Connect-Deluno
            $expectVersion = ($exe.VersionInfo.ProductVersion -split '\+')[0]
            $svcVersion = (Try-Deluno -Path '/api/system/status').Data.version
            Add-Check 'The service reports the installed version, a real one (never 0.0.0)' ($svcVersion -and "$svcVersion" -notmatch '^0\.0\.0' -and "$svcVersion" -like "$expectVersion*") "reported '$svcVersion', installed '$expectVersion'"
            $overview = Invoke-Deluno -Path '/api/security/allowed-hosts'
            $savedEnabled = [bool]$overview.enabled; $savedHosts = @($overview.allowedHosts)
            $put = Try-Deluno -Method PUT -Path '/api/security/allowed-hosts' -Body @{ enabled = $true; allowedHosts = @('localhost', '127.0.0.1') }
            Add-Check 'Allowed Hosts is turned on with only this PC listed' ($put.Status -in 200, 204) "HTTP $($put.Status)"
            $refused = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { (Get-StatusWithHostHeader -HostHeader $foreign -Port $port) -ne 200 }
            Add-Check 'The service turns the unlisted address away, and this PC still gets in' ($refused.Ok -and (Get-StatusWithHostHeader -HostHeader "localhost:$port" -Port $port) -eq 200) "status $(Get-StatusWithHostHeader -HostHeader $foreign -Port $port)"

            # What a person without a tray does: put the override in the settings file the service reads, and restart the service.
            Stop-Service -Name 'Deluno' -Force
            $effective = if (Test-Path $sysFile) { $sysFile } else { $sharedFile }
            Add-Check 'The service has a settings file of its own to edit' (Test-Path $effective) $effective
            $stopped = Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { (Get-Service -Name 'Deluno').Status -eq 'Stopped' }
            Add-Check 'The service is stopped for the edit' $stopped.Ok ''
            $json = Get-Content -Raw $effective | ConvertFrom-Json
            $json | Add-Member -NotePropertyName 'allowedHostsForceDisabled' -NotePropertyValue $true -Force
            Set-Content -Path $effective -Value ($json | ConvertTo-Json -Depth 6) -Encoding utf8
            Start-Service -Name 'Deluno'
            $again = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
            Add-Check 'The service starts again and answers' $again.Ok ''
            $in = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { (Get-StatusWithHostHeader -HostHeader $foreign -Port $port) -eq 200 }
            Add-Check 'With the override in the settings file the unlisted address is answered again' $in.Ok "status $(Get-StatusWithHostHeader -HostHeader $foreign -Port $port)"
            Connect-Deluno
            $kept = Invoke-Deluno -Path '/api/security/allowed-hosts'
            Add-Check 'The saved Allowed Hosts choice is untouched (still on, still this PC only)' ([bool]$kept.enabled -eq $true -and (@($kept.allowedHosts) -join ',') -eq 'localhost,127.0.0.1') ((@($kept.allowedHosts)) -join ',')

            # Remove the override: the saved list is enforced again.
            Stop-Service -Name 'Deluno' -Force
            Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { (Get-Service -Name 'Deluno').Status -eq 'Stopped' } | Out-Null
            $json2 = Get-Content -Raw $effective | ConvertFrom-Json
            $json2.PSObject.Properties.Remove('allowedHostsForceDisabled')
            Set-Content -Path $effective -Value ($json2 | ConvertTo-Json -Depth 6) -Encoding utf8
            Start-Service -Name 'Deluno'
            Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering } | Out-Null
            $back = Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { (Get-StatusWithHostHeader -HostHeader $foreign -Port $port) -ne 200 }
            Add-Check 'Without the override the unlisted address is turned away again' $back.Ok "status $(Get-StatusWithHostHeader -HostHeader $foreign -Port $port)"
        }
        finally {
            if ($null -ne $savedEnabled) {
                Connect-Deluno
                Try-Deluno -Method PUT -Path '/api/security/allowed-hosts' -Body @{ enabled = $savedEnabled; allowedHosts = $savedHosts } | Out-Null
            }
            if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
            if ($hadShared) { Set-Content -Path $sharedFile -Value $sharedBefore -Encoding utf8 } else { Remove-Item -LiteralPath $sharedFile -ErrorAction SilentlyContinue }
            if ($hadSys) { Set-Content -Path $sysFile -Value $sysBefore -Encoding utf8 } else { Remove-Item -LiteralPath $sysFile -ErrorAction SilentlyContinue }
            Add-Check 'The service is gone, the files are as found, and the tray''s Deluno answers' (-not (Get-DelunoServiceFacts) -and (Restore-DelunoTray)) ((Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | ForEach-Object { "Deluno process $($_.Id)" }) -join ', ')
            try {
                $trayVersion = (Try-Deluno -Path '/api/system/status').Data.version
                Add-Check 'The tray''s Deluno reports the installed version too, a real one' ($trayVersion -and "$trayVersion" -notmatch '^0\.0\.0' -and "$trayVersion" -like "$((($exe.VersionInfo.ProductVersion -split '\+')[0]))*") "reported '$trayVersion'"
            } catch { Add-Check 'The tray''s Deluno reports the installed version too, a real one' $false $_.Exception.Message }
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
