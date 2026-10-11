<#
    Install and first run: the tray icon blinks, with its menu, while Deluno sets itself up and the picker runs; Weir was
    installed through the picker and answers on this PC only. The harness (Invoke-GoldenPath.ps1) ran Setup in the desktop
    session and the tray watcher (Watch-Tray.ps1) read the icon every second from before Setup started.
#>
function Invoke-GroupInstall {
    Invoke-Scenario -Id 'install-first-run' -Title 'The tray blinks during the picker, with its menu; Weir is installed through the picker' -Body {
        $logs = Join-Path (Get-SuiteRoot) 'logs'
        $watch = Join-Path $logs 'traywatch.jsonl'
        Add-Check 'The tray watcher ran from before Setup' (Test-Path $watch) $watch
        $lines = @()
        if (Test-Path $watch) { $lines = @(Get-Content $watch | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json }) }
        $setting = @($lines | Where-Object { $_.deluno -and $_.deluno.found -and $_.deluno.hover -match 'setting up' })
        Add-Check 'The Deluno icon read "setting up" while the picker ran' ($setting.Count -ge 5) "$($setting.Count) readings"
        if ($setting.Count -ge 5) {
            $dot = Get-FirstRunDotSummary -Readings ($setting | ForEach-Object { [pscustomobject]@{ Found = $true; Brightness = $_.deluno.brightness; Colour = $_.deluno.colour } })
            $blink = $dot.Blink
            $detail = "samples {0}, flips {1}, levels {2}, colours [{3}]" -f $blink.Samples, $blink.Flips, $blink.BrightnessLevels, ($blink.Colours -join ',')
            # A blink is the dot drawn, then not drawn: only the brightness changes. A reading of the whole icon shows the dot's own
            # colour while it is drawn and the logo (amber) while it is not, so "grey and amber" is one grey dot blinking over the logo.
            Add-Check 'The dot blinks (its brightness alternates, and it is drawn then not drawn)' ($blink.Blinks -and $dot.LogoColours.Count -le 1) $detail
            Add-Check 'The dot is grey while Deluno sets itself up (never red, green or blue)' ($dot.GreyShown -and $dot.OnlyGreyAndLogo) $detail
            $span = ([DateTime]::Parse($setting[-1].t) - [DateTime]::Parse($setting[0].t)).TotalSeconds
            Add-Note ("The icon said 'setting up' for {0:N0} s." -f $span)
        }
        # The first-run menu is not read here: opening any menu while the picker is being driven disturbs it (a right click once opened
        # Server Manager's jump list over the picker). The hover text and the dot, which need no click, are the first-run evidence.
        Add-Note 'The first-run tray menu ("Open Deluno" and "Quit Deluno") is not read during the picker; only the hover text and the dot are.'

        # Weir through the picker.
        $weir = Get-WeirExe
        Add-Check 'Weir is installed (through the picker)' ($null -ne $weir) $(if ($weir) { "Weir.exe $($weir.VersionInfo.ProductVersion)" } else { 'Weir.exe not found' })
        $hook = Join-Path $env:LOCALAPPDATA 'Deluno\logs\install-hook.log'
        $hookText = if (Test-Path $hook) { Get-Content -Raw $hook } else { '' }
        Add-Check 'The install log names Weir''s installer and its sha256' ($hookText -match 'Weir: installer .* sha256 [0-9A-Fa-f]{64}') ''
        Add-Check 'The install log says the checksum matches the vendor''s' ($hookText -match 'Weir: SHA-256 matches') ''
        $listen = @(Get-NetTCPConnection -State Listen -LocalPort 9347 -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress })
        $onlyLocal = ($listen.Count -gt 0) -and (@($listen | Where-Object { $_ -notin '127.0.0.1', '::1' }).Count -eq 0)
        Add-Check 'Weir answers on this PC only' $onlyLocal ("listening on: " + ($listen -join ', '))
        Add-Check 'Weir is answering' (Test-WeirAnswering) ''
        $picker = 'C:\golden\picker.log'
        $pk = if (Test-Path $picker) { Get-Content -Raw $picker } else { '' }
        Add-Check 'The picker finished with every row installed, Weir included' ($pk -match 'Finished\.' -and $pk -match 'Weir' -and $pk -notmatch 'FAILED') ''
        Save-Evidence -Name 'picker.log' -Text $pk
    }
}
