<#
.SYNOPSIS
    Deploys the current publish output to the lab, copying only what changed.

.DESCRIPTION
    The lab is the environment of record, so a change is not proven until it is
    running there. That made deploy latency the tax on every slice, and it was
    a heavy one: a 163 MB single-file bundle plus 130 MB of stale web assets
    were zipped, transferred and unpacked to deliver, typically, under a
    megabyte of actual change.

    This compares SHA-256 per file against the host and copies only the
    differing ones, and removes files the build no longer produces so the host
    cannot accumulate orphans. Pair it with `publish-windows.ps1 -Fast`, whose
    output is ordinary assemblies rather than one bundle, and a backend change
    becomes a handful of DLLs.

    The scheduled task is stopped for the copy and started again afterwards,
    and the previous App directory is retained as a rollback exactly as a full
    promotion does. Readiness is proven before the script returns.

.EXAMPLE
    ./scripts/publish-windows.ps1 -Fast
    ./scripts/deploy-lab.ps1
#>
param(
    [string]$HostName,
    [string]$UserName,
    [string]$Password = $env:DELUNO_LAB_PASSWORD,
    [string]$AppPath,
    [string]$TaskName,
    [string]$ReadyUrl,
    [string]$Source,

    # Keep the previous App directory. Off by default now that only changed
    # files move: a full copy of 400 MB per deploy filled the disk quickly.
    # Use it for anything you would struggle to rebuild.
    [switch]$Rollback
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "lab\Get-Rig.ps1")
$rig = Get-Rig
if (-not $HostName) { $HostName = $rig.host }
if (-not $UserName) { $UserName = $rig.userName }
if (-not $AppPath)  { $AppPath = $rig.deluno.appPath }
if (-not $TaskName) { $TaskName = $rig.deluno.taskName }
if (-not $ReadyUrl) { $ReadyUrl = "$($rig.deluno.url)/api/health/ready" }

if (-not $Source) {
    $Source = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\publish\win-x64"
}
if (-not (Test-Path $Source)) {
    throw "No publish output at $Source. Run scripts/publish-windows.ps1 first."
}
if (-not $Password) {
    throw "Set DELUNO_LAB_PASSWORD, or pass -Password, so this script never carries a credential in source."
}

$started = [System.Diagnostics.Stopwatch]::StartNew()

$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($UserName, $secure)
$session = New-PSSession -ComputerName $HostName -Credential $credential

