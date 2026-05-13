param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $Root

$passCount = 0
$warnCount = 0
$failCount = 0

function Write-Ok {
    param([string]$Message)
    Write-Host "  OK  $Message"
    $script:passCount++
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  !!  $Message"
    $script:warnCount++
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  XX  $Message"
    $script:failCount++
}

function Test-EnvLock {
    param([string]$Text)
    return $Text -match 'MSB3027|MSB3021|MSB3492|CS2012|being used by another process|locked by:|cannot access the file'
}

function Invoke-LoggedCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $tmp = [System.IO.Path]::GetTempFileName()

    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE

        if ($null -ne $output) {
            $output | Out-File -FilePath $tmp -Encoding utf8
        }

        $text = if (Test-Path $tmp) { Get-Content $tmp -Raw } else { "" }

        return @{
            ExitCode = $exitCode
            Output = $text
        }
    } finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Backend"

$restore = Invoke-LoggedCommand -FilePath "dotnet" -Arguments @("restore", "Deluno.slnx", "-q")
if ($restore.ExitCode -eq 0) {
    Write-Ok "restore"
} else {
    if ($restore.Output) { Write-Host $restore.Output }
    Write-Fail "restore failed"
}

$build = Invoke-LoggedCommand -FilePath "dotnet" -Arguments @("build", "Deluno.slnx", "--configuration", "Release", "--no-restore", "-m:1", "-q")
if ($build.ExitCode -eq 0) {
    Write-Ok "build"
} elseif (Test-EnvLock $build.Output) {
    Write-Warn "Release outputs locked by running server; CI will catch real errors"
} else {
    if ($build.Output) { Write-Host $build.Output }
    Write-Fail "build failed"
}

Write-Host ""
Write-Host "Tray (Linux CI simulation)"

$tray = Invoke-LoggedCommand -FilePath "dotnet" -Arguments @(
    "build",
    "apps/windows-tray/Deluno.Tray.csproj",
    "-p:SimulateLinuxCI=true",
    "--configuration",
    "Release",
    "--no-restore",
    "-q"
)

if ($tray.ExitCode -eq 0) {
    Write-Ok "tray builds as empty Library"
} elseif (Test-EnvLock $tray.Output) {
    Write-Warn "Tray build locked by running server; CI will catch real errors"
} else {
    if ($tray.Output) { Write-Host $tray.Output }
    Write-Fail "tray would fail on Linux CI"
}

Write-Host ""
Write-Host "Frontend"

$packageLock = Join-Path $Root "package-lock.json"
$nodeModules = Join-Path $Root "node_modules"
$nodeModulesLock = Join-Path $nodeModules ".package-lock.json"
$needsNpmCi = -not (Test-Path $nodeModules) -or ((Test-Path $packageLock) -and (-not (Test-Path $nodeModulesLock) -or (Get-Item $packageLock).LastWriteTimeUtc -gt (Get-Item $nodeModulesLock).LastWriteTimeUtc))

if ($needsNpmCi) {
    Write-Host "   npm ci (node_modules out of date)..."
    $npmCi = Invoke-LoggedCommand -FilePath "npm.cmd" -Arguments @("ci", "--silent")
    if ($npmCi.ExitCode -eq 0) {
        Write-Ok "npm ci"
    } else {
        if ($npmCi.Output) { Write-Host $npmCi.Output }
        Write-Fail "npm ci failed"
    }
} else {
    Write-Ok "npm ci (node_modules current, skipped)"
}

$web = Invoke-LoggedCommand -FilePath "npm.cmd" -Arguments @("run", "build:web", "--silent")
if ($web.ExitCode -eq 0) {
    Write-Ok "build:web"
} else {
    if ($web.Output) { Write-Host $web.Output }
    Write-Fail "build:web failed"
}

Write-Host ""
Write-Host "Agent readiness"

$agents = Invoke-LoggedCommand -FilePath "powershell" -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/validate-agent-readiness.ps1")
if ($agents.ExitCode -eq 0) {
    Write-Ok "agent readiness"
} else {
    if ($agents.Output) { Write-Host $agents.Output }
    Write-Fail "agent readiness"
}

Write-Host ""
Write-Host "--------------------------------------"
Write-Host "  Passed: $passCount  Warned: $warnCount  Failed: $failCount"
Write-Host "--------------------------------------"

if ($failCount -gt 0) {
    Write-Host "  XX  Fix the failures above before pushing."
    exit 1
}

if ($warnCount -gt 0) {
    Write-Host "  !!  Warnings present (env locks from running server); safe to push."
    exit 0
}

Write-Host "  OK  All checks passed; safe to push."
