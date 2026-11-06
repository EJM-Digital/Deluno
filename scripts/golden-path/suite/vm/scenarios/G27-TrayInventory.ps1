<#
    The rest of the tray's inventory on a real Windows desktop (coverage issue 1278; docs/testing/TRAY-INVENTORY.md lists every menu item,
    icon state, hover text and balloon, and the scenario that drives each). G15 and G19 cover the menu, the dot and the red icon;
    G22 the clicks; this group covers what was left, read through UI Automation, the icon's pixels and Windows' notification record.

    tray.not-a-problem        what is NOT a problem leaves the dot green: a download client and an indexer that are switched off and cannot
                              be reached, and a library folder problem the person snoozed; the same things switched on, or no longer
                              snoozed, turn it amber.
    tray.hover-fits           a hover text too long for Windows' limit (127 characters) is shortened by whole words and ends in "...",
                              and the menu's status line says the same.
    tray.start-failure        another program holds Deluno's port while it restarts: while it tries again the menu says so and greys
                              Restart and Pause; each failed try is told in a "Deluno did not start" balloon, giving up in a
                              "Deluno could not start" one; Restart Deluno brings it back once the port is free.
    tray.port-balloon         Change port...: the menu item names the live port, and the move to a new port and the move back each say
                              so in a "Deluno is now at port N" balloon.
#>

# A long name made of whole words, exactly $Length characters or just under, ending in $Tail.
function New-LongTrayName {
    param([Parameter(Mandatory)] [string] $Tail, [int] $Length = 92)
    $words = 'Golden', 'hover', 'text', 'limit', 'check', 'with', 'a', 'very', 'long', 'indexer', 'name', 'that', 'goes', 'on', 'and', 'on', 'for', 'a', 'good', 'while'
    $name = ''
    foreach ($w in $words) { if (($name + ' ' + $w + ' ' + $Tail).Length -gt $Length) { break }; $name = ($name + ' ' + $w).Trim() }
    "$name $Tail"
}

