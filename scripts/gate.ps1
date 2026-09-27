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

$logDir = Join-Path ([System.IO.Path]::GetTempPath()) "deluno-gate"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
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
$steps = @(
    @{ Name = "Backend tests"; File = $powershell; Arguments = "-NoProfile -ExecutionPolicy Bypass -File scripts\test-dotnet-serial.ps1" },
    @{ Name = "ci:check"; File = $npm; Arguments = "run ci:check" },
    @{ Name = "Web tests"; File = $npm; Arguments = "run test:web" }
)

$running = @()
foreach ($step in $steps) {
    $log = Join-Path $logDir (($step.Name -replace "[^A-Za-z]", "") + ".log")
    $err = "$log.err"
    $process = Start-Process -FilePath $step.File -ArgumentList $step.Arguments -WorkingDirectory $Root `
        -RedirectStandardOutput $log -RedirectStandardError $err -NoNewWindow -PassThru
    $null = $process.Handle  # makes ExitCode readable after a redirected Start-Process
    $entry = @{ Name = $step.Name; Process = $process; Log = $log; Err = $err }
    if ($Serial) {
        $process.WaitForExit()
    }
    $running += $entry
}

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

Write-Host ("Gate finished in {0:mm\:ss} ({1})" -f $total.Elapsed, $(if ($Serial) { "serial" } else { "parallel" }))
if ($failed -gt 0) {
    Write-Host "  XX  $failed step(s) failed; do not push." -ForegroundColor Red
    exit 1
}
Write-Host "  OK  Every step passed; safe to push."
exit 0
