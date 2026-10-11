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
    [int] $InstallMinutes = 40,
    # Leave the browser alone at the end (for a run on a workstation, where Open Deluno would open a page).
    [switch] $SkipOpenDeluno,
    # Tick with the keyboard only (list focused, Home, Down, Space), to prove that route where the Toggle pattern would have worked.
    [switch] $ForceKeys
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

# Tick every row. The six rows are the children of the picker's one list, a WinForms CheckedListBox. Depending on the Windows build and on
# what has been ticked, UI Automation shows a row as a ListItem or as a CheckBox, and lookups by control type or by name come and go as
# the window lays itself out. So nothing is held between steps and nothing is looked up by type or name: every step asks the LIST for
# its children again and takes row N by position. A row is ticked through its Toggle pattern; if that is refused, the keyboard does it
# as a person would (list focused, Home, Down to the row, Space) -- but only once the list is verified to hold the keyboard focus in
# the picker's own window, so a stray key can never reach Skip (which closes the picker).
Add-Type -Namespace Gp -Name Fg -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow(); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'
function Get-PickerList {
    $w = ByName $AE::RootElement 'Set up download clients and Weir'
    if (-not $w) { throw 'the picker window is gone' }
    $lists = @(AllOfType $w $CT::List | Where-Object { $_.Current.ClassName -match 'ListBox' })
    if ($lists.Count -ne 1) { throw "expected one component list in the picker, found $($lists.Count)" }
    $lists[0]
}
function Get-PickerRows { @((Get-PickerList).FindAll($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition)) }
function RowState($row) { try { "$($row.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)" } catch { 'unknown' } }
function Assert-ListHasKeyboard {
    $w = ByName $AE::RootElement 'Set up download clients and Weir'
    if (-not $w) { throw 'the picker window is gone, so no key was sent' }
    $hwnd = [System.IntPtr]$w.Current.NativeWindowHandle
    if ([Gp.Fg]::GetForegroundWindow() -ne $hwnd) { [void][Gp.Fg]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 300 }
    if ([Gp.Fg]::GetForegroundWindow() -ne $hwnd) { throw 'the picker is not the foreground window, so no key was sent' }
    $list = Get-PickerList
    $list.SetFocus(); Start-Sleep -Milliseconds 200
    $has = $list.Current.HasKeyboardFocus -or (@(Get-PickerRows | Where-Object { $_.Current.HasKeyboardFocus }).Count -gt 0)
    if (-not $has) { throw 'the component list does not hold the keyboard focus, so no key was sent (a stray key could hit Skip)' }
}
function Get-RowStates { (@(Get-PickerRows) | ForEach-Object { $(if ((RowState $_) -eq 'On') { '1' } else { '0' }) }) -join '' }
function Tick-RowByKeys([int] $index) {
    # The focus a row reports to UI Automation is not reliable, so the proof of a key is the row's own state, read back by the caller.
    # What makes a key safe is that the list holds the keyboard focus in the picker's own window: Home and Down cannot leave the
    # list, and Space only ticks, so Skip (which closes the picker) is never pressed.
    Assert-ListHasKeyboard
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    Start-Sleep -Milliseconds 300
    for ($d = 0; $d -lt $index; $d++) { [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 300
    Assert-ListHasKeyboard
    Log "keys: states before Space (1 = on, 0 = off): $(Get-RowStates)"
    [System.Windows.Forms.SendKeys]::SendWait(' ')
    Start-Sleep -Milliseconds 400
    Log "keys: states after Space: $(Get-RowStates)"
}
Step 'read the rows'
$names = @(Get-PickerRows | ForEach-Object { $_.Current.Name })
Log "rows: $($names.Count): $($names -join ' | ')"
if ($names.Count -eq 0) { throw 'the picker lists no component rows to tick' }
Step 'tick every row'
for ($i = 0; $i -lt $names.Count; $i++) {
    $name = $names[$i]
    if ($name -match 'already installed') { Log "row $i '$name': left alone (already installed)"; continue }
    $ok = $false
    for ($attempt = 1; $attempt -le 3 -and -not $ok; $attempt++) {
        $row = @(Get-PickerRows)[$i]
        if (-not $row) { throw "row $i '$name' is not in the list any more" }
        $state = RowState $row
        if ($state -eq 'On') { $ok = $true; break }
        $how = 'Toggle'
        try { if ($ForceKeys) { throw 'keys only' }; $row.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
        catch { $how = 'keys'; Tick-RowByKeys $i }
        Start-Sleep -Milliseconds 400
        $after = RowState (@(Get-PickerRows)[$i])
        Log "row $i '$name': $how, was $state, now $after (attempt $attempt)"
        if ($after -eq 'On') { $ok = $true } elseif ($after -eq 'unknown' -and $how -eq 'keys') { $ok = $true }
    }
    if (-not $ok) { throw "row $i '$name' would not tick after 3 tries" }
}
Start-Sleep -Seconds 1
Step 'check the ticks'
$unticked = 0
$i = 0
foreach ($row in @(Get-PickerRows)) {
    $state = RowState $row
    Log "row '$($row.Current.Name)': $state"
    if ($state -ne 'On' -and $state -ne 'unknown' -and $row.Current.Name -notmatch 'already installed') { $unticked++ }
    $i++
}
if ($i -ne $names.Count) { throw "the list has $i rows now, it had $($names.Count)" }
if ($unticked -gt 0) { throw "$unticked row(s) are not ticked" }
$window = ByName $AE::RootElement 'Set up download clients and Weir'

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
    if ($open -and $open.Current.IsEnabled -and -not $SkipOpenDeluno) { Press $open; Log "pressed Open Deluno"; Start-Sleep -Seconds 8 }
    $browsers = @(Get-Process msedge, chrome, firefox -ErrorAction SilentlyContinue)
    Log "browser processes after Open Deluno: $($browsers.Count)"
    $close = ByName $window 'Close'
    if ($close -and $close.Current.IsEnabled) { Press $close; Log "pressed Close" }
}
Log "done"
