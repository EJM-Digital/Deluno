<#
.SYNOPSIS
    Counts a full scenario-suite run plus passing re-runs as the golden-path gate (James, 17 Oct 2026).

.DESCRIPTION
    When a full run fails only on harness or timing problems and the fix touches no shipped file, the full run's passes plus each
    failed scenario re-run alone and passing on the same build count as the gate. This checks all of it:
      - the same build: every run is for the same commit and its installed build carries that commit;
      - every failed or missing scenario of the full run has a passing re-run (a group that broke as a whole needs a re-run of
        the whole group);
      - no shipped path changed between the full run's harness and now (scripts/ship-paths.mjs, the one rule).
    It then writes one record (scenario-suite-results.md and .json) listing the full run and the re-runs, and sets
    golden-path=success on the commit. Any check that fails sets nothing and exits 1.

.PARAMETER FullRun
    The full run's results folder (holds scenario-suite-results.json).
.PARAMETER Reruns
    The results folders of the re-runs (each a -Scenarios or -Groups run that passed).
.PARAMETER FullRunHarnessCommit
    The commit the full run's harness (scripts/golden-path) ran from. Newer results record it; older ones need it here.
.PARAMETER Head
    The commit to compare to (default HEAD).
.PARAMETER ShipPathsScript
    The ship-paths module (default scripts/ship-paths.mjs of this repository).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FullRun,
    [Parameter(Mandatory)] [string[]] $Reruns,
    [string] $FullRunHarnessCommit,
    [string] $Head = 'HEAD',
    [string] $ShipPathsScript,
    [string] $OutDirectory,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $ResultsUrl,
    [switch] $DryRun
)

# One value for the repository (repository.json); the Actions environment's own name wins when set.
if (-not $Repository) { $Repository = (Get-Content -Raw (Join-Path $PSScriptRoot '..\..\repository.json') | ConvertFrom-Json).deluno }

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'suite\ScenarioPlan.ps1')
. (Join-Path $PSScriptRoot 'suite\SuiteHost.Lib.ps1')

function Read-RunResults([string] $Folder) {
    $f = Join-Path $Folder 'scenario-suite-results.json'
    if (-not (Test-Path -LiteralPath $f)) { throw "No scenario-suite-results.json in $Folder." }
    Get-Content -Raw -LiteralPath $f | ConvertFrom-Json
}

$Reruns = @($Reruns | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$plan = @(Get-ScenarioPlan)
$full = Read-RunResults $FullRun
$rerunResults = @($Reruns | ForEach-Object { Read-RunResults $_ })

if (-not $FullRunHarnessCommit) { $FullRunHarnessCommit = [string]$full.harnessCommit }
if (-not $FullRunHarnessCommit) { throw 'The full run does not record the commit its harness ran from: pass -FullRunHarnessCommit.' }
if ($full.harnessDirty) { throw 'The full run ran from a harness with uncommitted changes, so no commit stands for it.' }
if (-not $ShipPathsScript) { $ShipPathsScript = Join-Path $repoRoot 'scripts\ship-paths.mjs' }
$headSha = (& git -C $repoRoot rev-parse --verify "$Head^{commit}" 2>$null | Select-Object -First 1)
if (-not $headSha) { throw "$Head is not a commit in this repository." }

Push-Location $repoRoot
try { $ship = Invoke-ShipPathsCheck -Script $ShipPathsScript -Base $FullRunHarnessCommit -Head $headSha } finally { Pop-Location }
$judge = Get-CombinedRunJudgement -Full $full -Reruns $rerunResults -Plan $plan -ShipPaths $ship -FullHarnessCommit $FullRunHarnessCommit

$commit = [string]$full.commit
$version = [string]$full.version
if (-not $OutDirectory) { $OutDirectory = Join-Path $repoRoot ("artifacts\scenario-suite-{0}-combined-{1}" -f $version, (Get-Date -Format 'yyyyMMdd-HHmm')) }
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null

$state = if ($judge.Ok) { 'success' } else { 'failure' }
$passed = @($judge.Combined | Where-Object { $_.Status -eq 'pass' }).Count
$verdict = [pscustomobject]@{ State = $state; Passed = $passed; Total = @($judge.Combined).Count; Failed = @($judge.Needed | Where-Object { $judge.Problems.Count -gt 0 }); HarnessFailures = @(); BuildMatches = $true; FullRun = $true }
$note = " (full run + $($rerunResults.Count) re-run$(if ($rerunResults.Count -ne 1) { 's' }))"
$statusArgs = Get-StatusArguments -Repository $Repository -Sha $commit -Verdict $verdict -Version $version -TargetUrl $ResultsUrl -Note $note

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("# Combined scenario-suite record for $version ($commit)")
[void]$lines.Add('')
[void]$lines.Add("Verdict: **$($state.ToUpperInvariant())**: $passed of $(@($judge.Combined).Count) scenarios pass across the full run and its re-runs.")
[void]$lines.Add('')
[void]$lines.Add("- Full run: $FullRun ($($full.passed) of $($full.total) passed; failed: $((@($full.failed)) -join ', ')); harness commit $FullRunHarnessCommit")
$i = 0
foreach ($r in $Reruns) { $i++; [void]$lines.Add("- Re-run ${i}: $r ($((@((Read-RunResults $r).scenarios) | ForEach-Object { $_.Id }) -join ', '))") }
[void]$lines.Add("- Shipped files: $($ship.Reason) (compared $FullRunHarnessCommit to $headSha)")
[void]$lines.Add("- Installed build: $($full.installedBuild)")
if ($judge.Problems.Count -gt 0) { [void]$lines.Add(''); [void]$lines.Add('Not counted as the gate because:'); foreach ($p in $judge.Problems) { [void]$lines.Add("- $p") } }
[void]$lines.Add('')
[void]$lines.Add('| Scenario | Result | Vouched for by |')
[void]$lines.Add('| --- | --- | --- |')
foreach ($c in $judge.Combined) { [void]$lines.Add("| ``$($c.Id)`` | $($c.Status) | $($c.By) |") }
Set-Content -Path (Join-Path $OutDirectory 'scenario-suite-results.md') -Value $lines -Encoding utf8
([ordered]@{ version = $version; commit = $commit; state = $state; fullRun = $FullRun; reruns = $Reruns; harnessCommit = $FullRunHarnessCommit; head = $headSha; problems = $judge.Problems; scenarios = $judge.Combined } |
    ConvertTo-Json -Depth 8) | Set-Content -Path (Join-Path $OutDirectory 'scenario-suite-results.json') -Encoding utf8

Write-Host "Combined record: $(Join-Path $OutDirectory 'scenario-suite-results.md')"
if (-not $judge.Ok) {
    Write-Host 'Not counted as the gate:' -ForegroundColor Red
    $judge.Problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
if ($DryRun) { Write-Host "Dry run: would record: gh $($statusArgs -join ' ')" -ForegroundColor Yellow; exit 0 }
$out = & gh @statusArgs 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host "Recording the status failed: $out" -ForegroundColor Red; exit 2 }
Write-Host "Recorded golden-path=success on $commit." -ForegroundColor Green
exit 0
