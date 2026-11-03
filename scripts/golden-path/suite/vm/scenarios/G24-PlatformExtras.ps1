<#
    Platform rows that needed a real Windows and were left open (coverage issue 1278): the service manager's stop, the updater in service
    mode, what the tray starts again, the tray menu's two waiting-work lines, and what the installed machine listens on.

    service.stop-and-clean-shutdown  Stop from the service manager ends the service by itself, with the exit code 0 and no "terminated
                                     unexpectedly" event, and leaves every database closed (no write-ahead log left, each file opens
                                     exclusively) and no Deluno process behind.
    service.updates-in-service-mode  In service mode the updater says it cannot update itself: the install kind is not the Velopack one, no
                                     update control is offered, and Check, Download and Restart to update answer in plain words and do nothing.
    tray.restarts-clients-at-start   A client Deluno started that is not answering when the tray starts is started again; Weir, which Deluno
                                     never starts, is left stopped.
    tray.waiting-work-menu-lines     "Add Transmission's torrents back..." and "Try again: install apps..." appear when the work they offer
                                     is waiting, the first one runs and goes, the second goes when the list is cleared.
    install.loopback-and-hook-log    Every client's web UI and Weir listen on this PC only, the data folders the install made are there, and
                                     the install hook log tells each step in words with no failure in it.
#>

$script:ClientPortsExtras = [ordered]@{ qbittorrent = 8081; transmission = 9091; deluge = 8112; sabnzbd = 8080; nzbget = 6789 }

# The process id that listens on a port (the first), or 0.
function Get-ListenerProcessId {
    param([Parameter(Mandatory)] [int] $Port)
    $c = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { [int]$c.OwningProcess } else { 0 }
}

function Test-PortAnswers {
    param([Parameter(Mandatory)] [int] $Port)
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $Port); $c.Close(); $true } catch { $false }
}

# The Service Control Manager's events about Deluno written after a record number of the System log (read by number: the VM's clock is set again after a checkpoint is restored).
function Read-DelunoServiceEvents {
    param([long] $After)
    @(Get-WinEvent -LogName System -FilterXPath "*[System[Provider[@Name='Service Control Manager'] and EventRecordID>$After]]" -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'Deluno' })
}

# The settings file the tray's Run as a Windows service dialog copies to where the service (another account) reads it, then the service.
function Install-DelunoServiceNow {
    $exe = Get-DelunoExe
    $port = Get-LanPort
    $shared = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Deluno\data'
    $trayFile = Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json'
    New-Item -ItemType Directory -Force -Path $shared | Out-Null
    if (Test-Path $trayFile) { Copy-Item -LiteralPath $trayFile -Destination (Join-Path $shared 'deluno.json') -Force }
    else { Set-Content -Path (Join-Path $shared 'deluno.json') -Value (@{ port = $port; dataRoot = (Join-Path $env:LOCALAPPDATA 'DelunoData') } | ConvertTo-Json) -Encoding utf8 }
    $null = Quit-DelunoFromTray
    & $exe.FullName --install-service | Out-Null
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
    if ($up.Ok) { Connect-Deluno }
    $up.Ok
}

