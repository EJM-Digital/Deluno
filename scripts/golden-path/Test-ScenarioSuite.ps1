<#
.SYNOPSIS
    Checks the scenario suite's own code offline, without the VM: every script parses on Windows PowerShell 5.1, the plan and the
    group files agree, the host's judging and status code behave, the Deluno/Weir client code works against a mock server, the news
    stub's hang list works, and no address or machine name is written into a script.

.DESCRIPTION
    Run it after any change under scripts/golden-path. `npm run ci:check` runs it. It needs nothing installed beyond Windows
    PowerShell 5.1; the Edge DevTools check is skipped when Edge is not installed. Exit code 1 when any check fails.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$suite = Join-Path $root 'suite'
$script:failures = 0
$script:passes = 0
function Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    if ($Ok) { $script:passes++; Write-Host "  ok    $Name" }
    else { $script:failures++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}

Write-Host 'Every script parses'
foreach ($f in Get-ChildItem -Path $root -Recurse -Filter '*.ps1') {
    $errs = $null; $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errs)
    Check ("parses: " + $f.FullName.Substring($root.Length + 1)) ($errs.Count -eq 0) (($errs | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; ')
}

Write-Host 'No stored script block reads a variable from its defining function (the $base / $sess bug)'
# PowerShell resolves a script block's free variables when the block is RUN, from the caller's scope, not from the function that
# made it. A block that is stored or handed back (assigned, returned, put in a hashtable or array) must therefore read only its own
# params, variables it assigns itself, scope-qualified names ($script:, $global:, $env:, $using:) and automatic variables, or be
# closed with .GetNewClosure(). A block passed straight to a command (Invoke-Command, ForEach-Object, -Body...) runs in place and
# is exempt. Allow-list entries are 'file|function|variable' = the reason why the read is safe.
$script:storedBlockAllow = @{
}
function Get-StoredBlockHazards([string] $Path, [string] $Text) {
    $errs = $null; $tokens = $null
    $ast = if ($Text) { [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errs) }
           else { [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errs) }
    $automatic = @('_', 'psitem', 'args', 'input', 'true', 'false', 'null', 'psscriptroot', 'pscommandpath', 'matches', 'lastexitcode', 'error',
        'this', 'psboundparameters', 'pscmdlet', 'myinvocation', 'host', 'pid', 'home', 'pwd', 'pshome', 'profile', 'foreach', 'switch', 'event',
        'sender', 'eventargs', 'stacktrace', 'executioncontext', 'verbosepreference', 'erroractionpreference', 'warningpreference',
        'progresspreference', 'debugpreference', 'informationpreference', 'confirmpreference', 'whatifpreference', 'outputencoding', 'ofs', '?')
    $lang = 'System.Management.Automation.Language.'
    $isVar = { param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }
    $blocks = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }, $true)
    foreach ($b in $blocks) {
        # Where does the block go? Walk up through wrappers and containers to the thing that holds it: an assignment, a return or a
        # statement's output means stored; a command argument (a calculated property in Select-Object too) or a method call on it
        # (.GetNewClosure(), .Invoke()) means run in place or closed.
        $p = $b.Parent; $stored = $false
        while ($p) {
            if ($p -is [System.Management.Automation.Language.ParenExpressionAst] -or $p -is [System.Management.Automation.Language.ConvertExpressionAst] -or $p -is [System.Management.Automation.Language.CommandExpressionAst] -or $p -is [System.Management.Automation.Language.UnaryExpressionAst] -or
                $p -is [System.Management.Automation.Language.HashtableAst] -or $p -is [System.Management.Automation.Language.ArrayLiteralAst] -or $p -is [System.Management.Automation.Language.ArrayExpressionAst]) {
                $next = $p.Parent
                if ($p -is [System.Management.Automation.Language.HashtableAst] -or $p -is [System.Management.Automation.Language.ArrayLiteralAst] -or $p -is [System.Management.Automation.Language.ArrayExpressionAst]) {
                    $stored = $true   # inside a container: stored unless the container is itself a command argument (decided below)
                    if ($next -is [System.Management.Automation.Language.AssignmentStatementAst] -or $next -is [System.Management.Automation.Language.ReturnStatementAst]) { break }
                }
                $p = $next; continue
            }
            if ($p -is [System.Management.Automation.Language.PipelineAst]) {
                if ($p.PipelineElements.Count -eq 1) {
                    $g = $p.Parent
                    if ($g -is [System.Management.Automation.Language.HashtableAst]) { $p = $g; continue }
                    if ($g -is [System.Management.Automation.Language.StatementBlockAst] -and ($g.Parent -is [System.Management.Automation.Language.ArrayExpressionAst] -or $g.Parent -is [System.Management.Automation.Language.SubExpressionAst])) { $p = $g.Parent; continue }
                    $stored = ($g -is [System.Management.Automation.Language.StatementBlockAst]) -or ($g -is [System.Management.Automation.Language.NamedBlockAst]) -or ($g -is [System.Management.Automation.Language.ReturnStatementAst]) -or $stored
                }
                else { $stored = $false }
                break
            }
            if ($p -is [System.Management.Automation.Language.AssignmentStatementAst] -or $p -is [System.Management.Automation.Language.ReturnStatementAst]) { $stored = $true }
            elseif ($p -is [System.Management.Automation.Language.CommandAst] -or $p -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -or $p -is [System.Management.Automation.Language.MemberExpressionAst]) { $stored = $false }
            break
        }
        if (-not $stored) { continue }
        $fn = $b.Parent; while ($fn -and $fn -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $fn = $fn.Parent }
        $fnName = if ($fn) { $fn.Name } else { '(script)' }
        $known = @{}
        foreach ($pa in $b.FindAll({ param($n) $n -is [System.Management.Automation.Language.ParameterAst] }, $true)) { $known[$pa.Name.VariablePath.UserPath.ToLowerInvariant()] = $true }
        foreach ($as in $b.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
            foreach ($v in $as.Left.FindAll($isVar, $true)) { $known[$v.VariablePath.UserPath.ToLowerInvariant()] = $true }
        }
        foreach ($fe in $b.FindAll({ param($n) $n -is [System.Management.Automation.Language.ForEachStatementAst] }, $true)) { $known[$fe.Variable.VariablePath.UserPath.ToLowerInvariant()] = $true }
        foreach ($v in $b.FindAll($isVar, $true)) {
            $name = $v.VariablePath.UserPath
            if ($name.Contains(':')) { continue }
            $lc = $name.ToLowerInvariant()
            if ($known.ContainsKey($lc) -or $automatic -contains $lc) { continue }
            [pscustomobject]@{ File = $Path; Line = $v.Extent.StartLineNumber; Function = $fnName; Variable = $name }
        }
    }
}
$repoRoot = (Resolve-Path (Join-Path $root '..\..')).Path
$hazards = @()
foreach ($f in Get-ChildItem -Path (Join-Path $repoRoot 'scripts') -Recurse -Filter '*.ps1') {
    $rel = $f.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
    $hazards += @(Get-StoredBlockHazards $rel $null)
}
$hazards = @($hazards | Where-Object { -not $script:storedBlockAllow.ContainsKey("$($_.File)|$($_.Function)|$($_.Variable)") })
Check 'no stored or returned script block reads a variable it does not define' ($hazards.Count -eq 0) (($hazards | Sort-Object File, Line -Unique | ForEach-Object { "$($_.File):$($_.Line) in $($_.Function) reads `$$($_.Variable)" }) -join '; ')
$bugSnippet = 'function Open-X { $base = ''x''; $call = { Invoke-RestMethod -Uri $base }; $call }'
$cleanSnippets = @(
    'function Open-X { $base = ''x''; $call = { param($Uri) Invoke-RestMethod -Uri $Uri }; $call }',
    'function Open-X { $base = ''x''; $call = { Invoke-RestMethod -Uri $base }.GetNewClosure(); $call }',
    'function Open-X { $base = ''x''; $call = { Invoke-RestMethod -Uri $script:base }; $call }',
    'function Open-X { $call = { $u = ''y''; Invoke-RestMethod -Uri $u; foreach ($i in 1..2) { $i } }; $call }',
    'function Open-X { $base = ''x''; Invoke-Command -ScriptBlock { Invoke-RestMethod -Uri $base }; 1..2 | ForEach-Object { $base + $_ } }')
$escapeSnippets = @(
    'function Open-X { $base = ''x''; @{ Call = { Invoke-RestMethod -Uri $base } } }',
    'function Open-X { $base = ''x''; return { $base } }',
    'function Open-X { $base = ''x''; $list = @({ $base }) }',
    'function Open-X { $base = ''x''; $o.Call = { $base } }')
$bugHits = @(Get-StoredBlockHazards 'bug.ps1' $bugSnippet)
Check 'self-test: the original bug (a returned block reading the function''s $base) is flagged' ($bugHits.Count -eq 1 -and $bugHits[0].Variable -eq 'base')
for ($i = 0; $i -lt $cleanSnippets.Count; $i++) {
    Check "self-test: fixed shape $($i + 1) passes" (@(Get-StoredBlockHazards 'snippet.ps1' $cleanSnippets[$i]).Count -eq 0) $cleanSnippets[$i]
}
for ($i = 0; $i -lt $escapeSnippets.Count; $i++) {
    Check "self-test: escaping shape $($i + 1) (hashtable, return, array, property) is flagged" (@(Get-StoredBlockHazards 'snippet.ps1' $escapeSnippets[$i]).Count -eq 1) $escapeSnippets[$i]
}

Write-Host 'No address or machine name is written into a script'
$addressPattern = '\b(?!127\.0\.0\.1\b|0\.0\.0\.0\b|192\.168\.1\.10\b)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b'
foreach ($f in Get-ChildItem -Path $root -Recurse -Filter '*.ps1') {
    if ($f.Name -eq 'Test-ScenarioSuite.ps1') { continue }
    $text = Get-Content -Raw $f.FullName
    $hits = [regex]::Matches($text, $addressPattern) | Where-Object { $_.Value -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $true } | Where-Object { $_.Value -notmatch '^(1\.0\.0\.\d+|2\.0\.0\.\d+)$' }
    # A dotted number is only an address when it is four numbers and not a version; versions here are 1.0.0.x at most.
    $real = @($hits | Where-Object { $_.Value -notmatch '^\d+\.0\.0\.\d+$' })
    Check ("no IP address in " + $f.Name) ($real.Count -eq 0) (($real | ForEach-Object { $_.Value }) -join ', ')
}

