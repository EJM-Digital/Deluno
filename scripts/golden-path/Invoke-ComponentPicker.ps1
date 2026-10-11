<#
.SYNOPSIS
  Ticks Deluno's first-run component picker inside the golden-path VM, the way a person would (#1158).

.DESCRIPTION
  Runs in the VM's signed-in desktop session (start it with a one-shot Interactive scheduled task). It waits for the
  "Set up download clients and Weir" window, ticks every component, fills the Usenet logins with the values given,
  presses "Install selected", follows the progress list until the window says "Finished.", records what each row said,
  then presses "Open Deluno" and "Close". Everything it saw goes to -LogPath, one line per step.

  The logins are test values for this throwaway VM only. They are written nowhere but the clients' own config.
#>
param(
    [Parameter(Mandatory)] [string] $UsenetUser,
    [Parameter(Mandatory)] [string] $UsenetPassword,
    [string] $LogPath = 'C:\golden\picker.log',
    [int] $WaitForWindowSeconds = 600,
    [int] $InstallMinutes = 40
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Log([string] $line) { "{0:o} {1}" -f (Get-Date).ToUniversalTime(), $line | Out-File -FilePath $LogPath -Append -Encoding utf8 }
# Any error stops the run at once and says which step it was, instead of a log that just ends.
trap { Log "FAILED at step '$script:Step': $($_.Exception.Message)"; exit 1 }
$script:Step = 'start'
function Step([string] $name) { $script:Step = $name; Log "step: $name" }
# Windows Server opens Server Manager at sign-in and it comes to the front over the picker. Close it whenever it shows.
function Clear-ServerManager { $p = @(Get-Process ServerManager -ErrorAction SilentlyContinue); if ($p.Count -gt 0) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; Log "closed Server Manager (it had opened over the picker)" } }
# A control the next step needs: found, or the run fails now saying which one was missing.
function Need($root, [string] $name) { $e = ByName $root $name; if (-not $e) { throw "the control '$name' is not in the window" }; $e }
function ByName($root, [string] $name) { $root.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function AllOfType($root, $type) { $root.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))) }
function Press($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText($element, [string] $value) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Texts($root) { @(AllOfType $root $CT::Text | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null
Log "waiting for the picker window (first run asks 'Start Deluno when you sign in to Windows?' before it)"
Step 'wait for the picker window'
Clear-ServerManager
$window = $null
$asked = $false
for ($i = 0; $i -lt $WaitForWindowSeconds -and -not $window; $i++) {
    $window = ByName $AE::RootElement 'Set up download clients and Weir'
    if (-not $window -and -not $asked) {
        # First run asks "Start Deluno when you sign in to Windows?" (Yes / No) before the picker. A person answering
        # a fresh install says No here, so do the same, and write down what the box said.
        # A plain Windows message box shows its text and its buttons to UI Automation as Panes (class Static / Button),
        # with no Invoke pattern, so find them by name and press the button through its window handle.
        $box = $null
        foreach ($candidate in $AE::RootElement.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Deluno')))) {
            if (ByName $candidate 'Start Deluno when you sign in to Windows?') { $box = $candidate; break }
        }
        if ($box) {
            $parts = @($box.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } |
                ForEach-Object { "'$($_.Current.Name)' ($($_.Current.ClassName), enabled=$($_.Current.IsEnabled))" })
            Log "start-with-windows question shown: window title 'Deluno'; controls: $($parts -join ', ')"
            $no = ByName $box 'No'
            # BM_CLICK straight to the button's window: a mouse click needs the box in front, and Server Manager's own pop-up can cover it.
            Add-Type -Namespace Gp -Name Win -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
            [void][Gp.Win]::SendMessage([System.IntPtr]$no.Current.NativeWindowHandle, 0x00F5, [System.IntPtr]::Zero, [System.IntPtr]::Zero)
            Start-Sleep -Seconds 1
            Log "start-with-windows question answered: No (pressed the No button); box still showing: $([bool](ByName $AE::RootElement 'Start Deluno when you sign in to Windows?'))"
            $asked = $true
        }
    }
    if (-not $window) { if ($i % 5 -eq 0) { Clear-ServerManager }; Start-Sleep -Seconds 1 }
}
if (-not $asked) { Log "start-with-windows question was NOT shown before the picker" }
if (-not $window) { Log "FAILED: the picker never appeared"; exit 1 }
Clear-ServerManager
Step 'focus the picker'
try { $window.SetFocus(); Log "picker focused" } catch { Log "could not focus the picker ($($_.Exception.Message)); going on, the controls are pressed through UI Automation" }
Log "picker open; texts: $((Texts $window) -join ' | ')"

# Tick every row. WinForms' CheckedListBox shows each row to UI Automation as a CheckBox with a Toggle pattern
# (not as a ListItem), so look for those; fall back to list items (select, then press Space).
function Rows($root) {
    $boxes = @(AllOfType $root $CT::CheckBox | Where-Object { $_.Current.Name -notmatch "Use the same login" })
    if ($boxes.Count -gt 0) { return $boxes }
    return @(AllOfType $root $CT::ListItem)
}
$rows = Rows $window
Log "rows: $($rows.Count): $((@($rows) | ForEach-Object { $_.Current.Name }) -join ' | ')"
if ($rows.Count -eq 0) { Log "FAILED: the picker lists no component rows to tick"; exit 1 }
Step 'tick every row'
# Rows are found by name every time, of any control type: ticking a row lays the window out again and adds controls
# of its own (login boxes, a checkbox), so an element or a type read before can go stale or stop matching.
$rowNames = @($rows | ForEach-Object { $_.Current.Name })
function FindRow([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    return $window.FindFirst($Scope::Descendants, $cond)
}
function RowState($row) { try { "$($row.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)" } catch { 'unknown' } }
function TickRow([string]$name) {
    $row = FindRow $name
    if (-not $row) { Log "row '$name' not found this time"; return }
    if ((RowState $row) -eq 'On') { return }
    try {
        $row.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Log "ticked '$name'"
    } catch {
        # As a person would: focus the row and press Space, which ticks a checked list row.
        try { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }
        try { $row.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait(' '); Log "ticked '$name' with Space" } catch { Log "could not tick '$name': $($_.Exception.Message)" }
    }
    Start-Sleep -Milliseconds 400
}
foreach ($pass in 1, 2) {
    foreach ($name in $rowNames) { TickRow $name }
    Start-Sleep -Seconds 1
}
Step 'check the ticks'
$unticked = 0
foreach ($name in $rowNames) {
    $row = FindRow $name
    $state = if ($row) { RowState $row } else { 'not found' }
    Log "row '$name': $state"
    if ($state -ne 'On' -and $name -notmatch 'already installed') { $unticked++ }
}
if ($unticked -gt 0) { Log "FAILED: $unticked row(s) are not ticked"; exit 1 }

# The Usenet logins appear once SABnzbd or NZBGet is ticked.
foreach ($field in 'SABnzbd username', 'NZBGet username') { $box = ByName $window $field; if ($box) { SetText $box $UsenetUser } }
foreach ($field in 'SABnzbd password', 'NZBGet password') { $box = ByName $window $field; if ($box) { SetText $box $UsenetPassword } }
Log "folders: $((AllOfType $window $CT::Edit | ForEach-Object { $_.Current.Name + '=' + $(try { $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' }) } | Where-Object { $_ -notmatch 'password' }) -join ' | ')"

Step 'press Install selected'
Press (Need $window 'Install selected')
Log "pressed Install selected"
Step 'follow the progress'

$deadline = (Get-Date).AddMinutes($InstallMinutes)
$last = ''
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    Clear-ServerManager
    $now = ByName $AE::RootElement 'Set up download clients and Weir'
    if (-not $now) { Log "the picker window closed by itself"; break }
    $texts = (Texts $now) -join ' | '
    $rowsNow = (@(AllOfType $now $CT::ListItem) | ForEach-Object { $_.Current.Name }) -join ' | '
    $snapshot = "$texts || $rowsNow"
    if ($snapshot -ne $last) { Log "progress: $snapshot"; $last = $snapshot }
    if ($texts -match 'Finished\.') { break }
}

$window = ByName $AE::RootElement 'Set up download clients and Weir'
if ($window) {
    Log "final: $((Texts $window) -join ' | ')"
    $open = ByName $window 'Open Deluno'
    if ($open -and $open.Current.IsEnabled) { Press $open; Log "pressed Open Deluno"; Start-Sleep -Seconds 8 }
    $browsers = @(Get-Process msedge, chrome, firefox -ErrorAction SilentlyContinue)
    Log "browser processes after Open Deluno: $($browsers.Count)"
    $close = ByName $window 'Close'
    if ($close -and $close.Current.IsEnabled) { Press $close; Log "pressed Close" }
}
Log "done"