try {
    Write-Host "Hashing local publish output..."
    $sourceRoot = (Resolve-Path $Source).Path.TrimEnd('\') + '\'
    $local = @{}
    foreach ($file in Get-ChildItem $Source -Recurse -File) {
        $local[$file.FullName.Substring($sourceRoot.Length)] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    }
    Write-Host "  $($local.Count) files."

    Write-Host "Hashing deployed files on $HostName..."
    $remote = Invoke-Command -Session $session -ArgumentList $AppPath -ScriptBlock {
        param($appPath)
        $map = @{}
        if (Test-Path $appPath) {
            $root = $appPath.TrimEnd('\') + '\'
            foreach ($file in Get-ChildItem $appPath -Recurse -File) {
                $map[$file.FullName.Substring($root.Length)] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
            }
        }
        $map
    }
    Write-Host "  $($remote.Count) files."

    $changed = @($local.Keys | Where-Object { -not $remote.ContainsKey($_) -or $remote[$_] -ne $local[$_] })
    $orphaned = @($remote.Keys | Where-Object { -not $local.ContainsKey($_) })

    $changedBytes = 0
    foreach ($rel in $changed) { $changedBytes += (Get-Item (Join-Path $Source $rel)).Length }

    Write-Host ""
    Write-Host "  changed or new : $($changed.Count) files, $([math]::Round($changedBytes / 1MB, 2)) MB"
    Write-Host "  no longer built: $($orphaned.Count) files"
    Write-Host ""

    if ($changed.Count -eq 0 -and $orphaned.Count -eq 0) {
        Write-Host "Deployed build already matches. Nothing to do."
        return
    }

    Invoke-Command -Session $session -ArgumentList $TaskName -ScriptBlock {
        param($taskName)
        $ErrorActionPreference = 'Stop'
        Stop-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Process -Name 'Deluno.Host' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        $still = Get-Process -Name 'Deluno.Host' -ErrorAction SilentlyContinue
        if ($still) { $still | Stop-Process -Force; Start-Sleep -Seconds 2 }
        if (Get-Process -Name 'Deluno.Host' -ErrorAction SilentlyContinue) {
            throw 'Deluno.Host is still running; refusing to write over a live build.'
        }
    }

    $rollbackPath = $null
    if ($Rollback) {
        $rollbackPath = Invoke-Command -Session $session -ArgumentList $AppPath -ScriptBlock {
            param($appPath)
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $target = "$appPath.rollback-$stamp"
            Copy-Item -Path $appPath -Destination $target -Recurse -Force
            $target
        }
        Write-Host "Rollback retained at $rollbackPath"
    }

    foreach ($rel in $changed) {
        $destination = Join-Path $AppPath $rel
        Invoke-Command -Session $session -ArgumentList $destination -ScriptBlock {
            param($destination)
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        }
        Copy-Item -Path (Join-Path $Source $rel) -Destination $destination -ToSession $session -Force
    }
    Write-Host "Copied $($changed.Count) files."

    if ($orphaned.Count -gt 0) {
        Invoke-Command -Session $session -ArgumentList $AppPath, $orphaned -ScriptBlock {
            param($appPath, $orphaned)
            foreach ($rel in $orphaned) {
                Remove-Item -Path (Join-Path $appPath $rel) -Force -ErrorAction SilentlyContinue
            }
        }
        Write-Host "Removed $($orphaned.Count) files the build no longer produces."
    }

    # Prove the host now holds exactly what was published, rather than trusting
    # that the copies landed.
    $mismatch = Invoke-Command -Session $session -ArgumentList $AppPath, $local -ScriptBlock {
        param($appPath, $expected)
        $bad = @()
        foreach ($rel in $expected.Keys) {
            $path = Join-Path $appPath $rel
            if (-not (Test-Path $path)) { $bad += "missing: $rel"; continue }
            if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $expected[$rel]) { $bad += "differs: $rel" }
        }
        $bad
    }
    if ($mismatch.Count -gt 0) {
        throw "Deployed files do not match the publish output:`n$($mismatch -join "`n")"
    }
    Write-Host "Verified all $($local.Count) files match the publish output."

    $info = Invoke-Command -Session $session -ArgumentList $TaskName, $AppPath -ScriptBlock {
        param($taskName, $appPath)
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds(90)
        do {
            Start-Sleep -Seconds 2
            $process = Get-Process -Name 'Deluno.Host' -ErrorAction SilentlyContinue
        } while (-not $process -and (Get-Date) -lt $deadline)
        [PSCustomObject]@{
            Pid = ($process | Select-Object -First 1 -ExpandProperty Id)
            ExeSha256 = (Get-FileHash (Join-Path $appPath 'Deluno.Host.exe') -Algorithm SHA256).Hash
        }
    }

    $ready = $null
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 3
        try { $ready = Invoke-RestMethod -Uri $ReadyUrl -TimeoutSec 10 } catch { $ready = $null }
    } while (($null -eq $ready -or $ready.status -ne 'ready') -and (Get-Date) -lt $deadline)

    if ($null -eq $ready -or $ready.status -ne 'ready') {
        throw "Deluno did not become ready after deployment. Last status: $($ready.status)"
    }

    $readyChecks = @($ready.checks | Where-Object { $_.status -eq 'ready' }).Count
    $started.Stop()

    [PSCustomObject]@{
        FilesCopied = $changed.Count
        FilesRemoved = $orphaned.Count
        MegabytesSent = [math]::Round($changedBytes / 1MB, 2)
        Rollback = $rollbackPath
        Pid = $info.Pid
        ExeSha256 = $info.ExeSha256
        Readiness = "$readyChecks/$(@($ready.checks).Count) ready"
        Elapsed = "$([math]::Round($started.Elapsed.TotalSeconds, 1))s"
    }
}
finally {
    Remove-PSSession $session
}
