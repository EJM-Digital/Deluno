<#
.SYNOPSIS
    Turns a fresh Windows machine into the Deluno simulation rig.

.DESCRIPTION
    The old rig was hand-built, and it showed. Its SABnzbd was registered
    differently from its three neighbours and nobody noticed until the usenet
    half of the end-to-end plan had to run unattended; its SABnzbd configuration
    then vanished and took a GA gate with it; and after six weeks its C:\Deluno
    held forty-eight directories, twenty-eight of them rollback copies. None of
    that was recoverable from anything written down.

    So this. One script, one known-good software set (rig-software.json), and a
    machine anybody can rebuild.

    WHAT THIS DELIBERATELY DOES NOT DO

    It stops before Deluno's first run. Phase 0.5 of the end-to-end plan is
    "a clean install asks to create an account, not to sign in", and phases 1
    to 7 are the first-run experience, the libraries, the profiles and the
    connections. Provisioning those would destroy the first thing the plan
    tests. This leaves Deluno installed, running, and untouched.

.PARAMETER Stage
    Run one stage rather than all of them. Provisioning a machine is a long
    sequence of things that can each fail on their own, and re-running the
    whole thing to retry the last step wastes a lot of time.

.PARAMETER LibraryPath
    Where the media library lives. A UNC path to a share is the interesting
    case and the reason ServiceAccount exists; a local path also works.

.EXAMPLE
    ./scripts/lab/provision-rig.ps1 -ComputerName 192.168.1.150 -Password '...' `
        -ServiceAccount 'deluno' -ServiceAccountPassword '...' `
        -LibraryPath '\\nas\DelunoLab' -NasUser '...' -NasPassword '...'

    ./scripts/lab/provision-rig.ps1 -Stage verify
