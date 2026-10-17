<#
    GitHub's hourly limit, used up for real from the VM's address (#1181, proof owed in #1196).

    github-limit.messages: with the anonymous allowance gone, the two places that cannot work around the limit say so in plain words,
    with when it lifts in the viewer's own time:
      - Settings > General > the tracker definitions' "Update now" (POST /api/trackers/definitions/update-check/run; the state's
        error is the sentence, and the screen shows it through localizeResetTimes). The definitions come from GitHub's Contents API.
      - The connections' version check (GET /api/connections/versions: latestCheckPausedUntilUtc, shown beside the connection's
        name as "can't look for a newer version until <time> (GitHub's limit)"). A project's newest release is read from
        github.com's free release feed first, so the limit shows only when that route cannot be read AND the API is limited AND
        nothing was read before. The scenario makes exactly that case: github.com is pointed at this PC for a few minutes (the VM's
        hosts file, put back at the end) and Deluno is restarted from its tray menu, which empties its memory of earlier reads.

    The allowance stays used up for up to an hour after this scenario. It is the last group of the run, and the picker scenario
    (github-limit.picker) is the host's own, on a machine of its own.
#>

$script:GithubHostsTag = '# golden-suite-github-block'

function Get-HostsFilePath { Join-Path $env:SystemRoot 'System32\drivers\etc\hosts' }

# Points github.com at this PC so the free release feed cannot be read, and codeload.github.com too, the free archive the tracker
# definitions are taken from first (api.github.com is left alone: it is the route that is limited).
function Set-GithubFeedBlocked {
    param([bool] $Blocked)
    $path = Get-HostsFilePath
    $lines = @(Get-Content -LiteralPath $path -ErrorAction SilentlyContinue | Where-Object { $_ -notlike "*$($script:GithubHostsTag)*" })
    if ($Blocked) { $lines += "127.0.0.1 github.com www.github.com codeload.github.com $($script:GithubHostsTag)" }
    Set-Content -LiteralPath $path -Value $lines -Encoding ascii
    try { & ipconfig.exe /flushdns | Out-Null } catch { }
}

