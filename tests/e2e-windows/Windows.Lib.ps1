<#
.SYNOPSIS
    What the Windows end-to-end legs need from the machine they run on (docs/exec-plans/active/PLAN-E2E-IN-CI.md, step 3).

.DESCRIPTION
    Dot-source this file. The legs run on GitHub's `windows-latest` runner, which is a fresh Windows machine for every job, the way the
    golden-path VM's clean checkpoint was. The runner is already signed in to a desktop, so nothing here needs PowerShell Direct or a
    scheduled task: Deluno's Setup, the component picker driver and the tray watcher are started as ordinary hidden processes in the
    runner's own session, which UI Automation can see.

    The checks are the golden-path scenario suite's own (scripts/golden-path/suite/vm: Tray.Lib.ps1, Update.Lib.ps1, GithubLimit.Lib.ps1,
    scenarios/G01-Install.ps1). Results use the same JSON shape (Suite.Lib.ps1 Save-Results).
    Windows PowerShell 5.1.
#>

$script:WinRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:GoldenVm = Join-Path $script:WinRepoRoot 'scripts\golden-path\suite\vm'
# The golden path's scenarios keep the picker's log here; the install scenario (G01) reads it from this exact place.
$script:PickerLog = 'C:\golden\picker.log'

. (Join-Path $script:GoldenVm 'Suite.Lib.ps1')
. (Join-Path $script:GoldenVm 'Tray.Lib.ps1')
. (Join-Path $script:GoldenVm 'GithubLimit.Lib.ps1')
. (Join-Path $script:GoldenVm 'Initialize-Environment.ps1')
. (Join-Path $script:GoldenVm 'Update.Lib.ps1')
. (Join-Path $script:GoldenVm 'scenarios\G01-Install.ps1')
. (Join-Path $script:WinRepoRoot 'scripts\golden-path\suite\SuiteHost.Lib.ps1')

# Which scenarios each leg owns. A scenario that never reports is an error, not a skip (Assert-LegReported).
function Get-LegPlan {
    @(
        [pscustomobject]@{ Leg = 'install'; Ids = @('install-first-run', 'outage.tray') }
        [pscustomobject]@{ Leg = 'githubpicker'; Ids = @('github-limit.picker') }
        [pscustomobject]@{ Leg = 'update'; Ids = @('update.over-running-previous') }
    )
}

# What the runner offers, saved with the results: if the tray scenarios fail, this says whether there was a desktop to read.
function Get-RunnerPreflight {
    $session = (Get-Process -Id $PID).SessionId
    $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
    $taskbar = $false
    try { $taskbar = @(Get-TrayButtons).Count -gt 0 } catch { }
    $os = try { $o = Get-CimInstance Win32_OperatingSystem; "$($o.Caption) $($o.BuildNumber)" } catch { 'unknown' }
    [pscustomobject]@{
        Os = $os; SessionId = $session; UserInteractive = [Environment]::UserInteractive
        Explorer = ($explorer.Count -gt 0); TaskbarButtons = $taskbar
        Screen = ("{0}x{1}" -f [System.Windows.Forms.SystemInformation]::PrimaryMonitorSize.Width, [System.Windows.Forms.SystemInformation]::PrimaryMonitorSize.Height)
    }
}

