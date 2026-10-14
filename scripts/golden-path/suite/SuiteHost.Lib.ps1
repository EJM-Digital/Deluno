<#
    Host-side helpers for Invoke-ScenarioSuite.ps1: judging the results, writing the results file and building the golden-path
    commit status. Nothing here talks to the VM or to GitHub, so Test-ScenarioSuite.ps1 can check all of it offline.
    Windows PowerShell 5.1.
#>

# The final status of every planned scenario: what the VM reported, or "error" with "never reported" when it did not.
# Reported is the list from results.json; Plan comes from Get-ScenarioPlan; Groups or Scenarios limit the plan (empty = all).
function Merge-SuiteResults {
    param([object[]] $Plan, [object[]] $Reported, [string[]] $Groups = @(), [string[]] $Scenarios = @())
    $byId = @{}
    foreach ($r in @($Reported)) { if ($r -and $r.id) { $byId[[string]$r.id] = $r } }
    $out = New-Object System.Collections.ArrayList
    foreach ($g in $Plan) {
        if ($Groups.Count -gt 0 -and $g.Name -notin $Groups) { continue }
        foreach ($id in $g.Ids) {
            if ($Scenarios.Count -gt 0 -and $id -notin $Scenarios) { continue }
            if ($byId.ContainsKey($id)) {
                $r = $byId[$id]
                $status = if ($r.status -in 'pass', 'fail', 'error') { [string]$r.status } else { 'error' }
                $err = if ($r.status -eq 'running') { 'The scenario was still running when the suite stopped.' } elseif ($r.status -notin 'pass', 'fail', 'error') { "Unknown status '$($r.status)'." } else { [string]$r.error }
                [void]$out.Add([pscustomobject]@{ Group = $g.Name; Id = $id; Title = [string]$r.title; Status = $status; Seconds = $r.seconds; Error = $err; Checks = @($r.checks); Notes = @($r.notes) })
            }
            else {
                [void]$out.Add([pscustomobject]@{ Group = $g.Name; Id = $id; Title = ''; Status = 'error'; Seconds = 0; Error = 'The scenario never reported (its group stopped before it, or the run ended).'; Checks = @(); Notes = @() })
            }
        }
    }
    # A group that broke as a whole reports as group.<name>; keep those too so the cause shows.
    foreach ($r in @($Reported)) {
        if ($r -and "$($r.id)" -like 'group.*') {
            [void]$out.Add([pscustomobject]@{ Group = ("$($r.id)" -replace '^group\.', ''); Id = [string]$r.id; Title = [string]$r.title; Status = 'error'; Seconds = $r.seconds; Error = [string]$r.error; Checks = @(); Notes = @() })
        }
    }
    @($out)
}

# pass only when every planned scenario passed, the harness had no failed line, the installed build is the commit under test,
# and the whole plan ran (a run limited to some groups can fail but never pass).
function Get-SuiteVerdict {
    param([object[]] $Merged, [object[]] $HarnessFailures = @(), [bool] $BuildMatches = $true, [bool] $FullRun = $true)
    $failed = @($Merged | Where-Object { $_.Status -ne 'pass' })
    $state = if ($failed.Count -eq 0 -and @($HarnessFailures).Count -eq 0 -and $BuildMatches -and $FullRun) { 'success' } else { 'failure' }
    [pscustomobject]@{
        State = $state; Passed = @($Merged | Where-Object { $_.Status -eq 'pass' }).Count; Total = @($Merged).Count
        Failed = @($failed | ForEach-Object { $_.Id }); HarnessFailures = @($HarnessFailures); BuildMatches = $BuildMatches; FullRun = $FullRun
    }
}

# The status description GitHub shows: at most 140 characters, and on a failure it names the scenarios that failed.
function Get-StatusDescription {
    param([Parameter(Mandatory)] $Verdict, [string] $Version = '')
    if ($Verdict.State -eq 'success') { return "Scenario suite passed: $($Verdict.Passed) of $($Verdict.Total) on $Version" }
    $names = New-Object System.Collections.ArrayList
    if (-not $Verdict.BuildMatches) { [void]$names.Add('installed build is not this commit') }
    foreach ($h in @($Verdict.HarnessFailures)) { [void]$names.Add('install') }
    if (-not $Verdict.FullRun) { [void]$names.Add('not a full run') }
    foreach ($f in @($Verdict.Failed)) { [void]$names.Add($f) }
    $head = "Scenario suite failed ($($Verdict.Failed.Count) of $($Verdict.Total)): "
    $list = (@($names | Select-Object -Unique) -join ', ')
    $text = $head + $list
    if ($text.Length -gt 140) { $text = $text.Substring(0, 137) + '...' }
    $text
}

