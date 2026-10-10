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
    try { $chevron.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 600; return $true } catch { return $false }
}

function Close-TrayOverflow { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 200 }

function Find-TrayIcon {
    param([Parameter(Mandatory)] [string] $Icon)
    @(Get-TrayButtons | Where-Object { $_.Name -match "^$Icon\b" }) | Select-Object -First 1
}

# Classifies the dot in a tray icon's pixels: the most saturated cluster in the button's rectangle.
function Get-DotColour {
    param([System.Windows.Rect] $Rect)
    $w = [int]$Rect.Width; $h = [int]$Rect.Height
    if ($w -lt 4 -or $h -lt 4 -or [double]::IsInfinity($Rect.X)) { return $null }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen([int]$Rect.X, [int]$Rect.Y, 0, 0, (New-Object System.Drawing.Size($w, $h))) } finally { $g.Dispose() }
    $sumH = 0.0; $n = 0; $bright = 0.0
    for ($x = 0; $x -lt $w; $x++) {
        for ($y = 0; $y -lt $h; $y++) {
            $c = $bmp.GetPixel($x, $y)
            $bright += $c.GetBrightness()
            if ($c.GetSaturation() -gt 0.55 -and $c.GetBrightness() -gt 0.25) { $sumH += $c.GetHue(); $n++ }
        }
    }
    $bmp.Dispose()
    $mean = [math]::Round($bright / ($w * $h), 4)
    if ($n -lt 3) { return [pscustomobject]@{ Colour = 'none'; Hue = $null; Brightness = $mean; Pixels = $n } }
    $hue = $sumH / $n
    $name = if ($hue -lt 20 -or $hue -gt 340) { 'red' } elseif ($hue -lt 65) { 'amber' } elseif ($hue -lt 170) { 'green' } elseif ($hue -lt 260) { 'blue' } else { 'other' }
    [pscustomobject]@{ Colour = $name; Hue = [math]::Round($hue, 1); Brightness = $mean; Pixels = $n }
}

# One reading of an app's tray icon: its hover text and the colour of its dot. Icon is the app's name ("Deluno" or "Weir").
function Get-TrayDot {
    param([Parameter(Mandatory)] [string] $Icon)
    $btn = Find-TrayIcon -Icon $Icon
    $opened = $false
    if (-not $btn -or $btn.Element.Current.IsOffscreen) {
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

# Opens an app's tray menu with a right click and returns the visible menu items' names (empty when it could not be opened).
# The menu is left open; the caller picks an item with Invoke-TrayMenuItem or closes it with Close-TrayMenu.
function Open-TrayMenu {
    param([Parameter(Mandatory)] [string] $Icon)
    $btn = Find-TrayIcon -Icon $Icon
    $opened = $false
    if (-not $btn -or $btn.Element.Current.IsOffscreen) { $opened = Open-TrayOverflow; $btn = Find-TrayIcon -Icon $Icon }
    if (-not $btn) { return @() }
    $r = $btn.Element.Current.BoundingRectangle
    Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)) -Right
    Start-Sleep -Milliseconds 700
    Get-OpenMenuItems
}

function Get-OpenMenuItems {
    $uia = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $items = $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    @($items | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name } | ForEach-Object { [string]$_.Current.Name })
}

function Close-TrayMenu { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 300 }

# Opens the app's tray menu and clicks the item whose name matches the pattern. Returns $true when it was clicked.
function Invoke-TrayMenuItem {
    param([Parameter(Mandatory)] [string] $Icon, [Parameter(Mandatory)] [string] $Item)
    $names = Open-TrayMenu -Icon $Icon
    if ($names.Count -eq 0) { return $false }
    $uia = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($el in $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if (-not $el.Current.IsOffscreen -and $el.Current.Name -match $Item) {
            try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
            catch { $r = $el.Current.BoundingRectangle; Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)); return $true }
        }
    }
    Close-TrayMenu
    $false
}

# Classifies a series of Get-TrayDot readings: does the dot blink (its brightness alternates between two or more values
# while its colour never changes), and what hover texts did it show.
function Get-BlinkSummary {
    param([object[]] $Readings)
    $found = @($Readings | Where-Object { $_.Found -and $null -ne $_.Brightness })
    $levels = @($found | ForEach-Object { [math]::Round([double]$_.Brightness, 2) } | Select-Object -Unique)
    $colours = @($found | ForEach-Object { $_.Colour } | Where-Object { $_ -and $_ -ne 'none' } | Select-Object -Unique)
    $flips = 0
    for ($i = 1; $i -lt $found.Count; $i++) { if ([math]::Abs([double]$found[$i].Brightness - [double]$found[$i - 1].Brightness) -gt 0.01) { $flips++ } }
    [pscustomobject]@{ Samples = $found.Count; BrightnessLevels = $levels.Count; Flips = $flips; Colours = $colours; Blinks = ($flips -ge 4 -and $levels.Count -ge 2) }
}
