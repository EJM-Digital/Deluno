<#
    The update phase (#1256): the CANDIDATE's Setup run silently over a RUNNING PREVIOUS release, on a machine the previous
    release set up itself. The host (Invoke-ScenarioSuite.ps1, "the update phase") restores the clean checkpoint, installs the
    previous release silently, and then starts Run-Update.ps1 in the desktop session. It is never run over the candidate the
    other groups used: that would be a downgrade onto a newer schema, which no real person does.

    What it knows about Velopack (rc.30.27 run): Setup.exe --silent over a running app stops that app, replaces the install and
    starts nothing, because a silent Setup does not launch the app. So nothing in this scenario starts Deluno after Setup: the
    candidate's own install hook arranges it (RelaunchAfterSetup, #1256), a short helper that waits for Setup to exit (about
    11 s here) and then starts Deluno. The previous release here (rc.30.25)
    predates Deluno's running marks, so the hook finds out that Deluno was running from the jobs database: its worker writes a
    heartbeat every 15 seconds, and a write in the 90 seconds before the hook ran means Deluno was running. If it
    does not come back, the scenario says so, in these words, apart from a hung start:
      - "Deluno was never started again after Setup": no Deluno process other than the previous release's appeared within
        3 minutes. That is the installer not relaunching, not Deluno's start.
      - "Deluno was started again but never answered": a new process appeared and the web server never answered within
        6 minutes. That is a hung or failed start; the failure reports the start marker (phase, step) and the last lines of the
        start log, which say where it stopped.
#>

