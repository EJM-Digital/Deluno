<#
    update.over-running-previous (#1256): the PREVIOUS release is installed silently from its GitHub Release asset and started the way a
    person starts it; it is given state; the candidate's Setup (the installer this run's windows-installer job built) runs silently over
    it while it runs. From then on nothing in the scenario starts Deluno: it must come back on its own as the candidate, with its download
    clients, libraries and Activity, and its start must go cleanly. The scenario itself is the golden path's own (Update.Lib.ps1,
    Invoke-UpdateScenario), run unchanged; this leg only does what the host script did before it (install the previous release).
#>
function Invoke-LegUpdate {
    param([Parameter(Mandatory)] [string] $Setup, [Parameter(Mandatory)] [string] $Version, [Parameter(Mandatory)] [string] $PreviousSetup, [Parameter(Mandatory)] [string] $PreviousVersion)
    Initialize-RunnerDesktop

    $began = Get-Date
    $prev = Start-Process -FilePath $PreviousSetup -ArgumentList '--silent' -PassThru
    $finished = $prev.WaitForExit(600000)
    $prevExit = if ($finished) { $prev.ExitCode } else { $null }
    Write-SuiteLog ("Previous release {0}: Setup --silent finished {1}, exit {2}, {3} s." -f $PreviousVersion, $finished, $prevExit, [int]((Get-Date) - $began).TotalSeconds)
    # A silent Setup does not start Deluno: it is started as a person starts it, in the runner's desktop session.
    if ($finished -and $prevExit -eq 0) { Start-DelunoApp }

    Invoke-UpdateScenario -CandidateSetup $Setup -CandidateVersion $Version
    Add-LateChecks -Id 'update.over-running-previous' -Body {
        Add-Check "The previous release's Setup.exe --silent finished with exit 0" ($finished -and $prevExit -eq 0) "version $PreviousVersion; exit $prevExit"
    }
    Save-RunnerScreen 'after-update'
}
