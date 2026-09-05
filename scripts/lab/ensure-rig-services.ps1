<#
.SYNOPSIS
    Holds every service on the simulation VM to one rule: it starts without a
    person, and it comes back after a reboot.

.DESCRIPTION
    The rig runs four things. Deluno, qBittorrent and Weir were registered
    as SYSTEM scheduled tasks with a boot trigger, so they start over WinRM and
    survive a restart. SABnzbd was registered with an InteractiveToken principal
    and a one-shot time trigger, so it could only be started by somebody sitting
    at the console, and a reboot lost it.

    The end-to-end plan recorded that as a fact about the rig - "SABnzbd E2E
    Interactive will not start over WinRM, it needs an interactive session" -
    and the whole usenet half of the plan was written off because of it.

    Half of that was true and the fix is not a task principal. SABnzbd checks
    its own session id at startup:

        if hasattr(sys, "frozen") and ProcessIdToSessionId(...) == 0:
            servicemanager.StartServiceCtrlDispatcher()

    Every process launched over WinRM is in session 0, and so is every SYSTEM
    scheduled task, so SABnzbd always decides it is a Windows service - before
    it parses a single argument, which is why even "SABnzbd.exe install" could
    not be run remotely. It does not need a desktop. It needs to actually be a
    service, which it is built to be: as a real service the dispatcher connects,
    and its options come from the CommandLine value under its own service key.

    So one script, one rule, four services, two mechanisms. It reports drift and
    repairs only what has it; something already in the right shape keeps running.

.PARAMETER ReportOnly
    Print the drift and change nothing.

.EXAMPLE
    ./scripts/lab/ensure-rig-services.ps1 -ReportOnly
    ./scripts/lab/ensure-rig-services.ps1
