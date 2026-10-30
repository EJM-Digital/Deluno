<#
    Windows Firewall and "let other devices reach Deluno", on a real Windows (coverage issues 1283, 1278 and 1295).

    firewall.first-run-rule        the rule the first run added: Deluno's own, for its port, on every network, and Deluno reads it as open.
    firewall.screen-allow          with the rule removed, the guide's "Other devices" step names the problem, "Allow other devices" asks
                                   Windows for permission (a real permission prompt, answered Yes), the rule appears and the step reads
                                   "Other devices can reach Deluno".
    firewall.screen-declined       the same, answered No: no rule, and the screen says permission was not given.
    firewall.tray-allow-and-revoke the tray's "Allow other devices on your network..." and "Only allow this PC": the prompt, the rule, the
                                   menu items' enabled state, the balloon each says, "another rule still lets them in" (no prompt, the other
                                   rule stays), and a declined prompt.

    Deluno runs elevated in the suite VM (Setup is started as an administrator), where Windows asks nothing. A person's Deluno is not, so
    these start Deluno again without administrator rights first. The permission prompt is moved to the ordinary desktop for the scenario
    (Use-UacOnUserDesktop) so the administrator suite can read and press it, and put back afterwards.
#>

# Runs an action that asks Windows for elevation with the permission prompt on the ordinary desktop (on the secure desktop it hides
# every window from the suite, and UI Automation stops answering until it times out), and answers the prompt if one comes. Leaves what
# it saw in $script:Uac ($null when no prompt came within the wait).
function Invoke-WithElevationAnswer {
    param([Parameter(Mandatory)] [scriptblock] $Action, [bool] $Allow = $true, [int] $WaitSec = 30)
    $script:Uac = $null
    Use-UacOnUserDesktop {
        $out = Start-UacAnswerer -Allow $Allow -TimeoutSec $WaitSec
        & $Action
        $script:Uac = Wait-UacAnswer -OutFile $out -TimeoutSec ($WaitSec + 15)
    }
}

# One check about the permission request. A prompt must have appeared and been answered when the answer is No; for Yes, either a prompt
# was answered or Windows granted the request without one (the built-in Administrator has Admin Approval Mode off), and the rule that
# appears afterwards, checked separately, shows it was granted.
function Add-ElevationCheck {
    param([Parameter(Mandatory)] [string] $What, [bool] $Allow = $true)
    if ($script:Uac) {
        Add-Check "Windows' permission prompt for $What appeared and was answered $(if ($Allow) { 'Yes' } else { 'No' })" ($script:Uac.Pressed) $script:Uac.Text
    }
    elseif ($Allow) {
        Add-Check "Deluno asked Windows for elevation for $What and it was granted without a prompt (this VM's Administrator has Admin Approval Mode off)" $true 'no prompt appeared within the wait'
    }
    else {
        Add-Check "Windows' permission prompt for $What appeared so that it could be declined" $false 'no prompt appeared within the wait; windows: ' + (Format-DesktopWindows)
    }
}

function Get-LanPort {
    $r = Try-Deluno -Path '/api/system/lan-access'
    if ($r.Data -and $r.Data.port) { [int]$r.Data.port } else { 7879 }
}

# Every enabled inbound allow rule for this TCP port, as name | networks.
function Get-InboundRulesFor {
    param([Parameter(Mandatory)] [int] $Port)
    $rows = New-Object System.Collections.ArrayList
    foreach ($rule in @(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue)) {
        $pf = $rule | Get-NetFirewallPortFilter
        if ("$($pf.LocalPort)" -eq "$Port" -and "$($pf.Protocol)" -in 'TCP', '6') {
            [void]$rows.Add([pscustomobject]@{ Name = [string]$rule.DisplayName; Profile = [string]$rule.Profile })
        }
    }
    @($rows)
}

function Get-LanState { (Try-Deluno -Path '/api/system/lan-access').Data }

function Wait-LanState {
    param([Parameter(Mandatory)] [string] $State, [int] $TimeoutSec = 30)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 1 -Condition { $s = Get-LanState; if ($s -and $s.state -eq $State) { $s } else { $null } }
    if ($w.Ok) { $w.Value } else { $null }
}

function Remove-InboundRulesFor {
    param([Parameter(Mandatory)] [int] $Port)
    foreach ($row in @(Get-InboundRulesFor -Port $Port)) {
        & netsh.exe advfirewall firewall delete rule name="$($row.Name)" | Out-Null
    }
}

