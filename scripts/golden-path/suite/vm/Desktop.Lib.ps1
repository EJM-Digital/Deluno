<#
.SYNOPSIS
    Reads and presses the VM desktop for the scenarios that need more than the tray icon: Windows' own permission prompt (UAC),
    a message box or dialog a tray item opens, the pop-up a tray balloon becomes, and whether a process runs with administrator
    rights (golden path scenario suite, coverage issues 1283 and 1278).

.DESCRIPTION
    Dot-source after Tray.Lib.ps1 (it uses Wait-UiaClickable and Send-Click). Windows PowerShell 5.1, in the signed-in desktop
    session, where the suite runs as an administrator.

    UAC: the prompt normally appears on the secure desktop, which no program of the signed-in session can read or press. The scenarios
    that need a real answer switch the prompt to appear on the ordinary desktop for their duration (Use-UacOnUserDesktop, the only
    thing changed is "PromptOnSecureDesktop"; the consent prompt itself, "ask for consent for non-Windows binaries", is left as it is)
    and put it back. The administrator suite process can then read and press "Yes" or "No" on it like any window. Nothing is
    answered on behalf of a program that did not ask.
#>

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ('GpTok.Elev' -as [type])) {
    Add-Type -Namespace GpTok -Name Elev -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(System.IntPtr process, uint access, out System.IntPtr token);
[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(System.IntPtr token, int infoClass, out int info, int length, out int returned);
[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(System.IntPtr token, int infoClass, System.IntPtr buffer, int length, out int returned);
[System.Runtime.InteropServices.DllImport("advapi32.dll")] public static extern System.IntPtr GetSidSubAuthority(System.IntPtr sid, int index);
[System.Runtime.InteropServices.DllImport("advapi32.dll")] public static extern System.IntPtr GetSidSubAuthorityCount(System.IntPtr sid);
[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)] public static extern bool ConvertStringSidToSid(string s, out System.IntPtr sid);
[System.Runtime.InteropServices.DllImport("advapi32.dll")] public static extern bool EqualSid(System.IntPtr a, System.IntPtr b);
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool CloseHandle(System.IntPtr h);
'@
}

# $true when the process runs with an elevated (administrator) token, $false when it runs filtered, $null when it could not be read.
function Test-ProcessElevated {
    param([Parameter(Mandatory)] [int] $ProcessId)
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $token = [IntPtr]::Zero
    try {
        if (-not [GpTok.Elev]::OpenProcessToken($p.Handle, 0x0008, [ref]$token)) { return $null } # TOKEN_QUERY
        $elevated = 0; $returned = 0
        # TokenElevation = 20
        if (-not [GpTok.Elev]::GetTokenInformation($token, 20, [ref]$elevated, 4, [ref]$returned)) { return $null }
        if ($elevated -eq 0) { return $false }
        # A token made by "run at basic user level" keeps the administrator's elevation flag and high integrity, but the Administrators
        # group in it is "for deny only": the process cannot administer, so a program checking the role (Deluno's firewall step) asks for
        # elevation. That group's attribute is what is read.
        $adminSid = [IntPtr]::Zero
        [void][GpTok.Elev]::ConvertStringSidToSid('S-1-5-32-544', [ref]$adminSid)
        $size = 8192
        $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
        try {
            if ([GpTok.Elev]::GetTokenInformation($token, 2, $buf, $size, [ref]$returned)) {
                $count = [Runtime.InteropServices.Marshal]::ReadInt32($buf)
                for ($i = 0; $i -lt $count; $i++) {
                    $entry = [IntPtr]::Add($buf, 8 + 16 * $i)
                    $sid = [Runtime.InteropServices.Marshal]::ReadIntPtr($entry)
                    $attrs = [Runtime.InteropServices.Marshal]::ReadInt32($entry, 8)
                    if ([GpTok.Elev]::EqualSid($sid, $adminSid)) { return (($attrs -band 0x10) -eq 0) }
                }
            }
        }
        finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buf) }
        return $true
    }
    catch { return $null }
    finally { if ($token -ne [IntPtr]::Zero) { [void][GpTok.Elev]::CloseHandle($token) } }
}

# ---------------------------------------------------------------- windows