function Invoke-GroupPlatformExtras {
    Invoke-GroupPlatformExtrasPicker
    Invoke-Scenario -Id 'service.stop-and-clean-shutdown' -Title 'Stop from the service manager ends the Deluno service by itself and cleanly: exit code 0, no unexpected-termination event, every database closed, no process left' -Body {
        if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $sysData = $null
        try {
            $ok = Install-DelunoServiceNow
            Add-Check 'Deluno runs as the Windows service and answers' ($ok -and (Get-DelunoServiceFacts).State -eq 'Running') ''
            if (-not $ok) { return }
            $svcProc = (Get-DelunoServiceFacts).ProcessId
            Add-Check 'The service has a process of its own to stop' ($svcProc -gt 0) "process $svcProc"
            # Some work in flight: a request being served while the stop comes.
            # The system log is read by record number, not by time (the VM's clock is set again after its checkpoint is restored).
            $lastRecord = [long](@(Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction SilentlyContinue | ForEach-Object { $_.RecordId }) + 0)[0]
            $stopWatch = [Diagnostics.Stopwatch]::StartNew()
            Stop-Service -Name 'Deluno' -ErrorAction Stop
            $stopped = Wait-Until -TimeoutSec 90 -IntervalSec 0.5 -Condition { (Get-Service -Name 'Deluno').Status -eq 'Stopped' }
            $stopWatch.Stop()
            Add-Check 'The service manager reports it Stopped within 90 seconds, without being killed' $stopped.Ok ("{0:N1} s" -f $stopWatch.Elapsed.TotalSeconds)
            $left = Wait-Until -TimeoutSec 30 -IntervalSec 0.5 -Condition { -not (Get-Process -Id $svcProc -ErrorAction SilentlyContinue) }
            Add-Check 'The service process is gone: nothing is left holding the databases' $left.Ok ''
            $svc = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue
            Add-Check 'It stopped with exit code 0 (a clean stop, so the restart-on-failure actions do not run)' ($svc -and [int]$svc.ExitCode -eq 0) "exit code $($svc.ExitCode)"
            $null = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { @(Read-DelunoServiceEvents -After $lastRecord | Where-Object { $_.Id -eq 7036 -and $_.Message -match 'stopped' }).Count -gt 0 } # the log line follows the stop by a moment
            $events = @(Read-DelunoServiceEvents -After $lastRecord)
            $bad = @($events | Where-Object { $_.Id -in 7031, 7034 })
            $good = @($events | Where-Object { $_.Id -eq 7036 -and $_.Message -match 'stopped' })
            Add-Check 'Windows logged the service entering the stopped state, and no "terminated unexpectedly" or "terminated with an error"' ($good.Count -ge 1 -and $bad.Count -eq 0) "stopped events $($good.Count), unexpected-termination events $($bad.Count), after record $lastRecord, all Deluno events: $((($events | ForEach-Object { "$($_.Id)" }) -join ','))"

            # Databases closed: a clean close checkpoints the write-ahead log away, and each file can be taken exclusively.
            $dbs = @(Get-ChildItem -Path $dataRoot -Filter '*.db' -File -ErrorAction SilentlyContinue)
            $wal = @($dbs | ForEach-Object { Get-Item -LiteralPath ($_.FullName + '-wal') -ErrorAction SilentlyContinue } | Where-Object { $_.Length -gt 0 })
            Add-Check 'No database is left with an unmerged write-ahead log (they were closed, not cut off)' ($dbs.Count -ge 4 -and $wal.Count -eq 0) "$($dbs.Count) databases; with a log left: $((($wal | ForEach-Object { $_.Name }) -join ', '))"
            $locked = @()
            foreach ($db in $dbs) {
                try { $fs = [IO.File]::Open($db.FullName, 'Open', 'ReadWrite', 'None'); $fs.Close() } catch { $locked += $db.Name }
            }
            Add-Check 'Every database file can be opened exclusively (nothing holds it)' ($dbs.Count -ge 4 -and $locked.Count -eq 0) (($locked) -join ', ')
            Add-Check 'The service does not answer any more' (-not (Test-DelunoAnswering)) ''
        }
        finally {
            if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
            Add-Check 'The service is removed and the tray''s Deluno answers with its own data' (-not (Get-DelunoServiceFacts) -and (Restore-DelunoTray)) ''
        }
    }

    Invoke-Scenario -Id 'service.updates-in-service-mode' -Title 'As a Windows service Deluno does not offer to update itself: the updater says so in plain words and Check, Download and Restart to update do nothing' -Body {
        if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
        try {
            $ok = Install-DelunoServiceNow
            Add-Check 'Deluno runs as the Windows service and answers' ($ok -and (Get-DelunoServiceFacts).State -eq 'Running') ''
            if (-not $ok) { return }
            $st = Try-Deluno -Path '/api/updates/status'
            Save-Evidence -Name 'service-update-status.json' -Text $st.Raw
            Add-Check 'The updater answers its status' ($st.Status -eq 200) "HTTP $($st.Status)"
            if ($st.Status -ne 200) { return }
            $d = $st.Data
            Add-Check 'The service is not taken for the Velopack install: no check, download or apply control is offered' ($d.installKind -ne 'velopack' -and -not $d.canCheck -and -not $d.canDownload -and -not $d.canApply) "installKind $($d.installKind), canCheck $($d.canCheck), canDownload $($d.canDownload), canApply $($d.canApply)"
            Add-Check 'It reports the running version, a real one' ($d.currentVersion -and "$($d.currentVersion)" -notmatch '^0\.0\.0') "$($d.currentVersion)"
            $chk = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            Add-Check 'Check for updates answers and does not claim an update is available or ready' ($chk.Status -eq 200 -and -not $chk.Data.status.updateAvailable -and -not $chk.Data.status.restartRequired) "HTTP $($chk.Status): $($chk.Data.message)"
            $words = "$($chk.Data.message) $($chk.Data.status.message)"
            Add-Check 'The check says, in words, that this is not an install Deluno updates itself (never "up to date" as if it had looked)' ($words -match 'not a Velopack|installing a newer build|manual|cannot|can''t' -and $words -notmatch 'up to date') $words
            $dl = Try-Deluno -Method POST -Path '/api/updates/download' -TimeoutSec 120
            Add-Check 'Download update does nothing (nothing is ready to restart)' ($dl.Status -lt 500 -and -not $dl.Data.status.restartRequired) "HTTP $($dl.Status): $($dl.Data.message)"
            $pidBefore = (Get-DelunoServiceFacts).ProcessId
            $rs = Try-Deluno -Method POST -Path '/api/updates/restart-now' -TimeoutSec 120
            # The Updates page keeps its Restart to update button off here (it needs canApply and a downloaded update), so the route is not reached by a person; what it
            # answers is noted, and what matters is that nothing restarts.
            Add-Note "Restart to update, asked through the route, answered HTTP $($rs.Status): accepted $($rs.Data.accepted), '$($rs.Data.message)'."
            Start-Sleep -Seconds 15 # reason: give a restart that should not happen time to show itself; nothing signals that it did not
            $f = Get-DelunoServiceFacts
            Add-Check 'The service is not restarted: the same process is still running and answers' ($f.State -eq 'Running' -and $f.ProcessId -eq $pidBefore -and (Test-DelunoAnswering)) "process $pidBefore -> $($f.ProcessId)"
        }
        finally {
            if (Get-DelunoServiceFacts) { Remove-DelunoServiceNow }
            Add-Check 'The service is removed and the tray''s Deluno answers with its own data' (-not (Get-DelunoServiceFacts) -and (Restore-DelunoTray)) ''
        }
    }

    Invoke-Scenario -Id 'tray.restarts-clients-at-start' -Title 'When the tray starts, a client Deluno started that is not answering is started again; Weir, which Deluno never starts, is left as it was' -Body {
        Connect-Deluno
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $choices = Join-Path $dataRoot 'config\started-components.json'
        $remembered = if (Test-Path $choices) { @(Get-Content -Raw $choices | ConvertFrom-Json) } else { @() }
        $kinds = @($remembered | Where-Object { $script:ClientPortsExtras.Contains([string]$_) } | ForEach-Object { [string]$_ })
        Add-Check 'Deluno remembers the clients it started (so there is something to start again)' ($kinds.Count -ge 3) ($kinds -join ', ')
        if ($kinds.Count -lt 1) { return }
        $weirWasUp = Test-WeirAnswering
        try {
            $null = Quit-DelunoFromTray
            Add-Check 'The tray is quit' (@(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue).Count -eq 0) ''
            if ($weirWasUp) { $null = Stop-WeirApp }
            Add-Check 'Weir is stopped by hand' (-not (Test-PortAnswers 9347)) ''
            # What a restart of Windows does to them: the clients are not running when the tray comes up at sign-in.
            $ended = @()
            foreach ($kind in $kinds) {
                $procId = Get-ListenerProcessId -Port $script:ClientPortsExtras[$kind]
                if ($procId -gt 0) { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue; $ended += $kind }
            }
            $down = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { @($kinds | Where-Object { Test-PortAnswers $script:ClientPortsExtras[$_] }).Count -eq 0 }
            Add-Check 'Every client Deluno started is stopped' $down.Ok "stopped: $($ended -join ', ')"
            $exe = Get-DelunoExe
            Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
            $back = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition { @($kinds | Where-Object { -not (Test-PortAnswers $script:ClientPortsExtras[$_]) }).Count -eq 0 }
            $missing = @($kinds | Where-Object { -not (Test-PortAnswers $script:ClientPortsExtras[$_]) })
            Add-Check 'The tray started each of them again by itself' $back.Ok $(if ($back.Ok) { "all $($kinds.Count) answered after $($back.Seconds) s" } else { "not answering: $($missing -join ', ')" })
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check 'Deluno answers' $up.Ok ''
            # Weir is its own app with its own tray: Deluno never starts it, however long it waits.
            Start-Sleep -Seconds 60 # reason: nothing is expected to start; the wait is the proof that Deluno does not start Weir
            Add-Check 'Weir is still stopped: Deluno never starts it' (-not (Test-PortAnswers 9347) -and -not (Get-Process -Name 'WeirServer' -ErrorAction SilentlyContinue)) ''
        }
        finally {
            if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) {
                $exe = Get-DelunoExe
                Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
            }
            if ($weirWasUp -and -not (Test-PortAnswers 9347)) { Start-WeirApp }
            $null = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Connect-Deluno
            Add-Check 'Deluno answers and Weir is running as it was' ((Test-DelunoAnswering) -and ((-not $weirWasUp) -or (Wait-Until -TimeoutSec 60 -IntervalSec 2 -Condition { Test-PortAnswers 9347 }).Ok)) ''
        }
    }

    Invoke-Scenario -Id 'tray.waiting-work-menu-lines' -Title 'The tray menu offers "Add Transmission''s torrents back..." and "Try again: install apps..." only while that work is waiting; the first one runs and goes, the second goes when its list is cleared' -Body {
        Connect-Deluno
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $readd = Join-Path $dataRoot 'components\transmission-readd.json'
        $pending = Join-Path $dataRoot 'config\pending-component-installs.json'
        $hadReadd = Test-Path $readd; $hadPending = Test-Path $pending
        $readdBefore = if ($hadReadd) { Get-Content -Raw $readd } else { $null }
        $pendingBefore = if ($hadPending) { Get-Content -Raw $pending } else { $null }
        try {
            Remove-Item -LiteralPath $readd, $pending -Force -ErrorAction SilentlyContinue
            $items = @(Get-TrayMenuStates)
            Add-Check 'With nothing waiting, neither line is in the menu' (@($items | Where-Object { $_.Name -match "^Add Transmission|^Try again" }).Count -eq 0) (($items | ForEach-Object { $_.Name }) -join ' | ')

            # The notes a reinstall that lost Transmission's folder, and a picker stopped by GitHub's limit, leave.
            New-Item -ItemType Directory -Force -Path (Split-Path $readd) | Out-Null
            Set-Content -Path $readd -Encoding utf8 -Value (@{ CreatedUtc = [DateTime]::UtcNow.ToString('o'); DaemonNeedsRestart = $false; Torrents = @(); Summary = $null } | ConvertTo-Json -Compress) # the names the app itself writes (its files are read with the exact names)
            New-Item -ItemType Directory -Force -Path (Split-Path $pending) | Out-Null
            $at = [DateTime]::UtcNow.AddHours(2).ToString('o') # in the future: once the wait is over the tray offers the picker by itself
            Set-Content -Path $pending -Encoding utf8 -Value (@{ Kinds = @('weir'); RetryAfterUtc = $at; AskedByPerson = $false; Reason = "GitHub's hourly limit on looking up releases was used up." } | ConvertTo-Json -Compress)
            $shown = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition {
                $names = @(Get-TrayMenuStates | ForEach-Object { $_.Name })
                if (@($names | Where-Object { $_ -match "^Add Transmission's torrents back" }).Count -eq 1 -and @($names | Where-Object { $_ -match '^Try again: install ' }).Count -eq 1) { $names } else { $null }
            }
            Add-Check 'With both waiting, the menu shows both lines' $shown.Ok $(if ($shown.Ok) { ($shown.Value | Where-Object { $_ -match "^Add Transmission|^Try again" }) -join ' | ' } else { (@(Get-TrayMenuStates | ForEach-Object { $_.Name })) -join ' | ' })
            if ($shown.Ok) {
                $try = @($shown.Value | Where-Object { $_ -match '^Try again: install ' })[0]
                Add-Check 'The second line names the app waiting and when GitHub allows it, and ends in "..."' ($try -match '^Try again: install Weir \(GitHub allows it from .+\)\.\.\.$') $try
                $states = @(Get-TrayMenuStates | Where-Object { $_.Name -match "^Add Transmission|^Try again" })
                Add-Check 'Both lines can be pressed' (@($states | Where-Object { -not $_.Enabled }).Count -eq 0) (($states | ForEach-Object { "$($_.Name)=$($_.Enabled)" }) -join ' | ')
            }

            # Pressing the first: Transmission is read, nothing needs adding, and the note goes.
            $pressed = Invoke-TrayMenuItem -Icon 'Deluno' -Item "^Add Transmission's torrents back"
            Add-Check 'The first line can be pressed' $pressed $script:TrayMenuMiss
            $gone = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { -not (Test-Path $readd) }
            Add-Check 'It reads Transmission, adds back what it holds and removes the note' $gone.Ok ''
            # The answer is a message box named Deluno (it holds the tray until it is closed).
            $box = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 30
            $boxText = if ($box) { Get-WindowText -Window $box } else { '' }
            Add-Check 'It says what it did, in words, in a message box' ($null -ne $box -and $boxText -match 'torrents') $boxText
            if ($box) { Click-WindowButton -Window $box -NamePattern '^OK$' | Out-Null }
            $after = @(Get-TrayMenuStates | ForEach-Object { $_.Name })
            Add-Check 'The first line is gone from the menu and the second stays' (@($after | Where-Object { $_ -match "^Add Transmission" }).Count -eq 0 -and @($after | Where-Object { $_ -match '^Try again: install ' }).Count -eq 1) ($after -join ' | ')

            # A list with no app left in it: the line goes.
            Set-Content -Path $pending -Encoding utf8 -Value (@{ Kinds = @(); RetryAfterUtc = $null; AskedByPerson = $false; Reason = $null } | ConvertTo-Json -Compress)
            $cleared = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { @(Get-TrayMenuStates | Where-Object { $_.Name -match '^Try again' }).Count -eq 0 }
            Add-Check 'When the list of waiting apps is emptied the second line goes too' $cleared.Ok ''
        }
        finally {
            Remove-Item -LiteralPath $readd, $pending -Force -ErrorAction SilentlyContinue
            $stray = Wait-DesktopWindow -NamePattern '^Set up download clients and Weir$' -Process 'Deluno' -TimeoutSec 2
            if ($stray) { Close-DesktopWindow -Window $stray }
            if ($hadReadd) { Set-Content -Path $readd -Value $readdBefore -Encoding utf8 }
            if ($hadPending) { Set-Content -Path $pending -Value $pendingBefore -Encoding utf8 }
            Add-Check 'The two notes are as found' ((Test-Path $readd) -eq $hadReadd -and (Test-Path $pending) -eq $hadPending) ''
        }
    }

    Invoke-Scenario -Id 'install.loopback-and-hook-log' -Title 'After the install every client''s web UI and Weir listen on this PC only, the data folders are in place, and the install hook log tells each step with no failure in it' -Body {
        foreach ($kind in $script:ClientPortsExtras.Keys) {
            $port = [int]$script:ClientPortsExtras[$kind]
            $listen = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Select-Object -Unique)
            Add-Check "$kind listens on port $port and only on this PC" ($listen.Count -gt 0 -and @($listen | Where-Object { $_ -notin '127.0.0.1', '::1' }).Count -eq 0) ($listen -join ', ')
        }
        $weir = @(Get-NetTCPConnection -State Listen -LocalPort 9347 -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Select-Object -Unique)
        Add-Check 'Weir listens on port 9347 and only on this PC' ($weir.Count -gt 0 -and @($weir | Where-Object { $_ -notin '127.0.0.1', '::1' }).Count -eq 0) ($weir -join ', ')
        # From another address of this PC (the LAN address), a client's port is refused.
        $lan = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } | Select-Object -First 1)
        if ($lan.Count -gt 0) {
            $reach = @($script:ClientPortsExtras.Keys | Where-Object { try { $c = New-Object Net.Sockets.TcpClient; $r = $c.BeginConnect($lan[0].IPAddress, [int]$script:ClientPortsExtras[$_], $null, $null); $ok = $r.AsyncWaitHandle.WaitOne(2000) -and $c.Connected; $c.Close(); $ok } catch { $false } })
            Add-Check 'Not one client''s port answers on this PC''s network address' ($reach.Count -eq 0) "answering on the network address: $($reach -join ', ')"
        }
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $dirs = @(Get-ChildItem -Path $dataRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $need = 'backups', 'components', 'config', 'logs', 'protection-keys', 'secrets'
        Add-Check 'The data folder holds the folders the install makes' (@($need | Where-Object { $_ -notin $dirs }).Count -eq 0) "missing: $((@($need | Where-Object { $_ -notin $dirs })) -join ', ')"
        $dbs = @(Get-ChildItem -Path $dataRoot -Filter '*.db' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        Add-Check 'The databases are there' (@(@('jobs.db', 'movies.db', 'platform.db', 'series.db', 'cache.db') | Where-Object { $_ -notin $dbs }).Count -eq 0) ($dbs -join ', ')
        $hook = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $text = if (Test-Path $hook) { Get-Content -Raw $hook } else { '' }
        Add-Check 'The install hook log exists and has lines of its own' ($text.Length -gt 200) "$($text.Length) characters"
        Add-Check 'Each line begins with a time' (@($text -split "`r?`n" | Where-Object { $_ } | Where-Object { $_ -notmatch '^\d{4}-\d\d-\d\dT' }).Count -eq 0) ''
        $names = 'qbittorrent', 'transmission', 'deluge', 'sabnzbd', 'nzbget', 'weir'
        $told = @($names | Where-Object { $text -match "component '$_'" })
        Add-Check 'It says what became of each of the six apps the picker installed' ($told.Count -eq 6) "told: $($told -join ', ')"
        $failures = @($text -split "`r?`n" | Where-Object { $_ -match 'Exception|FAILED|Failed|could not|Could not|\bat Deluno\.' })
        Add-Check 'It holds no failure, no "could not" and no stack trace' ($failures.Count -eq 0) (($failures | Select-Object -First 3) -join ' | ')
        Save-Evidence -Name 'install-hook.log' -Text $text
    }
}

