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
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Log([string] $line) { "{0:o} {1}" -f (Get-Date).ToUniversalTime(), $line | Out-File -FilePath $LogPath -Append -Encoding utf8 }
function ByName($root, [string] $name) { $root.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function AllOfType($root, $type) { $root.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))) }
function Press($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText($element, [string] $value) { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Texts($root) { @(AllOfType $root $CT::Text | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null
Log "waiting for the picker window"
$window = $null
for ($i = 0; $i -lt $WaitForWindowSeconds -and -not $window; $i++) {
    $window = ByName $AE::RootElement 'Set up download clients and Weir'
    if (-not $window) { Start-Sleep -Seconds 1 }
}
if (-not $window) { Log "FAILED: the picker never appeared"; exit 1 }
Log "picker open; texts: $((Texts $window) -join ' | ')"

# Tick every row. A CheckedListBox row toggles; fall back to selecting it and pressing Space.
$rows = AllOfType $window $CT::ListItem
Log "rows: $($rows.Count): $((@($rows) | ForEach-Object { $_.Current.Name }) -join ' | ')"
foreach ($row in $rows) {
    $done = $false
    try {
        $toggle = $row.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
        $done = $true
    } catch { }
    if (-not $done) {
        $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $row.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait(' ')
    }
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Seconds 1
foreach ($row in (AllOfType $window $CT::ListItem)) {
    $state = try { $row.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { 'unknown' }
    Log "row '$($row.Current.Name)': $state"
}

# The Usenet logins appear once SABnzbd or NZBGet is ticked.
foreach ($field in 'SABnzbd username', 'NZBGet username') { $box = ByName $window $field; if ($box) { SetText $box $UsenetUser } }
foreach ($field in 'SABnzbd password', 'NZBGet password') { $box = ByName $window $field; if ($box) { SetText $box $UsenetPassword } }
Log "folders: $((AllOfType $window $CT::Edit | ForEach-Object { $_.Current.Name + '=' + $(try { $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '' }) } | Where-Object { $_ -notmatch 'password' }) -join ' | ')"

Press (ByName $window 'Install selected')
Log "pressed Install selected"

$deadline = (Get-Date).AddMinutes($InstallMinutes)
$last = ''
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
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
