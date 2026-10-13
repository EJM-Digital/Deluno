<#
.SYNOPSIS
    Reads the Windows tray in the VM's signed-in desktop session: an app's icon hover text, the colour of its status dot,
    and its context menu (UI Automation and a screen grab; nothing is installed).

.DESCRIPTION
    Dot-source this file in a session that has a desktop (a scheduled task with an interactive logon). The hover text of a
    tray icon is the Name of its button in the taskbar's notification area or its overflow; the dot is read from the pixels
    of that button. Windows PowerShell 5.1.

    What the tray looks like differs a little between Windows builds, so every function here returns what it found and
    never throws for "not there": Get-TrayDot returns Found=$false and the caller decides.
#>

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

if (-not ('Golden.Mouse' -as [type])) {
    Add-Type -Namespace Golden -Name Mouse -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extra);
'@
}

function Get-TrayButtons {
    $uia = [System.Windows.Automation.AutomationElement]
    $root = $uia::RootElement
    $buttonCond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $found = New-Object System.Collections.ArrayList
    foreach ($class in 'Shell_TrayWnd', 'TopLevelWindowForOverflowXamlIsland', 'NotifyIconOverflowWindow') {
        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($uia::ClassNameProperty, $class)))
        foreach ($w in $windows) {
            foreach ($b in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond)) {
                [void]$found.Add([pscustomobject]@{ Name = [string]$b.Current.Name; Element = $b; Area = $class })
            }
        }
    }
    @($found)
}

# Opens the hidden-icons overflow when the icon is in it. Returns $true when something was opened.
function Open-TrayOverflow {
    $chevron = @(Get-TrayButtons | Where-Object { $_.Name -match 'Show Hidden Icons|Notification Chevron|hidden icons' }) | Select-Object -First 1
    if (-not $chevron) { return $false }
    try { Wait-UiaClickable -Element $chevron.Element -What "the hidden-icons button" -TimeoutSec 3; $chevron.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 600; return $true } catch { return $false }
}