# The gh arguments that record the status. The context is the one the release gate reads.
function Get-StatusArguments {
    param([Parameter(Mandatory)] [string] $Repository, [Parameter(Mandatory)] [string] $Sha, [Parameter(Mandatory)] $Verdict, [string] $Version = '', [string] $TargetUrl = '')
    $a = @('api', "repos/$Repository/statuses/$Sha", '-f', "state=$($Verdict.State)", '-f', 'context=golden-path', '-f', "description=$(Get-StatusDescription -Verdict $Verdict -Version $Version)")
    if ($TargetUrl) { $a += @('-f', "target_url=$TargetUrl") }
    $a
}

# The harness writes a markdown table: | Step | Result | Detail |. Returns its rows.
function Read-HarnessRows {
    param([Parameter(Mandatory)] [string] $File)
    if (-not (Test-Path $File)) { return @() }
    $rows = New-Object System.Collections.ArrayList
    foreach ($line in Get-Content $File) {
        if ($line -match '^\|\s*(.+?)\s*\|\s*(passed|failed|manual)\s*\|\s*(.*?)\s*\|\s*$') {
            [void]$rows.Add([pscustomobject]@{ Step = $Matches[1]; Status = $Matches[2]; Detail = $Matches[3] })
        }
    }
    @($rows)
}

# Does the installed build belong to the commit under test? Deluno.exe's ProductVersion reads "<version>+<full sha>".
function Test-BuildMatchesCommit {
    param([string] $ProductVersion, [Parameter(Mandatory)] [string] $Commit)
    if (-not $ProductVersion -or $ProductVersion -notmatch '\+([0-9a-fA-F]{7,40})') { return $false }
    $built = $Matches[1].ToLowerInvariant()
    $want = $Commit.ToLowerInvariant()
    $n = [math]::Min($built.Length, $want.Length)
    $n -ge 7 -and $built.Substring(0, $n) -eq $want.Substring(0, $n)
}

function ConvertTo-MdCell { param([string] $Text) (($Text -replace '\|', '/') -replace '\r?\n', ' ') }