#>
[CmdletBinding()]
param(
    [string] $ComputerName,
    [string] $UserName,
    [string] $Password,

    # Created on the rig. The services run as this rather than as SYSTEM,
    # because a SYSTEM process authenticates to SMB as the machine account and
    # a workgroup NAS refuses it.
    [string] $ServiceAccount = 'deluno',
    [string] $ServiceAccountPassword,

    [string] $LibraryPath,
    [string] $NasUser,
    [string] $NasPassword,

    [ValidateSet('all', 'preflight', 'stage', 'account', 'folders', 'apps', 'configure', 'services', 'verify')]
    [string] $Stage = 'all'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = Get-Rig
if (-not $ComputerName) { $ComputerName = $rig.host }
if (-not $UserName) { $UserName = $rig.userName }
if (-not $Password) { $Password = $rig.password }

$repoRoot   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# vendor, not artifacts: these are inputs to a build, not output from one, and
# nothing on the developer machine is meant to live outside C:\Projects.
$stagingDir = Join-Path $repoRoot 'vendor\rig-installers'
$software   = Get-Content (Join-Path $PSScriptRoot 'rig-software.json') -Raw | ConvertFrom-Json

$credential = New-Object System.Management.Automation.PSCredential(
    $UserName, (ConvertTo-SecureString $Password -AsPlainText -Force))

$session = $null
function Rig { param([scriptblock] $Body, [object[]] $Args)
    if (-not $script:session) { $script:session = New-PSSession -ComputerName $ComputerName -Credential $credential }
    Invoke-Command -Session $script:session -ScriptBlock $Body -ArgumentList $Args
}

function Head($text) { Write-Host "`n$text" -ForegroundColor Cyan }
function Step($text) { Write-Host "  $text" }
function Warn($text) { Write-Host "  ! $text" -ForegroundColor Yellow }

function Should($name) { return $Stage -eq 'all' -or $Stage -eq $name }

# ------------------------------------------------------------------ preflight

if (Should 'preflight') {
    Head 'Preflight'

    $facts = Rig { [pscustomobject]@{
        Os          = (Get-CimInstance Win32_OperatingSystem).Caption
        Version     = [string](Get-CimInstance Win32_OperatingSystem).Version
        PsEdition   = $PSVersionTable.PSEdition
        PsVersion   = [string]$PSVersionTable.PSVersion
        FreeGB      = [int]((Get-Volume -DriveLetter C).SizeRemaining / 1GB)
        AlreadyHere = (Test-Path 'C:\Deluno\App') -or (Test-Path 'C:\Program Files\qBittorrent')
    } }

    Step "$($facts.Os) $($facts.Version)"
    Step "PowerShell $($facts.PsVersion) ($($facts.PsEdition))"
    Step "$($facts.FreeGB) GB free on C:"
    if ($facts.FreeGB -lt 60) { Warn "Under 60 GB free. Downloads, refined output and the app want room." }
    if ($facts.AlreadyHere) { Warn "This machine already has some of the rig on it. Stages are idempotent, but check you meant this." }

    # The fixtures live on the developer machine, so the rig has to reach it.
    $fixtureHost = $rig.fixtures.host
    $reachable = Rig { param($h, $p) (Test-NetConnection -ComputerName $h -Port $p -WarningAction SilentlyContinue).TcpTestSucceeded } @($fixtureHost, $rig.fixtures.torznabPort)
    if ($reachable) { Step "can reach the torznab fixture on ${fixtureHost}:$($rig.fixtures.torznabPort)" }
    else { Warn "cannot reach ${fixtureHost}:$($rig.fixtures.torznabPort) - start the fixture, or the acquisition phases have no indexer" }
}

# ------------------------------------------------------------------ stage

if (Should 'stage') {
    Head 'Staging the installers'

    New-Item -ItemType Directory -Force $stagingDir | Out-Null
    # Weir's portable zip is read from the sibling repository where its own
    # build already puts it, rather than copied here. It is 190 MB, so a copy
    # would duplicate that for nothing - and it is already inside C:\Projects.
    if (-not (Test-Path $software.weir.source)) {
        throw "Weir's portable zip is not built. Run packaging\windows\build-velopack.ps1 in C:\Projects\Weir."
    }

    $wanted = @(
        @{ Key = 'qbittorrent'; Local = Join-Path $stagingDir $software.qbittorrent.fileName },
        @{ Key = 'sabnzbd';     Local = Join-Path $stagingDir $software.sabnzbd.fileName },
        @{ Key = 'weir';    Local = $software.weir.source }
    )

    foreach ($item in $wanted) {
        $spec = $software.($item.Key)

        if (-not (Test-Path $item.Local)) {
            $where = if ($spec.note) { "$($spec.url)`n  $($spec.note)" } else { $spec.url }
            throw "Missing $([IO.Path]::GetFileName($item.Local)) in $stagingDir.`n  Get it from $where"
        }

        # Before the hash, two cheaper questions: is it plausibly the file at
        # all, and did the vendor sign it? SourceForge answered a scripted
        # download with a 0.1 MB Cloudflare challenge page saved under the
        # installer's name. A hash alone would have pinned that happily on the
        # first run and copied a web page to the rig to be "installed".
        $actualBytes = (Get-Item $item.Local).Length
        if ($spec.minimumBytes -and $actualBytes -lt $spec.minimumBytes) {
            throw "$([IO.Path]::GetFileName($item.Local)) is only $([math]::Round($actualBytes/1MB,1)) MB, under the $([math]::Round($spec.minimumBytes/1MB,1)) MB floor. That is usually a download page saved under the installer's name, not the installer."
        }
        if ($spec.mustBeSigned) {
            $sig = Get-AuthenticodeSignature $item.Local
            if ($sig.Status -ne 'Valid') {
                throw "$([IO.Path]::GetFileName($item.Local)) has no valid signature (Authenticode says $($sig.Status)). Refusing to stage an unsigned installer."
            }
            Step "$([IO.Path]::GetFileName($item.Local)) signed by $(($sig.SignerCertificate.Subject -split ',')[0] -replace '^CN=','')"
        }

        $hash = (Get-FileHash $item.Local -Algorithm SHA256).Hash
        $pinned = $spec.sha256

        if (-not $pinned) {
            # First time: record what we staged, so every rig after this one
            # gets the same bytes rather than whatever the URL serves later.
            $spec.sha256 = $hash
            Step "pinned $([IO.Path]::GetFileName($item.Local)) at $($hash.Substring(0,16))..."
        } elseif ($pinned -ne $hash) {
            throw "$([IO.Path]::GetFileName($item.Local)) does not match the pinned hash.`n  pinned  $pinned`n  staged  $hash"
        } else {
            Step "$([IO.Path]::GetFileName($item.Local)) matches its pinned hash"
        }
    }

    $software | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $PSScriptRoot 'rig-software.json') -Encoding utf8

    Rig { New-Item -ItemType Directory -Force 'C:\Deluno\Setup' | Out-Null }
    foreach ($item in $wanted) {
        Copy-Item -Path $item.Local -Destination 'C:\Deluno\Setup\' -ToSession $script:session -Force
    }
    Step "copied $($wanted.Count) files to C:\Deluno\Setup"
}

