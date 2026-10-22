<#
    The tray icon's menu and states on a real Windows desktop (coverage issues 1278 and 1295), read and pressed through UI Automation.

    tray.menu-actions           the whole menu in the standard's order with each item's enabled state; Pause / Resume automation flips the
                                Automation switch; Copy address puts the address on the clipboard and says so; Open data folder and Open logs
                                folder open their folders; Start with Windows writes and removes its sign-in entry; Open Deluno opens the
                                browser at the live address; Restart Deluno restarts the host inside the same process.
    tray.change-port            Change port... : the dialog, a refused (busy) port, a new port: Deluno answers on it, the old one is closed,
                                the port is saved, the firewall rule stays on the old port (it is per port), and back to the default.
    tray.allowed-hosts-recovery Allowed Hosts locks an address out; Advanced > Let any address in... (confirm box, writes the override,
                                restarts, balloon) lets it in again.
    tray.red-when-host-fails    another program holds Deluno's port while Deluno restarts: the icon goes red, "Stopped - choose Restart
                                Deluno", with the host down, and Restart Deluno brings it back once the port is free.
#>

# One raw HTTP/1.1 GET with any Host header (an address Deluno has not been told about). Returns the status code, 0 on no answer.
function Get-StatusWithHostHeader {
    param([Parameter(Mandatory)] [string] $HostHeader, [string] $Path = '/api/auth/bootstrap-status', [int] $Port = 7879)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        $client.ReceiveTimeout = 8000; $client.SendTimeout = 8000
        $stream = $client.GetStream()
        $req = [Text.Encoding]::ASCII.GetBytes("GET $Path HTTP/1.1`r`nHost: $HostHeader`r`nConnection: close`r`n`r`n")
        $stream.Write($req, 0, $req.Length)
        $reader = New-Object IO.StreamReader($stream)
        $line = $reader.ReadLine()
        if ($line -match 'HTTP/\d\.\d (\d{3})') { return [int]$Matches[1] }
        0
    }
    catch { 0 }
    finally { $client.Close() }
}

function Get-AutomationOn { [bool](Invoke-Deluno -Path '/api/settings').autoStartJobs }

function Get-TrayAppSettingsPath { Join-Path $env:LOCALAPPDATA 'DelunoConfig\deluno.json' }

function Get-OpenWindowTitles { (Get-DesktopWindows | ForEach-Object { "$($_.Process)|$($_.Name)" }) }

# Closes the folder windows File Explorer opened for a path (by title), so the next check starts clean.
function Close-ExplorerWindows {
    param([Parameter(Mandatory)] [string] $TitlePattern)
    foreach ($w in @(Get-DesktopWindows | Where-Object { $_.Process -eq 'explorer' -and $_.Class -eq 'CabinetWClass' -and $_.Name -match $TitlePattern })) { Close-DesktopWindow -Window $w }
}

