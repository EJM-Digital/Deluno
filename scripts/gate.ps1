param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    # Run the three checks one after another, the old way, for comparison.
    [switch]$Serial
)

# The full pre-push gate: Release build, then the backend tests, ci:check and
# the web tests. Those three do not depend on each other, so they run side by
# side after one shared build (about 20 minutes one after another). The build
# comes first on purpose: ci:check and the smoke host (`dotnet run`) build
# Release again, and with nothing changed those builds copy nothing, so they
# never overwrite binaries the backend tests have loaded.

$ErrorActionPreference = "Stop"
Set-Location $Root

# Create a unique log directory per checkout using a hash of the repo root path.
# This prevents multiple concurrent gates (e.g., main + worktree) from interleaving logs.
$md5 = [System.Security.Cryptography.MD5]::Create()
$hash = [System.BitConverter]::ToString($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Root))) -replace "-", ""
$hashPrefix = $hash.Substring(0, 8)
$logDir = Join-Path ([System.IO.Path]::GetTempPath()) "deluno-gate-$hashPrefix"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# CI runs the browser smoke suite as two shards, each against its own fresh smoke host
# and database (.github/workflows/ci.yml, job e2e-smoke). The gate does the same, so a
# spec that depends on state an earlier spec left behind fails here and not only in CI.
# Shard 1 uses DELUNO_SMOKE_API_PORT / DELUNO_SMOKE_WEB_PORT (default 5199 / 5174), shard 2
# uses those plus 100. All four must be free before anything starts.
$smokeApiPort = if ($env:DELUNO_SMOKE_API_PORT) { [int]$env:DELUNO_SMOKE_API_PORT } else { 5199 }
$smokeWebPort = if ($env:DELUNO_SMOKE_WEB_PORT) { [int]$env:DELUNO_SMOKE_WEB_PORT } else { 5174 }
$shards = @(
    @{ Id = 1; Shard = "1/2"; ApiPort = $smokeApiPort; WebPort = $smokeWebPort },
    @{ Id = 2; Shard = "2/2"; ApiPort = $smokeApiPort + 100; WebPort = $smokeWebPort + 100 }
)
foreach ($shard in $shards) {
    foreach ($port in @($shard.ApiPort, $shard.WebPort)) {
        try {
            $connection = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
            if ($connection) {
                Write-Host "Port $port is already in use (needed by smoke shard $($shard.Shard))." -ForegroundColor Yellow
                Write-Host "Set DELUNO_SMOKE_API_PORT (currently $smokeApiPort) and DELUNO_SMOKE_WEB_PORT (currently $smokeWebPort) so that those and the same numbers plus 100 are all unused, and try again." -ForegroundColor Yellow
                exit 1
            }
        } catch {
            # If Get-NetTCPConnection fails, skip the check and let Playwright fail later.
        }
    }
}

$total = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "Release build..."
$build = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet build Deluno.slnx --configuration Release -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "  XX  Release build failed" -ForegroundColor Red
    exit 1
}
Write-Host ("  OK  Release build ({0:mm\:ss})" -f $build.Elapsed)

$powershell = (Get-Command powershell.exe).Source
$npm = (Get-Command npm.cmd).Source
# Release is built once, above. Neither the backend tests nor the smoke hosts build
# it again: two builds at the same moment raced for the same output files and the
# smoke host failed to start ("Process from config.webServer was not able to start").
$env:DELUNO_SMOKE_NO_BUILD = "1"