Write-Host 'The plan and the group files agree'
. (Join-Path $suite 'ScenarioPlan.ps1')
. (Join-Path $suite 'SuiteHost.Lib.ps1')
$plan = @(Get-ScenarioPlan)
$allIds = @($plan | ForEach-Object { $_.Ids })
Check 'scenario ids are unique' (($allIds | Select-Object -Unique).Count -eq $allIds.Count)
Check 'there are scenarios for every client in the issue' (@($allIds | Where-Object { $_ -like 'film.*' }).Count -ge 5)
$groupText = (Get-ChildItem (Join-Path $suite 'vm\scenarios') -Filter 'G*.ps1' | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
foreach ($g in @($plan | Where-Object { -not $_.Host })) {
    Check "group function $($g.Function) is defined" ($groupText -match ('function\s+' + [regex]::Escape($g.Function) + '\b'))
}
foreach ($id in @($plan | Where-Object { -not $_.Host } | ForEach-Object { $_.Ids })) {
    $prefix = ($id -replace '\.(qbittorrent|transmission|deluge|sabnzbd|nzbget)', '.$kind' -replace '\.(qbittorrent|transmission|deluge|sabnzbd|nzbget)$', '')
    $stem = ($id -split '\.')[0]
    Check "an Invoke-Scenario for '$id' exists in the group files" ($groupText -match ("-Id\s+[`"']" + [regex]::Escape($stem))) $stem
}
$topics = @(Get-ScenarioTopics)
Check 'every group says what it proves' (@($plan | Where-Object { $_.Name -notin $topics.Group }).Count -eq 0)

Write-Host 'The update phase (#1256)'
$updateGroup = @($plan | Where-Object { $_.Name -eq 'update' })[0]
Check 'the plan has the update scenario, run by the host' ($allIds -contains 'update.over-running-previous' -and $updateGroup.Host)
Check 'the update group is the last one in the plan' ($plan[-1].Name -eq 'update')
Check 'the existing scenario ids are unchanged' ((@('install-first-run', 'film.qbittorrent', 'film.extra-left-out', 'remove-copy.deluge.file', 'resend.during.sabnzbd', 'stuck.peers.transmission', 'outage.inflight', 'tryagain.nzbget', 'vanish.waiting.deluge', 'problems.dismiss-everywhere', 'activity.one-story') | Where-Object { $_ -notin $allIds }).Count -eq 0)
$runSuiteText = Get-Content -Raw (Join-Path $suite 'vm\Run-Suite.ps1')
Check 'the VM runner leaves host groups to the host' ($runSuiteText -match '\$group\.Host')
Check 'a group error carries the script stack trace into the log and the results, so the next error names its line' ($runSuiteText -match '(?s)catch \{.*ScriptStackTrace.*GROUP ERROR.*error = \$msg')
Write-Host 'The single-file takeover scenario (#1262)'
Check 'the plan has resend.single-file.takeover in the resend group, and a pattern for one client does not pick it' ((@($plan | Where-Object { $_.Name -eq 'resend' })[0].Ids -contains 'resend.single-file.takeover') -and (@(Resolve-ScenarioSelection -Scenarios 'resend.*.qbittorrent' -Plan $plan) -notcontains 'resend.single-file.takeover'))
Check 'resend.single-file.takeover can be chosen alone' ((@(Resolve-ScenarioSelection -Scenarios 'resend.single-file.takeover' -Plan $plan) -join ',') -eq 'resend.single-file.takeover')
$takeoverText = Get-Content -Raw (Join-Path $suite 'vm\scenarios\G04-Resend.ps1')
Check 'the takeover scenario asks Weir for its own answer, checks the path Deluno imported from, and Deluno''s record of finding the file' ($takeoverText -match 'Get-WeirIntakeStatus' -and $takeoverText -match 'Get-ExpectedProcessorOutput' -and $takeoverText -match 'found the cleaned file')

Check 'the takeover scenario accepts either contract: Weir names the path (Deluno imports from it) or names none (layout fallback, and Deluno says so), and records which branch ran' ($takeoverText -match 'weirNamesPath' -and $takeoverText -match 'output path Weir named' -and $takeoverText -match 'the branch run is' -and $takeoverText -notmatch 'names no output path"')
Check 'the VM runner loads the shared reading of the secret Deluno shares with Weir' ($runSuiteText -match 'DelunoSecrets\.Lib\.ps1')
$hostScript = Get-Content -Raw (Join-Path $root 'Invoke-ScenarioSuite.ps1')
$iAfterSuite = $hostScript.IndexOf('& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $AfterSuiteScript')
$iPhaseCall = $hostScript.IndexOf('Invoke-UpdatePhase)')
$iVerdict = $hostScript.IndexOf('$verdict = Get-SuiteVerdict')
$iLogs = $hostScript.IndexOf('Collecting the logs failed')
Check 'the update phase runs after the groups, the log collection and the -AfterSuiteScript hook' ($iLogs -gt 0 -and $iAfterSuite -gt $iLogs -and $iPhaseCall -gt $iAfterSuite)
Check 'the update phase is recorded before the verdict, so the full-run verdict includes it' ($iPhaseCall -gt 0 -and $iVerdict -gt $iPhaseCall)
$phaseBody = $hostScript.Substring($hostScript.IndexOf('function Invoke-UpdatePhase'))
$phaseBody = $phaseBody.Substring(0, $phaseBody.IndexOf("`nif (`$runUpdate)"))
$iHarness = $phaseBody.IndexOf('Invoke-GoldenPath.ps1')
Check 'the update phase restores the checkpoint first, through the harness, with the PREVIOUS release''s installer' ($iHarness -gt 0 -and $phaseBody -match '-InstallerPath \$PreviousInstallerPath' -and $phaseBody -match '-Checkpoint \$Checkpoint')
Check 'the harness installs the previous release silently (not interactively)' ($phaseBody.Substring($iHarness, 700) -notmatch '-Interactive')
Check 'the candidate''s Setup is copied only after that, to a folder of its own' ($phaseBody.IndexOf('Copy-FileToVm -Session $rigU -LocalPath $InstallerPath') -gt $iHarness -and $phaseBody -match 'C:\\golden\\candidate')
Check 'the update phase never installs the candidate or the previous release over the other from the host' (@([regex]::Matches($phaseBody, 'Invoke-GoldenPath\.ps1')).Count -eq 1 -and $phaseBody -notmatch '-InstallerPath \$InstallerPath')
Check 'a missing previous installer fails the phase plainly (and so the verdict)' ($phaseBody -match 'if \(-not \$PreviousInstallerPath\)' -and $phaseBody -match 'nothing waives it')
Check 'the host takes -PreviousInstallerPath and -PreviousVersion' ($hostScript.Contains('[string] $PreviousInstallerPath') -and $hostScript.Contains('[string] $PreviousVersion'))
$updateLib = Get-Content -Raw (Join-Path $suite 'vm\Update.Lib.ps1')
$runUpdate = Get-Content -Raw (Join-Path $suite 'vm\Run-Update.ps1')
Check 'the VM side runs the candidate''s Setup silently over the running previous release' ($updateLib -match "Start-Process -FilePath \`$CandidateSetup -ArgumentList '--silent'")
$afterSetup = $updateLib.Substring($updateLib.IndexOf('# The update: the candidate'))
Check 'nothing starts Deluno by hand after Setup runs' ($afterSetup -notmatch 'Start-ScheduledTask' -and $afterSetup -notmatch 'Register-ScheduledTask' -and $afterSetup -notmatch 'Start-Process\s+-FilePath\s+\$(exe|installed|nowExe)' -and $updateLib -notmatch 'Start-DelunoInDesktop')
Check 'the VM side holds no previous installer: it cannot install it over anything' ($updateLib -notmatch 'previous\\Deluno-Setup' -and $runUpdate -notmatch 'previous\\Deluno-Setup')
Check 'it tells "never started again" from "started again but never answered"' ($afterSetup -match 'never started again after Setup' -and $afterSetup -match 'started again but never answered')
Check 'it requires a new process, answering, as the candidate' ($afterSetup -match 'notin \$oldPids' -and $afterSetup -match 'CandidateVersion')
Check 'it reads the start marker and the start log and fails a retried start' ($afterSetup -match 'Read-StartMarker' -and $afterSetup -match "phase -eq 'ready'" -and $afterSetup -match 'Deluno started on try')
Check 'it gives the previous release a library, a download client and Activity, and compares them after' ($updateLib -match '/api/libraries' -and $updateLib -match '/api/download-clients' -and $afterSetup -match 'Clients' -and $afterSetup -match 'Libraries' -and $afterSetup -match 'Activity')
foreach ($f in 'Update.Lib.ps1', 'Run-Update.ps1') {
    $perr = $null; $ptok = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $suite "vm\$f"), [ref]$ptok, [ref]$perr)
    Check "$f parses on this PowerShell" ($perr.Count -eq 0)
}
Check 'the new start-log lines are picked by their timestamp, not by counting the old log''s lines' ($afterSetup -match 'Get-StartLogSince' -and $updateLib -notmatch 'Select-Object -Skip \$markLine')
# rc.30.28: the new build wrote a fresh tray-startup.log; skipping the old log's 7 lines lost "is starting".
$startLogFile = Join-Path ([IO.Path]::GetTempPath()) ('startlog-' + [guid]::NewGuid().ToString('N') + '.log')
@('2026-10-13T08:40:00.0000000-07:00 a line from the previous release',
  '2026-10-13T08:48:47.2631564-07:00 Deluno 1.0.0-rc.30.28 is starting (process 2164, try 1).',
  '2026-10-13T08:49:06.1954632-07:00 Deluno started on port 7879',
  '  Data root: C:\data',
  '2026-10-13T08:49:06.2075891-07:00 [info] Deluno started on its first try.') | Set-Content -Path $startLogFile -Encoding utf8
. ([scriptblock]::Create(($updateLib -split "`n" | Out-String)))
$picked = @(Get-StartLogSince -StartLog $startLogFile -Since ([DateTimeOffset]::Parse('2026-10-13T08:48:30-07:00').UtcDateTime))
Check 'Get-StartLogSince keeps the new lines (with their continuation lines) and drops the old ones' ($picked.Count -eq 4 -and ($picked -match 'is starting') -and -not ($picked -match 'previous release') -and ($picked -match 'Data root'))
Remove-Item -LiteralPath $startLogFile -ErrorAction SilentlyContinue
$noUpdate = @(Merge-SuiteResults -Plan $plan -Reported @() -Groups @('update'))
Check 'an update phase that never reported is an error in the merged results' ($noUpdate.Count -eq 1 -and $noUpdate[0].Status -eq 'error')
$updateFailed = [pscustomobject]@{ id = 'update.over-running-previous'; title = 't'; status = 'fail'; seconds = 0; error = $null; checks = @([pscustomobject]@{ name = 'x'; ok = $false; detail = 'd' }); notes = @() }
$withUpdate = @(Merge-SuiteResults -Plan $plan -Reported @($updateFailed))
Check 'a failed update phase fails the full-run verdict' ((Get-SuiteVerdict -Merged $withUpdate -FullRun $true).State -eq 'failure' -and ((Get-SuiteVerdict -Merged $withUpdate -FullRun $true).Failed -contains 'update.over-running-previous'))

Write-Host 'The host judges results and builds the status'
$reported = @(
    [pscustomobject]@{ id = 'install-first-run'; title = 't'; status = 'pass'; seconds = 3; error = $null; checks = @([pscustomobject]@{ name = 'a'; ok = $true; detail = '' }); notes = @() }
    [pscustomobject]@{ id = 'film.qbittorrent'; title = 't'; status = 'fail'; seconds = 9; error = $null; checks = @([pscustomobject]@{ name = 'b'; ok = $false; detail = 'bad' }); notes = @() }
    [pscustomobject]@{ id = 'film.sabnzbd'; title = 't'; status = 'running'; seconds = 0; error = $null; checks = @(); notes = @() }
    [pscustomobject]@{ id = 'group.removal'; title = 'The removal group stopped'; status = 'error'; seconds = 0; error = 'boom'; checks = @(); notes = @() }
)
$merged = @(Merge-SuiteResults -Plan $plan -Reported $reported)
$byId = @{}; foreach ($m in $merged) { $byId[$m.Id] = $m }
Check 'a passing scenario stays pass' ($byId['install-first-run'].Status -eq 'pass')
Check 'a failing scenario stays fail' ($byId['film.qbittorrent'].Status -eq 'fail')
Check 'a scenario still running when the suite stopped is an error' ($byId['film.sabnzbd'].Status -eq 'error' -and $byId['film.sabnzbd'].Error -match 'still running')
Check 'a scenario that never reported is an error' ($byId['film.nzbget'].Status -eq 'error' -and $byId['film.nzbget'].Error -match 'never reported')
Check 'a group that stopped shows up under its own name' ($null -ne $byId['group.removal'])
Check 'every planned scenario is in the merged list' (@($allIds | Where-Object { -not $byId.ContainsKey($_) }).Count -eq 0)
$limited = @(Merge-SuiteResults -Plan $plan -Reported $reported -Groups @('install'))
Check 'a group filter limits the plan' (@($limited | Where-Object { $_.Group -ne 'install' -and $_.Id -notlike 'group.*' }).Count -eq 0)

$allPass = @($allIds | ForEach-Object { [pscustomobject]@{ Group = 'x'; Id = $_; Title = ''; Status = 'pass'; Seconds = 1; Error = ''; Checks = @(); Notes = @() } })
$ok = Get-SuiteVerdict -Merged $allPass
Check 'all scenarios passing is a success' ($ok.State -eq 'success' -and $ok.Passed -eq $allIds.Count)
Check 'a harness failure stops a success' ((Get-SuiteVerdict -Merged $allPass -HarnessFailures @('Component picker')).State -eq 'failure')
Check 'a build that is not the commit stops a success' ((Get-SuiteVerdict -Merged $allPass -BuildMatches $false).State -eq 'failure')
Check 'a partial run can never be a success' ((Get-SuiteVerdict -Merged $allPass -FullRun $false).State -eq 'failure')
$bad = Get-SuiteVerdict -Merged $merged
Check 'one failing scenario makes a failure' ($bad.State -eq 'failure' -and $bad.Failed -contains 'film.qbittorrent')
Check 'the success description is under 140 characters' ((Get-StatusDescription -Verdict $ok -Version '1.0.0-rc.30.21').Length -le 140)
$longFail = Get-SuiteVerdict -Merged @($allIds | ForEach-Object { [pscustomobject]@{ Group = 'x'; Id = $_; Title = ''; Status = 'fail'; Seconds = 1; Error = ''; Checks = @(); Notes = @() } })
$desc = Get-StatusDescription -Verdict $longFail
Check 'a failure description is cut to 140 characters' ($desc.Length -le 140 -and $desc -like 'Scenario suite failed*') "$($desc.Length)"
Check 'a failure description names the failed scenarios' ((Get-StatusDescription -Verdict $bad) -match 'film\.qbittorrent')
$args1 = Get-StatusArguments -Repository 'owner/repo' -Sha ('a' * 40) -Verdict $ok -Version '1.0.0-rc.30.21'
Check 'the status goes to the golden-path context on the commit' (($args1 -join ' ') -match 'repos/owner/repo/statuses/a{40}' -and ($args1 -contains 'context=golden-path') -and ($args1 -contains 'state=success'))
Check 'a failure is recorded as state=failure' ((Get-StatusArguments -Repository 'o/r' -Sha ('b' * 40) -Verdict $bad) -contains 'state=failure')
Check 'target_url is added only when given' (-not (($args1 -join ' ') -match 'target_url') -and ((Get-StatusArguments -Repository 'o/r' -Sha ('b' * 40) -Verdict $ok -TargetUrl 'https://example.test/r') -contains 'target_url=https://example.test/r'))
Check 'the installed build must carry the commit' (Test-BuildMatchesCommit -ProductVersion '1.0.0-rc.30.21+387500fdc1c2d3e4' -Commit '387500fdc1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6')
Check 'a different commit is refused' (-not (Test-BuildMatchesCommit -ProductVersion '1.0.0-rc.30.21+387500fd8f1d' -Commit '16f256f6c1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6'))
Check 'a build with no commit in its version is refused' (-not (Test-BuildMatchesCommit -ProductVersion '1.0.0-rc.30.21' -Commit '387500fd'))
$md = New-ResultsMarkdown -Verdict $bad -Merged $merged -Version 'v' -Commit 'c' -InstallerSha256 's' -InstalledBuild 'b' -Minutes 12.3 -DryRun $true -RecordedStatus 'x'
Check 'the results file names the verdict, every scenario and the failed check' ($md -match 'Verdict: FAILURE' -and $md -match 'film\.qbittorrent' -and $md -match 'FAILED: b \(bad\)' -and $md -match 'dry run')
$rows = @(Read-HarnessRows -File (Join-Path ([IO.Path]::GetTempPath()) 'nothing-here.md'))
Check 'a missing harness file reads as no rows' ($rows.Count -eq 0)

Write-Host 'Single scenarios (-Scenarios): every id can be chosen alone (#1238)'
$vmIds = @($plan | Where-Object { -not $_.Host } | ForEach-Object { $_.Ids })
Check 'a list of ids picks them in plan order, once each' ((@(Resolve-ScenarioSelection -Scenarios @('film.nzbget,film.qbittorrent', 'film.nzbget') -Plan $plan) -join ',') -eq 'film.qbittorrent,film.nzbget')
Check 'a pattern picks every match' ((@(Resolve-ScenarioSelection -Scenarios 'resend.*.sabnzbd' -Plan $plan) -join ',') -eq 'resend.before.sabnzbd,resend.during.sabnzbd,resend.after.sabnzbd')
$typo = ''
try { Resolve-ScenarioSelection -Scenarios 'film.qbitorrent' -Plan $plan | Out-Null } catch { $typo = $_.Exception.Message }
Check 'an unknown id is an error that names near ids' ($typo -match 'Unknown scenario' -and $typo -match 'film\.qbittorrent')
Check 'no -Scenarios means everything' ((Get-RunScope -Plan $plan -Groups @() -Scenarios @()).FullRun)
$bothErr = ''
try { Get-RunScope -Plan $plan -Groups @('films') -Scenarios @('film.deluge') | Out-Null } catch { $bothErr = $_.Exception.Message }
Check '-Groups and -Scenarios together are refused' ($bothErr -match 'not both')
$notAlone = @()
foreach ($id in $allIds) {
    $s = Get-RunScope -Plan $plan -Groups @() -Scenarios @($id)
    $isHost = [bool]($plan | Where-Object { $_.Host -and $_.Ids -contains $id })
    if (-not ((@($s.ChosenIds) -join ',') -eq $id -and -not $s.FullRun -and $s.NeedVm -eq (-not $isHost) -and $s.RunUpdate -eq ($id -eq 'update.over-running-previous') -and $s.RunGithubPicker -eq ($id -eq 'github-limit.picker'))) { $notAlone += $id }
}
Check "every planned scenario id ($($allIds.Count)) can be chosen alone, and only the VM ones need the VM" ($notAlone.Count -eq 0) ($notAlone -join ', ')
Check 'a run of the update scenario alone needs no VM stage' (-not (Get-RunScope -Plan $plan -Scenarios @('update.over-running-previous')).NeedVm)
Check 'a run of groups still lists their ids' ((@((Get-RunScope -Plan $plan -Groups @('outage')).ChosenIds) -join ',') -eq 'outage.idle,outage.inflight')
$onlyOne = @(Merge-SuiteResults -Plan $plan -Reported @() -Scenarios @('film.deluge', 'stuck.zero.sabnzbd'))
Check 'the results of a scenario run list only the chosen scenarios (a missing one is an error)' ($onlyOne.Count -eq 2 -and @($onlyOne | Where-Object { $_.Status -ne 'error' }).Count -eq 0)
Check 'a run of a scenario never records a pass' ((Get-SuiteVerdict -Merged @([pscustomobject]@{ Group = 'films'; Id = 'film.deluge'; Title = ''; Status = 'pass'; Seconds = 1; Error = ''; Checks = @(); Notes = @() }) -FullRun $false).State -eq 'failure')

# Every group function run with one id chosen: only that scenario runs, and only its own setup (the films it needs) is done. The
# group code runs against stand-ins for everything that touches Deluno, the clients or Weir, which only write down what was asked.
& {   # a scope of its own, so the stand-ins below do not outlive these checks
. (Join-Path $suite 'vm\scenarios\Common.ps1')
foreach ($f in Get-ChildItem (Join-Path $suite 'vm\scenarios') -Filter 'G*.ps1' | Sort-Object Name) { . $f.FullName }
$script:Trace = New-Object System.Collections.ArrayList
$script:Made = New-Object System.Collections.ArrayList
function Invoke-Scenario { param([string] $Id, [string] $Title, [scriptblock] $Body) if (-not (Test-ScenarioSelected -Id $Id)) { return }; [void]$script:Trace.Add("run:$Id") }
function New-FilmFor {
    param([string] $Kind, [string] $Layout, [string] $Variant, [string] $Size, [switch] $Extra, [string] $Label, [switch] $Slow, [switch] $HangLast, [switch] $HangAll)
    $f = [pscustomobject]@{ Kind = $Kind; Label = $Label; Release = "R.$Label.$Kind"; Title = "T $Label $Kind"; Protocol = $(if (Test-IsTorrent $Kind) { 'torrent' } else { 'usenet' }); ClientKind = $Kind; SentUtc = [DateTime]::UtcNow }
    [void]$script:Trace.Add("film:$Kind"); [void]$script:Made.Add($f); $f
}
function Send-Film { param($Film, [string] $Kind) [void]$script:Trace.Add("send:$Kind"); $Film }
function Write-SuiteLog { param($Text) }
function Hide-Secrets { param($Text) $Text }
function Start-Sleep { param($Seconds, $Milliseconds) } # reason: replaces the real sleep in the harness test, so the unit under test runs at once
function Invoke-Deluno {
    param($Method, $Path, $Body, $TimeoutSec)
    # The keep-seeding group's setup that reaches Deluno is written down, so a run of one scenario can be shown to do only its own.
    if ($Method -eq 'POST' -and $Path -eq '/api/indexers') { [void]$script:Trace.Add('ks:private-indexer'); return [pscustomobject]@{ id = 'ix1'; privacy = 'private' } }
    if ($Method -eq 'PUT' -and $Path -eq '/api/download-health/keep-seeding') { [void]$script:Trace.Add('ks:choices') }
    if ($Method -eq 'POST' -and $Path -eq '/api/download-health/keep-seeding/check') { return [pscustomobject]@{ status = 'ready'; message = '' } }
    if ($Method -eq 'PUT' -and $Path -like '/api/download-health/rules/*') { [void]$script:Trace.Add('ks:rules-put') }
    @()
}
if ($null -eq $script:Fx) { $script:Fx = @{} }
$script:Fx.FeedPort = 1; $script:Fx.IndexerIds = @{}
function Add-Note { param($Text) }
function Get-ClientByKind { param([string] $Kind) [pscustomobject]@{ id = "c-$Kind"; name = $Kind; kind = $Kind } }
function New-KsClientCategory { param([string] $Kind, [string] $Name) [void]$script:Trace.Add("ks:category:$Kind") }
function Get-ActivityText { param($PageSize, $MaxPages) @() }
function Wait-KsAllStruck { param($Films, $TimeoutSec) $h = @{}; foreach ($k in $Films.Keys) { $h[$k] = @{ Message = 'x'; Seconds = 1 } }; $h }
function Set-HealthRules { param($On, $CheckMinutes) [void]$script:Trace.Add('rules') }
function Wait-AllStruck { param($Films, $TimeoutSec) $h = @{}; foreach ($k in $Films.Keys) { $h[$k] = @{ Line = 'x'; Seconds = 1; MaxProgress = 0.0; Statuses = @{}; LastRow = $null } }; $h }
function Set-WeirPause { param($Paused) [void]$script:Trace.Add("pause:$Paused") }
function Stop-WeirApp { [void]$script:Trace.Add('weir:stop'); [pscustomobject]@{ GoneAt = [DateTime]::UtcNow; ViaTray = $true } }
function Start-WeirApp { [void]$script:Trace.Add('weir:start') }
function Test-WeirAnswering { $true }
function Wait-FilmFinished { param($Film, $TimeoutSec) [pscustomobject]@{ Ok = $true } }
function Wait-Until { param($Condition, $TimeoutSec, $IntervalSec) [pscustomobject]@{ Ok = $true; Seconds = 0; Value = [pscustomobject]@{ id = 'h1'; status = 'failed' } } }
function Find-Handoffs { param($Name) @([pscustomobject]@{ id = 'h2'; status = 'completed'; releaseName = $Name }) }
function Get-FilmHandoff { param($Film) [pscustomobject]@{ id = 'h1'; status = 'source-gone'; failureMessage = 'gone' } }
function Remove-FilmDownload { param($Film, $Reason) $null }
function Start-AgainDownload { param($Film) }
function Remove-FilmFiles { param($Film) 0 }
function Start-AutomationPause { [void]$script:Trace.Add('automation:pause') }
function Stop-AutomationPause { [void]$script:Trace.Add('automation:resume') }
function Get-TelemetryRows { param([switch] $IncludeHistory) @($script:Made | ForEach-Object { [pscustomobject]@{ releaseName = $_.Release; title = $_.Title; status = 'filesGone' } }) }
function Find-QueueRow { param($Name, [switch] $IncludeHistory) $null }
function Save-Evidence { param($Name, $Text) }
function Start-Edge { }
function New-CdpPage { param($Url) 'page' }
function Close-CdpPage { param($Page) }
function Open-OutagePages { [void]$script:Trace.Add('pages:open') }
function Close-OutagePages { }
function Restore-WeirAfterOutage { }

function Get-ExpectedSetupKinds([string] $Id) {
    if ($Id -eq 'film.extra-left-out') { return @('qbittorrent') }
    if ($Id -match '^keepseeding\.(moved|public-removed)\.(?<k>[a-z]+)$') { return @($Matches['k']) }
    if ($Id -match '^(film|resend\.before|stuck\.(zero|short|peers|slow)|tryagain|vanish\.(before|waiting))\.(?<k>[a-z]+)$') { return @($Matches['k']) }
    @()
}
$groupFunctions = @($plan | Where-Object { -not $_.Host })
$badRuns = @(); $badSetup = @(); $threw = @()
foreach ($g in $groupFunctions) {
    foreach ($id in $g.Ids) {
        Set-ScenarioSelection -Ids @($id)
        $script:Trace.Clear(); $script:Made.Clear()
        try { & $g.Function } catch { $threw += "$id ($($_.Exception.Message))" }
        $ran = @($script:Trace | Where-Object { $_ -like 'run:*' } | ForEach-Object { $_.Substring(4) })
        if (($ran -join ',') -ne $id) { $badRuns += "$id ran [$($ran -join ',')]" }
        $sent = @($script:Trace | Where-Object { $_ -like 'send:*' } | ForEach-Object { $_.Substring(5) })
        $films = @($script:Trace | Where-Object { $_ -like 'film:*' } | ForEach-Object { $_.Substring(5) })
        $want = @(Get-ExpectedSetupKinds $id)
        if (($sent -join ',') -ne ($want -join ',') -or ($films -join ',') -ne ($want -join ',')) { $badSetup += "$id sent [$($sent -join ',')] made [$($films -join ',')] wanted [$($want -join ',')]" }
    }
}
Set-ScenarioSelection -Ids @()
Check "each of the $($vmIds.Count) scenarios, chosen alone, runs only itself" ($badRuns.Count -eq 0 -and $threw.Count -eq 0) (($badRuns + $threw) -join '; ')
Check 'chosen alone, a scenario sends only the film(s) its own setup needs' ($badSetup.Count -eq 0) ($badSetup -join '; ')
# The keep-seeding group (#1240): each id chosen alone runs only its own setup. A "moved" scenario adds the private indexer, makes the
# category in its own client only and saves the keep-seeding choices; a "public-removed" one does none of that. Both set the rule.
$ksBad = @()
foreach ($id in @($allIds | Where-Object { $_ -like 'keepseeding.*' })) {
    Set-ScenarioSelection -Ids @($id)
    $script:Trace.Clear(); $script:Made.Clear()
    try { Invoke-GroupKeepSeeding } catch { $ksBad += "$id threw $($_.Exception.Message)" }
    $kind = ($id -split '\.')[-1]
    $setup = @($script:Trace | Where-Object { $_ -like 'ks:*' -or $_ -eq 'rules' })
    $wantSetup = if ($id -like 'keepseeding.moved.*') { @('ks:private-indexer', "ks:category:$kind", 'ks:choices', 'rules', 'ks:rules-put', 'ks:choices') } else { @('rules', 'ks:rules-put') }
    # The restore at the end writes the choices back once more, so a moved run has 'ks:choices' twice.
    $wantSetup = @($wantSetup | Sort-Object)
    if ((@($setup | Sort-Object) -join ',') -ne ($wantSetup -join ',')) { $ksBad += "$id did [$($setup -join ',')] wanted [$($wantSetup -join ',')]" }
}
Set-ScenarioSelection -Ids @('keepseeding.moved.deluge', 'keepseeding.public-removed.qbittorrent')
$script:Trace.Clear(); $script:Made.Clear(); Invoke-GroupKeepSeeding
$ksTwo = @($script:Trace | Where-Object { $_ -like 'send:*' -or $_ -like 'ks:category:*' } | ForEach-Object { $_ } | Sort-Object)
if (($ksTwo -join ',') -ne 'ks:category:deluge,send:deluge,send:qbittorrent') { $ksBad += "two chosen did [$($ksTwo -join ',')]" }
Set-ScenarioSelection -Ids @('film.deluge')
$script:Trace.Clear(); Invoke-GroupKeepSeeding
if ($script:Trace.Count -ne 0) { $ksBad += "with no keep-seeding scenario chosen the group did [$($script:Trace -join ',')]" }
Set-ScenarioSelection -Ids @()
Check 'each keep-seeding scenario chosen alone sets up only its own client, film, indexer and choices; none chosen does nothing' ($ksBad.Count -eq 0) ($ksBad -join '; ')
# Choosing nothing runs the whole group as before, with the same sends.
$sendsFull = @{ films = 5; removal = 0; resend = 5; stuck = 15; keepseeding = 6; tryagain = 5; vanish = 10; outage = 0; problems = 0; install = 0; nzbgetsetup = 0; seasonpack = 0; layout = 0; github = 0 }
$fullBad = @()
foreach ($g in $groupFunctions) {
    $script:Trace.Clear(); $script:Made.Clear()
    try { & $g.Function } catch { $fullBad += "$($g.Name) threw $($_.Exception.Message)" }
    $ran = @($script:Trace | Where-Object { $_ -like 'run:*' } | ForEach-Object { $_.Substring(4) })
    $sends = @($script:Trace | Where-Object { $_ -like 'send:*' }).Count
    if ((@($ran | Sort-Object) -join ',') -ne (@($g.Ids | Sort-Object) -join ',')) { $fullBad += "$($g.Name) ran [$($ran -join ',')]" }
    if ($sends -ne $sendsFull[$g.Name]) { $fullBad += "$($g.Name) sent $sends films, expected $($sendsFull[$g.Name])" }
}
Check 'with nothing chosen every group runs all its scenarios and sends the same films as before' ($fullBad.Count -eq 0) ($fullBad -join '; ')
# Two scenarios of one group share one setup window and each reads only its own film.
Set-ScenarioSelection -Ids @('resend.before.sabnzbd', 'resend.before.qbittorrent')
$script:Trace.Clear(); $script:Made.Clear(); Invoke-GroupResend
$sentTwo = @($script:Trace | Where-Object { $_ -like 'send:*' } | ForEach-Object { $_.Substring(5) })
Check 'two chosen scenarios of a group share one window and send only their two films' ((($sentTwo | Sort-Object) -join ',') -eq 'qbittorrent,sabnzbd')
Set-ScenarioSelection -Ids @()
}
$vmRunner = Get-Content -Raw (Join-Path $suite 'vm\Run-Suite.ps1')
Check 'the VM runner takes -Scenarios, skips a group none of whose scenarios was chosen, and leaves Weir''s key to a full run' ($vmRunner -match '\[string\[\]\] \$Scenarios' -and $vmRunner -match 'Test-AnyScenarioSelected -Ids \$group\.Ids' -and $vmRunner -match '\$selected\.Count -eq 0 -and \(\$Groups')

Write-Host 'The ready checkpoint (a fast start for partial runs) (#1238)'
$scFull = Get-RunScope -Plan $plan
$scFilm = Get-RunScope -Plan $plan -Scenarios @('film.deluge')
$scInst = Get-RunScope -Plan $plan -Scenarios @('install-first-run', 'film.deluge')
$scInstGroup = Get-RunScope -Plan $plan -Groups @('install')
Check 'a full run may not use a ready checkpoint (it installs from clean)' ((Get-ReadyCheckpointProblem -Scope $scFull) -match 'full run always installs')
Check 'a run of one scenario may' ($null -eq (Get-ReadyCheckpointProblem -Scope $scFilm))
Check 'a run of one group may' ($null -eq (Get-ReadyCheckpointProblem -Scope (Get-RunScope -Plan $plan -Groups @('stuck'))))
Check 'install-first-run may not (chosen alone, with others, or as its group)' ((Get-ReadyCheckpointProblem -Scope $scInst) -match 'install' -and (Get-ReadyCheckpointProblem -Scope $scInstGroup) -match 'install')
Check 'the after-setup hook and -SkipInstall may not' ((Get-ReadyCheckpointProblem -Scope $scFilm -AfterSetupScript $true) -match 'AfterSetupScript' -and (Get-ReadyCheckpointProblem -Scope $scFilm -SkipInstall $true) -match 'SkipInstall')
$nameA = Get-ReadyCheckpointName -Version '1.0.0-rc.30.30' -Commit ('ade3e471' + 'f' * 32)
$nameB = Get-ReadyCheckpointName -Version '1.0.0-rc.30.30' -Commit ('bbbbbbbb' + 'f' * 32)
Check 'the checkpoint is named for the version and the commit, so a rebuild never reuses it' ($nameA -eq 'ready-1.0.0-rc.30.30-ade3e471' -and $nameA -ne $nameB)
Check 'a name can be given' ((Get-ReadyCheckpointName -Version 'v' -Commit 'abc' -Override 'ready-mine') -eq 'ready-mine')
$hostText = Get-Content -Raw (Join-Path $root 'Invoke-ScenarioSuite.ps1')
Check 'the host script has -Scenarios, -UseReadyCheckpoint and -ReadyCheckpoint' ($hostText -match '\[string\[\]\] \$Scenarios' -and $hostText -match '\[switch\] \$UseReadyCheckpoint' -and $hostText -match '\[string\] \$ReadyCheckpoint')
Check 'the ready checkpoint is restored and saved through Hyper-V cmdlets, as the clean one is' ($hostText -match 'Restore-VMSnapshot -VMName' -and $hostText -match 'Checkpoint-VM -Name' -and $hostText -match 'Get-VMSnapshot -VMName')
Check 'a restored ready checkpoint skips the harness (install and setup)' ($hostText -match 'if \(-not \$SkipInstall -and \$needVm -and -not \$readyRestored\)')
$iPrepare = $hostText.IndexOf("' -PrepareOnly'")
$iSave = $hostText.IndexOf('New-RigCheckpoint -Name $readyName')
Check 'a missing ready checkpoint is made after a normal install and setup, before the scenarios start' ($iPrepare -gt 0 -and $iSave -gt $iPrepare -and $iSave -lt $hostText.IndexOf("'GoldenSuiteRun'"))
Check 'the installed build is still checked against the commit after a restore' ($hostText.IndexOf('Test-BuildMatchesCommit -ProductVersion') -gt $hostText.IndexOf('if ($readyRestored)'))
Check 'only a full run records the status' ($hostText -match 'elseif \(\$fullRun\) \{\s+\$out = & gh')

Write-Host 'The events-request log is collected and says what is missing (#1238)'
$collect = Get-Content -Raw (Join-Path $suite 'vm\Collect-Logs.ps1')
$pat = [regex]::Match($collect, '\$eventsPattern = ''([^'']+)''').Groups[1].Value
Check 'the events pattern is in Collect-Logs' ($pat.Length -gt 0)
# The exact line ProcessorEventAnswerLog writes (src/Deluno.Platform/Processing/ProcessorEventAnswerLog.cs, Describe): the first with a
# reason, the second as the plain log file or the JSON log carries it, the third for a report already handled.
$answerLine409 = 'POST /api/integrations/processors/events answered 409 for hand-off 7f3a9c, report completed: The download was removed.'
$answerLineJson = (@{ category = 'Deluno.Platform.ProcessorEvents'; level = 'Information'; message = $answerLine409 } | ConvertTo-Json -Compress)
$answerLineHandled = 'POST /api/integrations/processors/events answered 200 (already handled) for hand-off 7f3a9c, report completed'
foreach ($line in $answerLine409, $answerLineJson, $answerLineHandled, '{"message":"POST /api/integrations/processors/events answered 200"}', '2026-10-14 Processors events: answered 200 for handoff', '{"message":"processors-events reply"}', 'GET \/api\/integrations\/processors\/events') {
    Check "the pattern finds: $line" ($line -match $pat)
}
Check 'the pattern does not take unrelated processor lines' (-not ('Processor connection test answered 200' -match $pat))
Check 'Collect-Logs searches the machine-wide and per-user data folders and says what was searched and what is missing' ($collect -match 'ProgramData' -and $collect -match 'processors-events-requests\.diag\.txt' -and $collect -match 'MISSING: no Deluno log file' -and $collect -match 'MISSING: Deluno''''s log has lines')
Check 'the host names what is missing when there are no events lines' ($hostText -match 'diagMissing' -and $hostText -match 'processors-events-requests\.diag\.txt')

if ($env:OS -ne 'Windows_NT') {
    Write-Host 'The VM-side checks need Windows (HttpListener, UI Automation, Edge): skipped here.'
    Write-Host ("{0} checks passed, {1} failed." -f $script:passes, $script:failures)
    if ($script:failures -gt 0) { exit 1 }
    exit 0
}

Write-Host 'The VM-side code works against a mock Deluno'
. (Join-Path $suite 'vm\Suite.Lib.ps1')
$port = Get-Random -Minimum 20000 -Maximum 29000
$mockScript = {
    param($p)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$p/")
    $l.Start()
    $logins = 0
    $weirLogins = 0
    while ($l.IsListening) {
        $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response
        $path = $req.Url.AbsolutePath; $auth = $req.Headers['Authorization']
        $code = 200; $body = '{}'
        if ($path -like '/api/v1/*') {
            # Weir's protocol middleware: a change that carries an Origin but no X-Requested-With is refused with 403.
            $xrw = $req.Headers['X-Requested-With']
            if ($path -eq '/api/v1/auth/csrf') { $body = '{"csrf_token":"c1"}' }
            elseif ($req.HttpMethod -ne 'GET' -and $xrw -ne 'XMLHttpRequest') { $code = 403; $body = '{"detail":"Missing X-Requested-With header."}' }
            elseif ($path -eq '/api/v1/auth/login') { $weirLogins++; $body = '{}' }
            elseif ($path -eq '/api/v1/pause') { if ($weirLogins -lt 2) { $code = 403; $body = '{"detail":"Forbidden."}' } else { $body = '{"paused":true}' } }
        }
        elseif ($path -eq '/api/auth/login') { $logins++; $body = '{"accessToken":"tok-' + $logins + '","user":{"name":"golden"}}' }
        elseif ($path -eq '/stop') { $body = 'bye'; $l.Stop() }
        elseif ($auth -ne ('Bearer tok-' + $logins) -and $path -ne '/api/auth/bootstrap-status') { $code = 401; $body = '{"message":"sign in"}' }
        elseif ($path -eq '/api/integrations/processors/handoffs') { $body = '[{"id":"h1","releaseName":"Gpfilm.2020.1080p","status":"completed","importJobId":"j1","createdUtc":"2026-10-11T10:00:00Z"},{"id":"h2","releaseName":"Gpfilm.2020.1080p","status":"failed","createdUtc":"2026-10-11T11:00:00Z"}]' }
        elseif ($path -eq '/api/api-keys') { $sr = New-Object IO.StreamReader($req.InputStream); $posted = $sr.ReadToEnd(); $body = '{"item":{},"apiKey":"key-ABCDEF123456","posted":' + ($posted | ConvertTo-Json) + '}' }
        elseif ($path -eq '/api/boom') { $code = 500; $body = '{"message":"it broke"}' }
        elseif ($path -eq '/api/echo') { $sr = New-Object IO.StreamReader($req.InputStream); $body = $sr.ReadToEnd() }
        elseif ($path -eq '/api/expire') { $code = 401; $body = '{}' }
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $res.StatusCode = $code; $res.ContentType = 'application/json'; $res.OutputStream.Write($bytes, 0, $bytes.Length); $res.Close()
    }
}
$ps = [powershell]::Create()
[void]$ps.AddScript($mockScript).AddArgument($port)
$async = $ps.BeginInvoke()
Start-Sleep -Milliseconds 700 # reason: lets the in-process mock listener start; no event to wait on
try {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('suite-test-' + [guid]::NewGuid().ToString('N'))
    Initialize-Suite -Root $tmp -DelunoUrl "http://127.0.0.1:$port" -WeirUrl "http://127.0.0.1:1"
    $pw = New-SuiteSecret 'deluno-password'
    Check 'a throwaway login is made and kept in a file' ((Test-Path (Join-Path $tmp 'secrets.json')) -and $pw.Length -ge 20)
    Check 'asking for the same secret again gives the same value' ((New-SuiteSecret 'deluno-password') -eq $pw)
    Check 'a secret is redacted from text' ((Hide-Secrets "the password is $pw ok") -notmatch [regex]::Escape($pw))
    Connect-Deluno
    Check 'signing in keeps the token' ($script:S.Token -eq 'tok-1')
    Check 'the token is redacted from text' ((Hide-Secrets 'Bearer tok-1') -notmatch 'tok-1')
    $h = @(Get-Handoffs)
    Check 'Get-Handoffs reads the list' ($h.Count -eq 2)
    $f = @(Find-Handoffs -Name 'Gpfilm')
    Check 'Find-Handoffs returns the newest first' ($f.Count -eq 2 -and $f[0].id -eq 'h2')
    $t = Try-Deluno -Path '/api/boom'
    Check 'Try-Deluno returns an HTTP error as a status, not an exception' ($t.Status -eq 500 -and $t.Data.message -eq 'it broke')
    $threw = $false; try { Invoke-Deluno -Path '/api/boom' | Out-Null } catch { $threw = $true }
    Check 'Invoke-Deluno throws on an HTTP error' $threw
    $e = Invoke-Deluno -Method POST -Path '/api/echo' -Body @{ a = 1; reason = 'reclaim' }
    Check 'a request body is sent as JSON' ($e.reason -eq 'reclaim')
    $w = Wait-Until -TimeoutSec 5 -IntervalSec 0.2 -Condition { $script:n++; if ($script:n -ge 3) { 'done' } else { $null } }
    $script:n = 0
    Check 'Wait-Until returns the value and the time' ($w.Ok -and $w.Value -eq 'done' -and $w.Seconds -ge 0.3 -and $w.Seconds -lt 3) "$($w.Seconds)"
    $w2 = Wait-Until -TimeoutSec 1 -IntervalSec 0.2 -Condition { $null }
    Check 'Wait-Until times out with Ok=false' ((-not $w2.Ok) -and $w2.Seconds -ge 1)

    Invoke-Scenario -Id 't.pass' -Title 'passes' -Body { Add-Check 'one' $true 'fine' }
    Invoke-Scenario -Id 't.fail' -Title 'fails' -Body { Add-Check 'one' $true ''; Add-Check 'two' $false 'wrong' }
    Invoke-Scenario -Id 't.error' -Title 'errors' -Body { throw "went wrong with $pw inside" }
    Invoke-Scenario -Id 't.empty' -Title 'no checks' -Body { }
    $res = @{}; foreach ($r in $script:S.Results) { $res[$r.id] = $r }
    Check 'a scenario whose checks pass is pass' ($res['t.pass'].status -eq 'pass')
    Check 'a scenario with a failed check is fail' ($res['t.fail'].status -eq 'fail')
    Check 'a scenario that throws is error, with the secret removed' ($res['t.error'].status -eq 'error' -and $res['t.error'].error -notmatch [regex]::Escape($pw))
    Check 'a scenario that throws records the script stack trace in its error' ($res['t.error'].error -match '\[stack: ')
    Check 'a scenario that checks nothing is an error, never a pass' ($res['t.empty'].status -eq 'error')
    $saved = Get-Content -Raw (Join-Path $tmp 'results\results.json') | ConvertFrom-Json
    Check 'results.json is written after each scenario' (@($saved.scenarios).Count -eq 4)
    # A scenario that was not chosen records nothing and its body is not run (-Scenarios).
    $script:BodyRan = $false
    Set-ScenarioSelection -Ids @('t.chosen')
    Invoke-Scenario -Id 't.other' -Title 'not chosen' -Body { $script:BodyRan = $true; Add-Check 'x' $true '' }
    Invoke-Scenario -Id 't.chosen' -Title 'chosen' -Body { Add-Check 'y' $true '' }
    Set-ScenarioSelection -Ids @()
    Check 'a scenario that was not chosen is not run and is not recorded; the chosen one is' (-not $script:BodyRan -and @($script:S.Results | Where-Object { $_.id -eq 't.other' }).Count -eq 0 -and @($script:S.Results | Where-Object { $_.id -eq 't.chosen' -and $_.status -eq 'pass' }).Count -eq 1)
    $logText = Get-Content -Raw (Join-Path $tmp 'logs\suite.log')
    Check 'the log never holds a login' ($logText -notmatch [regex]::Escape($pw))

    . (Join-Path $suite 'vm\Initialize-Environment.ps1')
    function Test-WeirAnswering { $true }
    $keyFile = Join-Path $tmp 'weir-key.txt'
    Publish-WeirScenarioKey -Path $keyFile
    $keyText = [IO.File]::ReadAllText($keyFile)
    Check 'the key file holds one line, the key and nothing else' ($keyText -eq "key-ABCDEF123456`n")
    Check 'the key is masked in the suite log' ((Get-Content -Raw (Join-Path $tmp 'logs\suite.log')) -notmatch 'key-ABCDEF123456')
    Check 'the key is masked in any text the suite writes' ((Hide-Secrets 'x key-ABCDEF123456 y') -notmatch 'key-ABCDEF123456')
    Check 'the key is registered so the collected logs are masked too' ((Get-Content -Raw (Join-Path $tmp 'secrets.json')) -match 'weir-scenario-key')
    $loginFile = Join-Path $tmp 'weir-login.txt'
    $threwNoConnect = ''; try { Publish-WeirScenarioLogin -Path $loginFile } catch { $threwNoConnect = $_.Exception.Message }
    Check 'with no Connect Weir the login is not handed over, and the reason says so' ($threwNoConnect -match 'Connect Weir did not run' -and -not (Test-Path $loginFile))
    New-SuiteSecret 'weir-username' | Out-Null; New-SuiteSecret 'weir-password' | Out-Null
    Check 'the Weir username is a made-up name, not a word that would hide ordinary text' ((Get-SuiteSecret 'weir-username') -match '^Gp-')
    Check 'both Weir secrets are masked in text' ((Hide-Secrets ((Get-SuiteSecret 'weir-username') + ' ' + (Get-SuiteSecret 'weir-password'))) -eq '*** ***')
    # Weir's Update phase runs after Deluno's setup and before any film: the login is handed over then, with nothing to follow yet.
    Set-Content -Path (Join-Path (Get-SuiteRoot) 'weir-connected.flag') -Value 'x'
    $realConnectWeir = ${function:Connect-Weir}; $realGetHandoffs = ${function:Get-Handoffs}
    function Connect-Weir { }
    function Get-Handoffs { @() }
    $threwNoFilm = ''; try { Publish-WeirScenarioLogin -Path $loginFile } catch { $threwNoFilm = $_.Exception.Message }
    Check 'after the groups, a login with no handed-off film left is refused' ($threwNoFilm -match 'No film' -and -not (Test-Path $loginFile))
    Publish-WeirScenarioLogin -Path $loginFile -BeforeAnyFilm
    Check 'before any film (-BeforeAnyFilm) the login is handed over: two lines, username then password' ([IO.File]::ReadAllText($loginFile) -eq ((Get-SuiteSecret 'weir-username') + "`n" + (Get-SuiteSecret 'weir-password') + "`n"))
    Remove-Item -LiteralPath $loginFile, (Join-Path (Get-SuiteRoot) 'weir-connected.flag') -ErrorAction SilentlyContinue
    Set-Item -Path function:Connect-Weir -Value $realConnectWeir; Set-Item -Path function:Get-Handoffs -Value $realGetHandoffs
    Check 'the setup-only pass hands Weir its login before any film and runs no group' ((Get-Content -Raw (Join-Path $suite 'vm\Run-Suite.ps1')) -match '(?s)if \(\$SetupOnly\) \{.*Publish-WeirScenarioLogin -BeforeAnyFilm.*return')
    . (Join-Path $suite 'vm\scenarios\G09-Problems.ps1')
    Check 'the story rules are defined' ($null -ne (Get-Command Get-StoryProblems -ErrorAction SilentlyContinue))

    # Weir: every change carries X-Requested-With beside Origin, and a session Weir no longer knows is replaced once.
    $wh = Get-WeirHeaders -Origin 'http://localhost:9347'
    Check 'Weir requests carry Origin and X-Requested-With' ($wh['Origin'] -eq 'http://localhost:9347' -and $wh['X-Requested-With'] -eq 'XMLHttpRequest')
    $script:S.WeirUrl = "http://127.0.0.1:$port"
    $paused = Invoke-Weir -Method PUT -Path '/pause' -Body @{ paused = $true; scan_while_paused = $true }
    Check 'a refused session is signed in again once and the change goes through' ($paused.paused -eq $true)

    # What a strike line says, from the real Activity lines of the rc.30.22 run.
    . (Join-Path $suite 'vm\scenarios\G05-Stuck.ps1')
    $slowLine = 'Gpslownqtx27.2020.1080p.WEB-DL.x264-GOLDEN (Too slow): It stayed under 2048 KB/s for 1 check, so Deluno stopped waiting for it and will not pick it again. Deluno blocked this release, is looking for another copy and removed it from SABnzbd.'
    $zeroLine = 'Gpzerohsiq22.2020.1080p.WEB-DL.x264-GOLDEN (No progress): It made no progress for 2 checks in a row, so Deluno stopped waiting for it and will not pick it again. Deluno blocked this release, is looking for another copy and removed it from NZBGet.'
    $strikeFilm = [pscustomobject]@{ Title = 'GpTest' }
    Invoke-Scenario -Id 't.slow1' -Title 'slow, 1 check' -Body { Assert-Struck -Film $strikeFilm -Seen @{ Line = $slowLine; Seconds = 17; MaxProgress = 0; Statuses = @{} } -Strikes 1 -RuleWords 'slow|speed|stayed under' -InARow $false }
    Invoke-Scenario -Id 't.zero2' -Title 'no progress, 2 checks in a row' -Body { Assert-Struck -Film $strikeFilm -Seen @{ Line = $zeroLine; Seconds = 348; MaxProgress = 0; Statuses = @{} } -Strikes 2 -RuleWords 'No progress' }
    Invoke-Scenario -Id 't.zero2wrong' -Title 'a plural where one is expected' -Body { Assert-Struck -Film $strikeFilm -Seen @{ Line = ($zeroLine -replace 'for 2 checks', 'for 1 checks'); Seconds = 1; MaxProgress = 0; Statuses = @{} } -Strikes 1 -RuleWords 'No progress' }
    $res2 = @{}; foreach ($r in $script:S.Results) { $res2[$r.id] = $r }
    Check 'the speed rule''s real line (for 1 check, no "in a row") passes' ($res2['t.slow1'].status -eq 'pass') (($res2['t.slow1'].checks | Where-Object { -not $_.ok } | ForEach-Object { $_.name + ' ' + $_.detail }) -join '; ')
    Check 'the no-progress rule''s real line (for 2 checks in a row) passes' ($res2['t.zero2'].status -eq 'pass')
    Check 'a wrong plural (1 checks) still fails' ($res2['t.zero2wrong'].status -eq 'fail')

    # The closing lines of a resent release, in the shape of the rc.30.23 run (story-Gprebeforerojy14): the replaced send was told
    # "could not tell yet", then "needed no report"; the new send was told once. Two sends, one closing line each.
    $origActivity = ${function:Get-ActivityText}
    function New-StoryLine([string] $Category, [string] $Label, [string] $HandoffId, [string] $Message) {
        (@{ category = $Category; label = $Label; message = $Message; detailsJson = (@{ HandoffId = $HandoffId; ReleaseName = 'GpStory.2020.1080p.WEB-DL.x264-GOLDEN.mkv' } | ConvertTo-Json -Compress) } | ConvertTo-Json -Compress)
    }
    $first = '01a12b7cbd217acd854f29182ad898ea'; $second = '01a12b7dab657b34acf9000717af90d3'
    $resent = @(
        (New-StoryLine 'processing.outcome-superseded' 'An earlier send needed no report, a newer send took over' $first 'GpStory.2020.1080p.WEB-DL.x264-GOLDEN.mkv was sent to Weir again after this send, so Deluno left the report to the newer send.'),
        (New-StoryLine 'processing.outcome-reported' 'Told the processor what happened' $second 'Deluno told Weir it has GpStory.2020.1080p.WEB-DL.x264-GOLDEN.mkv. Weir recorded that Deluno imported the file.'),
        (New-StoryLine 'processing.outcome-delayed' 'Could not tell the processor yet, will keep trying' $first 'Deluno could not tell Weir what became of GpStory.2020.1080p.WEB-DL.x264-GOLDEN.mkv yet, and will keep trying.'))
    $script:StoryLines = $resent
    function Get-ActivityText { $script:StoryLines }
    $storyFilm = [pscustomobject]@{ Title = 'GpStory'; Release = 'GpStory.2020.1080p.WEB-DL.x264-GOLDEN' }
    Check 'a resent release with one closing line for each send (and a "will keep trying" step) tells one story' (@(Get-StoryProblems -Film $storyFilm).Count -eq 0) ((@(Get-StoryProblems -Film $storyFilm)) -join '; ')
    $script:StoryLines = $resent + (New-StoryLine 'processing.outcome-reported' 'Told the processor what happened' $second 'Deluno told Weir it has GpStory.2020.1080p.WEB-DL.x264-GOLDEN.mkv. Weir recorded that Deluno imported the file.')
    Check 'two closing lines for the one send are still a problem' (@(Get-StoryProblems -Film $storyFilm).Count -eq 1)
    $script:StoryLines = @((New-StoryLine 'processing.outcome-reported' 'Told the processor what happened' $second 'Deluno told Weir it has GpStory.mkv. Weir recorded that Deluno imported the file. Deluno will not import this file.'))
    Check 'imported and will not import in one line is still a problem' (@(Get-StoryProblems -Film $storyFilm).Count -eq 1)
    Set-Item -Path function:Get-ActivityText -Value $origActivity
}
finally {
    try { Invoke-WebRequest "http://127.0.0.1:$port/stop" -UseBasicParsing -TimeoutSec 3 | Out-Null } catch { }
    $ps.Stop(); $ps.Dispose()
    if ($tmp -and (Test-Path $tmp)) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host 'The runner loads every library and group'
foreach ($lib in 'Tray.Lib.ps1', 'Cdp.Lib.ps1', 'Fixtures.Lib.ps1', 'GithubLimit.Lib.ps1', 'Initialize-Environment.ps1', 'scenarios\Common.ps1') { . (Join-Path (Join-Path $suite 'vm') $lib) }
foreach ($f in Get-ChildItem (Join-Path $suite 'vm\scenarios') -Filter 'G*.ps1' | Sort-Object Name) { . $f.FullName }
foreach ($g in @($plan | Where-Object { -not $_.Host })) { Check "the runner can call $($g.Function)" ($null -ne (Get-Command $g.Function -CommandType Function -ErrorAction SilentlyContinue)) }
foreach ($fn in 'Initialize-SuiteEnvironment', 'Set-HealthRules', 'Send-Film', 'New-TorrentFilm', 'New-UsenetFilm', 'Wait-FilmFinished', 'Open-DelunoPage', 'Get-TrayDot', 'Invoke-TrayMenuItem', 'Stop-WeirApp', 'Start-WeirApp', 'Set-WeirPause', 'Get-WeirLogText', 'Test-OneStory') {
    Check "$fn is defined" ($null -ne (Get-Command $fn -ErrorAction SilentlyContinue))
}

Write-Host 'The keep-seeding group (#1240)'
Check 'the plan has a moved and a public-removed scenario for each torrent client, and none for Usenet or the clients that are not in the VM' ((@('qbittorrent', 'transmission', 'deluge') | ForEach-Object { "keepseeding.moved.$_"; "keepseeding.public-removed.$_" } | Where-Object { $_ -notin $allIds }).Count -eq 0 -and @($allIds | Where-Object { $_ -like 'keepseeding.*' }).Count -eq 6)
Check 'the keep-seeding group runs before the update phase, which stays last' (@($plan | ForEach-Object Name).IndexOf('keepseeding') -gt @($plan | ForEach-Object Name).IndexOf('stuck') -and $plan[-1].Name -eq 'update')
# The two strike lines Deluno 1.0.0-rc.30.32 really wrote on the golden VM (14 Oct 2026), as /api/activity returns them,
# with the machine name replaced. Both carry the label "Fixed a stuck download": only the details' actions tell a move
# from a removal (the first live run read the label and took the move for a removal).
$movedMessage = 'Moved Gpkeepprivateanhq07.2020.1080p.WEB-DL.x264-GOLDEN.mkv to golden-keep-seeding on qBittorrent on GOLDEN-VM so it keeps seeding on Golden private feed, and is looking for another copy. Deluno blocked this release. It made no progress for 2 checks in a row.'
$movedLine = '{"id":"01a1397f8e8e7ddeb269d0d29ddff6ff","category":"download.health.remediated","detailsJson":"{\"clientId\":\"01a13972744f73a2ba5eb8844d850df1\",\"queueItemId\":\"e260099120b53f0c2112e4c9ef6c31591ac3e0f3\",\"releaseName\":\"Gpkeepprivateanhq07.2020.1080p.WEB-DL.x264-GOLDEN.mkv\",\"actions\":[\"moved to golden-keep-seeding\",\"release refused\",\"replacement search queued\"],\"categoryOwned\":true,\"payloadOwned\":false,\"dryRun\":false,\"movedTo\":\"golden-keep-seeding\",\"keepSeedingSource\":\"client\"}","relatedJobId":null,"relatedEntityType":"download_dispatch","relatedEntityId":"01a1397a49b878c6bbe535f73d116d60","createdUtc":"2026-10-14T08:20:23.3101651+00:00","message":"' + $movedMessage + '","label":"Fixed a stuck download","title":null,"filePath":null,"sourceName":null,"reasonGroup":null,"type":"grabbed","source":"client","outcome":"good"}'
$removedMessage = 'Gpkeeppublictmvn10.2020.1080p.WEB-DL.x264-GOLDEN.mkv (No progress): It made no progress for 2 checks in a row, so Deluno stopped waiting for it and will not pick it again. Deluno blocked this release, is looking for another copy and removed it from qBittorrent on GOLDEN-VM.'
$removedLine = '{"id":"01a1397f8f017b6da75fe3e97fbb7ae6","category":"download.health.remediated","detailsJson":"{\"clientId\":\"01a13972744f73a2ba5eb8844d850df1\",\"queueItemId\":\"72b68a0067a9cb5e0a92a9aeecbdcfb5d50cef9f\",\"releaseName\":\"Gpkeeppublictmvn10.2020.1080p.WEB-DL.x264-GOLDEN.mkv\",\"actions\":[\"release refused\",\"replacement search queued\",\"client entry removed with its files\"],\"categoryOwned\":true,\"payloadOwned\":false,\"dryRun\":false,\"movedTo\":null,\"keepSeedingSource\":null}","relatedJobId":null,"relatedEntityType":"download_dispatch","relatedEntityId":"01a1397a710e7372a4a175f64e82685c","createdUtc":"2026-10-14T08:20:23.4252868+00:00","message":"' + $removedMessage + '","label":"Fixed a stuck download","title":null,"filePath":null,"sourceName":null,"reasonGroup":null,"type":"grabbed","source":"client","outcome":"good"}'
$blockedLine = '{"id":"01a1397f8e797c5ba4c5e1e7123b1bad","category":"release.blocked","detailsJson":"{\"release\":\"Gpkeepprivateanhq07.2020.1080p.WEB-DL.x264-GOLDEN\"}","message":"Deluno blocked Gpkeepprivateanhq07.2020.1080p.WEB-DL.x264-GOLDEN: It made no progress for 2 checks in a row, so Deluno stopped waiting for it and will not pick it again.","label":"Release blocked"}'
$ksFilm = [pscustomobject]@{ Title = 'Gpkeepprivateanhq07'; Release = 'Gpkeepprivateanhq07.2020.1080p.WEB-DL.x264-GOLDEN' }
$ksPublic = [pscustomobject]@{ Title = 'Gpkeeppublictmvn10'; Release = 'Gpkeeppublictmvn10.2020.1080p.WEB-DL.x264-GOLDEN' }
$ksLines = @($zeroLine, $blockedLine, $movedLine, $removedLine)
Check 'the moved line is found for its release, as its message text' ((Get-KsMovedMessage -Film $ksFilm -Lines $ksLines) -eq $movedMessage)
Check 'a strike line for another release is not taken for the moved line' ($null -eq (Get-KsMovedMessage -Film ([pscustomobject]@{ Title = 'Other'; Release = 'Other.2020.1080p.WEB-DL.x264-GOLDEN' }) -Lines $ksLines))
Check 'the real moved line, labelled "Fixed a stuck download" like every strike, is not read as a removal' ($null -eq (Get-KsRemovedMessage -Film $ksFilm -Lines $ksLines))
Check 'the real removal of the public torrent is read as a removal, and not as a move' ((Get-KsRemovedMessage -Film $ksPublic -Lines $ksLines) -eq $removedMessage -and $null -eq (Get-KsMovedMessage -Film $ksPublic -Lines $ksLines))
Check 'the private film has exactly one strike line (the move), so "not struck again" holds; the release-blocked line is not a strike' (@(Get-KsStrikeLines -Film $ksFilm -Lines $ksLines).Count -eq 1)
Check 'a second strike line for the same release is counted' (@(Get-KsStrikeLines -Film $ksFilm -Lines @($movedLine, $movedLine)).Count -eq 2)
Check 'a removal is not read as a move' ($null -eq (Get-KsMovedMessage -Film $strikeFilm -Lines @($zeroLine)))
$ksText = Get-Content -Raw (Join-Path $suite 'vm\scenarios\G10-KeepSeeding.ps1')
Check 'the group sets the rule to move, puts it back, and puts the keep-seeding choices and the private indexer back through Deluno' ($ksText -match "privateAction = 'move-and-replace'" -and $ksText -match 'could not restore rule' -and $ksText -match 'could not restore the keep-seeding choices' -and $ksText -match '-Method DELETE -Path "/api/indexers/')
Check 'the group reads each client itself for the category and the torrent, never only Deluno' ($ksText -match 'Get-KsClientTorrent' -and $ksText -match 'core\.get_torrent_status' -and $ksText -match 'torrent-get' -and $ksText -match 'torrents/info')
Check 'the private torrent is checked for its data, for not being deleted, and for staying out of Transfers' ($ksText -match 'Get-KsFilesOnDisk' -and $ksText -match 'still holds the torrent' -and $ksText -match 'gone from Transfers')
Check 'a password read for a client is masked in everything the suite writes' ((Get-Content -Raw (Join-Path $suite 'vm/Fixtures.Lib.ps1')) -match 'S\.Secrets\["ks-\$Kind-password"\]')

Write-Host 'The four proof lines of #1196 are scenarios (github-limit.messages, github-limit.picker, seasonpack.no-library, layout.390-folder-chain)'
$planNames = @($plan | ForEach-Object Name)
foreach ($id in 'github-limit.messages', 'github-limit.picker', 'seasonpack.no-library', 'layout.390-folder-chain') { Check "$id is in the plan and can be run alone with -Scenarios" ($id -in $allIds -and (Get-RunScope -Plan $plan -Scenarios @($id)).ChosenIds -contains $id) }
Check 'the picker scenario is the host''s own (it needs a clean machine and the limit used up before Setup), the message scenario runs in the VM' ((@($plan | Where-Object { $_.Host } | ForEach-Object { $_.Ids }) -contains 'github-limit.picker') -and -not (@($plan | Where-Object { $_.Host } | ForEach-Object { $_.Ids }) -contains 'github-limit.messages'))
Check 'the picker scenario chosen alone needs no VM stage and runs the picker phase; it never runs the update phase' (-not (Get-RunScope -Plan $plan -Scenarios @('github-limit.picker')).NeedVm -and (Get-RunScope -Plan $plan -Scenarios @('github-limit.picker')).RunGithubPicker -and -not (Get-RunScope -Plan $plan -Scenarios @('github-limit.picker')).RunUpdate)
Check 'the GitHub group is the last that runs in the VM (it uses the allowance up for an hour) and the host phases follow, the update phase last' (($planNames | Where-Object { $_ -in 'github', 'githubpicker', 'update' }) -join ',' -eq 'github,githubpicker,update' -and @($plan | Where-Object { -not $_.Host })[-1].Name -eq 'github')
Check 'Weir''s key is left after the last group that runs in the VM, whatever it is called' ($runSuiteText -match 'lastVmGroup' -and $runSuiteText -match '\$Groups -contains \$lastVmGroup' -and $runSuiteText -match "GithubLimit\.Lib\.ps1")
foreach ($n in 'seasonpack', 'layout', 'github', 'githubpicker') { Check "the $n group says what it proves" ($n -in $topics.Group) }
# #1257: the NZBGet setup-twice scenario is in the plan, runs before the films, can be run alone, and judges NZBGet's own replies
# (recorded from NZBGet 26.3) the way the issue describes.
Check 'nzbget.setup-twice is in the plan, runs before the film scenarios, and can be run alone with -Scenarios' ('nzbget.setup-twice' -in $allIds -and $planNames.IndexOf('nzbgetsetup') -lt $planNames.IndexOf('films') -and (Get-RunScope -Plan $plan -Scenarios @('nzbget.setup-twice')).ChosenIds -contains 'nzbget.setup-twice' -and (Get-RunScope -Plan $plan -Scenarios @('nzbget.setup-twice')).NeedVm)
Check 'the nzbgetsetup group says what it proves' ('nzbgetsetup' -in $topics.Group)
& {
    . (Join-Path $suite 'vm\scenarios\G00-NzbgetSetupTwice.ps1')
    $recorded = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'tests\fixtures\recorded\nzbget\26.3'
    function Read-Reply([string] $State, [string] $Method) {
        $doc = Get-Content -Raw -LiteralPath (Join-Path $recorded "$State.json") | ConvertFrom-Json
        ($doc.exchanges | Where-Object { $_.request.body.method -eq $Method } | Select-Object -First 1).reply.body.result
    }
    $paused = @(Get-NzbgetConfigProblems -Status (Read-Reply 'config-error-paused' 'status') -LogEntries (Read-Reply 'config-error-paused' 'log'))
    Check 'NZBGet paused over a bad nzbget.conf (recorded status and log) fails the check, naming the pause and the Invalid option line' ($paused.Count -ge 2 -and ($paused -join '|') -match 'paused' -and ($paused -join '|') -match 'Invalid option' -and ($paused -join '|') -match 'errors in configuration') ($paused -join ' | ')
    $clean = @(Get-NzbgetConfigProblems -Status (Read-Reply 'config-error-cleared' 'status') -LogEntries @())
    Check 'NZBGet after the file is valid again (recorded status, not paused, empty log) passes the check' ($clean.Count -eq 0) ($clean -join ' | ')
}

# The limit helpers, against stand-ins for GitHub (defined in a scope of their own, so they never reach the other checks).
& {
    $script:Stub = @{ Remaining = 3; Limit = 60; Reset = 1790000000; Calls = 0; Spend = $true }
    function Invoke-RestMethod { param($Uri, [switch] $UseBasicParsing, $TimeoutSec, $Headers) [pscustomobject]@{ resources = [pscustomobject]@{ core = [pscustomobject]@{ limit = $script:Stub.Limit; remaining = $script:Stub.Remaining; reset = $script:Stub.Reset } } } }
    function Invoke-WebRequest { param($Uri, [switch] $UseBasicParsing, $TimeoutSec, $Headers) $script:Stub.Calls++; if ($script:Stub.Spend -and $script:Stub.Remaining -gt 0) { $script:Stub.Remaining-- }; [pscustomobject]@{ Headers = @{ 'X-RateLimit-Remaining' = "$($script:Stub.Remaining)"; 'X-RateLimit-Reset' = "$($script:Stub.Reset)" } } }
    $used = Use-GitHubLimitUp -MaxCalls 70
    Check 'the VM spends exactly the calls left and stops at the first answer that says none are left' ($used.Reachable -and $used.Calls -eq 3 -and $used.Remaining -eq 0 -and $used.RemainingAtStart -eq 3 -and $used.ResetEpoch -eq 1790000000) ($used | ConvertTo-Json -Compress)
    $script:Stub.Remaining = 0; $script:Stub.Calls = 0
    $again = Use-GitHubLimitUp -MaxCalls 70
    Check 'a limit that is already used up costs no calls' ($again.Reachable -and $again.Calls -eq 0)
    $script:Stub.Remaining = 50; $script:Stub.Calls = 0; $script:Stub.Spend = $false
    $capped = Use-GitHubLimitUp -MaxCalls 70
    Check 'the loop never makes more calls than its cap, and says the limit was not reached' (-not $capped.Reachable -and $capped.Calls -eq 70 -and $capped.Reason -match 'could not be used up') ($capped | ConvertTo-Json -Compress)
    $script:Stub.Limit = 5000; $script:Stub.Remaining = 4999; $script:Stub.Calls = 0
    $big = Use-GitHubLimitUp -MaxCalls 70
    Check 'an allowance bigger than the cap (a signed-in address) is not spent at all' (-not $big.Reachable -and $big.Calls -eq 0 -and $big.Reason -match '5000')
}
Check 'the reset is written as Deluno writes it: rounded up to the minute, in UTC' ((Get-GitHubLimitStamp -ResetEpoch 1) -eq '1970-01-01 00:01 UTC' -and (Get-GitHubLimitStamp -ResetEpoch 0) -eq '1970-01-01 00:00 UTC') (Get-GitHubLimitStamp -ResetEpoch 1)
$resetNow = [DateTimeOffset]::UtcNow.AddMinutes(37).ToUnixTimeSeconds()
$localForms = @(Get-GitHubLimitLocalForms -ResetEpoch $resetNow)
Check 'the reset reads in the viewer''s local time in the 12 and the 24 hour forms' ($localForms.Count -eq 2 -and $localForms[0] -match '^\d{1,2}:\d{2}$' -and $localForms[1] -match '^\d{2}:\d{2}$')
Check 'a sentence with the local time is accepted (12 hour, 24 hour, a minute either side) and one with only UTC or another time is not' ((Test-TextHasLocalReset -Text "used up until $($localForms[0]) PM. Deluno will try again then." -ResetEpoch $resetNow) -and (Test-TextHasLocalReset -Text "until $($localForms[1])" -ResetEpoch $resetNow) -and -not (Test-TextHasLocalReset -Text 'until 9:99' -ResetEpoch $resetNow) -and -not (Test-TextHasLocalReset -Text 'used up until 2026-10-09 00:35 UTC' -ResetEpoch $resetNow))
Check 'a two-digit minute is not read inside a longer number' (-not (Test-TextHasLocalReset -Text "id 1$($localForms[1])5" -ResetEpoch $resetNow))

# What the picker scenario concludes from what the VM kept.
$goodLog = "2026-10-14T01:00:00 Weir: installer Weir-Setup.exe sha256 $('A' * 64)`n2026-10-14T01:00:00 Weir: SHA-256 matches the vendor's published checksum"
$goodLimit = [pscustomobject]@{ Reachable = $true; Calls = 60; Remaining = 0; ResetUtc = '2026-10-14T02:00:00Z'; Reason = '' }
$goodPicker = "x`n2026 final: Weir | Finished."
$tags = @('v3.4.0-rc.1', 'v3.3.0', 'v3.2.0')
$good = @(Get-GithubPickerChecks -Limit $goodLimit -HookLog $goodLog -PickerLog $goodPicker -WeirVersion '3.4.0-rc.1+abc' -FeedTags $tags)
Check 'the picker scenario passes when Weir was installed through the feed with the limit used up' (@($good | Where-Object { -not $_.ok }).Count -eq 0 -and $good.Count -ge 7) (($good | Where-Object { -not $_.ok } | ForEach-Object { $_.name }) -join '; ')
$badLimitLog = @(Get-GithubPickerChecks -Limit $goodLimit -HookLog ($goodLog + "`nWeir: GitHub's anonymous limit is used up until 2026-10-14T02:00:00") -PickerLog $goodPicker -WeirVersion '3.4.0' -FeedTags $tags)
Check 'it fails when the install log says the limit stopped Weir' (@($badLimitLog | Where-Object { -not $_.ok -and $_.name -match 'never says' }).Count -eq 1)
$notInstalled = @(Get-GithubPickerChecks -Limit $goodLimit -HookLog $goodLog -PickerLog $goodPicker -WeirVersion '' -FeedTags $tags)
Check 'it fails when Weir is not installed' (@($notInstalled | Where-Object { -not $_.ok -and $_.name -match 'Weir is installed' }).Count -eq 1)
$oldRelease = @(Get-GithubPickerChecks -Limit $goodLimit -HookLog $goodLog -PickerLog $goodPicker -WeirVersion '1.0.0' -FeedTags $tags)
Check 'it fails when the installed Weir is not one of the feed''s newest releases' (@($oldRelease | Where-Object { -not $_.ok -and $_.name -match 'release feed' }).Count -eq 1)
$notUsed = @(Get-GithubPickerChecks -Limit ([pscustomobject]@{ Reachable = $true; Calls = 3; Remaining = 5; ResetUtc = ''; Reason = '' }) -HookLog $goodLog -PickerLog $goodPicker -WeirVersion '3.3.0' -FeedTags $tags)
Check 'it fails when GitHub still had calls left before Setup (the picker never met the limit)' (@($notUsed | Where-Object { -not $_.ok -and $_.name -match 'used up' }).Count -eq 1)
$na = @(Get-GithubPickerChecks -Limit ([pscustomobject]@{ Reachable = $false; Calls = 0; Reason = 'GitHub allows 5000 calls an hour from this address.' }) -HookLog '' -PickerLog '' -WeirVersion '' -FeedTags @())
Check 'it is not applicable, with the reason and as a pass, when the limit cannot be used up' ($na.Count -eq 1 -and $na[0].ok -and $na[0].name -match '^Not applicable' -and $na[0].detail -match '5000')
Check 'it fails when the hook that uses the allowance up left no record' (@(Get-GithubPickerChecks -Limit $null | Where-Object { -not $_.ok }).Count -eq 1)
Check 'a version reads as its first three numbers whatever its suffix or leading v' ((Get-VersionCore 'v3.4.0-rc.1') -eq '3.4.0' -and (Get-VersionCore '3.4.0.0') -eq '3.4.0' -and (Get-VersionCore '3.4.0+abc') -eq '3.4.0' -and (Get-VersionCore 'nonsense') -eq '')
$rec = New-HostScenarioRecord -Id 'x' -Title 't' -Checks @([pscustomobject]@{ name = 'a'; ok = $true; detail = '' }) -Seconds 1
$recBad = New-HostScenarioRecord -Id 'x' -Title 't' -Checks @([pscustomobject]@{ name = 'a'; ok = $false; detail = '' }) -Seconds 1
$recNone = New-HostScenarioRecord -Id 'x' -Title 't' -Seconds 1
$recErr = New-HostScenarioRecord -Id 'x' -Title 't' -ErrorText 'boom'
Check 'a host-judged scenario is a pass, a fail, or an error (no checks, or an error), as the VM''s are' ($rec.status -eq 'pass' -and $recBad.status -eq 'fail' -and $recNone.status -eq 'error' -and $recErr.status -eq 'error' -and $recErr.error -eq 'boom')
$mergedHost = @(Merge-SuiteResults -Plan $plan -Reported @($rec | ForEach-Object { $_.id = 'github-limit.picker'; $_ }) -Scenarios @('github-limit.picker'))
Check 'a host-judged record merges into the results under the picker''s id' ($mergedHost.Count -eq 1 -and $mergedHost[0].Status -eq 'pass' -and $mergedHost[0].Group -eq 'githubpicker')

# The host script's picker phase: a clean machine, the allowance used up BEFORE Setup, after the groups and before the update phase.
$iPickerFn = $hostScript.IndexOf('function Invoke-GithubPickerPhase')
$pickerBody = $hostScript.Substring($iPickerFn, $hostScript.IndexOf("`nif (`$runGithubPicker)") - $iPickerFn)
Check 'the picker phase installs interactively (the picker is a first-run dialog) from the clean checkpoint, with the candidate''s installer' ($pickerBody -match '-Interactive' -and $pickerBody -match '-InstallerPath \$InstallerPath' -and $pickerBody -match '-Checkpoint \$Checkpoint')
Check 'the limit is used up in the harness hook, which runs before Setup, with at most 70 calls' ($pickerBody -match '-AfterVmReady \$limitHook' -and $pickerBody -match 'Use-GitHubLimitUp -MaxCalls 70' -and $pickerBody.IndexOf('Use-GitHubLimitUp') -lt $pickerBody.IndexOf('Invoke-GoldenPath.ps1'))
Check 'the phase reads what the VM kept (limit record, install log, picker log, Weir''s version, the release feed) and judges it with Get-GithubPickerChecks, and the build under test' ($pickerBody -match 'install-hook\.log' -and $pickerBody -match 'picker\.log' -and $pickerBody -match 'releases\.atom' -and $pickerBody -match 'Get-GithubPickerChecks' -and $pickerBody -match 'Test-BuildMatchesCommit')
Check 'the picker phase runs after the log collection and the after-suite hook, before the update phase, and is in the verdict' ($hostScript.IndexOf('Invoke-GithubPickerPhase)') -gt $iAfterSuite -and $hostScript.IndexOf('Invoke-GithubPickerPhase)') -lt $iPhaseCall -and $hostScript.IndexOf('Invoke-GithubPickerPhase)') -lt $iVerdict)
Check 'the host script takes the picker scenario from the scope (RunGithubPicker)' ($hostScript -match '\$runGithubPicker = \$scope\.RunGithubPicker' -and $hostScript -match 'if \(\$runGithubPicker\)')

# The season-pack scenario.
$spText = Get-Content -Raw (Join-Path $suite 'vm\scenarios\G11-SeasonPack.ps1')
Check 'the season-pack scenario builds its show in a library of its own, imports two episodes, then changes the library''s folder so the old files match no library' ($spText -match "mediaType = 'tv'" -and $spText -match '/api/filesystem/import/jobs' -and $spText -match 'PUT -Path "/api/libraries/\$libId"' -and $spText -match 'rootPath = \$movedRoot')
Check 'the replacement is a real one: Deluno''s own season search sends the pack and stores the targets, never a hand-made import call with a made-up dispatch' ($spText -match '/seasons/1/search' -and $spText -match 'replacementComparisons' -and $spText -match 'Wait-FilmFinished' -and $spText -notmatch 'forceReplacement' -and $spText -notmatch 'replacementTargets =' -and $spText -notmatch 'golden-suite-pack')
Check 'the pack is a torrent in the feed (TV category) with its data placed in the client, and the library is routed to the client and the feed' ($spText -match 'Cat 5040' -and $spText -match 'Get-ClientDataFolder' -and $spText -match '/routing' -and $spText -match 'Add-SuiteTorrentRelease')
Check 'the old files are hashed before the pack and compared after, and looked for in place or at the pack path''s aside name' ($spText -match 'Get-FileHash' -and $spText -match 'deluno-pack-backup-' -and $spText -match 'byte-identical')
Check 'Deluno''s own words are looked for in its log, for this show, once for each file' ($spText -match 'because no library matched it; nothing was deleted' -and $spText -match 'Get-DelunoLogFiles' -and $spText -match 'ge 2')
Check 'the show and the library are removed afterwards, whatever happened' ($spText -match 'finally' -and $spText -match '/api/series/bulk' -and $spText -match "Method DELETE -Path ""/api/libraries/")
Check 'the words the scenario looks for are the words the product writes' ((Get-Content -Raw (Join-Path $root '..\..\src\Deluno.Filesystem\ImportPipelineService.cs')) -match 'because no library matched it; nothing was deleted' -and (Get-Content -Raw (Join-Path $root '..\..\src\Deluno.Filesystem\ImportPipelineService.cs')) -match '\.deluno-pack-backup')

# The 390 layout scenario.
$lyText = Get-Content -Raw (Join-Path $suite 'vm\scenarios\G12-Layout.ps1')
Check 'the layout scenario makes a real folder-chain problem (a library routed to a client whose folder disagrees) and confirms it from the folder check itself' ($lyText -match '/folder-chain' -and $lyText -match "status -eq 'problem'" -and $lyText -match 'allMatch -eq \$false' -and $lyText -match '/routing')
Check 'it lets Health raise the problem and reads its row, cause and fix for the library''s name' ($lyText -match '/api/system/health/run' -and $lyText -match 'library:\$\{libId\}:folder-chain' -and $lyText -match '\.cause' -and $lyText -match '\.fix')
Check 'the walk is at 390 px wide in the VM''s browser over DevTools, never on the workstation, and checks the width it got' ($lyText -match 'setDeviceMetricsOverride' -and $lyText -match 'width = 390' -and $lyText -match '\$_\.Width -ne 390')
Check 'it fails on a page that scrolls sideways or a control outside the screen, and the library is removed afterwards' ($lyText -match 'scrollWidth > w \+ 1' -and $lyText -match 'outside the screen' -and $lyText -match "Method DELETE -Path ""/api/libraries/")
. (Join-Path $suite 'vm\scenarios\G12-Layout.ps1')
$walkRoutes = @(Get-NarrowWalkRoutes)
$routerText = Get-Content -Raw (Join-Path $root '..\..\apps\web\src\router.tsx')
$shellText = Get-Content -Raw (Join-Path $root '..\..\apps\web\src\components\app\settings-shell.tsx')
$layoutText = Get-Content -Raw (Join-Path $root '..\..\apps\web\src\layouts\app-layout.tsx')
$want = New-Object System.Collections.Generic.HashSet[string]
[void]$want.Add('/settings'); [void]$want.Add('/setup-guide')
$blockStart = $routerText.IndexOf('path: "settings",'); $blockEnd = $routerText.IndexOf('path: "system",')
foreach ($mm in [regex]::Matches($routerText.Substring($blockStart, $blockEnd - $blockStart), 'path: "([a-z][a-z0-9-]*)",\s*\r?\n\s*lazy')) { [void]$want.Add('/settings/' + $mm.Groups[1].Value) }
foreach ($mm in [regex]::Matches($routerText, 'path: "(settings/[a-z0-9-]+)"')) { [void]$want.Add('/' + $mm.Groups[1].Value) }
foreach ($mm in [regex]::Matches($shellText, 'to: "(/(?:settings|indexers|search-cycles|system)[a-z0-9/-]*)"')) { [void]$want.Add($mm.Groups[1].Value) }
foreach ($navName in 'libraryNav', 'operationsNav') {
    $ns = $layoutText.IndexOf("const $navName = ["); $ne = $layoutText.IndexOf('] as const;', $ns)
    foreach ($mm in [regex]::Matches($layoutText.Substring($ns, $ne - $ns), 'to: "(/[a-z0-9/-]*)"')) { [void]$want.Add($mm.Groups[1].Value) }
}
$missingRoutes = @($want | Where-Object { $_ -notin $walkRoutes })
Check "the 390 walk visits every screen the smoke walk does ($($want.Count) from the router and the shell)" ($want.Count -gt 40 -and $missingRoutes.Count -eq 0) ($missingRoutes -join ', ')

# The GitHub scenario.
$ghText = Get-Content -Raw (Join-Path $suite 'vm\scenarios\G13-Github.ps1')
Check 'the message scenario skips as not applicable, with the reason, when the limit cannot be used up' ($ghText -match 'Not applicable: the limit could not be used up from this address' -and $ghText -match 'Use-GitHubLimitUp -MaxCalls 70')
Check 'it reads the tracker definitions'' Update now from the API and the screen, in the sentence the product writes' ($ghText -match 'update-check/run' -and $ghText -match '/settings/general' -and $ghText -match "GitHub's limit for checking releases from this network is used up until" -and (Get-Content -Raw (Join-Path $root '..\..\src\Deluno.Infrastructure\Http\GitHubApiConditionalHandler.cs')) -match "GitHub's limit for checking releases from this network is used up until")
Check 'it reads the connections'' version check from the API and the screen, in the words the web app writes' ($ghText -match 'latestCheckPausedUntilUtc' -and $ghText -match "can't look for a newer version until" -and (Get-Content -Raw (Join-Path $root '..\..\apps\web\src\components\app\connection-version.tsx')) -match "can't look for a newer version until \$\{formatResetTime\(pausedUntil\)\} \(GitHub's limit\)")
Check 'it restarts Deluno from its tray menu with github.com pointed at the VM, so the free feed cannot be read and nothing is remembered, and puts the hosts file back whatever happens' ($ghText -match 'Restart Deluno' -and $ghText -match 'Set-GithubFeedBlocked -Blocked \$true' -and $ghText -match 'finally' -and $ghText -match 'Set-GithubFeedBlocked -Blocked \$false')
Check 'it checks the reset is shown in the viewer''s local time and never as UTC, on both screens' ((@([regex]::Matches($ghText, 'Test-TextHasLocalReset')).Count -ge 2) -and $ghText -match "notmatch 'UTC'")
$hostsLines = @('127.0.0.1 localhost', "127.0.0.1 github.com www.github.com $script:GithubHostsTag", '# comment')
Check 'only the suite''s own line is taken out of the hosts file' (@($hostsLines | Where-Object { $_ -notlike "*$($script:GithubHostsTag)*" }).Count -eq 2)

Write-Host 'The news stub can hang an article and slow another'
. (Join-Path $root 'usenet-stub\UsenetStub.Lib.ps1')
$stubRoot = Join-Path ([IO.Path]::GetTempPath()) ('stub-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $stubRoot 'articles') | Out-Null
foreach ($id in 'fine@x', 'hung@x', 'slow@x') { [IO.File]::WriteAllText((Join-Path $stubRoot "articles\$id.art"), "Message-ID: <$id>`r`n`r`n=ybegin line=128 size=1 name=a`r`nx`r`n=yend size=1`r`n") }
Set-Content (Join-Path $stubRoot 'hang.txt') 'hung@x'
Set-Content (Join-Path $stubRoot 'slow.txt') 'slow@x'
$srv = New-Object Golden.NntpServer
$srv.Root = $stubRoot; $srv.Port = 0; $srv.HangFile = (Join-Path $stubRoot 'hang.txt'); $srv.SlowFile = (Join-Path $stubRoot 'slow.txt'); $srv.ArticleDelayMs = 1500
$srv.Start()
function Ask([string] $cmd, [int] $waitMs) {
    $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $srv.Port)
    $c.ReceiveTimeout = $waitMs
    $s = $c.GetStream(); $r = New-Object IO.StreamReader($s); $w = New-Object IO.StreamWriter($s); $w.AutoFlush = $true
    [void]$r.ReadLine()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $w.WriteLine($cmd)
    $line = $null
    try { $line = $r.ReadLine() } catch { $line = 'TIMEOUT' }
    $c.Close()
    [pscustomobject]@{ Line = $line; Ms = $sw.ElapsedMilliseconds }
}
try {
    $a = Ask 'BODY <fine@x>' 4000
    Check 'an ordinary article is answered at once' ($a.Line -like '222*' -and $a.Ms -lt 1000) "$($a.Line) $($a.Ms)ms"
    $b = Ask 'BODY <hung@x>' 2000
    Check 'an article on the hang list is never answered' ($b.Line -eq 'TIMEOUT') "$($b.Line) $($b.Ms)ms"
    $c = Ask 'BODY <slow@x>' 5000
    Check 'an article on the slow list is answered after the pause' ($c.Line -like '222*' -and $c.Ms -ge 1400) "$($c.Line) $($c.Ms)ms"
    $d = Ask 'BODY <nothere@x>' 3000
    Check 'a missing article is still 430' ($d.Line -like '430*') $d.Line
}
finally { $srv.Stop(); Remove-Item -LiteralPath $stubRoot -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'A multi-file torrent is well formed'
$tt = Join-Path ([IO.Path]::GetTempPath()) ('torrent-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $tt 'rel\sub') | Out-Null
[IO.File]::WriteAllBytes((Join-Path $tt 'rel\a.bin'), (New-Object byte[] 700000))
[IO.File]::WriteAllBytes((Join-Path $tt 'rel\sub\b.bin'), (New-Object byte[] 300000))
$made = & (Join-Path $root 'torznab-feed\Make-Torrent.ps1') -Folder (Join-Path $tt 'rel') -Out (Join-Path $tt 'x.torrent') | ConvertFrom-Json
$bytes = [IO.File]::ReadAllBytes((Join-Path $tt 'x.torrent'))
$ascii = [Text.Encoding]::ASCII.GetString($bytes)
Check 'the folder torrent names its folder, both files and the right size' ($made.name -eq 'rel' -and $made.size -eq 1000000 -and $ascii.Contains('5:files') -and $ascii.Contains('5:b.bin') -and $ascii.Contains('3:sub'))
Remove-Item -LiteralPath $tt -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Edge DevTools driving works (skipped when Edge is not installed)'
$edge = @('C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe', 'C:\Program Files\Microsoft\Edge\Application\msedge.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) {
    . (Join-Path $suite 'vm\Cdp.Lib.ps1')
    $cdpPort = Get-Random -Minimum 30000 -Maximum 39000
    $prof = Join-Path ([IO.Path]::GetTempPath()) ('edge-test-' + [guid]::NewGuid().ToString('N'))
    $proc = Start-Process -FilePath $edge -ArgumentList "--headless=new", "--remote-debugging-port=$cdpPort", "--user-data-dir=$prof", '--no-first-run', 'about:blank' -PassThru
    try {
        $script:Cdp.Port = $cdpPort
        $up = $false
        for ($i = 0; $i -lt 40 -and -not $up; $i++) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$cdpPort/json/version" -TimeoutSec 2 | Out-Null; $up = $true } catch { } } # reason: poll interval inside a loop bounded by its deadline
        Check 'Edge opens its DevTools port' $up
        if ($up) {
            $page = New-CdpPage 'about:blank'
            $html = '<html><body><h1>Queue</h1><button onclick="document.title=''clicked''">Send to Weir again</button><div role="dialog">Drawer text</div></body></html>'
            Send-Cdp -Page $page -Method 'Page.navigate' -Params @{ url = 'data:text/html,' + [uri]::EscapeDataString($html) } | Out-Null
            Start-Sleep -Seconds 1 # reason: UI settle after input; Windows raises no event for it
            Check 'the page text is read' ((Get-PageText -Page $page) -match 'Queue')
            Check 'the open drawer text is read' ((Get-DrawerText -Page $page) -match 'Drawer text')
            Check 'a button is found by its text and pressed with a real click' (Click-PageText -Page $page -Pattern '^Send to .* again')
            Start-Sleep -Milliseconds 300 # reason: UI settle after input; Windows raises no event for it
            Check 'the click reached the page' ((Invoke-CdpEval -Page $page -Script 'document.title') -eq 'clicked')
            Check 'a button that is not there is not found' (-not (Click-PageText -Page $page -Pattern 'No such button' -TimeoutSec 0))
            $busyHtml = '<html><body><button id=remove disabled onclick="document.title=''removed''">Remove...</button><script>setTimeout(function(){document.getElementById("remove").disabled=false},3500)</script></body></html>'
            Send-Cdp -Page $page -Method 'Page.navigate' -Params @{ url = 'data:text/html,' + [uri]::EscapeDataString($busyHtml) } | Out-Null
            Start-Sleep -Milliseconds 400 # reason: UI settle after input; Windows raises no event for it
            Check 'a disabled button is not clicked when the caller only probes' (-not (Click-PageText -Page $page -Pattern '^Remove' -TimeoutSec 0))
            $threw = ''; try { Click-PageText -Page $page -Pattern '^Remove' -TimeoutSec 1 | Out-Null } catch { $threw = $_.Exception.Message }
            Check 'a button that stays disabled fails with a message that says so' ($threw -match 'disabled') $threw
            Check 'a button that becomes enabled is clicked once it is' (Click-PageText -Page $page -Pattern "^Remove" -TimeoutSec 10)
            Start-Sleep -Milliseconds 300 # reason: UI settle after input; Windows raises no event for it
            Check 'the click reached the enabled button' ((Invoke-CdpEval -Page $page -Script 'document.title') -eq 'removed')
            Close-CdpPage -Page $page
        }
    }
    finally { try { Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($prof) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } } catch { }; Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath $prof -Recurse -Force -ErrorAction SilentlyContinue } # reason: lets the browser exit before its profile is removed
}
else { Write-Host '  skipped (no Edge)' }

Write-Host 'The tray code loads and judges blinking'
. (Join-Path $suite 'vm\Tray.Lib.ps1')
$buttons = @(Get-TrayButtons)
Check 'the notification area can be listed' ($true) "$($buttons.Count) buttons"
$blinking = Get-BlinkSummary -Readings @(1..12 | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = $(if ($_ % 2) { 0.31 } else { 0.18 }); Colour = 'blue' } })
Check 'an alternating dot of one colour is a blink' ($blinking.Blinks -and $blinking.Colours.Count -eq 1)
$steady = Get-BlinkSummary -Readings @(1..12 | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = 0.31; Colour = 'green' } })
Check 'a steady dot is not a blink' (-not $steady.Blinks)

Write-Host 'The dot classifier reads grey, and one stray reading is ignored'
function New-Px([double] $H, [double] $S, [double] $B, [int] $Count) { 1..$Count | ForEach-Object { [pscustomobject]@{ H = $H; S = $S; B = $B } } }
$greyDot = Get-DotClassFromPixels -Pixels @((New-Px 0 0.02 0.55 40) + (New-Px 0 0.0 0.15 200))
Check 'an unsaturated dot reads grey' ($greyDot.Colour -eq 'grey') $greyDot.Colour
$strays = Get-DotClassFromPixels -Pixels @((New-Px 0 0.02 0.55 40) + (New-Px 210 0.9 0.6 3) + (New-Px 0 0.0 0.15 200))
Check 'a few stray saturated pixels do not turn a grey dot blue' ($strays.Colour -eq 'grey') $strays.Colour
Check 'a green dot reads green' ((Get-DotClassFromPixels -Pixels @(New-Px 120 0.8 0.5 60)).Colour -eq 'green')
Check 'an amber dot reads amber' ((Get-DotClassFromPixels -Pixels @(New-Px 40 0.9 0.6 60)).Colour -eq 'amber')
$mixed = Get-BlinkSummary -Readings @(1..12 | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = $(if ($_ % 2) { 0.31 } else { 0.18 }); Colour = $(if ($_ -eq 5) { 'amber' } else { 'grey' }) } })
Check 'one outlier colour in a run does not break a blink' ($mixed.Blinks -and $mixed.Colours.Count -eq 1 -and $mixed.Colours[0] -eq 'grey')

Write-Host 'The first-run dot is a grey dot over the logo'
$blinkReadings = @(1..12 | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = $(if ($_ % 2) { 0.833 } else { 0.8503 }); Colour = $(if ($_ % 2) { 'amber' } else { 'grey' }) } })
$firstRun = Get-FirstRunDotSummary -Readings $blinkReadings
Check 'grey and amber readings that alternate are one grey dot blinking over the amber logo (the rc.30.22 readings)' ($firstRun.Blink.Blinks -and $firstRun.GreyShown -and $firstRun.OnlyGreyAndLogo -and $firstRun.LogoColours.Count -eq 1)
$redDot = Get-FirstRunDotSummary -Readings @(1..12 | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = $(if ($_ % 2) { 0.833 } else { 0.8503 }); Colour = $(if ($_ % 2) { 'red' } else { 'grey' }) } })
Check 'a red reading is not the logo and fails' (-not $redDot.OnlyGreyAndLogo)
$noGrey = Get-FirstRunDotSummary -Readings @(1..12 | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = $(if ($_ % 2) { 0.833 } else { 0.8503 }); Colour = 'amber' } })
Check 'a dot that is never grey fails' (-not $noGrey.GreyShown)

& {   # stand-ins for the clients live in a scope of their own
Write-Host 'A torrent film''s data is where the client looks before the client checks it (#1238, rc.30.30)'
$pRoot = Join-Path ([IO.Path]::GetTempPath()) ('placement-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $pRoot | Out-Null
    function Write-SuiteLog { param($Text) }
    function Add-Note { param($Text) $script:Notes += $Text }
    function Save-Evidence { param($Name, $Text) }
    # A fake client: what it says about the torrent, and what it was asked to do (with the state of the files at that moment).
    $script:Cl = @{}
    $script:Notes = @()
    $script:Events = New-Object System.Collections.ArrayList
    function Get-ClientTorrent {
        param($Kind, $Hash, $For)
        $c = $script:Cl[$Kind]
        [pscustomobject]@{ Kind = $Kind; Present = $c.Present; State = $c.State; Progress = [double]$c.Progress; Checking = [bool]$c.Checking; DataFolder = $c.Folder; SavePath = $c.Folder; DownloadPath = $null; ContentPath = $null }
    }
    function Find-QueueRow { param($Name, [switch] $IncludeHistory) [pscustomobject]@{ id = 'abc'; clientId = 'c1'; releaseName = $Name; sourcePath = 'C:\Downloads\Completed\Movies\x'; status = 'notTransferring'; progress = 0 } }
    function Invoke-Recheck { param($Film) [void]$script:Events.Add([pscustomobject]@{ What = 'recheck'; Release = $Film.Release; Verified = (Test-FilmDataAt -Film $Film -Folder $script:Cl[$Film.ClientKind].Folder).Ok }); $true }
    function Invoke-DelugeSettle { param($Film) [void]$script:Events.Add([pscustomobject]@{ What = 'deluge'; Release = $Film.Release; Verified = (Test-FilmDataAt -Film $Film -Folder $script:Cl[$Film.ClientKind].Folder).Ok }); $true }
    function Invoke-TransmissionVerify { param($Film) [void]$script:Events.Add([pscustomobject]@{ What = 'verify'; Release = $Film.Release; Verified = (Test-FilmDataAt -Film $Film -Folder $script:Cl[$Film.ClientKind].Folder).Ok }); $true }

    # A film whose data sits in $placedIn, as New-TorrentFilm leaves it; the client says it looks in $lookIn.
    function New-PlacedFilm([string] $Kind, [string] $Layout, [string] $PlacedIn, [string] $Name) {
        $release = "$Name.2020.1080p.WEB-DL.x264-GOLDEN"
        $item = $(if ($Layout -eq 'file') { "$release.mkv" } else { $release })
        if ($Layout -eq 'file') { [IO.File]::WriteAllBytes((Join-Path (New-Item -ItemType Directory -Force -Path $PlacedIn).FullName $item), (New-Object byte[] 5000)) }
        else {
            New-Item -ItemType Directory -Force -Path (Join-Path $PlacedIn "$release\Sample") | Out-Null
            [IO.File]::WriteAllBytes((Join-Path $PlacedIn "$release\$release.mkv"), (New-Object byte[] 5000))
            [IO.File]::WriteAllBytes((Join-Path $PlacedIn "$release\Sample\sample.mkv"), (New-Object byte[] 700))
        }
        [pscustomobject]@{ Release = $release; Title = $Name; Protocol = 'torrent'; ClientKind = $Kind; Layout = $Layout; Hash = ('ab' * 20); DataFolder = $PlacedIn; DataItem = $item; DataFiles = @(Get-FilmFilesIn -Folder $PlacedIn -Item $item); SettleKey = $null }
    }

    foreach ($kind in 'qbittorrent', 'transmission', 'deluge') {
        foreach ($layout in 'file', 'folder') {
            $tag = "$kind $layout"
            $placed = Join-Path $pRoot "$kind-$layout\placed"
            $look = Join-Path $pRoot "$kind-$layout\look"
            New-Item -ItemType Directory -Force -Path $look | Out-Null
            # 1. The client looks somewhere other than where the data was put (qBittorrent's completed folder, rc.30.30).
            $f = New-PlacedFilm $kind $layout $placed "Gp$kind$layout"
            $script:Cl[$kind] = @{ Present = $true; State = 'checkingResumeData'; Progress = 0; Checking = $false; Folder = $look }
            $script:Events.Clear()
            $told = Invoke-FilmSettle -Film $f -WaitForRowSec 5
            $recheck = @($script:Events | Where-Object { $_.Release -eq $f.Release })
            Check "$tag - the data is moved to the folder the client names and verified there before the client is told to check" ((Test-FilmDataAt -Film $f -Folder $look).Ok -and -not (Test-Path (Join-Path $placed $f.DataItem)) -and $recheck.Count -eq 1 -and $recheck[0].Verified) ($recheck | Out-String)
            Check "$tag - the client is told to check once" $told "told=$told"
            Check "$tag - the film remembers where the data is" ($f.DataFolder -ieq $look)

            # 2. Data already where the client looks: nothing moves; qBittorrent and Deluge are told to check, Transmission checks on its own.
            $placed2 = Join-Path $pRoot "$kind-$layout\again"
            $f2 = New-PlacedFilm $kind $layout $placed2 "Gp$kind${layout}b"
            $script:Cl[$kind].Folder = $placed2
            $script:Events.Clear()
            $told2 = Invoke-FilmSettle -Film $f2 -WaitForRowSec 5
            $ev2 = @($script:Events | Where-Object { $_.Release -eq $f2.Release })
            if ($kind -eq 'transmission') { Check "$tag - Transmission is left alone when its data is in place" (-not $told2 -and $ev2.Count -eq 0) }
            else { Check "$tag - data already in place is verified, then the client is told to check" ($told2 -and $ev2.Count -eq 1 -and $ev2[0].Verified) }

            # 3. The files are not there (or not whole): the client is never told to check, and the reason is said.
            $f3 = New-PlacedFilm $kind $layout (Join-Path $pRoot "$kind-$layout\gone") "Gp$kind${layout}c"
            Remove-Item -LiteralPath (Join-Path $f3.DataFolder $f3.DataItem) -Recurse -Force
            $script:Cl[$kind].Folder = Join-Path $pRoot "$kind-$layout\elsewhere"
            $script:Events.Clear()
            $told3 = Invoke-FilmSettle -Film $f3 -WaitForRowSec 5
            Check "$tag - missing data is never checked blindly" (-not $told3 -and @($script:Events | Where-Object { $_.Release -eq $f3.Release }).Count -eq 0)
            $report = Get-FilmPlacementReport -Film $f3
            Check "$tag - the report names where the client looks, where the data was put, and what is missing" ($report -match [regex]::Escape($script:Cl[$kind].Folder) -and $report -match [regex]::Escape($f3.DataFolder) -and $report -match 'missing') $report
            $f4 = New-PlacedFilm $kind $layout (Join-Path $pRoot "$kind-$layout\short") "Gp$kind${layout}d"
            $biggest = $f4.DataFiles | Sort-Object Length -Descending | Select-Object -First 1
            [IO.File]::WriteAllBytes((Join-Path $f4.DataFolder $biggest.Rel), (New-Object byte[] 4000))
            $script:Cl[$kind].Folder = $f4.DataFolder
            $script:Events.Clear()
            $told4 = Invoke-FilmSettle -Film $f4 -WaitForRowSec 5
            Check "$tag - data that is short of its size is never checked, and the report says the size" (-not $told4 -and $script:Events.Count -eq 0 -and (Get-FilmPlacementReport -Film $f4) -match 'wrong size') (Get-FilmPlacementReport -Film $f4)
        }
    }

    Write-Host 'A film that is not finishing is looked at again when the client''s answer changes, never on a timer'
    $kind = 'qbittorrent'
    $a = Join-Path $pRoot 'loop\a'; $b = Join-Path $pRoot 'loop\b'
    $f = New-PlacedFilm $kind 'folder' $a 'Gploop'
    $script:Cl[$kind] = @{ Present = $true; State = 'stalledDL'; Progress = 0; Checking = $false; Folder = $a }
    $script:Events.Clear()
    [void](Invoke-FilmSettle -Film $f -WaitForRowSec 5)
    1..10 | ForEach-Object { [void](Invoke-FilmSettle -Film $f -IfChanged) }
    Check 'ten looks at a client that still shows 0% with the data verified send no further check' ($script:Events.Count -eq 1) "$($script:Events.Count) checks"
    $script:Cl[$kind].Folder = $b
    [void](Invoke-FilmSettle -Film $f -IfChanged)
    Check 'when the client names another folder the data follows it and the client is told to check again' ($script:Events.Count -eq 2 -and $script:Events[1].Verified -and (Test-FilmDataAt -Film $f -Folder $b).Ok)
    $script:Cl[$kind].Folder = $a; $script:Cl[$kind].Checking = $true
    [void](Invoke-FilmSettle -Film $f -IfChanged)
    Check 'a client that is checking is left to finish, and its files are not moved from under it' ($script:Events.Count -eq 2 -and (Test-FilmDataAt -Film $f -Folder $b).Ok)
    $script:Cl[$kind].Checking = $false
    [void](Invoke-FilmSettle -Film $f -IfChanged)
    Check 'when the check has ended the data is moved to the folder the client names, then checked' ($script:Events.Count -eq 3 -and $script:Events[2].Verified -and (Test-FilmDataAt -Film $f -Folder $a).Ok)

    Write-Host 'A film''s data is put where the client keeps it, before the torrent is sent'
    function Get-SuiteRoot { Join-Path $pRoot 'suite' }
    $baseFile = Join-Path $pRoot 'base.mkv'; [IO.File]::WriteAllBytes($baseFile, (New-Object byte[] 614400))
    $extraFile = Join-Path $pRoot 'extra.mkv'; [IO.File]::WriteAllBytes($extraFile, (New-Object byte[] 300))
    $script:Fx.Base = @{ small = $baseFile; big = $baseFile; extra = $extraFile }
    $script:Fx.IndexerIds = @{ torrent = 'ix' }
    function Add-SuiteTorrentRelease { param($Title, $Cat, $File, $Folder, $DummyMb) [pscustomobject]@{ size = 9000; infohash = ('cd' * 20); torrent = 'x.torrent' } }
    function Get-ClientDataFolder { param($Kind, $For) $script:Asked += $Kind; Join-Path $pRoot "client-$Kind" }
    foreach ($kind in 'qbittorrent', 'transmission', 'deluge') {
        foreach ($layout in 'file', 'folder') {
            $script:Asked = @()
            $film = New-TorrentFilm -Client $kind -Layout $layout -Label 'up'
            Check "$kind $layout - the client is asked where it keeps an unfinished torrent, and the whole film is there" ($script:Asked -contains $kind -and $film.DataFolder -ieq (Join-Path $pRoot "client-$kind") -and (Test-FilmDataAt -Film $film -Folder $film.DataFolder).Ok -and $film.DataFiles.Count -ge 1)
            $short = New-TorrentFilm -Client $kind -Layout $layout -Variant short -Label 'sh'
            $sz = ($short.DataFiles | Sort-Object Length -Descending | Select-Object -First 1).Length
            if ($kind -eq 'transmission') { Check "$kind $layout - a short Transmission film is cut short, since it takes a file of the right size as whole" ($sz -lt 614400) }
        }
    }
    $script:Asked = @()
    $dummy = New-TorrentFilm -Client 'qbittorrent' -Variant dummy -Label 'du'
    Check 'a film with no data does not ask the client for a folder or place anything' ($script:Asked.Count -eq 0 -and @($dummy.DataFiles).Count -eq 0)
}
finally { Remove-Item -LiteralPath $pRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

& {

Write-Host 'The folder a client keeps an unfinished torrent in is read from the client'
function Write-SuiteLog { param($Text) }
$script:Fx.Sessions.Clear()
function Open-KsQbit { param($Subject) [pscustomobject]@{ Base = 'http://127.0.0.1:1'; Session = $null } }
function Open-KsDeluge { param($Subject) [pscustomobject]@{ Base = 'http://127.0.0.1:1/json'; Session = $null } }
function Invoke-KsDeluge { param($Session, $Method, $Params, $Subject) if ($Method -eq 'core.get_config_value') { [pscustomobject]@{ result = 'C:\Downloads\Incomplete\Deluge'; error = $null } } else { [pscustomobject]@{ result = $null; error = $null } } }
$script:Prefs = [pscustomobject]@{ temp_path_enabled = $true; temp_path = 'C:/Downloads/Incomplete/qBittorrent'; save_path = 'C:/Downloads/Completed' }
$script:Cats = '{"deluno-movies":{"name":"deluno-movies","savePath":"C:/Downloads/Completed/Movies"}}' | ConvertFrom-Json
function Invoke-RestMethod { param($Uri, $Headers, $WebSession, [switch] $UseBasicParsing, $TimeoutSec) if ($Uri -like '*app/preferences') { $script:Prefs } else { $script:Cats } }
Check 'qBittorrent with an incomplete folder and automatic management: the category folder inside it' ((Get-ClientDataFolder -Kind 'qbittorrent') -eq 'C:\Downloads\Incomplete\qBittorrent\deluno-movies') (Get-ClientDataFolder -Kind 'qbittorrent')
$script:Cats = '{"deluno-movies":{"name":"deluno-movies","savePath":"C:/Downloads/Completed/Movies","download_path":"D:/Mine/Partial"}}' | ConvertFrom-Json
Check 'qBittorrent with an incomplete folder of its own on the category: that folder' ((Get-ClientDataFolder -Kind 'qbittorrent') -eq 'D:\Mine\Partial')
$script:Prefs.temp_path_enabled = $false; $script:Cats = '{"deluno-movies":{"name":"deluno-movies","savePath":"C:/Downloads/Completed/Movies","download_path":false}}' | ConvertFrom-Json
Check 'qBittorrent without an incomplete folder: the folder the category saves to' ((Get-ClientDataFolder -Kind 'qbittorrent') -eq 'C:\Downloads\Completed\Movies')
Check 'Deluge: its download location' ((Get-ClientDataFolder -Kind 'deluge') -eq 'C:\Downloads\Incomplete\Deluge')
function Invoke-KsTransmission { param($Method, $Arguments, $Subject) [pscustomobject]@{ arguments = [pscustomobject]@{ 'incomplete-dir-enabled' = $true; 'incomplete-dir' = 'C:/Downloads/Incomplete/Transmission'; 'download-dir' = 'C:/Downloads/Completed' } } }
Check 'Transmission: its incomplete folder when it has one' ((Get-ClientDataFolder -Kind 'transmission') -eq 'C:\Downloads\Incomplete\Transmission')
function Invoke-KsTransmission { param($Method, $Arguments, $Subject) [pscustomobject]@{ arguments = [pscustomobject]@{ 'incomplete-dir-enabled' = $false; 'incomplete-dir' = ''; 'download-dir' = 'C:/Downloads/Completed' } } }
Check 'Transmission: its download folder when it has no incomplete one' ((Get-ClientDataFolder -Kind 'transmission') -eq 'C:\Downloads\Completed')
}

Write-Host 'No settle step guesses a time'
$fixText = Get-Content -Raw (Join-Path $suite 'vm\Fixtures.Lib.ps1')
$fixAst = [System.Management.Automation.Language.Parser]::ParseInput($fixText, [ref]$null, [ref]$null)
function Get-FnText([string] $Name) { ($fixAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true) | Select-Object -First 1).Extent.Text }
foreach ($fn in 'Invoke-FilmSettle', 'Resolve-FilmPlacement', 'Invoke-DelugeSettle', 'Wait-ClientCheckDone', 'Wait-FilmFinished', 'Move-FilmDataToFolder', 'Get-ClientTorrent', 'Get-ClientDataFolder') {
    $t = Get-FnText $fn
    Check "$fn has no sleep" ($t -and $t -notmatch 'Start-Sleep') # reason: the text being checked for, not a call
}
$wait = Get-FnText 'Wait-FilmFinished'
Check 'Wait-FilmFinished no longer re-checks every twenty seconds' ($wait -notmatch 'TotalSeconds' -and $wait -notmatch '\* \(\$state' -and $wait -match 'Invoke-FilmSettle -Film \$Film -IfChanged')
$settle = Get-FnText 'Invoke-FilmSettle'
Check 'the client is told to check only after the placement is verified' ($settle.IndexOf('Resolve-FilmPlacement') -ge 0 -and $settle.IndexOf('Resolve-FilmPlacement') -lt $settle.IndexOf('Invoke-Recheck') -and $settle.IndexOf('Resolve-FilmPlacement') -lt $settle.IndexOf('Invoke-DelugeSettle') -and $settle.IndexOf('if (-not $p.Ok)') -lt $settle.IndexOf('Invoke-Recheck'))
$resolve = Get-FnText 'Resolve-FilmPlacement'
Check 'the placement is verified against the client''s own answer, then the files are tested' ($resolve -match 'Get-ClientTorrent' -and $resolve -match 'Test-FilmDataAt')
$newFilm = Get-FnText 'New-TorrentFilm'
Check 'a film''s data is placed where the client says, not at a written-down folder' ($newFilm -match 'Get-ClientDataFolder' -and $fixText -notmatch 'Fx\.Incomplete' -and $newFilm.IndexOf('Get-ClientDataFolder') -lt $newFilm.IndexOf('Add-SuiteTorrentRelease'))
Check 'a failed film is explained with the client''s folder and the files found' ($wait -match 'Get-FilmPlacementReport' -and $wait -match 'Add-Note' -and (Get-FnText 'Save-FilmClientEvidence') -match 'Get-FilmPlacementReport')
$allText = (Get-ChildItem (Join-Path $suite 'vm') -Recurse -Filter '*.ps1' | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
Check 'nothing refers to the old written-down incomplete folders' ($allText -notmatch 'Fx\.Incomplete')

Write-Host 'Every call to a download client is bounded, logged and needs no other group (the 14 Oct hang)'
$vmDir = Join-Path $suite 'vm'
$fixAst2 = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $vmDir 'Fixtures.Lib.ps1'), [ref]$null, [ref]$null)
$fixLoops = @($fixAst2.FindAll({ param($n) $n -is [System.Management.Automation.Language.WhileStatementAst] -or $n -is [System.Management.Automation.Language.DoWhileStatementAst] -or $n -is [System.Management.Automation.Language.DoUntilStatementAst] }, $true))
Check 'the fixture code has no while or do loop (every wait goes through Wait-Until, which has a limit)' ($fixLoops.Count -eq 0) (($fixLoops | ForEach-Object { 'line ' + $_.Extent.StartLineNumber }) -join ', ')
$fixFors = @($fixAst2.FindAll({ param($n) $n -is [System.Management.Automation.Language.ForStatementAst] }, $true))
Check 'every for loop in the fixture code counts to a fixed number' (@($fixFors | Where-Object { $_.Condition.Extent.Text -notmatch '-lt \d+$' }).Count -eq 0)
$fixCmds = @($fixAst2.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
Check 'the fixture code never prompts' (@($fixCmds | Where-Object { $_.GetCommandName() -in 'Read-Host', 'Get-Credential', 'pause' }).Count -eq 0)
$httpCalls = @($fixCmds | Where-Object { $_.GetCommandName() -in 'Invoke-RestMethod', 'Invoke-WebRequest' })
$httpFns = @($httpCalls | ForEach-Object { $p = $_.Parent; while ($p -and $p -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $p = $p.Parent }; if ($p) { $p.Name } else { '(script)' } } | Select-Object -Unique)
Check 'only Invoke-FxHttp sends a request to a client, and it always sets a time limit' (($httpFns -join ',') -match '^Invoke-FxHttp(,Start-FeedAndStubs)?$|^Start-FeedAndStubs,Invoke-FxHttp$' -and $fixText -match 'TimeoutSec = \$limit')
$vmCalls = foreach ($vf in Get-ChildItem $vmDir -Recurse -Filter '*.ps1') {
    $a = [System.Management.Automation.Language.Parser]::ParseFile($vf.FullName, [ref]$null, [ref]$null)
    $a.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { [pscustomobject]@{ File = $vf.Name; Name = $_.GetCommandName(); Text = $_.Extent.Text; Cmd = $_ } }
}
$noLimit = @($vmCalls | Where-Object { $_.Name -eq 'Wait-Until' -and $_.Text -notmatch '-TimeoutSec' })
Check 'every Wait-Until in the suite sets its time limit' ($noLimit.Count -eq 0) (($noLimit | ForEach-Object { $_.File + ': ' + $_.Text.Substring(0, [math]::Min(60, $_.Text.Length)) }) -join '; ')
$rawHttp = @($vmCalls | Where-Object { $_.Name -in 'Invoke-RestMethod', 'Invoke-WebRequest' -and $_.Text -notmatch 'TimeoutSec' -and $_.Text -notmatch '@p\b' })
Check 'every other request in the suite sets a time limit too' ($rawHttp.Count -eq 0) (($rawHttp | ForEach-Object { $_.File + ': ' + $_.Text.Substring(0, [math]::Min(60, $_.Text.Length)) }) -join '; ')
Check 'a client''s session is not a script block that reads its address from whoever calls it' ($fixText -notmatch '\$call = \{' -and $fixText -match 'function Invoke-KsDeluge')
Check 'the films group no longer needs the keep-seeding group: the client helpers are in Fixtures.Lib.ps1, not in G10' ((@('Open-KsQbit', 'Open-KsDeluge', 'Invoke-KsDeluge', 'Invoke-KsTransmission', 'Get-KsComponentLogin') | Where-Object { $groupText -match ('function\s+' + $_ + '\b') }).Count -eq 0 -and $fixText -match '\$script:Ks = @\{')

# A stand-in for the three clients' web interfaces on this PC (a real listener, so the real request code runs), and one that accepts
# a call and never answers.
$srvScript = {
    param($Port, $Mode)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$Port/")
    $l.Start()
    while ($l.IsListening) {
        $ctx = $l.GetContext()
        if ($Mode -eq 'silent') { Start-Sleep -Seconds 600; continue } # reason: the stand-in server that never answers, so the caller's own timeout is what the test shows
        $req = $ctx.Request; $res = $ctx.Response
        $body = (New-Object IO.StreamReader($req.InputStream)).ReadToEnd()
        $path = $req.Url.AbsolutePath
        $out = ''
        if ($path -eq '/stop') { $res.OutputStream.Close(); $l.Stop(); break }
        elseif ($path -eq '/json') {
            $m = ($body | ConvertFrom-Json).method
            $r = switch ($m) { 'auth.login' { $true } 'web.connected' { $true } 'core.get_config_value' { 'C:/Downloads/Incomplete/Deluge' } default { $null } }
            $out = (@{ result = $r; error = $null; id = 1 } | ConvertTo-Json -Compress)
        }
        elseif ($path -eq '/transmission/rpc') {
            if (-not $req.Headers['X-Transmission-Session-Id']) { $res.StatusCode = 409; $res.Headers.Add('X-Transmission-Session-Id', 'abc') }
            else { $out = '{"result":"success","arguments":{"incomplete-dir":"C:/Downloads/Incomplete/Transmission","incomplete-dir-enabled":true,"download-dir":"C:/Downloads/Completed"}}' }
        }
        elseif ($path -eq '/api/v2/auth/login') { $out = 'Ok.' }
        elseif ($path -eq '/api/v2/app/preferences') { $out = '{"temp_path_enabled":true,"temp_path":"C:/Downloads/Incomplete/qBittorrent","save_path":"C:/Downloads/Completed"}' }
        elseif ($path -eq '/api/v2/torrents/categories') { $out = '{"deluno-movies":{"name":"deluno-movies","savePath":"C:/Downloads/Completed/Movies"}}' }
        $bytes = [Text.Encoding]::UTF8.GetBytes($out)
        $res.ContentType = 'application/json'
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
        $res.OutputStream.Close()
    }
}
function Get-FreePort { $t = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $t.Start(); $p = $t.LocalEndpoint.Port; $t.Stop(); $p }
$okPort = Get-FreePort; $silentPort = Get-FreePort
$okPs = [powershell]::Create(); [void]$okPs.AddScript($srvScript).AddArgument($okPort).AddArgument('ok'); $okHandle = $okPs.BeginInvoke()
$silentPs = [powershell]::Create(); [void]$silentPs.AddScript($srvScript).AddArgument($silentPort).AddArgument('silent'); $silentHandle = $silentPs.BeginInvoke()
$coldRoot = Join-Path ([IO.Path]::GetTempPath()) ('cold-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $coldRoot | Out-Null
try {
    $up = $false
    for ($i = 0; $i -lt 50 -and -not $up; $i++) { try { Invoke-RestMethod -Uri "http://127.0.0.1:$okPort/json" -Method Post -Body '{"method":"web.connected"}' -ContentType 'application/json' -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep -Milliseconds 100 } } # reason: poll interval inside a loop bounded by its deadline
    Check 'the stand-in client answers' $up
    # A fresh Windows PowerShell that loads only the suite's shared code and the fixtures, as the films group sees them when it is
    # chosen alone: no group file, so nothing the keep-seeding group sets up. A decoy file stands for the test video: the old code
    # sent Deluge's calls to the film's own variable, which held that path.
    $decoy = Join-Path $coldRoot 'base-video.mkv'
    [IO.File]::WriteAllBytes($decoy, (New-Object byte[] 5000))
    $child = Join-Path $coldRoot 'cold.ps1'
    Set-Content -Path $child -Encoding ascii -Value @'
param($Vm, $OkPort, $SilentPort, $Decoy)
$ErrorActionPreference = 'Stop'
. (Join-Path $Vm 'Suite.Lib.ps1')
. (Join-Path $Vm 'Fixtures.Lib.ps1')
function Get-KsComponentLogin { param($Kind) [pscustomobject]@{ User = 'user'; Password = 'secret' } }
$out = [ordered]@{}
$script:Ks.Ports.qbittorrent = $OkPort; $script:Ks.Ports.deluge = $OkPort; $script:Ks.Ports.transmission = $OkPort
# Called the way New-TorrentFilm calls it, with a local named $base that holds a file path.
function Ask-LikeNewTorrentFilm { param($Kind) $base = $Decoy; $sess = $null; Get-ClientDataFolder -Kind $Kind -For 'GpTest.2020.1080p.WEB-DL.x264-GOLDEN' }
foreach ($k in 'deluge', 'qbittorrent', 'transmission') {
    $script:Fx.Sessions.Clear(); $script:Ks.TrSession = $null
    try { $out[$k] = Ask-LikeNewTorrentFilm -Kind $k } catch { $out[$k] = 'ERROR ' + $_.Exception.Message }
}
$out.DecoyBytes = (Get-Item -LiteralPath $Decoy).Length
# A client that takes the call and never answers: a short limit, and the time it took.
$script:Fx.ClientCallSec = 2; $script:Fx.ClientReadSec = 4
foreach ($k in 'deluge', 'qbittorrent', 'transmission') {
    $script:Ks.Ports.qbittorrent = $SilentPort; $script:Ks.Ports.deluge = $SilentPort; $script:Ks.Ports.transmission = $SilentPort
    $script:Fx.Sessions.Clear(); $script:Ks.TrSession = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $r = Ask-LikeNewTorrentFilm -Kind $k; $out["silent-$k"] = "answered $r" } catch { $out["silent-$k"] = $_.Exception.Message }
    $out["silent-$k-seconds"] = [math]::Round($sw.Elapsed.TotalSeconds, 1)
}
# A call whose address is not a local web address is refused before anything is sent (and the file is not touched).
try { Invoke-FxHttp -Client 'deluge' -Call 'core.get_config_value' -Subject 'x' -Uri $Decoy -Method Post -Body '{}' ; $out.FilePath = 'sent' } catch { $out.FilePath = $_.Exception.Message }
try { Invoke-FxHttp -Client 'deluge' -Call 'core.get_config_value' -Subject 'x' -Uri '' -Method Post -Body '{}' ; $out.EmptyUri = 'sent' } catch { $out.EmptyUri = $_.Exception.Message }
# The keep-seeding group's own setup, from a cold start (the 14 Oct run: Deluge's label call read $base from its caller and sent to no address).
$script:Ks.Ports.qbittorrent = $OkPort; $script:Ks.Ports.deluge = $OkPort; $script:Ks.Ports.transmission = $OkPort
. (Join-Path $Vm 'scenarios\G10-KeepSeeding.ps1')
foreach ($k in 'qbittorrent', 'deluge', 'transmission') { $script:Fx.Sessions.Clear(); try { New-KsClientCategory -Kind $k -Name 'golden-keep-seeding'; $out["category-$k"] = 'made' } catch { $out["category-$k"] = $_.Exception.Message } }
$out.DecoyBytesAfter = (Get-Item -LiteralPath $Decoy).Length
$out | ConvertTo-Json -Depth 4
'@
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $raw = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $child -Vm $vmDir -OkPort $okPort -SilentPort $silentPort -Decoy $decoy 2>&1
    $elapsed = $sw.Elapsed.TotalSeconds
    $jsonText = ($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | Where-Object { $_ -notmatch '^\d{4}-\d\d-\d\d \d\d:' }) -join "`n"
    $logLines = @($raw | ForEach-Object { "$_" } | Where-Object { $_ -match 'asking ' })
    $c = $null; try { $c = $jsonText | ConvertFrom-Json } catch { }
    Check 'the cold-start run gave an answer to read' ($null -ne $c) ($raw -join ' | ')
    if ($c) {
        Check 'Deluge: its download location, from a cold start with no keep-seeding setup' ($c.deluge -eq 'C:\Downloads\Incomplete\Deluge') "$($c.deluge)"
        Check 'qBittorrent: its folder, from a cold start with no keep-seeding setup' ($c.qbittorrent -eq 'C:\Downloads\Incomplete\qBittorrent\deluno-movies') "$($c.qbittorrent)"
        Check 'Transmission: its folder, from a cold start with no keep-seeding setup' ($c.transmission -eq 'C:\Downloads\Incomplete\Transmission') "$($c.transmission)"
        Check 'the keep-seeding group makes its category in each client from a cold start, with no address left empty' ($c.'category-deluge' -notmatch 'Uri|null or empty' -and $c.'category-qbittorrent' -notmatch 'Uri|null or empty' -and $c.'category-transmission' -eq 'made') "$($c.'category-deluge') | $($c.'category-qbittorrent')"
        Check 'a variable named $base in the caller (the film''s video) is never taken for a client''s address, and the file is untouched' ($c.DecoyBytes -eq 5000 -and $c.DecoyBytesAfter -eq 5000)
        Check 'an address that is a file path is refused before anything is sent, and says which step' ($c.FilePath -match 'was not sent' -and $c.FilePath -match 'deluge' -and $c.FilePath -match 'core.get_config_value')
        Check 'an empty address is refused too' ($c.EmptyUri -match 'was not sent' -or $c.EmptyUri -match 'Uri')
        foreach ($k in 'deluge', 'qbittorrent', 'transmission') {
            $msg = [string]$c."silent-$k"; $sec = [double]$c."silent-$k-seconds"
            Check "$k that never answers ends in a bounded error ($sec s, limit 2 s a call)" ($sec -lt 25 -and $msg -notmatch '^answered') $msg
            Check "$k's error names the client, the step, the film and what it waited for" ($msg -match $k -and $msg -match 'GpTest\.2020' -and $msg -match 'waiting for') $msg
        }
        Check 'the suite log gets a line before each client call, naming the client, the call and the film' (@($logLines | Where-Object { $_ -match 'asking deluge: auth.login \(GpTest' }).Count -ge 1 -and @($logLines | Where-Object { $_ -match 'asking transmission: session-get \(GpTest' }).Count -ge 1 -and @($logLines | Where-Object { $_ -match 'asking qbittorrent: preferences \(GpTest' }).Count -ge 1) ($logLines -join ' | ')
        Check 'the whole run, silent clients included, finished in a bounded time' ($elapsed -lt 120) "$elapsed s"
    }
}
finally {
    try { Invoke-RestMethod -Uri "http://127.0.0.1:$okPort/stop" -Method Post -Body 'x' -TimeoutSec 3 | Out-Null } catch { }
    $okPs.Stop(); $okPs.Dispose(); $silentPs.Stop(); $silentPs.Dispose()
    Remove-Item -LiteralPath $coldRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host ("{0} checks passed, {1} failed." -f $script:passes, $script:failures) -ForegroundColor $(if ($script:failures -eq 0) { 'Green' } else { 'Red' })
if ($script:failures -gt 0) { exit 1 }