function Add-OwnLanRule {
    param([Parameter(Mandatory)] [int] $Port)
    & netsh.exe advfirewall firewall add rule name="Deluno (port $Port)" dir=in action=allow protocol=TCP localport=$Port profile=any | Out-Null
}

# Opens the guide at its "Other devices" step in a fresh Edge tab.
function Open-GuideNetworkStep {
    param([Parameter(Mandatory)] $Page)
    Open-DelunoPage -Page $Page -Path '/setup-guide' -Login $script:S.Login
    try { Click-PageText -Page $Page -Pattern '^\d+\s+Other devices' -TimeoutSec 20 | Out-Null }
    catch { Save-Evidence -Name "guide-page-text.txt" -Text ((Get-PageText -Page $Page) + "`n" + (Invoke-CdpEval -Page $Page -Script "location.href")); throw }
    Start-Sleep -Seconds 1 # reason: the step draws and asks the server whether the firewall lets other devices in; the page has no ready signal
}

# Presses Copy address and returns what is on the clipboard afterwards ('' when nothing new arrived).
function Copy-TrayAddress {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.Clipboard]::SetText('golden-suite-before-copy')
    [void](Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Copy address$')
    $w = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { $c = [System.Windows.Forms.Clipboard]::GetText(); if ($c -match '^http://') { $c } else { $null } }
    if ($w.Ok) { $w.Value } else { '' }
}

