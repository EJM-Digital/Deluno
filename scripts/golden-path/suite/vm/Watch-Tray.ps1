<#
.SYNOPSIS
    Reads the Deluno and Weir tray icons once a second into a log, from before Setup starts until it is stopped (golden path scenario suite).

.DESCRIPTION
    Run in the VM's signed-in desktop session (a scheduled task with an interactive logon), hidden. Each line of the log is
    one JSON object: the time, and for each app the icon's hover text, the colour of its dot and its brightness. The suite reads
    the log afterwards to show that the Deluno icon blinks while it sets itself up, and when each icon turned amber and green.
    Windows PowerShell 5.1.
#>
[CmdletBinding()]
param(
    [string] $Log = 'C:\golden\suite\logs\traywatch.jsonl',
    [double] $IntervalSeconds = 1,
    # Open Deluno's tray menu once, the first time its icon is found, and record the item names (the menu a person sees during the picker).
    [switch] $ProbeMenuOnce,
    [string] $MenuLog = 'C:\golden\suite\logs\traymenu-first.json',
    [string] $StopFile = 'C:\golden\suite\logs\traywatch.stop'
)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'Tray.Lib.ps1')
New-Item -ItemType Directory -Force -Path (Split-Path $Log) | Out-Null
Remove-Item -LiteralPath $StopFile -ErrorAction SilentlyContinue
$probed = -not $ProbeMenuOnce
while (-not (Test-Path -LiteralPath $StopFile)) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $line = [ordered]@{ t = [DateTime]::UtcNow.ToString('o') }
    # Read without any pop-up or key: the icons are promoted onto the taskbar (see Set-TrayIconsPromoted in Tray.Lib.ps1).
    try { Set-TrayIconsPromoted } catch { }
    foreach ($app in 'Deluno', 'Weir') {
        try { $r = Get-TrayDot -Icon $app -NoOverflow; $line[$app.ToLowerInvariant()] = [ordered]@{ found = $r.Found; hover = $r.Hover; colour = $r.Colour; brightness = $r.Brightness } }
        catch { $line[$app.ToLowerInvariant()] = [ordered]@{ found = $false; error = $_.Exception.Message } }
    }
    if (-not $probed -and $line.deluno -and $line.deluno.found) {
        $probed = $true
        try {
            $names = @(Open-TrayMenu -Icon 'Deluno')
            Close-TrayMenu
            ([ordered]@{ t = [DateTime]::UtcNow.ToString('o'); hover = $line.deluno.hover; items = $names } | ConvertTo-Json -Compress) | Set-Content -Path $MenuLog -Encoding utf8
        } catch { Add-Content -Path $Log -Value ("menu probe failed: " + $_.Exception.Message) -Encoding utf8 }
    }
    Add-Content -Path $Log -Value ($line | ConvertTo-Json -Compress -Depth 4) -Encoding utf8
    $left = $IntervalSeconds - $sw.Elapsed.TotalSeconds
    if ($left -gt 0) { Start-Sleep -Milliseconds ([int]($left * 1000)) } # reason: paces the sampler to its interval; the interval is the behaviour
}