# ---------------------------------------------------------------- the host's own: on a restored clean machine with the previous release installed
# These run through Run-Update.ps1 (one scenario per machine) like the other update paths in UpdatePaths.Lib.ps1.

# The data files the installed Deluno holds that must survive, as names under the data folder, for comparing before and after.
function Get-DataFileList {
    $data = Join-Path $env:LOCALAPPDATA 'DelunoData'
    @(Get-ChildItem -Path $data -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.db', '.zip' -or $_.FullName -match 'protection-keys' } | ForEach-Object { $_.FullName.Substring($data.Length) })
}

function Invoke-UpdatePreferencesAndBackgroundCheck {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.preferences-and-background-check' -Title 'The update preferences are saved and read back across a restart; with auto-check on and a download mode chosen, the check runs by itself 20 seconds after start and the update downloads without a click; a check that cannot reach the feed says why in the Updates page''s words and in a balloon' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $feed = Start-UpdateFeed -Folder (Get-UpdateFeedFolder) -ThrottleMs 0
        try {
            Set-UpdateSourceToFeed | Out-Null
            # ---- saved and read back, across a restart
            $orig = (Try-Deluno -Path '/api/updates/preferences').Data
            $put = Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ mode = 'download-apply-on-restart'; channel = ' Beta '; autoCheck = $true }
            Add-Check 'Saving the mode, the channel and auto-check answers with what was saved (the channel tidied to lower case)' ($put.Status -eq 200 -and $put.Data.mode -eq 'download-apply-on-restart' -and $put.Data.channel -eq 'beta' -and $put.Data.autoCheck -eq $true) $put.Raw
            $get = Try-Deluno -Path '/api/updates/preferences'
            Add-Check 'Reading them back gives the same three values' ($get.Data.mode -eq 'download-apply-on-restart' -and $get.Data.channel -eq 'beta' -and $get.Data.autoCheck -eq $true) $get.Raw
            $bad = Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ mode = 'surprise-me' }
            Add-Check 'A mode that is not one of the three is ignored; the saved mode stays' ((Try-Deluno -Path '/api/updates/preferences').Data.mode -eq 'download-apply-on-restart') "HTTP $($bad.Status)"
            $null = Restart-DelunoAs -Elevated $true
            $again = Try-Deluno -Path '/api/updates/preferences'
            Add-Check 'After Deluno is quit and started again the three values are still the ones saved' ($again.Data.mode -eq 'download-apply-on-restart' -and $again.Data.channel -eq 'beta' -and $again.Data.autoCheck -eq $true) $again.Raw
            Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ channel = $orig.channel } | Out-Null

            # ---- the check runs by itself, 20 s after start, and the download follows
            Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ mode = 'download-background'; autoCheck = $true } | Out-Null
            Clear-DownloadedUpdates
            Stop-UpdateFeed $feed
            $feed = Start-UpdateFeed -Folder (Get-UpdateFeedFolder) -ThrottleMs 0 # a fresh request log
            $quit = Quit-DelunoFromTray
            Add-Check 'Deluno is quit from its tray menu' ($quit -and @(Get-DelunoProcess).Count -eq 0) ''
            $exe = Get-InstalledDelunoExe
            Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
            $asked = Wait-Until -TimeoutSec 240 -IntervalSec 1 -Condition { @(Get-UpdateFeedRequests | Where-Object { $_ -match 'releases\.|RELEASES' }).Count -gt 0 }
            $procStart = (Get-DelunoProcess | Select-Object -First 1).StartTime.ToUniversalTime()
            $first = if ($asked.Ok) { [DateTime]::Parse((@(Get-UpdateFeedRequests | Where-Object { $_ -match 'releases\.|RELEASES' })[0] -split ' ')[0]).ToUniversalTime() } else { $null }
            $after = if ($first) { ($first - $procStart).TotalSeconds } else { -1 }
            Add-Check 'With auto-check on, Deluno asks the feed for its release list by itself after it starts' $asked.Ok $(if ($asked.Ok) { "{0:N0} s after the process started" -f $after } else { 'the feed was never asked' })
            Add-Check 'It waits about 20 seconds first (not at once, and soon after the host is up)' ($asked.Ok -and $after -ge 19 -and $after -le 120) ("{0:N0} s after the process started" -f $after)
            Connect-Deluno
            $got = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition { $s = Get-UpdateStatus; if ($s -and $s.restartRequired -and $s.latestVersion -like "*$CandidateVersion*") { $s } else { $null } }
            Add-Check 'In a download mode the update is downloaded by itself and waits to be applied, with nobody pressing Download' $got.Ok $(if ($got.Ok) { "state $($got.Value.state), after $($got.Seconds) s" } else { "status: $((Get-UpdateStatus | ConvertTo-Json -Compress -Depth 3))" })
            Add-Check 'The feed served the package' (@(Get-UpdateFeedRequests | Where-Object { $_ -match '200 .*nupkg' }).Count -gt 0) ((Get-UpdateFeedRequests) -join ' | ')

            # ---- a check that cannot reach the feed
            Try-Deluno -Method PUT -Path '/api/updates/preferences' -Body @{ mode = 'notify-only'; autoCheck = $false } | Out-Null
            Clear-DownloadedUpdates
            Stop-UpdateFeed $feed; $feed = $null
            $chk = Try-Deluno -Method POST -Path '/api/updates/check' -TimeoutSec 120
            $st = $chk.Data.status
            $words = "$($chk.Data.message) $($st.message)"
            Add-Check 'The in-app check says why it failed in the Updates page''s words, and claims no update' ($chk.Status -eq 200 -and $words -match "Couldn't check for updates" -and -not $st.updateAvailable) $words
            $before = Get-ToastTextCount -Text "Couldn't check for updates"
            $pressed = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Check for updates'
            Add-Check 'The tray''s Check for updates item is pressed' $pressed $script:TrayMenuMiss
            Add-Check 'The tray tells the person in a balloon ("Couldn''t check for updates")' (Wait-ToastShown -Text "Couldn't check for updates" -Before $before -TimeoutSec 60) ''
            $line = @(Get-TrayMenuStates | Where-Object { $_.Name -match "Couldn't check" })
            Add-Check 'The menu''s status line carries the same sentence' ($line.Count -ge 1) (($line | ForEach-Object { $_.Name }) -join ' | ')
        }
        finally { Stop-UpdateFeed $feed }
    }
}