function Start-GateStep($name, $file, $arguments) {
    $log = Join-Path $logDir (($name -replace "[^A-Za-z0-9]", "") + ".log")
    $err = "$log.err"
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $Root `
        -RedirectStandardOutput $log -RedirectStandardError $err -NoNewWindow -PassThru
    $null = $process.Handle  # makes ExitCode readable after a redirected Start-Process
    $entry = @{ Name = $name; Process = $process; Log = $log; Err = $err }
    if ($Serial) {
        $process.WaitForExit()
    }
    return $entry
}

$running = @()
# Tests that time an endpoint against a budget (Category=Timing) do not run beside
# the web tests, which starve the machine: an endpoint that answers in 12 ms alone
# took 754 ms there. They run on their own once the parallel steps are done.
$running += Start-GateStep "Backend tests" $powershell "-NoProfile -ExecutionPolicy Bypass -File scripts\test-dotnet-serial.ps1 -NoBuild -Filter `"Category!=Timing`""

# CI builds the web app once and hands both smoke shards that build. The gate does the
# same, and keeps its own copy of the bundle: ci:check builds the web app again into
# apps/web/dist while the shards run, which would empty the folder under them.
Write-Host "Web build..."
$webBuild = [System.Diagnostics.Stopwatch]::StartNew()
$webBuildLog = Join-Path $logDir "Webbuild.log"
$webBuildProcess = Start-Process -FilePath $npm -ArgumentList "--workspace apps/web run build" -WorkingDirectory $Root `
    -RedirectStandardOutput $webBuildLog -RedirectStandardError "$webBuildLog.err" -NoNewWindow -PassThru -Wait
if ($webBuildProcess.ExitCode -ne 0) {
    Write-Host "  XX  Web build failed; log: $webBuildLog" -ForegroundColor Red
    Get-Content $webBuildLog -Tail 25 | ForEach-Object { Write-Host "      $_" }
    exit 1
}
Write-Host ("  OK  Web build ({0:mm\:ss})" -f $webBuild.Elapsed)
$smokeDist = Join-Path $logDir "web-dist"
if (Test-Path $smokeDist) { Remove-Item -Recurse -Force $smokeDist }
Copy-Item -Recurse -Path (Join-Path $Root "apps\web\dist") -Destination $smokeDist

$running += Start-GateStep "ci:check" $npm "run ci:check"
$running += Start-GateStep "Web unit tests" $npm "--workspace apps/web run test:unit"

$env:DELUNO_SMOKE_PREBUILT = "1"
$env:DELUNO_SMOKE_DIST = $smokeDist
foreach ($shard in $shards) {
    $env:DELUNO_SMOKE_API_PORT = [string]$shard.ApiPort
    $env:DELUNO_SMOKE_WEB_PORT = [string]$shard.WebPort
    $output = Join-Path $logDir "playwright-output-$($shard.Id)"
    $running += Start-GateStep "Smoke shard $($shard.Shard)" $npm "--workspace apps/web run test:smoke -- --shard=$($shard.Shard) --reporter=dot --output=`"$output`""
}
# Put the caller's smoke ports back for anything started later.
$env:DELUNO_SMOKE_API_PORT = [string]$smokeApiPort
$env:DELUNO_SMOKE_WEB_PORT = [string]$smokeWebPort

$failed = 0
foreach ($entry in $running) {
    $entry.Process.WaitForExit()
    $elapsed = $entry.Process.ExitTime - $entry.Process.StartTime
    $code = $entry.Process.ExitCode
    if ($code -eq 0) {
        Write-Host ("  OK  {0} ({1:mm\:ss})" -f $entry.Name, $elapsed)
    } else {
        $failed++
        Write-Host ("  XX  {0} failed with exit code {1} ({2:mm\:ss}); log: {3}" -f $entry.Name, $code, $elapsed, $entry.Log) -ForegroundColor Red
        Get-Content $entry.Log -Tail 25 | ForEach-Object { Write-Host "      $_" }
    }
}

$timing = [System.Diagnostics.Stopwatch]::StartNew()
$timingLog = Join-Path $logDir "Timingtests.log"
& dotnet test (Join-Path $Root "tests\Deluno.Persistence.Tests\Deluno.Persistence.Tests.csproj") --configuration Release --no-build --no-restore `
    --filter "Category=Timing" *> $timingLog
if ($LASTEXITCODE -eq 0) {
    Write-Host ("  OK  Timing tests, run alone ({0:mm\:ss})" -f $timing.Elapsed)
} else {
    $failed++
    Write-Host ("  XX  Timing tests failed, run alone ({0:mm\:ss}); log: {1}" -f $timing.Elapsed, $timingLog) -ForegroundColor Red
    Get-Content $timingLog -Tail 25 | ForEach-Object { Write-Host "      $_" }
}

Write-Host ("Gate finished in {0:mm\:ss} ({1})" -f $total.Elapsed, $(if ($Serial) { "serial" } else { "parallel" }))
if ($failed -gt 0) {
    Write-Host "  XX  $failed step(s) failed; do not push." -ForegroundColor Red
    exit 1
}
Write-Host "  OK  Every step passed; safe to push."
exit 0