# A picture of the desktop, kept with the evidence (uploaded by the job). Never throws.
function Save-RunnerScreen {
    param([Parameter(Mandatory)] [string] $Name)
    try {
        $dir = Join-Path (Get-SuiteRoot) 'results\evidence'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try { $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size) } finally { $g.Dispose() }
        $bmp.Save((Join-Path $dir "screen-$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
    catch { Write-SuiteLog "   could not save a picture of the desktop ($Name): $($_.Exception.Message)" }
}

# Windows Server opens Server Manager at sign-in and it covers the picker (rc.30.21): stop it now and keep it from opening again.
function Initialize-RunnerDesktop {
    New-Item -Path 'HKCU:\Software\Microsoft\ServerManager' -Force | Out-Null
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\ServerManager' -Name 'DoNotOpenServerManagerAtLogon' -Value 1 -Type DWord
    Get-Process ServerManager -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path 'C:\golden', 'C:\Media\Movies', 'C:\Downloads\Completed\Movies' | Out-Null
}

# The tray watcher (Watch-Tray.ps1) reads the Deluno and Weir icons once a second into the suite's logs from before Setup starts.
function Start-TrayWatch {
    $logs = Join-Path (Get-SuiteRoot) 'logs'
    $script:TrayWatchStop = Join-Path $logs 'traywatch.stop'
    $watchArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Log "{1}" -MenuLog "{2}" -StopFile "{3}"' -f (Join-Path $script:GoldenVm 'Watch-Tray.ps1'), (Join-Path $logs 'traywatch.jsonl'), (Join-Path $logs 'traymenu-first.json'), $script:TrayWatchStop
    $script:TrayWatchProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $watchArgs -WindowStyle Hidden -PassThru
}

function Stop-TrayWatch {
    if (-not $script:TrayWatchStop) { return }
    New-Item -ItemType File -Force -Path $script:TrayWatchStop | Out-Null
    if ($script:TrayWatchProcess) { [void]$script:TrayWatchProcess.WaitForExit(15000) }
    # The watcher must not touch the tray while a scenario reads it.
    if ($script:TrayWatchProcess -and -not $script:TrayWatchProcess.HasExited) { Stop-Process -Id $script:TrayWatchProcess.Id -Force -ErrorAction SilentlyContinue }
}

# The candidate's Setup, the way a person runs it: not silent, so first run shows the component picker, which the golden path's picker
# driver then ticks (every row, throwaway Usenet logins) and follows to "Finished.". Returns what the two processes did.
function Install-CandidateWithPicker {
    param([Parameter(Mandatory)] [string] $Setup, [int] $PickerMinutes = 50)
    Remove-Item -LiteralPath $script:PickerLog -ErrorAction SilentlyContinue
    $login = New-SuiteSecret 'usenet-password'
    $pickerArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -UsenetUser deluno -UsenetPassword {1} -LogPath "{2}" -SkipOpenDeluno' -f (Join-Path $script:WinRepoRoot 'scripts\golden-path\Invoke-ComponentPicker.ps1'), $login, $script:PickerLog
    $picker = Start-Process -FilePath 'powershell.exe' -ArgumentList $pickerArgs -WindowStyle Hidden -PassThru
    $began = Get-Date
    $setupProc = Start-Process -FilePath $Setup -PassThru
    # Setup installs, starts Deluno and ends; the picker is Deluno's own first-run window, driven until its finish screen.
    [void]$setupProc.WaitForExit(600000)
    $done = $picker.WaitForExit($PickerMinutes * 60000)
    if (-not $done) { Stop-Process -Id $picker.Id -Force -ErrorAction SilentlyContinue }
    [pscustomobject]@{
        SetupExit = $(if ($setupProc.HasExited) { $setupProc.ExitCode } else { $null }); PickerFinished = $done
        PickerExit = $(if ($done) { $picker.ExitCode } else { $null }); Seconds = [int]((Get-Date) - $began).TotalSeconds
    }
}

# Turns a list of {name, ok, detail} (what the golden path's host-side judges return) into this scenario's checks.
function Add-ChecksFrom {
    param([object[]] $Checks)
    foreach ($c in $Checks) { Add-Check $c.name ([bool]$c.ok) ([string]$c.detail) }
}

# Every scenario the leg owns reported; one that did not is an error record, so a leg that stopped early cannot read as a pass.
function Assert-LegReported {
    param([Parameter(Mandatory)] [string] $Leg)
    $plan = @(Get-LegPlan | Where-Object { $_.Leg -eq $Leg })
    if ($plan.Count -eq 0) { throw "Unknown leg '$Leg'." }
    foreach ($id in $plan[0].Ids) {
        if (@($script:S.Results | Where-Object { $_.id -eq $id }).Count -eq 0) {
            [void]$script:S.Results.Add([ordered]@{ id = $id; title = 'The scenario never reported'; status = 'error'; startedUtc = [DateTime]::UtcNow.ToString('o'); seconds = 0; checks = @(); notes = @(); error = "The $Leg leg ended without a result for $id." })
        }
    }
    Save-Results
}

# 0 when every scenario of the leg passed; 1 otherwise.
function Get-LegExitCode {
    $bad = @($script:S.Results | Where-Object { $_.status -ne 'pass' })
    if ($bad.Count -gt 0) { return 1 }
    0
}

# Starts Weir again as a person double-clicking it would. The runner's own session is the desktop, so no scheduled task is needed
# (the VM's Suite.Lib.ps1 version registers an interactive task because PowerShell Direct has no desktop). This replaces it.
function Start-WeirApp {
    $exe = Get-WeirExe
    if (-not $exe) { throw 'Weir.exe is not installed.' }
    Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName | Out-Null
    Start-Sleep -Seconds 3 # reason: settle after starting or stopping a process; no event to wait on
}

# Starts Deluno the way a person starts it, in the runner's own desktop session.
function Start-DelunoApp {
    $exe = Get-InstalledDelunoExe
    if (-not $exe) { throw 'Deluno.exe is not installed.' }
    Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName | Out-Null
}

# Adds checks to a scenario that already finished (one of the golden path's own, which cannot be given more checks from inside)
# and works its status out again.
function Add-LateChecks {
    param([Parameter(Mandatory)] [string] $Id, [Parameter(Mandatory)] [scriptblock] $Body)
    $rec = @($script:S.Results | Where-Object { $_.id -eq $Id }) | Select-Object -Last 1
    if (-not $rec) { return }
    $script:S.Current = $rec
    try { & $Body }
    catch { $rec.error = Hide-Secrets $_.Exception.Message }
    finally { $script:S.Current = $null }
    $failed = @($rec.checks | Where-Object { -not $_.ok })
    $rec.status = if ($rec.error) { 'error' } elseif ($failed.Count -gt 0) { 'fail' } elseif ($rec.checks.Count -eq 0) { 'error' } else { 'pass' }
    Save-Results
}

# Finds the one Setup executable a path names (the file itself, or the folder an artifact was downloaded into).
function Resolve-SetupExe {
    param([Parameter(Mandatory)] [string] $Path)
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Resolve-Path -LiteralPath $Path).Path }
    $found = @(Get-ChildItem -LiteralPath $Path -Filter '*Setup*.exe' -File -Recurse -ErrorAction SilentlyContinue)
    if ($found.Count -ne 1) { throw "Expected one Setup executable under $Path, found $($found.Count)." }
    $found[0].FullName
}