function Get-InstalledDelunoExe {
    $dir = Join-Path $env:LOCALAPPDATA 'Deluno\current'
    Get-ChildItem -Path $dir -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
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
    # A title, so the update has a film to keep.
    try { Invoke-Deluno -Method POST -Path '/api/movies' -Body @{ title = 'Update Scenario Film'; releaseYear = 2026; monitored = $false } | Out-Null } catch { }
    Invoke-Deluno -Method POST -Path '/api/download-clients' -Body @{
        name = 'Update scenario client'; protocol = 'qbittorrent'; host = '127.0.0.1'; port = 18081; username = 'x'; password = 'y'
        moviesCategory = 'deluno-movies'; tvCategory = 'deluno-tv'; priority = 1; isEnabled = $false
    } | Out-Null
    # A plan (a media plan: a policy set on the quality profile), a processor connection with a saved secret, and a film file in the library,
    # so the update has each of them to keep (#1278). The processor is switched off: nothing is to ask it anything.
    Try-Deluno -Method POST -Path '/api/policy-sets' -Body @{ name = 'Update scenario plan'; mediaType = 'movies'; qualityProfileId = $profile.id; customFormatIds = ''; upgradeUntilCutoff = $true; isEnabled = $true; notes = 'kept through the update' } | Out-Null
    Try-Deluno -Method POST -Path '/api/integrations/processors/connections' -Body @{ name = 'Update scenario processor'; provider = 'generic'; submissionUrl = 'http://127.0.0.1:19347/hook'; authHeaderName = 'Authorization'; secret = 'update-scenario-secret'; isEnabled = $false } | Out-Null
    $filmFolder = 'C:\Media\Movies\Update Scenario Film (2026)'
    New-Item -ItemType Directory -Force -Path $filmFolder | Out-Null
    $bytes = New-Object byte[] (300KB); (New-Object System.Random 4242).NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $filmFolder 'Update Scenario Film (2026).mkv'), $bytes)
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
    # What else a person keeps through an update: the quality profiles, the titles, the sign-in entry, the firewall rule, and every
    # installed client and Weir that was answering (#1295). Each part is read on its own so one that cannot be read does not hide the rest.
    $profiles = @(); $titles = @()
    try { $profiles = @(Invoke-Deluno -Path '/api/quality-profiles' | ForEach-Object { "$($_.id)|$($_.name)" } | Sort-Object) } catch { }
    try { $titles = @(Invoke-Deluno -Path '/api/movies' | ForEach-Object { "$($_.id)|$($_.title)" } | Sort-Object) } catch { }
    $startsWithWindows = $false
    try { $run = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue; $startsWithWindows = [bool]($run -and ($run.PSObject.Properties | Where-Object { [string]$_.Value -match 'Deluno' })) } catch { }
    $rules = 0
    try { $rules = @(Get-NetFirewallRule -Direction Inbound -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Deluno*' }).Count } catch { }
    $answering = @(8081, 9091, 8112, 8080, 6789, 9347 | Where-Object { Get-NetTCPConnection -State Listen -LocalPort $_ -ErrorAction SilentlyContinue })
    # And the plans, the processor connections with whether their saved secret can still be read, the saved download client keys, the
    # library's details and the media files on disk (name and size), the protection key's hash (what the saved secrets are sealed with).
    $plans = @(); $processors = @(); $clientKeys = @(); $libraryDetails = @(); $mediaFiles = @(); $keyHash = ''
    try { $plans = @(Invoke-Deluno -Path '/api/policy-sets' | ForEach-Object { "$($_.id)|$($_.name)|$($_.qualityProfileId)" } | Sort-Object) } catch { }
    try { $processors = @(Invoke-Deluno -Path '/api/integrations/processors/connections' | ForEach-Object { "$($_.id)|$($_.name)|$($_.submissionUrl)|$($_.authHeaderName)|configured=$($_.secretConfigured)|unreadable=$($_.secretUnreadable)" } | Sort-Object) } catch { }
    try { $clientKeys = @(Get-DownloadClients | ForEach-Object { "$($_.id)|unreadable=$($_.secretUnreadable)" } | Sort-Object) } catch { }
    try { $libraryDetails = @(Invoke-Deluno -Path '/api/libraries' | ForEach-Object { "$($_.id)|$($_.name)|$($_.rootPath)|$($_.downloadsPath)|$($_.qualityProfileId)|$($_.defaultPolicySetId)|$($_.importWorkflow)" } | Sort-Object) } catch { }
    try { $mediaFiles = @(Get-ChildItem -LiteralPath 'C:\Media' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { "$($_.FullName)|$($_.Length)" } | Sort-Object) } catch { }
    try { $keyFile = Join-Path $env:LOCALAPPDATA 'DelunoData\secrets\master.key'; if (Test-Path $keyFile) { $keyHash = (Get-FileHash -LiteralPath $keyFile -Algorithm SHA256).Hash } } catch { }
    [pscustomobject]@{ Clients = $clients; Libraries = $libs; Activity = $activity; Profiles = $profiles; Titles = $titles; StartsWithWindows = $startsWithWindows; FirewallRules = $rules; AnsweringPorts = $answering
        Plans = $plans; Processors = $processors; ClientKeys = $clientKeys; LibraryDetails = $libraryDetails; MediaFiles = $mediaFiles; KeyHash = $keyHash }
}

# The start log's lines written at or after $Since. Lines are picked by their own timestamp, not by counting the lines that
# were there before: the new build may start a fresh log (rc.30.28: the old log had 7 lines, the new one was written from
# scratch, so skipping 7 lines lost "is starting"). A line with no timestamp belongs to the line above it.
function Get-StartLogSince {
    param([string] $StartLog, [Parameter(Mandatory)] [DateTime] $Since)
    if (-not (Test-Path $StartLog)) { return @() }
    $keep = $false
    @(Get-Content $StartLog | Where-Object {
        if ($_ -match '^(\d{4}-\d{2}-\d{2}T[0-9:.]+[+-]\d{2}:\d{2})\s') {
            $at = [DateTimeOffset]::MinValue
            $keep = [DateTimeOffset]::TryParse($Matches[1], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $at) -and $at.UtcDateTime -ge $Since.ToUniversalTime()
        }
        $keep
    })
}

function Save-UpdateEvidence {
    param([string] $StartLog, [Parameter(Mandatory)] [DateTime] $Since)
    $text = if (Test-Path $StartLog) { (Get-StartLogSince -StartLog $StartLog -Since $Since) -join "`n" } else { 'no tray-startup.log' }
    Save-Evidence -Name 'update-start-log.txt' -Text $text
    # What the install hook decided about bringing Deluno back (RelaunchAfterSetup, #1256), and when the jobs database was last written.
    $hook = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
    Save-Evidence -Name 'update-install-hook-log.txt' -Text $(if (Test-Path $hook) { (Get-Content $hook -Tail 80) -join "`n" } else { 'no install-hook.log' })
    $dataFiles = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'DelunoData') -Filter 'jobs.db*' -ErrorAction SilentlyContinue
    Save-Evidence -Name 'update-jobs-db-times.txt' -Text $(if ($dataFiles) { ($dataFiles | ForEach-Object { "{0} last written {1:o}" -f $_.Name, $_.LastWriteTimeUtc }) -join "`n" } else { 'no jobs.db found' })
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
        if (-not $started.Ok) { Save-UpdateEvidence -StartLog $startLog -Since $began; return }
        # The previous release carries running marks, so the install hook knows Deluno was running from the mark, not from the jobs database's heartbeat.
        $hookLog = Join-Path (Join-Path $env:LOCALAPPDATA 'Deluno\logs') 'install-hook.log'
        $hookText = @(if (Test-Path $hookLog) { Get-Content $hookLog -Tail 120 } else { @() })
        $at = -1
        for ($i = 0; $i -lt $hookText.Count; $i++) { if ($hookText[$i] -match ('--veloapp-install ' + [regex]::Escape($CandidateVersion))) { $at = $i } }
        $thisInstall = if ($at -ge 0) { @($hookText[$at..($hookText.Count - 1)]) } else { @() }
        $viaMark = (@($thisInstall | Where-Object { $_ -match 'Deluno was running before this install' }).Count -gt 0) -and (@($thisInstall | Where-Object { $_ -match 'No record that Deluno was running' }).Count -eq 0)
        Add-Check 'The install hook knew Deluno was running from the previous release''s running mark, not from the jobs database heartbeat' $viaMark (($thisInstall | Where-Object { $_ -match 'running' } | Select-Object -First 2) -join ' | ')
        $newPid = $started.Value[0].Id

        # Did the new process come up? (Deluno's part)
        $back = Wait-Until -TimeoutSec 360 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        $marker = Read-StartMarker
        $where = if ($marker) { "start marker: pid $($marker.pid), try $($marker.attempt), phase $($marker.phase), step '$($marker.detail)', at $($marker.atUtc)" } else { 'no start marker was written' }
        Add-Check 'Deluno was started again and answered (a hung or failed start would not)' $back.Ok $(if ($back.Ok) { "after $($back.Seconds) s; $where" } else { "Deluno was started again but never answered within 6 minutes. $where" })
        if (-not $back.Ok) { Save-UpdateEvidence -StartLog $startLog -Since $began; return }
        Add-Note ("Back {0:N0} s after Setup began (process {1})." -f ((Get-Date) - $began).TotalSeconds, $newPid)
        $nowExe = Get-InstalledDelunoExe
        Add-Check 'It is the candidate build' ($nowExe.VersionInfo.ProductVersion -like "*$CandidateVersion*") "running '$($nowExe.VersionInfo.ProductVersion)', candidate '$CandidateVersion'"
        Add-Check 'The previous release''s process is gone' (@(Get-DelunoProcess | Where-Object { $_.Id -in $oldPids }).Count -eq 0) ''

        # The start went cleanly: the marker says ready for the new process; the log has no retry and no give-up.
        $ready = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition { $m = Read-StartMarker; if ($m -and $m.pid -eq $newPid -and $m.phase -eq 'ready') { $m } else { $null } }
        $marker = Read-StartMarker
        Add-Check 'The start marker reads "ready" for the new process' $ready.Ok $(if ($marker) { "pid $($marker.pid), phase $($marker.phase), $($marker.detail)" } else { 'no start-progress.json' })
        $lines = Get-StartLogSince -StartLog $startLog -Since $began
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
            Add-Check 'The previous release held a plan, a processor connection with a saved secret and a film file to keep' (@($before.Plans).Count -gt 0 -and @($before.Processors).Count -gt 0 -and @($before.MediaFiles | Where-Object { $_ -match 'Update Scenario Film' }).Count -eq 1) ("{0} plan(s), {1} processor connection(s), {2} media file(s)" -f @($before.Plans).Count, @($before.Processors).Count, @($before.MediaFiles).Count)
            Add-Check 'The plans are the same' (($after.Plans -join ';') -eq ($before.Plans -join ';')) ("before {0}, after {1}" -f @($before.Plans).Count, @($after.Plans).Count)
            Add-Check 'The processor connections are the same, with their saved secrets still readable' ((($after.Processors -join ';') -eq ($before.Processors -join ';')) -and @($after.Processors | Where-Object { $_ -match 'unreadable=True' }).Count -eq 0 -and @($after.Processors | Where-Object { $_ -match 'configured=True' }).Count -eq @($before.Processors | Where-Object { $_ -match 'configured=True' }).Count) ($after.Processors -join ' ; ')
            Add-Check 'The saved download client keys are the same and can still be read (the protection key is byte for byte the same)' ((($after.ClientKeys -join ';') -eq ($before.ClientKeys -join ';')) -and @($after.ClientKeys | Where-Object { $_ -match 'unreadable=True' }).Count -eq 0 -and $after.KeyHash -eq $before.KeyHash) ("keys before {0}, after {1}; key file hash equal: {2}" -f @($before.ClientKeys).Count, @($after.ClientKeys).Count, ($after.KeyHash -eq $before.KeyHash))
            Add-Check 'Each library keeps its folders, quality profile, plan and import workflow' (($after.LibraryDetails -join ';') -eq ($before.LibraryDetails -join ';')) ("before {0}, after {1}" -f @($before.LibraryDetails).Count, @($after.LibraryDetails).Count)
            Add-Check 'The media library files are the same (same names, same sizes)' (($after.MediaFiles -join ';') -eq ($before.MediaFiles -join ';')) ("before {0} file(s), after {1}" -f @($before.MediaFiles).Count, @($after.MediaFiles).Count)
            $missingPorts = @($before.AnsweringPorts | Where-Object { $_ -notin @($after.AnsweringPorts) })
            Add-Check 'The quality profiles, the titles, the sign-in entry, the firewall rule and every installed client and Weir that was answering are kept' ((($after.Profiles -join ';') -eq ($before.Profiles -join ';')) -and (($after.Titles -join ';') -eq ($before.Titles -join ';')) -and $after.StartsWithWindows -eq $before.StartsWithWindows -and $after.FirewallRules -ge $before.FirewallRules -and $missingPorts.Count -eq 0) ("profiles {0}->{1}, titles {2}->{3}, starts with Windows {4}->{5}, firewall rules {6}->{7}, no longer answering: [{8}]" -f $before.Profiles.Count, $after.Profiles.Count, $before.Titles.Count, $after.Titles.Count, $before.StartsWithWindows, $after.StartsWithWindows, $before.FirewallRules, $after.FirewallRules, ($missingPorts -join ','))
        }
    }
}
