<#
.SYNOPSIS
    Runs one leg of the Windows end-to-end suite on a GitHub `windows-latest` runner (docs/exec-plans/active/PLAN-E2E-IN-CI.md, step 3).

.DESCRIPTION
    Each leg needs a clean machine, so each is a job of its own in the `e2e-windows` matrix of .github/workflows/ci.yml:

      install       install-first-run, outage.tray   the candidate's Setup with the component picker; the tray; Weir quitting and returning
      githubpicker  github-limit.picker              the picker with GitHub's anonymous allowance used up first
      update        update.over-running-previous     the previous release, then the candidate's Setup over it while it runs

    Results are written in the golden path's JSON shape (results\results.json: startedUtc, updatedUtc, scenarios[id, title, status,
    startedUtc, seconds, checks[name, ok, detail, atUtc], notes, error]) with evidence beside it, and the exit code is 0 only when every
    scenario of the leg passed. A scenario the leg owns that never reported is an error.

    Windows PowerShell 5.1, in the runner's own signed-in desktop session. Never run this on a workstation: it installs Deluno.
.PARAMETER Leg
    install, githubpicker or update.
.PARAMETER Candidate
    The candidate's Setup.exe, or the folder the windows-installer artifact was downloaded into.
.PARAMETER CandidateVersion
    The version Directory.Build.props carries (what the installer was built as).
.PARAMETER Commit
    The commit under test; the installed Deluno.exe must report it (<version>+<sha>).
.PARAMETER Previous
    The previous release's Setup.exe, or its folder (update leg).
.PARAMETER PreviousVersion
    The previous release's version (update leg).
.PARAMETER Root
    Where results, logs and evidence go.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('install', 'githubpicker', 'update')] [string] $Leg,
    [Parameter(Mandatory)] [string] $Candidate,
    [Parameter(Mandatory)] [string] $CandidateVersion,
    [string] $Commit = '',
    [string] $Previous = '',
    [string] $PreviousVersion = '',
    [string] $Root = 'C:\e2e-windows'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here 'Windows.Lib.ps1')
foreach ($f in Get-ChildItem (Join-Path $here 'scenarios') -Filter 'W*.ps1' | Sort-Object Name) { . $f.FullName }

Initialize-Suite -Root $Root
$flag = Join-Path $Root 'results\done.flag'
Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue
$final = 'ok'
try {
    Write-SuiteLog "Windows end-to-end leg '$Leg' starting (candidate $CandidateVersion)."
    $setup = Resolve-SetupExe -Path $Candidate
    switch ($Leg) {
        'install' { Invoke-LegInstall -Setup $setup -Commit $Commit }
        'githubpicker' { Invoke-LegGithubPicker -Setup $setup -Commit $Commit }
        'update' {
            if (-not $Previous -or -not $PreviousVersion) { throw 'The update leg needs -Previous (the previous release''s Setup.exe) and -PreviousVersion.' }
            Invoke-LegUpdate -Setup $setup -Version $CandidateVersion -PreviousSetup (Resolve-SetupExe -Path $Previous) -PreviousVersion $PreviousVersion
        }
    }
}
catch { $final = Hide-Secrets $_.Exception.Message; Write-SuiteLog "LEG ERROR $final" }
finally {
    Assert-LegReported -Leg $Leg
    Save-Results
    Set-Content -Path $flag -Value $final -Encoding utf8
    Write-SuiteLog "Windows end-to-end leg '$Leg' finished: $final"
}
$summary = @($script:S.Results | ForEach-Object { "{0} {1}" -f $_.id, $_.status })
Write-Host ("Result: " + ($summary -join '; '))
exit (Get-LegExitCode)
