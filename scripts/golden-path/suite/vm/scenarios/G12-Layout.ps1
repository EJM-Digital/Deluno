<#
    The 390-pixel layout walk with a real folder-chain problem on screen (#1191, proof owed in #1196).

    layout.390-folder-chain: with a library whose download client's category folder does not agree with the library's downloads
    folder (a real problem Deluno finds by reading the client), every screen is opened at a phone's width (390 px, the same width
    the smoke walk uses, apps/web/tests/smoke/every-screen-fits-three-widths.spec.ts) in the VM's Edge, driven over DevTools with the
    window's size overridden. No visible browser on the workstation is involved: this runs in the VM.
      - no screen scrolls sideways, and no visible button, link or field sits outside the screen (outside a sideways scroller);
      - the folder-chain problem is raised by name: Health's row reads "The <library> library's category and save path", and a
        screen that lists problems shows that row with the library's name.
    The list of screens is the smoke walk's (every sidebar screen, every Settings page and area tab, the guided setup) and a film's
    page; Test-ScenarioSuite.ps1 compares it with the router so a new screen cannot be left out.
#>

# Every screen the smoke walk visits, as paths (apps/web/tests/helpers/route-lists.ts: sidebarRoutes + settingsRoutes).
function Get-NarrowWalkRoutes {
    @(
        '/', '/movies', '/tv', '/collections', '/calendar', '/discover', '/search', '/queue', '/activity', '/setup-guide',
        '/indexers/companion-tools', '/indexers/download-clients', '/indexers/history', '/indexers/indexers', '/indexers/library-routing',
        '/indexers/media-servers', '/indexers/path-mappings', '/indexers/proxies', '/indexers/scoreboard', '/indexers/subtitle-providers',
        '/search-cycles', '/search-cycles/blocklist', '/search-cycles/download-health', '/search-cycles/failed-downloads', '/search-cycles/keep-seeding',
        '/search-cycles/missing', '/search-cycles/retention', '/search-cycles/upgrades',
        '/settings', '/settings/auto-tagging', '/settings/custom-formats', '/settings/destination-rules', '/settings/general', '/settings/import-policy',
        '/settings/libraries', '/settings/lists', '/settings/media-management', '/settings/metadata', '/settings/migration', '/settings/notifications',
        '/settings/policy-sets', '/settings/processing', '/settings/profiles', '/settings/quality', '/settings/release-rules', '/settings/subtitles',
        '/settings/subtitles-wanted', '/settings/tags', '/settings/ui',
        '/system', '/system/api', '/system/audit', '/system/backups', '/system/docs', '/system/recycle-bin', '/system/security', '/system/updates'
    )
}

# Runs in the page: what is wrong with the layout right now, in words ('' when nothing). The same two rules the smoke walk starts from.
$script:NarrowMeasureJs = @'
(function(){
  var d = document.documentElement, w = d.clientWidth, found = [];
  if (d.scrollWidth > w + 1) found.push('the page scrolls sideways (' + d.scrollWidth + 'px in ' + w + 'px)');
  var els = document.querySelectorAll('button,a[href],input,select,textarea,[role=button],[role=tab]');
  var outside = [];
  for (var i = 0; i < els.length; i++) {
    var e = els[i], r = e.getBoundingClientRect();
    if (r.width <= 1 || r.height <= 1) continue;
    var cs = getComputedStyle(e);
    if (cs.visibility === 'hidden' || cs.display === 'none' || Number(cs.opacity) === 0) continue;
    if (e.closest('[aria-hidden=true],[inert],[hidden]')) continue;
    if (r.right <= w + 1 && r.left >= -1) continue;
    var p = e.parentElement, scroller = false;
    while (p && p !== document.body) {
      var o = getComputedStyle(p).overflowX;
      if ((o === 'auto' || o === 'scroll') && p.scrollWidth > p.clientWidth) { scroller = true; break; }
      p = p.parentElement;
    }
    if (!scroller) outside.push(((e.getAttribute('aria-label') || e.innerText || e.tagName) + '').trim().slice(0, 40));
  }
  if (outside.length) found.push(outside.length + ' control(s) outside the screen: ' + outside.slice(0, 4).join(' | '));
  return found.join('; ');
})()
'@

