<#
.SYNOPSIS
    Proves a release candidate on the golden-path VM before it is tagged: installs it, runs every scenario unattended, keeps the
    logs, and records the golden-path commit status itself (docs/golden-path.md, "The scenario suite"; Deluno issue 1238).

.DESCRIPTION
    1. Runs the install half of the golden path (Invoke-GoldenPath.ps1 -Interactive -UsenetStub): restores the clean checkpoint, installs
       the installer the way a person does, ticks the five download clients plus Weir in the component picker. A tray watcher is started
       in the VM before Setup so the first-run tray can be read afterwards.
    2. Checks the installed build is the commit under test (Deluno.exe reports <version>+<sha>), so a status is never recorded for a
       commit that was not the one installed.
    3. Copies the suite into the VM and starts it in the signed-in desktop session (Run-Suite.ps1). It sets Deluno up through its API with
       throwaway logins made in the VM and never printed, then runs every scenario and writes a result after each one.
    4. Collects Deluno's and Weir's full logs, the /processors/events request lines, the tray watcher log and the suite's own record,
       and writes one results file with a pass or fail for each scenario and its evidence.
    5. Records the commit status: state=success only when every scenario passed; state=failure, naming the failed scenarios, otherwise.
       context=golden-path is what scripts/tag-release.ps1 and the release workflow read.

    Access is PowerShell remoting to the Hyper-V host, then PowerShell Direct into the VM, with the same two DPAPI credential files as
    Invoke-GoldenPath.ps1. No host name, address or login is stored in this repository: the host is a parameter. Windows PowerShell 5.1.

.PARAMETER RigHost
    The Hyper-V host that holds the VM.

.PARAMETER PreviousInstallerPath
    The previous release's Setup.exe, required for a full run (and for -Groups update). The update phase (update.over-running-previous,
    #1256) runs AFTER the groups, the log collection and -AfterSuiteScript: it restores the checkpoint, installs this previous release
    silently, starts it, gives it state, then runs the candidate's Setup --silent over it while it runs, and requires Deluno back on
    its own with its state. It never installs over the candidate the groups used (that would be a downgrade onto a newer schema).
    Without it the phase fails and says so, and so does the verdict: nothing waives it.

.PARAMETER PreviousVersion
    The previous release's version (for example 1.0.0-rc.30.25), which the install step checks the installed build against. Read
    from the installer's file name when it carries one.

.PARAMETER InstallerPath
    The release candidate's Windows installer (Deluno-rc-Setup.exe, from a manual ci.yml run on the commit).

.PARAMETER Version
    The version being tested, for example 1.0.0-rc.30.21.

.PARAMETER Commit
    The commit the installer was built from. The status is recorded on this commit, and only if the installed build carries it.

.PARAMETER DryRun
    Run everything but record nothing: the status command is printed, not run.

.PARAMETER ListOnly
    Print the scenarios and what each proves, then stop. Touches nothing.

.PARAMETER Groups
    Run only these groups (install, films, removal, resend, stuck, outage, tryagain, vanish, problems, update). A partial run never records a pass.

.PARAMETER SkipInstall
    Use the VM as it is (already installed, for example after an earlier run); skips the checkpoint restore and the picker.

