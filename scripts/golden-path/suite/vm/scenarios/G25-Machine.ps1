<#
    What only a real Windows machine can show (coverage issues 1278 and 1276). Every scenario makes what it needs inside the VM and takes
    it away again, so the VM is as it was.

      machine.system-readings   the System overview reads the machine itself: the operating system, uptime, memory, the drives and a pending
                                reboot agree with what Windows says, and the temperature is a number or absent, never a guess
      machine.guide-contract    scripts/test-guide-package-contract.ps1 passes against the live Deluno
      machine.browse-errors     the folder browser: a folder that is not there is 404 in words, one the account may not list is 403
      machine.browse-unc-large  the folder browser on a share (\\localhost\...), a share that refuses the account, a mapped drive whose
                                share has gone, and a folder of 5000 sub-folders
      machine.picker-timeout    a folder window nobody answers is closed after 90 s with 408 and the next ask is not refused as "already open"
      machine.long-path         a destination that is longer than 260 characters, and one whose folder name is longer than 255, are each
                                either imported whole or refused in plain words, with nothing half-copied left and the source whole
      machine.automation-off    with automatic jobs off, a finished download sits in the client untouched (no hand-off, no import) and is
                                taken as soon as they are switched on again
#>

$script:MachineDir = 'C:\golden\suite\machine'

function Get-MachineUser { "$env:USERDOMAIN\$env:USERNAME" }

# A folder with many sub-folders made quickly (the .NET call, not a shell loop).
function New-ManyFolders {
    param([Parameter(Mandatory)] [string] $Root, [int] $Count)
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    for ($i = 1; $i -le $Count; $i++) { [void][System.IO.Directory]::CreateDirectory((Join-Path $Root ('folder{0:D5}' -f $i))) }
}

# ---------------------------------------------------------------- the live hub, read the way the browser reads it

# Opens Deluno's SignalR hub (JSON protocol) over a WebSocket with the signed-in token, joins the subjects and returns the connection.
function Open-HubConnection {
    param([string[]] $Subjects = @('live', 'queue', 'activity'))
    $neg = Try-Deluno -Method POST -Path '/hubs/deluno/negotiate?negotiateVersion=1'
    if ($neg.Status -ne 200) { throw "The hub's negotiate answered $($neg.Status). $($neg.Raw)" }
    $id = if ($neg.Data.connectionToken) { $neg.Data.connectionToken } else { $neg.Data.connectionId }
    $wsUrl = ($script:S.DelunoUrl -replace '^http', 'ws') + '/hubs/deluno?id=' + [uri]::EscapeDataString($id) + '&access_token=' + [uri]::EscapeDataString($script:S.Token)
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([uri]$wsUrl, [Threading.CancellationToken]::None).Wait(15000) | Out-Null
    $conn = [pscustomobject]@{ Ws = $ws; Buffer = (New-Object byte[] 65536); Pending = $null; Text = (New-Object Text.StringBuilder); Messages = (New-Object System.Collections.ArrayList); Handshaken = $false }
    Send-HubText -Connection $conn -Text '{"protocol":"json","version":1}'
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not $conn.Handshaken) { Read-HubMessages -Connection $conn -WaitMs 300 }
    Send-HubText -Connection $conn -Text ('{"type":1,"invocationId":"1","target":"Subscribe","arguments":[[' + (($Subjects | ForEach-Object { '"' + $_ + '"' }) -join ',') + ']]}')
    $conn
}

function Send-HubText {
    param([Parameter(Mandatory)] $Connection, [Parameter(Mandatory)] [string] $Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text + [char]0x1e)
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $bytes)
    $Connection.Ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(10000) | Out-Null
}

