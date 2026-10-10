<#
.SYNOPSIS
    Runs the scriptable part of the golden path (docs/golden-path.md, step 1 and the facts steps 2 and 3 rest on) on the clean Hyper-V VM.

.DESCRIPTION
    Restores the VM's "clean" checkpoint, starts it, copies the installer in, installs it silently and
    checks what can be checked without a person: Deluno starts and answers, the first-run state, which
    bundled components got installed (and their versions) and where their data folders are.

    It writes golden-path-<version>-<date>.md under artifacts/ with one line per step: passed, failed
    or manual (with what the person must look at). It leaves the VM running for the manual steps.

    Access is PowerShell remoting to the Hyper-V host (the rig), then PowerShell Direct into the VM.
    It uses only the two DPAPI credential files below and never reads, prints or stores a password.
    It changes no Windows security, firewall, network or TrustedHosts setting, and touches nothing on
    the host except this one VM (restore its checkpoint, start it, copy a file in, and delete the
    temporary copy of the installer it staged on the host).

    The component picker (the tick boxes) is GUI-only: Setup.exe and the tray take no component choice
    on the command line (apps/windows-tray/InstallHooks.cs shows the picker from first run, nothing
    else reads the choice). So a silent install installs Deluno alone and the five clients plus Weir
    stay a manual step. This script reports that rather than faking it.

.PARAMETER RigHost
    The Hyper-V host that holds the VM. Never committed; pass it on the command line.

.PARAMETER InstallerPath
    A local Deluno Setup.exe.

.PARAMETER Version
    The version being tested, for example 1.0.0-rc.30.4. Used in the results file name and compared
    with the installed build.

.PARAMETER VmName
    Defaults to Deluno-GoldenPath.

.PARAMETER Checkpoint
    Defaults to clean.

.PARAMETER RigCredentialFile
    DPAPI file for the host. Defaults to $env:LOCALAPPDATA\Deluno-rig-admin.xml.

.PARAMETER VmCredentialFile
    DPAPI file for the VM. Defaults to $env:LOCALAPPDATA\Deluno-goldenvm-admin.xml.

.PARAMETER OutDirectory
    Where the results file goes. Defaults to artifacts/ in the repository (gitignored).

.PARAMETER PickerWaitSeconds
    How long to wait after the silent install for first-run work (the picker, the firewall step) to
    finish or show itself, before the checks.

.EXAMPLE
    ./scripts/golden-path/Invoke-GoldenPath.ps1 -RigHost <host> -InstallerPath .\artifacts\windows\velopack\Deluno-rc-Setup.exe -Version 1.0.0-rc.30.4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RigHost,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [string] $Version,
    [string] $VmName = 'Deluno-GoldenPath',
    [string] $Checkpoint = 'clean',
    [string] $RigCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-rig-admin.xml'),
    [string] $VmCredentialFile = (Join-Path $env:LOCALAPPDATA 'Deluno-goldenvm-admin.xml'),
    [string] $OutDirectory,
    [int] $PickerWaitSeconds = 60,
    # Install the way a person does: Setup.exe in the signed-in desktop session, so first run shows the component
    # picker, which Invoke-ComponentPicker.ps1 then ticks. Screens are saved under <OutDirectory>\golden-screens.
    [switch] $Interactive,
    # Serve one Usenet film from inside the VM (docs/golden-path.md, step 6): a stub news server and a stub Newznab
    # indexer from scripts/golden-path/usenet-stub, started in the signed-in desktop session after the install.
    # The VM has no news server and no Usenet account, and none is ever used.
    [switch] $UsenetStub,
    [int] $UsenetStubNntpPort = 1119,
    [int] $UsenetStubNewznabPort = 1180,
    # The run's throwaway Deluno login. With it the script points Deluno at the stub through Deluno's own API;
    # without it the script prints the one command that does, to run once the account exists and a Usenet client is connected.
    [string] $DelunoUsername,
    [string] $DelunoPassword
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot 'artifacts' }
if (-not (Test-Path $InstallerPath)) { throw "Installer not found: $InstallerPath" }
$InstallerPath = (Resolve-Path $InstallerPath).Path

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string] $Step, [ValidateSet('passed', 'failed', 'manual')] [string] $Status, [string] $Detail) {
    $results.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail })
    $colour = switch ($Status) { 'passed' { 'Green' } 'failed' { 'Red' } default { 'Yellow' } }
    Write-Host ("[{0,-6}] {1} - {2}" -f $Status, $Step, $Detail) -ForegroundColor $colour
}

