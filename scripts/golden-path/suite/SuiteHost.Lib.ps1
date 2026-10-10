<#
    Host-side helpers for Invoke-ScenarioSuite.ps1: judging the results, writing the results file and building the golden-path
    commit status. Nothing here talks to the VM or to GitHub, so Test-ScenarioSuite.ps1 can check all of it offline.
    Windows PowerShell 5.1.
#>

# The final status of every planned scenario: what the VM reported, or "error" with "never reported" when it did not.
# Reported is the list from results.json; Plan comes from Get-ScenarioPlan; Groups limits the plan (empty = all).
function Merge-SuiteResults {
    param([object[]] $Plan, [object[]] $Reported, [string[]] $Groups = @())
    $byId = @{}
    foreach ($r in @($Reported)) { if ($r -and $r.id) { $byId[[string]$r.id] = $r } }
    $out = New-Object System.Collections.ArrayList
    foreach ($g in $Plan) {
        if ($Groups.Count -gt 0 -and $g.Name -notin $Groups) { continue }
        foreach ($id in $g.Ids) {
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
        [string] $RecordedStatus, [string[]] $Warnings = @()
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
    if (-not $Verdict.FullRun) { $l.Add('- This was not a full run (some groups were left out), so it can never record a pass.') }
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