# Every top-level window UI Automation can see: name, class and owning process. Used to find a dialog and to say what was on the
# desktop when one never came.
function Get-DesktopWindows {
    Invoke-UiaRetry -Block { Get-DesktopWindowsOnce }
}

function Get-DesktopWindowsOnce {
    $uia = [System.Windows.Automation.AutomationElement]
    $all = $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $found = New-Object System.Collections.ArrayList
    foreach ($w in $all) {
        $name = ''; $class = ''; $procId = 0; $procName = ''
        try { $name = [string]$w.Current.Name; $class = [string]$w.Current.ClassName; $procId = [int]$w.Current.ProcessId } catch { continue }
        $procName = try { (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { '' }
        [void]$found.Add([pscustomobject]@{ Name = $name; Class = $class; ProcessId = $procId; Process = $procName; Element = $w })
    }
    @($found)
}

function Format-DesktopWindows {
    param([object[]] $Windows = @(Get-DesktopWindows))
    (@($Windows | ForEach-Object { "[{0}|{1}|{2}] {3}" -f $_.Process, $_.Class, $_.ProcessId, $_.Name }) -join '; ')
}

# Waits for a top-level window whose name matches (and, when given, whose process is the one named). Returns the window or $null.
function Wait-DesktopWindow {
    param([Parameter(Mandatory)] [string] $NamePattern, [string] $Process = '', [int] $TimeoutSec = 20)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 0.5 -Condition {
        Get-DesktopWindows | Where-Object { $_.Name -match $NamePattern -and (-not $Process -or $_.Process -eq $Process) } | Select-Object -First 1
    }
    if ($w.Ok) { $w.Value } else { $null }
}

# Every named control under an element: type, name, enabled.
function Get-WindowControls {
    param([Parameter(Mandatory)] $Window)
    $uia = [System.Windows.Automation.AutomationElement]
    @($Window.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
        $n = ''; $t = ''; $en = $false; $off = $true
        try { $n = [string]$_.Current.Name; $t = [string]$_.Current.ControlType.ProgrammaticName; $en = $_.Current.IsEnabled; $off = $_.Current.IsOffscreen } catch { }
        [pscustomobject]@{ Type = ($t -replace '^ControlType\.', ''); Name = $n; Enabled = $en; Offscreen = $off; Element = $_ }
    })
}

# The text a window shows: its own name and every named, visible control in it.
function Get-WindowText {
    param([Parameter(Mandatory)] $Window)
    $names = @(Get-WindowControls -Window $Window | Where-Object { $_.Name -and -not $_.Offscreen } | ForEach-Object { $_.Name })
    (@($Window.Name) + $names) -join ' | '
}

# Presses the button of a window whose name matches. Returns $true when pressed.
function Click-WindowButton {
    param([Parameter(Mandatory)] $Window, [Parameter(Mandatory)] [string] $NamePattern, [int] $TimeoutSec = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    do {
        $b = Get-WindowControls -Window $Window | Where-Object { $_.Type -in 'Button', 'Pane' -and $_.Name -match $NamePattern -and -not $_.Offscreen } | Select-Object -First 1
        if ($b) {
            Wait-UiaClickable -Element $b.Element -What "the '$($b.Name)' button"
            try { $b.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
            catch { $r = $b.Element.Current.BoundingRectangle; Send-Click -X ([int]($r.X + $r.Width / 2)) -Y ([int]($r.Y + $r.Height / 2)); return $true }
        }
        Start-Sleep -Milliseconds 300 # reason: poll interval inside a loop bounded by its deadline
    } while ([DateTime]::UtcNow -lt $deadline)
    $false
}

# Closes a window the way a person closes it (its Window pattern), falling back to Alt+F4 only when that window is in front.
function Close-DesktopWindow {
    param([Parameter(Mandatory)] $Window)
    try { $Window.Element.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); return }
    catch { }
}

# ---------------------------------------------------------------- UAC

$script:UacPolicyPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'

# Runs a block with Windows' permission prompt on the ordinary desktop, and puts the setting back however the block ends.
function Use-UacOnUserDesktop {
    param([Parameter(Mandatory)] [scriptblock] $Block)
    $before = (Get-ItemProperty -Path $script:UacPolicyPath -Name PromptOnSecureDesktop -ErrorAction Stop).PromptOnSecureDesktop
    Set-ItemProperty -Path $script:UacPolicyPath -Name PromptOnSecureDesktop -Value 0 -Type DWord
    try { & $Block }
    finally { Set-ItemProperty -Path $script:UacPolicyPath -Name PromptOnSecureDesktop -Value $before -Type DWord }
}

# Waits for the permission prompt and answers it. Returns what the prompt said (its text), or $null when none appeared. The prompt
# is the "User Account Control" window of consent.exe; "Yes" allows and "No" declines.
function Answer-UacPrompt {
    param([bool] $Allow = $true, [int] $TimeoutSec = 30)
    $win = Wait-DesktopWindow -NamePattern 'User Account Control' -TimeoutSec $TimeoutSec
    if (-not $win) { return $null }
    $text = Get-WindowText -Window $win
    $pressed = Click-WindowButton -Window $win -NamePattern $(if ($Allow) { '^Yes$' } else { '^No$' }) -TimeoutSec 10
    [pscustomobject]@{ Text = $text; Pressed = $pressed; Allowed = $Allow }
}

# ---------------------------------------------------------------- starting Deluno the way a person without administrator rights does

function Get-DelunoExe {
    Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Deluno\current') -Filter 'Deluno.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
}

# Runs a program in this signed-in session without administrator rights (the suite itself runs elevated, and a program it starts
# would be elevated too, so the permission prompt would never appear).
function Start-ProgramUnelevated {
    param([Parameter(Mandatory)] [string] $Path, [string] $Arguments = '', [string] $WorkingDirectory = '', [switch] $Elevated)
    if (-not $Elevated) {
        # The built-in Administrator is never filtered on this VM (Admin Approval Mode is off for it), so a "limited" task still runs with the
        # full token. Run at basic user level instead: the process cannot administer, so Deluno asks Windows for elevation as a person's would.
        $inner = '"' + $Path + '"' + $(if ($Arguments) { ' ' + $Arguments } else { '' })
        Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\runas.exe') -ArgumentList '/trustlevel:0x20000', ('"' + ($inner -replace '"', '\"') + '"') -WorkingDirectory $(if ($WorkingDirectory) { $WorkingDirectory } else { $env:SystemRoot })
        return
    }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $name = 'GoldenSuiteUnelevated'
    $action = if ($Arguments) { New-ScheduledTaskAction -Execute $Path -Argument $Arguments -WorkingDirectory $WorkingDirectory } else { New-ScheduledTaskAction -Execute $Path -WorkingDirectory $WorkingDirectory }
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    Start-Sleep -Seconds 3 # reason: settle after starting a process; the task has no event to wait on
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}

# Quits Deluno from its tray menu and starts it again without administrator rights, as a person who installed it and signs in
# again would have it. Returns $true when Deluno answers again and its process is not elevated.
function Restart-DelunoAs {
    param([bool] $Elevated = $false)
    $quit = $false
    for ($attempt = 1; $attempt -le 3 -and -not $quit; $attempt++) {
        try { $quit = [bool](Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Quit Deluno') } catch { $quit = $false }
        if (-not $quit) { Start-Sleep -Seconds 2 } # reason: a retry pause before reading the tray menu again; no event says the menu is ready
    }
    $gone = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue) }
    if (-not $gone.Ok) { Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Stop-Process -Force }
    $exe = Get-DelunoExe
    if (-not $exe) { throw 'Deluno.exe is not installed.' }
    Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated:$Elevated
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
    if (-not $up.Ok) { throw 'Deluno did not answer within 4 minutes of being started again.' }
    Connect-Deluno
    $proc = Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{ ViaTray = $quit; Elevated = $(if ($proc) { Test-ProcessElevated -ProcessId $proc.Id } else { $null }); ProcessId = $(if ($proc) { $proc.Id } else { 0 }) }
}

function Restart-DelunoUnelevated { Restart-DelunoAs -Elevated $false }
function Restart-DelunoElevated { Restart-DelunoAs -Elevated $true }

# Makes sure Deluno runs without administrator rights (so Windows asks for permission when it changes the firewall), restarting it
# from its tray menu when it does not. Returns whether it is unelevated now.
function Initialize-DelunoUnelevated {
    $proc = Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc -and (Test-ProcessElevated -ProcessId $proc.Id) -eq $false) { return $true }
    $r = Restart-DelunoUnelevated
    $r.Elevated -eq $false
}

# ---------------------------------------------------------------- tray balloons
# A tray balloon becomes a Windows notification. No window of this server's desktop shows it, but Windows keeps every one in its
# notification database (wpndatabase.db and its write-ahead log) with the title and text in it. A balloon is told by the count of
# its text there going up.

function Get-ToastTextCount {
    param([Parameter(Mandatory)] [string] $Text)
    $dir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Notifications'
    $total = 0
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Force -File -Filter 'wpndatabase.db*' -ErrorAction SilentlyContinue)) {
        try {
            $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            try { $b = New-Object byte[] $fs.Length; [void]$fs.Read($b, 0, $b.Length) } finally { $fs.Close() }
            foreach ($enc in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
                $t = $enc.GetString($b); $i = 0
                while (($i = $t.IndexOf($Text, $i, [StringComparison]::Ordinal)) -ge 0) { $total++; $i += $Text.Length }
            }
        }
        catch { }
    }
    $total
}