# Opens every route at 390 px wide and reports, for each, what is wrong with its layout and its text. Returns one object a route.
function Invoke-NarrowWalk {
    param([Parameter(Mandatory)] [string[]] $Routes, [Parameter(Mandatory)] $Login)
    Start-Edge
    $page = New-CdpPage 'about:blank'
    $out = New-Object System.Collections.ArrayList
    try {
        Send-Cdp -Page $page -Method 'Emulation.setDeviceMetricsOverride' -Params @{ width = 390; height = 844; deviceScaleFactor = 1; mobile = $false } | Out-Null
        Open-DelunoPage -Page $page -Path $Routes[0] -Login $Login
        $first = $true
        foreach ($route in $Routes) {
            if (-not $first) {
                Send-Cdp -Page $page -Method 'Page.navigate' -Params @{ url = "$($script:S.DelunoUrl)$route" } | Out-Null
                Start-Sleep -Milliseconds 1500
            }
            $first = $false
            # Shown when the screen has some text, then given a moment to lay out and load its rows.
            $ready = Wait-Until -TimeoutSec 20 -IntervalSec 0.5 -Condition { $t = Get-PageText -Page $page; if ($t.Trim().Length -gt 40) { $t } else { $null } }
            Start-Sleep -Milliseconds 1500
            $text = ''; $problem = ''; $width = 0
            try { $text = Get-PageText -Page $page; $problem = [string](Invoke-CdpEval -Page $page -Script $script:NarrowMeasureJs); $width = [int](Invoke-CdpEval -Page $page -Script 'document.documentElement.clientWidth') } catch { $problem = "the screen could not be read: $($_.Exception.Message)" }
            [void]$out.Add([pscustomobject]@{ Route = $route; Shown = $ready.Ok; Width = $width; Problem = $problem; Text = $text })
        }
    }
    finally { Close-CdpPage -Page $page }
    @($out)
}