$rigCred = Import-Clixml $RigCredentialFile
$vmCred = Import-Clixml $VmCredentialFile
$rig = $null
$stagedOnHost = $null

# Runs a script block inside the VM (PowerShell Direct, through the host session). The block comes back
# as text and is rebuilt on the host, so it must not capture local variables: pass them in -ArgumentList.
function Invoke-InVm([scriptblock] $Block, [object[]] $ArgumentList = @()) {
    Invoke-Command -Session $rig -ArgumentList $Block.ToString(), $ArgumentList, $VmName, $vmCred -ScriptBlock {
        param($text, $vmArgs, $name, $cred)
        $sb = [scriptblock]::Create($text)
        Invoke-Command -VMName $name -Credential $cred -ArgumentList $vmArgs -ScriptBlock $sb
    }
}

# Saves the VM's screen now, under <OutDirectory>\golden-screens\<name>.png (through the host; see Get-VmScreen.ps1).
function Save-VmScreen([string] $Name) {
    try {
        $hostPath = Invoke-Command -Session $rig -ArgumentList (Get-Content -Raw (Join-Path $PSScriptRoot 'Get-VmScreen.ps1')), $VmName, $Name -ScriptBlock {
            param($scriptText, $vm, $name)
            $file = Join-Path $env:TEMP "golden-screen-$name.png"
            $kb = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_Keyboard |
                Where-Object { $_.SystemName -eq (Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$vm'").Name }
            if ($kb) { $null = Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = [uint32]0x11 } }  # wake the display with Ctrl pressed once: it does nothing on its own. F15 (0x7E) did not wake it, every picker screen came back black (rc.30.8 run); Shift five times opens the Sticky Keys prompt
            Start-Sleep -Milliseconds 800
            & ([scriptblock]::Create($scriptText)) -VmName $vm -OutPath $file -Width 1024 -Height 768 | Out-Null
            $file
        }
        $dir = Join-Path $OutDirectory 'golden-screens'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item -Path $hostPath -Destination (Join-Path $dir "$Name.png") -FromSession $rig -Force
        Invoke-Command -Session $rig -ArgumentList $hostPath -ScriptBlock { param($p) Remove-Item -LiteralPath $p -ErrorAction SilentlyContinue }
    } catch { Write-Host "screen '$Name' not saved: $($_.Exception.Message)" -ForegroundColor Yellow }
}

# Installs the way a person does (#1158): Setup.exe without --silent in the signed-in desktop session, so Velopack
# starts Deluno's first run, which shows the component picker; Invoke-ComponentPicker.ps1 then ticks every component
# in the same session. The Usenet login is a throwaway value made inside the VM for this run.
function Invoke-InteractiveInstall([string] $SetupName) {
    $pickerText = Get-Content -Raw (Join-Path $PSScriptRoot 'Invoke-ComponentPicker.ps1')
    $started = Invoke-InVm {
        param($file, $pickerText)
        Set-Content -Path 'C:\golden\Invoke-ComponentPicker.ps1' -Value $pickerText -Encoding utf8
        Remove-Item 'C:\golden\picker.log' -ErrorAction SilentlyContinue
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $password = 'Gp-' + [guid]::NewGuid().ToString('N').Substring(0, 16)
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $setup = New-ScheduledTaskAction -Execute (Join-Path 'C:\golden' $file)
        $picker = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File C:\golden\Invoke-ComponentPicker.ps1 -UsenetUser deluno -UsenetPassword $password"
        Register-ScheduledTask -TaskName 'GoldenPathSetup' -Action $setup -Principal $principal -Force | Out-Null
        Register-ScheduledTask -TaskName 'GoldenPathPicker' -Action $picker -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName 'GoldenPathPicker'
        Start-ScheduledTask -TaskName 'GoldenPathSetup'
        'started'
    } @($SetupName, $pickerText)
    Add-Result 'Setup.exe started in the desktop session' $(if ($started -eq 'started') { 'passed' } else { 'failed' }) 'Not silent: first run shows the picker.'

    $deadline = (Get-Date).AddMinutes(50)
    $shot = 0
    $log = ''
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 20
        $log = Invoke-InVm { if (Test-Path 'C:\golden\picker.log') { Get-Content -Raw 'C:\golden\picker.log' } else { '' } }
        if ($shot -lt 40 -and ($shot -lt 6 -or $shot % 3 -eq 0)) { Save-VmScreen ('picker-{0:d2}' -f $shot) }
        $shot++
        if ($log -match '(?m) done\s*$' -or $log -match 'FAILED') { break }
    }
    Save-VmScreen 'picker-final'
    Invoke-InVm { Unregister-ScheduledTask -TaskName 'GoldenPathSetup' -Confirm:$false -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName 'GoldenPathPicker' -Confirm:$false -ErrorAction SilentlyContinue }
    $logFile = Join-Path $OutDirectory "golden-picker-$Version.log"
    Set-Content -Path $logFile -Value $log -Encoding utf8
    # "done" alone proves nothing (a window that closed by itself also ends the picker script): the finish screen must have been seen.
    if ($log -match '(?m) done\s*$' -and $log -notmatch 'FAILED' -and $log -match 'final: .*Finished\.') {
        Add-Result 'Component picker ticked and installed' 'passed' "Every row ticked and installed; what it said is in $logFile."
    } else {
        Add-Result 'Component picker ticked and installed' 'failed' "See $logFile and golden-screens\picker-*.png."
    }
}

