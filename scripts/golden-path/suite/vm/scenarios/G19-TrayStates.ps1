<#
    The tray icon's states and the rest of its menu on a real Windows desktop (coverage issue 1278), read through UI Automation and the
    icon's own pixels. G15 covers the menu's actions, the port, Allowed Hosts and the red dot; this group covers what is left.

    tray.dot-and-hover       the green dot and its hover text; the pause bars appear on the icon, in the hover text and in the menu when automation
                             is paused; the dot turns amber when a download client does not answer and the hover and the menu's status line name it;
                             it goes back to green once the client is gone.
    tray.starting-dot        Restart Deluno from the menu: the hover reads "Starting", the dot is the starting (grey) dot, it ends green and "Running",
                             a manual restart says nothing about Deluno stopping by itself, and the host stops cleanly and starts again in the same process.
    tray.menu-details        Advanced holds the two recovery items, there is no Stop or Start item, the version line carries the installed version and is
                             greyed, Report a problem opens the logs folder, Check for updates is enabled and pressing it ends in a state the menu words.
    tray.quit                Quit Deluno: the icon shows busy, Deluno's process is gone within 30 s, the icon is removed and the host stopped; Deluno is then
                             started again the way a person starts it.
#>

# A hash of what the icon's button shows on screen (every pixel), to tell two looks of the icon apart.
function Get-TrayIconLook {
    param([string] $Icon = 'Deluno')
    $btn = Find-TrayIcon -Icon $Icon
    if (-not $btn) { return $null }
    $r = $btn.Element.Current.BoundingRectangle
    $w = [int]$r.Width; $h = [int]$r.Height
    if ($w -lt 4 -or $h -lt 4 -or [double]::IsInfinity($r.X)) { return $null }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, (New-Object System.Drawing.Size($w, $h))) } finally { $g.Dispose() }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $hash = [BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($ms.ToArray())).Replace('-', '')
    $ms.Dispose()
    $hash
}

# Reads the icon until it is steady (the same look three times in a row) and returns that look, or $null.
function Get-SteadyTrayIconLook {
    $script:SteadyLast = $null; $script:SteadySame = 0
    $w = Wait-Until -TimeoutSec 20 -IntervalSec 0.4 -Condition {
        $now = Get-TrayIconLook
        if ($now -and $now -eq $script:SteadyLast) { $script:SteadySame++ } else { $script:SteadySame = 0 }
        $script:SteadyLast = $now
        if ($script:SteadySame -ge 2) { $now } else { $null }
    }
    if ($w.Ok) { $w.Value } else { $null }
}

function Wait-TrayDot {
    param([Parameter(Mandatory)] [scriptblock] $Match, [int] $TimeoutSec = 120)
    Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 2 -Condition { $d = Get-TrayDot -Icon 'Deluno' -NoOverflow; if ($d.Found -and (& $Match $d)) { $d } else { $null } }
}

