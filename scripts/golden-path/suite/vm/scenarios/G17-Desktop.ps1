<#
    Things Deluno does on the computer's own desktop that a web page cannot show (coverage issues 1283 and 1278).

    companions.tray-install-picker   the Companion Tools page asks the tray to install an app only the tray installs (NZBGet): the ask is stored,
                                     the tray's picker window opens, naming it; a tool the tray does not install is refused in words.
    filesystem.native-folder-picker  POST /api/filesystem/native-folder-picker opens Windows' own folder window on this desktop; typing a folder
                                     and pressing OK returns it, Cancel returns "cancelled", and a second ask while it is open is refused in words.
#>

# Starts the POST in a background job (it waits for the person at the folder window) and returns the job.
function Start-NativePickerAsk {
    param([string] $InitialPath = 'C:\')
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

    Invoke-Scenario -Id 'filesystem.native-folder-picker' -Title "The native folder picker opens Windows' own folder window on this desktop: a typed folder and OK returns it, Cancel returns cancelled, and a second ask while it is open is refused in words" -Body {
        $target = 'C:\Media'
        $job = Start-NativePickerAsk -InitialPath 'C:\'
        try {
            $dlg = Wait-DesktopWindow -NamePattern 'Browse For Folder' -Process 'Deluno' -TimeoutSec 30
            Add-Check 'Windows'' own folder window (Browse For Folder) opens on this desktop, asking "Choose a folder for Deluno"' ($null -ne $dlg -and (Get-WindowText -Window $dlg) -match 'Choose a folder for Deluno') $(if ($dlg) { $dlg.Name } else { 'windows: ' + (Format-DesktopWindows) })
            if (-not $dlg) { return }
            $second = Try-Deluno -Method POST -Path '/api/filesystem/native-folder-picker' -Body @{ initialPath = 'C:\' } -TimeoutSec 20
            Add-Check 'A second ask while it is open is refused: "A folder window is already open on the server"' ($second.Status -eq 409 -and $second.Raw -match 'already open') "HTTP $($second.Status) $($second.Raw)"
            $edit = Get-WindowControls -Window $dlg | Where-Object { $_.Type -eq 'Edit' } | Select-Object -First 1
            if ($edit) { $edit.Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($target) }
            Click-WindowButton -Window $dlg -NamePattern '^OK$' | Out-Null
            $done = Wait-Job $job -Timeout 30
            $answer = if ($done) { Receive-Job $job } else { 'no answer' }
            Add-Check "OK returns the folder that was typed ($target)" ("$answer" -match '^200 ' -and "$answer" -match [regex]::Escape(($target -replace '\\', '\\\\')) -and "$answer" -match '"cancelled":\s*false') "$answer"
        }
        finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }

        $job = Start-NativePickerAsk -InitialPath $target
        try {
            $dlg = Wait-DesktopWindow -NamePattern 'Browse For Folder' -Process 'Deluno' -TimeoutSec 30
            if ($dlg) { Click-WindowButton -Window $dlg -NamePattern '^Cancel$' | Out-Null }
            $done = Wait-Job $job -Timeout 30
            $answer = if ($done) { Receive-Job $job } else { 'no answer' }
            Add-Check 'Cancel returns "cancelled": true and no folder' ($dlg -and "$answer" -match '^200 ' -and "$answer" -match '"cancelled":\s*true') "$answer"
        }
        finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }
    }
}