# Starts the Usenet stub in the VM and, when the run's Deluno login is known, points Deluno at it (scripts/golden-path/usenet-stub).
# Needs the install done: the release's test video is made with the ffmpeg Deluno ships.
function Start-UsenetStubInVm {
    $stubDir = Join-Path $PSScriptRoot 'usenet-stub'
    $files = @('UsenetStub.Lib.ps1', 'New-UsenetRelease.ps1', 'Start-NntpStub.ps1', 'Start-NewznabStub.ps1', 'Set-UsenetStubInDeluno.ps1') |
        ForEach-Object { , @($_, (Get-Content -Raw (Join-Path $stubDir $_))) }
    $made = Invoke-InVm {
        param($pairs, $nntpPort, $newznabPort)
        $dir = 'C:\golden\usenet-stub'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        foreach ($p in $pairs) { Set-Content -Path (Join-Path $dir $p[0]) -Value $p[1] -Encoding utf8 }
        $release = Join-Path $dir 'release'
        $info = & (Join-Path $dir 'New-UsenetRelease.ps1') -OutDirectory $release
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $tasks = @(
            @('GoldenPathUsenetNntp', 'Start-NntpStub.ps1', $nntpPort),
            @('GoldenPathUsenetNewznab', 'Start-NewznabStub.ps1', $newznabPort)
        )
        foreach ($task in $tasks) {
            $argLine = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $dir $task[1]) + '" -Root "' + $release + '" -Port ' + $task[2]
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argLine
            Register-ScheduledTask -TaskName $task[0] -Action $action -Principal $principal -Settings $settings -Force | Out-Null
            Start-ScheduledTask -TaskName $task[0]
        }
        # Wait for both to answer: the news server with its greeting, the indexer with its capabilities.
        $nntp = 'no answer'; $newznab = 'no answer'
        for ($i = 0; $i -lt 40 -and ($nntp -notlike '200*' -or $newznab -notlike '*<caps>*'); $i++) {
            Start-Sleep -Seconds 2
            try {
                $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $nntpPort)
                $reader = New-Object System.IO.StreamReader($c.GetStream())
                $nntp = $reader.ReadLine(); $c.Close()
            } catch { }
            try { $newznab = (Invoke-WebRequest "http://127.0.0.1:$newznabPort/api?t=caps" -UseBasicParsing -TimeoutSec 5).Content } catch { }
        }
        [pscustomobject]@{ Name = $info.name; SizeMb = [math]::Round($info.sizeBytes / 1MB, 1); Parts = $info.parts; Nntp = $nntp; NewznabOk = ($newznab -like '*<caps>*') }
    } @($files, $UsenetStubNntpPort, $UsenetStubNewznabPort)
    $ok = ($made.Nntp -like '200*') -and $made.NewznabOk
    Add-Result 'Usenet stub running in the VM (news server and Newznab indexer)' $(if ($ok) { 'passed' } else { 'failed' }) ("Release $($made.Name), $($made.SizeMb) MB in $($made.Parts) parts. News server on 127.0.0.1:$UsenetStubNntpPort answered '$($made.Nntp)'; indexer on http://127.0.0.1:$UsenetStubNewznabPort/ answered caps: $($made.NewznabOk).")
    if (-not $ok) { return }

    $settingsText = "In Deluno set: a Usenet provider at 127.0.0.1 port $UsenetStubNntpPort (no SSL, any dummy login, 2 connections), and a Newznab indexer at http://127.0.0.1:$UsenetStubNewznabPort with any API key. Then add Big Buck Bunny (2008) and search."
    if ($DelunoUsername -and $DelunoPassword) {
        try {
            $wired = Invoke-InVm {
                param($user, $pass, $nntpPort, $newznabPort)
                (& 'C:\golden\usenet-stub\Set-UsenetStubInDeluno.ps1' -Username $user -Password $pass -NntpPort $nntpPort -NewznabPort $newznabPort *>&1 | Out-String).Trim()
            } @($DelunoUsername, $DelunoPassword, $UsenetStubNntpPort, $UsenetStubNewznabPort)
            Add-Result 'Deluno pointed at the Usenet stub through its API' 'passed' $wired
        } catch {
            Add-Result 'Deluno pointed at the Usenet stub through its API' 'failed' "$($_.Exception.Message) Once the account exists and a Usenet client is connected, run C:\golden\usenet-stub\Set-UsenetStubInDeluno.ps1 -Username <login> -Password <password> in the VM. $settingsText"
        }
    }
    else {
        Add-Result 'Deluno pointed at the Usenet stub through its API' 'manual' "No -DelunoUsername/-DelunoPassword given. After the first account exists and SABnzbd or NZBGet is connected, run in the VM: C:\golden\usenet-stub\Set-UsenetStubInDeluno.ps1 -Username <login> -Password <password> (a PowerShell prompt, it uses only Deluno's own API). $settingsText"
    }
}