function New-ResultsMarkdown {
    param(
        [Parameter(Mandatory)] $Verdict, [Parameter(Mandatory)] [object[]] $Merged, [object[]] $HarnessRows = @(),
        [string] $Version, [string] $Commit, [string] $InstallerSha256, [string] $InstalledBuild, [double] $Minutes, [bool] $DryRun,
        [string] $RecordedStatus, [string[]] $Warnings = @(), [string[]] $Notes = @(), [string] $Coverage = ''
    )
    $l = New-Object System.Collections.Generic.List[string]
    $l.Add("# Golden path scenario suite $Version")
    $l.Add('')
    $l.Add("**Verdict: $($Verdict.State.ToUpperInvariant())** - $($Verdict.Passed) of $($Verdict.Total) scenarios passed in $([math]::Round($Minutes, 1)) minutes.")
    $l.Add('')
    $l.Add("- Commit under test: ``$Commit``")
    $l.Add("- Installed build: ``$InstalledBuild`` (matches the commit: $($Verdict.BuildMatches))")
    $l.Add("- Installer SHA256: ``$InstallerSha256``")
    $l.Add("- Golden-path commit status: $RecordedStatus$(if ($DryRun) { ' (dry run: nothing was recorded)' })")
    if ($Verdict.Failed.Count -gt 0) { $l.Add("- Failed or not reported: $($Verdict.Failed -join ', ')") }
    if (-not $Verdict.FullRun) { $l.Add('- This was not a full run' + $(if ($Coverage) { " ($Coverage)" } else { ' (some groups or scenarios were left out)' }) + ', so it can never record a pass.') }
    foreach ($n in $Notes) { $l.Add("- Note: $n") }
    foreach ($w in $Warnings) { $l.Add("- Warning: $w") }
    $l.Add('')
    $l.Add('## Scenarios')
    $l.Add('')
    $l.Add('| Scenario | Result | Seconds | What it proves | Evidence |')
    $l.Add('| --- | --- | ---: | --- | --- |')
    foreach ($m in $Merged) {
        $evidence = if ($m.Error) { "ERROR: $($m.Error)" } else {
            $bad = @($m.Checks | Where-Object { -not $_.ok })
            if ($bad.Count -gt 0) { ($bad | ForEach-Object { "FAILED: $($_.name) ($($_.detail))" }) -join '; ' }
            else { "$(@($m.Checks).Count) checks passed" }
        }
        $l.Add("| ``$($m.Id)`` | $($m.Status) | $($m.Seconds) | $(ConvertTo-MdCell $m.Title) | $(ConvertTo-MdCell $evidence) |")
    }
    $l.Add('')
    $l.Add('## Every check, in order')
    foreach ($m in $Merged) {
        $l.Add('')
        $l.Add("### $($m.Id) - $($m.Status)")
        if ($m.Error) { $l.Add("- ERROR: $(ConvertTo-MdCell $m.Error)") }
        foreach ($c in @($m.Checks)) { $l.Add("- $(if ($c.ok) { 'ok' } else { 'FAILED' }): $(ConvertTo-MdCell $c.name) - $(ConvertTo-MdCell $c.detail)") }
        foreach ($n in @($m.Notes)) { $l.Add("- note: $(ConvertTo-MdCell $n)") }
    }
    if (@($HarnessRows).Count -gt 0) {
        $l.Add('')
        $l.Add('## Install and checks run by the harness (Invoke-GoldenPath.ps1)')
        $l.Add('')
        $l.Add('| Step | Result | Detail |')
        $l.Add('| --- | --- | --- |')
        foreach ($h in $HarnessRows) { $l.Add("| $(ConvertTo-MdCell $h.Step) | $($h.Status) | $(ConvertTo-MdCell $h.Detail) |") }
    }
    $l -join "`n"
}

# ---------------------------------------------------------------- the "ready" checkpoint (fast start for partial runs)

# Hyper-V checkpoint names: the version and the commit, so a rebuild of the same version on another commit never reuses it.
function Get-ReadyCheckpointName {
    param([Parameter(Mandatory)] [string] $Version, [Parameter(Mandatory)] [string] $Commit, [string] $Override = '')
    if ($Override) { return $Override }
    $short = $Commit.ToLowerInvariant()
    if ($short.Length -gt 8) { $short = $short.Substring(0, 8) }
    ("ready-{0}-{1}" -f $Version, $short) -replace '[^A-Za-z0-9._-]', '-'
}

# Why a ready checkpoint may not be used for this run, or $null when it may. A full run always installs from clean; the install
# scenario reads the install itself; the after-setup hook runs on the machine as Setup left it; -SkipInstall uses the VM as it is.
function Get-ReadyCheckpointProblem {
    param([Parameter(Mandatory)] $Scope, [bool] $AfterSetupScript = $false, [bool] $SkipInstall = $false)
    if ($Scope.FullRun) { return 'A full run always installs from the clean checkpoint, so the install scenario stays covered; -UseReadyCheckpoint is for runs of single scenarios or groups.' }
    if ('install-first-run' -in @($Scope.ChosenIds)) { return 'install-first-run reads the install itself (the tray while the picker ran), so it needs a fresh install, not a ready checkpoint.' }
    if ($AfterSetupScript) { return '-AfterSetupScript runs between the setup and the groups of a fresh install; it cannot be used with a ready checkpoint.' }
    if ($SkipInstall) { return '-SkipInstall uses the VM as it is; it cannot be used with a ready checkpoint.' }
    $null
}

# What the user is told about the run's coverage in the results file.
function Get-RunCoverageText {
    param([Parameter(Mandatory)] $Scope)
    if ($Scope.FullRun) { return 'all scenarios' }
    if (@($Scope.Selection).Count -gt 0) { return ('only these scenarios: ' + (@($Scope.Selection) -join ', ')) }
    'only some groups'
}

# ---------------------------------------------------------------- host-side scenarios (the update phase and the GitHub picker phase)