# Reads whatever the hub has pushed in the next WaitMs; each complete message is added, with the time it arrived, to Connection.Messages.
function Read-HubMessages {
    param([Parameter(Mandatory)] $Connection, [int] $WaitMs = 300)
    if ($Connection.Ws.State -ne 'Open') { return }
    if (-not $Connection.Pending) {
        $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $Connection.Buffer)
        $Connection.Pending = $Connection.Ws.ReceiveAsync($seg, [Threading.CancellationToken]::None)
    }
    if (-not $Connection.Pending.Wait($WaitMs)) { return }
    $r = $Connection.Pending.Result
    $Connection.Pending = $null
    [void]$Connection.Text.Append([Text.Encoding]::UTF8.GetString($Connection.Buffer, 0, $r.Count))
    $parts = $Connection.Text.ToString().Split([char]0x1e)
    [void]$Connection.Text.Clear()
    [void]$Connection.Text.Append($parts[$parts.Count - 1])
    for ($i = 0; $i -lt $parts.Count - 1; $i++) {
        if ($parts[$i] -eq '{}') { $Connection.Handshaken = $true; continue }
        $m = $null
        try { $m = $parts[$i] | ConvertFrom-Json } catch { continue }
        if ($m.type -eq 1 -and $m.target -eq 'RealtimeEvent') {
            # Deluno pushes every event as one "RealtimeEvent" carrying the event's name, its screen subject, its order number and its data.
            $env = $m.arguments[0]
            [void]$Connection.Messages.Add([pscustomobject]@{ At = [DateTime]::UtcNow; Type = 1; Target = $env.name; Seq = $env.seq; Subject = $env.subject; Args = @($env.data) })
        }
        else { [void]$Connection.Messages.Add([pscustomobject]@{ At = [DateTime]::UtcNow; Type = $m.type; Target = $m.target; Seq = $null; Subject = $null; Args = $m.arguments }) }
    }
}

function Close-HubConnection {
    param($Connection)
    try { if ($Connection -and $Connection.Ws.State -eq 'Open') { $Connection.Ws.CloseAsync('NormalClosure', 'done', [Threading.CancellationToken]::None).Wait(5000) | Out-Null } } catch { }
    try { if ($Connection) { $Connection.Ws.Dispose() } } catch { }
}