function Invoke-UpdateKilledMidApply {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.killed-mid-apply' -Title 'An update whose install is killed part-way (the installer ended by force the moment it started) never leaves Deluno unusable: the install still starts and answers, with its data' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx) { return }
        $d = Initialize-DownloadedUpdate -Context $ctx -CandidateVersion $CandidateVersion
        try {
            if (-not $d.Downloaded) { return }
            $filesBefore = @(Get-DataFileList)
            # Quit applies a downloaded update: Update.exe starts. It is ended the moment it is seen, again if it comes back, until it stays gone.
            $watch = Start-Job -ScriptBlock {
                $end = (Get-Date).AddMinutes(6); $killed = 0; $quietSince = $null
                while ((Get-Date) -lt $end) {
                    $running = @(Get-Process -Name 'Update' -ErrorAction SilentlyContinue)
                    foreach ($p in $running) { try { $p.Kill(); $killed++ } catch { } }
                    if ($running.Count -gt 0) { $quietSince = $null } elseif ($killed -gt 0) { if (-not $quietSince) { $quietSince = Get-Date } elseif (((Get-Date) - $quietSince).TotalSeconds -ge 20) { break } }
                    Start-Sleep -Milliseconds 50 # reason: a tight loop must catch an installer that lives only seconds
                }
                $killed
            }
            $quit = Quit-DelunoFromTray
            Add-Check 'Quit Deluno from the tray menu ends the process' ($quit -and @(Get-DelunoProcess).Count -eq 0) ''
            $killed = Receive-Job $watch -Wait -AutoRemoveJob
            Add-Check 'The installer was started by the quit and ended by force part-way' ([int]$killed -ge 1) "ended $killed time(s)"
            $exe = Get-InstalledDelunoExe
            Add-Check 'A Deluno.exe is still installed (the install folder was not left empty)' ($null -ne $exe) $(if ($exe) { $exe.VersionInfo.ProductVersion } else { 'none' })
            if (-not $exe) { return }
            Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
            $up = Wait-Until -TimeoutSec 420 -IntervalSec 3 -Condition { Test-DelunoAnswering }
            Add-Check 'Deluno starts and answers after the interrupted install' $up.Ok $(if ($up.Ok) { "after $($up.Seconds) s, as $((Get-InstalledDelunoExe).VersionInfo.ProductVersion)" } else { 'never answered' })
            if (-not $up.Ok) { return }
            $connected = $false; try { Connect-Deluno; $connected = $true } catch { }
            Add-Check 'The signed-in account still works' $connected ''
            if ($connected) {
                $after = Get-DelunoStateSummary
                Add-Check 'The data is intact: clients, libraries and Activity are the same' ((($after.Clients -join ';') -eq ($ctx.Before.Clients -join ';')) -and (($after.Libraries -join ';') -eq ($ctx.Before.Libraries -join ';')) -and $after.Activity -ge $ctx.Before.Activity) ''
                $lost = @($filesBefore | Where-Object { $_ -notin @(Get-DataFileList) })
                Add-Check 'No database or key file was lost' ($lost.Count -eq 0) ($lost -join ', ')
            }
            Add-Note "After the interrupted install the installed build is $((Get-InstalledDelunoExe).VersionInfo.ProductVersion) (previous $($ctx.PreviousVersion), candidate $CandidateVersion)."
        }
        finally { Stop-UpdateFeed $d.Feed }
    }
}

