<#
    The guided setup walked through in the browser on the installed Windows app (coverage issues 1283 and 1302).

    guided-setup.walk   Run by the host on a clean machine of its own (the picker has installed the five download clients and Weir; Deluno has
                        no account and nothing set up). The scenario makes the first account, then walks the guide step by step in Edge with
                        real mouse events: Connect all, Weir's own account, every Yes/No question answered, the folders, the quality choices
                        and Create baseline. It then checks what that really made, against the real apps: the libraries, the clients Deluno
                        connected, Weir's workflows linked and watching, and the folder chain agreeing.
#>

# The unanswered Yes/No questions on the page: each radio group (role=radiogroup) with no ticked radio, by its label.
function Get-UnansweredQuestions {
    param($Page)
    $js = @'
(function(){
  var out = [];
  document.querySelectorAll('[role=radiogroup]').forEach(function(g){
    var r = g.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return;
    var radios = g.querySelectorAll('[role=radio]'); if (radios.length < 2) return;
    var answered = false; radios.forEach(function(x){ if (x.getAttribute('aria-checked') === 'true') answered = true; });
    if (!answered) out.push(g.getAttribute('aria-label') || '');
  });
  return out;
})()
'@
    @(Invoke-CdpEval -Page $Page -Script $js)
}

# Answers one question with real mouse events on its Yes or No radio.
function Select-Answer {
    param($Page, [Parameter(Mandatory)] [string] $Question, [Parameter(Mandatory)] [ValidateSet('Yes', 'No')] [string] $Answer)
    $label = ($Question + ' ' + $Answer)
    Click-PageText -Page $Page -Pattern ('^' + [regex]::Escape($label) + '$') -TimeoutSec 10 | Out-Null
}

# Answers every unanswered Yes/No question on the step: Yes where the question starts with one of the given texts, No otherwise.
function Answer-StepQuestions {
    param($Page, [string[]] $YesStarts = @())
    $asked = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 40; $i++) {
        $open = @(Get-UnansweredQuestions -Page $Page)
        if ($open.Count -eq 0) { break }
        $q = $open[0]
        $yes = $false
        foreach ($s in $YesStarts) { if ($q -like "$s*") { $yes = $true } }
        Select-Answer -Page $Page -Question $q -Answer $(if ($yes) { 'Yes' } else { 'No' })
        [void]$asked.Add("$q => $(if ($yes) { 'Yes' } else { 'No' })")
        Start-Sleep -Milliseconds 300 # reason: lets the page draw the answer before the next question is read; the page has no ready signal
    }
    @($asked)
}

function Set-PageInputAt {
    param($Page, [Parameter(Mandatory)] [string] $Selector, [Parameter(Mandatory)] [int] $Index, [Parameter(Mandatory)] [string] $Value)
    $js = "(function(){var e=document.querySelectorAll(" + ($Selector | ConvertTo-Json) + ")[" + $Index + "];if(!e)return false;e.focus();var d=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(e),'value');d.set.call(e," + ($Value | ConvertTo-Json) + ");e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));return true;})()"
    [bool](Invoke-CdpEval -Page $Page -Script $js)
}

# The visible text of the step's form and the labels of its enabled buttons, for the evidence and for a failure's detail.
function Get-StepSnapshot {
    param($Page)
    $js = @'
(function(){
  var btns = []; document.querySelectorAll('button,[role=radio],a,input').forEach(function(b){
    var r = b.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return;
    var t = (b.getAttribute('aria-label') || b.innerText || b.placeholder || b.value || '').trim().replace(/\s+/g,' ').slice(0,80);
    if (t) btns.push((b.disabled ? '[off] ' : '') + b.tagName.toLowerCase() + ': ' + t);
  });
  return {text: document.body.innerText.replace(/\s+/g,' ').slice(0,1500), controls: btns.slice(0,80)};
})()
'@
    Invoke-CdpEval -Page $Page -Script $js
}