try {
    $rig = New-PSSession -ComputerName $RigHost -Credential $rigCred

    # 1. Restore the clean checkpoint and start the VM.
    try {
        Invoke-Command -Session $rig -ArgumentList $VmName, $Checkpoint -ScriptBlock {
            param($name, $cp)
            if ((Get-VM -Name $name).State -ne 'Off') { Stop-VM -Name $name -TurnOff -Force }
            Restore-VMSnapshot -VMName $name -Name $cp -Confirm:$false
            Start-VM -Name $name
        }
        Add-Result 'Checkpoint restored, VM started' 'passed' "'$Checkpoint' restored on $VmName."
    } catch {
        Add-Result 'Checkpoint restored, VM started' 'failed' $_.Exception.Message
        throw
    }

    # 2. Wait for PowerShell Direct.
    $deadline = (Get-Date).AddMinutes(15)
    $ready = $false
    while ((Get-Date) -lt $deadline -and -not $ready) {
        try { $null = Invoke-InVm { $env:COMPUTERNAME }; $ready = $true } catch { $lastErr = $_.Exception.Message; Start-Sleep -Seconds 5 }
    }
    if (-not $ready) { Add-Result 'VM answers over PowerShell Direct' 'failed' "No answer within 15 minutes (last error: $lastErr)."; throw 'VM did not come up.' }
    $os = Invoke-InVm { (Get-CimInstance Win32_OperatingSystem).Caption }
    Add-Result 'VM answers over PowerShell Direct' 'passed' $os

    # 3. Copy the installer in: local -> host temp -> VM (Copy-Item over a PowerShell Direct session).
    try {
        $stagedOnHost = Invoke-Command -Session $rig -ScriptBlock {
            $d = Join-Path $env:TEMP ('golden-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $d | Out-Null
            $d
        }
        Copy-Item -Path $InstallerPath -Destination $stagedOnHost -ToSession $rig
        $hash = (Get-FileHash $InstallerPath -Algorithm SHA256).Hash
        $inVm = Invoke-Command -Session $rig -ArgumentList $stagedOnHost, (Split-Path $InstallerPath -Leaf), $VmName, $vmCred -ScriptBlock {
            param($dir, $file, $name, $cred)
            $s = New-PSSession -VMName $name -Credential $cred
            try {
                Invoke-Command -Session $s -ScriptBlock { New-Item -ItemType Directory -Force -Path 'C:\golden' | Out-Null }
                Copy-Item -Path (Join-Path $dir $file) -Destination 'C:\golden\' -ToSession $s
                Invoke-Command -Session $s -ArgumentList $file -ScriptBlock { param($f) (Get-FileHash (Join-Path 'C:\golden' $f) -Algorithm SHA256).Hash }
            } finally { Remove-PSSession $s }
        }
        if ($inVm -ne $hash) { throw "Hash mismatch after copy (local $hash, VM $inVm)." }
        Add-Result 'Installer copied into the VM' 'passed' "SHA256 $hash matches."
    } catch {
        Add-Result 'Installer copied into the VM' 'failed' $_.Exception.Message
        throw
    }
    $setupName = Split-Path $InstallerPath -Leaf

    if ($Interactive) {
        # The picker finds NZBGet's and Weir's latest releases through GitHub's anonymous API (60 calls an hour for
        # each public address, shared by everything behind it). When that is used up the picker fails them with
        # "Could not find ... latest release on GitHub" (rc.30.8 run), so say so before spending a run on it.
        $budget = Invoke-InVm {
            try { $c = (Invoke-RestMethod 'https://api.github.com/rate_limit' -UseBasicParsing -TimeoutSec 20).resources.core; "$($c.remaining)|$([DateTimeOffset]::FromUnixTimeSeconds($c.reset).ToLocalTime().ToString('HH:mm'))" } catch { "unknown|$($_.Exception.Message)" }
        }
        $left, $resets = ($budget -split '\|', 2)
        if ($left -match '^\d+$' -and [int]$left -lt 20) {
            # Not a reason to stop any more: the picker finds Weir through GitHub's release feed when the limit is used up (#1181),
            # which is a case the run has to be able to meet. Say so in the record and go on; the picker's own words show what it did.
            Add-Result 'GitHub anonymous API budget (the picker needs about 6 calls)' 'manual' "Only $left calls left until $resets (VM clock). Going on: the picker must still install Weir through the release feed, or say plainly that GitHub's limit is reached, with a time (#1181). Record what it said."
        }
        else {
            Add-Result 'GitHub anonymous API budget (the picker needs about 6 calls)' 'passed' "$left calls left (resets $resets)."
        }
        Invoke-InteractiveInstall -SetupName $setupName
    } else {
    # 4. Silent install. Velopack's Setup takes --silent; the component picker is a first-run dialog
    #    and cannot be driven from here.
    $install = Invoke-InVm {
        param($file)
        $setup = Join-Path 'C:\golden' $file
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $setup -ArgumentList '--silent' -PassThru
        $finished = $p.WaitForExit(600000)
        [pscustomobject]@{ Finished = $finished; ExitCode = if ($finished) { $p.ExitCode } else { $null }; Seconds = [int]$sw.Elapsed.TotalSeconds }
    } @($setupName)
    if ($install.Finished -and $install.ExitCode -eq 0) {
        Add-Result 'Silent install (Setup.exe --silent)' 'passed' "Exit 0 after $($install.Seconds)s."
    } else {
        Add-Result 'Silent install (Setup.exe --silent)' 'failed' "Finished=$($install.Finished) exit=$($install.ExitCode) after $($install.Seconds)s."
    }


    # A silent install does not start Deluno, and first run (firewall step, component picker) happens on
    # first start. Start it the way a person would, in the VM's signed-in desktop session, through a
    # one-shot scheduled task that is removed again straight after. PowerShell Direct's own session has
    # no desktop, so the picker could never show there.
    $launch = Invoke-InVm {
        $exe = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Deluno\current') -Filter 'Deluno.exe' | Select-Object -First 1
        if (-not $exe) { return 'Deluno.exe not found.' }
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $action = New-ScheduledTaskAction -Execute $exe.FullName -WorkingDirectory $exe.DirectoryName
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName 'GoldenPathLaunch' -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName 'GoldenPathLaunch'
        Start-Sleep -Seconds 5
        Unregister-ScheduledTask -TaskName 'GoldenPathLaunch' -Confirm:$false
        'started in the signed-in desktop session'
    }
    Add-Result 'Deluno started in the desktop session' $(if ($launch -like 'started*') { 'passed' } else { 'failed' }) $launch

    # First run (firewall step, then the picker) starts after Setup; give it time to settle or show itself.
    Start-Sleep -Seconds $PickerWaitSeconds
    }

    # 5. What is installed and running.
    $state = Invoke-InVm {
        param($port)
        $root = Join-Path $env:LOCALAPPDATA 'Deluno'
        $exe = Get-ChildItem -Path $root -Recurse -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        $procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Deluno*' -and $_.ProcessName -ne 'Deluno.Setup' } | Select-Object -ExpandProperty ProcessName)
        $http = $null; $body = $null
        for ($i = 0; $i -lt 20 -and -not $http; $i++) {
            try {
                $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/auth/bootstrap-status" -UseBasicParsing -TimeoutSec 5
                $http = [int]$r.StatusCode; $body = $r.Content
            } catch {
                $resp = $_.Exception.Response
                if ($resp) { $http = [int]$resp.StatusCode; $body = $_.ErrorDetails.Message } else { Start-Sleep -Seconds 3 }
            }
        }
        $listen = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 7879, 8081, 9091, 8112, 8080, 6789, 9347 } |
            ForEach-Object { "$($_.LocalAddress):$($_.LocalPort) pid $($_.OwningProcess) $((Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName)" })
        $hookLog = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $hook = if (Test-Path $hookLog) { @(Get-Content $hookLog -Tail 20) } else { @() }
        $uninstall = @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -match 'Deluno|qBittorrent|Transmission|Deluge|SABnzbd|NZBGet|Weir' } |
            ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" })
        $data = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $components = if (Test-Path (Join-Path $data 'components')) { @(Get-ChildItem (Join-Path $data 'components') -Directory | Select-Object -ExpandProperty FullName) } else { @() }
        $clientFolders = @('C:\Downloads\Completed', 'C:\Downloads\Completed\Movies', 'C:\Downloads\Completed\TV', 'C:\Downloads\Incomplete') | ForEach-Object { "$_ : $(Test-Path $_)" }
        $configs = @(
            "$env:APPDATA\qBittorrent\qBittorrent.ini", "$env:LOCALAPPDATA\transmission", "$env:APPDATA\transmission",
            "$env:APPDATA\deluge", "$env:LOCALAPPDATA\sabnzbd", "$env:APPDATA\NZBGet", "$env:ProgramData\NZBGet", "$env:ProgramData\Weir", "$env:LOCALAPPDATA\Weir"
        ) | Where-Object { Test-Path $_ }
        # Weir's installed version, and the installer the picker ran: the tray logs its hash, since the file is deleted
        # after the run. Without that line the newest Weir setup left in the temp folder is hashed, if there is one.
        $weirExe = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Weir') -Recurse -Filter 'Weir.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        $weirServer = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Weir') -Recurse -Filter 'WeirServer.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        $weirLog = if (Test-Path $hookLog) { @(Get-Content $hookLog | Where-Object { $_ -match 'Weir: installer .* sha256 [0-9A-Fa-f]{64}' } | Select-Object -Last 1) } else { @() }
        $weirTemp = Get-ChildItem -Path (Join-Path $env:TEMP 'Deluno-components') -Filter '*Weir*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $weirTempHash = if ($weirTemp) { (Get-FileHash $weirTemp.FullName -Algorithm SHA256).Hash } else { $null }
        $dataTop = if (Test-Path $data) { @(Get-ChildItem $data -Force | Select-Object -ExpandProperty Name) } else { @() }
        [pscustomobject]@{
            Exe = $exe.FullName; FileVersion = $exe.VersionInfo.ProductVersion; Processes = $procs; Http = $http; Body = $body
            Listening = $listen; Hook = $hook; Uninstall = $uninstall; DataRoot = $data; DataTop = $dataTop
            Components = $components; ClientFolders = $clientFolders; ConfigsFound = $configs
            WeirVersion = $weirExe.VersionInfo.ProductVersion; WeirServerVersion = $weirServer.VersionInfo.ProductVersion
            WeirInstallerLog = ($weirLog -join ''); WeirTempInstallerHash = $weirTempHash
        }
    } @(7879)

    if ($state.Exe) {
        $isBuild = ($state.FileVersion -and ($state.FileVersion -like "*$Version*"))
        if ($isBuild) { Add-Result 'Installed build is the one under test' 'passed' "Deluno.exe reports $($state.FileVersion)." }
        else { Add-Result 'Installed build is the one under test' 'failed' "Deluno.exe reports '$($state.FileVersion)', expected $Version." }
    } else {
        Add-Result 'Installed build is the one under test' 'failed' 'Deluno.exe not found under the install folder.'
    }

    Add-Result 'Deluno process running' $(if ($state.Processes.Count -gt 0) { 'passed' } else { 'failed' }) $(if ($state.Processes.Count -gt 0) { ($state.Processes -join ', ') } else { 'No Deluno process after install.' })

    if ($state.Http -eq 200) {
        Add-Result 'Deluno answers on its port' 'passed' "GET /api/auth/bootstrap-status on port 7879 returned 200."
        Add-Result 'First-run state' 'passed' "bootstrap-status says: $($state.Body)"
    } else {
        Add-Result 'Deluno answers on its port' 'failed' "GET /api/auth/bootstrap-status on port 7879 returned '$($state.Http)'."
        Add-Result 'First-run state' 'failed' 'No answer, so the first-run state could not be read.'
    }

    Add-Result 'Listening ports (Deluno 7879, clients 8081/9091/8112/8080/6789, Weir 9347)' 'passed' $(if ($state.Listening.Count) { $state.Listening -join '; ' } else { 'none of them is listening' })

    # Components: the picker cannot be driven, so the expected result of a silent install is none.
    $names = 'qBittorrent', 'Transmission', 'Deluge', 'SABnzbd', 'NZBGet', 'Weir'
    $found = foreach ($n in $names) { $state.Uninstall | Where-Object { $_ -like "*$n*" } }
    if ($found) {
        Add-Result 'Bundled components installed (versions)' 'passed' ($found -join '; ')
    } else {
        Add-Result 'Bundled components installed (versions)' 'manual' 'None installed, as expected after a silent install: Setup.exe and the app take no component choice on the command line, the picker is a first-run dialog. Tick the five clients plus Weir by hand (step 1), then re-run the checks on the VM to read their versions and folders.'
    }
    # The run record names Weir: the version the picker installed and the sha256 of the installer it ran (step 1).
    if ($state.WeirVersion) {
        $weirHash = if ($state.WeirInstallerLog -match 'sha256 ([0-9A-Fa-f]{64})') { "$($Matches[1].ToUpperInvariant()) (from the tray's install log)" }
                    elseif ($state.WeirTempInstallerHash) { "$($state.WeirTempInstallerHash) (hashed from the setup left in the VM's temp folder)" }
                    else { $null }
        if ($weirHash) {
            Add-Result 'Weir named in the run record (version and installer sha256)' 'passed' "Weir.exe $($state.WeirVersion), WeirServer.exe $($state.WeirServerVersion); installer sha256 $weirHash."
        } else {
            Add-Result 'Weir named in the run record (version and installer sha256)' 'failed' "Weir.exe is $($state.WeirVersion) but neither the install log nor the temp folder names the installer's sha256."
        }
    } else {
        Add-Result 'Weir named in the run record (version and installer sha256)' 'manual' 'Weir is not installed yet (a silent install installs Deluno alone); after the picker has run, re-run the checks to read its version and installer sha256.'
    }
    $firstRun = @($state.Hook | Where-Object { $_ -match 'first run' })
    if ($firstRun.Count -gt 0) {
        Add-Result 'First-run hook ran (firewall step, picker)' 'passed' ($firstRun -join ' | ')
    } else {
        Add-Result 'First-run hook ran (firewall step, picker)' 'manual' 'The install hook log has no first-run line: a silent Setup does not trigger it, so the picker and the firewall step were not exercised. Run Setup.exe by hand in the VM console for step 1.'
    }
    Add-Result 'Component data folders' 'passed' ("data root $($state.DataRoot): [" + ($state.DataTop -join ', ') + "]; component folders: [" + ($state.Components -join ', ') + "]; client config found: [" + ($state.ConfigsFound -join ', ') + "]; download folders: " + ($state.ClientFolders -join '; '))
    Add-Result 'Install hook log' 'passed' ($state.Hook -join ' | ')

    if ($UsenetStub) {
        try { Start-UsenetStubInVm }
        catch { Add-Result 'Usenet stub running in the VM (news server and Newznab indexer)' 'failed' $_.Exception.Message }
    }

    # Everything else in docs/golden-path.md needs eyes or a person's clicks.
    $manual = @(
        '1. Picker: tick the five clients plus Weir (exact-SHA Weir build); progress and finish screens show; "open Deluno" is offered; Weir answers on this PC only and its tray starts silently.',
        '2. Clients: Movies and TV categories, the documented folders, Deluge Label plugin on, Library Routing folder check all Matches.',
        '3. Guided setup: every automatic choice asked Yes/No; Connect Weir creates the account and links Movies and TV with the Folder chain all green.',
        '4. Public trackers: three Healthy and kept after Add; routing automatic; 1337x through the Cloudflare helper.',
        '5. Usenet: provider entered once; SABnzbd and NZBGet Healthy only after; one Newznab indexer Healthy (with -UsenetStub: the in-VM stub news server and indexer).',
        '6. Real path: torrent film, Usenet film (with -UsenetStub: Big Buck Bunny from the in-VM stub), TV episode, 4:3 film; lanes in Weir; originals still in the client folder; posters; Activity in both apps.',
        '7. A title''s own profile is honoured: 4K and 720p each targeted correctly.',
        '8. Failure and recovery: stalled torrent replaced; failed hand-off under Needs action and Try again; remove with files and restore.',
        '9. Weir pause and repeats: paused hand-off waits and is processed once on resume; a repeat settles as Skipped: already done.',
        '10. Weir reachable from another computer via Devices on my network (VM network Public).',
        '11. Screens at 1920, 1366 and phone width; Weir Logs clean; no issue numbers or old page names in copy.',
        '12. Pass record: set the golden-path commit status on both commits.'
    )
    foreach ($m in $manual) { Add-Result ($m.Substring(0, $m.IndexOf('.'))) 'manual' $m.Substring($m.IndexOf('.') + 2) }
}
catch {
    Add-Result 'Run aborted' 'failed' $_.Exception.Message
}
finally {
    if ($rig) {
        if ($stagedOnHost) {
            try { Invoke-Command -Session $rig -ArgumentList $stagedOnHost -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
        }
        try {
            $vmState = Invoke-Command -Session $rig -ArgumentList $VmName -ScriptBlock { param($n) (Get-VM -Name $n).State.ToString() }
            Write-Host "VM $VmName is left $vmState for the manual steps."
        } catch { }
        Remove-PSSession $rig -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
    $file = Join-Path $OutDirectory ("golden-path-{0}-{1}.md" -f $Version, (Get-Date -Format 'yyyy-MM-dd'))
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Golden path $Version, $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    $lines.Add('')
    $lines.Add("Installer SHA256: $((Get-FileHash $InstallerPath -Algorithm SHA256).Hash)")
    $lines.Add('')
    $lines.Add('| Step | Result | Detail |')
    $lines.Add('| --- | --- | --- |')
    foreach ($r in $results) {
        $lines.Add("| $($r.Step) | $($r.Status) | $(($r.Detail -replace '\|', '/') -replace '\r?\n', ' ') |")
    }
    $failed = @($results | Where-Object Status -eq 'failed').Count
    $lines.Add('')
    $lines.Add("Scripted steps failed: $failed. Manual lines are still to be done by a person; fill each with what was seen.")
    Set-Content -Path $file -Value $lines -Encoding utf8
    Write-Host "Results: $file"
}
if (@($results | Where-Object Status -eq 'failed').Count -gt 0) { exit 1 }