function Invoke-UpdateOlderBuildNewerDatabase {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'update.older-build-newer-database' -Title 'The previous release''s Setup run over the updated Deluno (a manual rollback): the older build does not open the newer database in silence, and the data is kept' -Body {
        Assert-UpdateFeedFiles | Out-Null
        $olderSetup = 'C:\golden\Deluno-rc-Setup.exe'
        Add-Check 'The previous release''s Setup.exe is in the VM' (Test-Path $olderSetup) $olderSetup
        $ctx = Start-PreviousForUpdate -CandidateVersion $CandidateVersion
        if (-not $ctx -or -not (Test-Path $olderSetup)) { return }
        $d = Initialize-DownloadedUpdate -Context $ctx -CandidateVersion $CandidateVersion
        try {
            if (-not $d.Downloaded) { return }
            $rs = Try-Deluno -Method POST -Path '/api/updates/restart-now' -TimeoutSec 600
            Add-Check 'Restart to update is accepted' ($rs.Status -eq 200 -and $rs.Data.accepted) ''
            if (-not (Test-CameBackAsCandidate -Context $ctx -CandidateVersion $CandidateVersion)) { return }
            $filesNewer = @(Get-DataFileList)
            $newer = Get-InstalledDelunoExe
            # The older Setup, silent, over the newer install (the way someone puts an older version back).
            $null = Quit-DelunoFromTray
            $p = Start-Process -FilePath $olderSetup -ArgumentList '--silent' -PassThru
            $done = $p.WaitForExit(600000)
            Add-Note "The older Setup --silent ended: $(if ($done) { "exit $($p.ExitCode)" } else { 'timed out' })"
            Wait-InstallerQuiet | Out-Null
            $now = Get-InstalledDelunoExe
            Add-Note "Installed build after the older Setup: $($now.VersionInfo.ProductVersion) (was $($newer.VersionInfo.ProductVersion))"
            if ($now.VersionInfo.ProductVersion -like "*$CandidateVersion*") {
                Add-Check 'Setup of an older version over a newer install left the newer build installed' $true "installed $($now.VersionInfo.ProductVersion), exit $($p.ExitCode)"
            }
            else {
                # The older build is the installed one: start it on the newer data and see what it does.
                Start-ProgramUnelevated -Path $now.FullName -WorkingDirectory $now.DirectoryName -Elevated
                $up = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { Test-DelunoAnswering }
                $log = Join-Path $env:LOCALAPPDATA 'Deluno\logs\tray-startup.log'
                $tail = if (Test-Path $log) { (Get-Content $log -Tail 40) -join ' | ' } else { '' }
                Save-Evidence -Name 'older-build-start-log.txt' -Text $tail
                if ($up.Ok) {
                    $connected = $false; try { Connect-Deluno; $connected = $true } catch { }
                    $says = if ($connected) { (Get-ActivityText | Where-Object { $_ -match 'newer|downgrad|older' } | Select-Object -First 1) } else { $null }
                    Add-Check 'The older build that opens newer data says so in words (it must not run on it without a word)' ([bool]$says) $(if ($says) { $says } else { 'it started and answered, and nothing in Activity says the data is newer than it' })
                }
                else {
                    $says = $tail -match 'newer|downgrad|older|cannot open|can''t open'
                    Add-Check 'The older build refuses the newer data and says why in its log' ([bool]$says) $tail
                }
                $lost = @($filesNewer | Where-Object { $_ -notin @(Get-DataFileList) })
                Add-Check 'No database, backup or key file was lost' ($lost.Count -eq 0) ($lost -join ', ')
            }
        }
        finally { Stop-UpdateFeed $d.Feed }
    }
}