#>
[CmdletBinding()]
param(
    [string] $ComputerName = '192.168.1.142',
    [string] $UserName = 'Administrator',
    [string] $Password = '***REMOVED***',
    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'

$credential = New-Object System.Management.Automation.PSCredential(
    $UserName, (ConvertTo-SecureString $Password -AsPlainText -Force))

$body = {
    param($ReportOnly)

    $ErrorActionPreference = 'Stop'

    $rig = @(
        @{
            Kind = 'task'; Name = 'Deluno Host'; Port = 5099
            Command = 'C:\Deluno\App\Deluno.Host.exe'; Arguments = ''
            WorkingDir = 'C:\Deluno\App'
        },
        @{
            Kind = 'task'; Name = 'qBittorrent'; Port = 8080
            Command = 'C:\Program Files\qBittorrent\qbittorrent.exe'
            Arguments = '--profile="C:\Deluno\qbt-profile"'
            WorkingDir = 'C:\Program Files\qBittorrent'
        },
        @{
            Kind = 'task'; Name = 'Weir Server'; Port = 8788
            Command = 'C:\Users\Administrator\AppData\Local\Weir\current\server\WeirServer.exe'
            Arguments = ''
            WorkingDir = 'C:\Users\Administrator\AppData\Local\Weir\current\server'
        },
        @{
            # A real service, for the reason in the comment block above. The
            # arguments live in the registry because that is where SABnzbd's own
            # get_serv_parms reads them from; passing them on binPath is ignored.
            Kind = 'service'; Name = 'SABnzbd'; Port = 8085
            Command = 'C:\Program Files\SABnzbd\SABnzbd.exe'
            DisplayName = 'SABnzbd Binary Newsreader'
            CommandLine = @('-f', 'C:\Deluno\Data\sabnzbd\sabnzbd.ini', '-s', '0.0.0.0:8085')
        }
    )

    function Get-TaskDrift($task, $spec) {
        if (-not $task) { return @('not registered') }
        $reasons = @()
        if ($task.Principal.UserId -notin @('SYSTEM', 'S-1-5-18')) {
            $reasons += "runs as $($task.Principal.UserId), not SYSTEM"
        }
        if ($task.Principal.LogonType -eq 'InteractiveToken') { $reasons += 'needs a logged-on session' }
        if (-not ($task.Triggers | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' })) {
            $reasons += 'does not start at boot'
        }
        if ($task.Settings.DisallowStartIfOnBatteries) { $reasons += 'refuses to start on batteries' }
        if ($task.Settings.ExecutionTimeLimit -ne 'PT0S') { $reasons += "gives up after $($task.Settings.ExecutionTimeLimit)" }
        if ($task.Settings.RestartCount -lt 1) { $reasons += 'does not restart on failure' }
        if ($task.Actions[0].Execute -ne $spec.Command) { $reasons += "runs $($task.Actions[0].Execute)" }
        return $reasons
    }

    function Get-ServiceDrift($service, $spec) {
        if (-not $service) { return @('not registered') }
        $reasons = @()
        $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$($spec.Name)"
        $registered = (Get-ItemProperty -Path $key -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
        if ($registered -notlike "*$([IO.Path]::GetFileName($spec.Command))*") {
            $reasons += "runs $registered"
        }
        if ($service.StartType -ne 'Automatic') { $reasons += "start type is $($service.StartType)" }
        $parms = (Get-ItemProperty -Path $key -Name CommandLine -ErrorAction SilentlyContinue).CommandLine
        if (-not $parms -or (Compare-Object $parms $spec.CommandLine)) {
            $reasons += 'its registry CommandLine does not match'
        }
        return $reasons
    }

    $results = @()

    foreach ($spec in $rig) {
        $listening = [bool](Get-NetTCPConnection -State Listen -LocalPort $spec.Port -ErrorAction SilentlyContinue)

        if ($spec.Kind -eq 'task') {
            $existing = Get-ScheduledTask -TaskName $spec.Name -ErrorAction SilentlyContinue
            $drift = Get-TaskDrift $existing $spec
        } else {
            $existing = Get-Service -Name $spec.Name -ErrorAction SilentlyContinue
            $drift = Get-ServiceDrift $existing $spec
        }

        if ($drift.Count -eq 0) {
            $results += [pscustomobject]@{
                Service = $spec.Name; Kind = $spec.Kind; Action = 'left alone'
                Listening = $listening; Detail = 'already headless'
            }
            continue
        }

        if ($ReportOnly) {
            $results += [pscustomobject]@{
                Service = $spec.Name; Kind = $spec.Kind; Action = 'would repair'
                Listening = $listening; Detail = ($drift -join '; ')
            }
            continue
        }

        if ($spec.Kind -eq 'task') {
            $action = if ($spec.Arguments) {
                New-ScheduledTaskAction -Execute $spec.Command -Argument $spec.Arguments -WorkingDirectory $spec.WorkingDir
            } else {
                New-ScheduledTaskAction -Execute $spec.Command -WorkingDirectory $spec.WorkingDir
            }

            Register-ScheduledTask -TaskName $spec.Name -Force `
                -Action $action `
                -Trigger (New-ScheduledTaskTrigger -AtStartup) `
                -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) `
                -Settings (New-ScheduledTaskSettingsSet `
                    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
                    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)) | Out-Null
            Start-ScheduledTask -TaskName $spec.Name
        } else {
            if (-not $existing) {
                New-Service -Name $spec.Name -BinaryPathName "`"$($spec.Command)`"" `
                    -DisplayName $spec.DisplayName -StartupType Automatic | Out-Null
            } else {
                Set-Service -Name $spec.Name -StartupType Automatic
            }

            New-ItemProperty -Force -PropertyType MultiString `
                -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$($spec.Name)" `
                -Name 'CommandLine' -Value $spec.CommandLine | Out-Null

            & sc.exe failure $spec.Name reset= 86400 `
                actions= restart/60000/restart/60000/restart/60000 | Out-Null

            Restart-Service -Name $spec.Name -Force
        }

        $results += [pscustomobject]@{
            Service = $spec.Name; Kind = $spec.Kind; Action = 'repaired'
            Listening = $listening; Detail = ($drift -join '; ')
        }
    }

    # The ports again, after any repair, because "registered" is not "answering".
    Start-Sleep -Seconds 10
    foreach ($result in $results) {
        $port = ($rig | Where-Object { $_.Name -eq $result.Service }).Port
        $result.Listening = [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    }

    $results
}

Write-Host "Rig services on $ComputerName" -ForegroundColor Cyan
Invoke-Command -ComputerName $ComputerName -Credential $credential -ScriptBlock $body -ArgumentList $ReportOnly.IsPresent |
    Select-Object Service, Kind, Action, Listening, Detail |
    Format-Table -AutoSize -Wrap
