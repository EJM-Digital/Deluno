<#
.SYNOPSIS
    Checks the Windows end-to-end suite's own code offline, without installing or starting Deluno: every script parses on Windows
    PowerShell 5.1, the legs own exactly the scenarios the parity table gives to `windows`, the runner's result record has the golden
    path's JSON shape, a leg that stops early cannot read as a pass, and the CI job is wired to the runner script.

.DESCRIPTION
    `npm run ci:check` runs it. It needs nothing installed beyond Windows PowerShell 5.1 and never starts an application.
    Exit code 1 when any check fails.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$script:failures = 0
$script:passes = 0
function Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    if ($Ok) { $script:passes++; Write-Host "  ok    $Name" }
    else { $script:failures++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}

function Get-Thrown([scriptblock] $Block) { try { & $Block; $null } catch { $_ } }

Write-Host 'Every script parses'
foreach ($f in Get-ChildItem -Path $here -Recurse -Filter '*.ps1') {
    $errs = $null; $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errs)
    Check ("parses: " + $f.FullName.Substring($here.Length + 1)) ($errs.Count -eq 0) (($errs | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; ')
}

Write-Host 'No fixed sleep without a reason'
foreach ($f in Get-ChildItem -Path $here -Recurse -Filter '*.ps1' | Where-Object { $_.Name -ne 'Test-WindowsE2E.ps1' }) {
    $bad = @(Get-Content $f.FullName | Where-Object { $_ -match '\bStart-Sleep\b' -and $_ -notmatch '^\s*#' -and $_ -notmatch '#\s*reason:' })
    Check ("every sleep says why: " + $f.Name) ($bad.Count -eq 0) ($bad -join ' | ')
}

Write-Host 'The libraries load, and nothing starts'
. (Join-Path $here 'Windows.Lib.ps1')
foreach ($f in Get-ChildItem (Join-Path $here 'scenarios') -Filter 'W*.ps1' | Sort-Object Name) { . $f.FullName }
foreach ($fn in 'Invoke-LegInstall', 'Invoke-LegGithubPicker', 'Invoke-LegUpdate', 'Invoke-OutageTray', 'Invoke-GroupInstall', 'Invoke-UpdateScenario', 'Get-GithubPickerChecks', 'Use-GitHubLimitUp', 'Get-TrayDot', 'Watch-TrayWeirSignals') {
    Check "$fn is defined" ([bool](Get-Command $fn -ErrorAction SilentlyContinue))
}
Check 'Start-WeirApp is the runner''s (no scheduled task)' ((Get-Command Start-WeirApp).ScriptBlock.ToString() -notmatch 'ScheduledTask')

Write-Host 'The legs own exactly the scenarios the parity table gives to windows'
$planText = Get-Content -Raw (Join-Path $repo 'docs\exec-plans\active\PLAN-E2E-IN-CI.md')
$tableIds = @([regex]::Matches($planText, '(?m)^\| `([^`]+)` \| `windows` \|') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
$legIds = @(Get-LegPlan | ForEach-Object { $_.Ids } | Sort-Object)
Check 'the table and the legs list the same ids' (($tableIds -join ',') -eq ($legIds -join ',')) "table: $($tableIds -join ', '); legs: $($legIds -join ', ')"
Check 'every id is one of the golden path''s' (@($legIds | Where-Object { $_ -ne 'outage.tray' }).Count -gt 0 -and @($legIds | Where-Object { $_ -ne 'outage.tray' -and (Get-Content -Raw (Join-Path $repo 'scripts\golden-path\suite\ScenarioPlan.ps1')) -notmatch [regex]::Escape("'$_'") }).Count -eq 0)

Write-Host 'The result record has the golden path''s shape'
$root = Join-Path ([IO.Path]::GetTempPath()) ('e2ew-test-' + [guid]::NewGuid().ToString('N'))
try {
    Initialize-Suite -Root $root
    Invoke-Scenario -Id 'install-first-run' -Title 't' -Body { Add-Check 'a thing' $true 'detail' }
    $rec = $script:S.Results[0]
    Check 'a passing scenario reads pass' ($rec.status -eq 'pass')
    Add-LateChecks -Id 'install-first-run' -Body { Add-Check 'a late failure' $false 'why' }
    Check 'a late failing check turns the scenario to fail' ($script:S.Results[0].status -eq 'fail' -and $script:S.Results[0].checks.Count -eq 2)
    Assert-LegReported -Leg 'install'
    $ids = @($script:S.Results | ForEach-Object { $_.id })
    Check 'a scenario the leg never ran is recorded as an error' (($ids -contains 'outage.tray') -and (@($script:S.Results | Where-Object { $_.id -eq 'outage.tray' })[0].status -eq 'error'))
    Check 'a leg with a failed or missing scenario exits 1' ((Get-LegExitCode) -eq 1)
    $json = Get-Content -Raw (Join-Path $root 'results\results.json') | ConvertFrom-Json
    $keys = @($json.scenarios[0].PSObject.Properties.Name)
    Check 'results.json has startedUtc, updatedUtc and scenarios' ($json.startedUtc -and $json.updatedUtc -and @($json.scenarios).Count -eq 2)
    Check 'each scenario has id, title, status, startedUtc, seconds, checks, notes, error' (@('id', 'title', 'status', 'startedUtc', 'seconds', 'checks', 'notes', 'error' | Where-Object { $keys -notcontains $_ }).Count -eq 0) ($keys -join ',')
    Check 'each check has name, ok, detail, atUtc' (@($json.scenarios[0].checks[0].PSObject.Properties.Name | Sort-Object) -join ',' -eq 'atUtc,detail,name,ok')
    Check 'an unknown leg is refused' ($null -ne (Get-Thrown { Assert-LegReported -Leg 'nope' }))

    Initialize-Suite -Root $root
    Invoke-Scenario -Id 'github-limit.picker' -Title 't' -Body { Add-Check 'ok' $true }
    Assert-LegReported -Leg 'githubpicker'
    Check 'a leg whose scenarios all passed exits 0' ((Get-LegExitCode) -eq 0)

    Write-Host 'Finding the Setup executable'
    $dl = Join-Path $root 'artifact'
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    Set-Content -Path (Join-Path $dl 'Deluno-rc-Setup.exe') -Value 'x'
    Set-Content -Path (Join-Path $dl 'releases.rc.json') -Value '{}'
    Check 'a folder holding one Setup resolves to it' ((Resolve-SetupExe -Path $dl) -like '*Deluno-rc-Setup.exe')
    Check 'a file resolves to itself' ((Resolve-SetupExe -Path (Join-Path $dl 'Deluno-rc-Setup.exe')) -like '*Deluno-rc-Setup.exe')
    Set-Content -Path (Join-Path $dl 'Other-Setup.exe') -Value 'x'
    Check 'a folder holding two is refused' ($null -ne (Get-Thrown { Resolve-SetupExe -Path $dl }))
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'The tray reading used on the runner classifies dots'
$grey = 1..40 | ForEach-Object { [pscustomobject]@{ H = 0; S = 0.0; B = 0.5 } }
Check 'a grey dot reads grey' ((Get-DotClassFromPixels -Pixels $grey).Colour -eq 'grey')
$amber = 1..40 | ForEach-Object { [pscustomobject]@{ H = 40; S = 0.9; B = 0.6 } }
Check 'an amber dot reads amber' ((Get-DotClassFromPixels -Pixels $amber).Colour -eq 'amber')
$green = 1..40 | ForEach-Object { [pscustomobject]@{ H = 120; S = 0.9; B = 0.6 } }
Check 'a green dot reads green' ((Get-DotClassFromPixels -Pixels $green).Colour -eq 'green')

Write-Host 'The CI job runs this suite'
$ci = Get-Content -Raw (Join-Path $repo '.github\workflows\ci.yml')
Check 'ci.yml has the e2e-windows job' ($ci -match '(?m)^  e2e-windows:')
Check 'it runs the runner script for each leg' ($ci -match 'tests/e2e-windows/Run-WindowsE2E\.ps1' -and $ci -match 'leg:\s*\[install, githubpicker, update\]')
Check 'it is due exactly when windows-installer is' ($ci -match "(?s)  e2e-windows:.{0,400}run_windows_installer == 'true'")
Check 'it takes the installer windows-installer built in the same run' ($ci -match 'name: deluno-windows-\$\{\{ github\.sha \}\}' -and $ci -match "(?s)  e2e-windows:.{0,700}needs: \[changes, windows-installer\]")

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:passes, $script:failures)
if ($script:failures -gt 0) { exit 1 }