# ------------------------------------------------------------------ account

if (Should 'account') {
    Head 'Service account'

    if (-not $ServiceAccountPassword) { throw "-ServiceAccountPassword is required: the scheduled tasks store it, because S4U would run them with no network credentials." }

    Rig {
        param($Account, $AccountPassword, $NasHost, $NasUser, $NasPassword)

        $secure = ConvertTo-SecureString $AccountPassword -AsPlainText -Force
        if (Get-LocalUser -Name $Account -ErrorAction SilentlyContinue) {
            Set-LocalUser -Name $Account -Password $secure -PasswordNeverExpires $true
        } else {
            New-LocalUser -Name $Account -Password $secure -PasswordNeverExpires `
                -Description 'Runs the Deluno rig services' | Out-Null
        }
        # Local admin: qBittorrent and SABnzbd write under Program Files, and the
        # services register themselves. A tighter grant is a later problem.
        Add-LocalGroupMember -Group 'Administrators' -Member $Account -ErrorAction SilentlyContinue

        # "Log on as a service" and "as a batch job" are not grantable through a
        # cmdlet, only through the security database.
        $inf = Join-Path $env:TEMP 'deluno-rights.inf'
        $db  = Join-Path $env:TEMP 'deluno-rights.sdb'
        secedit /export /cfg $inf /areas USER_RIGHTS | Out-Null
        $sid = (Get-LocalUser -Name $Account).SID.Value
        $text = Get-Content $inf
        foreach ($right in 'SeServiceLogonRight', 'SeBatchLogonRight') {
            if ($text -match "^$right") {
                $text = $text -replace "^($right\s*=\s*.*)$", "`$1,*$sid"
            } else {
                $text += "$right = *$sid"
            }
        }
        $text | Set-Content $inf -Encoding Unicode
        secedit /configure /db $db /cfg $inf /areas USER_RIGHTS | Out-Null
        Remove-Item $inf, $db -Force -ErrorAction SilentlyContinue

        # The NAS credential has to live in THAT account's vault, and cmdkey
        # only ever writes to the vault of whoever runs it. So it is run as that
        # account, once, through the task scheduler. Running it here would store
        # the credential for the administrator and the service would still be
        # refused - which is the shape of the bug this whole account exists to
        # avoid, so it is worth not repeating one level down.
        if ($NasHost -and $NasUser) {
            $task = 'Deluno one-shot store NAS credential'
            Register-ScheduledTask -TaskName $task -Force `
                -Action (New-ScheduledTaskAction -Execute 'cmdkey.exe' `
                    -Argument "/add:$NasHost /user:$NasUser /pass:$NasPassword") `
                -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(1)) `
                -User ".\$Account" -Password $AccountPassword -RunLevel Highest | Out-Null
            Start-ScheduledTask -TaskName $task
            Start-Sleep -Seconds 8
            Unregister-ScheduledTask -TaskName $task -Confirm:$false
        }
    } @($ServiceAccount, $ServiceAccountPassword, $(if ($LibraryPath -like '\\*') { ($LibraryPath -split '\\')[2] } else { $null }), $NasUser, $NasPassword)

    Step "$ServiceAccount exists, is an administrator, and may log on as a service and as a batch job"
    if ($LibraryPath -like '\\*') { Step "stored the share credential in $ServiceAccount's own vault" }
}

# ------------------------------------------------------------------ folders