# Escape goes to whatever window is in front. During the install that is the component picker, and an Escape there
# cancels it (rc.30.21: the picker vanished 0.5 to 4 s after opening on every run). So a key is sent only when the window
# in front really is the shell's overflow (owned by explorer) or a pop-up menu (window class #32768).
if (-not ("GpFg.Win" -as [type])) {
    Add-Type -Namespace GpFg -Name Win -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
"@
}
function Get-ForegroundInfo {
    $h = [GpFg.Win]::GetForegroundWindow(); $procId = [uint32]0; [void][GpFg.Win]::GetWindowThreadProcessId($h, [ref]$procId)
    $sb = New-Object System.Text.StringBuilder 256; [void][GpFg.Win]::GetClassName($h, $sb, 256)
    $name = try { (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { "" }
    [pscustomobject]@{ Process = $name; Class = $sb.ToString() }
}
function Send-EscapeIfSafe([string] $Kind) {
    $fg = Get-ForegroundInfo
    $isMenu = $fg.Class -eq "#32768"
    $isShell = $fg.Process -eq "explorer" -or $fg.Process -eq "ShellExperienceHost" -or $fg.Process -eq "ShellHost"
    if (($Kind -eq "menu" -and $isMenu) -or ($Kind -eq "overflow" -and ($isShell -or $isMenu))) { [System.Windows.Forms.SendKeys]::SendWait("{ESC}") }
}
function Close-TrayOverflow { Send-EscapeIfSafe overflow; Start-Sleep -Milliseconds 200 }

function Find-TrayIcon {
    param([Parameter(Mandatory)] [string] $Icon)
    # A taskbar button for a browser tab titled "Deluno - ..." is not the tray icon.
    @(Get-TrayButtons | Where-Object { $_.Name -match "^$Icon\b" -and $_.Name -notmatch 'Edge|Chrome|Firefox|window' }) | Select-Object -First 1
}

# Classifies the dot in a tray icon's pixels: the most saturated cluster in the button's rectangle.
function Get-DotColour {
    param([System.Windows.Rect] $Rect)
    $w = [int]$Rect.Width; $h = [int]$Rect.Height
    if ($w -lt 4 -or $h -lt 4 -or [double]::IsInfinity($Rect.X)) { return $null }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen([int]$Rect.X, [int]$Rect.Y, 0, 0, (New-Object System.Drawing.Size($w, $h))) } finally { $g.Dispose() }
    $px = New-Object System.Collections.ArrayList
    for ($x = 0; $x -lt $w; $x++) {
        for ($y = 0; $y -lt $h; $y++) {
            $c = $bmp.GetPixel($x, $y)
            [void]$px.Add([pscustomobject]@{ H = $c.GetHue(); S = $c.GetSaturation(); B = $c.GetBrightness() })
        }
    }
    $bmp.Dispose()
    Get-DotClassFromPixels -Pixels @($px)
}

# Classifies a dot from its pixels (hue, saturation, brightness each). Saturated pixels vote by their median hue; a handful of
# stray saturated pixels (anti-aliasing, a neighbour's glow) is not a dot, and a dot with no colour in it is grey (the first-run
# tray draws a grey dot while Deluno sets itself up).
function Get-DotClassFromPixels {
    param([object[]] $Pixels)
    if (-not $Pixels -or $Pixels.Count -eq 0) { return $null }
    $mean = [math]::Round((($Pixels | Measure-Object -Property B -Average).Average), 4)
    $sat = @($Pixels | Where-Object { $_.S -gt 0.55 -and $_.B -gt 0.25 })
    $n = $sat.Count
    if ($n -lt 12) { return [pscustomobject]@{ Colour = 'grey'; Hue = $null; Brightness = $mean; Pixels = $n } }
    $hues = @($sat | ForEach-Object { [double]$_.H } | Sort-Object)
    $hue = $hues[[int][math]::Floor($hues.Count / 2)]
    $name = if ($hue -lt 20 -or $hue -gt 340) { 'red' } elseif ($hue -lt 65) { 'amber' } elseif ($hue -lt 170) { 'green' } elseif ($hue -lt 260) { 'blue' } else { 'other' }
    [pscustomobject]@{ Colour = $name; Hue = [math]::Round($hue, 1); Brightness = $mean; Pixels = $n }
}

# One reading of an app's tray icon: its hover text and the colour of its dot. Icon is the app's name ("Deluno" or "Weir").
# Windows keeps the hidden-icons choice per app under NotifyIconSettings. Promoting Deluno's and Weir's entries shows their icons on
# the taskbar itself, so they can be read with no pop-up and no key: opening and closing the pop-up during the install sent an
# Escape that cancelled the component picker (rc.30.21 to rc.30.24).
function Set-TrayIconsPromoted {
    Get-ChildItem -Path 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue | ForEach-Object {
        $path = (Get-ItemProperty -Path $_.PSPath -Name ExecutablePath -ErrorAction SilentlyContinue).ExecutablePath
        if ($path -match '\\(Deluno|Weir)\.exe$') {
            $promoted = (Get-ItemProperty -Path $_.PSPath -Name IsPromoted -ErrorAction SilentlyContinue).IsPromoted
            if ($promoted -ne 1) { Set-ItemProperty -Path $_.PSPath -Name IsPromoted -Value 1 -Type DWord -ErrorAction SilentlyContinue }
        }
    }
}

function Get-TrayDot {
    param([Parameter(Mandatory)] [string] $Icon, [switch] $NoOverflow)
    $btn = Find-TrayIcon -Icon $Icon
    $opened = $false
    if (-not $NoOverflow -and (-not $btn -or $btn.Element.Current.IsOffscreen)) {
        $opened = Open-TrayOverflow
        $btn = Find-TrayIcon -Icon $Icon
    }
    if (-not $btn) { if ($opened) { Close-TrayOverflow }; return [pscustomobject]@{ Found = $false; Hover = $null; Colour = $null; Brightness = $null } }
    $dot = $null
    try { $dot = Get-DotColour -Rect $btn.Element.Current.BoundingRectangle } catch { }
    $hover = $btn.Name
    if ($opened) { Close-TrayOverflow }
    [pscustomobject]@{
        Found = $true; Hover = $hover
        Colour = $(if ($dot) { $dot.Colour } else { $null }); Brightness = $(if ($dot) { $dot.Brightness } else { $null }); Pixels = $(if ($dot) { $dot.Pixels } else { 0 })
    }
}

function Send-Click {
    param([int] $X, [int] $Y, [switch] $Right)
    [Golden.Mouse]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 120
    $down, $up = if ($Right) { 0x0008, 0x0010 } else { 0x0002, 0x0004 }
    [Golden.Mouse]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
    [Golden.Mouse]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
}

# Waits until a UI Automation element can be clicked (enabled and on screen) and throws, saying why, when it cannot. A click on a
# disabled control does nothing and says nothing, so the suite never sends one.
function Wait-UiaClickable {
    param([Parameter(Mandatory)] $Element, [string] $What = 'the control', [int] $TimeoutSec = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        $enabled = $Element.Current.IsEnabled
        $offscreen = $Element.Current.IsOffscreen
        if ($enabled -and -not $offscreen) { return }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Could not click $What within $TimeoutSec s: it is $(if (-not $enabled) { 'disabled' } else { 'not visible' })."
        }
        Start-Sleep -Milliseconds 250
    }
}