function Invoke-GroupTray {
    Invoke-Scenario -Id 'tray.menu-actions' -Title "The tray menu: every item in the standard's order and state, and each item does what it says (Pause and Resume, Copy address, the two folders, Start with Windows, Open Deluno, Restart Deluno)" -Body {
        Connect-Deluno
        $states = @(Get-TrayMenuStates)
        $names = @($states | ForEach-Object { $_.Name })
        Save-Evidence -Name 'tray-menu-states.json' -Text ($states | ConvertTo-Json)
        # The menu bar of another window can read as menu items too ("File / Edit / ..."); only Deluno's items are judged.
        $order = @('^Open Deluno$', '^Deluno - ', '^(Pause|Resume) automation$', '^Restart Deluno$', '^Copy address$', '^Allow other devices on your network', '^Only allow this PC$', '^Change port \(\d+\)', '^Open data folder$', '^Open logs folder$', '^Start with Windows$', '^(Check for updates|Checking for updates|Downloading update|Download update|Restart to update)', '^Deluno v\d', '^Report a problem', '^Advanced$', '^Quit Deluno$')
        $at = @(); $missing = @()
        foreach ($p in $order) { $i = [array]::FindIndex($names, [Predicate[string]] { param($n) $n -match $p }); if ($i -lt 0) { $missing += $p } else { $at += $i } }
        $sorted = ($at -join ',') -eq ((@($at) | Sort-Object) -join ',')
        Add-Check 'All sixteen menu items are there' ($missing.Count -eq 0) $(if ($missing.Count) { 'missing: ' + ($missing -join ', ') + '; found: ' + ($names -join ' | ') } else { $names -join ' | ' })
        Add-Check "They come in the standard's order (Open, status, Pause, Restart, Copy address, firewall, port, folders, Start with Windows, update, version, Report, Advanced, Quit)" $sorted ($at -join ',')
        $by = @{}; foreach ($s in $states) { $by[$s.Name] = $s }
        $status = $states | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1
        $ver = $states | Where-Object { $_.Name -match '^Deluno v\d' } | Select-Object -First 1
        Add-Check 'The status line and the version line are greyed (information, not actions)' ($status -and -not $status.Enabled -and $ver -and -not $ver.Enabled) "$($status.Name) / $($ver.Name)"
        $enabledWanted = @('^Open Deluno$', '^Restart Deluno$', '^Copy address$', '^Open data folder$', '^Open logs folder$', '^Quit Deluno$', '^Report a problem')
        $off = @($enabledWanted | Where-Object { $p = $_; -not ($states | Where-Object { $_.Name -match $p -and $_.Enabled }) })
        Add-Check 'While Deluno runs, Open, Restart, Copy address, the folders, Report and Quit are enabled' ($off.Count -eq 0) ($off -join ', ')
        $running = [string]($states | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1 | ForEach-Object { $Matches[1] })

        # ---- Pause / Resume automation: the same switch as Settings
        $was = (Get-AutomationOn)
        $want = -not $was
        $pause = Get-TrayMenuItemState -Pattern '^(Pause|Resume) automation$'
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^(Pause|Resume) automation$'
        $flipped = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { ((Get-AutomationOn) -eq $want) }
        Add-Check "'$($pause.Name)' flips the Automation switch (it was $was)" ($clicked -and $flipped.Ok) "clicked=$clicked now=$((Get-AutomationOn))"
        $label = (Get-TrayMenuItemState -Pattern '^(Pause|Resume) automation$').Name
        Add-Check 'The menu item then offers the opposite' ($label -ne $pause.Name) "$($pause.Name) -> $label"
        Invoke-TrayMenuItem -Icon 'Deluno' -Item '^(Pause|Resume) automation$' | Out-Null
        $back = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { ((Get-AutomationOn) -eq $was) }
        Add-Check 'Pressed again, it puts the switch back' $back.Ok "now=$((Get-AutomationOn))"

        # ---- Copy address
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.Clipboard]::SetText('golden-suite-before-copy')
        $before = Get-ToastTextCount -Text 'Address copied'
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Copy address$'
        $copied = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { $c = [System.Windows.Forms.Clipboard]::GetText(); if ($c -match '^http://[^/\s]+:\d+$') { $c } else { $null } }
        Add-Check 'Copy address puts Deluno''s address on the clipboard' ($clicked -and $copied.Ok) $(if ($copied.Ok) { 'the address is on the clipboard' } else { "clipboard: $([System.Windows.Forms.Clipboard]::GetText())" })
        Add-Check 'It says "Address copied" in a balloon' (Wait-ToastShown -Text 'Address copied' -Before $before) 'seen in Windows'' notification record'

        # ---- the two folders
        foreach ($f in @(@{ Item = '^Open data folder$'; Title = 'DelunoData' }, @{ Item = '^Open logs folder$'; Title = 'logs' })) {
            Close-ExplorerWindows -TitlePattern $f.Title
            $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item $f.Item
            $w = Wait-DesktopWindow -NamePattern $f.Title -Process 'explorer' -TimeoutSec 20
            Add-Check "'$($f.Item.Trim('^$'))' opens a folder window titled '$($f.Title)'" ($clicked -and $null -ne $w) $(if ($w) { $w.Name } else { 'windows: ' + (Format-DesktopWindows) })
            if ($w) { Close-DesktopWindow -Window $w }
        }

        # ---- Start with Windows
        $hasEntry = { $p = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue; [bool]($p -and ($p.PSObject.Properties | Where-Object { [string]$_.Value -match 'Deluno' })) }
        $startBefore = & $hasEntry
        Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null
        $toggled = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { (& $hasEntry) -ne $startBefore }
        Add-Check "Start with Windows $(if ($startBefore) { 'removes' } else { 'writes' }) Deluno's sign-in entry" $toggled.Ok "entry before: $startBefore"
        $tick = (Get-TrayMenuItemState -Pattern '^Start with Windows$').Checked
        Add-Check 'The menu item shows the tick that matches' ($tick -eq (& $hasEntry)) "tick=$tick entry=$(& $hasEntry)"
        Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Start with Windows$' | Out-Null
        $restored = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { (& $hasEntry) -eq $startBefore }
        Add-Check 'Pressed again, it puts the entry back as it was' $restored.Ok ''

        # ---- Open Deluno: the browser opens at the live address, and only because it was pressed
        $stamp = Get-Date
        $windowsBefore = @(Get-DesktopWindows | Where-Object { $_.Process -eq 'msedge' -and $_.Name -match 'Deluno' }).Count
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Open Deluno$'
        # The browser is started (a new process with Deluno's address) or, when one is open already, asked for another tab or window.
        $opened = Wait-Until -TimeoutSec 30 -IntervalSec 0.3 -Condition {
            $p = Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CreationDate -gt $stamp -and $_.CommandLine -match ':\d+' } | Select-Object -First 1
            if ($p) { $p } elseif (@(Get-DesktopWindows | Where-Object { $_.Process -eq 'msedge' -and $_.Name -match 'Deluno' }).Count -gt $windowsBefore) { 'a new Deluno window' } else { $null }
        }
        Add-Check 'Open Deluno opens the browser at Deluno''s address' ($clicked -and $opened.Ok) $(if ($opened.Ok -and $opened.Value.CommandLine) { ($opened.Value.CommandLine -replace '\d+\.\d+\.\d+\.\d+', '<address>').Substring(0, [math]::Min(160, $opened.Value.CommandLine.Length)) } elseif ($opened.Ok) { "$($opened.Value)" } else { 'no browser process or window appeared' })
        # ---- Restart Deluno: inside the same process, back as ready
        $pidBefore = (Get-Process -Name 'Deluno' | Select-Object -First 1).Id
        $secs = Restart-DelunoFromTray
        $pidAfter = (Get-Process -Name 'Deluno' | Select-Object -First 1).Id
        Add-Check 'Restart Deluno brings Deluno back (start marker ready, answering) in the same tray process' ($pidBefore -eq $pidAfter) "process $pidBefore -> $pidAfter in $secs s"
    }

    Invoke-Scenario -Id 'tray.change-port' -Title "Change port...: a busy port is refused in words, a free one is used (Deluno answers there, the old port is closed, the port is saved), the firewall rule moves to the new port, and the default port comes back" -Body {
        $old = Get-LanPort
        $new = $old + 11
        $listener = $null
        $stayed = $false
        try {
            # A busy port first: the dialog must refuse it and say so.
            $busy = $old + 12
            $listener = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Any, $busy); $listener.Start()
            Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Change port' -Modal | Out-Null
            $dlg = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 20
            Add-Check 'Change port... opens the dialog "Change Deluno''s port"' ($null -ne $dlg -and (Get-WindowText -Window $dlg) -match "Change Deluno's port") $(if ($dlg) { Get-WindowText -Window $dlg } else { Format-DesktopWindows })
            if (-not $dlg) { return }
            $edit = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Edit' } | Select-Object -First 1
            $setPort = { param($box, $n) $box.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([string]$n) }
            & $setPort $edit $busy
            Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
            $refused = Wait-Until -TimeoutSec 10 -IntervalSec 0.5 -Condition { (Get-WindowText -Window $dlg) -match 'already using port' }
            Add-Check 'A port another program holds is refused: "Another program on this computer is already using port ..."' $refused.Ok $(if ($refused.Ok) { 'refused in words' } else { Get-WindowText -Window $dlg })
            Add-Check 'Deluno is still on the old port after the refusal' (Test-DelunoAnswering) ''
            # Now a free port.
            & $setPort $edit $new
            Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
            $script:S.DelunoUrl = "http://localhost:$new"
            $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
            Add-Check "Deluno answers on the new port ($new)" $up.Ok ''
            $oldClosed = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { (Get-StatusWithHostHeader -HostHeader 'localhost' -Port $old) -eq 0 }
            Add-Check "The old port ($old) no longer answers" $oldClosed.Ok ''
            $saved = if (Test-Path (Get-TrayAppSettingsPath)) { Get-Content -Raw (Get-TrayAppSettingsPath) } else { '' }
            Add-Check 'The new port is saved in deluno.json' ($saved -match "\b$new\b") ''
            Connect-Deluno
            # The rule follows the port (#1372): Deluno's rule is on the new port, the old port has none of Deluno's, and Deluno itself reads other devices as let in.
            $state = Wait-LanState -State 'open' -TimeoutSec 60
            $ruleNew = @(Get-InboundRulesFor -Port $new | Where-Object { $_.Name -like 'Deluno*' }).Count
            $ruleOld = @(Get-InboundRulesFor -Port $old | Where-Object { $_.Name -like 'Deluno*' }).Count
            Add-Check "The firewall rule moved with the port: Deluno's rule is on the new port ($new), not the old ($old), so other devices reach the new port with no extra step" ($ruleNew -eq 1 -and $ruleOld -eq 0 -and $null -ne $state -and [int]$state.port -eq $new) "rules old=$ruleOld new=$ruleNew; Deluno reads: $((Get-LanState) | ConvertTo-Json -Compress)"
        }
        finally {
            if ($listener) { $listener.Stop() }
            # Back to the default port through the dialog's own option.
            if ((Test-DelunoAnswering) -and $script:S.DelunoUrl -ne "http://localhost:$old") {
                Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Change port' -Modal | Out-Null
                $dlg = Wait-DesktopWindow -NamePattern '^Deluno$' -Process 'Deluno' -TimeoutSec 20
                if ($dlg) {
                    $edit = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Edit' } | Select-Object -First 1
                    $edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([string]$old)
                    Click-WindowButton -Window $dlg -NamePattern '^Restart on this port$' | Out-Null
                }
                $script:S.DelunoUrl = "http://localhost:$old"
                Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering } | Out-Null
                Connect-Deluno
            }
        }
        Add-Check "Deluno is back on its own port ($old)" ((Get-LanPort) -eq $old) ''
    }

    Invoke-Scenario -Id 'tray.allowed-hosts-recovery' -Title "Allowed Hosts turns an address away; the tray's Advanced > Let any address in... (confirm box, override written to deluno.json, host restarted, balloon) lets it in again" -Body {
        $port = Get-LanPort
        $foreign = 'golden-suite.example.test'
        $overview = Invoke-Deluno -Path '/api/security/allowed-hosts'
        $savedEnabled = [bool]$overview.enabled
        $savedHosts = @($overview.allowedHosts)
        $settingsPath = Get-TrayAppSettingsPath
        $settingsBefore = if (Test-Path $settingsPath) { Get-Content -Raw $settingsPath } else { $null }
        try {
            Add-Check 'Before: a request for an address Deluno is not told about is answered' ((Get-StatusWithHostHeader -HostHeader $foreign -Port $port) -eq 200) "status $(Get-StatusWithHostHeader -HostHeader $foreign -Port $port)"
            $put = Try-Deluno -Method PUT -Path '/api/security/allowed-hosts' -Body @{ enabled = $true; allowedHosts = @('localhost', '127.0.0.1') }
            Add-Check 'Allowed Hosts is turned on with only this PC listed' ($put.Status -in 200, 204) "HTTP $($put.Status) $($put.Raw)"
            $refused = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { (Get-StatusWithHostHeader -HostHeader $foreign -Port $port) -ne 200 }
            Add-Check 'The unlisted address is now turned away' $refused.Ok "status $(Get-StatusWithHostHeader -HostHeader $foreign -Port $port)"
            Add-Check 'This PC (localhost) still gets in' ((Get-StatusWithHostHeader -HostHeader "localhost:$port" -Port $port) -eq 200) ''

            # Advanced > Let any address in...
            $before = Get-ToastTextCount -Text 'Deluno answers on any address again'
            $adv = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Advanced$'
            $item = $null
            if ($adv) {
                $w = Wait-Until -TimeoutSec 5 -IntervalSec 0.3 -Condition { Get-OpenMenuItems | Where-Object { $_ -match '^Let any address in' } | Select-Object -First 1 }
                $item = $w.Value
            }
            Add-Check 'Advanced shows "Let any address in..." (and "Run as a Windows service...")' ($null -ne $item) $(if ($item) { $item } else { 'Advanced did not open a submenu' })
            if ($item) {
                $uia = [System.Windows.Automation.AutomationElement]
                $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
                foreach ($el in $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
                    if ($el.Current.Name -match '^Let any address in') { $r = $el.Current.BoundingRectangle; Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)); break }
                }
                $confirm = Wait-DesktopWindow -NamePattern 'Let any address in\?' -TimeoutSec 20
                Add-Check 'A warning asks first ("Let any address in?")' ($null -ne $confirm) $(if ($confirm) { Get-WindowText -Window $confirm } else { Format-DesktopWindows })
                $clickedAt = (Get-Date).ToUniversalTime().AddSeconds(-2)
                $pressed = [bool]($confirm -and (Click-WindowButton -Window $confirm -NamePattern '^OK$'))
                Add-Check 'OK is pressed' $pressed ''
                $marker = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { $m = Read-StartMarker; $at = $null; if ($m -and $m.atUtc) { try { $at = [DateTimeOffset]::Parse([string]$m.atUtc).UtcDateTime } catch { $at = $null } }; if ($m -and "$($m.phase)" -eq 'ready' -and $at -and $at -gt $clickedAt -and (Test-DelunoAnswering)) { $m } else { $null } }
                Add-Check 'Deluno restarted its host (a new ready start marker after the click)' $marker.Ok ''
                $json = if (Test-Path $settingsPath) { Get-Content -Raw $settingsPath } else { '' }
                Add-Check 'deluno.json now carries the override (AllowedHostsForceDisabled: true)' ($json -match '(?i)"allowedHostsForceDisabled"\s*:\s*true') ''
                $in = Wait-Until -TimeoutSec 120 -IntervalSec 2 -Condition { (Get-StatusWithHostHeader -HostHeader $foreign -Port $port) -eq 200 }
                Add-Check 'The unlisted address is answered again after the restart' $in.Ok "status $(Get-StatusWithHostHeader -HostHeader $foreign -Port $port)"
                Add-Check 'The balloon "Deluno answers on any address again" was shown' (Wait-ToastShown -Text 'Deluno answers on any address again' -Before $before -TimeoutSec 60) 'seen in Windows'' notification record'
            }
        }
        finally {
            # Put Allowed Hosts and the override back as they were.
            Connect-Deluno
            Try-Deluno -Method PUT -Path '/api/security/allowed-hosts' -Body @{ enabled = $savedEnabled; allowedHosts = $savedHosts } | Out-Null
            if ($null -ne $settingsBefore -and (Test-Path $settingsPath) -and ((Get-Content -Raw $settingsPath) -ne $settingsBefore)) {
                Set-Content -Path $settingsPath -Value $settingsBefore -Encoding utf8
                Restart-DelunoFromTray | Out-Null
            }
        }
    }

    Invoke-Scenario -Id 'tray.red-when-host-fails' -Title "When another program holds Deluno's port while it restarts, the icon goes red and reads 'Stopped - choose Restart Deluno', and Restart Deluno brings it back once the port is free" -Body {
        $port = Get-LanPort
        $stopFile = 'C:\golden\suite\logs\hold-port.stop'
        Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
        # A job that takes the port the moment Deluno lets go of it, and keeps it until told to stop.
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
            # Five tries with waits that double from 15 s: it gives up in about four minutes.
            $red = Wait-Until -TimeoutSec 420 -IntervalSec 5 -Condition {
                $d = Get-TrayDot -Icon 'Deluno' -NoOverflow
                if ($d.Found -and $d.Colour -eq 'red' -and $d.Hover -match 'Stopped') { $d } else { $null }
            }
            $seen = Get-TrayDot -Icon 'Deluno' -NoOverflow
            Add-Check 'The Deluno icon goes red and its hover text says it is stopped' $red.Ok "colour $($seen.Colour), hover '$($seen.Hover)'"
            $menu = Get-TrayMenuStates
            $status = $menu | Where-Object { $_.Name -match '^Deluno - ' } | Select-Object -First 1
            Add-Check 'The menu''s status line says "Stopped - choose Restart Deluno"' ($status -and $status.Name -match 'Stopped') ($status.Name)
            $restart = $menu | Where-Object { $_.Name -match '^Restart Deluno$' } | Select-Object -First 1
            $pause = $menu | Where-Object { $_.Name -match 'automation$' } | Select-Object -First 1
            Add-Check 'Restart Deluno stays enabled and Pause automation is greyed while the host is down' ($restart.Enabled -and $pause -and -not $pause.Enabled) "restart=$($restart.Enabled) pause=$($pause.Enabled)"
            Add-Check 'Deluno does not answer' (-not (Test-DelunoAnswering)) ''
        }
        finally {
            New-Item -ItemType File -Force -Path $stopFile | Out-Null
            Wait-Job $holder -Timeout 30 | Out-Null
            Add-Note ("Port holder: " + ((Receive-Job $holder -ErrorAction SilentlyContinue) -join ' '))
            Remove-Job $holder -Force -ErrorAction SilentlyContinue
        }
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Restart Deluno'
        $back = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition { Test-DelunoAnswering }
        Add-Check 'Restart Deluno, with the port free again, brings Deluno back' ($clicked -and $back.Ok) ''
        if ($back.Ok) { Connect-Deluno }
        $dot = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition { $d = Get-TrayDot -Icon 'Deluno' -NoOverflow; if ($d.Found -and $d.Colour -in 'green', 'amber') { $d } else { $null } }
        Add-Check 'The icon is no longer red' $dot.Ok $(if ($dot.Ok) { $dot.Value.Colour } else { 'still red or unreadable' })
    }
}