function Invoke-GroupTrayInventory {
    Invoke-Scenario -Id 'tray.not-a-problem' -Title 'A download client and an indexer that are switched off, and a snoozed library-folder problem, leave the tray dot green; switched on, or no longer snoozed, they turn it amber' -Body {
        Connect-Deluno
        $clientId = $null; $offClientId = $null; $offIndexerId = $null; $libraryId = $null
        $folder = 'C:\golden\notaproblem-library'
        $downloads = 'C:\golden\notaproblem-downloads'
        try {
            $green = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -notmatch "isn't answering" } -TimeoutSec 300
            Add-Check 'Start: the dot is green and nothing is named' $green.Ok ''
            if (-not $green.Ok) {
                # Keep what the tray read and every client's stored health, so a leftover from an earlier scenario can be named.
                Save-Evidence -Name 'not-a-problem-start-state.txt' -Text (("hover: " + (Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover) + [Environment]::NewLine + ((Try-Deluno -Path '/api/download-clients').Raw))
            }

            $off = Invoke-Deluno -Method POST -Path '/api/download-clients' -Body @{
                name = 'Golden switched-off client'; protocol = 'qbittorrent'; host = '127.0.0.1'; port = 1; username = 'x'; password = 'y'
                moviesCategory = 'deluno-movies'; tvCategory = 'deluno-tv'; priority = 42; isEnabled = $false
            }
            $offClientId = $off.id
            $offIx = Invoke-Deluno -Method POST -Path '/api/indexers' -Body @{
                name = 'Golden switched-off indexer'; protocol = 'torznab'; privacy = 'public'; baseUrl = 'http://127.0.0.1:1/'; apiKey = 'golden'
                priority = 91; categories = '2000'; mediaScope = 'both'; isEnabled = $false; keepName = $true; routeToLibraries = $false
                rssEnabled = $false; automaticSearchEnabled = $false
            }
            $offIndexerId = $offIx.id

            # A control that is switched on and cannot be reached: when the tray names only it, the two switched-off ones were left out.
            $c = Invoke-Deluno -Method POST -Path '/api/download-clients' -Body @{
                name = 'Golden control client'; protocol = 'qbittorrent'; host = '127.0.0.1'; port = 1; username = 'x'; password = 'y'
                moviesCategory = 'deluno-movies'; tvCategory = 'deluno-tv'; priority = 43; isEnabled = $true
            }
            $clientId = $c.id
            $amber = Wait-TrayDot -Match { param($d) $d.Colour -eq 'amber' -and $d.Hover -match "qBittorrent on .*:1 isn't answering|Golden control client isn't answering" } -TimeoutSec 300
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'The switched-on client that does not answer turns the dot amber and is named' $amber.Ok "colour $($seen.Colour), hover '$($seen.Hover)'"
            Add-Check 'Only that one is counted: the two switched-off connections are not named and are not in "and N more"' ($seen.Hover -notmatch 'switched-off' -and $seen.Hover -notmatch 'and \d+ more') "hover '$($seen.Hover)'"
            Invoke-Deluno -Method DELETE -Path "/api/download-clients/$clientId" | Out-Null; $clientId = $null
            $back = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -notmatch "isn't answering" } -TimeoutSec 300
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'With only the switched-off ones left, the dot is green' $back.Ok "colour $($seen.Colour), hover '$($seen.Hover)'"

            # Switched on, the same client is a problem.
            $on = Try-Deluno -Method PUT -Path "/api/download-clients/$offClientId" -Body @{ isEnabled = $true }
            Add-Check 'The same client switched on is accepted' ($on.Status -in 200, 204) "HTTP $($on.Status) $($on.Raw)"
            $again = Wait-TrayDot -Match { param($d) $d.Colour -eq 'amber' } -TimeoutSec 300
            Add-Check 'Switched on, it turns the dot amber' $again.Ok "hover '$((Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover)'"
            Invoke-Deluno -Method DELETE -Path "/api/download-clients/$offClientId" | Out-Null; $offClientId = $null
            Invoke-Deluno -Method DELETE -Path "/api/indexers/$offIndexerId" | Out-Null; $offIndexerId = $null
            $clear = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -notmatch "isn't answering" } -TimeoutSec 300
            Add-Check 'All clear: the dot is green' $clear.Ok ''

            # ---- a snoozed problem
            New-Item -ItemType Directory -Force -Path $folder, $downloads | Out-Null
            $profile = (Get-Library -MediaType 'movies').qualityProfileId
            $lib = Try-Deluno -Method POST -Path '/api/libraries' -Body @{
                name = 'Golden snooze library'; mediaType = 'movies'; purpose = 'Main library'; rootPath = $folder; downloadsPath = $downloads
                qualityProfileId = $profile; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
                removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
            }
            Add-Check 'A library is made on a folder that exists' ($lib.Status -in 200, 201) "HTTP $($lib.Status) $($lib.Raw)"
            if ($lib.Status -notin 200, 201) { return }
            $libraryId = $lib.Data.id
            Remove-Item -LiteralPath $folder -Recurse -Force
            # Taking a folder away asks for no health pass: the library check runs on its fifteen-minute pass (SystemTasks.HealthChecks), so wait past one.
            $gone = Wait-TrayDot -Match { param($d) $d.Colour -eq 'amber' -and $d.Hover -match 'Golden snooze library' } -TimeoutSec 1200
            Add-Check 'The library whose folder is gone turns the dot amber' $gone.Ok "hover '$((Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover)'"
            $row = $null
            $found = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition {
                $h = Try-Deluno -Path '/api/system/health'
                $r = @($h.Data.checks | Where-Object { $_.id -like 'library:*:root' -and $_.name -match 'Golden snooze library' }) | Select-Object -First 1
                if ($r) { $r } else { $null }
            }
            Add-Check 'System > Health lists that problem' $found.Ok ''
            if (-not $found.Ok) { return }
            $row = $found.Value
            $sn = Try-Deluno -Method POST -Path "/api/system/health/$($row.id)/snooze" -Body @{ hours = 1 }
            Add-Check 'The problem is snoozed for an hour' ($sn.Status -eq 200) "HTTP $($sn.Status)"
            $quiet = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -notmatch 'Golden snooze library' } -TimeoutSec 300
            Add-Check 'Snoozed, it no longer colours the dot or the hover text' $quiet.Ok "hover '$((Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover)'"
            $un = Try-Deluno -Method POST -Path "/api/system/health/$($row.id)/unsnooze"
            Add-Check 'The snooze is taken off' ($un.Status -eq 200) "HTTP $($un.Status)"
            $loud = Wait-TrayDot -Match { param($d) $d.Colour -eq 'amber' -and $d.Hover -match 'Golden snooze library' } -TimeoutSec 300
            Add-Check 'No longer snoozed, the dot is amber again and names the library' $loud.Ok "hover '$((Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover)'"
        }
        finally {
            foreach ($id in @($clientId, $offClientId)) { if ($id) { try { Invoke-Deluno -Method DELETE -Path "/api/download-clients/$id" | Out-Null } catch { } } }
            if ($offIndexerId) { try { Invoke-Deluno -Method DELETE -Path "/api/indexers/$offIndexerId" | Out-Null } catch { } }
            if ($libraryId) { try { Invoke-Deluno -Method DELETE -Path "/api/libraries/$libraryId" | Out-Null } catch { } }
            Remove-Item -LiteralPath $folder, $downloads -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-Scenario -Id 'tray.hover-fits' -Title "A hover text longer than Windows' 127 characters is shortened by whole words and ends in '...'; the menu's status line says the same" -Body {
        Connect-Deluno
        $ids = New-Object System.Collections.ArrayList
        $names = @()
        try {
            foreach ($n in 1..3) {
                $name = New-LongTrayName -Tail "number $n"
                $names += $name
                $ix = Invoke-Deluno -Method POST -Path '/api/indexers' -Body @{
                    name = $name; protocol = 'torznab'; privacy = 'public'; baseUrl = 'http://127.0.0.1:1/'; apiKey = 'golden'
                    priority = 90 + $n; categories = '2000'; mediaScope = 'both'; isEnabled = $true; keepName = $true; routeToLibraries = $false
                    rssEnabled = $false; automaticSearchEnabled = $false
                }
                [void]$ids.Add($ix.id)
            }
            $cut = Wait-TrayDot -Match { param($d) $d.Colour -eq 'amber' -and $d.Hover -match 'Golden hover text' } -TimeoutSec 300
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'Three long-named indexers that do not answer turn the dot amber and the hover names the first' $cut.Ok "hover '$($seen.Hover)'"
            $hover = [string]$seen.Hover
            Add-Check 'The hover text is no longer than 127 characters' ($hover.Length -le 127) "$($hover.Length) characters"
            Add-Check 'It ends in "..." (the rest was left out)' ($hover -match '\.\.\.$') $hover
            $kept = ($hover -replace '^Deluno - ', '') -replace '\.\.\.$', ''
            # Any other connection that does not answer is counted too, so the number in "and N more" is not fixed.
            $fulls = @($names | ForEach-Object { "$_ isn't answering and 2 more"; "$_ isn't answering and 3 more"; "$_ isn't answering and 4 more" })
            $whole = @($fulls | Where-Object { $_.StartsWith($kept) -and $_.Length -gt $kept.Length -and $_[$kept.Length] -eq ' ' }).Count -gt 0
            Add-Check 'It was cut between words, never in the middle of one' $whole "kept '$kept'"
            $status = (Get-TrayMenuStates | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1).Name
            Add-Check 'The menu''s status line carries the same text' ($status -eq $hover) "$status"
        }
        finally {
            foreach ($id in $ids) { try { Invoke-Deluno -Method DELETE -Path "/api/indexers/$id" | Out-Null } catch { } }
        }
        $clear = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -notmatch "isn't answering" } -TimeoutSec 300
        Add-Check 'With the indexers gone, the dot is green again' $clear.Ok ''
    }

    Invoke-Scenario -Id 'tray.start-failure' -Title "While another program holds Deluno's port, a restart tries again (the menu says so, Restart and Pause are greyed, each try is told in a balloon), gives up in a balloon, and Restart Deluno brings it back" -Body {
        Connect-Deluno
        $port = Get-LanPort
        $stopFile = 'C:\golden\suite\logs\hold-port-balloons.stop'
        Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
        $retryBefore = Get-ToastTextCount -Text 'Deluno did not start'
        $gaveUpBefore = Get-ToastTextCount -Text 'Deluno could not start'
        $holder = Start-Job -ArgumentList $port, $stopFile -ScriptBlock {
            param($p, $stop)
            $deadline = (Get-Date).AddMinutes(12)
            $l = $null
            while ((Get-Date) -lt $deadline -and -not $l) {
                try { $x = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::IPv6Any, $p); $x.Server.DualMode = $true; $x.Start(); $l = $x } catch { Start-Sleep -Milliseconds 40 } # reason: poll interval inside a loop bounded by its deadline
            }
            if (-not $l) { return 'never got the port' }
            while ((Get-Date) -lt $deadline -and -not (Test-Path $stop)) { Start-Sleep -Milliseconds 500 } # reason: poll interval inside a loop bounded by its deadline
            $l.Stop(); 'held then released'
        }
        try {
            $null = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno'
            # While it tries again: the status line says Starting or "Stopped by itself - starting again (try N of 5)".
            $retry = Wait-Until -TimeoutSec 240 -IntervalSec 1 -Condition {
                $m = @(Get-TrayMenuStates)
                $s = $m | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1
                if ($s -and $s.Name -match 'Stopped by itself|Starting') { , $m } else { $null }
            }
            Add-Check 'While it tries again the menu''s status line says so' $retry.Ok ''
            if ($retry.Ok) {
                $m = @($retry.Value)
                $restart = $m | Where-Object { $_.Name -match '^Restart Deluno$' } | Select-Object -First 1
                $pause = $m | Where-Object { $_.Name -match '^(Pause|Resume) automation$' } | Select-Object -First 1
                $open = $m | Where-Object { $_.Name -match '^Open Deluno$' } | Select-Object -First 1
                $quit = $m | Where-Object { $_.Name -match '^Quit Deluno$' } | Select-Object -First 1
                $status = $m | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1
                Add-Check 'Restart Deluno and Pause automation are greyed while it starts; Open Deluno and Quit Deluno are not' ($restart -and -not $restart.Enabled -and $pause -and -not $pause.Enabled -and $open.Enabled -and $quit.Enabled) "$($status.Name): restart=$($restart.Enabled) pause=$($pause.Enabled) open=$($open.Enabled) quit=$($quit.Enabled)"
            }
            $red = Wait-Until -TimeoutSec 420 -IntervalSec 5 -Condition { $d = Get-TrayDot -Icon 'Deluno' -NoOverflow; if ($d.Found -and $d.Colour -eq 'red' -and $d.Hover -match 'Stopped') { $d } else { $null } }
            Add-Check 'It gives up: the icon is red and reads "Stopped - choose Restart Deluno"' $red.Ok "$((Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover)"
            Add-Check 'Each try that failed was told in a "Deluno did not start" balloon' ((Get-ToastTextCount -Text 'Deluno did not start') -gt $retryBefore) 'seen in Windows'' notification record'
            Add-Check 'Giving up was told in a "Deluno could not start" balloon' ((Get-ToastTextCount -Text 'Deluno could not start') -gt $gaveUpBefore) 'seen in Windows'' notification record'
        }
        finally {
            New-Item -ItemType File -Force -Path $stopFile | Out-Null
            Wait-Job $holder -Timeout 30 | Out-Null
            Add-Note ("Port holder: " + ((Receive-Job $holder -ErrorAction SilentlyContinue) -join ' '))
            Remove-Job $holder -Force -ErrorAction SilentlyContinue
        }
        $null = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno'
        $back = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'Restart Deluno, with the port free again, brings Deluno back' $back.Ok ''
        if ($back.Ok) { Connect-Deluno }
    }

    Invoke-Scenario -Id 'tray.port-balloon' -Title "Change port...: the menu item names the live port, and the move to another port and back each say so in a 'Deluno is now at port N' balloon" -Body {
        Connect-Deluno
        $old = Get-LanPort
        $new = $old + 13
        $move = {
            param([int] $To)
            $before = Get-ToastTextCount -Text "Deluno is now at port $To"
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Change port' -Modal | Out-Null
            $dlg = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 20
            if (-not $dlg) { return [pscustomobject]@{ Ok = $false; Before = $before; Why = 'the dialog did not open' } }
            $edit = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Edit' } | Select-Object -First 1
            $edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([string]$To)
            Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
            $script:S.DelunoUrl = "http://localhost:$To"
            [pscustomobject]@{ Ok = $true; Before = $before; Why = '' }
        }
        try {
            $item = Get-TrayMenuItemState -Pattern '^Change port'
            Add-Check 'The menu item names the live port: "Change port (N)..."' ($item -and $item.Name -eq "Change port ($old)...") "$($item.Name)"
            $m = & $move $new
            Add-Check 'Change port... opens its dialog and takes a free port' $m.Ok $m.Why
            if (-not $m.Ok) { return }
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check "Deluno answers on the new port ($new)" $up.Ok ''
            Add-Check 'A "Deluno is now at port N" balloon says so' (Wait-ToastShown -Text "Deluno is now at port $new" -Before $m.Before -TimeoutSec 60) 'seen in Windows'' notification record'
            if ($up.Ok) { Connect-Deluno }
            $item = Get-TrayMenuItemState -Pattern '^Change port'
            Add-Check 'The menu item now names the new port' ($item -and $item.Name -eq "Change port ($new)...") "$($item.Name)"
        }
        finally {
            if ($script:S.DelunoUrl -ne "http://localhost:$old") {
                $back = & $move $old
                if ($back.Ok) {
                    Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering } | Out-Null
                    Add-Check 'The move back to the first port is told in a balloon too' (Wait-ToastShown -Text "Deluno is now at port $old" -Before $back.Before -TimeoutSec 60) 'seen in Windows'' notification record'
                    Connect-Deluno
                }
            }
        }
        Add-Check "Deluno is back on its own port ($old)" ((Get-LanPort) -eq $old) ''
    }
}
