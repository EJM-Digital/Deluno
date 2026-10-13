<#
.SYNOPSIS
    Runs the scenario suite inside the golden-path VM, unattended, in the signed-in desktop session (golden path scenario suite).

.DESCRIPTION
    Started by Invoke-ScenarioSuite.ps1 (as a scheduled task with an interactive logon, hidden) after the harness has installed
    the release candidate. It sets Deluno up through its API, then runs every scenario group in order and writes results.json
    after each scenario, so the host can read progress. When it ends it writes done.flag (with "ok" or the error that stopped it).
    Windows PowerShell 5.1.

.PARAMETER Root
    The suite's folder in the VM. Holds secrets.json (throwaway logins, never printed), logs, results and evidence.

.PARAMETER Groups
    Only these groups (see ScenarioPlan.ps1). Empty runs all.

.PARAMETER Scenarios
    Only these scenarios (ids, or patterns such as resend.*.sabnzbd). Each group prepares only what the chosen scenarios need, so a
    scenario run alone gives the verdict it gives in a full run. Empty runs all. Not together with -Groups.

.PARAMETER SkipEnvironment
    Do not set Deluno up again (it was set up by an earlier run of this folder).

.PARAMETER SetupOnly
    Set Deluno up, leave Weir's login (and the Deluno key) for Weir's own suite, and stop before any group. Weir's Update phase runs
    here, between Deluno's setup and its films (Invoke-ScenarioSuite.ps1 -AfterSetupScript); the groups then run with -SkipEnvironment.

.PARAMETER PrepareOnly
    Set Deluno up and stop: nothing is left for Weir's suite. The host takes the "ready" checkpoint after this (Invoke-ScenarioSuite.ps1
    -UseReadyCheckpoint); the scenarios then run with -SkipEnvironment.
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\golden\suite',
    [string[]] $Groups = @(),
    [string[]] $Scenarios = @(),
    [switch] $SkipEnvironment,
    [switch] $SetupOnly,
    [switch] $PrepareOnly
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$suiteDir = Split-Path $here -Parent

. (Join-Path $here 'Suite.Lib.ps1')
. (Join-Path $here 'Tray.Lib.ps1')
. (Join-Path $here 'Cdp.Lib.ps1')
. (Join-Path $here 'Fixtures.Lib.ps1')
. (Join-Path $here 'Initialize-Environment.ps1')
. (Join-Path $suiteDir 'ScenarioPlan.ps1')
. (Join-Path $here 'scenarios\Common.ps1')
foreach ($f in Get-ChildItem (Join-Path $here 'scenarios') -Filter 'G*.ps1' | Sort-Object Name) { . $f.FullName }

$Groups = @($Groups | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$selected = @(Resolve-ScenarioSelection -Scenarios $Scenarios)
if ($selected.Count -gt 0 -and $Groups.Count -gt 0) { throw 'Use -Groups or -Scenarios, not both.' }
Set-ScenarioSelection -Ids $selected
Initialize-Suite -Root $Root
$flag = Join-Path $Root 'results\done.flag'
Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
$final = 'ok'
try {
    Write-SuiteLog 'Scenario suite starting.'
    if ($selected.Count -gt 0) { Write-SuiteLog ("Only these scenarios: " + ($selected -join ', ')) }
    if (-not $SkipEnvironment) { Initialize-SuiteEnvironment }
    else { Wait-DelunoUp | Out-Null; Connect-Deluno; Start-FeedAndStubs; Initialize-Indexers; Initialize-BaseVideos }
    if ($PrepareOnly) {
        Write-SuiteLog 'Set up only: Deluno is ready for the scenarios; nothing is left for Weir''s suite and no group runs in this pass.'
        return
    }
    if ($SetupOnly) {
        Publish-WeirScenarioKey
        Publish-WeirScenarioLogin -BeforeAnyFilm
        Write-SuiteLog 'Set up only: Weir''s login and the Deluno key are left for Weir''s suite; no group runs in this pass.'
        return
    }
    foreach ($group in Get-ScenarioPlan) {
        if ($Groups.Count -gt 0 -and $group.Name -notin $Groups) { continue }
        # A group none of whose scenarios was chosen sets nothing up.
        if (-not (Test-AnyScenarioSelected -Ids $group.Ids)) { continue }
        # The update phase is run by the host after the groups, on a machine of its own (Invoke-ScenarioSuite.ps1, Run-Update.ps1).
        if ($group.Host) { continue }
        Write-SuiteLog "=== group $($group.Name)"
        try { & $group.Function }
        catch {
            # A group that breaks as a whole is recorded so the host sees which one, and the run goes on with the next.
            $msg = Hide-Secrets $_.Exception.Message
            Write-SuiteLog "   GROUP ERROR $($group.Name): $msg"
            $rec = [ordered]@{ id = "group.$($group.Name)"; title = "The $($group.Name) group stopped"; status = 'error'; startedUtc = [DateTime]::UtcNow.ToString('o'); seconds = 0
                checks = @(); notes = @(); error = $msg }
            [void]$script:S.Results.Add($rec)
            Save-Results
        }
    }
}
catch { $final = Hide-Secrets $_.Exception.Message; Write-SuiteLog "SUITE ERROR $final" }
finally {
    Save-Results
    # After the last scenario: the key Weir's scenario suite needs (Weir #954). A failure here is the run's failure. Only a run
    # of the whole suite, or of the last group alone, leaves it (a run of single scenarios is not Weir's suite's starting point).
    if (-not $SetupOnly -and -not $PrepareOnly -and $selected.Count -eq 0 -and ($Groups.Count -eq 0 -or $Groups -contains 'problems')) {
        try { Publish-WeirScenarioKey }
        catch { $final = 'Could not make the key for Weir''s suite: ' + (Hide-Secrets $_.Exception.Message); Write-SuiteLog $final }
        try { Publish-WeirScenarioLogin }
        catch { $final = 'Could not hand Weir''s suite its login: ' + (Hide-Secrets $_.Exception.Message); Write-SuiteLog $final }
    }
    Set-Content -Path $flag -Value $final -Encoding utf8
    Write-SuiteLog "Scenario suite finished: $final"
}
