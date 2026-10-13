<#
    The update phase (#1256): the CANDIDATE's Setup run silently over a RUNNING PREVIOUS release, on a machine the previous
    release set up itself. The host (Invoke-ScenarioSuite.ps1, "the update phase") restores the clean checkpoint, installs the
    previous release silently, and then starts Run-Update.ps1 in the desktop session. It is never run over the candidate the
    other groups used: that would be a downgrade onto a newer schema, which no real person does.

    What it assumes about Velopack (not confirmed on this VM before the first run): Setup.exe, run silently over an instance of
    the app that is running, stops that instance, replaces the install and then starts the new build itself, in the session Setup
    ran in. (Weir #956 saw exactly that: a silent Setup over a running Weir, then a server started by the new tray.) So nothing
    here starts Deluno after Setup. If that assumption is wrong the scenario says so, in these words, apart from a hung start:
      - "Deluno was never started again after Setup": no Deluno process other than the previous release's appeared within
        3 minutes. That is the installer not relaunching, not Deluno's start.
      - "Deluno was started again but never answered": a new process appeared and the web server never answered within
        6 minutes. That is a hung or failed start; the failure reports the start marker (phase, step) and the last lines of the
        start log, which say where it stopped.
#>

function Test-DelunoAnswering {
    $r = Try-Deluno -Path '/api/auth/bootstrap-status' -TimeoutSec 5
    $r.Status -eq 200
}

function Get-InstalledDelunoExe {
    $dir = Join-Path $env:LOCALAPPDATA 'Deluno\current'
    Get-ChildItem -Path $dir -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Get-DelunoProcess {
    @(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)
}

function Read-StartMarker {
    $path = Join-Path $env:LOCALAPPDATA 'Deluno\logs\start-progress.json'
    if (-not (Test-Path $path)) { return $null }
    try { Get-Content -Raw $path | ConvertFrom-Json } catch { $null }
}

# The state the previous release is given: the first account, a quality profile, a library, a download client, and some Activity.
function Initialize-UpdateState {
    $status = Wait-DelunoUp
    New-SuiteSecret 'deluno-password' | Out-Null
    if ($status.requiresSetup) {
        $r = Try-Deluno -Method POST -Path '/api/auth/bootstrap' -Body @{ username = 'golden'; displayName = 'Golden suite'; password = (Get-SuiteSecret 'deluno-password') }
        if ($r.Status -ne 200) { throw (Hide-Secrets "Creating the first account answered $($r.Status). $($r.Raw)") }
    }
    Connect-Deluno
    New-Item -ItemType Directory -Force -Path 'C:\Media\Movies', 'C:\Downloads\Completed\Movies' | Out-Null
    $profile = Invoke-Deluno -Method POST -Path '/api/quality-profiles' -Body @{
        name = 'Update scenario profile'; mediaType = 'movies'; cutoffQuality = 'WEB 1080p'; allowedQualities = 'WEB 720p,WEB 1080p'
        customFormatIds = ''; upgradeUntilCutoff = $true; upgradeUnknownItems = $false
    }
    Invoke-Deluno -Method POST -Path '/api/libraries' -Body @{
        name = 'Update scenario movies'; mediaType = 'movies'; purpose = 'Main library'; rootPath = 'C:\Media\Movies'; downloadsPath = 'C:\Downloads\Completed\Movies'
        qualityProfileId = $profile.id; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
        removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
    } | Out-Null
    Invoke-Deluno -Method POST -Path '/api/download-clients' -Body @{
        name = 'Update scenario client'; protocol = 'qbittorrent'; host = '127.0.0.1'; port = 18081; username = 'x'; password = 'y'
        moviesCategory = 'deluno-movies'; tvCategory = 'deluno-tv'; priority = 1; isEnabled = $false
    } | Out-Null
    # Settings changes are written to Activity; switching automation on and off leaves rows to keep.
    Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $true } | Out-Null
    Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $false } | Out-Null
}

# What Deluno holds that an update must not lose.
function Get-DelunoStateSummary {
    $clients = @(Get-DownloadClients | ForEach-Object { "$($_.id)|$($_.name)" } | Sort-Object)
    $libs = @(Get-Library | ForEach-Object { "$($_.id)|$($_.name)" } | Sort-Object)
    $activity = 0
    try { $activity = @(Get-ActivityText).Count } catch { }
    [pscustomobject]@{ Clients = $clients; Libraries = $libs; Activity = $activity }
}

function Save-UpdateEvidence {
    param([string] $StartLog, [int] $FromLine)
    $text = if (Test-Path $StartLog) { (Get-Content $StartLog | Select-Object -Skip $FromLine) -join "`n" } else { 'no tray-startup.log' }
    Save-Evidence -Name 'update-start-log.txt' -Text $text
}

function Invoke-UpdateScenario {
    param([Parameter(Mandatory)] [string] $CandidateSetup, [Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.over-running-previous' -Title 'Deluno updated over a running previous release comes back on its own, as the new build, with its state, and its start goes cleanly' -Body {
        Add-Check 'The candidate''s Setup.exe is in the VM' (Test-Path $CandidateSetup) $CandidateSetup
        if (-not (Test-Path $CandidateSetup)) { return }

        # The previous release: installed silently by the harness and started the way a person starts it.
        $installed = Get-InstalledDelunoExe
        $previousVersion = if ($installed) { $installed.VersionInfo.ProductVersion } else { '' }
        Add-Check 'The installed build is the previous release, not the candidate' ($installed -and $previousVersion -and $previousVersion -notlike "*$CandidateVersion*") "installed '$previousVersion', candidate '$CandidateVersion'"
        $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'The previous release is running and answering' $up.Ok "after $($up.Seconds) s"
        if (-not $up.Ok -or -not $installed -or $previousVersion -like "*$CandidateVersion*") { return }
        $oldPids = @(Get-DelunoProcess | ForEach-Object { $_.Id })
        Add-Check 'The previous release is a running process' ($oldPids.Count -gt 0) ($oldPids -join ', ')

        # State, through the previous release's own API.
        Initialize-UpdateState
        $before = Get-DelunoStateSummary
        Add-Note ("State before: {0} download clients, {1} libraries, {2} Activity rows." -f $before.Clients.Count, $before.Libraries.Count, $before.Activity)
        Add-Check 'The previous release holds state to preserve (a download client, a library, Activity rows)' ($before.Clients.Count -gt 0 -and $before.Libraries.Count -gt 0 -and $before.Activity -gt 0) ("{0} clients, {1} libraries, {2} Activity rows" -f $before.Clients.Count, $before.Libraries.Count, $before.Activity)
        $startLog = Join-Path $env:LOCALAPPDATA 'Deluno\logs\tray-startup.log'
        $markLine = if (Test-Path $startLog) { @(Get-Content $startLog).Count } else { 0 }

        # The update: the candidate's Setup, silently, over the RUNNING previous release. From here nothing starts Deluno.
        $began = Get-Date
        $upd = Start-Process -FilePath $CandidateSetup -ArgumentList '--silent' -PassThru
        $updDone = $upd.WaitForExit(600000)
        Add-Check 'Setup.exe --silent over the running previous release finished' ($updDone -and $upd.ExitCode -eq 0) "exit $(if ($updDone) { $upd.ExitCode } else { 'none' }) after $([int]((Get-Date) - $began).TotalSeconds) s"

        # Was Deluno started again at all? (the installer's part)
        $started = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition {
            $now = @(Get-DelunoProcess | Where-Object { $_.Id -notin $oldPids })
            if ($now.Count -gt 0) { $now } else { $null }
        }
        Add-Check 'Deluno was started again after Setup, by nobody in this scenario (a new process appeared)' $started.Ok $(if ($started.Ok) { "process $($started.Value[0].Id) after $($started.Seconds) s" } else { 'Deluno was never started again after Setup: the installer did not relaunch it. This is not a hung start.' })
        if (-not $started.Ok) { Save-UpdateEvidence $startLog $markLine; return }
        $newPid = $started.Value[0].Id

        # Did the new process come up? (Deluno's part)
        $back = Wait-Until -TimeoutSec 360 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        $marker = Read-StartMarker
        $where = if ($marker) { "start marker: pid $($marker.pid), try $($marker.attempt), phase $($marker.phase), step '$($marker.detail)', at $($marker.atUtc)" } else { 'no start marker was written' }
        Add-Check 'Deluno was started again and answered (a hung or failed start would not)' $back.Ok $(if ($back.Ok) { "after $($back.Seconds) s; $where" } else { "Deluno was started again but never answered within 6 minutes. $where" })
        if (-not $back.Ok) { Save-UpdateEvidence $startLog $markLine; return }
        Add-Note ("Back {0:N0} s after Setup began (process {1})." -f ((Get-Date) - $began).TotalSeconds, $newPid)
        $nowExe = Get-InstalledDelunoExe
        Add-Check 'It is the candidate build' ($nowExe.VersionInfo.ProductVersion -like "*$CandidateVersion*") "running '$($nowExe.VersionInfo.ProductVersion)', candidate '$CandidateVersion'"
        Add-Check 'The previous release''s process is gone' (@(Get-DelunoProcess | Where-Object { $_.Id -in $oldPids }).Count -eq 0) ''

        # The start went cleanly: the marker says ready for the new process; the log has no retry and no give-up.
        $ready = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { $m = Read-StartMarker; if ($m -and $m.pid -eq $newPid -and $m.phase -eq 'ready') { $m } else { $null } }
        $marker = Read-StartMarker
        Add-Check 'The start marker reads "ready" for the new process' $ready.Ok $(if ($marker) { "pid $($marker.pid), phase $($marker.phase), $($marker.detail)" } else { 'no start-progress.json' })
        $lines = if (Test-Path $startLog) { @(Get-Content $startLog | Select-Object -Skip $markLine) } else { @() }
        $retried = @($lines | Where-Object { $_ -match 'Deluno started on try [2-9]' -or $_ -match 'Start try [2-9] of' })
        $gaveUp = @($lines | Where-Object { $_ -match '\[error\]' -or $_ -match 'Giving up' })
        Add-Check 'The first start after the update needed no retry' ($retried.Count -eq 0) ($retried -join ' | ')
        Add-Check 'The start log has no error and no give-up' ($gaveUp.Count -eq 0) ($gaveUp -join ' | ')
        Add-Check 'The start log says how it began (starting)' (@($lines | Where-Object { $_ -match 'is starting' }).Count -gt 0) ''
        Save-Evidence -Name 'update-start-log.txt' -Text ($lines -join "`n")

        # The state is the same.
        $connected = $false
        try { Connect-Deluno; $connected = $true } catch { }
        Add-Check 'The signed-in account still works after the update' $connected ''
        if ($connected) {
            $after = Get-DelunoStateSummary
            Add-Check 'The download clients are the same' (($after.Clients -join ';') -eq ($before.Clients -join ';')) ("before {0}, after {1}" -f $before.Clients.Count, $after.Clients.Count)
            Add-Check 'The libraries are the same' (($after.Libraries -join ';') -eq ($before.Libraries -join ';')) ("before {0}, after {1}" -f $before.Libraries.Count, $after.Libraries.Count)
            Add-Check 'Activity kept its history' ($after.Activity -ge $before.Activity) ("before {0}, after {1}" -f $before.Activity, $after.Activity)
        }
    }
}
