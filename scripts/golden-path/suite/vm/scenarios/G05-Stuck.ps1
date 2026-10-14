<#
    Stuck downloads: stuck at 0 bytes, stuck just short of the end, too slow, too few peers. Each is struck at its count, the
    plural wording is right, and a download stuck at 99.99% never reads 100% or "Not handed over" (#1225).

    Two phases, because Deluno gives a download one rule's strike at a time:
      A  "No progress" on, two strikes:           a torrent with no data and no peers, a torrent missing its last piece
                                                  (every torrent client), and a Usenet release that is never answered, and one
                                                  whose last article is never answered (every Usenet client).
      B  "Too few peers" (two strikes, floor 5) and "Too slow" (one strike, 2 MB/s floor) on, "No progress" off:
                                                  a torrent with no peers on each torrent client, and a slowed Usenet release
                                                  on each Usenet client.
    The check-in interval is one minute for the test and is put back at the end.
#>

function Get-StrikeLine {
    param([Parameter(Mandatory)] $Film, [string[]] $Lines)
    @($Lines | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Fixed a stuck download' }) | Select-Object -First 1
}

# Waits for each film to be struck (or the time to run out) and records what each looked like on the way.
function Wait-AllStruck {
    param([Parameter(Mandatory)] [hashtable] $Films, [int] $TimeoutSec = 420)
    $seen = @{}
    foreach ($k in $Films.Keys) { $seen[$k] = @{ MaxProgress = 0.0; Statuses = @{}; Line = $null; Seconds = $null; LastRow = $null } }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $rows = @(Get-TelemetryRows)
        $recent = @(Get-ActivityText -PageSize 300 -MaxPages 1)
        foreach ($k in $Films.Keys) {
            $f = $Films[$k]
            if (-not $seen[$k].Line) {
                $row = $rows | Where-Object { $_.releaseName -like "*$($f.Release)*" -or $_.title -like "*$($f.Title)*" } | Select-Object -First 1
                if ($row) {
                    $seen[$k].LastRow = $row
                    if ([double]$row.progress -gt $seen[$k].MaxProgress) { $seen[$k].MaxProgress = [double]$row.progress }
                    $seen[$k].Statuses["$($row.status)"] = $true
                }
                $line = Get-StrikeLine -Film $f -Lines $recent
                if ($line) { $seen[$k].Line = $line; $seen[$k].Seconds = [math]::Round(([DateTime]::UtcNow - $f.SentUtc).TotalSeconds, 0) }
            }
        }
        if (@($seen.Values | Where-Object { -not $_.Line }).Count -eq 0) { break }
        Start-Sleep -Seconds 4 # reason: poll interval inside a loop bounded by its deadline
    }
    $seen
}

# What a strike line says (#1206): "had fewer than 2 peers for 3 checks in a row", "stayed under 75 KB/s for 3 checks", "made no
# progress for 3 checks in a row". The speed rule counts checks without "in a row" (rc.30.22 showed "It stayed under 2048 KB/s for
# 1 check"), so the suite asks for that wording from the slow rule and "in a row" from the others. 1 check is singular, N checks plural.
function Assert-Struck {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] $Seen, [Parameter(Mandatory)] [int] $Strikes, [Parameter(Mandatory)] [string] $RuleWords, [bool] $ShortOfEnd = $false, [bool] $InARow = $true)
    Add-Check 'It was struck and replaced' ($null -ne $Seen.Line) $(if ($Seen.Line) { "after $($Seen.Seconds) s" } else { 'no "Fixed a stuck download" line in Activity' })
    if ($Seen.Line) {
        Add-Check "The line names the rule ($RuleWords)" ($Seen.Line -match $RuleWords) ''
        $m = [regex]::Match($Seen.Line, 'for (\d+) checks?')
        Add-Check "It was struck at its count ($Strikes)" ($m.Success -and [int]$m.Groups[1].Value -eq $Strikes) $(if ($m.Success) { $m.Value } else { 'no count in the line' })
        $unit = if ($Strikes -eq 1) { 'check' } else { 'checks' }
        $tail = if ($InARow) { ' in a row' } else { '' }
        $wordsOk = $Seen.Line -match ("for $Strikes $unit" + $tail + '(?![\w])')
        Add-Check "The wording is right for $Strikes (1 check, $Strikes checks$tail)" ([bool]$wordsOk) $(if ($m.Success) { $m.Value } else { '' })
        Add-Check 'Deluno removed it from the client and looked for another copy' ($Seen.Line -match 'looking for another copy' -and $Seen.Line -match 'removed it from') ''
        Save-Evidence -Name "strike-$($Film.Title).txt" -Text $Seen.Line
    }
    if ($ShortOfEnd) {
        Add-Check 'Its progress read below 100% all the way (never rounded up to 100%)' ($Seen.MaxProgress -gt 0 -and $Seen.MaxProgress -lt 100) ("highest progress seen: {0}" -f $Seen.MaxProgress)
        Add-Check 'It was never shown as "Not handed over"' (-not ($Seen.Statuses.Keys | Where-Object { $_ -match 'notHandedOver|not-handed' })) ("statuses seen: " + (($Seen.Statuses.Keys | Sort-Object) -join ', '))
        # The client's row as Deluno read it last, so a progress that never moves can be told from a row that was never found.
        if ($Seen.LastRow) { Save-Evidence -Name "row-$($Film.Title).json" -Text ($Seen.LastRow | ConvertTo-Json -Depth 3) }
    }
}

