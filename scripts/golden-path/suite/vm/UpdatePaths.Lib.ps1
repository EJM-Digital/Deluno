<#
    The other ways an installed Deluno is updated (coverage issue 1295, register rows L.10.2 to L.10.21, L.11.8). Each runs on a
    clean machine of its own that the host set up with the PREVIOUS release (Invoke-ScenarioSuite.ps1, "the update phase"), like
    update.over-running-previous:

      update.in-app             Check, Download (progress) and Restart to update from the app's own Updates API, against a local
                                Velopack feed that serves the candidate's real packages; the pre-update backup is taken; Deluno
                                comes back as the candidate with its state.
      update.apply-on-quit      A downloaded update is installed when Deluno is quit from its tray menu, after the pre-update
                                backup, and Deluno is not started again by the install.
      update.apply-at-start     A downloaded update is applied at the next start, before the host runs, after the pre-update backup.
      update.corrupt-package    An update whose package is damaged (its checksum fails, and a package that is the wrong size or is
                                not a package at all) never leaves Deluno unusable: the previous version keeps running or comes back.
      update.setup-interactive  The candidate's Setup run the way a person runs it (not silent) over a running previous release.
      update.setup-over-stopped The candidate's Setup run silently over a Deluno that is stopped starts nothing.

    The feed is a tiny HTTP listener on the loopback address (the app reads it as a "SimpleWebSource" once its saved update source
    is not github.com), serving the files the release publishes: the .nupkg, releases.<channel>.json and RELEASES-<channel>.
    Windows PowerShell 5.1, in the signed-in desktop session.
#>

$script:FeedPort = 18099

# Where the feed's files and the candidate were copied by the host.
function Get-UpdateFeedFolder { 'C:\golden\updatefeed' }

# The release the feed serves is the candidate's. Returns the folder; throws when the host did not send it.
function Assert-UpdateFeedFiles {
    $dir = Get-UpdateFeedFolder
    $nupkg = Get-ChildItem -Path $dir -Filter '*.nupkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nupkg) { throw "The candidate's update package is not in $dir." }
    $dir
}

# Starts the feed (a hidden PowerShell serving $Folder on the loopback address) and returns the process. It writes every request it
# answers to $Folder\..\feed-requests.log, so the scenario can show the app really read the feed. -ThrottleMs pauses after each
# 1 MB chunk, so a download lasts long enough for its progress to be seen.
function Start-UpdateFeed {
    param([Parameter(Mandatory)] [string] $Folder, [int] $ThrottleMs = 30, [string] $LogName = 'feed-requests.log')
    $script = Join-Path (Split-Path $Folder -Parent) 'feed-server.ps1'
    $log = Join-Path (Split-Path $Folder -Parent) $LogName
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
    Set-Content -Path $script -Encoding utf8 -Value @'
param([string] $Dir, [int] $Port, [int] $ThrottleMs, [string] $Log)
$l = New-Object System.Net.HttpListener
$l.Prefixes.Add("http://127.0.0.1:$Port/")
$l.Start()
while ($l.IsListening) {
    $c = $l.GetContext()
    try {
        $name = [IO.Path]::GetFileName([Uri]::UnescapeDataString($c.Request.Url.AbsolutePath))
        $path = Join-Path $Dir $name
        if ($name -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-Content -Path $Log -Value ("{0:o} 200 {1}" -f [DateTime]::UtcNow, $name)
            $c.Response.ContentLength64 = (Get-Item -LiteralPath $path).Length
            $fs = [IO.File]::OpenRead($path)
            try {
                $buf = New-Object byte[] 1048576
                while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                    $c.Response.OutputStream.Write($buf, 0, $n)
                    if ($ThrottleMs -gt 0) { Start-Sleep -Milliseconds $ThrottleMs } # reason: paces the test feed's download so its progress can be seen; nothing signals it
                }
            } finally { $fs.Dispose() }
        } else {
            Add-Content -Path $Log -Value ("{0:o} 404 {1}" -f [DateTime]::UtcNow, $c.Request.Url.AbsolutePath)
            $c.Response.StatusCode = 404
        }
    } catch { } finally { try { $c.Response.Close() } catch { } }
}
'@
    $p = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Dir', $Folder, '-Port', $script:FeedPort, '-ThrottleMs', $ThrottleMs, '-Log', $log
    $up = Wait-Until -TimeoutSec 30 -IntervalSec 0.5 -Condition { try { (Invoke-WebRequest -Uri "http://127.0.0.1:$($script:FeedPort)/nothing.txt" -UseBasicParsing -TimeoutSec 3) | Out-Null; $true } catch { [bool]$_.Exception.Response } }
    if (-not $up.Ok) { throw 'The local update feed did not start.' }
    $p
}

function Stop-UpdateFeed { param($Process) if ($Process) { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } }

function Get-UpdateFeedRequests {
    $log = Join-Path (Split-Path (Get-UpdateFeedFolder) -Parent) 'feed-requests.log'
    if (Test-Path $log) { @(Get-Content $log) } else { @() }
}