# A scenario the host judged itself, in the shape the VM runner reports (id, title, status, seconds, error, notes, checks), so it merges
# into the results like any other. A scenario with an error is an error; one with a failed check fails; one with no checks is an error.
function New-HostScenarioRecord {
    param([Parameter(Mandatory)] [string] $Id, [Parameter(Mandatory)] [string] $Title, [object[]] $Checks = @(), [string[]] $Notes = @(), [double] $Seconds = 0, [string] $ErrorText = '')
    $failed = @($Checks | Where-Object { -not $_.ok })
    $status = if ($ErrorText) { 'error' } elseif ($failed.Count -gt 0) { 'fail' } elseif (@($Checks).Count -eq 0) { 'error' } else { 'pass' }
    if ($status -eq 'error' -and -not $ErrorText) { $ErrorText = 'The scenario ran no checks.' }
    [pscustomobject]@{ id = $Id; title = $Title; status = $status; seconds = [math]::Round($Seconds, 1); error = $(if ($ErrorText) { $ErrorText } else { $null }); notes = @($Notes); checks = @($Checks) }
}

# The first three numbers of a version or a tag (a tag with a leading v and a release-candidate suffix, a four-part file version and a build suffix all read as the same three numbers), to compare an installed
# program's version with a release feed's tags.
function Get-VersionCore {
    param([string] $Text)
    $t = ([string]$Text).Trim() -replace '^[vV]', ''
    $m = [regex]::Match($t, '^(\d+)\.(\d+)\.(\d+)')
    if ($m.Success) { return ('{0}.{1}.{2}' -f [int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value) }
    ''
}

# What the GitHub picker scenario asserts, from what the VM kept (github-limit.picker, #1181). -Limit is what Use-GitHubLimitUp returned
# before Setup ran (null when nothing was kept). When the limit could not be used up from the VM's address, the scenario is "not
# applicable" and says why, and passes with that one line, which is the owner's rule for it.
function Get-GithubPickerChecks {
    param($Limit, [string] $HookLog = '', [string] $PickerLog = '', [string] $WeirVersion = '', [string[]] $FeedTags = @())
    $checks = New-Object System.Collections.ArrayList
    $add = { param($n, $ok, $d) [void]$checks.Add([pscustomobject]@{ name = $n; ok = [bool]$ok; detail = [string]$d }) }
    if ($null -eq $Limit) {
        & $add 'The VM used GitHub''s allowance up before Setup ran' $false 'The hook that does it before Setup left no record, so the picker did not meet the limit.'
        return @($checks)
    }
    if (-not $Limit.Reachable) {
        & $add 'Not applicable: the limit could not be used up from this address' $true ([string]$Limit.Reason)
        return @($checks)
    }
    & $add 'GitHub''s anonymous allowance was used up from the VM before Setup ran (0 calls left)' ($Limit.Remaining -eq 0) ("{0} call(s) made; reset {1}" -f $Limit.Calls, $Limit.ResetUtc)
    & $add 'Weir is installed (through the picker)' (-not [string]::IsNullOrWhiteSpace($WeirVersion)) $(if ($WeirVersion) { "Weir.exe $WeirVersion" } else { 'Weir.exe not found' })
    & $add 'The install log names Weir''s installer and its sha256' ($HookLog -match 'Weir: installer .* sha256 [0-9A-Fa-f]{64}') ''
    & $add 'The install log says the checksum matches the vendor''s' ($HookLog -match 'Weir: SHA-256 matches') ''
    & $add 'The install log never says GitHub''s limit stopped Weir (it was found without the API)' ($HookLog -notmatch 'Weir: GitHub''s anonymous limit is used up') ''
    & $add 'The install log has no "could not find Weir''s latest release"' ($HookLog -notmatch 'Weir: .*[Cc]ould not find') ''
    $core = Get-VersionCore $WeirVersion
    $top = @($FeedTags | Select-Object -First 3 | ForEach-Object { Get-VersionCore $_ } | Where-Object { $_ })
    & $add 'The installed Weir is one of the newest releases github.com''s release feed lists (the release used through the feed)' ($core -and ($core -in $top)) "installed $WeirVersion; feed newest: $((@($FeedTags) | Select-Object -First 3) -join ', ')"
    & $add 'The picker reached its finish screen' ($PickerLog -match 'final: .*Finished\.') ''
    @($checks)
}