function Invoke-GroupMachine {
    Invoke-Scenario -Id 'machine.system-readings' -Title 'The System overview agrees with Windows: operating system, uptime, memory, drives and a pending reboot; the temperature is a number or absent' -Body {
        $o = Try-Deluno -Path '/api/system/overview'
        Save-Evidence -Name 'machine-system-overview.json' -Text $o.Raw
        Add-Check 'The System overview answers' ($o.Status -eq 200 -and $o.Data.machine) "HTTP $($o.Status)"
        if (-not ($o.Status -eq 200 -and $o.Data.machine)) { return }
        $m = $o.Data.machine
        $os = Get-CimInstance Win32_OperatingSystem
        Add-Check 'It names this operating system' ("$($m.os)" -match [regex]::Escape(($os.Caption.Trim() -replace '^Microsoft\s+', '')) -or "$($m.os)" -match [regex]::Escape($os.Version)) "Windows: $($os.Caption) $($os.Version); Deluno: $($m.os)"
        $realUptime = ((Get-Date) - $os.LastBootUpTime).TotalSeconds
        Add-Check 'Its uptime is the real time since the last start (within two minutes)' ([math]::Abs($m.uptimeSeconds - $realUptime) -lt 120) ("Windows {0:N0} s, Deluno {1:N0} s" -f $realUptime, $m.uptimeSeconds)
        $totalBytes = [long]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory)
        Add-Check 'Its memory total is the installed memory (within 2%)' ([math]::Abs($m.memoryTotalBytes - $totalBytes) -le ($totalBytes * 0.02)) ("Windows {0:N0} MB, Deluno {1:N0} MB" -f ($totalBytes / 1MB), ($m.memoryTotalBytes / 1MB))
        $freeBytes = [long]$os.FreePhysicalMemory * 1KB
        $usedReal = $totalBytes - $freeBytes
        Add-Check 'Its memory in use is what Windows says is in use (within 10% of the total)' ([math]::Abs($m.memoryUsedBytes - $usedReal) -le ($totalBytes * 0.10)) ("Windows {0:N0} MB, Deluno {1:N0} MB" -f ($usedReal / 1MB), ($m.memoryUsedBytes / 1MB))
        Add-Check 'It counts the processors' ($m.cores -eq [Environment]::ProcessorCount) "Windows $([Environment]::ProcessorCount), Deluno $($m.cores)"
        $pending = $false
        foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { if (Test-Path $k) { $pending = $true } }
        $sm = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
        if ($sm -and $sm.PendingFileRenameOperations) { $pending = $true }
        Add-Check 'A pending reboot is reported as Windows has it (or "cannot say")' ($null -eq $m.rebootPending -or [bool]$m.rebootPending -eq $pending) "Windows $pending, Deluno $($m.rebootPending)"
        $temp = $m.cpuTemperatureC
        Add-Check 'The temperature is a believable number or absent (never a made-up zero)' ($null -eq $temp -or ($temp -gt 10 -and $temp -lt 120)) "$(if ($null -eq $temp) { 'absent: this machine has no sensor' } else { "$temp C" })"
        $c = @($o.Data.drives | Where-Object { "$($_.path)" -match '^[Cc]:' -or "$($_.name)" -match '^[Cc]:' }) | Select-Object -First 1
        $cReal = New-Object System.IO.DriveInfo('C')
        Add-Check 'The drive that holds Deluno is listed with its real size (within 1%)' ($c -and [math]::Abs($c.totalBytes - $cReal.TotalSize) -le ($cReal.TotalSize * 0.01)) $(if ($c) { "Windows {0:N1} GB, Deluno {1:N1} GB" -f ($cReal.TotalSize / 1GB), ($c.totalBytes / 1GB) } else { 'no C: in ' + (($o.Data.drives | ForEach-Object { "$($_.path)" }) -join ', ') })
        Add-Check 'Its free space is the real free space (within 1% of the drive)' ($c -and [math]::Abs($c.freeBytes - $cReal.AvailableFreeSpace) -le ($cReal.TotalSize * 0.01)) $(if ($c) { "Windows {0:N1} GB, Deluno {1:N1} GB" -f ($cReal.AvailableFreeSpace / 1GB), ($c.freeBytes / 1GB) } else { '' })
        Add-Check 'Deluno reports the address it listens on and that it runs as the app or a service' ($o.Data.instance.address -and $o.Data.instance.runsAs) "$($o.Data.instance.address), $($o.Data.instance.runsAs)"
    }

    Invoke-Scenario -Id 'machine.guide-contract' -Title 'scripts/test-guide-package-contract.ps1 passes against the live Deluno: package, inventory, versions and every profile compile' -Body {
        $script = 'C:\golden\scripts\test-guide-package-contract.ps1'
        Add-Check 'The contract script is in the VM' (Test-Path -LiteralPath $script) $script
        if (-not (Test-Path -LiteralPath $script)) { return }
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -BaseUrl $script:S.DelunoUrl -Username golden -Password (Get-SuiteSecret 'deluno-password') 2>&1 | Out-String
        $code = $LASTEXITCODE
        $text = Hide-Secrets $out
        Save-Evidence -Name 'machine-guide-contract.txt' -Text $text
        $passes = ([regex]::Matches($text, '(?m)^PASS\s')).Count
        Add-Check 'The script exits 0 and says the contract passed' ($code -eq 0 -and $text -match 'Guide package contract passed') "exit $code; $passes checks passed"
        Add-Check 'It checked the package, the inventory, the versions and at least one profile' ($text -match 'package has a stable id' -and $text -match 'inventory has zero unexplained' -and $text -match 'version history' -and $text -match 'compiled plan hash') ''
    }

    Invoke-Scenario -Id 'machine.browse-errors' -Title 'The folder browser answers in words: a missing folder is 404, one the account may not list is 403' -Body {
        $dir = Join-Path $script:MachineDir 'browse'
        $locked = Join-Path $dir 'Locked'
        $user = Get-MachineUser
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path $locked, (Join-Path $dir 'Open\Child') | Out-Null
            $open = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString($dir))
            Add-Check 'A folder that exists lists its sub-folders (200)' ($open.Status -eq 200 -and @($open.Data.entries | Where-Object { $_.name -eq 'Open' }).Count -eq 1) "HTTP $($open.Status)"
            $missing = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString((Join-Path $dir 'NotThere')))
            Add-Check 'A folder that is not there is 404, "The requested directory does not exist."' ($missing.Status -eq 404 -and $missing.Raw -match 'The requested directory does not exist') "HTTP $($missing.Status) $($missing.Raw)"
            & icacls.exe $locked /deny "${user}:(RD)" | Out-Null
            $denied = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString($locked))
            Add-Check 'A folder the account may not list is 403 (not 500, not an empty list)' ($denied.Status -eq 403) "HTTP $($denied.Status) $($denied.Raw)"
            $diag = Try-Deluno -Method POST -Path '/api/filesystem/path-diagnostics' -Body @{ path = $locked }
            Add-Check 'The path check says it cannot read the folder, in words' ($diag.Status -eq 200 -and $diag.Raw -match '(?i)read check failed|access to the path') "HTTP $($diag.Status) $($diag.Raw)"
            $still = Try-Deluno -Path '/api/system/health'
            Add-Check 'Deluno still answers other calls' ($still.Status -eq 200) "HTTP $($still.Status)"
        }
        finally {
            & icacls.exe $locked /remove:d $user 2>&1 | Out-Null
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-Scenario -Id 'machine.browse-unc-large' -Title 'The folder browser on a share, on a share that refuses the account, on a mapped drive whose share is gone, and on a folder of 5000 sub-folders' -Body {
        $dir = Join-Path $script:MachineDir 'unc'
        $good = "GoldBrowseOk$(New-FilmToken)".Replace('-', '')
        $bad = "GoldBrowseNo$(New-FilmToken)".Replace('-', '')
        $gone = "GoldBrowseGone$(New-FilmToken)".Replace('-', '')
        $user = Get-MachineUser
        $letter = $null
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
            foreach ($n in 'ok', 'no', 'gone') { New-Item -ItemType Directory -Force -Path (Join-Path $dir $n) | Out-Null }
            1..3 | ForEach-Object { New-Item -ItemType Directory -Force -Path (Join-Path $dir "ok\Sub$_") | Out-Null }
            New-SmbShare -Name $good -Path (Join-Path $dir 'ok') -FullAccess $user | Out-Null
            New-SmbShare -Name $gone -Path (Join-Path $dir 'gone') -FullAccess $user | Out-Null
            New-SmbShare -Name $bad -Path (Join-Path $dir 'no') -NoAccess $user | Out-Null

            $t = [Diagnostics.Stopwatch]::StartNew()
            $unc = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString("\\localhost\$good"))
            Add-Check 'A share (\\localhost\...) lists its sub-folders' ($unc.Status -eq 200 -and @($unc.Data.entries).Count -eq 3) ("HTTP $($unc.Status), {0} entries in {1:N1} s" -f @($unc.Data.entries).Count, $t.Elapsed.TotalSeconds)
            $diagOk = Try-Deluno -Method POST -Path '/api/filesystem/path-diagnostics' -Body @{ path = "\\localhost\$good" }
            Add-Check 'The path check on the share reads it and says it is a UNC path that needs the account to hold share permission' ($diagOk.Status -eq 200 -and $diagOk.Raw -match 'UNC path' -and $diagOk.Data.canRead) $diagOk.Raw

            $t.Restart()
            $no = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString("\\localhost\$bad")) -TimeoutSec 60
            Add-Note ('The share that refuses the account answered HTTP ' + $no.Status + ' in ' + [math]::Round($t.Elapsed.TotalSeconds, 1) + ' s: ' + $no.Raw)
            Add-Check 'A share that refuses the account is not listed, and is a plain 403 or 404 in words, within 30 s (never a 500 or a hang)' ($no.Status -in 403, 404 -and $t.Elapsed.TotalSeconds -lt 30) ("HTTP $($no.Status) in {0:N1} s" -f $t.Elapsed.TotalSeconds)
            $diagNo = Try-Deluno -Method POST -Path '/api/filesystem/path-diagnostics' -Body @{ path = "\\localhost\$bad" }
            Add-Check 'The path check on it says it cannot be read and points at network-share permission' ($diagNo.Status -eq 200 -and $diagNo.Data.canRead -eq $false -and $diagNo.Raw -match '(?i)UNC path|share') $diagNo.Raw

            # A mapped drive whose share goes away.
            $free = [char[]]'ZYXWVUTSRQP' | Where-Object { -not (Test-Path "${_}:\") } | Select-Object -First 1
            $letter = "$free"
            & net.exe use "${letter}:" "\\localhost\$gone" /persistent:no 2>&1 | Out-Null
            $mapped = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString("${letter}:\"))
            Add-Check 'A mapped drive lists while its share is there' ($mapped.Status -eq 200) "HTTP $($mapped.Status)"
            Remove-SmbShare -Name $gone -Force
            $t.Restart()
            $off = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString("${letter}:\")) -TimeoutSec 60
            Add-Note ('The mapped drive whose share is gone answered HTTP ' + $off.Status + ' in ' + [math]::Round($t.Elapsed.TotalSeconds, 1) + ' s: ' + $off.Raw)
            Add-Check 'A mapped drive whose share is gone answers in words within 30 s: 404, 403 or 400 with the message (never a 500 or a hang)' ($off.Status -in 400, 403, 404 -and $t.Elapsed.TotalSeconds -lt 30 -and $off.Raw -match '\w') ("HTTP $($off.Status) in {0:N1} s" -f $t.Elapsed.TotalSeconds)
            $alive = Try-Deluno -Path '/api/system/health'
            Add-Check 'Deluno still answers other calls afterwards' ($alive.Status -eq 200) "HTTP $($alive.Status)"

            # A very large folder.
            $big = Join-Path $dir 'big'
            New-ManyFolders -Root $big -Count 5000
            $t.Restart()
            $large = Try-Deluno -Path ('/api/filesystem/directories?path=' + [uri]::EscapeDataString($big)) -TimeoutSec 120
            $count = @($large.Data.entries).Count
            Add-Check 'A folder of 5000 sub-folders lists all of them' ($large.Status -eq 200 -and $count -eq 5000) ("HTTP $($large.Status), $count entries in {0:N1} s" -f $t.Elapsed.TotalSeconds)
            Add-Check 'They come back in name order and in under 10 s' ($large.Status -eq 200 -and $count -gt 1 -and $large.Data.entries[0].name -eq 'folder00001' -and $large.Data.entries[$count - 1].name -eq 'folder05000' -and $t.Elapsed.TotalSeconds -lt 10) ("{0:N1} s" -f $t.Elapsed.TotalSeconds)
        }
        finally {
            if ($letter) { & net.exe use "${letter}:" /delete /y 2>&1 | Out-Null }
            foreach ($n in $good, $bad, $gone) { Remove-SmbShare -Name $n -Force -ErrorAction SilentlyContinue }
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-Scenario -Id 'machine.picker-timeout' -Title 'A folder window nobody answers is closed after 90 s with 408, and the next ask is not refused as already open' -Body {
        $target = 'C:\Media'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        $seenBefore = @(Get-DesktopWindows | ForEach-Object { "$($_.ProcessId)|$($_.Name)" })
        $job = Start-NativePickerAsk -InitialPath $target
        $started = Get-Date
        try {
            $dlg = Wait-NewFolderWindow -Before $seenBefore -TimeoutSec 120
            Add-Check 'The folder window opened on the desktop' ($null -ne $dlg) $(if ($dlg) { $dlg.Name } else { 'windows: ' + (Format-DesktopWindows) })
            # Nobody answers. Deluno gives up after 90 s.
            $done = Wait-Job $job -Timeout 150
            $answer = if ($done) { Receive-Job $job } else { 'no answer' }
            $waited = ((Get-Date) - $started).TotalSeconds
            Add-Check 'Deluno answered 408, "Nobody answered the folder window on the server in time"' ("$answer" -match '^408 ' -and "$answer" -match 'Nobody answered') "$answer"
            Add-Check 'It waited about 90 s first (not at once, not minutes)' ($waited -ge 85 -and $waited -le 140) ("{0:N0} s from the ask" -f $waited)
            $closed = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { -not (Wait-NewFolderWindow -Before $seenBefore -TimeoutSec 1) }
            Add-Check 'The unanswered window was closed by Deluno' $closed.Ok ''
        }
        finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }
        # The guard is let go: a new ask opens a window again (it is cancelled at once).
        $before2 = @(Get-DesktopWindows | ForEach-Object { "$($_.ProcessId)|$($_.Name)" })
        $job2 = Start-NativePickerAsk -InitialPath $target
        try {
            $dlg2 = Wait-NewFolderWindow -Before $before2 -TimeoutSec 90
            Add-Check 'The next ask opens a window (it is not refused as "already open")' ($null -ne $dlg2) $(if ($dlg2) { $dlg2.Name } else { 'no window' })
            if ($dlg2) { Click-WindowButton -Window $dlg2 -NamePattern '^Cancel$' | Out-Null }
            $d2 = Wait-Job $job2 -Timeout 60
            $a2 = if ($d2) { Receive-Job $job2 } else { 'no answer' }
            Add-Check 'It ends with "cancelled" when cancelled' ("$a2" -match '^200 ' -and "$a2" -match '"cancelled":\s*true') "$a2"
        }
        finally { Remove-Job $job2 -Force -ErrorAction SilentlyContinue }
    }

    Invoke-Scenario -Id 'machine.long-path' -Title 'A destination longer than 260 characters, and one with a folder name longer than 255, are imported whole or refused in plain words, never left half-copied' -Body {
        $token = New-FilmToken
        $srcRoot = Join-Path $script:VolDir "src_Long$token"
        $libRoot = "C:\Media\Long$token"
        $lib = $null
        try {
            $lib = New-VolumeLibrary -Name "Long $token" -Root $libRoot
            $long = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction SilentlyContinue).LongPathsEnabled
            Add-Note "Windows' LongPathsEnabled setting: $long"
            foreach ($case in @(
                    @{ Name = 'over 260 in all'; Pad = 120 },
                    @{ Name = 'a folder name over 255'; Pad = 300 })) {
                $title = ("Long $token " + ('Word' * [int]($case.Pad / 4))).Trim()
                $src = New-VolumeSource -Folder $srcRoot -Title ('Src' + $token + $case.Pad)
                $movie = New-VolumeMovie -Title $title -Library $lib
                try {
                    $r = Invoke-VolumeImport -Source $src -Library $lib -Title $title -Mode 'copy' -Fallback $true
                    $after = @(Get-ChildItem -LiteralPath ('\\?\' + $libRoot) -Recurse -File -Force -ErrorAction SilentlyContinue)
                    $videos = @($after | Where-Object { $_.Extension -in '.mkv', '.mp4', '.avi' })
                    $leftovers = @($after | Where-Object { $_.Extension -notin '.mkv', '.mp4', '.avi' })
                    $destLen = if ($videos.Count) { $videos[0].FullName.Length } else { 0 }
                    Add-Note ("$($case.Name): import succeeded=$($r.Succeeded); library holds $($videos.Count) film(s) (path length $destLen); message: $($r.Message)")
                    Add-Check "$($case.Name): Deluno answered the import (no crash)" ($r.Status -in 200, 207, 400, 409, 422) "HTTP $($r.Status)"
                    Add-Check "$($case.Name): the import either put one whole film in place or said in plain words why not" (($r.Succeeded -and $videos.Count -eq 1 -and $videos[0].Length -eq (Get-Item -LiteralPath $script:Fx.Base['small']).Length) -or (-not $r.Succeeded -and $videos.Count -eq 0 -and $r.Message -match '\w{3}' -and $r.Message -notmatch '(?m)^\s+at |System\.[A-Z]\w+Exception|Unhandled')) $r.Message
                    Add-Check "$($case.Name): no staging file or half-copy is left in the library" ($leftovers.Count -eq 0) (($leftovers | ForEach-Object { $_.Name }) -join '; ')
                    Add-Check "$($case.Name): a refused import leaves the source file whole" ($r.Succeeded -or ((Test-Path -LiteralPath $src) -and (Get-Item -LiteralPath $src).Length -eq (Get-Item -LiteralPath $script:Fx.Base['small']).Length)) $(if ($r.Succeeded) { 'imported, so the copy import removed its checked original as it always does' } else { $src })
                    $alive = Try-Deluno -Path '/api/system/health'
                    Add-Check "$($case.Name): Deluno still answers" ($alive.Status -eq 200) "HTTP $($alive.Status)"
                }
                finally { Remove-VolumeMovie -Movie $movie }
            }
        }
        finally {
            Remove-VolumeLibrary -Library $lib
            Remove-Item -LiteralPath ('\\?\' + $libRoot), $srcRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-Scenario -Id 'machine.automation-off' -Title 'With automatic jobs off, a finished download is left alone in the client (no hand-off, no import) until they are switched on' -Body {
        $film = $null
        try {
            Start-AutomationPause
            $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'idle') -Kind 'qbittorrent'
            $fin = Wait-FilmFinished -Film $film -TimeoutSec 240
            Add-Check 'The real client finished the download' $fin.Ok "after $($fin.Seconds) s"
            Start-Sleep -Seconds 100 # reason: the quiet is the behaviour under test; the import pass runs every 15 s, so this is more than six passes
            Add-Check 'Nothing was handed to Weir in that time' ($null -eq (Get-FilmHandoff -Film $film)) ''
            Add-Check 'Nothing was imported' ($null -eq (Find-Imported -Title $film.Title)) ''
            $act = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match 'Sent to the processor|Imported into the library' })
            Add-Check 'Activity shows no hand-off or import for it' ($act.Count -eq 0) ($act -join ' || ')
            Add-Check 'The files are still in the client''s folder' (@(Get-FilmLeftovers -Film $film).Count -gt 0) ''
            Stop-AutomationPause
            $r = Wait-FilmImported -Film $film
            Add-Check 'Switched on again, the same download is handed to Weir and imported' ($r.HandedOff -and $r.Settled -and $r.Imported) "handed $($r.HandedOff), settled $($r.Settled), imported $($r.Imported)"
        }
        finally { Stop-AutomationPause }
    }

    Invoke-Scenario -Id 'machine.hub-pushes' -Title 'A real grab, download and import push what a person sees to a browser connected to the hub, in order: the journey stages, the progress and Activity' -Body {
        $hub = $null
        try {
            $hub = Open-HubConnection -Subjects @('live', 'queue', 'activity')
            Add-Check 'The hub accepted the sign-in token and the connection is open' ($hub.Ws.State -eq 'Open') "$($hub.Ws.State)"
            $film = Send-Film -Film (New-FilmFor -Kind 'qbittorrent' -Layout 'folder' -Label 'push') -Kind 'qbittorrent'
            $until = (Get-Date).AddMinutes(9)
            $done = $false
            while ((Get-Date) -lt $until -and -not $done) {
                Read-HubMessages -Connection $hub -WaitMs 500
                $done = @($hub.Messages | Where-Object { $_.Target -eq 'ActivityEventAdded' -and "$($_.Args[0].message)" -match [regex]::Escape($film.Title) -and "$($_.Args[0].message)" -match 'Imported into the library' }).Count -gt 0
            }
            for ($i = 0; $i -lt 10; $i++) { Read-HubMessages -Connection $hub -WaitMs 300 } # the last pushes of the import
            $mine = @($hub.Messages | Where-Object { $_.Type -eq 1 })
            $names = ($mine | Group-Object Target | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
            Add-Note "Everything pushed while the film ran: $names"
            Save-Evidence -Name 'machine-hub-pushes.json' -Text (($mine | Select-Object -First 400 | ForEach-Object { [pscustomobject]@{ at = $_.At.ToString('o'); target = $_.Target; args = $_.Args } }) | ConvertTo-Json -Depth 6)
            $stages = @($mine | Where-Object { $_.Target -eq 'JourneyStageChanged' -and "$($_.Args[0].releaseName)" -eq $film.Release })
            Add-Check 'The Live board was told of at least three steps of this film''s journey' ($stages.Count -ge 3) (($stages | ForEach-Object { $_.Args[0].stage }) -join ' > ')
            $times = @($stages | ForEach-Object { [DateTimeOffset]::Parse($_.Args[0].at) })
            Add-Check 'Those steps arrived in the order they happened (their own times never go backwards)' ($times.Count -ge 2 -and ((($times | Sort-Object) -join ',') -eq ($times -join ','))) ''
            Add-Check 'The last step it was told is the import' ($stages.Count -gt 0 -and "$($stages[-1].Args[0].stage)" -match '(?i)import') $(if ($stages.Count) { "$($stages[-1].Args[0].stage)" } else { 'no steps' })
            $prog = @($mine | Where-Object { $_.Target -eq 'DownloadProgress' -or $_.Target -eq 'DownloadSpeedSample' })
            Add-Check 'Download progress was pushed while the client worked' ($prog.Count -ge 1) "$($prog.Count) pushes"
            $ho = @($mine | Where-Object { $_.Target -eq 'ProcessorHandoffChanged' -and "$($_.Args[0].releaseName)" -eq $film.Release })
            Add-Check 'The hand-off to Weir was pushed as it moved, ending completed' ($ho.Count -ge 1 -and "$($ho[-1].Args[0].state)" -match 'completed|passed') (($ho | ForEach-Object { $_.Args[0].state }) -join ' > ')
            $acts = @($mine | Where-Object { $_.Target -eq 'ActivityEventAdded' -and "$($_.Args[0].message)" -match [regex]::Escape($film.Title) })
            $idx = @('Sent to the download client', 'Sent to the processor', 'Imported into the library') | ForEach-Object { $p = $_; $k = -1; for ($j = 0; $j -lt $acts.Count; $j++) { if ("$($acts[$j].Args[0].message)" -match $p) { $k = $j; break } }; $k }
            Add-Check 'Activity pushed "Sent to the download client", "Sent to the processor" and "Imported into the library", in that order' (($idx -notcontains -1) -and $idx[0] -lt $idx[1] -and $idx[1] -lt $idx[2]) ("positions: " + ($idx -join ', '))
        }
        finally { Close-HubConnection -Connection $hub }
    }
}