# Points the app at the local feed, as a person's saved update source would (the app reads deluno.json on every check) and picks
# the update mode. The file is created by the app's own preferences call first, so only the one line is added.
function Set-UpdateSourceToFeed {
    param([string] $Mode = 'notify-only', [string] $Source = "http://127.0.0.1:$($script:FeedPort)")
    $r = Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ mode = $Mode; autoCheck = $false }
    if ($r.Status -ne 200) { throw "Saving the update mode answered $($r.Status). $($r.Raw)" }
    $file = Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json'
    if (-not (Test-Path $file)) { throw "Deluno's settings file was not written at $file." }
    $json = Get-Content -Raw $file | ConvertFrom-Json
    if ($json.PSObject.Properties['UpdateSource']) { $json.UpdateSource = $Source } else { $json | Add-Member -NotePropertyName UpdateSource -NotePropertyValue $Source }
    [IO.File]::WriteAllText($file, ($json | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    # The channel the saved copy reads from must be the one the feed's files are named for.
    Clear-DownloadedUpdates
    $r2 = Try-Deluno -Path '/api/updates/status'
    $r2.Data
}

# The installed copy looks for updates on its own, on the real release feed, as soon as it starts (a download of the real next release can
# already be waiting by the time the scenario begins). Removes what it fetched, so the scenario's own download comes from its feed.
function Clear-DownloadedUpdates {
    $dir = Join-Path $env:LOCALAPPDATA 'Deluno\packages'
    $left = @(Get-ChildItem -Path $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.nupkg*' })
    $left | Remove-Item -Force -ErrorAction SilentlyContinue
    Add-Note ("Cleared {0} package(s) the installed copy had already fetched on its own from the release feed: {1}" -f $left.Count, (($left | ForEach-Object { $_.Name }) -join ', '))
}

function Get-UpdateStatus { $r = Try-Deluno -Path '/api/updates/status'; if ($r.Status -eq 200) { $r.Data } else { $null } }

# The previous release, installed and answering, with state; shared start of every scenario here. Returns $null (after saying why in a check) when it is not so.
function Start-PreviousForUpdate {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    $installed = Get-InstalledDelunoExe
    $previousVersion = if ($installed) { $installed.VersionInfo.ProductVersion } else { '' }
    Add-Check 'The installed build is the previous release, not the candidate' ($installed -and $previousVersion -and $previousVersion -notlike "*$CandidateVersion*") "installed '$previousVersion', candidate '$CandidateVersion'"
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
    Add-Check 'The previous release is running and answering' $up.Ok "after $($up.Seconds) s"
    if (-not $up.Ok -or -not $installed -or $previousVersion -like "*$CandidateVersion*") { return $null }
    Initialize-UpdateState
    # The installed copy checks the real release feed on its own and downloads what it finds, and a downloaded update is installed
    # when Deluno is quit. A scenario that quits Deluno, or judges what an update does, must start with nothing downloaded and the
    # automatic check off, so the only update there is, is the one the scenario makes.
    Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ mode = 'notify-only'; autoCheck = $false } | Out-Null
    Clear-DownloadedUpdates
    $before = Get-DelunoStateSummary
    Add-Check 'The previous release holds state to preserve (a download client, a library, Activity rows)' ($before.Clients.Count -gt 0 -and $before.Libraries.Count -gt 0 -and $before.Activity -gt 0) ("{0} clients, {1} libraries, {2} Activity rows" -f $before.Clients.Count, $before.Libraries.Count, $before.Activity)
    [pscustomobject]@{ PreviousVersion = $previousVersion; Before = $before; OldPids = @(Get-DelunoProcess | ForEach-Object { $_.Id }); Began = Get-Date }
}

# After an update: the candidate is the installed build and Deluno answers as it, with the state it had.
function Test-CameBackAsCandidate {
    param($Context, [string] $CandidateVersion, [int] $TimeoutSec = 420)
    $back = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 3 -Condition { $e = Get-InstalledDelunoExe; if ($e -and $e.VersionInfo.ProductVersion -like "*$CandidateVersion*" -and (Test-DelunoAnswering)) { $e } else { $null } }
    $marker = Read-StartMarker
    $where = if ($marker) { "start marker: pid $($marker.pid), phase $($marker.phase), step '$($marker.detail)'" } else { 'no start marker' }
    Add-Check 'Deluno answers again as the candidate build' $back.Ok $(if ($back.Ok) { "after $($back.Seconds) s; $where" } else { "not within $TimeoutSec s; installed '$((Get-InstalledDelunoExe).VersionInfo.ProductVersion)'; $where" })
    if (-not $back.Ok) { return $false }
    $connected = $false
    try { Connect-Deluno; $connected = $true } catch { }
    Add-Check 'The signed-in account still works after the update' $connected ''
    if ($connected) {
        $after = Get-DelunoStateSummary
        Add-Check 'The download clients, libraries and Activity history are the same' ((($after.Clients -join ';') -eq ($Context.Before.Clients -join ';')) -and (($after.Libraries -join ';') -eq ($Context.Before.Libraries -join ';')) -and $after.Activity -ge $Context.Before.Activity) ("clients {0}->{1}, libraries {2}->{3}, Activity {4}->{5}" -f $Context.Before.Clients.Count, $after.Clients.Count, $Context.Before.Libraries.Count, $after.Libraries.Count, $Context.Before.Activity, $after.Activity)
    }
    $true
}

# The backups the app lists, as text, so a "pre-update" one can be found by its name.
function Get-BackupListText {
    $r = Try-Deluno -Path '/api/backups'
    if ($r.Status -eq 200) { $r.Raw } else { "answered $($r.Status)" }
}

# Backup files on disk under the data folder (found by name), for when Deluno is not answering.
function Get-PreUpdateBackupFiles {
    $root = Join-Path $env:LOCALAPPDATA 'DelunoData'
    @(Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'pre-update' })
}

# Velopack's Update.exe (and the hooks it runs) end every process that runs from the install folder when they finish, so nothing is started or judged until they are gone.
function Wait-InstallerQuiet {
    param([int] $TimeoutSec = 300)
    $quiet = 0
    Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 3 -Condition {
        if (@(Get-Process -Name 'Update' -ErrorAction SilentlyContinue).Count -eq 0) { $script:quietSince = if ($script:quietSince) { $script:quietSince } else { Get-Date } } else { $script:quietSince = $null }
        if ($script:quietSince -and ((Get-Date) - $script:quietSince).TotalSeconds -ge 20) { $true } else { $null }
    }
}