# Quits Deluno and starts it again the way a person does, from its tray menu, and waits until it answers as a new process.
function Restart-DelunoFromTray {
    # Restart Deluno rebuilds the host inside the same process (the tray keeps its process id), so a restart is told by Deluno's own
    # start marker (start-progress.json): written again after the click, in phase "ready", and Deluno answering.
    $clickedAt = (Get-Date).ToUniversalTime().AddSeconds(-2)
    $clicked = $false
    try { $clicked = [bool](Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno') } catch { $clicked = $false }
    if (-not $clicked) { throw ("Deluno's tray menu did not offer Restart Deluno ($script:TrayMenuMiss), so Deluno could not be restarted to empty its memory of earlier reads.") }
    $new = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition {
        $m = Read-StartMarker
        $at = $null
        if ($m -and $m.atUtc) { try { $at = [DateTimeOffset]::Parse([string]$m.atUtc).UtcDateTime } catch { $at = $null } }
        if ($m -and "$($m.phase)" -eq 'ready' -and $at -and $at -gt $clickedAt -and (Test-DelunoAnswering)) { $m } else { $null }
    }
    if (-not $new.Ok) { throw 'Deluno did not write a new "ready" start marker and answer within 4 minutes of Restart Deluno.' }
    Wait-DelunoUp | Out-Null
    Connect-Deluno
    $new.Seconds
}

function Invoke-GroupGithub {
    Invoke-Scenario -Id 'github-limit.messages' -Title "With GitHub's hourly limit used up, the tracker definitions' Update now and the connections' version check name the limit, with the reset in the viewer's local time" -Body {
        $limit = Use-GitHubLimitUp -MaxCalls 70
        Add-Note ("Calls made from the VM to use the allowance up: {0}; calls left before: {1}; after: {2}." -f $limit.Calls, $limit.RemainingAtStart, $limit.Remaining)
        if (-not $limit.Reachable) {
            Add-Check 'Not applicable: the limit could not be used up from this address' $true $limit.Reason
            return
        }
        $stamp = Get-GitHubLimitStamp -ResetEpoch $limit.ResetEpoch
        Add-Check "GitHub's anonymous allowance is used up from the VM (0 calls left)" ($limit.Remaining -eq 0) ("made {0} call(s); GitHub says it lifts at {1} UTC" -f $limit.Calls, $limit.ResetUtc.ToString('yyyy-MM-dd HH:mm'))

        # The definitions come from codeload.github.com's free archive first, which spends none of the allowance, so the archive and the
        # feed are pointed at this PC from here: only the limited API route is left, the case the sentence is for.
        $blocked = $false
        try {
            Set-GithubFeedBlocked -Blocked $true
            $blocked = $true

        # ---------------------------------------------------------- the tracker definitions' Update now
        $run = Try-Deluno -Method POST -Path '/api/trackers/definitions/update-check/run' -TimeoutSec 120
        $state = $run.Data
        Save-Evidence -Name 'github-limit-tracker-definitions.json' -Text $run.Raw
        Add-Check 'Update now (tracker definitions) answers, and says it failed' ($run.Status -eq 200 -and $state.status -eq 'failed') ("HTTP $($run.Status), status '$($state.status)'")
        $error1 = [string]$state.error
        Add-Check "Update now names GitHub's limit, not an unreachable source" ($error1 -match "GitHub's limit for checking releases from this network is used up until" -and $error1 -notmatch 'could not reach') $error1
        $m = [regex]::Match($error1, '(\d{4}-\d{2}-\d{2} \d{2}:\d{2}) UTC')
        $diff = $null
        if ($m.Success) {
            $said = [DateTime]::ParseExact($m.Groups[1].Value, 'yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
            $github = [DateTime]::ParseExact(($stamp -replace ' UTC$', ''), 'yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
            $diff = [math]::Abs(($said - $github).TotalMinutes)
        }
        Add-Check "The reset in the sentence is GitHub's own reset time (within a minute)" ($null -ne $diff -and $diff -le 1) $(if ($m.Success) { "Deluno says $($m.Groups[1].Value) UTC; GitHub says $stamp" } else { 'no reset time in the sentence' })

        # ... and on the screen, the way a person presses it: Settings > General, in the viewer's own time.
        Start-Edge
        $page = New-CdpPage 'about:blank'
        try {
            Open-DelunoPage -Page $page -Path '/settings/general' -Login $script:S.Login
            Click-PageText -Page $page -Pattern '^Update now$' | Out-Null
            $shown = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match "used up until") { $t } else { $null } }
            $text = if ($shown.Ok) { $shown.Value } else { Get-PageText -Page $page }
            $sentence = [regex]::Match($text, "GitHub's limit for checking releases[^\r\n]*").Value
            Save-Evidence -Name 'github-limit-tracker-definitions-screen.txt' -Text $text
            Add-Check "The screen names GitHub's limit after Update now" $shown.Ok $(if ($shown.Ok) { $sentence } else { 'the sentence did not appear within 60 s' })
            Add-Check 'The screen gives the reset in the viewer''s local time, never as UTC' ($shown.Ok -and $sentence -notmatch 'UTC' -and (Test-TextHasLocalReset -Text $sentence -ResetEpoch $limit.ResetEpoch)) $sentence
        }
        finally { Close-CdpPage -Page $page }

        # ---------------------------------------------------------- the connections' version check
        # (github.com and codeload.github.com are already pointed at this PC.)
            $secs = Restart-DelunoFromTray
            Add-Note "Deluno was restarted from its tray menu ($secs s) with github.com pointed at this PC, so its release reads start empty and the free feed cannot be read."
            # Reading the versions starts each project's release read in the background; it is asked again until one says why it cannot.
            $paused = Wait-Until -TimeoutSec 180 -IntervalSec 5 -Condition {
                $r = Try-Deluno -Path '/api/connections/versions'
                $rows = @($r.Data | Where-Object { $_.latestCheckPausedUntilUtc })
                if ($rows.Count -gt 0) { $rows } else { $null }
            }
            $all = Try-Deluno -Path '/api/connections/versions'
            Save-Evidence -Name 'github-limit-connection-versions.json' -Text $all.Raw
            Add-Check "At least one connection's newest-release read is paused by GitHub's limit (latestCheckPausedUntilUtc)" $paused.Ok $(if ($paused.Ok) { "$(@($paused.Value).Count) connection(s): " + ((@($paused.Value) | ForEach-Object { "$($_.kind)/$($_.id) until $($_.latestCheckPausedUntilUtc)" }) -join '; ') } else { 'none of the connections reported a pause in 3 minutes; ' + (($all.Data | ForEach-Object { "$($_.kind)=$($_.version)/latest $($_.latestVersion)/problem $($_.latestCheckProblem)" }) -join '; ') })
            if ($paused.Ok) {
                $row = @($paused.Value)[0]
                $until = [DateTimeOffset]::Parse([string]$row.latestCheckPausedUntilUtc).UtcDateTime
                $want = [DateTimeOffset]::FromUnixTimeSeconds($limit.ResetEpoch).UtcDateTime
                Add-Check "The pause lifts when GitHub's limit does (within a minute)" ([math]::Abs(($until - $want).TotalMinutes) -le 1.5) ("Deluno: {0:u}; GitHub: {1:u}" -f $until, $want)

                Start-Edge
                $page2 = New-CdpPage 'about:blank'
                try {
                    # The version sits beside the name on the screens that list connections; the download clients are the ones the VM has.
                    $line = $null
                    foreach ($route in '/indexers/download-clients', '/indexers/media-servers', '/settings/processing') {
                        Open-DelunoPage -Page $page2 -Path $route -Login $script:S.Login
                        $t = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { $x = Get-PageText -Page $page2; if ($x -match "can't look for a newer version until") { $x } else { $null } }
                        if ($t.Ok) { $line = [regex]::Match($t.Value, "can't look for a newer version until[^\r\n]*").Value; Save-Evidence -Name 'github-limit-connection-screen.txt' -Text $t.Value; break }
                    }
                    Add-Check "The connections screen says it can't look for a newer version, naming GitHub's limit" ($null -ne $line -and $line -match "\(GitHub's limit\)") $(if ($line) { $line } else { 'no screen showed the sentence' })
                    Add-Check 'It gives the reset in the viewer''s local time, never as UTC' ($null -ne $line -and $line -notmatch 'UTC' -and (Test-TextHasLocalReset -Text $line -ResetEpoch $limit.ResetEpoch)) $line
                }
                finally { Close-CdpPage -Page $page2 }
            }
        }
        finally {
            if ($blocked) { Set-GithubFeedBlocked -Blocked $false }
        }
    }
}