function Invoke-GroupStuck {
    $restore = @{ Rules = @(Invoke-Deluno -Path '/api/download-health/rules') }
    try {
        # ---------------- phase A: no progress, two strikes
        # Only what the chosen scenarios read is sent; the rules are the same as in a full run.
        $a = @{}
        $seenA = @{}
        $zeroUsenetKinds = @($script:Kinds.usenet | Where-Object { Test-ScenarioSelected "stuck.zero.$_" })
        $phaseA = Test-AnyScenarioSelected -Ids (@($script:Kinds.torrent + $script:Kinds.usenet | ForEach-Object { "stuck.zero.$_"; "stuck.short.$_" }))
        if ($phaseA) {
            Set-HealthRules -On @{ 'no-progress' = @{ Strikes = 2 } } -CheckMinutes 1
            foreach ($kind in $script:Kinds.torrent) {
                if (Test-ScenarioSelected "stuck.zero.$kind") { $a["zero.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Variant 'dummy' -Label 'zero') -Kind $kind }
                if (Test-ScenarioSelected "stuck.short.$kind") { $a["short.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Variant 'short' -Label 'short') -Kind $kind }
            }
            # A Usenet client works down its queue: a job whose every article is unanswered holds the connections and the jobs
            # behind it wait at 0 bytes (rc.30.22: SABnzbd's "short" job read 0% behind the "zero" job). So the job that is
            # stuck just short of the end goes first, and the one stuck at 0 bytes is sent once that one is struck and gone.
            foreach ($kind in $script:Kinds.usenet) {
                if (Test-ScenarioSelected "stuck.short.$kind") { $a["short.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Label 'short' -HangLast) -Kind $kind }
            }
            if ($a.Count -gt 0) { $seenA = Wait-AllStruck -Films $a }
            $zeroUsenet = @{}
            foreach ($kind in $zeroUsenetKinds) {
                $zeroUsenet["zero.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Label 'zero' -HangAll) -Kind $kind
            }
            if ($zeroUsenet.Count -gt 0) {
                $seenZero = Wait-AllStruck -Films $zeroUsenet
                foreach ($key in $zeroUsenet.Keys) { $a[$key] = $zeroUsenet[$key]; $seenA[$key] = $seenZero[$key] }
            }
        }
        foreach ($key in $a.Keys | Sort-Object) {
            $parts = $key -split '\.'
            $what = if ($parts[0] -eq 'zero') { 'stuck at 0 bytes' } else { 'stuck just short of the end' }
            Invoke-Scenario -Id "stuck.$($parts[0]).$($parts[1])" -Title "A $($parts[1]) download $what is struck at its count" -Body {
                Assert-Struck -Film $a[$key] -Seen $seenA[$key] -Strikes 2 -RuleWords 'No progress' -ShortOfEnd:($parts[0] -eq 'short')
            }
        }

        # ---------------- phase B: too few peers (torrents) and too slow (Usenet)
        $b = @{}
        $seenB = @{}
        $phaseB = Test-AnyScenarioSelected -Ids (@($script:Kinds.torrent | ForEach-Object { "stuck.peers.$_" }) + @($script:Kinds.usenet | ForEach-Object { "stuck.slow.$_" }))
        if ($phaseB) {
            Set-HealthRules -On @{ 'low-peers' = @{ Strikes = 2; Threshold = 5 }; 'too-slow' = @{ Strikes = 1; Threshold = 2048 } } -CheckMinutes 1
            foreach ($kind in $script:Kinds.torrent) { if (Test-ScenarioSelected "stuck.peers.$kind") { $b["peers.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Variant 'dummy' -Label 'peers') -Kind $kind } }
            foreach ($kind in $script:Kinds.usenet) { if (Test-ScenarioSelected "stuck.slow.$kind") { $b["slow.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Size 'big' -Label 'slow' -Slow) -Kind $kind } }
            $seenB = Wait-AllStruck -Films $b
        }
        foreach ($key in $b.Keys | Sort-Object) {
            $parts = $key -split '\.'
            if ($parts[0] -eq 'peers') {
                Invoke-Scenario -Id "stuck.peers.$($parts[1])" -Title "A $($parts[1]) download with too few peers is struck at its count" -Body {
                    Assert-Struck -Film $b[$key] -Seen $seenB[$key] -Strikes 2 -RuleWords 'peers|seeders'
                }
            }
            else {
                Invoke-Scenario -Id "stuck.slow.$($parts[1])" -Title "A $($parts[1]) download that is too slow is struck at its count" -Body {
                    Assert-Struck -Film $b[$key] -Seen $seenB[$key] -Strikes 1 -RuleWords 'slow|speed|stayed under' -InARow $false
                }
            }
        }
    }
    finally {
        # Put the rules back as they were before the phases, and stop the slowed articles slowing anything else.
        try { Set-Content -Path $script:Fx.Slow -Value '' -Encoding ascii; Set-Content -Path $script:Fx.Hang -Value '' -Encoding ascii } catch { }
        foreach ($rule in $restore.Rules) {
            try { Invoke-Deluno -Method PUT -Path "/api/download-health/rules/$($rule.id)" -Body $rule | Out-Null } catch { Write-SuiteLog "   could not restore rule $($rule.kind): $($_.Exception.Message)" }
        }
    }
}
