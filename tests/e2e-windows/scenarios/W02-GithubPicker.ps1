<#
    github-limit.picker (#1181): GitHub's anonymous allowance is used up on this machine BEFORE Setup runs, so the first-run picker meets
    the limit, and it must still install Weir through github.com's release feed (releases.atom): the install log names Weir's installer
    and its checksum, never says the limit stopped it, and the installed Weir is one of the newest releases the feed lists.

    The golden path's host script ran this on a clean VM of its own after the groups (Invoke-ScenarioSuite.ps1, Invoke-GithubPickerPhase);
    the judging is the same function (Get-GithubPickerChecks, scripts/golden-path/suite/SuiteHost.Lib.ps1). The runner's address is
    GitHub's own network, so the allowance is the runner's own anonymous one; when GitHub allows more than the 70 calls the suite will
    spend, the scenario says "not applicable" with the reason, as the VM's did.
#>
function Invoke-LegGithubPicker {
    param([Parameter(Mandatory)] [string] $Setup, [string] $Commit = '')
    Initialize-RunnerDesktop
    $pickerId = 'github-limit.picker'
    # Before Setup runs: anonymous calls until GitHub says none are left (at most 70), kept for the judge.
    $limit = Use-GitHubLimitUp -MaxCalls 70
    Save-Evidence -Name 'github-limit-before-install.json' -Text ($limit | ConvertTo-Json -Compress)
    Write-SuiteLog ("GitHub allowance used up before Setup: reachable {0}, calls {1}, remaining {2}." -f $limit.Reachable, $limit.Calls, $limit.Remaining)

    $install = $null
    if ($limit.Reachable) { $install = Install-CandidateWithPicker -Setup $Setup }
    Save-RunnerScreen 'github-picker-after'

    Invoke-Scenario -Id $pickerId -Title "With GitHub's hourly limit used up, the first-run picker still installs Weir through github.com's release feed" -Body {
        $hookPath = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $hookLog = if (Test-Path $hookPath) { Get-Content -Raw $hookPath } else { '' }
        $pickerLog = if (Test-Path $script:PickerLog) { Get-Content -Raw $script:PickerLog } else { '' }
        $weir = Get-WeirExe
        $weirVersion = if ($weir) { $weir.VersionInfo.ProductVersion } else { '' }
        $tags = @()
        try {
            $atom = (Invoke-WebRequest -Uri 'https://github.com/EJM-Digital/Weir/releases.atom' -UseBasicParsing -TimeoutSec 40).Content
            $tags = @([regex]::Matches($atom, '<link[^>]*href="[^"]*/releases/tag/([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
        } catch { }
        $after = Get-GitHubLimitState
        Save-Evidence -Name 'picker.log' -Text $pickerLog
        Save-Evidence -Name 'install-hook.log' -Text $hookLog
        Add-ChecksFrom (Get-GithubPickerChecks -Limit $limit -HookLog $hookLog -PickerLog $pickerLog -WeirVersion $weirVersion -FeedTags $tags)
        if ($limit.Reachable) {
            $exe = Get-InstalledDelunoExe
            $build = if ($exe) { $exe.VersionInfo.ProductVersion } else { '' }
            if ($Commit) { Add-Check 'The installed build is the commit under test' (Test-BuildMatchesCommit -ProductVersion $build -Commit $Commit) "Deluno.exe reports '$build'; commit $Commit" }
            Add-Check 'Setup.exe (not silent) ran to its end' ($install -and $install.SetupExit -eq 0) $(if ($install) { "exit $($install.SetupExit)" } else { 'the install did not run' })
        }
        Add-Note ("Allowance after the install: {0} left." -f $after.Remaining)
        Add-Note ("Weir's release feed lists (newest first): {0}." -f ((@($tags) | Select-Object -First 5) -join ', '))
        $hookLines = @($hookLog -split "`r?`n" | Where-Object { $_ -match 'Weir' } | Select-Object -Last 12)
        if ($hookLines.Count -gt 0) { Add-Note ("Install log, Weir lines: " + ($hookLines -join ' || ')) }
    }
}