# Presses Continue when it is enabled; returns $false (and the snapshot) when it stays disabled.
function Press-Continue {
    param($Page, [int] $TimeoutSec = 15)
    try { Click-PageText -Page $Page -Pattern '^Continue$' -TimeoutSec $TimeoutSec | Out-Null; $true } catch { $false }
}

function Invoke-GroupGuide {
    Invoke-Scenario -Id 'guided-setup.walk' -Title 'The installed Windows app is set up end to end through the guided setup in the browser: clients and Weir connected, every question answered, the baseline created, and the libraries, the Weir workflows and the folder chain checked against the real apps' -Body {
        $moviesRoot = 'C:\Media\Movies'; $tvRoot = 'C:\Media\TV'
        $moviesDown = 'C:\Downloads\Completed\Movies'; $tvDown = 'C:\Downloads\Completed\TV'
        foreach ($f in $moviesRoot, $tvRoot, $moviesDown, $tvDown) { New-Item -ItemType Directory -Force -Path $f | Out-Null }

        # ---- The installed state: the picker put the clients and Weir on this computer; Deluno has no account and nothing set up.
        $boot = Wait-DelunoUp
        Add-Check 'Deluno is a fresh install nobody has set up (no account yet)' ([bool]$boot.requiresSetup) ($boot | ConvertTo-Json -Compress)
        Add-Check 'The tray icon is showing and Deluno runs from the installed copy' ([bool](Find-TrayIcon -Icon 'Deluno') -and (Get-DelunoProcess).Count -gt 0) ''
        $weirExe = Get-WeirExe
        Add-Check 'Weir is installed (the picker put it on this computer)' ($null -ne $weirExe) $(if ($weirExe) { $weirExe.FullName } else { 'no Weir.exe' })

        # ---- The first account, through Deluno's own setup page (the same call the page makes).
        New-SuiteSecret 'deluno-password' | Out-Null
        New-SuiteSecret 'weir-password' | Out-Null
        New-SuiteSecret 'weir-username' | Out-Null
        $r = Try-Deluno -Method POST -Path '/api/auth/bootstrap' -Body @{ username = 'golden'; displayName = 'Golden suite'; password = (Get-SuiteSecret 'deluno-password') }
        Add-Check 'The first account is made' ($r.Status -eq 200) "HTTP $($r.Status)"
        Connect-Deluno

        Start-Edge
        $page = New-CdpPage 'about:blank'
        try {
            Open-DelunoPage -Page $page -Path '/setup-guide' -Login $script:S.Login
            $snap = Get-StepSnapshot -Page $page
            Save-Evidence -Name 'guide-first-screen.txt' -Text ($snap.text + "`n" + ($snap.controls -join "`n"))
            Add-Check 'The guided setup opens at its first step for the new account' ($snap.text -match 'Guided setup' -and $snap.text -match 'Your account') ($snap.text.Substring(0, [math]::Min(160, $snap.text.Length)))

            $steps = @('Your account', 'Time and region', 'How you sign in', 'Addresses', 'Other devices', 'Updates', 'Your library', 'Folders', 'Library Profiles', 'Find & Download', 'Connect installed apps', 'Automation', 'Start')
            $asked = New-Object System.Collections.ArrayList
            $stuckAt = $null
            for ($i = 0; $i -lt $steps.Count - 1 -and -not $stuckAt; $i++) {
                $name = $steps[$i]
                Click-PageText -Page $page -Pattern ('^\d*\s*' + [regex]::Escape($name)) -TimeoutSec 15 | Out-Null
                Start-Sleep -Seconds 1 # reason: the step draws and may ask the server; the page has no ready signal
                switch ($name) {
                    'Folders' {
                        Click-PageText -Page $page -Pattern '^Both' -TimeoutSec 10 | Out-Null
                        foreach ($pair in @(@('Media\Movies', $moviesRoot), @('Completed\films', $moviesDown), @('Media\TV', $tvRoot), @('Completed\shows', $tvDown))) {
                            $js = "(function(){var all=[].slice.call(document.querySelectorAll('input'));var i=all.findIndex(function(e){return (e.placeholder||'').indexOf(" + ($pair[0] | ConvertTo-Json) + ")>=0});return i;})()"
                            $idx = [int](Invoke-CdpEval -Page $page -Script $js)
                            if ($idx -ge 0) { Set-PageInputAt -Page $page -Selector 'input' -Index $idx -Value $pair[1] | Out-Null }
                        }
                    }
                    'Library Profiles' {
                        Click-PageText -Page $page -Pattern '^Balanced 1080p' -TimeoutSec 10 | Out-Null
                        Click-PageText -Page $page -Pattern '^Just make it work' -TimeoutSec 10 | Out-Null
                    }
                    'Connect installed apps' {
                        Click-PageText -Page $page -Pattern '^Connect all$' -TimeoutSec 20 | Out-Null
                        $connected = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { $t = Get-PageText -Page $page; if ($t -notmatch 'Connecting' -and $t -match 'Connected') { $t } else { $null } }
                        Save-Evidence -Name 'guide-connect-all.txt' -Text (Get-PageText -Page $page)
                        Click-PageText -Page $page -Pattern '^Connect Weir$' -TimeoutSec 20 | Out-Null
                        Start-Sleep -Seconds 1 # reason: the account panel opens; the page has no ready signal
                        Set-PageInputAt -Page $page -Selector '[aria-label="Your Weir account"] input' -Index 0 -Value (Get-SuiteSecret 'weir-username') | Out-Null
                        Set-PageInputAt -Page $page -Selector '[aria-label="Your Weir account"] input' -Index 1 -Value (Get-SuiteSecret 'weir-password') | Out-Null
                        Set-PageInputAt -Page $page -Selector '[aria-label="Your Weir account"] input' -Index 2 -Value (Get-SuiteSecret 'weir-password') | Out-Null
                        Click-PageText -Page $page -Pattern '^Create Weir account and connect$' -TimeoutSec 20 | Out-Null
                        $weirUp = Wait-Until -TimeoutSec 300 -IntervalSec 3 -Condition { $t = Get-PageText -Page $page; if ($t -match 'Weir' -and $t -notmatch 'Connecting to Weir' -and $t -notmatch 'Create Weir account and connect') { $t } else { $null } }
                        Save-Evidence -Name 'guide-connect-weir.txt' -Text (Get-PageText -Page $page)
                    }
                }
                foreach ($line in (Answer-StepQuestions -Page $page -YesStarts @('Movies: Clean files with Weir', 'TV: Clean files with Weir'))) { [void]$asked.Add("$name | $line") }
                if (-not (Press-Continue -Page $page)) {
                    $snap = Get-StepSnapshot -Page $page
                    Save-Evidence -Name "guide-stuck-$($i + 1).txt" -Text ($snap.text + "`n" + ($snap.controls -join "`n"))
                    $stuckAt = $name
                }
            }
            Add-Check 'Every step could be answered and left through Continue' ($null -eq $stuckAt) $(if ($stuckAt) { "Continue stayed disabled on '$stuckAt'" } else { 'all twelve steps' })
            Add-Note ("Yes/No answers given: " + ($asked -join '; '))
            if ($stuckAt) { return }

            # ---- Start: Create baseline
            foreach ($line in (Answer-StepQuestions -Page $page -YesStarts @('Movies: Clean files with Weir', 'TV: Clean files with Weir'))) { [void]$asked.Add("Start | $line") }
            Click-PageText -Page $page -Pattern '^Create baseline$' -TimeoutSec 20 | Out-Null
            $done = Wait-Until -TimeoutSec 240 -IntervalSec 3 -Condition { $t = Get-PageText -Page $page; if ($t -match 'Continue to Deluno') { $t } else { $null } }
            Save-Evidence -Name 'guide-finished.txt' -Text (Get-PageText -Page $page)
            Add-Check 'Create baseline finishes and offers "Continue to Deluno"' $done.Ok $(if ($done.Ok) { ($done.Value -replace '\s+', ' ').Substring(0, 200) } else { (Get-PageText -Page $page) -replace '\s+', ' ' })
        }
        finally { Close-CdpPage -Page $page }

        # ---- What the walk made, judged against the real apps
        Connect-Deluno
        $libs = @(Invoke-Deluno -Path '/api/libraries')
        $movies = $libs | Where-Object { $_.mediaType -eq 'movies' } | Select-Object -First 1
        $tv = $libs | Where-Object { $_.mediaType -eq 'tv' } | Select-Object -First 1
        Add-Check 'A Movies and a TV library exist, with the folders that were typed' ($movies -and $tv -and $movies.rootPath -eq $moviesRoot -and $tv.rootPath -eq $tvRoot) (($libs | ForEach-Object { "$($_.name): $($_.rootPath)" }) -join '; ')
        $clients = @(Get-DownloadClients)
        $kinds = @($clients | ForEach-Object { "$($_.protocol)" } | Sort-Object -Unique)
        Add-Check 'The five download clients the picker installed are connected and healthy' ((@('deluge', 'nzbget', 'qbittorrent', 'sabnzbd', 'transmission') | Where-Object { $_ -notin $kinds }).Count -eq 0 -and @($clients | Where-Object { $_.healthStatus -ne 'healthy' }).Count -eq 0) (($clients | ForEach-Object { "$($_.protocol)=$($_.healthStatus)" }) -join ', ')
        $conn = @(Invoke-Deluno -Path '/api/integrations/processors/connections') | Where-Object { $_.isEnabled -and $_.name -match 'weir' } | Select-Object -First 1
        Add-Check 'Weir is connected as a processor' ($null -ne $conn) ($conn | ConvertTo-Json -Compress)
        foreach ($lib in $movies, $tv) {
            if (-not $lib) { continue }
            $wf = Try-Deluno -Path "/api/libraries/$($lib.id)/workflow"
            Add-Check "The $($lib.name) library cleans with Weir before it imports (refine before import), with Weir's output folder set" ($wf.Status -eq 200 -and $wf.Data.importWorkflow -eq 'refine-before-import' -and $wf.Data.processorOutputPath) ($wf.Raw)
        }
        if ($conn) {
            $weirLibs = $null
            try { $weirLibs = Wait-Until -TimeoutSec 240 -IntervalSec 5 -Condition { $x = Invoke-Weir -Path '/processing/libraries'; $list = if ($x.PSObject.Properties.Name -contains 'value') { @($x.value) } else { @($x) }; $en = @($list | Where-Object { $_.enabled }); if ($en.Count -ge 2 -and @($en | Where-Object { -not $_.watched_folder }).Count -eq 0) { $en } else { $null } } } catch { }
            Add-Check "Weir's Movies and TV workflows are linked and each watches its folder" ($weirLibs -and $weirLibs.Ok) $(if ($weirLibs -and $weirLibs.Ok) { (@($weirLibs.Value) | ForEach-Object { "$($_.name): $($_.watched_folder)" }) -join '; ' } else { 'Weir listed no two watched workflows within 4 minutes' })
        }
        $health = Try-Deluno -Path '/api/system/health'
        Save-Evidence -Name 'guide-health.json' -Text $health.Raw
        $chain = @($health.Data.items) + @($health.Data) | Where-Object { $_ -and ("$($_.title) $($_.message) $($_.name)" -match 'category and save path|Folder chain') }
        Add-Check 'The folder chain is green: Health names no library whose client category and save path disagree' (@($chain).Count -eq 0) (($chain | ForEach-Object { $_.title }) -join '; ')
    }
}