.PARAMETER AfterSetupScript
    A script on this machine to run once Deluno is set up in the VM and before any group: Weir's own suite runs its Update phase
    here (Weir's login is left for it). Its exit code is reported but does not change Deluno's verdict.

.PARAMETER AfterSuiteScript
    A script on this machine to run after the groups and the log collection (Weir's login and the key are left for it again):
    Weir's WithDeluno and Fresh phases. Its exit code is reported but does not change Deluno's verdict.

.PARAMETER ResultsUrl
    Optional target_url for the status (where the results file can be read).

.EXAMPLE
    ./scripts/golden-path/Invoke-ScenarioSuite.ps1 -RigHost <host> -InstallerPath .\Deluno-rc-Setup.exe -Version 1.0.0-rc.30.21 -Commit <sha>
#>
[CmdletBinding()]
param(
    [string] $RigHost,
    [string] $InstallerPath,
    [string] $PreviousInstallerPath,
    [string] $PreviousVersion,
    [string] $Version,
    [string] $Commit,
    [string] $VmName = 'Deluno-GoldenPath',
    [string] $Checkpoint = 'clean',
    [string] $RigCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-rig-admin.xml'),
    [string] $VmCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-goldenvm-admin.xml'),
    [string] $OutDirectory,
    [string[]] $Groups = @(),
    [string] $Repository = 'jampat000/Deluno',
    [string] $ResultsUrl,
    [int] $TimeoutMinutes = 240,
    [switch] $DryRun,
    [switch] $ListOnly,
    [switch] $SkipInstall,
    [string] $AfterSetupScript,
    [string] $AfterSuiteScript
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$suiteDir = Join-Path $PSScriptRoot 'suite'
. (Join-Path $suiteDir 'ScenarioPlan.ps1')
. (Join-Path $suiteDir 'SuiteHost.Lib.ps1')

$plan = @(Get-ScenarioPlan)
# "-Groups resend,stuck" through powershell -File arrives as one string; split it.
$Groups = @($Groups | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Groups.Count -gt 0) {
    $unknown = @($Groups | Where-Object { $_ -notin $plan.Name })
    if ($unknown.Count -gt 0) { throw "Unknown group(s): $($unknown -join ', '). Groups: $($plan.Name -join ', ')." }
}

if ($ListOnly) {
    $topics = Get-ScenarioTopics
    foreach ($g in $plan) {
        Write-Host ("{0,-9} {1} scenarios" -f $g.Name, $g.Ids.Count) -ForegroundColor Cyan
        Write-Host ("          " + ($topics | Where-Object Group -eq $g.Name).Proves)
        Write-Host ("          " + ($g.Ids -join ', ')) -ForegroundColor DarkGray
    }
    Write-Host ("{0} scenarios in all." -f (($plan | ForEach-Object { $_.Ids.Count } | Measure-Object -Sum).Sum))
    return
}

foreach ($p in 'RigHost', 'InstallerPath', 'Version', 'Commit') { if (-not (Get-Variable $p -ValueOnly)) { throw "-$p is required." } }
if ($PreviousInstallerPath -and -not (Test-Path $PreviousInstallerPath)) { throw "Previous installer not found: $PreviousInstallerPath" }

# The full commit, so the status lands on the right one.
$fullSha = $Commit
if ($Commit -notmatch '^[0-9a-fA-F]{40}$') {
    $resolved = & git -C $repoRoot rev-parse --verify "$Commit^{commit}" 2>$null
    if ($LASTEXITCODE -eq 0 -and $resolved) { $fullSha = $resolved.Trim() }
    elseif (-not $DryRun) { throw "Commit '$Commit' is not a commit in this repository, so the status could not be recorded on it. Pass the full sha, or fetch it." }
}

if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot ("artifacts\scenario-suite-{0}-{1}" -f $Version, (Get-Date -Format 'yyyyMMdd-HHmm')) }
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew()
$warnings = New-Object System.Collections.ArrayList
Write-Host "Scenario suite for $Version ($fullSha). Results: $OutDirectory$(if ($DryRun) { ' (DRY RUN: no status will be recorded)' })" -ForegroundColor Cyan

# ------------------------------------------------------------------ the files that go into the VM

# Every file the suite needs, as relative path -> text. The suite's own scripts go under C:\golden\suite, the feed's under C:\golden\feed.
function Get-VmFileBundle {
    $map = [ordered]@{}
    foreach ($f in Get-ChildItem -Path (Join-Path $suiteDir 'vm') -Recurse -File -Filter '*.ps1') {
        $rel = $f.FullName.Substring($suiteDir.Length + 1)
        $map[('suite\' + $rel)] = Get-Content -Raw $f.FullName
    }
    $map['suite\ScenarioPlan.ps1'] = Get-Content -Raw (Join-Path $suiteDir 'ScenarioPlan.ps1')
    foreach ($f in Get-ChildItem -Path (Join-Path $PSScriptRoot 'torznab-feed') -File -Filter '*.ps1') { $map[('feed\' + $f.Name)] = Get-Content -Raw $f.FullName }
    $map
}

# Runs a block inside the VM through the rig (PowerShell Direct). The block comes back as text and is rebuilt there, so it
# must not capture variables: pass them in -ArgumentList.
function Invoke-InVmVia {
    param($Session, [string] $Name, $Credential, [scriptblock] $Block, [object[]] $ArgumentList = @())
    Invoke-Command -Session $Session -ArgumentList $Block.ToString(), $ArgumentList, $Name, $Credential -ScriptBlock {
        param($text, $vmArgs, $n, $cred)
        $sb = [scriptblock]::Create($text)
        Invoke-Command -VMName $n -Credential $cred -ArgumentList $vmArgs -ScriptBlock $sb
    }
}

function Send-BundleToVm {
    param($Session, [string] $Name, $Credential, $Bundle)
    Invoke-InVmVia -Session $Session -Name $Name -Credential $Credential -ArgumentList @(, $Bundle) -Block {
        param($files)
        foreach ($rel in $files.Keys) {
            $dest = Join-Path 'C:\golden' $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            Set-Content -Path $dest -Value $files[$rel] -Encoding utf8
        }
    } | Out-Null
}

# Starts a hidden task in the VM's signed-in desktop session (the suite needs a desktop for the tray and for Edge).
$startTaskBlock = {
    param($taskName, $arguments)
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
}

$bundle = Get-VmFileBundle

# ------------------------------------------------------------------ 1. install (the harness)

$harnessFile = Join-Path $OutDirectory ("golden-path-{0}-{1}.md" -f $Version, (Get-Date -Format 'yyyy-MM-dd'))
$harnessFailures = @()
$harnessRows = @()
if (-not $SkipInstall) {
    # Before Setup runs, start the tray watcher in the desktop session, so the first-run icon is read from its first second.
    $hook = {
        param($rigSession, $name, $cred)
        Send-BundleToVm -Session $rigSession -Name $name -Credential $cred -Bundle $bundle
        # Windows Server opens Server Manager at sign-in and it covers the picker: close it now and stop it opening again.
        Invoke-InVmVia -Session $rigSession -Name $name -Credential $cred -Block {
            New-Item -Path 'HKCU:\Software\Microsoft\ServerManager' -Force | Out-Null
            Set-ItemProperty -Path 'HKCU:\Software\Microsoft\ServerManager' -Name 'DoNotOpenServerManagerAtLogon' -Value 1 -Type DWord
            Get-Process ServerManager -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        } | Out-Null
        Invoke-InVmVia -Session $rigSession -Name $name -Credential $cred -ArgumentList @('GoldenSuiteTray', '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\golden\suite\vm\Watch-Tray.ps1"') -Block $startTaskBlock | Out-Null
    }
    $harness = Join-Path $PSScriptRoot 'Invoke-GoldenPath.ps1'
    Write-Host 'Installing the candidate the way a person does (restore checkpoint, Setup, component picker)...' -ForegroundColor Cyan
    & $harness -RigHost $RigHost -InstallerPath $InstallerPath -Version $Version -VmName $VmName -Checkpoint $Checkpoint `
        -RigCredentialFile $RigCredentialFile -VmCredentialFile $VmCredentialFile -OutDirectory $OutDirectory `
        -Interactive -UsenetStub -UsenetStubSmall -AfterVmReady $hook
    $harnessRows = @(Read-HarnessRows -File $harnessFile)
    $harnessFailures = @($harnessRows | Where-Object { $_.Status -eq 'failed' } | ForEach-Object { $_.Step })
    if ($harnessRows.Count -eq 0) { [void]$warnings.Add("The harness wrote no results file at $harnessFile.") }
}

# ------------------------------------------------------------------ 2..4 run the suite in the VM

$rigCred = Import-Clixml $RigCredentialFile
$vmCred = Import-Clixml $VmCredentialFile
$rig = New-PSSession -ComputerName $RigHost -Credential $rigCred
$reported = @()
$installedBuild = ''
$buildMatches = $false
$runnerNote = ''
try {
    $installedBuild = [string](Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -Block {
        $exe = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Deluno') -Recurse -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\current\\' } | Select-Object -First 1
        if (-not $exe) { $exe = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Deluno') -Recurse -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if ($exe) { $exe.VersionInfo.ProductVersion } else { '' }
    })
    $buildMatches = Test-BuildMatchesCommit -ProductVersion $installedBuild -Commit $fullSha
    Write-Host "Installed build: '$installedBuild'. It carries the commit under test: $buildMatches" -ForegroundColor $(if ($buildMatches) { 'Green' } else { 'Red' })

    if ($harnessFailures.Count -gt 0 -or -not $buildMatches) {
        $runnerNote = if (-not $buildMatches) { 'The suite did not run: the installed build is not the commit under test.' } else { "The suite did not run: the install failed ($($harnessFailures -join '; '))." }
        Write-Host $runnerNote -ForegroundColor Red
    }
    else {
        Send-BundleToVm -Session $rig -Name $VmName -Credential $vmCred -Bundle $bundle
        # The tray watcher has done its job (the install is over); it must not touch the tray while the scenarios read it.
        Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -Block { New-Item -ItemType File -Force -Path 'C:\golden\suite\logs\traywatch.stop' | Out-Null; Start-Sleep -Seconds 3 } | Out-Null
        $runArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\golden\suite\vm\Run-Suite.ps1" -Root "C:\golden\suite"'
        if ($AfterSetupScript) {
            # Deluno is set up first (Connect Weir makes Weir's account), then Weir's Update phase runs, then the groups on the same
            # environment. Weir's WithDeluno phase judges only hand-offs made after its update, so the groups must come after it.
            Write-Host 'Setting Deluno up before the hand-over to the after-setup script...' -ForegroundColor Cyan
            Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -ArgumentList @('GoldenSuiteSetup', ($runArgs + ' -SetupOnly')) -Block $startTaskBlock | Out-Null
            $setupDone = $null
            $setupDeadline = (Get-Date).AddMinutes(30)
            while ((Get-Date) -lt $setupDeadline -and $null -eq $setupDone) {
                Start-Sleep -Seconds 15
                $setupDone = Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -Block { $f = 'C:\golden\suite\results\done.flag'; if (Test-Path $f) { (Get-Content -Raw $f).Trim() } else { $null } }
            }
            if ($setupDone -ne 'ok') { throw "Setting Deluno up before the after-setup script did not finish: $(if ($setupDone) { $setupDone } else { 'no done flag in 30 minutes' })" }
            Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -Block { Remove-Item -LiteralPath 'C:\golden\suite\results\done.flag' -ErrorAction SilentlyContinue } | Out-Null
            Write-Host "Running the after-setup script: $AfterSetupScript" -ForegroundColor Cyan
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $AfterSetupScript
            $afterSetupExit = $LASTEXITCODE
            Write-Host "The after-setup script exited $afterSetupExit." -ForegroundColor $(if ($afterSetupExit -eq 0) { 'Green' } else { 'Yellow' })
            if ($afterSetupExit -ne 0) { [void]$warnings.Add("The after-setup script ($AfterSetupScript) exited $afterSetupExit.") }
            $runArgs += ' -SkipEnvironment'
        }
        if ($Groups.Count -gt 0) { $runArgs += ' -Groups ' + ($Groups -join ',') }
        Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -ArgumentList @('GoldenSuiteRun', $runArgs) -Block $startTaskBlock | Out-Null
        Write-Host 'The suite is running in the VM. Progress:' -ForegroundColor Cyan

        $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
        $lastLine = ''
        $finished = $false
        $quietSince = $null
        while ((Get-Date) -lt $deadline -and -not $finished) {
            Start-Sleep -Seconds 20
            $state = Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -Block {
                $o = [ordered]@{ Done = $null; Json = $null; Running = $false }
                $flag = 'C:\golden\suite\results\done.flag'
                if (Test-Path $flag) { $o.Done = (Get-Content -Raw $flag).Trim() }
                $rf = 'C:\golden\suite\results\results.json'
                if (Test-Path $rf) { $o.Json = Get-Content -Raw $rf }
                $o.Running = [bool](Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'Run-Suite' })
                [pscustomobject]$o
            }
            if ($state.Json) {
                try {
                    $parsed = $state.Json | ConvertFrom-Json
                    $reported = @($parsed.scenarios)
                    $counts = @($reported | Group-Object status | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
                    $line = "  [{0:N0} min] {1}; last: {2}" -f $clock.Elapsed.TotalMinutes, $counts, $(if ($reported.Count) { $reported[-1].id } else { '-' })
                    if ($line -ne $lastLine) { Write-Host $line; $lastLine = $line }
                } catch { }
            }
            if ($null -ne $state.Done) { $finished = $true; if ($state.Done -ne 'ok') { $runnerNote = "The suite stopped early: $($state.Done)" } }
            elseif (-not $state.Running) {
                if (-not $quietSince) { $quietSince = Get-Date }
                elseif (((Get-Date) - $quietSince).TotalSeconds -gt 90) { $runnerNote = 'The suite process is gone and left no done flag.'; $finished = $true }
            }
            else { $quietSince = $null }
        }
        if (-not $finished) { $runnerNote = "The suite did not finish within $TimeoutMinutes minutes."; [void]$warnings.Add($runnerNote) }
        if ($runnerNote) { Write-Host $runnerNote -ForegroundColor Yellow }
    }

    # ------------------------------------------------------------------ keep the logs
    try {
        $summary = Invoke-InVmVia -Session $rig -Name $VmName -Credential $vmCred -Block { & 'C:\golden\suite\vm\Collect-Logs.ps1' -Root 'C:\golden\suite' }
        Write-Host "Logs: $summary"
        $staging = Invoke-Command -Session $rig -ScriptBlock { $d = Join-Path $env:TEMP ('golden-logs-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null; $d }
        try {
            Invoke-Command -Session $rig -ArgumentList $staging, $VmName, $vmCred -ScriptBlock {
                param($dir, $n, $cred)
                $s = New-PSSession -VMName $n -Credential $cred
                try { Copy-Item -FromSession $s -Path 'C:\golden\suite\collect.zip' -Destination $dir } finally { Remove-PSSession $s }
            }
            Copy-Item -FromSession $rig -Path (Join-Path $staging 'collect.zip') -Destination (Join-Path $OutDirectory 'vm-logs.zip') -Force
            $logDir = Join-Path $OutDirectory 'vm-logs'
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
            Expand-Archive -Path (Join-Path $OutDirectory 'vm-logs.zip') -DestinationPath $logDir -Force
            $eventsLog = Join-Path $logDir 'processors-events-requests.log'
            if (Test-Path $eventsLog) {
                $n = @(Get-Content $eventsLog).Count
                Write-Host "Kept /processors/events request lines: $n"
                if ($n -eq 0) { [void]$warnings.Add('No /processors/events request lines were found in Deluno''s log.') }
            }
            else { [void]$warnings.Add('The /processors/events request log was not collected.') }
            foreach ($need in @(@('deluno', 'Deluno''s logs'), @('weir', 'Weir''s logs'))) {
                if (@(Get-ChildItem (Join-Path $logDir $need[0]) -File -ErrorAction SilentlyContinue).Count -eq 0) { [void]$warnings.Add("$($need[1]) were not collected.") }
            }
        }
        finally { Invoke-Command -Session $rig -ArgumentList $staging -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
    }
    catch { [void]$warnings.Add("Collecting the logs failed: $($_.Exception.Message)") }
}
finally { Remove-PSSession $rig -ErrorAction SilentlyContinue }

# ------------------------------------------------------------------ Weir's phases, then the update phase

# Weir's WithDeluno and Fresh phases follow on the VM as the groups left it (the run left Weir's login and the key again).
if ($AfterSuiteScript -and -not ($harnessFailures.Count -gt 0 -or -not $buildMatches)) {
    Write-Host "Running the after-suite script: $AfterSuiteScript" -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $AfterSuiteScript
    Write-Host "The after-suite script exited $LASTEXITCODE." -ForegroundColor $(if ($LASTEXITCODE -eq 0) { 'Green' } else { 'Yellow' })
}

# The update phase (#1256) runs last of all, after the groups, the log collection and the after-suite script, and depends on nothing
# before it: it restores the clean checkpoint itself and installs the PREVIOUS release on it. It never installs over the candidate
# the groups used, which would be a downgrade onto a newer schema.
$updateId = 'update.over-running-previous'
$updateTitle = 'Deluno updated over a running previous release comes back on its own, as the new build, with its state, and its start goes cleanly'
function New-UpdateFailureRecord([string] $Why) {
    [pscustomobject]@{ id = $updateId; title = $updateTitle; status = 'fail'; seconds = 0; error = $null; notes = @()
        checks = @([pscustomobject]@{ name = 'The update phase could run'; ok = $false; detail = $Why }) }
}

function Copy-FileToVm {
    param($Session, [string] $LocalPath, [string] $VmFolder, [string] $VmName2, $Cred)
    $staged = Invoke-Command -Session $Session -ScriptBlock { $d = Join-Path $env:TEMP ('golden-up-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null; $d }
    try {
        Copy-Item -Path $LocalPath -Destination (Join-Path $staged 'Deluno-Setup.exe') -ToSession $Session
        $hash = (Get-FileHash $LocalPath -Algorithm SHA256).Hash
        $inVm = Invoke-Command -Session $Session -ArgumentList $staged, $VmFolder, $VmName2, $Cred -ScriptBlock {
            param($dir, $dest, $n, $c)
            $s = New-PSSession -VMName $n -Credential $c
            try {
                Invoke-Command -Session $s -ArgumentList $dest -ScriptBlock { param($d) New-Item -ItemType Directory -Force -Path $d | Out-Null }
                Copy-Item -Path (Join-Path $dir 'Deluno-Setup.exe') -Destination (Join-Path $dest 'Deluno-Setup.exe') -ToSession $s -Force
                Invoke-Command -Session $s -ArgumentList $dest -ScriptBlock { param($d) (Get-FileHash (Join-Path $d 'Deluno-Setup.exe') -Algorithm SHA256).Hash }
            } finally { Remove-PSSession $s }
        }
        if ($inVm -ne $hash) { throw "Hash mismatch after copy to $VmFolder (local $hash, VM $inVm)." }
    }
    finally { Invoke-Command -Session $Session -ArgumentList $staged -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
}

function Invoke-UpdatePhase {
    if (-not $PreviousInstallerPath) {
        return (New-UpdateFailureRecord 'The previous release''s Setup.exe was not given (-PreviousInstallerPath), so the update over a running previous release was not proven. A full run needs it; nothing waives it.')
    }
    $prevVersion = $PreviousVersion
    if (-not $prevVersion -and (Split-Path $PreviousInstallerPath -Leaf) -match '(\d+\.\d+\.\d+(?:-rc[\.\d]*)?)') { $prevVersion = $Matches[1] }
    if (-not $prevVersion) {
        return (New-UpdateFailureRecord 'The previous release''s version is not known (-PreviousVersion), so the install step could not check what it installed.')
    }
    $updateDir = Join-Path $OutDirectory 'update-phase'
    New-Item -ItemType Directory -Force -Path $updateDir | Out-Null
    try {
        Write-Host "Update phase: restoring the clean checkpoint and installing the previous release ($prevVersion) silently..." -ForegroundColor Cyan
        $updateHook = {
            param($rigSession, $name, $cred)
            Send-BundleToVm -Session $rigSession -Name $name -Credential $cred -Bundle $bundle
            Invoke-InVmVia -Session $rigSession -Name $name -Credential $cred -Block {
                New-Item -Path 'HKCU:\Software\Microsoft\ServerManager' -Force | Out-Null
                Set-ItemProperty -Path 'HKCU:\Software\Microsoft\ServerManager' -Name 'DoNotOpenServerManagerAtLogon' -Value 1 -Type DWord
                Get-Process ServerManager -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            } | Out-Null
        }
        $updateHarnessFile = Join-Path $updateDir ("golden-path-{0}-{1}.md" -f $prevVersion, (Get-Date -Format 'yyyy-MM-dd'))
        # Silent (not -Interactive): Setup --silent, then Deluno started in the signed-in desktop session, as the harness always does.
        & (Join-Path $PSScriptRoot 'Invoke-GoldenPath.ps1') -RigHost $RigHost -InstallerPath $PreviousInstallerPath -Version $prevVersion -VmName $VmName -Checkpoint $Checkpoint `
            -RigCredentialFile $RigCredentialFile -VmCredentialFile $VmCredentialFile -OutDirectory $updateDir -AfterVmReady $updateHook
        $rows = @(Read-HarnessRows -File $updateHarnessFile)
        $bad = @($rows | Where-Object { $_.Status -eq 'failed' } | ForEach-Object { $_.Step })
        if ($rows.Count -eq 0) { return (New-UpdateFailureRecord "The previous release's install wrote no results file at $updateHarnessFile.") }
        if ($bad.Count -gt 0) { return (New-UpdateFailureRecord "The previous release did not install and start cleanly: $($bad -join '; ').") }

        $rigU = New-PSSession -ComputerName $RigHost -Credential (Import-Clixml $RigCredentialFile)
        try {
            $vmCredU = Import-Clixml $VmCredentialFile
            Copy-FileToVm -Session $rigU -LocalPath $InstallerPath -VmFolder 'C:\golden\candidate' -VmName2 $VmName -Cred $vmCredU
            $runArgs2 = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\golden\suite\vm\Run-Update.ps1" -Root "C:\golden\update" -CandidateSetup "C:\golden\candidate\Deluno-Setup.exe" -CandidateVersion "' + $Version + '"'
            Invoke-InVmVia -Session $rigU -Name $VmName -Credential $vmCredU -ArgumentList @('GoldenUpdateRun', $runArgs2) -Block $startTaskBlock | Out-Null
            Write-Host 'Update phase: running in the VM (the previous release gets state, then the candidate''s Setup runs over it)...' -ForegroundColor Cyan
            $deadline = (Get-Date).AddMinutes(30)
            $done = $null
            while ((Get-Date) -lt $deadline -and $null -eq $done) {
                Start-Sleep -Seconds 15
                $done = Invoke-InVmVia -Session $rigU -Name $VmName -Credential $vmCredU -Block { $f = 'C:\golden\update\results\done.flag'; if (Test-Path $f) { (Get-Content -Raw $f).Trim() } else { $null } }
            }
            $json = Invoke-InVmVia -Session $rigU -Name $VmName -Credential $vmCredU -Block { $f = 'C:\golden\update\results\results.json'; if (Test-Path $f) { Get-Content -Raw $f } else { $null } }
            # Deluno's logs for this phase too.
            try {
                $sum = Invoke-InVmVia -Session $rigU -Name $VmName -Credential $vmCredU -Block { & 'C:\golden\suite\vm\Collect-Logs.ps1' -Root 'C:\golden\update' }
                Write-Host "Update phase logs: $sum"
                $st = Invoke-Command -Session $rigU -ScriptBlock { $d = Join-Path $env:TEMP ('golden-uplogs-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null; $d }
                try {
                    Invoke-Command -Session $rigU -ArgumentList $st, $VmName, $vmCredU -ScriptBlock {
                        param($dir, $n, $cred)
                        $s = New-PSSession -VMName $n -Credential $cred
                        try { Copy-Item -FromSession $s -Path 'C:\golden\update\collect.zip' -Destination $dir } finally { Remove-PSSession $s }
                    }
                    Copy-Item -FromSession $rigU -Path (Join-Path $st 'collect.zip') -Destination (Join-Path $updateDir 'vm-logs.zip') -Force
                    Expand-Archive -Path (Join-Path $updateDir 'vm-logs.zip') -DestinationPath (Join-Path $updateDir 'vm-logs') -Force
                }
                finally { Invoke-Command -Session $rigU -ArgumentList $st -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
            }
            catch { [void]$warnings.Add("Collecting the update phase's logs failed: $($_.Exception.Message)") }
            if ($null -eq $done) { return (New-UpdateFailureRecord 'The update phase did not finish within 30 minutes.') }
            $parsed = $null
            if ($json) { try { $parsed = $json | ConvertFrom-Json } catch { } }
            $rec = @($parsed.scenarios) | Where-Object { $_.id -eq $updateId } | Select-Object -First 1
            if (-not $rec) { return (New-UpdateFailureRecord "The update phase reported no result (it ended: $done).") }
            return $rec
        }
        finally { Remove-PSSession $rigU -ErrorAction SilentlyContinue }
    }
    catch { return (New-UpdateFailureRecord "The update phase stopped: $($_.Exception.Message)") }
}

if ($Groups.Count -eq 0 -or $Groups -contains 'update') {
    $reported = @($reported) + @(Invoke-UpdatePhase)
}

# ------------------------------------------------------------------ results and the status

$fullRun = ($Groups.Count -eq 0)
$suiteDidNotRun = ($harnessFailures.Count -gt 0 -or -not $buildMatches)
$merged = @(Merge-SuiteResults -Plan $plan -Reported $reported -Groups $Groups)
if ($suiteDidNotRun) {
    # Nothing ran: every planned scenario is an error with the reason, so the status names them.
    $merged = @($merged | ForEach-Object { $_.Error = $runnerNote; $_ })
}
$verdict = Get-SuiteVerdict -Merged $merged -HarnessFailures $harnessFailures -BuildMatches $buildMatches -FullRun $fullRun
if ($runnerNote -and $verdict.State -eq 'success') { $verdict.State = 'failure' }

$statusArgs = Get-StatusArguments -Repository $Repository -Sha $fullSha -Verdict $verdict -Version $Version -TargetUrl $ResultsUrl
$recorded = 'not recorded'
if ($DryRun) { $recorded = "would record: gh $($statusArgs -join ' ')" }
elseif (-not $fullRun) { $recorded = 'not recorded (a partial run records nothing)' }

$minutes = $clock.Elapsed.TotalMinutes
$md = New-ResultsMarkdown -Verdict $verdict -Merged $merged -HarnessRows $harnessRows -Version $Version -Commit $fullSha -InstallerSha256 $installerSha `
    -InstalledBuild $installedBuild -Minutes $minutes -DryRun $DryRun.IsPresent -RecordedStatus $recorded -Warnings @($warnings)
$resultsMd = Join-Path $OutDirectory 'scenario-suite-results.md'
Set-Content -Path $resultsMd -Value $md -Encoding utf8
([ordered]@{ version = $Version; commit = $fullSha; installedBuild = $installedBuild; state = $verdict.State; passed = $verdict.Passed; total = $verdict.Total; failed = $verdict.Failed; minutes = [math]::Round($minutes, 1); scenarios = $merged } |
    ConvertTo-Json -Depth 10) | Set-Content -Path (Join-Path $OutDirectory 'scenario-suite-results.json') -Encoding utf8

Write-Host ''
Write-Host ("Verdict: {0}. {1} of {2} scenarios passed in {3:N1} minutes." -f $verdict.State.ToUpperInvariant(), $verdict.Passed, $verdict.Total, $minutes) -ForegroundColor $(if ($verdict.State -eq 'success') { 'Green' } else { 'Red' })
if ($verdict.Failed.Count -gt 0) { Write-Host ("Failed or not reported: " + ($verdict.Failed -join ', ')) -ForegroundColor Red }
Write-Host "Results: $resultsMd"

if ($DryRun) { Write-Host "Dry run: $recorded" -ForegroundColor Yellow }
elseif ($fullRun) {
    $out = & gh @statusArgs 2>&1
    if ($LASTEXITCODE -ne 0) { Write-Host "Recording the status failed: $out" -ForegroundColor Red; exit 2 }
    Write-Host "Recorded golden-path=$($verdict.State) on $fullSha." -ForegroundColor Green
}
if ($verdict.State -eq 'success') { exit 0 } else { exit 1 }