# Waits until a balloon with this text has been shown since $Before (a Get-ToastTextCount taken before the click).
function Wait-ToastShown {
    param([Parameter(Mandatory)] [string] $Text, [Parameter(Mandatory)] [int] $Before, [int] $TimeoutSec = 20)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 1 -Condition { (Get-ToastTextCount -Text $Text) -gt $Before }
    $w.Ok
}

# ---------------------------------------------------------------- tray menu items with their state

# Opens Deluno's tray menu and returns each visible item with whether it is enabled and ticked. The menu is closed again.
function Get-TrayMenuStates {
    param([string] $Icon = 'Deluno')
    $names = @(Open-TrayMenu -Icon $Icon)
    $uia = [System.Windows.Automation.AutomationElement]
    $cond = New-Object System.Windows.Automation.PropertyCondition($uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $out = New-Object System.Collections.ArrayList
    foreach ($el in $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($el.Current.IsOffscreen -or -not $el.Current.Name) { continue }
        $checked = $false
        try { $checked = ($el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') } catch { }
        [void]$out.Add([pscustomobject]@{ Name = [string]$el.Current.Name; Enabled = [bool]$el.Current.IsEnabled; Checked = $checked })
    }
    Close-TrayMenu
    @($out)
}

function Get-TrayMenuItemState {
    param([Parameter(Mandatory)] [string] $Pattern)
    @(Get-TrayMenuStates | Where-Object { $_.Name -match $Pattern }) | Select-Object -First 1
}



# ---------------------------------------------------------------- Deluno as an ordinary administrator (a real permission prompt)
# The golden VM's built-in Administrator is never filtered (Admin Approval Mode is off for it), so nothing it runs is ever asked for
# permission, and a program run "without administrator rights" cannot get them back. A person's account is a second administrator:
# its programs run filtered, and Windows asks before one is elevated. So the permission scenarios run Deluno as such an account
# ("goldenadmin", a throwaway made here), from a copy of the install (a filtered token cannot read the first account's profile).

$script:SecondAdmin = @{ User = 'goldenadmin'; Folder = 'C:\golden\second\Deluno' }

function Get-SecondAdminCredential {
    $pw = (New-SuiteSecret 'goldenadmin-password').Substring(0, 13) # reason: net user asks a question for a password over 14 characters
    New-Object System.Management.Automation.PSCredential(".\$($script:SecondAdmin.User)", (ConvertTo-SecureString $pw -AsPlainText -Force))
}

function Quit-DelunoFromTray {
    $quit = $false
    for ($attempt = 1; $attempt -le 3 -and -not $quit; $attempt++) {
        try { $quit = [bool](Invoke-TrayMenuItem -Icon 'Deluno' -Item '^Quit Deluno') } catch { $quit = $false }
        if (-not $quit) { Start-Sleep -Seconds 2 } # reason: a retry pause before reading the tray menu again; no event says the menu is ready
    }
    $gone = Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { -not (Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue) }
    if (-not $gone.Ok) { Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Stop-Process -Force }
    $quit
}

# Quits the suite's Deluno and starts a copy of it as the second administrator. Returns the new process and whether it is elevated
# (it must not be). Its data folder is its own and empty, so it is given a first account, which Connect-Deluno then uses.
function Start-DelunoAsSecondAdmin {
    $user = $script:SecondAdmin.User
    $pw = (New-SuiteSecret 'goldenadmin-password').Substring(0, 13) # reason: net user asks a question for a password over 14 characters
    & cmd.exe /c "net user $user >nul 2>&1"
    if ($LASTEXITCODE -ne 0) {
        & net.exe user $user $pw /add | Out-Null
        & net.exe localgroup Administrators $user /add | Out-Null
    }
    $exe = Get-DelunoExe
    $dst = $script:SecondAdmin.Folder
    if (-not (Test-Path (Join-Path $dst 'Deluno.exe'))) {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        & robocopy.exe $exe.DirectoryName $dst /E /NFL /NDL /NJH /NJS /NP | Out-Null
    }
    Quit-DelunoFromTray | Out-Null
    Start-Process -FilePath (Join-Path $dst 'Deluno.exe') -WorkingDirectory $dst -Credential (Get-SecondAdminCredential) -LoadUserProfile | Out-Null
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
    if (-not $up.Ok) { throw 'Deluno did not answer when it was started as the second administrator.' }
    New-SuiteSecret 'goldenadmin-deluno-password' | Out-Null
    $status = Try-Deluno -Path '/api/auth/bootstrap-status'
    if ($status.Data.requiresSetup) {
        $r = Try-Deluno -Method POST -Path '/api/auth/bootstrap' -Body @{ username = 'golden2'; displayName = 'Golden second'; password = (Get-SuiteSecret 'goldenadmin-deluno-password') }
        if ($r.Status -ne 200) { throw (Hide-Secrets "Creating the second Deluno's first account answered $($r.Status). $($r.Raw)") }
    }
    Connect-Deluno -Username 'golden2' -Password (Get-SuiteSecret 'goldenadmin-deluno-password')
    # Windows files this copy's icon in the hidden ones; promote it onto the taskbar so it can be read without a pop-up.
    Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { Set-TrayIconsPromoted; Find-TrayIcon -Icon 'Deluno' } | Out-Null
    $proc = Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{ ProcessId = $proc.Id; Elevated = (Test-ProcessElevated -ProcessId $proc.Id) }
}

# Quits the second administrator's Deluno and starts the suite's own again, as the install left it, signed in with the suite's login.
function Stop-DelunoAsSecondAdmin {
    Quit-DelunoFromTray | Out-Null
    $exe = Get-DelunoExe
    Start-ProgramUnelevated -Path $exe.FullName -WorkingDirectory $exe.DirectoryName -Elevated
    $up = Wait-Until -TimeoutSec 240 -IntervalSec 2 -Condition { Test-DelunoAnswering }
    if ($up.Ok) { Connect-Deluno }
    $up.Ok
}

# ---------------------------------------------------------------- answering the permission prompt
# The prompt is a window of Windows' own consent program, which runs as SYSTEM: a program of the signed-in administrator (high
# integrity) can see that it is there but cannot read its buttons or press them (UI Automation does not cross that line). So a
# small helper is started as SYSTEM inside the signed-in session, as a person's own mouse would be: a scheduled task as SYSTEM
# runs the launcher below, which starts the helper in the console session; the helper waits for the prompt, writes down what it
# says, and presses Yes or No.

$script:UacHelperText = @'
param([string] $Answer = 'Yes', [string] $Out, [int] $TimeoutSec = 60)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$uia = [System.Windows.Automation.AutomationElement]
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
$result = [ordered]@{ found = $false; pressed = $false; text = ''; names = @() }
while ([DateTime]::UtcNow -lt $deadline) {
    $wins = $uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($uia::NameProperty, 'User Account Control')))
    foreach ($w in $wins) {
        $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $names = @($all | ForEach-Object { "$($_.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''):$($_.Current.Name)" })
        $btn = $all | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -eq $Answer } | Select-Object -First 1
        if ($btn) {
            $result.found = $true
            $result.names = $names
            $result.text = (@($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ')
            try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $result.pressed = $true } catch { $result.error = $_.Exception.Message }
            ($result | ConvertTo-Json -Compress -Depth 4) | Set-Content -Path $Out -Encoding utf8
            return
        }
    }
    Start-Sleep -Milliseconds 300 # reason: poll interval inside a loop bounded by its deadline
}
$result.names = @($uia::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
($result | ConvertTo-Json -Compress -Depth 4) | Set-Content -Path $Out -Encoding utf8
'@

$script:UacLauncherText = @'
$Command = (Get-Content -Raw 'C:\golden\suite\logs\uac-cmd.txt').Trim()
try {
Add-Type -TypeDefinition @"
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class GpSys {
  [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool DuplicateTokenEx(IntPtr tok, uint acc, IntPtr attr, int imp, int type, out IntPtr dup);
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool SetTokenInformation(IntPtr tok, int cls, ref uint info, int len);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct SI { public int cb; public string r; public string desktop; public string title; public int x,y,xs,ys,xc,yc,fill,flags; public short show,cbr; public IntPtr r2,i,o,e; }
  [StructLayout(LayoutKind.Sequential)] public struct PI { public IntPtr hp,ht; public int pid,tid; }
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool CreateProcessAsUser(IntPtr tok, string app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint fl, IntPtr env, string dir, ref SI si, out PI pi);
  public static int Run(string cmd) {
    uint sid = WTSGetActiveConsoleSessionId();
    IntPtr h = IntPtr.Zero;
    foreach (var p in Process.GetProcessesByName("winlogon")) { if ((uint)p.SessionId == sid) { h = p.Handle; break; } }
    IntPtr tok; if (!OpenProcessToken(h, 0x0F, out tok)) return -1;
    IntPtr dup; if (!DuplicateTokenEx(tok, 0x02000000, IntPtr.Zero, 2, 1, out dup)) return -2;
    if (!SetTokenInformation(dup, 12, ref sid, 4)) return -3;
    var si = new SI(); si.cb = Marshal.SizeOf(si); si.desktop = "winsta0\\default";
    PI pi; if (!CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out pi)) return -4;
    return pi.pid;
  }
}
"@
[GpSys]::Run($Command) | Set-Content -Path 'C:\golden\suite\logs\uac-launch.txt'
} catch { $_.Exception.Message | Set-Content -Path 'C:\golden\suite\logs\uac-launch.txt' }
'@

# Starts the helper (as SYSTEM, in the signed-in session). It answers the next permission prompt and writes what it saw to the file.
function Start-UacAnswerer {
    param([bool] $Allow = $true, [int] $TimeoutSec = 60)
    $dir = 'C:\golden\suite\logs'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $out = Join-Path $dir 'uac-answer.json'
    Remove-Item -LiteralPath $out, (Join-Path $dir 'uac-launch.txt') -ErrorAction SilentlyContinue
    Set-Content -Path (Join-Path $dir 'uac-helper.ps1') -Value $script:UacHelperText -Encoding utf8
    Set-Content -Path (Join-Path $dir 'uac-launcher.ps1') -Value $script:UacLauncherText -Encoding utf8
    $cmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Answer {1} -Out "{2}" -TimeoutSec {3}' -f (Join-Path $dir 'uac-helper.ps1'), $(if ($Allow) { 'Yes' } else { 'No' }), $out, $TimeoutSec
    Set-Content -Path (Join-Path $dir 'uac-cmd.txt') -Value $cmd -Encoding ascii
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f (Join-Path $dir 'uac-launcher.ps1'))
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName 'GoldenSuiteUacHelper' -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName 'GoldenSuiteUacHelper'
    $out
}

# What the helper saw and did: Found, Pressed, Text; $null when it never saw a prompt.
function Wait-UacAnswer {
    param([Parameter(Mandatory)] [string] $OutFile, [int] $TimeoutSec = 70)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 0.5 -Condition { if (Test-Path -LiteralPath $OutFile) { Get-Content -Raw -LiteralPath $OutFile } else { $null } }
    Unregister-ScheduledTask -TaskName 'GoldenSuiteUacHelper' -Confirm:$false -ErrorAction SilentlyContinue
    if (-not $w.Ok) { return $null }
    $r = $w.Value | ConvertFrom-Json
    if (-not $r.found) { return $null }
    [pscustomobject]@{ Text = [string]$r.text; Pressed = [bool]$r.pressed; Names = @($r.names) }
}