# Backup files (zips) in the app's backups folder, by name.
function Get-BackupFiles {
    @(Get-ChildItem -Path (Join-Path (Join-Path $env:LOCALAPPDATA 'DelunoData') 'backups') -Filter '*.zip' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
}

function Get-FeedPackageName { (Get-ChildItem -Path (Get-UpdateFeedFolder) -Filter '*.nupkg' | Select-Object -First 1).Name }

# Common to the three that need a downloaded update: feed up, source set, check, download. Returns the feed process and the status after download.
function Initialize-DownloadedUpdate {
    param($Context, [string] $CandidateVersion, [string] $Folder = (Get-UpdateFeedFolder))
    $feed = Start-UpdateFeed -Folder $Folder -ThrottleMs 0
    $st = Set-UpdateSourceToFeed
    Add-Check 'The updater sees a Velopack install with the update controls on' ($st -and $st.isInstalled -and $st.canCheck -and $st.canDownload -and $st.canApply) $(if ($st) { "installKind $($st.installKind), channel $($st.channel)" } else { 'no status' })
    $chk = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
    Add-Check 'Check for updates finds the candidate on the feed' ($chk.Status -eq 200 -and $chk.Data.status.updateAvailable -and $chk.Data.status.latestVersion -like "*$CandidateVersion*") $(if ($chk.Data) { "$($chk.Data.message) | latest $($chk.Data.status.latestVersion) | state $($chk.Data.status.state)" } else { "answered $($chk.Status) $($chk.Raw)" })
    $dl = Try-Deluno -Method POST -Path '/api/updates/download' -TimeoutSec 600
    Add-Check 'Download update ends "Update downloaded. Restart Deluno to apply it." and is ready to restart' ($dl.Status -eq 200 -and $dl.Data.status.state -eq 'ready-to-restart' -and $dl.Data.status.restartRequired -and $dl.Data.status.message -eq 'Update downloaded. Restart Deluno to apply it.') $(if ($dl.Data) { "$($dl.Data.status.message) | state $($dl.Data.status.state)" } else { "answered $($dl.Status) $($dl.Raw)" })
    [pscustomobject]@{ Feed = $feed; Downloaded = ($dl.Status -eq 200 -and $dl.Data.status.state -eq 'ready-to-restart') }
}

# ---------------------------------------------------------------- scenarios

function Invoke-UpdateInApp {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.in-app' -Title 'Check, Download and Restart to update from the app against a Velopack feed: progress is shown, the pre-update backup is taken, Deluno comes back as the new build with its state' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $feed = Start-UpdateFeed -Folder (Get-UpdateFeedFolder) -ThrottleMs 40
        try {
            $st = Set-UpdateSourceToFeed
            Add-Check 'The updater sees a Velopack install with the update controls on' ($st -and $st.isInstalled -and $st.canCheck -and $st.canDownload -and $st.canApply) $(if ($st) { "installKind $($st.installKind), channel $($st.channel), current $($st.currentVersion)" } else { 'no status' })

            # Check.
            $chk = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            Add-Check 'Check for updates finds the candidate on the feed (notify-only: found, not downloaded)' ($chk.Status -eq 200 -and $chk.Data.status.updateAvailable -and $chk.Data.status.latestVersion -like "*$CandidateVersion*" -and $chk.Data.status.state -eq 'update-available') $(if ($chk.Data) { "$($chk.Data.message) | latest $($chk.Data.status.latestVersion) | state $($chk.Data.status.state)" } else { "answered $($chk.Status) $($chk.Raw)" })
            Add-Check 'The check read the feed''s release list' (@(Get-UpdateFeedRequests | Where-Object { $_ -match 'releases\.' -or $_ -match 'RELEASES' }).Count -gt 0) ((Get-UpdateFeedRequests) -join ' | ')

            # Download, watching the state move.
            $token = $script:S.Token; $url = $script:S.DelunoUrl
            $job = Start-Job -ArgumentList $url, $token -ScriptBlock { param($u, $t) try { (Invoke-WebRequest -Uri "$u/api/updates/download" -Method POST -Headers @{ Authorization = "Bearer $t" } -UseBasicParsing -TimeoutSec 900).Content } catch { "ERROR $($_.Exception.Message)" } }
            $seen = New-Object System.Collections.Generic.HashSet[string]; $maxPct = 0; $slowest = 0.0
            while ($job.State -eq 'Running') {
                $t0 = Get-Date
                $s = Get-UpdateStatus
                $slowest = [math]::Max($slowest, ((Get-Date) - $t0).TotalSeconds)
                if ($s) { [void]$seen.Add([string]$s.state); if ($null -ne $s.progressPercent -and $s.progressPercent -gt $maxPct) { $maxPct = [int]$s.progressPercent } }
                Start-Sleep -Milliseconds 300 # reason: polling a download's progress; the app pushes nothing a script can wait on
            }
            $dlText = Receive-Job $job -Wait; Remove-Job $job -Force
            $dl = $null; try { $dl = $dlText | ConvertFrom-Json } catch { }
            Add-Note ("Reading the update status while the download ran: states seen {0}; highest progress {1} %; the slowest answer took {2:N1} s (the status call waits for the download to end when it is polled; the tray menu shows 'Downloading update...' in update.tray-menu)." -f (@($seen) -join ', '), $maxPct, $slowest)
            Add-Check 'The download ends "Update downloaded. Restart Deluno to apply it." and is ready to restart' ($dl -and $dl.status.state -eq 'ready-to-restart' -and $dl.status.restartRequired -and $dl.status.message -eq 'Update downloaded. Restart Deluno to apply it.') $(if ($dl) { "$($dl.status.message) | state $($dl.status.state)" } else { $dlText })
            Add-Check 'The feed served the candidate''s package' (@(Get-UpdateFeedRequests | Where-Object { $_ -match '200 .*nupkg' }).Count -gt 0) ((Get-UpdateFeedRequests) -join ' | ')
            $again = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            Add-Note $(if ($again.Data) { "A second check after the download answered '$($again.Data.message)', state $($again.Data.status.state), restartRequired $($again.Data.status.restartRequired). From the next release a second check keeps the downloaded update waiting for the restart; the previous release this phase installs predates that." } else { "A second check answered $($again.Status)" })

            # Restart to update.
            $backupsBefore = Get-BackupListText
            $began = Get-Date
            $rs = Try-Deluno -Method POST -Path '/api/updates/restart-now' -TimeoutSec 600
            Add-Check 'Restart to update takes the pre-update backup and is accepted ("Backup completed. Restarting to apply update.")' ($rs.Status -eq 200 -and $rs.Data.accepted -and $rs.Data.message -match 'Backup completed') $(if ($rs.Data) { $rs.Data.message } else { "answered $($rs.Status) $($rs.Raw)" })
            $gone = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { @(Get-DelunoProcess | Where-Object { $_.Id -in $ctx.OldPids }).Count -eq 0 }
            Add-Check 'The previous release stops cleanly' $gone.Ok "after $($gone.Seconds) s"
            if (-not (Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion)) {
                Save-Evidence -Name 'update-in-app-files.txt' -Text ((Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Deluno') | ForEach-Object { $_.Name }) -join "`n")
                return
            }
            Add-Note ("Back {0:N0} s after Restart to update was pressed." -f ((Get-Date) - $began).TotalSeconds)
            $after = Get-BackupListText
            Add-Check 'A "pre-update" backup is in the backups the new build lists' ($after -match 'pre-update') (($after -replace '\s+', ' ').Substring(0, [math]::Min(300, $after.Length)))
            $s2 = Get-UpdateStatus
            Add-Check 'The new build is on the same feed with nothing newer to apply' ($s2 -and $s2.currentVersion -like "*$CandidateVersion*" -and -not $s2.restartRequired) $(if ($s2) { "current $($s2.currentVersion), restartRequired $($s2.restartRequired)" } else { 'no status' })
            # #1376: the Windows updater records the update like the Docker path does: the version on the Updates card with its
            # date, and one "Deluno updated" line in Activity.
            $appliedList = if ($s2 -and $s2.PSObject.Properties['appliedUpdates'] -and $null -ne $s2.appliedUpdates) { @($s2.appliedUpdates) } else { @() }
            $appliedNow = @($appliedList | Where-Object { $_.version -like "*$CandidateVersion*" })
            Add-Check 'The Updates card lists the applied version with its date' ($appliedNow.Count -eq 1 -and $appliedNow[0].appliedAtUtc) $(if ($appliedList.Count) { ($appliedList | ForEach-Object { "$($_.version) at $($_.appliedAtUtc)" }) -join '; ' } else { 'no applied versions listed' })
            $lines = @(Get-ActivityText | Where-Object { $_ -match "Deluno updated (from \S+ )?to (version )?\S*$([regex]::Escape($CandidateVersion))" })
            Add-Check 'Activity has one "Deluno updated" line for the new version' ($lines.Count -eq 1) $(if ($lines.Count) { $lines -join ' | ' } else { 'no such line' })
            Add-Note $(if ($lines.Count -and $lines[0] -match 'updated from') { "The line names the version it came from: $($lines[0])" } else { 'The line names only the new version: the previous release leaves no packages the new build can read the old version from.' })
            Save-Evidence -Name 'update-in-app-feed-requests.txt' -Text ((Get-UpdateFeedRequests) -join "`n")
        }
        finally { Stop-UpdateFeed $feed }
    }
}

function Invoke-UpdateApplyOnQuit {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.apply-on-quit' -Title 'A downloaded update is installed when Deluno is quit from its tray menu, after the pre-update backup, and Deluno then runs as the new build with its state' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $d = Initialize-DownloadedUpdate -Context $ctx -CandidateVersion $CandidateVersion
        try {
            if (-not $d.Downloaded) { return }
            $backupsBefore = @(Get-BackupFiles)
            $quit = Quit-DelunoFromTray
            Add-Check 'Quit Deluno from the tray menu ends the process' ($quit -and @(Get-DelunoProcess).Count -eq 0) "menu pressed: $quit"
            $installed = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition { $e = Get-InstalledDelunoExe; if ($e -and $e.VersionInfo.ProductVersion -like "*$CandidateVersion*") { $e } else { $null } }
            Add-Check 'The downloaded update is installed after Quit (the installed build is the candidate)' $installed.Ok $(if ($installed.Ok) { "after $($installed.Seconds) s" } else { "installed '$((Get-InstalledDelunoExe).VersionInfo.ProductVersion)'" })
            $new = @(Get-BackupFiles | Where-Object { $_ -notin $backupsBefore })
            Add-Check 'The pre-update backup was taken before the install (a new backup file exists)' ($new.Count -gt 0) ($new -join ', ')
            Wait-InstallerQuiet | Out-Null
            # What starts Deluno after the install is not asserted here: the previous release this phase installs predates the mark that
            # tells a quit Deluno from a running one, so the install hook may start it (it says which in its log). Either way it answers.
            $running = Wait-Until -TimeoutSec 60 -IntervalSec 3 -Condition { Test-DelunoAnswering }
            Add-Note $(if ($running.Ok) { "Deluno was running again $($running.Seconds) s after the installer finished, started by the install hook's helper." } else { 'Nothing started Deluno after the install; it is started by hand next.' })
            if (-not $running.Ok) {
                $exe = Get-InstalledDelunoExe
                Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
            }
            Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion | Out-Null
            Add-Check 'The pre-update backup is still in the backups the new build lists' ((Get-BackupListText) -match 'pre-update') ''
        }
        finally { Stop-UpdateFeed $d.Feed }
    }
}

function Invoke-UpdateApplyAtStart {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.apply-at-start' -Title 'A downloaded update waiting when Deluno next starts is backed up and applied before the host runs, and Deluno restarts into the new build' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $d = Initialize-DownloadedUpdate -Context $ctx -CandidateVersion $CandidateVersion
        try {
            if (-not $d.Downloaded) { return }
            # Not Quit (that would install it on exit): end the process, as a crash or a shutdown does, so the update is still waiting.
            Get-DelunoProcess | Where-Object { $_.Id -in $ctx.OldPids } | Stop-Process -Force
            $gone = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { @(Get-DelunoProcess).Count -eq 0 }
            Add-Check 'Deluno was ended without Quit' $gone.Ok ''
            $still = Get-InstalledDelunoExe
            Add-Check 'The update is still waiting (the installed build is the previous release)' ($still.VersionInfo.ProductVersion -notlike "*$CandidateVersion*") $still.VersionInfo.ProductVersion
            $startLog = Join-Path $env:LOCALAPPDATA 'Deluno\logs\tray-startup.log'
            $began = Get-Date
            Start-ProgramUnelevated -Path $still.FullName -WorkingDirectory $still.DirectoryName -Elevated
            $ok = Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion
            $lines = Get-StartLogSince -StartLog $startLog -Since $began
            Save-Evidence -Name 'update-at-start-log.txt' -Text ($lines -join "`n")
            Add-Check 'The start log shows the pre-update backup taken before the install' (@($lines | Where-Object { $_ -match 'Backup' }).Count -gt 0) ((@($lines | Where-Object { $_ -match 'Backup' }) | Select-Object -First 3) -join ' | ')
            Add-Check 'A pre-update backup exists' (@(Get-PreUpdateBackupFiles).Count -gt 0 -or ((Get-BackupListText) -match 'pre-update')) ''
        }
        finally { Stop-UpdateFeed $d.Feed }
    }
}

