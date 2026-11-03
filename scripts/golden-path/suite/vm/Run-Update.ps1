<#
.SYNOPSIS
    Runs the update phase inside the golden-path VM, in the signed-in desktop session (#1256). Started by Invoke-ScenarioSuite.ps1
    after it restored the clean checkpoint, installed the PREVIOUS release silently and started it. It gives that release some
    state, runs the candidate's Setup --silent over it, and from then on starts nothing. Writes results.json (one scenario,
    update.over-running-previous) and done.flag. Windows PowerShell 5.1.
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\golden\update',
    [Parameter(Mandatory)] [string] $CandidateSetup,
    [Parameter(Mandatory)] [string] $CandidateVersion,
    # Which update scenario this machine is for (update.over-running-previous when empty); the others are in UpdatePaths.Lib.ps1.
    [string] $Scenario = 'update.over-running-previous'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'Suite.Lib.ps1')
. (Join-Path $here 'Tray.Lib.ps1')
. (Join-Path $here 'Desktop.Lib.ps1')
. (Join-Path $here 'Initialize-Environment.ps1')
. (Join-Path $here 'Update.Lib.ps1')
. (Join-Path $here 'UpdatePaths.Lib.ps1')

Initialize-Suite -Root $Root
$flag = Join-Path $Root 'results\done.flag'
Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
$final = 'ok'
try {
    Write-SuiteLog 'Update phase starting.'
    switch ($Scenario) {
        'update.over-running-previous' { Invoke-UpdateScenario -CandidateSetup $CandidateSetup -CandidateVersion $CandidateVersion }
        'update.in-app' { Invoke-UpdateInApp -CandidateVersion $CandidateVersion }
        'update.apply-on-quit' { Invoke-UpdateApplyOnQuit -CandidateVersion $CandidateVersion }
        'update.apply-at-start' { Invoke-UpdateApplyAtStart -CandidateVersion $CandidateVersion }
        'update.corrupt-package' { Invoke-UpdateCorruptPackage -CandidateVersion $CandidateVersion }
        'update.setup-interactive' { Invoke-UpdateSetupInteractive -CandidateSetup $CandidateSetup -CandidateVersion $CandidateVersion }
        'update.setup-over-stopped' { Invoke-UpdateSetupOverStopped -CandidateSetup $CandidateSetup -CandidateVersion $CandidateVersion }
        'update.service-mode' { Invoke-UpdateServiceMode -CandidateSetup $CandidateSetup -CandidateVersion $CandidateVersion }
        'uninstall.keeps-data' { Invoke-UninstallKeepsData }
        'update.tray-menu' { Invoke-UpdateTrayMenu -CandidateVersion $CandidateVersion }
        default { throw "Unknown update scenario '$Scenario'." }
    }
}
catch { $final = Hide-Secrets $_.Exception.Message; Write-SuiteLog "UPDATE PHASE ERROR $final" }
finally {
    Save-Results
    Set-Content -Path $flag -Value $final -Encoding utf8
    Write-SuiteLog "Update phase finished: $final"
}