function Invoke-GroupLayout {
    Invoke-Scenario -Id 'layout.390-folder-chain' -Title 'Every screen fits at 390 px wide while a library has a folder-chain problem, and the problem names its library' -Body {
        $token = New-FilmToken
        $name = "Narrow walk $token"
        $dirs = @(('C:\Media\NarrowWalk' + $token), ('C:\Downloads\Completed\NarrowWalk' + $token))
        New-Item -ItemType Directory -Force -Path $dirs | Out-Null
        $libId = $null
        try {
            # ---------------------------------------------------------- a real folder-chain problem
            $profile = (Get-Library -MediaType 'movies').qualityProfileId
            $lib = Invoke-Deluno -Method POST -Path '/api/libraries' -Body @{
                name = $name; mediaType = 'movies'; purpose = 'Main library'; rootPath = $dirs[0]; downloadsPath = $dirs[1]
                qualityProfileId = $profile; autoSearchEnabled = $false; missingSearchEnabled = $false; upgradeSearchEnabled = $false
                removeEmptySourceFolders = $false; searchIntervalMinutes = 720; retryDelayHours = 6; maxItemsPerRun = 10
            }
            $libId = $lib.id
            # Routed to qBittorrent, whose category folder for movies is the Movies library's folder, not this library's.
            $client = Get-ClientByKind -Kind 'qbittorrent'
            $sources = @()
            foreach ($id in @($script:Fx.IndexerIds.torrent, $script:Fx.IndexerIds.usenet, $script:Fx.IndexerIds.private) | Where-Object { $_ }) { $sources += @{ indexerId = $id; priority = ($sources.Count + 1); requiredTags = ''; excludedTags = '' } }
            Invoke-Deluno -Method PUT -Path "/api/libraries/$libId/routing" -Body @{ sources = $sources; downloadClients = @(@{ downloadClientId = $client.id; priority = 1 }) } | Out-Null
            $chain = Try-Deluno -Path "/api/libraries/$libId/folder-chain" -TimeoutSec 120
            Save-Evidence -Name 'layout-folder-chain.json' -Text $chain.Raw
            $bad = @($chain.Data.clients | ForEach-Object { $_.links } | Where-Object { $_.status -eq 'problem' })
            Add-Check 'The library has a real folder-chain problem (the client''s folder does not agree with the library''s downloads folder)' ($chain.Status -eq 200 -and $chain.Data.allMatch -eq $false -and $bad.Count -gt 0) $(if ($bad.Count) { $bad[0].message } else { "HTTP $($chain.Status), allMatch $($chain.Data.allMatch); nothing was marked as a problem" })
            if ($bad.Count -eq 0) { return }

            # Health raises it by name: the check runs again at once ("Check now").
            Try-Deluno -Method POST -Path '/api/system/health/run' | Out-Null
            $raised = Wait-Until -TimeoutSec 240 -IntervalSec 5 -Condition {
                $h = Try-Deluno -Path '/api/system/health'
                $c = @($h.Data.checks | Where-Object { $_.id -eq "library:${libId}:folder-chain" -and $_.status -eq 'problem' })
                if ($c.Count -gt 0) { $c[0] } else { $null }
            }
            Add-Check 'Health raises the folder-chain problem for the library, and its row names the library' ($raised.Ok -and $raised.Value.name -like "*$name*") $(if ($raised.Ok) { "$($raised.Value.name): $($raised.Value.message)" } else { 'no folder-chain problem for the library in 4 minutes' })
            if ($raised.Ok) { Add-Check 'Its cause and its fix name the library too' (([string]$raised.Value.cause) -like "*$name*" -and ([string]$raised.Value.fix) -like "*$name*") "cause: $($raised.Value.cause) | fix: $($raised.Value.fix)" }

            # ---------------------------------------------------------- the walk at 390 px
            $routes = @(Get-NarrowWalkRoutes)
            $mv = Try-Deluno -Path '/api/movies'
            $movie = @(if ($mv.Data -and $mv.Data.PSObject.Properties.Name -contains 'items') { $mv.Data.items } else { $mv.Data }) | Where-Object { $_ -and $_.id } | Select-Object -First 1
            if ($movie -and $movie.id) { $routes += "/movies/$($movie.id)" }
            $walk = @(Invoke-NarrowWalk -Routes $routes -Login $script:S.Login)
            Save-Evidence -Name 'layout-390-walk.json' -Text (($walk | ForEach-Object { [pscustomobject]@{ route = $_.Route; shown = $_.Shown; width = $_.Width; problem = $_.Problem } }) | ConvertTo-Json -Depth 3)
            $notShown = @($walk | Where-Object { -not $_.Shown })
            Add-Check "Every screen opened ($($walk.Count) screens) and showed something" ($notShown.Count -eq 0) (($notShown | ForEach-Object { $_.Route }) -join ', ')
            $wrongWidth = @($walk | Where-Object { $_.Shown -and $_.Width -ne 390 -and $_.Width -ne 0 })
            Add-Check 'Every screen was measured at 390 px wide' ($wrongWidth.Count -eq 0) (($wrongWidth | ForEach-Object { "$($_.Route) was $($_.Width)" }) -join ', ')
            $misfit = @($walk | Where-Object { $_.Problem })
            Add-Check 'No screen scrolls sideways or has a control outside the screen' ($misfit.Count -eq 0) (($misfit | ForEach-Object { "$($_.Route): $($_.Problem)" }) -join ' || ')

            # ---------------------------------------------------------- the problem, on a screen, naming the library
            $pattern = '(?i)' + [regex]::Escape($name) + '.{0,80}category and save path|category and save path.{0,200}' + [regex]::Escape($name)
            $naming = @($walk | Where-Object { $_.Text -match $pattern })
            Add-Check 'At 390 px, a screen shows the folder-chain problem with the library''s name' ($naming.Count -gt 0) $(if ($naming.Count) { 'on ' + (($naming | ForEach-Object { $_.Route }) -join ', ') } else { "no screen's text names '$name' beside the folder-chain problem" })
            $dashOrSystem = @($walk | Where-Object { $_.Route -in '/', '/system' -and $_.Text -match [regex]::Escape($name) })
            Add-Check 'The Dashboard or System names the library in its problems' ($dashOrSystem.Count -gt 0) $(if ($dashOrSystem.Count) { 'on ' + (($dashOrSystem | ForEach-Object { $_.Route }) -join ', ') } else { 'neither screen names it' })
        }
        finally {
            if ($libId) { Try-Deluno -Method DELETE -Path "/api/libraries/$libId" | Out-Null }
        }
    }
}