# A feed folder whose package is not the real one: $Kind 'checksum' = real package, release list names a wrong checksum; 'truncated' = the
# first part of the real package, listed with its own (matching) size and checksums, so the download passes and only the install meets the damage.
function New-DamagedFeed {
    param([ValidateSet('checksum', 'truncated')] [string] $Kind)
    $src = Get-UpdateFeedFolder
    $dst = "$src-$Kind"
    Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    $nupkg = Get-ChildItem -Path $src -Filter '*.nupkg' | Select-Object -First 1
    $json = Get-Content -Raw (Get-ChildItem -Path $src -Filter 'releases.*.json' | Select-Object -First 1).FullName | ConvertFrom-Json
    $jsonName = (Get-ChildItem -Path $src -Filter 'releases.*.json' | Select-Object -First 1).Name
    $relName = (Get-ChildItem -Path $src -Filter 'RELEASES-*' | Select-Object -First 1).Name
    $target = Join-Path $dst $nupkg.Name
    if ($Kind -eq 'checksum') {
        Copy-Item -LiteralPath $nupkg.FullName -Destination $target
        # Same size, one byte changed in the middle: the listed checksum no longer matches.
        $fs = [IO.File]::Open($target, 'Open', 'ReadWrite')
        try { $fs.Position = [long]($fs.Length / 2); $b = $fs.ReadByte(); $fs.Position = [long]($fs.Length / 2); $fs.WriteByte([byte](($b + 1) % 256)) } finally { $fs.Dispose() }
    }
    else {
        $in = [IO.File]::OpenRead($nupkg.FullName); $out = [IO.File]::Create($target)
        try { $buf = New-Object byte[] 1048576; for ($i = 0; $i -lt 20; $i++) { $n = $in.Read($buf, 0, $buf.Length); if ($n -le 0) { break }; $out.Write($buf, 0, $n) } } finally { $in.Dispose(); $out.Dispose() }
        $sha1 = (Get-FileHash $target -Algorithm SHA1).Hash; $sha256 = (Get-FileHash $target -Algorithm SHA256).Hash; $len = (Get-Item $target).Length
        $json.Assets[0].SHA1 = $sha1; $json.Assets[0].SHA256 = $sha256; $json.Assets[0].Size = $len
        Set-Content -Path (Join-Path $dst $relName) -Value "$sha1 $($nupkg.Name) $len" -Encoding ascii
    }
    if ($Kind -eq 'checksum') { Copy-Item -LiteralPath (Join-Path $src $relName) -Destination (Join-Path $dst $relName) }
    [IO.File]::WriteAllText((Join-Path $dst $jsonName), ($json | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $dst
}

function Invoke-UpdateCorruptPackage {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.corrupt-package' -Title 'An update whose package is damaged never leaves Deluno unusable: the previous version keeps running or comes back, with its data and the pre-update backup' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }

        # 1. A package whose checksum fails: the download is refused and Deluno carries on.
        $bad = New-DamagedFeed -Kind checksum
        $feed = Start-UpdateFeed -Folder $bad -ThrottleMs 0
        try {
            Set-UpdateSourceToFeed | Out-Null
            $chk = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            Add-Check 'Check finds the update on the damaged feed' ($chk.Status -eq 200 -and $chk.Data.status.updateAvailable) $(if ($chk.Data) { $chk.Data.message } else { "answered $($chk.Status)" })
            $dl = Try-Deluno -Method POST -Path '/api/updates/download' -TimeoutSec 600
            Add-Check 'A package that fails its checksum is not accepted: the download says it failed and nothing is ready to restart' ($dl.Status -eq 200 -and -not $dl.Data.status.restartRequired -and $dl.Data.status.state -ne 'ready-to-restart') $(if ($dl.Data) { "$($dl.Data.message) | state $($dl.Data.status.state) | error $($dl.Data.status.lastError)" } else { "answered $($dl.Status) $($dl.Raw)" })
            Add-Check 'Deluno still answers on the previous release' ((Test-DelunoAnswering) -and @(Get-DelunoProcess | Where-Object { $_.Id -in $ctx.OldPids }).Count -gt 0) ''
            $rs = Try-Deluno -Method POST -Path '/api/updates/restart-now' -TimeoutSec 600
            Start-Sleep -Seconds 20 # reason: give a restart that should not happen time to show itself; nothing signals that it did not
            Add-Check 'Restart to update with nothing downloaded changes nothing: the previous release is still the installed one and answering' ((Test-DelunoAnswering) -and (Get-InstalledDelunoExe).VersionInfo.ProductVersion -notlike "*$CandidateVersion*") $(if ($rs.Data) { $rs.Data.message } else { "answered $($rs.Status)" })
        }
        finally { Stop-UpdateFeed $feed }

        # 2. A package that passes its checksum but is cut short (not a package): the install meets the damage.
        $cut = New-DamagedFeed -Kind truncated
        $feed2 = Start-UpdateFeed -Folder $cut -ThrottleMs 0
        try {
            Set-UpdateSourceToFeed | Out-Null
            $chk = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            $dl = Try-Deluno -Method POST -Path '/api/updates/download' -TimeoutSec 600
            Add-Check 'A cut-short package that matches its own checksum downloads (only the install can tell)' ($dl.Status -eq 200 -and $dl.Data.status.state -eq 'ready-to-restart') $(if ($dl.Data) { "$($dl.Data.message) | state $($dl.Data.status.state)" } else { "answered $($dl.Status) $($dl.Raw)" })
            if ($dl.Status -ne 200 -or $dl.Data.status.state -ne 'ready-to-restart') { return }
            $rs = Try-Deluno -Method POST -Path '/api/updates/restart-now' -TimeoutSec 600
            Add-Note $(if ($rs.Data) { "Restart to update answered: accepted $($rs.Data.accepted), '$($rs.Data.message)'" } else { "Restart to update answered $($rs.Status) $($rs.Raw)" })
            # The old process ends and a new one starts; judge only the new one, not the old one still answering while it stops.
            $up = Wait-Until -TimeoutSec 420 -IntervalSec 3 -Condition { $e = Get-InstalledDelunoExe; if ($e -and @(Get-DelunoProcess | Where-Object { $_.Id -notin $ctx.OldPids }).Count -gt 0 -and (Test-DelunoAnswering)) { $e } else { $null } }
            Add-Note $(if ($up.Ok) { "Deluno came back as a new process $($up.Seconds) s after the damaged install was tried." } else { 'No new Deluno process answered within 7 minutes.' })
            $e = Get-InstalledDelunoExe
            Save-Evidence -Name 'update-corrupt-state.txt' -Text ("answering: {0}`ninstalled: {1}`nprocesses: {2}`ncurrent folder: {3}`n" -f $up.Ok, $e.VersionInfo.ProductVersion, (@(Get-DelunoProcess | ForEach-Object { $_.Id }) -join ','), ((Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Deluno') | ForEach-Object { $_.Name }) -join ','))
            Add-Check 'Deluno is usable after the failed install (it answers)' $up.Ok $(if ($up.Ok) { "after $($up.Seconds) s" } else { 'Deluno never answered again after the damaged update: it was left unusable' })
            if (-not $up.Ok) { return }
            Add-Check 'The last known-good version runs (the installed build is still the previous release)' ($e.VersionInfo.ProductVersion -notlike "*$CandidateVersion*" -and $e.VersionInfo.ProductVersion -eq $ctx.PreviousVersion) "installed $($e.VersionInfo.ProductVersion), previous $($ctx.PreviousVersion)"
            $connected = $false; try { Connect-Deluno; $connected = $true } catch { }
            if ($connected) {
                $after = Get-DelunoStateSummary
                Add-Check 'Data is intact: clients, libraries and Activity are the same' ((($after.Clients -join ';') -eq ($ctx.Before.Clients -join ';')) -and (($after.Libraries -join ';') -eq ($ctx.Before.Libraries -join ';')) -and $after.Activity -ge $ctx.Before.Activity) ''
                Add-Check 'The pre-update backup taken for the attempt is intact' ((Get-BackupListText) -match 'pre-update') ''
            }
            else { Add-Check 'The signed-in account still works' $false '' }
        }
        finally { Stop-UpdateFeed $feed2 }
    }
}

function Invoke-UpdateSetupInteractive {
    param([Parameter(Mandatory)] [string] $CandidateSetup, [Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.setup-interactive' -Title 'The candidate''s Setup run the way a person runs it (not silent) over a running previous release leaves one Deluno running, as the candidate, with its state' -Body {
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $began = Get-Date
        $p = Start-Process -FilePath $CandidateSetup -PassThru
        $done = $p.WaitForExit(90000)
        if (-not $done) {
            $dlg = Wait-DesktopWindow -NamePattern '^Deluno Setup' -Process 'Deluno-Setup' -TimeoutSec 20
            if ($dlg) {
                $text = Get-WindowText -Window $dlg
                Add-Note "Setup's own window says: $text"
                foreach ($b in 'Install', 'Yes', 'Update', 'Continue', 'OK', 'Retry', 'Close') { if (Click-WindowButton -Window $dlg -NamePattern "^$b") { Add-Note "Pressed '$b' in Setup's window."; break } }
                $done = $p.WaitForExit(120000)
            }
        }
        if (-not $done) {
            Save-Evidence -Name 'update-interactive-windows.txt' -Text ("{0}`n{1}" -f (Format-DesktopWindows), ((Get-Process | Where-Object { $_.ProcessName -match 'Setup|Update|Deluno' } | ForEach-Object { "$($_.ProcessName) $($_.Id) '$($_.MainWindowTitle)'" }) -join "`n"))
            $done = $p.WaitForExit(510000)
        }
        Add-Check 'Setup.exe run interactively finished' ($done -and $p.ExitCode -eq 0) "exit $(if ($done) { $p.ExitCode } else { 'none' }) after $([int]((Get-Date) - $began).TotalSeconds) s"
        if (-not (Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion)) { return }
        Start-Sleep -Seconds 60 # reason: give a second start (Setup's own launch and the install hook's helper both starting Deluno) time to show itself
        $procs = @(Get-DelunoProcess)
        Add-Check 'Exactly one Deluno is running (Setup''s launch and the install hook did not start two)' ($procs.Count -eq 1) "$($procs.Count) process(es): $(($procs | ForEach-Object { $_.Id }) -join ', ')"
        Add-Check 'The previous release''s process is gone' (@($procs | Where-Object { $_.Id -in $ctx.OldPids }).Count -eq 0) ''
    }
}

function Invoke-UpdateSetupOverStopped {
    param([Parameter(Mandatory)] [string] $CandidateSetup, [Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.setup-over-stopped' -Title 'The candidate''s Setup run over a Deluno that was stopped installs the new build and starts nothing' -Body {
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $quit = Quit-DelunoFromTray
        Add-Check 'Deluno was quit from its tray menu before Setup' ($quit -and @(Get-DelunoProcess).Count -eq 0) ''
        # The install hook starts a Deluno whose job worker wrote to its database in the last 90 seconds, because a build from before the
        # running marks leaves no other trace; a Deluno stopped longer ago than that is a stopped one. Wait that window out.
        Start-Sleep -Seconds 110 # reason: the install hook's 90-second window for a Deluno that left no running mark; nothing signals its end
        $began = Get-Date
        $p = Start-Process -FilePath $CandidateSetup -ArgumentList '--silent' -PassThru
        $done = $p.WaitForExit(600000)
        Add-Check 'Setup.exe --silent over the stopped Deluno finished' ($done -and $p.ExitCode -eq 0) "exit $(if ($done) { $p.ExitCode } else { 'none' }) after $([int]((Get-Date) - $began).TotalSeconds) s"
        $e = Get-InstalledDelunoExe
        Add-Check 'The candidate is installed' ($e -and $e.VersionInfo.ProductVersion -like "*$CandidateVersion*") $e.VersionInfo.ProductVersion
        $started = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition { @(Get-DelunoProcess).Count -gt 0 }
        Add-Check 'Nothing started Deluno: it stays stopped for 2 minutes after the install' (-not $started.Ok) $(if ($started.Ok) { "a Deluno process appeared after $($started.Seconds) s" } else { 'no Deluno process' })
        # And it starts when a person starts it, with its state.
        Start-ProgramUnelevated -Path $e.FullName -WorkingDirectory $e.DirectoryName -Elevated
        Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion | Out-Null
    }
}

function Invoke-UpdateServiceMode {
    param([Parameter(Mandatory)] [string] $CandidateSetup, [Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.service-mode' -Title 'The candidate''s Setup run over a Deluno running as a Windows service brings the service back as the candidate, with its state, and starts no tray' -Body {
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $exe = Get-InstalledDelunoExe
        $quit = Quit-DelunoFromTray
        Add-Check 'The tray was quit before the service took over' ($quit -and @(Get-DelunoProcess).Count -eq 0) ''
        try {
            # What the tray's Run as a Windows service dialog does first: the service runs as another account, so it is told where this person's data is.
            $shared = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Deluno\data'
            New-Item -ItemType Directory -Force -Path $shared | Out-Null
            Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json') -Destination (Join-Path $shared 'deluno.json') -Force
            & $exe.FullName --install-service | Out-Null
            $svc = Wait-Until -TimeoutSec 180 -IntervalSec 2 -Condition { $s = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue; if ($s -and $s.State -eq 'Running' -and $s.ProcessId -gt 0) { $s } else { $null } }
            Add-Check 'The previous release runs as the Windows service "Deluno"' $svc.Ok $(if ($svc.Ok) { "pid $($svc.Value.ProcessId), $($svc.Value.StartName)" } else { 'the service never reached Running' })
            if (-not $svc.Ok) { return }
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
            Add-Check 'It answers on its port' $up.Ok ''
            if (-not $up.Ok) { return }
            Connect-Deluno
            $beforeSvc = Get-DelunoStateSummary
            Add-Check 'The service opened on the data the tray had (clients, libraries, Activity)' ((($beforeSvc.Clients -join ';') -eq ($ctx.Before.Clients -join ';')) -and (($beforeSvc.Libraries -join ';') -eq ($ctx.Before.Libraries -join ';'))) ("clients {0}, libraries {1}" -f $beforeSvc.Clients.Count, $beforeSvc.Libraries.Count)
            $ctx.Before = $beforeSvc
            $oldServicePid = [int]$svc.Value.ProcessId

            $began = Get-Date
            $p = Start-Process -FilePath $CandidateSetup -ArgumentList '--silent' -PassThru
            $done = $p.WaitForExit(600000)
            Add-Check 'Setup.exe --silent over the running service finished' ($done -and $p.ExitCode -eq 0) "exit $(if ($done) { $p.ExitCode } else { 'none' }) after $([int]((Get-Date) - $began).TotalSeconds) s"
            $back = Wait-Until -TimeoutSec 420 -IntervalSec 3 -Condition { $s = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue; if ($s -and $s.State -eq 'Running' -and $s.ProcessId -gt 0 -and $s.ProcessId -ne $oldServicePid -and (Test-DelunoAnswering)) { $s } else { $null } }
            Add-Check 'The service is running again, as a new process, and answers' $back.Ok $(if ($back.Ok) { "pid $($back.Value.ProcessId) after $($back.Seconds) s" } else { "service state '$((Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue).State)'" })
            if (-not $back.Ok) { return }
            Add-Check 'Setup started no tray: the only Deluno process is the service''s' (@(Get-DelunoProcess | Where-Object { $_.Id -ne $back.Value.ProcessId }).Count -eq 0) ((Get-DelunoProcess | ForEach-Object { $_.Id }) -join ', ')
            Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion | Out-Null
            Add-Check 'It is still a service that starts automatically' ((Get-CimInstance Win32_Service -Filter "Name='Deluno'").StartMode -eq 'Auto') ''
        }
        finally {
            $e2 = Get-InstalledDelunoExe
            if ($e2) { & $e2.FullName --uninstall-service | Out-Null }
        }
    }
}

function Invoke-UpdateTrayMenu {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.tray-menu' -Title 'The tray menu''s update item walks Check for updates, Download update and Restart to update, and the pre-update backup is taken before the install' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $feed = $null
        try {
            Set-UpdateSourceToFeed | Out-Null
            # The feed is not up yet: a check that cannot reach it says so plainly and the menu item stays "Check for updates".
            $down = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            Add-Check 'A check against a feed that is down says it could not check (never "up to date")' ($down.Status -eq 200 -and $down.Data.status.state -eq 'error' -and $down.Data.message -match "Couldn't check for updates") $(if ($down.Data) { "$($down.Data.message) | state $($down.Data.status.state)" } else { "answered $($down.Status)" })
            $feed = Start-UpdateFeed -Folder (Get-UpdateFeedFolder) -ThrottleMs 150
            $backupsBefore = @(Get-BackupFiles)
            $press = {
                param([string] $Pattern)
                $ok = $false
                for ($i = 0; $i -lt 3 -and -not $ok; $i++) { try { $ok = [bool](Invoke-TrayMenuItem -Icon 'Deluno' -Item $Pattern) } catch { $ok = $false } ; if (-not $ok) { Start-Sleep -Seconds 2 } } # reason: retry pause before reading the tray menu again
                $ok
            }
            Add-Check 'The update item first reads "Check for updates" and pressing it runs a check' (& $press '^Check for updates$') ''
            $found = Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { $s = Get-UpdateStatus; if ($s -and $s.updateAvailable -and $s.latestVersion -like "*$CandidateVersion*") { $s } else { $null } }
            Add-Check 'The check from the tray found the candidate' $found.Ok $(if ($found.Ok) { "latest $($found.Value.latestVersion), state $($found.Value.state)" } else { 'nothing found' })
            Add-Check 'The item now reads "Download update (v...)" and pressing it downloads' (& $press '^Download update \(v') ''
            # The download lasts about 20 s on this feed: the menu, opened now, shows the grey "Downloading update..." item.
            $names = @(Open-TrayMenu -Icon 'Deluno')
            Close-TrayMenu
            Add-Check 'While it downloads the item reads "Downloading update..."' (@($names | Where-Object { $_ -match '^Downloading update' }).Count -gt 0) ($names -join ' | ')
            $ready = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { $s = Get-UpdateStatus; if ($s -and $s.state -eq 'ready-to-restart') { $s } else { $null } }
            Add-Check 'The download from the tray ended ready to restart' $ready.Ok $(if ($ready.Ok) { "state $($ready.Value.state)" } else { 'never ready' })
            $began = Get-Date
            Add-Check 'The item now reads "Restart to update (v...)" and pressing it restarts into the update' (& $press '^Restart to update \(v') ''
            if (-not (Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion)) { return }
            Add-Note ("Back {0:N0} s after Restart to update was pressed in the tray menu." -f ((Get-Date) - $began).TotalSeconds)
            $new = @(Get-BackupFiles | Where-Object { $_ -notin $backupsBefore })
            # The previous release this phase installs predates the tray taking the backup itself (the Updates page route always did), so
            # this is recorded, not judged; from the next release the tray's Restart to update takes it first (PreUpdateBackupGate.BackUpBeforeRestartAsync).
            Add-Note $(if ($new.Count -gt 0) { "The tray's Restart to update took a pre-update backup: $($new -join ', ')." } else { "The previous release's tray menu Restart to update took NO pre-update backup (the Updates page route does); fixed in the build after rc.30.35." })
        }
        finally { Stop-UpdateFeed $feed }
    }
}