if (Should 'folders') {
    Head 'Folder topology'

    $created = Rig {
        param($Account)
        $paths = @(
            'C:\Deluno\App', 'C:\Deluno\Data', 'C:\Deluno\Setup',
            'C:\Deluno\Downloads-Complete\Movies', 'C:\Deluno\Downloads-Complete\TV',
            'C:\Deluno\Downloads-Incomplete',
            'C:\Deluno\Refined\Movies', 'C:\Deluno\Refined\TV',
            'C:\Deluno\Work\Movies', 'C:\Deluno\Work\TV',
            'C:\Deluno\qbt-profile', 'C:\Deluno\Weir'
        )
        foreach ($p in $paths) { New-Item -ItemType Directory -Force $p | Out-Null }

        # The services run as the account, so it has to own the tree.
        $acl = Get-Acl 'C:\Deluno'
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $Account, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl 'C:\Deluno' $acl
        $paths.Count
    } @($ServiceAccount)

    Step "$created directories, with $ServiceAccount granted full control of C:\Deluno"
    if ($LibraryPath) { Step "library will be $LibraryPath (not created here; that is the share's job)" }
}

# ------------------------------------------------------------------ apps

if (Should 'apps') {
    Head 'Applications'

    $installed = Rig {
        param($Software)
        $report = @()

        foreach ($app in @('qbittorrent', 'sabnzbd')) {
            $spec = $Software.$app
            if (Test-Path $spec.installedTo) {
                $report += "$app already installed"
                continue
            }
            $setup = Join-Path 'C:\Deluno\Setup' $spec.fileName
            if (-not (Test-Path $setup)) { throw "$($spec.fileName) is not in C:\Deluno\Setup. Run -Stage stage first." }
            $p = Start-Process -FilePath $setup -ArgumentList $spec.silentArgs -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "$app installer exited $($p.ExitCode)" }
            if (-not (Test-Path $spec.installedTo)) { throw "$app installer reported success but $($spec.installedTo) is not there" }
            $report += "$app $($spec.version) installed"
        }

        # Weir is a zip, unpacked to a fixed path rather than a user profile.
        if (Test-Path $Software.weir.installedTo) {
            $report += 'weir already unpacked'
        } else {
            Expand-Archive -Path 'C:\Deluno\Setup\Weir-win-Portable.zip' -DestinationPath 'C:\Deluno\Weir' -Force
            if (-not (Test-Path $Software.weir.installedTo)) {
                $found = Get-ChildItem 'C:\Deluno\Weir' -Recurse -Filter 'WeirServer.exe' | Select-Object -First 1
                throw "WeirServer.exe is not at $($Software.weir.installedTo)$(if ($found) { "; it unpacked to $($found.FullName)" })"
            }
            $report += "weir $($Software.weir.version) unpacked"
        }

        $report
    } @($software)

    $installed | ForEach-Object { Step $_ }
}

# ------------------------------------------------------------------ configure

if (Should 'configure') {
    Head 'Client configuration'

    Rig {
        param($LibraryPath)

        # Captured from the retired VM, which had these settings and a working
        # pipeline - with one correction. deluno-tv's save_path was EMPTY there
        # while deluno-movies pointed at a subfolder, so TV landed in the root
        # of Downloads-Complete and left a stray deluno-tv folder behind. Both
        # categories name their folder here.
        $profile = 'C:\Deluno\qbt-profile\qBittorrent\config'
        New-Item -ItemType Directory -Force $profile | Out-Null

        @'
[BitTorrent]
Session\DefaultSavePath=C:/Deluno/Downloads-Complete
Session\TempPath=C:/Deluno/Downloads-Incomplete
Session\TempPathEnabled=true
Session\DisableAutoTMMTriggers\CategorySavePathChanged=false
Session\DisableAutoTMMTriggers\DefaultSavePathChanged=false

[Preferences]
Downloads\SavePath=C:/Deluno/Downloads-Complete
Downloads\TempPath=C:/Deluno/Downloads-Incomplete
Downloads\TempPathEnabled=true
WebUI\Address=*
WebUI\Port=8080
WebUI\LocalHostAuth=false
WebUI\AuthSubnetWhitelist=192.168.1.0/24
WebUI\AuthSubnetWhitelistEnabled=true
'@ | Set-Content (Join-Path $profile 'qBittorrent.ini') -Encoding utf8

        @'
{
    "deluno-movies": {
        "save_path": "C:/Deluno/Downloads-Complete/Movies"
    },
    "deluno-tv": {
        "save_path": "C:/Deluno/Downloads-Complete/TV"
    }
}
'@ | Set-Content (Join-Path $profile 'categories.json') -Encoding utf8

        # SABnzbd writes its own ini on first start; it only needs the folder and
        # the absolute paths that provision-usenet.ps1 will then build on.
        New-Item -ItemType Directory -Force 'C:\Deluno\Data\sabnzbd' | Out-Null
    } @($LibraryPath)

    Step 'qBittorrent profile written, with both categories naming their folder'
    Step 'SABnzbd data directory prepared; provision-usenet.ps1 does its news server and category'
}

