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

.PARAMETER SkipEnvironment
    Do not set Deluno up again (it was set up by an earlier run of this folder).
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\golden\suite',
    [string[]] $Groups = @(),
    [switch] $SkipEnvironment
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
Initialize-Suite -Root $Root
$flag = Join-Path $Root 'results\done.flag'
Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
$final = 'ok'
try {
    Write-SuiteLog 'Scenario suite starting.'
    if (-not $SkipEnvironment) { Initialize-SuiteEnvironment }
    else { Wait-DelunoUp | Out-Null; Connect-Deluno; Start-FeedAndStubs; Initialize-Indexers; Initialize-BaseVideos }
    foreach ($group in Get-ScenarioPlan) {
        if ($Groups.Count -gt 0 -and $group.Name -notin $Groups) { continue }
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
    Set-Content -Path $flag -Value $final -Encoding utf8
    Write-SuiteLog "Scenario suite finished: $final"
}