function Invoke-UninstallLeavesNothingBehind {
    Invoke-Scenario -Id 'uninstall.leaves-nothing-behind' -Title 'Uninstall from Apps while Deluno runs: the running Deluno is stopped, the sign-in entry does not point at a deleted program, the hook is quick, and a reinstall finds the old data' -Body {
        $ctx = Start-PreviousForUpdate -CandidateVersion 'no-candidate-here'
        if (-not $ctx) { return }
        $readEntry = { $p = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue; if ($p) { $p.PSObject.Properties | Where-Object { [string]$_.Value -match 'Deluno\.exe' } | Select-Object -First 1 } }
        if (-not (& $readEntry)) { Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null }
        $entry = (Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { & $readEntry }).Value
        Add-Check 'Start with Windows is on before the uninstall' ($null -ne $entry) $(if ($entry) { [string]$entry.Value } else { '' })
        $installFolder = Join-Path $env:LOCALAPPDATA 'Deluno'
        $kept = @(Get-DataFileList)
        Add-Check 'Deluno is running (the tray and its host) when the uninstall starts' (@(Get-DelunoProcess).Count -gt 0 -and (Test-DelunoAnswering)) ''
        $hookLog = Join-Path $installFolder 'logs\install-hook.log'
        $hookBefore = if (Test-Path $hookLog) { @(Get-Content $hookLog).Count } else { 0 }

        $began = Get-Date
        $r = Invoke-DelunoUninstall
        $took = ((Get-Date) - $began).TotalSeconds
        Add-Check 'The uninstaller (Update.exe --uninstall --silent) finished with exit code 0' $r.Ok $r.Detail
        $gone = Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { -not (Test-Path (Join-Path $installFolder 'current\Deluno.exe')) -and @(Get-DelunoProcess).Count -eq 0 }
        Add-Check 'The running Deluno was stopped and its program removed: no Deluno process, no Deluno.exe in the install folder' $gone.Ok "processes left: $(@(Get-DelunoProcess).Count)"
        $hookLines = if (Test-Path $hookLog) { @(Get-Content $hookLog | Select-Object -Skip $hookBefore) } else { @() }
        Add-Note ("The uninstall took {0:N1} s; the hook log gained {1} line(s): {2}" -f $took, $hookLines.Count, ($hookLines -join ' | '))
        Add-Check 'The whole uninstall, hook included, ends within a minute (the hook itself has Velopack''s 15 seconds)' ($r.Ok -and $took -lt 60) ("{0:N1} s" -f $took)
        $stale = & $readEntry
        $staleOk = (-not $stale) -or (Test-Path -LiteralPath (([string]$stale.Value).Trim('"')))
        Add-Check 'The sign-in entry is removed, or points at a program that is still there (never at a deleted one)' $staleOk $(if ($stale) { "left: $([string]$stale.Value)" } else { 'removed' })
        Add-Check 'No Deluno Windows service is left behind' (-not (Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue)) ''
        $now = @(Get-DataFileList)
        Add-Check 'The data folder is kept whole' (@($kept | Where-Object { $_ -notin $now }).Count -eq 0) "$($kept.Count) files"

        # Reinstall: the candidate's Setup finds the old data and is not a first run.
        $setup = 'C:\golden\candidate\Deluno-Setup.exe'
        Add-Check 'The candidate''s Setup is in the VM' (Test-Path $setup) $setup
        if (-not (Test-Path $setup)) { return }
        $p = Start-Process -FilePath $setup -ArgumentList '--silent' -PassThru
        $done = $p.WaitForExit(600000)
        Add-Check 'Setup --silent over the uninstalled machine finished' ($done -and $p.ExitCode -eq 0) "exit $(if ($done) { $p.ExitCode } else { 'none' })"
        Wait-InstallerQuiet | Out-Null
        $exe = Get-InstalledDelunoExe
        Add-Check 'The reinstall put Deluno.exe back' ($null -ne $exe) ''
        if (-not $exe) { return }
        Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
        $up = Wait-Until -TimeoutSec 420 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'The reinstalled Deluno starts and answers' $up.Ok ''
        if (-not $up.Ok) { return }
        $boot = Try-Deluno -Path '/api/auth/bootstrap-status'
        Add-Check 'It finds the old data: the account is there and first-run setup is not asked again' ($boot.Status -eq 200 -and $boot.Data.requiresSetup -eq $false) $boot.Raw
        $connected = $false; try { Connect-Deluno; $connected = $true } catch { }
        Add-Check 'The old sign-in works and the old libraries are there' ($connected -and @(Get-Library).Count -gt 0) ''

        # Uninstalled again while Deluno runs as a Windows service: the service must not be left behind pointing at a program that is gone.
        $null = Quit-DelunoFromTray
        $exe2 = Get-InstalledDelunoExe
        $shared = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Deluno\data'
        New-Item -ItemType Directory -Force -Path $shared | Out-Null
        $trayFile = Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json'
        if (Test-Path $trayFile) { Copy-Item -LiteralPath $trayFile -Destination (Join-Path $shared 'deluno.json') -Force }
        & $exe2.FullName --install-service | Out-Null
        $svcUp = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { $x = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue; if ($x -and $x.State -eq 'Running') { $x } else { $null } }
        Add-Check 'Deluno runs as a Windows service before the second uninstall' $svcUp.Ok ''
        if ($svcUp.Ok) {
            $r2 = Invoke-DelunoUninstall
            Add-Check 'The uninstaller finished with exit code 0 over the running service' $r2.Ok $r2.Detail
            Start-Sleep -Seconds 20 # reason: a service whose process was ended is started again by Windows after its restart delay; nothing signals it
            $left = Get-CimInstance Win32_Service -Filter "Name='Deluno'" -ErrorAction SilentlyContinue
            Add-Check 'No Deluno service is left behind pointing at the deleted program' (-not $left) $(if ($left) { "left: $($left.State), $($left.PathName)" } else { 'removed' })
            if ($left) { & sc.exe delete Deluno | Out-Null }
        }
    }
}