# ------------------------------------------------------------------ services

if (Should 'services') {
    Head 'Services'
    Step 'handing over to ensure-rig-services.ps1, which owns the shape'

    & (Join-Path $PSScriptRoot 'ensure-rig-services.ps1') `
        -ComputerName $ComputerName -UserName $UserName -Password $Password `
        -ServiceAccount ".\$ServiceAccount" -ServiceAccountPassword $ServiceAccountPassword
}

# ------------------------------------------------------------------ verify

if (Should 'verify') {
    Head 'Verify'

    $ports = Rig {
        @(5099, 8080, 8085, 8788) | ForEach-Object {
            [pscustomobject]@{ Port = $_; Listening = [bool](Get-NetTCPConnection -State Listen -LocalPort $_ -ErrorAction SilentlyContinue) }
        }
    }
    $ports | ForEach-Object { Step ("port {0,-5} {1}" -f $_.Port, $(if ($_.Listening) { 'answering' } else { 'SILENT' })) }

    if ($LibraryPath -like '\\*') {
        # As the service account, through a task - NOT from this session. A WinRM
        # session has no delegatable network credentials, so testing the share
        # from here would fail whether or not the service could reach it, and
        # would send somebody to debug the wrong machine.
        $result = Rig {
            param($Account, $AccountPassword, $Path)
            $out = 'C:\Deluno\Setup\nas-probe.txt'
            $probe = 'C:\Deluno\Setup\nas-probe.ps1'
            Remove-Item $out -Force -ErrorAction SilentlyContinue

            # A file, not a -Command string. The probe has to carry a UNC path
            # and its own quoting through the task scheduler, and escaping that
            # through three layers is how you end up debugging the escaping
            # instead of the share.
            @"
try {
    `$count = (Get-ChildItem -LiteralPath '$Path' -ErrorAction Stop).Count
    Set-Content -LiteralPath '$out' -Value "OK `$count entries"
} catch {
    Set-Content -LiteralPath '$out' -Value "FAIL `$(`$_.Exception.Message)"
}
"@ | Set-Content -LiteralPath $probe -Encoding utf8

            $task = 'Deluno one-shot NAS probe'
            Register-ScheduledTask -TaskName $task -Force `
                -Action (New-ScheduledTaskAction -Execute 'powershell.exe' `
                    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$probe`"") `
                -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(1)) `
                -User ".\$Account" -Password $AccountPassword -RunLevel Highest | Out-Null
            Start-ScheduledTask -TaskName $task
            Start-Sleep -Seconds 10
            Unregister-ScheduledTask -TaskName $task -Confirm:$false
            if (Test-Path $out) { Get-Content $out -Raw } else { 'FAIL the probe never wrote a result' }
        } @($ServiceAccount, $ServiceAccountPassword, $LibraryPath)

        if ($result -like 'OK*') { Step "$ServiceAccount can read $LibraryPath - $($result.Trim())" }
        else { Warn "$ServiceAccount cannot read ${LibraryPath}: $($result.Trim())" }
    }

    Head 'Provisioned. Deluno is running and untouched - its first run is phase 0 of the plan, not this script.'
}

if ($script:session) { Remove-PSSession $script:session }
