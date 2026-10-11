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
    Run only these groups (install, films, removal, resend, stuck, outage, tryagain, vanish, problems). A partial run never records a pass.

.PARAMETER SkipInstall
    Use the VM as it is (already installed, for example after an earlier run); skips the checkpoint restore and the picker.

.PARAMETER ResultsUrl
    Optional target_url for the status (where the results file can be read).

.EXAMPLE
    ./scripts/golden-path/Invoke-ScenarioSuite.ps1 -RigHost <host> -InstallerPath .\Deluno-rc-Setup.exe -Version 1.0.0-rc.30.21 -Commit <sha>
#>
[CmdletBinding()]
param(
    [string] $RigHost,
    [string] $InstallerPath,
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
    [switch] $SkipInstall
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
if (-not (Test-Path $InstallerPath)) { throw "Installer not found: $InstallerPath" }
$InstallerPath = (Resolve-Path $InstallerPath).Path
$installerSha = (Get-FileHash $InstallerPath -Algorithm SHA256).Hash

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
