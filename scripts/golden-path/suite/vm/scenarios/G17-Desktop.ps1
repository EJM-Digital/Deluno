<#
    Things Deluno does on the computer's own desktop that a web page cannot show (coverage issues 1283 and 1278).

    companions.tray-install-picker   the Companion Tools page asks the tray to install an app only the tray installs (NZBGet): the ask is stored,
                                     the tray's picker window opens, naming it; a tool the tray does not install is refused in words.
    filesystem.native-folder-picker  POST /api/filesystem/native-folder-picker opens Windows' own folder window on this desktop; typing a folder
                                     and pressing OK returns it, Cancel returns "cancelled", and a second ask while it is open is refused in words.
#>

# Starts the POST in a background job (it waits for the person at the folder window) and returns the job.
if (-not ('GpEnum.Win' -as [type])) {
    Add-Type -Namespace GpEnum -Name Win -MemberDefinition @"
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumChildWindows(System.IntPtr parent, EnumProc p, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern System.IntPtr SendMessage(System.IntPtr h, uint msg, System.IntPtr w, System.Text.StringBuilder l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
"@
}

# Every top-level window of a process by name, found with the Win32 window list (visible or not), for when UI Automation's desktop
# list does not show a window the product says it opened.
function Get-ProcessTopWindows {
    param([string] $Process = 'Deluno')
    $script:GpEnumPids = @(Get-Process -Name $Process -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
    $script:GpEnumFound = New-Object System.Collections.ArrayList
    $cb = [GpEnum.Win+EnumProc]{ param($h, $l)
        $procId = [uint32]0; [void][GpEnum.Win]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($script:GpEnumPids -contains $procId) {
            $c = New-Object System.Text.StringBuilder 256; [void][GpEnum.Win]::GetClassName($h, $c, 256)
            $t = New-Object System.Text.StringBuilder 512; [void][GpEnum.Win]::GetWindowText($h, $t, 512)
            [void]$script:GpEnumFound.Add([pscustomobject]@{ Handle = $h; Class = $c.ToString(); Title = $t.ToString(); Visible = [GpEnum.Win]::IsWindowVisible($h); ProcessId = $procId })
        }
        $true }
    [void][GpEnum.Win]::EnumWindows($cb, [IntPtr]::Zero)
    @($script:GpEnumFound)
}

# The text of every Edit box inside a window, read with Windows' own messages: the folder window's Folder box does not always show up
# as an Edit in UI Automation's tree (rc.30.35 runs), but its text is always there.
function Get-WindowEditTexts {
    param([IntPtr] $Handle)
    $script:GpEnumFound = New-Object System.Collections.ArrayList
    $cb = [GpEnum.Win+EnumProc]{ param($h, $l)
        $c = New-Object System.Text.StringBuilder 256; [void][GpEnum.Win]::GetClassName($h, $c, 256)
        if ($c.ToString() -eq 'Edit') {
            $t = New-Object System.Text.StringBuilder 1024
            [void][GpEnum.Win]::SendMessage($h, 0x000D, [IntPtr]1024, $t) # WM_GETTEXT
            [void]$script:GpEnumFound.Add($t.ToString())
        }
        $true }
    [void][GpEnum.Win]::EnumChildWindows($Handle, $cb, [IntPtr]::Zero)
    @($script:GpEnumFound)
}

function Wait-NewFolderWindow {
    param([string[]] $Before, [int] $TimeoutSec = 90)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 0.5 -Condition {
        $hit = Get-DesktopWindows | Where-Object { $Before -notcontains "$($_.ProcessId)|$($_.Name)" -and ($_.Process -eq 'Deluno' -or $_.Class -eq '#32770') } | Select-Object -First 1
        if (-not $hit) {
            # UI Automation's desktop list can miss a window that is there: ask Windows for the process's own windows.
            $own = Get-ProcessTopWindows | Where-Object { $_.Visible -and $_.Class -eq '#32770' } | Select-Object -First 1
            if ($own) {
                $el = [System.Windows.Automation.AutomationElement]::FromHandle($own.Handle)
                $hit = [pscustomobject]@{ Name = [string]$el.Current.Name; Class = $own.Class; ProcessId = [int]$own.ProcessId; Process = 'Deluno'; Element = $el }
            }
        }
        $hit
    }
    if ($w.Ok) { $w.Value } else { $null }
}

function Start-NativePickerAsk {
    param([string] $InitialPath = 'C:\')
    # Deluno owns its folder window to whatever window is in front when it is asked. The suite's own elevated terminal can be that
    # window, and a program may not own a window of a higher-integrity one, so the window never opens (rc.30.35 runs, 22 Oct). A
    # person asks from their browser; here the taskbar is put in front first, so the ask never depends on what the last step left up.
    [void](Set-TaskbarForeground)
    $token = $script:S.Token; $base = $script:S.DelunoUrl
    Start-Job -ArgumentList $base, $token, $InitialPath -ScriptBlock {
        param($base, $token, $initial)
        try {
            $r = Invoke-WebRequest -Uri "$base/api/filesystem/native-folder-picker" -Method Post -UseBasicParsing -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" } -Body (@{ initialPath = $initial } | ConvertTo-Json) -TimeoutSec 120
            "$([int]$r.StatusCode) $($r.Content)"
        }
        catch {
            $code = 0; $body = ''
            try { $code = [int]$_.Exception.Response.StatusCode; $body = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch { }
            "$code $body"
        }
    }
}

function Invoke-GroupDesktop {
    Invoke-Scenario -Id 'companions.tray-install-picker' -Title "The Companion Tools page asks the tray to install NZBGet: the ask is stored and the tray's picker window opens, naming it; a tool the tray does not install is refused" -Body {
        $dataRoot = Join-Path $env:LOCALAPPDATA 'DelunoData'
        $pending = Join-Path $dataRoot 'config\pending-component-installs.json'
        Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue
        $list = Try-Deluno -Path '/api/companion-tools'
        Add-Check 'The Companion Tools list answers and Deluno can install tools here (a signed-in desktop, not a service)' ($list.Status -eq 200 -and $list.Data.availability.available) "HTTP $($list.Status)"
        $refused = Try-Deluno -Method POST -Path '/api/companion-tools/tray-install' -Body @{ toolIds = @('sabnzbd') }
        Add-Check 'A tool the tray does not install is refused: "Tick at least one app."' ($refused.Status -eq 400 -and $refused.Raw -match 'Tick at least one app') "HTTP $($refused.Status) $($refused.Raw)"
        try {
            $ask = Try-Deluno -Method POST -Path '/api/companion-tools/tray-install' -Body @{ toolIds = @('nzbget') }
            Add-Check 'Asking for NZBGet is accepted (HTTP 202)' ($ask.Status -eq 202) "HTTP $($ask.Status)"
            $dlg = Wait-DesktopWindow -NamePattern '^Set up download clients and Weir$' -Process 'Deluno' -TimeoutSec 90
            Add-Check "The tray's picker window opens" ($null -ne $dlg) $(if ($dlg) { $dlg.Name } else { 'windows: ' + (Format-DesktopWindows) })
            if ($dlg) {
                $boxes = @(Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'CheckBox' })
                $nz = $boxes | Where-Object { $_.Name -match 'NZBGet' } | Select-Object -First 1
                Add-Check 'NZBGet is in it (already installed here, so it says it will not be touched)' ($null -ne $nz) (($boxes | ForEach-Object { $_.Name }) -join ' | ')
                Save-Evidence -Name 'tray-picker-window.txt' -Text (Get-WindowText -Window $dlg)
                Click-WindowButton -Window $dlg -NamePattern '^Skip$' | Out-Null
                $closed = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { -not (Wait-DesktopWindow -NamePattern '^Set up download clients and Weir$' -Process 'Deluno' -TimeoutSec 1) }
                Add-Check 'Skip closes it' $closed.Ok ''
            }
        }
        finally { Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue }
    }

    Invoke-Scenario -Id 'filesystem.native-folder-picker' -Title "The native folder picker opens Windows' own folder window on this desktop: OK returns the folder shown, Cancel returns cancelled, and a second ask while it is open is refused in words" -Body {
        $target = 'C:\Media'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        $seenBefore = @(Get-DesktopWindows | ForEach-Object { "$($_.ProcessId)|$($_.Name)" })
        $job = Start-NativePickerAsk -InitialPath $target
        try {
            # The folder window is whichever new window the ask brings (Windows' own title for it differs between builds); its own text
            # is then checked for "Choose a folder for Deluno" below.
            $dlg = Wait-NewFolderWindow -Before $seenBefore -TimeoutSec 240 # reason: the first folder window of a session can take minutes to load Windows' shell parts
            Add-Check 'Windows'' own folder window (Browse For Folder) opens on this desktop, asking "Choose a folder for Deluno"' ($null -ne $dlg -and (Get-WindowText -Window $dlg) -match 'Choose a folder for Deluno') $(if ($dlg) { $dlg.Name } else { 'windows: ' + (Format-DesktopWindows) })
            if (-not $dlg) {
                Save-Evidence -Name 'folder-picker-deluno-windows.txt' -Text ((Get-ProcessTopWindows | ForEach-Object { "$($_.Handle) class=$($_.Class) visible=$($_.Visible) title='$($_.Title)'" }) -join "`n")
                $late = if (Wait-Job $job -Timeout 120) { Receive-Job $job } else { 'no answer' }
                Add-Check 'Deluno answered the ask when no window showed' $false "$late"
                return
            }
            # The dialog sets its selection just after it appears (the tree's selection reaches the Folder box a moment later), so wait for it.
            # Read the Folder box (an Edit) and, as the same evidence, the tree's selected item; either showing the folder's name proves it opened there.
            $leaf = Split-Path -Leaf $target
            $opensAt = ''
            $seen = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition {
                $controls = Get-WindowControls -Window $dlg
                $box = $controls | Where-Object { $_.Type -eq 'Edit' } | ForEach-Object {
                    try { "$($_.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value)" } catch { '' }
                } | Where-Object { $_ } | Select-Object -First 1
                $item = $controls | Where-Object { $_.Type -eq 'TreeItem' } | Where-Object {
                    try { $_.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { $false }
                } | Select-Object -First 1
                if (-not $box) {
                    $hwnd = [IntPtr]$dlg.Element.Current.NativeWindowHandle
                    if ($hwnd -ne [IntPtr]::Zero) { $box = Get-WindowEditTexts -Handle $hwnd | Where-Object { $_ -and ((Split-Path -Leaf $_.TrimEnd('')) -eq $leaf) } | Select-Object -First 1 }
                    if ($box) { $box = $leaf }
                }
                if ($box -eq $leaf) { "Folder box: '$box'" } elseif ($item -and $item.Name -eq $leaf) { "selected folder in the tree: '$($item.Name)'" } else { $null }
            }
            $opensAt = if ($seen.Ok) { $seen.Value } else { 'Folder box and selected tree item never showed the folder: ' + ((Get-WindowControls -Window $dlg | Where-Object { $_.Type -in 'Edit','TreeItem' } | ForEach-Object { "$($_.Type)='$($_.Name)'" }) -join ', ') }
            Add-Check "The window opens at the folder it was asked for (its Folder box shows '$(Split-Path -Leaf $target)')" $seen.Ok "$opensAt"
            $second = Try-Deluno -Method POST -Path '/api/filesystem/native-folder-picker' -Body @{ initialPath = 'C:\' } -TimeoutSec 20
            Add-Check 'A second ask while it is open is refused: "A folder window is already open on the server"' ($second.Status -eq 409 -and $second.Raw -match 'already open') "HTTP $($second.Status) $($second.Raw)"
            Click-WindowButton -Window $dlg -NamePattern '^OK$' | Out-Null
            $done = Wait-Job $job -Timeout 30
            $answer = if ($done) { Receive-Job $job } else { 'no answer' }
            $picked = $null
            if ($done) { try { $picked = ([regex]::Replace("$answer", '^\d+\s+', '') | ConvertFrom-Json) } catch { } }
            Add-Check 'OK returns a folder that exists, and not "cancelled"' ($null -ne $picked -and $picked.cancelled -eq $false -and $picked.path -and (Test-Path -LiteralPath $picked.path)) "$answer"
            Add-Check "OK returns the folder the window was asked to start at ($target)" ($null -ne $picked -and "$($picked.path)".TrimEnd('') -eq $target) "asked $target, OK returned $($picked.path)"
        }
        finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }

        # Cancel. The window can be slow to appear, so an ask that Deluno gave up on ("nobody answered") is made once more.
        $answer = 'no answer'; $dlg = $null
        for ($attempt = 1; $attempt -le 2 -and -not $dlg; $attempt++) {
            $seenBefore = @(Get-DesktopWindows | ForEach-Object { "$($_.ProcessId)|$($_.Name)" })
            $job = Start-NativePickerAsk -InitialPath $target
            try {
                $dlg = Wait-NewFolderWindow -Before $seenBefore -TimeoutSec 90
                if ($dlg) { Click-WindowButton -Window $dlg -NamePattern '^Cancel$' | Out-Null }
                $done = Wait-Job $job -Timeout 150
                $answer = if ($done) { Receive-Job $job } else { 'no answer' }
            }
            finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }
        }
        Add-Check 'Cancel returns "cancelled": true and no folder' ($null -ne $dlg -and "$answer" -match '^200 ' -and "$answer" -match '"cancelled":\s*true') "$answer"
    }
}