function Invoke-InstallSilentFirstInstall {
    param([Parameter(Mandatory)] [string] $CandidateVersion)
    Invoke-Scenario -Id 'install.silent-first-install' -Title 'Setup.exe --silent on a machine with no Deluno installs, exits 0 and starts nothing; the first start afterwards is a plain start: none of the first-run steps (firewall rule, Start with Windows question, app picker) run' -Body {
        $setup = 'C:\golden\candidate\Deluno-Setup.exe'
        Add-Check 'The candidate''s Setup.exe is in the VM' (Test-Path $setup) $setup
        $ctx = Start-PreviousForUpdate -CandidateVersion 'no-candidate-here'
        if (-not $ctx -or -not (Test-Path $setup)) { return }
        # A clean machine again: the previous release is uninstalled and every trace of its data and settings removed.
        $null = Quit-DelunoFromTray
        $u = Invoke-DelunoUninstall
        Add-Check 'The previous release is uninstalled' $u.Ok $u.Detail
        Get-NetFirewallRule -DisplayName 'Deluno*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Deluno' -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'DelunoData'), (Join-Path $env:LOCALAPPDATA 'DelunoConfig'), (Join-Path $env:LOCALAPPDATA 'Deluno') -Recurse -Force -ErrorAction SilentlyContinue
        Add-Check 'The machine has no Deluno program, data or settings left' (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Deluno\current\Deluno.exe')) -and -not (Test-Path (Join-Path $env:LOCALAPPDATA 'DelunoData'))) ''

        $p = Start-Process -FilePath $setup -ArgumentList '--silent' -PassThru
        $done = $p.WaitForExit(600000)
        Add-Check 'Setup.exe --silent finished with exit code 0' ($done -and $p.ExitCode -eq 0) "exit $(if ($done) { $p.ExitCode } else { 'none (timed out)' })"
        Wait-InstallerQuiet | Out-Null
        $exe = Get-InstalledDelunoExe
        Add-Check 'Deluno is installed, as the candidate' ($exe -and $exe.VersionInfo.ProductVersion -like "*$CandidateVersion*") $(if ($exe) { $exe.VersionInfo.ProductVersion } else { 'no Deluno.exe' })
        Start-Sleep -Seconds 45 # reason: a Deluno wrongly started by the installer appears within seconds of it finishing; nothing signals "nothing will start"
        Add-Check 'Nothing was started: no Deluno process and nothing answering on its port' (@(Get-DelunoProcess).Count -eq 0 -and -not (Test-DelunoAnswering)) "processes: $(@(Get-DelunoProcess).Count)"
        Add-Check 'No sign-in entry and no firewall rule were made by the install' (-not (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue | ForEach-Object { $_.PSObject.Properties } | Where-Object { [string]$_.Value -match 'Deluno\.exe' }) -and @(Get-NetFirewallRule -DisplayName 'Deluno*' -ErrorAction SilentlyContinue).Count -eq 0) ''
        $hook = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $hookText = if (Test-Path $hook) { Get-Content -Raw $hook } else { '' }
        Add-Check 'The install hook log says the install ran' ($hookText -match 'after install') ''

        # The first start afterwards.
        if (-not $exe) { return }
        Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
        $up = Wait-Until -TimeoutSec 420 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'Deluno starts and answers' $up.Ok ''
        if (-not $up.Ok) { return }
        $boot = Try-Deluno -Path '/api/auth/bootstrap-status'
        Add-Check 'It is a fresh Deluno: it asks for the first account' ($boot.Status -eq 200 -and $boot.Data.requiresSetup -eq $true) $boot.Raw
        Start-Sleep -Seconds 30 # reason: first-run steps would show their windows and write their log lines within seconds of the start; nothing signals "none will"
        $hookText2 = if (Test-Path $hook) { Get-Content -Raw $hook } else { '' }
        Add-Check 'No first-run step ran: the install log has no "first run" line' ($hookText2 -notmatch 'first run:') (($hookText2 -split "`r?`n" | Where-Object { $_ -match 'first run' } | Select-Object -First 2) -join ' | ')
        Add-Check 'No firewall rule was added by a first run' (@(Get-NetFirewallRule -DisplayName 'Deluno*' -ErrorAction SilentlyContinue).Count -eq 0) ''
        $question = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 3
        Add-Check 'No "Start Deluno when you sign in" question and no app picker is on screen' ($null -eq $question) $(if ($question) { Get-WindowText -Window $question } else { '' })
        Add-Check 'No sign-in entry was made' (-not (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue | ForEach-Object { $_.PSObject.Properties } | Where-Object { [string]$_.Value -match 'Deluno\.exe' })) ''
    }
}

