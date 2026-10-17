<#
    The Usenet provider set a second time on an NZBGet that is already configured (#1257).

    nzbget.setup-twice: Deluno's provider setup read NZBGet's settings with its `config` call, which adds NZBGet's own runtime-only
    values (ConfigFile, AppBin, AppDir, Version), and saved the whole list back. On the second run nzbget.conf held those four lines,
    NZBGet logged `Invalid option "ConfigFile"` ... and "Pausing all activities due to errors in configuration", and every download
    then sat at 0 bytes. The harness has already set the provider once (Set-UsenetStubInDeluno.ps1, in the environment's setup); this
    scenario sets it again through Deluno's real API, exactly the "set the provider again" case, and then asks NZBGet itself:
      - its status says downloads are not paused;
      - its log holds no `Invalid option` and no `errors in configuration`;
      - Deluno still shows the client healthy, with no "settings have errors" warning.
    It runs before the films, so film.nzbget meets the NZBGet this leaves behind.
#>

# Calls NZBGet's JSON-RPC directly (127.0.0.1, its shipped login: the installer never changes it) and returns the "result".
function Invoke-NzbgetRpc {
    param([Parameter(Mandatory)] $Client, [Parameter(Mandatory)] [string] $Method, [object[]] $Params = @())
    $pair = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('nzbget:tegbzn6789'))
    $body = @{ method = $Method; params = @($Params); id = 1 } | ConvertTo-Json -Depth 4 -Compress
    $r = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/jsonrpc" -f $Client.port) -Method Post -ContentType 'application/json' -Body $body `
        -Headers @{ Authorization = "Basic $pair" } -UseBasicParsing -TimeoutSec 20
    $r.result
}

# What is wrong with NZBGet, in words, from its `status` result and its `log` entries ('' entries means nothing). Pure, so the suite's
# own test runs it on NZBGet's recorded replies.
function Get-NzbgetConfigProblems {
    param($Status, $LogEntries = @())
    $problems = New-Object System.Collections.ArrayList
    if ($Status -and $Status.DownloadPaused) { [void]$problems.Add('NZBGet has all downloads paused (DownloadPaused is true).') }
    foreach ($e in @($LogEntries)) {
        $text = "$($e.Text)"
        if ($text -match 'Invalid option') { [void]$problems.Add("NZBGet's log: $text") }
        elseif ($text -match 'errors in configuration') { [void]$problems.Add("NZBGet's log: $text") }
    }
    @($problems)
}

function Invoke-GroupNzbgetSetup {
    Invoke-Scenario -Id 'nzbget.setup-twice' -Title 'The Usenet provider set again on an NZBGet that already has it leaves a valid config: not paused, no "Invalid option" in its log' -Body {
        $client = Get-DownloadClients | Where-Object { $_.protocol -eq 'nzbget' } | Select-Object -First 1
        if (-not $client) { throw 'Deluno has no NZBGet connection.' }

        # Before: the first setup left NZBGet clean (otherwise this scenario would only blame the second one for the first's damage).
        $before = Get-NzbgetConfigProblems -Status (Invoke-NzbgetRpc -Client $client -Method 'status') -LogEntries (Invoke-NzbgetRpc -Client $client -Method 'log' -Params @(0, 500))
        Add-Check 'After the first setup NZBGet is not paused and its log is clean' ($before.Count -eq 0) ($before -join ' | ')

        # The second setup: the same call the guided setup and the Connections screen make, a throwaway login the NNTP stub accepts.
        $body = @{ host = '127.0.0.1'; port = 1119; useSsl = $false; username = 'golden'; password = ('Gp-' + [guid]::NewGuid().ToString('N').Substring(0, 12)); connections = 2 }
        $set = Invoke-Deluno -Method POST -Path '/api/download-clients/usenet-provider' -Body $body
        $mine = @($set.clients) | Where-Object { $_.clientId -eq $client.id } | Select-Object -First 1
        Add-Check 'Deluno gave the provider to NZBGet a second time' ($set.saved -and $mine -and $mine.saved) $(if ($mine) { "$($mine.message)" } else { 'NZBGet is not in the answer' })

        # NZBGet reads its settings a moment after the reload; give it that, then look at its own status and log.
        $w = Wait-Until -TimeoutSec 90 -IntervalSec 3 -Condition {
            try { $s = Invoke-NzbgetRpc -Client $client -Method 'status'; if ($s -and -not $s.DownloadPaused) { $s } else { $null } } catch { $null }
        }
        Add-Check 'NZBGet answers and its downloads are not paused' $w.Ok $(if ($w.Ok) { "$([int]$w.Seconds) s after the second setup" } else { 'still paused or not answering after 90 s' })
        Start-Sleep -Seconds 5 # reason: lets NZBGet settle after it answered, so the second read can show a change
        $status = Invoke-NzbgetRpc -Client $client -Method 'status'
        $log = Invoke-NzbgetRpc -Client $client -Method 'log' -Params @(0, 500)
        $after = Get-NzbgetConfigProblems -Status $status -LogEntries $log
        Add-Check 'After the second setup NZBGet''s status is not paused and its log has no "Invalid option" and no "errors in configuration"' ($after.Count -eq 0) ($after -join ' | ')
        Save-Evidence -Name 'nzbget-setup-twice-log.txt' -Text ((@($log) | ForEach-Object { "{0} {1}" -f $_.Time, $_.Text }) -join "`n")

        # Deluno's own view: healthy, and no settings-error warning on the client.
        $row = Get-DownloadClients | Where-Object { $_.id -eq $client.id } | Select-Object -First 1
        $msg = "$($row.lastHealthMessage)"
        Add-Check 'Deluno shows NZBGet without a settings-error warning' ($msg -notmatch 'settings have errors') $msg
    }
}
