<#
    Deluno as a Windows service across a restart of the computer (register row SVC-12, #1452).

    service.starts-clients-after-reboot
        With Deluno installed as a service and nobody signed in to the tray after the restart, Deluno and each bundled download client the
        person chose answer on their ports again. In tray mode the clients are started by the tray's own start; the service does it itself.

    The restart ends the suite's runner (it lives in the signed-in session), so the checks after it are made by a startup task that runs as
    SYSTEM before anyone signs in: it waits for each port, adds its checks to this scenario's record in results.json, and writes done.flag.
    Groups that follow the service group in a full run (desktop, github) do not run after the restart, so run this scenario on its own:
    -Scenarios service.starts-clients-after-reboot.
#>

# The ports the bundled clients answer on (installer/bundled-components.json, "defaultPort").
$script:ClientPorts = @{ qbittorrent = 8081; transmission = 9091; deluge = 8112; sabnzbd = 8080; nzbget = 6789 }

function Invoke-ServiceRebootScenario {
    Invoke-Scenario -Id 'service.starts-clients-after-reboot' -Title "Deluno as a Windows service, after the computer restarts with nobody signed in: Deluno and each bundled client the person chose answer on their ports again" -Body {
        if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
        $exe = Get-DelunoExe
        $port = Get-LanPort
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $remembered = @()
        $choicesFile = Join-Path $dataRoot 'config\started-components.json'
        if (Test-Path $choicesFile) { $remembered = @(Get-Content -Raw $choicesFile | ConvertFrom-Json) }
        $expected = @($remembered | Where-Object { $script:ClientPorts.ContainsKey([string]$_) } | ForEach-Object { [pscustomobject]@{ kind = [string]$_; port = [int]$script:ClientPorts[[string]$_] } })
        Add-Check 'The install left the clients the person chose in the list Deluno starts again (so there is something to prove)' ($expected.Count -gt 0) ((@($remembered)) -join ', ')

        # What the tray's Run as a Windows service dialog does first: the service runs as another account, so it is told where this person's data is.
        $shared = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Deluno\data'
        $trayFile = Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json'
        New-Item -ItemType Directory -Force -Path $shared | Out-Null
        if (Test-Path $trayFile) { Copy-Item -LiteralPath $trayFile -Destination (Join-Path $shared 'deluno.json') -Force }
        $null = Quit-DelunoFromTray
        & $exe.FullName --install-service | Out-Null
        $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'Deluno runs as the Windows service before the restart' ($up.Ok -and [bool](Get-DelunoServiceFacts)) ''
        if (-not $up.Ok) { return }

        # The checks after the restart, made by a startup task running as SYSTEM, which exists before anyone signs in.
        $results = Join-Path (Get-SuiteRoot) 'results'
        $verify = Join-Path $results 'after-reboot.ps1'
        $expectedJson = ($expected | ConvertTo-Json -Compress -AsArray)
        $lines = @(
            '$resultsFile = ''' + $results + '\results.json'''
            '$expected = ''' + $expectedJson + ''' | ConvertFrom-Json'
            '$wanted = @([pscustomobject]@{ kind = ''deluno''; port = ' + $port + ' }) + @($expected)'
            'function Test-Port($p) { try { $c = New-Object Net.Sockets.TcpClient; $c.Connect(''127.0.0.1'', $p); $c.Close(); $true } catch { $false } }'
            '$checks = @()'
            '$deadline = (Get-Date).AddMinutes(10)'
            'foreach ($w in $wanted) {'
            '    while (-not (Test-Port $w.port) -and (Get-Date) -lt $deadline) { [Threading.Thread]::Sleep(5000) }'
            '    $checks += [ordered]@{ name = "After the restart, $($w.kind) answers on port $($w.port) with nobody signed in"; ok = (Test-Port $w.port); detail = ''''; atUtc = [DateTime]::UtcNow.ToString(''o'') }'
            '}'
            '$svc = Get-CimInstance Win32_Service -Filter "Name=''Deluno''"'
            '$checks += [ordered]@{ name = ''It is the Windows service that is running Deluno''; ok = ($null -ne $svc -and $svc.State -eq ''Running''); detail = "$($svc.State)"; atUtc = [DateTime]::UtcNow.ToString(''o'') }'
            '$json = Get-Content -Raw $resultsFile | ConvertFrom-Json'
            'foreach ($r in @($json.scenarios)) {'
            '    if ($r.id -eq ''service.starts-clients-after-reboot'') {'
            '        $r.checks = @(@($r.checks) + $checks)'
            '        $r.status = if (@($r.checks | Where-Object { -not $_.ok }).Count -gt 0) { ''fail'' } else { ''pass'' }'
            '    }'
            '}'
            '($json | ConvertTo-Json -Depth 8) | Set-Content -Path $resultsFile -Encoding utf8'
            'Set-Content -Path ''' + $results + '\done.flag'' -Value ''ok'' -Encoding utf8'
            'Unregister-ScheduledTask -TaskName ''GoldenRebootCheck'' -Confirm:$false'
        )
        Set-Content -Path $verify -Value $lines -Encoding utf8
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$verify`""
        Register-ScheduledTask -TaskName 'GoldenRebootCheck' -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        Add-Check 'The startup check is in place and the computer is restarting now' ([bool](Get-ScheduledTask -TaskName 'GoldenRebootCheck' -ErrorAction SilentlyContinue)) ''
        $script:S.Current.status = 'running'
        Save-Results
        Restart-Computer -Force
        Start-Sleep -Seconds 120 # reason: the restart ends this process; nothing runs after it
    }
}
