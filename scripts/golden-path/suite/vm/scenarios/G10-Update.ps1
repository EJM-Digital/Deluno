<#
    Update over a running previous release (#1256). Weir's tray gave up for good after the first start following a silent update
    hung; Deluno hosts its web server inside the tray, so the same weakness would leave Deluno down with no one to notice.

    The scenario puts the PREVIOUS release on the VM and starts it the way a person does, notes what Deluno holds (its download
    clients, its libraries, how much Activity it has), then runs the CANDIDATE's Setup.exe silently over it, with the previous
    release still running. From then on nothing starts Deluno by hand: it must come back on its own, as the candidate, with the
    same state, and its start must have gone cleanly (the start marker reads "ready" for the new process, and the start log
    shows no retry and no give-up).

    Needs, beyond the rest of the suite, the previous release's Setup.exe in the VM at C:\golden\previous\Deluno-Setup.exe
    (Invoke-ScenarioSuite.ps1 -PreviousInstallerPath copies it there). Without it the scenario fails, saying so: a release that
    cannot show it updates cleanly is not proven. It runs last, because it replaces the build the other groups ran on.
#>

$script:UpdatePreviousSetup = 'C:\golden\previous\Deluno-Setup.exe'

# The candidate's Setup.exe is the one the harness copied into C:\golden; the previous release's is in the previous folder.
function Get-CandidateSetupPath {
    $found = Get-ChildItem -Path 'C:\golden' -Filter '*.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'Setup' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($found) { $found.FullName } else { $null }
}

function Get-InstalledDelunoExe {
    $dir = Join-Path $env:LOCALAPPDATA 'Deluno\current'
    Get-ChildItem -Path $dir -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-DelunoAnswering {
    $r = Try-Deluno -Path '/api/auth/bootstrap-status' -TimeoutSec 5
    $r.Status -eq 200
}

function Get-DelunoProcess {
    @(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)
}

# Starts Deluno the way a person does, in the signed-in desktop session (a one-shot task, removed again at once), because a silent
# Setup does not start it and the tray needs the desktop.
function Start-DelunoInDesktop {
    $exe = Get-InstalledDelunoExe
    if (-not $exe) { throw 'Deluno.exe is not installed.' }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $exe.FullName -WorkingDirectory $exe.DirectoryName
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName 'GoldenUpdateLaunch' -Action $action -Principal $principal -Force | Out-Null
    try { Start-ScheduledTask -TaskName 'GoldenUpdateLaunch'; Start-Sleep -Seconds 5 }
    finally { Unregister-ScheduledTask -TaskName 'GoldenUpdateLaunch' -Confirm:$false -ErrorAction SilentlyContinue }
}

function Stop-DelunoForReplacement {
    foreach ($p in Get-DelunoProcess) { try { $p.CloseMainWindow() | Out-Null } catch { } }
    Start-Sleep -Seconds 3
    foreach ($p in Get-DelunoProcess) { try { $p.Kill() } catch { } }
}

# What Deluno holds that an update must not lose.
function Get-DelunoStateSummary {
    $clients = @(Get-DownloadClients | ForEach-Object { "$($_.id)|$($_.name)" } | Sort-Object)
    $libs = @(Get-Library | ForEach-Object { "$($_.id)|$($_.name)" } | Sort-Object)
    $activity = 0
    try { $activity = @((Invoke-Deluno -Path '/api/activity?limit=500')).Count } catch { }
    [pscustomobject]@{ Clients = $clients; Libraries = $libs; Activity = $activity }
}

function Read-StartMarker {
    $path = Join-Path $env:LOCALAPPDATA 'Deluno\logs\start-progress.json'
    if (-not (Test-Path $path)) { return $null }
    try { Get-Content -Raw $path | ConvertFrom-Json } catch { $null }
}

function Invoke-GroupUpdate {
    Invoke-Scenario -Id 'update.over-running-previous' -Title 'Deluno updated over a running previous release comes back on its own, as the new build, with its state, and its start goes cleanly' -Body {
        $candidateSetup = Get-CandidateSetupPath
        Add-Check 'The candidate''s Setup.exe is in the VM' ($null -ne $candidateSetup) $candidateSetup
        $havePrevious = Test-Path $script:UpdatePreviousSetup
        Add-Check 'The previous release''s Setup.exe was given (-PreviousInstallerPath)' $havePrevious $script:UpdatePreviousSetup
        if (-not $candidateSetup -or -not $havePrevious) { return }

        $candidateVersion = (Get-InstalledDelunoExe).VersionInfo.ProductVersion
        $before = Get-DelunoStateSummary
        Add-Note ("State before: {0} download clients, {1} libraries, {2} Activity rows." -f $before.Clients.Count, $before.Libraries.Count, $before.Activity)
        Add-Check 'There is state to preserve (download clients and libraries)' ($before.Clients.Count -gt 0 -and $before.Libraries.Count -gt 0) ''

        # 1. The previous release, running, started the way a person starts it.
        Stop-DelunoForReplacement
        $prev = Start-Process -FilePath $script:UpdatePreviousSetup -ArgumentList '--silent' -PassThru
        $prevDone = $prev.WaitForExit(600000)
        Add-Check 'The previous release installed (Setup.exe --silent)' ($prevDone -and $prev.ExitCode -eq 0) "exit $(if ($prevDone) { $prev.ExitCode } else { 'none' })"
        Start-DelunoInDesktop
        $previousVersion = (Get-InstalledDelunoExe).VersionInfo.ProductVersion
        Add-Check 'The installed build is the previous release, not the candidate' ($previousVersion -ne $candidateVersion) "previous $previousVersion, candidate $candidateVersion"
        $upBefore = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'The previous release is running and answering' $upBefore.Ok "after $($upBefore.Seconds) s"
        if (-not $upBefore.Ok) { return }
        $oldPids = @(Get-DelunoProcess | ForEach-Object { $_.Id })
        Add-Check 'The previous release is a running process' ($oldPids.Count -gt 0) ($oldPids -join ', ')
        $logFolder = Join-Path $env:LOCALAPPDATA 'Deluno\logs'
        $startLog = Join-Path $logFolder 'tray-startup.log'
        $markLine = if (Test-Path $startLog) { @(Get-Content $startLog).Count } else { 0 }

        # 2. The update: the candidate's Setup, silently, over the running previous release. Nothing after this starts Deluno.
        $began = Get-Date
        $upd = Start-Process -FilePath $candidateSetup -ArgumentList '--silent' -PassThru
        $updDone = $upd.WaitForExit(600000)
        Add-Check 'Setup.exe --silent over the running previous release finished' ($updDone -and $upd.ExitCode -eq 0) "exit $(if ($updDone) { $upd.ExitCode } else { 'none' })"

        # 3. Back on its own: a new Deluno process, answering, as the candidate. No hand start, no scheduled task, nothing.
        $back = Wait-Until -TimeoutSec 360 -IntervalSec 3 -Condition {
            $now = @(Get-DelunoProcess | Where-Object { $_.Id -notin $oldPids })
            if ($now.Count -gt 0 -and (Test-DelunoAnswering)) { $now } else { $null }
        }
        Add-Check 'Deluno came back on its own after the update (a new process, answering, nobody started it)' $back.Ok "after $($back.Seconds) s"
        if (-not $back.Ok) {
            Save-Evidence -Name 'update-start-log.txt' -Text $(if (Test-Path $startLog) { (Get-Content $startLog | Select-Object -Last 200) -join "`n" } else { 'no tray-startup.log' })
            return
        }
        $newPid = $back.Value[0].Id
        Add-Note ("Back after {0:N0} s (process {1})." -f ((Get-Date) - $began).TotalSeconds, $newPid)
        $nowVersion = (Get-InstalledDelunoExe).VersionInfo.ProductVersion
        Add-Check 'It is the candidate build' ($nowVersion -eq $candidateVersion) "running $nowVersion, candidate $candidateVersion"
        Add-Check 'The previous release''s process is gone' (@(Get-DelunoProcess | Where-Object { $_.Id -in $oldPids }).Count -eq 0) ''

        # 4. The start went cleanly: the marker says ready for the new process; the log has no retry and no give-up.
        $ready = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { $m = Read-StartMarker; if ($m -and $m.pid -eq $newPid -and $m.phase -eq 'ready') { $m } else { $null } }
        $marker = Read-StartMarker
        Add-Check 'The start marker reads "ready" for the new process' $ready.Ok $(if ($marker) { "pid $($marker.pid), phase $($marker.phase), $($marker.detail)" } else { 'no start-progress.json' })
        $lines = if (Test-Path $startLog) { @(Get-Content $startLog | Select-Object -Skip $markLine) } else { @() }
        $retried = @($lines | Where-Object { $_ -match 'Deluno started on try [2-9]' -or $_ -match 'Start try [2-9] of' })
        $gaveUp = @($lines | Where-Object { $_ -match '\[error\]' -or $_ -match 'Giving up' })
        Add-Check 'The first start after the update needed no retry' ($retried.Count -eq 0) ($retried -join ' | ')
        Add-Check 'The start log has no error and no give-up' ($gaveUp.Count -eq 0) ($gaveUp -join ' | ')
        Add-Check 'The start log says how it began (starting, then ready)' (@($lines | Where-Object { $_ -match 'is starting' }).Count -gt 0) ''
        Save-Evidence -Name 'update-start-log.txt' -Text ($lines -join "`n")

        # 5. The state is the same.
        $connected = $false
        try { Connect-Deluno; $connected = $true } catch { }
        Add-Check 'The signed-in session still works after the update' $connected ''
        if ($connected) {
            $after = Get-DelunoStateSummary
            Add-Check 'The download clients are the same' (($after.Clients -join ';') -eq ($before.Clients -join ';')) ("before {0}, after {1}" -f $before.Clients.Count, $after.Clients.Count)
            Add-Check 'The libraries are the same' (($after.Libraries -join ';') -eq ($before.Libraries -join ';')) ("before {0}, after {1}" -f $before.Libraries.Count, $after.Libraries.Count)
            Add-Check 'Activity kept its history' ($after.Activity -ge $before.Activity) ("before {0}, after {1}" -f $before.Activity, $after.Activity)
        }
    }
}