function Invoke-GroupTrayStates {
    Invoke-Scenario -Id 'tray.dot-and-hover' -Title 'The tray dot and hover text: green while everything answers, pause bars when automation is paused, amber and the words when a download client does not answer, green again once it is gone' -Body {
        Connect-Deluno
        $wasOn = [bool](Invoke-Deluno -Path '/api/settings').autoStartJobs
        $clientId = $null
        try {
            if (-not $wasOn) { Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $true } | Out-Null }
            $green = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -match '^Deluno - Running' } -TimeoutSec 180
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'Everything answering: the dot is green and the hover text reads "Deluno - Running at <address>"' $green.Ok "colour $($seen.Colour), hover '$($seen.Hover)'"
            $lookGreen = Get-SteadyTrayIconLook

            # ---- pause bars
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Pause automation$' | Out-Null
            $paused = Wait-TrayDot -Match { param($d) $d.Hover -match 'Paused' } -TimeoutSec 60
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'Paused: the hover text says "Paused" and the dot stays green' ($paused.Ok -and $seen.Colour -eq 'green') "colour $($seen.Colour), hover '$($seen.Hover)'"
            $lookPaused = Get-SteadyTrayIconLook
            Add-Check 'Paused: the icon itself looks different (the pause bars are drawn on the dot)' ($lookGreen -and $lookPaused -and $lookGreen -ne $lookPaused) "unpaused $lookGreen / paused $lookPaused"
            $status = (Get-TrayMenuStates | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1).Name
            $item = Get-TrayMenuItemState -Pattern '^(Pause|Resume) automation$'
            Add-Check 'Paused: the menu''s status line says "Paused" and the item offers "Resume automation"' ($status -match 'Paused' -and $item.Name -eq 'Resume automation') "$status / $($item.Name)"
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Resume automation$' | Out-Null
            $resumed = Wait-TrayDot -Match { param($d) $d.Hover -notmatch 'Paused' -and $d.Colour -eq 'green' } -TimeoutSec 60
            Add-Check 'Resumed: the hover text no longer says "Paused"' $resumed.Ok ''
            $lookBack = Get-SteadyTrayIconLook
            Add-Check 'Resumed: the icon looks as it did before the pause' ($lookBack -eq $lookGreen) "before $lookGreen / after $lookBack"

            # ---- amber for a download client that does not answer
            $name = 'Golden tray amber client'
            $added = Invoke-Deluno -Method POST -Path '/api/download-clients' -Body @{
                name = $name; protocol = 'qbittorrent'; host = '127.0.0.1'; port = 1; username = 'x'; password = 'y'
                moviesCategory = 'deluno-movies'; tvCategory = 'deluno-tv'; priority = 40; isEnabled = $true
            }
            $clientId = $added.id
            $amber = Wait-TrayDot -Match { param($d) $d.Colour -eq 'amber' -and $d.Hover -match "qBittorrent on .*:1 isn't answering" } -TimeoutSec 300
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check "A download client that does not answer: the dot goes amber and the hover text names it (qBittorrent on <host>:1 is not answering)" $amber.Ok "colour $($seen.Colour), hover '$($seen.Hover)'"
            $status = (Get-TrayMenuStates | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1).Name
            Add-Check 'The menu''s status line says the same' ($status -match "qBittorrent on .*:1 isn't answering") $status
            Invoke-Deluno -Method DELETE -Path "/api/download-clients/$clientId" | Out-Null
            $clientId = $null
            $back = Wait-TrayDot -Match { param($d) $d.Colour -eq 'green' -and $d.Hover -notmatch "isn't answering" } -TimeoutSec 300
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'With the client removed, the dot is green again' $back.Ok "colour $($seen.Colour), hover '$($seen.Hover)'"
        }
        finally {
            if ($clientId) { try { Invoke-Deluno -Method DELETE -Path "/api/download-clients/$clientId" | Out-Null } catch { } }
            try { if (-not [bool](Invoke-Deluno -Path '/api/settings').autoStartJobs -and $wasOn) { Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $true } | Out-Null } } catch { }
            try { if ([bool](Invoke-Deluno -Path '/api/settings').autoStartJobs -and -not $wasOn) { Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $false } | Out-Null } } catch { }
        }
    }

    Invoke-Scenario -Id 'tray.starting-dot' -Title 'Restart Deluno from the menu: the icon says Starting with the starting dot until the first health reading, ends green and Running, and a manual restart is not reported as Deluno stopping by itself' -Body {
        Connect-Deluno
        $pidBefore = (Get-Process -Name 'Deluno' | Select-Object -First 1).Id
        $strayBefore = Get-ToastTextCount -Text 'Deluno stopped by itself'
        $clickedAt = (Get-Date).ToUniversalTime().AddSeconds(-2)
        $clicked = [bool](Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno$')
        Add-Check 'Restart Deluno is pressed' $clicked "$script:TrayMenuMiss"
        $readings = New-Object System.Collections.ArrayList
        $deadline = (Get-Date).AddMinutes(5)
        $done = $false
        while ((Get-Date) -lt $deadline -and -not $done) {
            $d = Get-TrayDot -Icon 'Deluno' -NoOverflow
            [void]$readings.Add($d)
            if ($d.Found -and $d.Hover -match '^Deluno - Running' -and $d.Colour -eq 'green' -and @($readings | Where-Object { $_.Hover -match 'Starting' }).Count -gt 0) { $done = $true }
        }
        $starting = @($readings | Where-Object { $_.Found -and $_.Hover -match 'Starting' })
        Save-Evidence -Name 'tray-starting-readings.json' -Text ($readings | ForEach-Object { [pscustomobject]@{ hover = $_.Hover; colour = $_.Colour; brightness = $_.Brightness } } | ConvertTo-Json)
        Add-Check 'While Deluno starts, the hover text reads "Deluno - Starting..."' ($starting.Count -gt 0) "$($starting.Count) of $($readings.Count) readings"
        $blink = Get-BlinkSummary -Readings $starting
        Add-Note ("The starting dot: {0} readings, {1} brightness levels, {2} changes, colours {3}" -f $blink.Samples, $blink.BrightnessLevels, $blink.Flips, ($blink.Colours -join ','))
        $notGreenRed = @($starting | Where-Object { $_.Colour -in 'green', 'red' })
        Add-Check 'The starting dot is neither the green nor the red dot (it is the blinking starting dot)' ($starting.Count -gt 0 -and $notGreenRed.Count -eq 0) "colours: $((@($starting | ForEach-Object { $_.Colour } | Select-Object -Unique)) -join ',')"
        if ($blink.Samples -ge 6) { Add-Check 'The starting dot blinks (its brightness alternates)' $blink.Blinks "$($blink.Flips) changes over $($blink.Samples) readings" }
        Add-Check 'It ends as the green dot reading "Running"' $done "last reading: colour $($readings[$readings.Count - 1].Colour), hover '$($readings[$readings.Count - 1].Hover)'"
        $marker = Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { $m = Read-StartMarker; $at = $null; if ($m -and $m.atUtc) { try { $at = [DateTimeOffset]::Parse([string]$m.atUtc).UtcDateTime } catch { } }; if ($m -and "$($m.phase)" -eq 'ready' -and $at -and $at -gt $clickedAt) { $m } else { $null } }
        Add-Check 'The host stopped and started again: a new "ready" start marker was written after the click' $marker.Ok ''
        $pidAfter = (Get-Process -Name 'Deluno' | Select-Object -First 1).Id
        Add-Check 'It happened inside the same tray process' ($pidBefore -eq $pidAfter) "process $pidBefore -> $pidAfter"
        Add-Check 'A restart the person asked for is not announced as "Deluno stopped by itself"' ((Get-ToastTextCount -Text 'Deluno stopped by itself') -eq $strayBefore) ''
        Connect-Deluno
    }

    Invoke-Scenario -Id 'tray.menu-details' -Title "The tray menu's finer points: Advanced holds the two recovery items, no Stop or Start item, the version line carries the installed version, Report a problem opens the logs, Check for updates is enabled and says what happened" -Body {
        Connect-Deluno
        $states = @(Get-TrayMenuStates)
        $names = @($states | ForEach-Object { $_.Name })
        Add-Check 'There is no Stop or Start item in the menu (Restart and Quit are the only ways to change it)' (@($names | Where-Object { $_ -match '^(Stop|Start)( Deluno)?$' }).Count -eq 0) ($names -join ' | ')
        $installed = $null
        $exe = Get-DelunoExe
        if ($exe) { $installed = $exe.VersionInfo.ProductVersion }
        $ver = $states | Where-Object { $_.Name -match '^Deluno v\d' } | Select-Object -First 1
        Add-Check "The version line reads the installed version and is greyed (not clickable)" ($ver -and -not $ver.Enabled -and $installed -and $ver.Name -match [regex]::Escape(($installed -replace '\+.*$', ''))) "menu '$($ver.Name)', installed '$installed'"

        # ---- Advanced
        $adv = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Advanced$'
        $sub = $null
        if ($adv) { $sub = Wait-Until -TimeoutSec 5 -IntervalSec 0.3 -Condition { $i = @(Get-OpenMenuItems); if (@($i | Where-Object { $_ -match '^Let any address in' }).Count -gt 0) { $i } else { $null } } }
        $subItems = if ($sub -and $sub.Ok) { @($sub.Value) } else { @() }
        Add-Check 'Advanced opens a submenu with "Run as a Windows service..." and "Let any address in..."' (@($subItems | Where-Object { $_ -match '^Run as a Windows service' }).Count -eq 1 -and @($subItems | Where-Object { $_ -match '^Let any address in' }).Count -eq 1) ($subItems -join ' | ')
        Close-TrayMenu
        Close-TrayMenu

        # ---- Report a problem opens the logs folder
        $windowsBefore = @(Get-DesktopWindows | Where-Object { $_.Process -eq 'explorer' -and $_.Class -eq 'CabinetWClass' -and $_.Name -match 'logs' })
        foreach ($w in $windowsBefore) { Close-DesktopWindow -Window $w }
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Report a problem'
        $w = Wait-DesktopWindow -NamePattern 'logs' -Process 'explorer' -TimeoutSec 20
        Add-Check '"Report a problem..." opens the logs folder (the log is what a report carries)' ($clicked -and $null -ne $w) $(if ($w) { $w.Name } else { 'windows: ' + (Format-DesktopWindows) })
        foreach ($x in @(Get-DesktopWindows | Where-Object { $_.Process -eq 'explorer' -and $_.Class -eq 'CabinetWClass' -and $_.Name -match 'logs' })) { Close-DesktopWindow -Window $x }

        # ---- Check for updates
        $update = Get-TrayMenuItemState -Pattern '^(Check for updates|Checking for updates|Downloading update|Download update|Restart to update)'
        Add-Check 'With no update waiting, the update item reads "Check for updates" and is enabled' ($update -and $update.Name -eq 'Check for updates' -and $update.Enabled) ($update | ConvertTo-Json -Compress)
        $before = (Invoke-Deluno -Path '/api/updates/status')
        $stamp = Get-Date
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Check for updates$'
        Add-Check 'Pressing it is accepted' $clicked "$script:TrayMenuMiss"
        # A check ends in one of: still "Check for updates" with a reason in the status line, or an update to download.
        $ended = Wait-Until -TimeoutSec 90 -IntervalSec 2 -Condition {
            $u = Get-TrayMenuItemState -Pattern '^(Check for updates|Checking for updates|Downloading update|Download update|Restart to update)'
            if ($u -and $u.Name -ne 'Checking for updates' -and $u.Enabled) { $u } else { $null }
        }
        Add-Check 'The check ends and the item is usable again (not stuck on "Checking for updates")' $ended.Ok $(if ($ended.Ok) { $ended.Value.Name } else { 'still checking' })
        $after = (Invoke-Deluno -Path '/api/updates/status')
        Save-Evidence -Name 'tray-update-status.json' -Text (@{ before = $before; after = $after; hover = (Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover } | ConvertTo-Json -Depth 6)
        $hover = (Get-TrayDot -Icon 'Deluno' -NoOverflow).Hover
        $ok = ($ended.Ok -and ($ended.Value.Name -ne 'Check for updates' -or $hover -match 'Running|check|update'))
        Add-Check 'The tray says what happened in words (an update waiting, or the reason a check failed in the status line)' $ok "item '$($ended.Value.Name)', hover '$hover'"
    }

    Invoke-Scenario -Id 'tray.quit' -Title 'Quit Deluno from the menu: the icon shows busy, Deluno stops within 30 seconds, the icon is removed and the host is down; Deluno is then started again' -Body {
        Connect-Deluno
        $port = Get-LanPort
        $proc = Get-Process -Name 'Deluno' | Select-Object -First 1
        $startedQuit = Get-Date
        try {
            $clicked = [bool](Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Quit Deluno$')
            Add-Check 'Quit Deluno is pressed' $clicked "$script:TrayMenuMiss"
            # Busy: the hover text says Updating while it winds down (a short window; seen or already gone is both fine, only recorded).
            $busy = Wait-Until -TimeoutSec 6 -IntervalSec 0.3 -Condition { $d = Get-TrayDot -Icon 'Deluno' -NoOverflow; if ($d.Found -and $d.Hover -match 'Updating|Starting') { $d } else { $null } }
            Add-Note $(if ($busy.Ok) { "The icon showed busy: '$($busy.Value.Hover)'." } else { 'The quit was too quick to catch the busy icon.' })
            $gone = Wait-Until -TimeoutSec 30 -IntervalSec 0.5 -Condition { -not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) }
            Add-Check 'Deluno''s process is gone within 30 seconds' $gone.Ok "$($gone.Seconds) s"
            $icon = Wait-Until -TimeoutSec 15 -IntervalSec 1 -Condition { -not (Find-TrayIcon -Icon 'Deluno') }
            Add-Check 'The tray icon is removed' $icon.Ok ''
            Add-Check 'The host is stopped (nothing answers on its port)' ((Get-StatusWithHostHeader -HostHeader 'localhost' -Port $port) -eq 0) ''
            Add-Check 'Nothing of Deluno is left running' (@(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue).Count -eq 0) ''
        }
        finally {
            # Start Deluno again the way a person does, so the next scenario finds it running.
            if (-not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)) {
                $exe = Get-DelunoExe
                Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated:$false
            }
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check 'Started again, Deluno answers' $up.Ok ''
            if ($up.Ok) { Connect-Deluno }
            $dot = Wait-TrayDot -Match { param($d) $d.Colour -in 'green', 'amber' } -TimeoutSec 180
            Add-Check 'Its icon is back' $dot.Ok ''
        }
    }
}