# Inside the group: a second picker on a machine that has the apps already.
function Invoke-GroupPlatformExtrasPicker {
    Invoke-Scenario -Id 'install.second-picker-leaves-installed-apps' -Title 'A picker opened again on a machine that has the apps marks each as already installed and will not be touched, refuses to tick it, and Install selected with nothing ticked closes it without running any installer' -Body {
        Connect-Deluno
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $pending = Join-Path $dataRoot 'config\pending-component-installs.json'
        $hook = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $hookBefore = if (Test-Path $hook) { (Get-Content -Raw $hook).Length } else { 0 }
        Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue
        try {
            $ask = Try-Deluno -Method POST -Path '/api/companion-tools/tray-install' -Body @{ toolIds = @('nzbget') }
            Add-Check 'Asking for the picker is accepted (HTTP 202)' ($ask.Status -eq 202) "HTTP $($ask.Status)"
            $dlg = Wait-DesktopWindow -NamePattern '^Set up download clients and Weir$' -Process 'Deluno' -TimeoutSec 90
            Add-Check 'The picker window opens' ($null -ne $dlg) $(if ($dlg) { $dlg.Name } else { 'windows: ' + (Format-DesktopWindows) })
            if (-not $dlg) { return }
            $boxes = @(Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'CheckBox' -and -not $_.Offscreen })
            Save-Evidence -Name 'second-picker-window.txt' -Text (Get-WindowText -Window $dlg)
            Add-Check 'It lists the six apps' ($boxes.Count -eq 6) (($boxes | ForEach-Object { "$($_.Name) enabled=$($_.Enabled)" }) -join ' | ')
            $labelled = @($boxes | Where-Object { $_.Name -match 'already installed, will not be touched' })
            Add-Check 'Each of the six apps is marked "already installed, will not be touched"' ($boxes.Count -eq 6 -and $labelled.Count -eq 6) (($boxes | ForEach-Object { $_.Name }) -join ' | ')
            # A person pressing a row: the tick is refused, so an installed app cannot be ticked and so cannot be installed again.
            foreach ($b in $boxes) { try { $b.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { } }
            Start-Sleep -Milliseconds 700 # reason: UI settle after input; Windows raises no event for it
            $again = @(Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'CheckBox' -and -not $_.Offscreen })
            $ticked = @($again | Where-Object { try { $_.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On' } catch { $false } })
            Add-Check 'Pressing every row ticks none of them' ($again.Count -eq 6 -and $ticked.Count -eq 0) "ticked: $((($ticked | ForEach-Object { $_.Name }) -join ', '))"
            # Install selected with nothing ticked closes the window and runs nothing.
            Click-WindowButton -Window $dlg -NamePattern '^Install selected$' | Out-Null
            $closed = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern '^Set up download clients and Weir$' -Process 'Deluno' -TimeoutSec 1) }
            Add-Check 'Install selected with nothing ticked closes the picker' $closed.Ok ''
            $hookAfter = if (Test-Path $hook) { Get-Content -Raw $hook } else { '' }
            $newText = if ($hookAfter.Length -gt $hookBefore) { $hookAfter.Substring($hookBefore) } else { '' }
            Add-Check 'No installer ran: the install log gained no download, checksum or install line' ($newText -notmatch 'installer .* sha256|SHA-256|Installed:') $newText
        }
        finally { Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue }
    }
}
