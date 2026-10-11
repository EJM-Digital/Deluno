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
foreach ($g in $plan) {
    Check "group function $($g.Function) is defined" ($groupText -match ('function\s+' + [regex]::Escape($g.Function) + '\b'))
}
foreach ($id in $allIds) {
    $prefix = ($id -replace '\.(qbittorrent|transmission|deluge|sabnzbd|nzbget)', '.$kind' -replace '\.(qbittorrent|transmission|deluge|sabnzbd|nzbget)$', '')
    $stem = ($id -split '\.')[0]
    Check "an Invoke-Scenario for '$id' exists in the group files" ($groupText -match ("-Id\s+[`"']" + [regex]::Escape($stem))) $stem
}
$topics = @(Get-ScenarioTopics)
Check 'every group says what it proves' (@($plan | Where-Object { $_.Name -notin $topics.Group }).Count -eq 0)

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
    while ($l.IsListening) {
        $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response
        $path = $req.Url.AbsolutePath; $auth = $req.Headers['Authorization']
        $code = 200; $body = '{}'
        if ($path -eq '/api/auth/login') { $logins++; $body = '{"accessToken":"tok-' + $logins + '","user":{"name":"golden"}}' }
        elseif ($path -eq '/stop') { $body = 'bye'; $l.Stop() }
        elseif ($auth -ne ('Bearer tok-' + $logins) -and $path -ne '/api/auth/bootstrap-status') { $code = 401; $body = '{"message":"sign in"}' }
        elseif ($path -eq '/api/integrations/processors/handoffs') { $body = '[{"id":"h1","releaseName":"Gpfilm.2020.1080p","status":"completed","importJobId":"j1","createdUtc":"2026-10-11T10:00:00Z"},{"id":"h2","releaseName":"Gpfilm.2020.1080p","status":"failed","createdUtc":"2026-10-11T11:00:00Z"}]' }
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
Start-Sleep -Milliseconds 700
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
    Check 'a scenario that checks nothing is an error, never a pass' ($res['t.empty'].status -eq 'error')
    $saved = Get-Content -Raw (Join-Path $tmp 'results\results.json') | ConvertFrom-Json
    Check 'results.json is written after each scenario' (@($saved.scenarios).Count -eq 4)
    $logText = Get-Content -Raw (Join-Path $tmp 'logs\suite.log')
    Check 'the log never holds a login' ($logText -notmatch [regex]::Escape($pw))

    . (Join-Path $suite 'vm\scenarios\G09-Problems.ps1')
    Check 'the story rules are defined' ($null -ne (Get-Command Get-StoryProblems -ErrorAction SilentlyContinue))
}
finally {
    try { Invoke-WebRequest "http://127.0.0.1:$port/stop" -UseBasicParsing -TimeoutSec 3 | Out-Null } catch { }
    $ps.Stop(); $ps.Dispose()
    if ($tmp -and (Test-Path $tmp)) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host 'The runner loads every library and group'
foreach ($lib in 'Tray.Lib.ps1', 'Cdp.Lib.ps1', 'Fixtures.Lib.ps1', 'Initialize-Environment.ps1', 'scenarios\Common.ps1') { . (Join-Path (Join-Path $suite 'vm') $lib) }
foreach ($f in Get-ChildItem (Join-Path $suite 'vm\scenarios') -Filter 'G*.ps1' | Sort-Object Name) { . $f.FullName }
foreach ($g in $plan) { Check "the runner can call $($g.Function)" ($null -ne (Get-Command $g.Function -CommandType Function -ErrorAction SilentlyContinue)) }
foreach ($fn in 'Initialize-SuiteEnvironment', 'Set-HealthRules', 'Send-Film', 'New-TorrentFilm', 'New-UsenetFilm', 'Wait-FilmFinished', 'Open-DelunoPage', 'Get-TrayDot', 'Invoke-TrayMenuItem', 'Stop-WeirApp', 'Start-WeirApp', 'Set-WeirPause', 'Get-WeirLogText', 'Test-OneStory') {
    Check "$fn is defined" ($null -ne (Get-Command $fn -ErrorAction SilentlyContinue))
}

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
        for ($i = 0; $i -lt 40 -and -not $up; $i++) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$cdpPort/json/version" -TimeoutSec 2 | Out-Null; $up = $true } catch { } }
        Check 'Edge opens its DevTools port' $up
        if ($up) {
            $page = New-CdpPage 'about:blank'
            $html = '<html><body><h1>Queue</h1><button onclick="document.title=''clicked''">Send to Weir again</button><div role="dialog">Drawer text</div></body></html>'
            Send-Cdp -Page $page -Method 'Page.navigate' -Params @{ url = 'data:text/html,' + [uri]::EscapeDataString($html) } | Out-Null
            Start-Sleep -Seconds 1
            Check 'the page text is read' ((Get-PageText -Page $page) -match 'Queue')
            Check 'the open drawer text is read' ((Get-DrawerText -Page $page) -match 'Drawer text')
            Check 'a button is found by its text and pressed with a real click' (Click-PageText -Page $page -Pattern '^Send to .* again')
            Start-Sleep -Milliseconds 300
            Check 'the click reached the page' ((Invoke-CdpEval -Page $page -Script 'document.title') -eq 'clicked')
            Check 'a button that is not there is not found' (-not (Click-PageText -Page $page -Pattern 'No such button' -TimeoutSec 0))
            $busyHtml = '<html><body><button id=remove disabled onclick="document.title=''removed''">Remove...</button><script>setTimeout(function(){document.getElementById("remove").disabled=false},1500)</script></body></html>'
            Send-Cdp -Page $page -Method 'Page.navigate' -Params @{ url = 'data:text/html,' + [uri]::EscapeDataString($busyHtml) } | Out-Null
            Start-Sleep -Milliseconds 400
            Check 'a disabled button is not clicked when the caller only probes' (-not (Click-PageText -Page $page -Pattern '^Remove' -TimeoutSec 0))
            $threw = ''; try { Click-PageText -Page $page -Pattern '^Remove' -TimeoutSec 1 | Out-Null } catch { $threw = $_.Exception.Message }
            Check 'a button that stays disabled fails with a message that says so' ($threw -match 'disabled') $threw
            Start-Sleep -Milliseconds 800
            Check 'a button that becomes enabled is clicked once it is' (Click-PageText -Page $page -Pattern '^Remove' -TimeoutSec 5)
            Start-Sleep -Milliseconds 300
            Check 'the click reached the enabled button' ((Invoke-CdpEval -Page $page -Script 'document.title') -eq 'removed')
            Close-CdpPage -Page $page
        }
    }
    finally { try { Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($prof) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } } catch { }; Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath $prof -Recurse -Force -ErrorAction SilentlyContinue }
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

Write-Host ''
Write-Host ("{0} checks passed, {1} failed." -f $script:passes, $script:failures) -ForegroundColor $(if ($script:failures -eq 0) { 'Green' } else { 'Red' })
if ($script:failures -gt 0) { exit 1 }