function Invoke-GroupFirewall {
    Invoke-Scenario -Id 'firewall.first-run-rule' -Title "The first run added Deluno's own Windows Firewall rule for its port on every network, and Deluno reads the port as open to other devices" -Body {
        $port = Get-LanPort
        $rules = @(Get-InboundRulesFor -Port $port)
        $own = @($rules | Where-Object { $_.Name -eq "Deluno (port $port)" })
        Add-Check "Windows Firewall holds the rule 'Deluno (port $port)', inbound, allow, TCP, for the port" ($own.Count -eq 1) (($rules | ForEach-Object { "$($_.Name) [$($_.Profile)]" }) -join '; ')
        Add-Check 'The rule covers every network (Windows files a home network it has not been told about as Public)' ($own.Count -eq 1 -and $own[0].Profile -eq 'Any') $(if ($own.Count) { $own[0].Profile } else { 'no rule' })
        $s = Get-LanState
        Add-Check 'Deluno reads the port as open (GET /api/system/lan-access)' ($s.state -eq 'open') ($s | ConvertTo-Json -Compress)
        $log = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $line = if (Test-Path $log) { @(Get-Content $log | Where-Object { $_ -match 'first run: ' }) | Select-Object -First 1 } else { $null }
        Add-Check "The install log has the first run's lines ('first run: ...')" ($null -ne $line) $(if ($line) { $line } else { 'no first-run line in install-hook.log' })
    }

    Invoke-Scenario -Id 'firewall.screen-allow' -Title "With the rule removed, the guide's Other devices step names the problem; Allow other devices asks Windows for elevation, adds Deluno's rule and the step reads that other devices can reach Deluno" -Body {
        $second = Start-DelunoAsSecondAdmin
        Add-Check "Deluno runs as an ordinary administrator (a filtered token), so Windows has to ask" ($second.Elevated -eq $false) "process $($second.ProcessId), elevated: $($second.Elevated)"
        try {
        $port = Get-LanPort
        Remove-InboundRulesFor -Port $port
        $blocked = Wait-LanState -State 'blocked'
        Add-Check 'With every rule for the port removed, Deluno reads it as blocked and offers to allow it' ($null -ne $blocked -and $blocked.canAllow) ($blocked | ConvertTo-Json -Compress)
        Start-Edge
        $page = New-CdpPage 'about:blank'
        try {
            Open-GuideNetworkStep -Page $page
            $card = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match 'Other devices cannot reach Deluno') { $t } else { $null } }
            $text = if ($card.Ok) { $card.Value } else { Get-PageText -Page $page }
            Save-Evidence -Name 'firewall-guide-blocked.txt' -Text $text
            Add-Check 'The step says other devices cannot reach Deluno and offers Allow other devices' ($text -match 'Other devices cannot reach Deluno' -and $text -match 'Allow other devices') (($text -replace '\s+', ' ').Substring(0, [math]::Min(300, $text.Length)))
            Invoke-WithElevationAnswer { Click-PageText -Page $page -Pattern '^Allow other devices$' -TimeoutSec 15 | Out-Null }
            Add-ElevationCheck -What 'adding the rule'
            $open = Wait-LanState -State 'open' -TimeoutSec 60
            Add-Check 'Deluno reads the port as open afterwards' ($null -ne $open) ($open | ConvertTo-Json -Compress)
            $rules = @(Get-InboundRulesFor -Port $port)
            $own = @($rules | Where-Object { $_.Name -eq "Deluno (port $port)" })
            Add-Check "Get-NetFirewallRule shows Deluno's rule for the port, on every network" ($own.Count -eq 1 -and $own[0].Profile -eq 'Any') (($rules | ForEach-Object { "$($_.Name) [$($_.Profile)]" }) -join '; ')
            Open-GuideNetworkStep -Page $page
            $after = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match 'Other devices can reach Deluno') { $t } else { $null } }
            Add-Check 'The step now reads "Other devices can reach Deluno"' $after.Ok $(if ($after.Ok) { ([regex]::Match($after.Value, 'Other devices can reach Deluno[^\r\n]*').Value) } else { (Get-PageText -Page $page) -replace '\s+', ' ' })
        }
        finally { Close-CdpPage -Page $page }
        }
        finally { Stop-DelunoAsSecondAdmin | Out-Null }
    }

    Invoke-Scenario -Id 'firewall.screen-declined' -Title "The permission prompt answered No: no rule is added, and the screen says permission was not given (needs a Windows that can show the prompt)" -Body {

        $second = Start-DelunoAsSecondAdmin
        Add-Check "Deluno runs as an ordinary administrator (a filtered token), so Windows has to ask" ($second.Elevated -eq $false) "process $($second.ProcessId), elevated: $($second.Elevated)"
        try {
        $port = Get-LanPort
        Remove-InboundRulesFor -Port $port
        Wait-LanState -State 'blocked' | Out-Null
        Start-Edge
        $page = New-CdpPage 'about:blank'
        try {
            Open-GuideNetworkStep -Page $page
            Invoke-WithElevationAnswer -Allow $false { Click-PageText -Page $page -Pattern '^Allow other devices$' -TimeoutSec 15 | Out-Null }
            Add-ElevationCheck -What 'adding the rule' -Allow $false
            $said = Wait-Until -TimeoutSec 30 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match 'it was not given') { $t } else { $null } }
            Add-Check 'The screen says Windows asked for permission and it was not given' $said.Ok $(if ($said.Ok) { ([regex]::Match($said.Value, 'Windows asked[^\r\n]*').Value) } else { (Get-PageText -Page $page) -replace '\s+', ' ' })
            Add-Check 'No rule was added for the port' (@(Get-InboundRulesFor -Port $port).Count -eq 0) (((Get-InboundRulesFor -Port $port) | ForEach-Object { $_.Name }) -join '; ')
            Add-Check 'Deluno still reads the port as blocked, with Allow still offered' ((Get-LanState).state -eq 'blocked' -and (Get-LanState).canAllow) ((Get-LanState) | ConvertTo-Json -Compress)
        }
        finally { Close-CdpPage -Page $page }
        Add-OwnLanRule -Port $port
        }
        finally { Stop-DelunoAsSecondAdmin | Out-Null }
    }

    Invoke-Scenario -Id 'firewall.tray-allow-and-revoke' -Title "The tray's Allow other devices and Only allow this PC: each item's enabled state, the rule Windows Firewall holds, the balloon each says, and another rule still letting devices in" -Body {
        # The tray icon belongs to the suite's own Deluno (the administrator's, which Windows never asks anything of). A copy run by another
        # account cannot put an icon on this taskbar, so the permission prompt itself is judged by the screen scenarios above.
        Connect-Deluno
        $port = Get-LanPort
        Remove-InboundRulesFor -Port $port
        Wait-LanState -State 'blocked' | Out-Null
        # The tray reads the firewall when Deluno starts and after its own changes: restart so it knows the rule is gone.
        Restart-DelunoFromTray | Out-Null
        $allow = Get-TrayMenuItemState -Pattern '^Allow other devices'
        $only = Get-TrayMenuItemState -Pattern '^Only allow this PC'
        Add-Check 'Blocked: "Allow other devices on your network..." is on the menu and enabled, "Only allow this PC" is disabled' ($allow -and $allow.Enabled -and $only -and -not $only.Enabled) ("allow: $($allow | ConvertTo-Json -Compress); only: $($only | ConvertTo-Json -Compress)")

        $copied = Copy-TrayAddress
        Add-Check 'Blocked: Copy address copies the localhost address (other devices cannot use a LAN one)' ($copied -eq "http://localhost:$port") ($copied -replace 'd+.d+.d+.d+', '<address>')

        # ---- Allow
        $okTitle = 'Other devices can reach Deluno'
        $before = Get-ToastTextCount -Text $okTitle
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Allow other devices'
        Add-Check 'The menu item was clicked' $clicked $script:TrayMenuMiss
        $open = Wait-LanState -State 'open' -TimeoutSec 60
        Add-Check "Deluno's rule is in Windows Firewall and Deluno reads the port as open" ($null -ne $open -and @(Get-InboundRulesFor -Port $port | Where-Object { $_.Name -eq "Deluno (port $port)" }).Count -eq 1) ($open | ConvertTo-Json -Compress)
        Add-Check 'The balloon "Other devices can reach Deluno" was shown' (Wait-ToastShown -Text $okTitle -Before $before) 'seen in Windows'' notification record'
        $copied = Copy-TrayAddress
        Add-Check 'Open: Copy address copies an address other devices can use, not localhost' ($copied -match "^http://(?!localhost)[^/s]+:$port$") ($copied -replace 'd+.d+.d+.d+', '<address>')
        $only = Get-TrayMenuItemState -Pattern '^Only allow this PC'
        Add-Check 'Open: "Only allow this PC" is enabled' ($only -and $only.Enabled) ($only | ConvertTo-Json -Compress)

        # ---- Only allow this PC
        $revokedTitle = 'Only this PC can reach Deluno'
        $before = Get-ToastTextCount -Text $revokedTitle
        $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Only allow this PC'
        Add-Check 'The menu item was clicked' $clicked $script:TrayMenuMiss
        $blocked = Wait-LanState -State 'blocked' -TimeoutSec 60
        Add-Check "Deluno's own rule is gone and the port reads as blocked" ($null -ne $blocked -and @(Get-InboundRulesFor -Port $port).Count -eq 0) ($blocked | ConvertTo-Json -Compress)
        Add-Check 'The balloon "Only this PC can reach Deluno" was shown' (Wait-ToastShown -Text $revokedTitle -Before $before) 'seen in Windows'' notification record'

        # ---- another rule still lets them in: Deluno did not add it, so it does not remove it. The tray reads the firewall when Deluno
        # starts and after its own changes, so Deluno is restarted (from the tray) with only the other rule in place.
        & netsh.exe advfirewall firewall add rule name="Golden suite other rule" dir=in action=allow protocol=TCP localport=$port profile=any | Out-Null
        try {
            Restart-DelunoFromTray | Out-Null
            Wait-LanState -State 'open' | Out-Null
            $otherTitle = 'Another firewall rule lets devices in'
            $before = Get-ToastTextCount -Text $otherTitle
            $only = Get-TrayMenuItemState -Pattern '^Only allow this PC'
            Add-Check 'With only another rule letting devices in, "Only allow this PC" is enabled' ($only -and $only.Enabled) ($only | ConvertTo-Json -Compress)
            $clicked = Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Only allow this PC'
            Add-Check 'The click was made' $clicked $script:TrayMenuMiss
            Add-Check 'The balloon "Another firewall rule lets devices in" was shown' (Wait-ToastShown -Text $otherTitle -Before $before) 'seen in Windows'' notification record'
            Add-Check 'The other rule is still there' (@(Get-InboundRulesFor -Port $port | Where-Object { $_.Name -eq 'Golden suite other rule' }).Count -eq 1) ''
        }
        finally { & netsh.exe advfirewall firewall delete rule name="Golden suite other rule" | Out-Null }

        # Put the machine back as the install left it.
        Add-OwnLanRule -Port $port
        Restart-DelunoFromTray | Out-Null
        Add-Check 'Deluno reads the port as open again, as the install left it' ((Wait-LanState -State 'open') -ne $null) ''
    }
}