# Opens an app's tray menu with a right click and returns the visible menu items' names (empty when it could not be opened).
# The menu is left open; the caller picks an item with Invoke-TrayMenuItem or closes it with Close-TrayMenu.
function Open-TrayMenu {
    param([Parameter(Mandatory)] [string] $Icon)
    $btn = Find-TrayIcon -Icon $Icon
    $opened = $false
    if (-not $btn -or $btn.Element.Current.IsOffscreen) { $opened = Open-TrayOverflow; $btn = Find-TrayIcon -Icon $Icon }
    if (-not $btn) { return @() }
    Wait-UiaClickable -Element $btn.Element -What "the $Icon tray icon"
    # Through the icon's own automation element: focus it and press the menu key. A click at a screen position can land on the
    # taskbar (it once opened Server Manager's jump list over the component picker), so none is ever sent for the icon.
    $btn.Element.SetFocus()
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    Start-Sleep -Milliseconds 700
    Get-OpenMenuItems
}

function Get-OpenMenuItems {
    $uia = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $items = $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    @($items | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name } | ForEach-Object { [string]$_.Current.Name })
}

function Close-TrayMenu { Send-EscapeIfSafe menu; Start-Sleep -Milliseconds 300 }

# Opens the app's tray menu and clicks the item whose name matches the pattern. Returns $true when it was clicked.
# Why the last Invoke-TrayMenuItem returned false, so a caller can say it (rc.30.28: "via its tray menu: False" twice with no
# reason, while the same Weir build quit from its menu in the run before).
$script:TrayMenuMiss = $null

function Invoke-TrayMenuItem {
    param([Parameter(Mandatory)] [string] $Icon, [Parameter(Mandatory)] [string] $Item)
    $script:TrayMenuMiss = $null
    $names = @(Open-TrayMenu -Icon $Icon)
    if ($names.Count -eq 0) {
        $dot = try { Get-TrayDot -Icon $Icon } catch { $null }
        $script:TrayMenuMiss = "the $Icon icon's menu did not open (icon found: $(if ($dot) { $dot.Found } else { 'unknown' }); hover '$(if ($dot) { $dot.Hover })')"
        return $false
    }
    $uia = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($el in $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if (-not $el.Current.IsOffscreen -and $el.Current.Name -match $Item) {
            Wait-UiaClickable -Element $el -What "the menu item '$($el.Current.Name)'"
            try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
            catch { $r = $el.Current.BoundingRectangle; Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)); return $true }
        }
    }
    Close-TrayMenu
    $script:TrayMenuMiss = "the $Icon menu opened but no visible item matched '$Item'; items: $($names -join ' | ')"
    $false
}

# Classifies a series of Get-TrayDot readings: does the dot blink (its brightness alternates between two or more values
# while its colour never changes), and what hover texts did it show.
function Get-BlinkSummary {
    param([object[]] $Readings)
    $found = @($Readings | Where-Object { $_.Found -and $null -ne $_.Brightness })
    $levels = @($found | ForEach-Object { [math]::Round([double]$_.Brightness, 2) } | Select-Object -Unique)
    # A colour read once in the whole run is an outlier sample, not the dot's colour.
    $colours = @($found | ForEach-Object { $_.Colour } | Where-Object { $_ -and $_ -ne 'none' } | Group-Object | Where-Object { $_.Count -ge 2 -or $found.Count -lt 3 } | ForEach-Object { $_.Name })
    $flips = 0
    for ($i = 1; $i -lt $found.Count; $i++) { if ([math]::Abs([double]$found[$i].Brightness - [double]$found[$i - 1].Brightness) -gt 0.01) { $flips++ } }
    [pscustomobject]@{ Samples = $found.Count; BrightnessLevels = $levels.Count; Flips = $flips; Colours = $colours; Blinks = ($flips -ge 4 -and $levels.Count -ge 2) }
}

# The first-run dot blinks by drawing the dot and then not drawing it. A reading takes the whole icon, so while the dot is drawn it
# reads grey (the dot covers the icon's colour) and while it is not drawn it reads the brand logo's own colour (amber). rc.30.22 read
# "colours [amber,grey]" and called the dot amber. The dot's colour is the colour of the readings where it is drawn; the others are
# the logo showing. This returns both, and says whether any colour appeared that a grey dot over an amber logo cannot make (red,
# green, blue: a real dot of that colour).
function Get-FirstRunDotSummary {
    param([object[]] $Readings)
    $blink = Get-BlinkSummary -Readings $Readings
    $other = @($blink.Colours | Where-Object { $_ -ne 'grey' })
    [pscustomobject]@{
        Blink = $blink
        GreyShown = (@($blink.Colours) -contains 'grey')
        LogoColours = $other
        OnlyGreyAndLogo = (@($other | Where-Object { $_ -ne 'amber' }).Count -eq 0)
    }
}
