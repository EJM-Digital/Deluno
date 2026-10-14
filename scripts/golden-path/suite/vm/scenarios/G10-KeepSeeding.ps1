<#
    A struck torrent from a private tracker moves to a keep-seeding category instead of being deleted (#1240).

    Every torrent client Deluno can move a download on is covered: qBittorrent (setCategory), Deluge (label.set_torrent, Label plugin
    on) and Transmission (labels). rTorrent and uTorrent are not installed in the VM, so they are not here.

    For each client, two torrents are sent from the local Torznab feed, each stuck just short of the end (the "short" variant G05 uses, so
    its data is on disk) and struck by "No progress" at two checks in a row:
      - one from "Golden private feed", an indexer the suite adds and marks private; the rule says "Move it to the keep-seeding category
        and look for another copy". Checked: Activity says it was moved, to which category, on which client, and that Deluno is looking
        for another copy; the client still holds it, now in the keep-seeding category (read from the client itself, not from Deluno);
        its data is still on disk; it is gone from Transfers and stays gone through another check; nothing was removed; no problem
        is raised.
      - one from the public "Golden feed", struck by the same rule: removed from the client and replaced, exactly as before.

    The group is self-contained. It adds the private indexer and the keep-seeding category in each client, puts the rule and the
    keep-seeding choices back at the end through Deluno's own API, and deletes the private indexer through Deluno. The category it
    makes in qBittorrent and Deluge stays in the client (Deluno never deletes a category in a client); it is empty, and the VM is
    restored for the next run.

    The clients' own logins are read the way the installer saved them (qBittorrent has a generated one, Transmission and Deluge keep
    their shipped ones; build-rules.md) and are masked in everything the suite writes.
#>

# The clients' own sessions and calls (Open-KsQbit, Open-KsDeluge, Invoke-KsDeluge, Invoke-KsTransmission, the saved logins) live in
# Fixtures.Lib.ps1, so a group that is chosen alone never depends on this one. $script:Ks (the keep-seeding names) is defined there too.

# Makes the keep-seeding category in the client, as a person does in the client first (Deluno never creates one). Transmission's
# labels need no making.
function New-KsClientCategory {
    param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [string] $Name)
    switch ($Kind) {
        'qbittorrent' {
            $q = Open-KsQbit
            try { Invoke-WebRequest -Uri "$($q.Base)/api/v2/torrents/createCategory" -Method Post -Body @{ category = $Name; savePath = '' } -Headers @{ Referer = $q.Base } -WebSession $q.Session -UseBasicParsing -TimeoutSec 20 | Out-Null }
            catch {
                # 409 is "Unable to create category": it is already there from an earlier run.
                if (-not ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 409)) { throw }
            }
        }
        'deluge' {
            $d = Open-KsDeluge
            $r = Invoke-KsDeluge -Session $d -Method 'label.add' -Params @($Name) -Subject "label $Name"
            if ($r.error -and "$($r.error.message)" -notmatch 'already exists') { throw "Deluge would not make the label: $($r.error.message)" }
        }
    }
}

# What the client itself says about one torrent: whether it holds it, and its category or label. Never asks Deluno.
function Get-KsClientTorrent {
    param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [string] $Hash)
    switch ($Kind) {
        'qbittorrent' {
            $q = Open-KsQbit
            $rows = @(Invoke-RestMethod -Uri "$($q.Base)/api/v2/torrents/info?hashes=$Hash" -Headers @{ Referer = $q.Base } -WebSession $q.Session -UseBasicParsing -TimeoutSec 20)
            $row = $rows | Where-Object { $_ } | Select-Object -First 1
            [pscustomobject]@{ Present = [bool]$row; Category = $(if ($row) { [string]$row.category } else { $null }); State = $(if ($row) { [string]$row.state } else { $null }) }
        }
        'deluge' {
            $d = Open-KsDeluge
            $r = Invoke-KsDeluge -Session $d -Method 'core.get_torrent_status' -Params @($Hash, @('label', 'state', 'name')) -Subject "hash $Hash"
            $s = $r.result
            $has = $s -and @($s.PSObject.Properties).Count -gt 0
            [pscustomobject]@{ Present = [bool]$has; Category = $(if ($has) { [string]$s.label } else { $null }); State = $(if ($has) { [string]$s.state } else { $null }) }
        }
        'transmission' {
            $r = Invoke-KsTransmission -Method 'torrent-get' -Arguments @{ ids = @($Hash); fields = @('hashString', 'labels', 'name', 'status') }
            $t = @($r.arguments.torrents) | Where-Object { $_ } | Select-Object -First 1
            [pscustomobject]@{ Present = [bool]$t; Category = $(if ($t) { (@($t.labels) -join ',') } else { $null }); State = $(if ($t) { [string]$t.status } else { $null }) }
        }
    }
}

# The torrent's files under the clients' folders (by release name), with their sizes.
function Get-KsFilesOnDisk {
    param([Parameter(Mandatory)] [string] $Release)
    @(Get-ChildItem -Path 'C:\Downloads' -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$Release*" -or $_.DirectoryName -like "*$Release*" })
}

# ---------------------------------------------------------------- reading Activity

# The "Moved X to Y on Z so it keeps seeding on T, and is looking for another copy" line for a release, as its message text.
function Get-KsMovedMessage {
    param([Parameter(Mandatory)] $Film, [string[]] $Lines)
    foreach ($line in $Lines) {
        if ($line -notmatch [regex]::Escape($Film.Release) -or $line -notmatch 'so it keeps seeding') { continue }
        try { $message = [string](($line | ConvertFrom-Json).message) } catch { $message = $line }
        if ($message -match '^Moved ') { return $message }
    }
    $null
}

# The strike lines Deluno wrote for one release: Activity rows of category download.health.remediated whose details name it.
# Every strike line carries the label "Fixed a stuck download", the move included, so the label never tells a move from a
# removal; the details' actions do ("moved to <category>" or "client entry removed ...").
function Get-KsStrikeLines {
    param([Parameter(Mandatory)] $Film, [string[]] $Lines)
    foreach ($line in $Lines) {
        if ($line -notmatch [regex]::Escape($Film.Release)) { continue }
        $row = try { $line | ConvertFrom-Json } catch { $null }
        if (-not $row -or $row.category -ne 'download.health.remediated') { continue }
        $details = try { [string]$row.detailsJson | ConvertFrom-Json } catch { $null }
        if (-not $details -or -not ([string]$details.releaseName).StartsWith($Film.Release, [StringComparison]::OrdinalIgnoreCase)) { continue }
        [pscustomobject]@{ Message = [string]$row.message; Actions = @($details.actions | ForEach-Object { [string]$_ }); MovedTo = [string]$details.movedTo }
    }
}

# Whether a strike removed the release from the client (the public torrent's story, and what a private torrent must never have).
function Get-KsRemovedMessage {
    param([Parameter(Mandatory)] $Film, [string[]] $Lines)
    foreach ($strike in @(Get-KsStrikeLines -Film $Film -Lines $Lines)) {
        if (@($strike.Actions | Where-Object { $_ -match 'removed' }).Count -gt 0) { return $strike.Message }
    }
    $null
}

# Waits until every film has been dealt with by a strike: the private ones moved, the public ones removed (or the time runs out).
function Wait-KsAllStruck {
    param([Parameter(Mandatory)] [hashtable] $Films, [int] $TimeoutSec = 480)
    $seen = @{}
    foreach ($k in $Films.Keys) { $seen[$k] = @{ Message = $null; Seconds = $null } }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $lines = @(Get-ActivityText -PageSize 300 -MaxPages 1)
        foreach ($k in $Films.Keys) {
            if ($seen[$k].Message) { continue }
            $f = $Films[$k]
            $m = if ($k -like 'private.*') { Get-KsMovedMessage -Film $f -Lines $lines } else { Get-KsRemovedMessage -Film $f -Lines $lines }
            if ($m) { $seen[$k].Message = $m; $seen[$k].Seconds = [math]::Round(([DateTime]::UtcNow - $f.SentUtc).TotalSeconds, 0) }
        }
        if (@($seen.Values | Where-Object { -not $_.Message }).Count -eq 0) { break }
        Start-Sleep -Seconds 4
    }
    $seen
}

# ---------------------------------------------------------------- the group

function Invoke-GroupKeepSeeding {
    # A run of chosen scenarios (-Scenarios) sets up only what they need: a film for each chosen client and kind, the private
    # indexer and the keep-seeding choices only when a "moved" scenario is chosen, the rule when any is.
    $movedKinds = @(Get-SelectedKinds -IdFormat 'keepseeding.moved.{0}' -Kinds $script:Kinds.torrent)
    $publicKinds = @(Get-SelectedKinds -IdFormat 'keepseeding.public-removed.{0}' -Kinds $script:Kinds.torrent)
    if ($movedKinds.Count + $publicKinds.Count -eq 0) { return }
    $category = $script:Ks.Category
    $restore = @{ Rules = @(Invoke-Deluno -Path '/api/download-health/rules'); Settings = $null; ChoicesChanged = $false; PrivateIndexerId = $null }
    try {
        # ---------------- setup: a private indexer, the category in each client, Deluno told about it, and the rule
        $ready = @{}
        $private = $null
        if ($movedKinds.Count -gt 0) {
            $view = Invoke-Deluno -Path '/api/download-health/keep-seeding'
            $restore.Settings = $view
            $private = Invoke-Deluno -Method POST -Path '/api/indexers' -Body @{
                name = $script:Ks.PrivateName; protocol = 'torznab'; privacy = 'private'; baseUrl = "http://127.0.0.1:$($script:Fx.FeedPort)/private"; apiKey = 'golden'
                priority = 26; categories = '2000,2040,5000,5040'; mediaScope = 'both'; isEnabled = $true; keepName = $true; routeToLibraries = $true
                # Nothing automatic (#936): the suite sends each release itself.
                rssEnabled = $false; automaticSearchEnabled = $false
            }
            $restore.PrivateIndexerId = $private.id
            $script:Fx.IndexerIds.private = $private.id
            Add-Note ("Private indexer {0} added; Deluno reads it as private: {1}" -f $script:Ks.PrivateName, $private.privacy)

            $clientCategories = @{}
            foreach ($kind in $movedKinds) {
                New-KsClientCategory -Kind $kind -Name $category
                $clientCategories[(Get-ClientByKind -Kind $kind).id] = $category
            }
            $restore.ChoicesChanged = $true
            $saved = Invoke-Deluno -Method PUT -Path '/api/download-health/keep-seeding' -Body @{ clientCategories = $clientCategories; indexers = @{}; tagCategories = @{} }
            foreach ($kind in $movedKinds) {
                $c = Get-ClientByKind -Kind $kind
                $ready[$kind] = Invoke-Deluno -Method POST -Path '/api/download-health/keep-seeding/check' -Body @{ clientId = $c.id; category = $category }
            }
        }

        Set-HealthRules -On @{ 'no-progress' = @{ Strikes = 2 } } -CheckMinutes 1
        $rule = @(Invoke-Deluno -Path '/api/download-health/rules') | Where-Object { $_.kind -eq 'no-progress' } | Select-Object -First 1
        $body = @{}
        foreach ($p in $rule.PSObject.Properties) { $body[$p.Name] = $p.Value }
        $body.privateAction = 'move-and-replace'
        $body.blocklistWhenMoved = $true
        Invoke-Deluno -Method PUT -Path "/api/download-health/rules/$($rule.id)" -Body $body | Out-Null

        # ---------------- send both kinds of torrent to each client
        $films = @{}
        foreach ($kind in $movedKinds) {
            $pf = New-FilmFor -Kind $kind -Layout 'file' -Variant 'short' -Label 'keepprivate'
            $pf | Add-Member -NotePropertyName IndexerId -NotePropertyValue $private.id -Force
            $pf | Add-Member -NotePropertyName IndexerName -NotePropertyValue $script:Ks.PrivateName -Force
            $films["private.$kind"] = Send-Film -Film $pf -Kind $kind
        }
        foreach ($kind in $publicKinds) {
            $films["public.$kind"] = Send-Film -Film (New-FilmFor -Kind $kind -Layout 'file' -Variant 'short' -Label 'keeppublic') -Kind $kind
        }
        $seen = Wait-KsAllStruck -Films $films
        # One more check interval, so "stays gone" and "is not struck again" are read after a further pass, not at the moment of the move.
        Start-Sleep -Seconds 75
        $lines = @(Get-ActivityText -PageSize 500 -MaxPages 2)
        $afterView = Invoke-Deluno -Path '/api/download-health/keep-seeding'

        foreach ($kind in $script:Kinds.torrent) {
            $client = Get-ClientByKind -Kind $kind
            $pf = $films["private.$kind"]
            $pub = $films["public.$kind"]

            Invoke-Scenario -Id "keepseeding.moved.$kind" -Title "A struck $kind torrent from a private tracker is moved to the keep-seeding category, not deleted" -Body {
                Add-Check "Deluno sees the keep-seeding category ready in $kind" ($ready[$kind].status -eq 'ready') ("status '{0}': {1}" -f $ready[$kind].status, $ready[$kind].message)
                $message = $seen["private.$kind"].Message
                Add-Check 'Activity says it was moved' ([bool]$message) $(if ($message) { "after $($seen["private.$kind"].Seconds) s" } else { 'no "Moved ... so it keeps seeding" line in Activity' })
                if ($message) {
                    Save-Evidence -Name "keepseeding-moved-$kind.txt" -Text $message
                    Add-Check 'The line names the release, the category and the client' ($message -match [regex]::Escape($pf.Release) -and $message -match [regex]::Escape(" to $category on ") -and $message -match [regex]::Escape(" on $($client.name) ")) $message
                    Add-Check 'The line names the private tracker' ($message -match [regex]::Escape($script:Ks.PrivateName)) $message
                    Add-Check 'Activity says plainly that Deluno is looking for another copy' ($message -match 'looking for another copy') $message
                    Add-Check 'The line gives the strike rule''s own reason (No progress)' ($message -match 'No progress|made no progress') $message
                }
                Add-Check 'Activity never says it was removed from the client' (-not (Get-KsRemovedMessage -Film $pf -Lines $lines)) ''

                $held = Get-KsClientTorrent -Kind $kind -Hash $pf.Hash
                Add-Check "$kind still holds the torrent (it was not deleted)" $held.Present ("state '{0}'" -f $held.State)
                Add-Check "$kind reports it in the keep-seeding category ($category)" ($held.Present -and $held.Category -ieq $category) ("category/label '{0}'" -f $held.Category)

                $files = Get-KsFilesOnDisk -Release $pf.Release
                Add-Check 'Its data is still on disk' (@($files | Where-Object { $_.Length -gt 0 }).Count -gt 0) (($files | Select-Object -First 3 | ForEach-Object { '{0} ({1} bytes)' -f $_.FullName, $_.Length }) -join '; ')

                $row = Find-QueueRow -Name $pf.Release
                Add-Check 'It is gone from Transfers, and stayed gone through another check' (-not $row) $(if ($row) { "still listed as '$($row.status)'" } else { '' })
                $strikes = @(Get-KsStrikeLines -Film $pf -Lines $lines)
                Add-Check 'It was not struck again after the move (one strike line, the move)' ($strikes.Count -eq 1) ("{0} strike line(s): {1}" -f $strikes.Count, (($strikes | ForEach-Object { $_.Actions -join '+' }) -join ' | '))
                $problems = @($afterView.problems | Where-Object { $_.releaseName -like "*$($pf.Title)*" })
                Add-Check 'No keep-seeding problem is raised for it' ($problems.Count -eq 0) (($problems | ForEach-Object { $_.message }) -join '; ')
            }

            Invoke-Scenario -Id "keepseeding.public-removed.$kind" -Title "A struck $kind torrent from a public tracker is removed and replaced, as before" -Body {
                $message = $seen["public.$kind"].Message
                Add-Check 'Activity says it was struck and replaced' ([bool]$message) $(if ($message) { "after $($seen["public.$kind"].Seconds) s" } else { 'no "Fixed a stuck download" line in Activity' })
                if ($message) {
                    Save-Evidence -Name "keepseeding-public-$kind.txt" -Text $message
                    Add-Check 'Deluno removed it from the client and looked for another copy' ($message -match 'looking for another copy' -and $message -match 'removed it from') $message
                    Add-Check 'It was not moved to the keep-seeding category' (-not (Get-KsMovedMessage -Film $pub -Lines $lines)) ''
                }
                $gone = Wait-Until -TimeoutSec 60 -IntervalSec 3 -Condition { -not (Get-KsClientTorrent -Kind $kind -Hash $pub.Hash).Present }
                Add-Check "$kind no longer holds it" $gone.Ok ''
            }
        }
    }
    finally {
        # Put everything back through Deluno's own API: the rules, the keep-seeding choices, and the private indexer.
        foreach ($rule in $restore.Rules) {
            try { Invoke-Deluno -Method PUT -Path "/api/download-health/rules/$($rule.id)" -Body $rule | Out-Null } catch { Write-SuiteLog "   could not restore rule $($rule.kind): $(Hide-Secrets $_.Exception.Message)" }
        }
        if ($restore.ChoicesChanged) { try {
            $before = @{}
            if ($restore.Settings) { foreach ($c in @($restore.Settings.clients)) { if ($c.category) { $before[$c.clientId] = $c.category } } }
            Invoke-Deluno -Method PUT -Path '/api/download-health/keep-seeding' -Body @{ clientCategories = $before; indexers = @{}; tagCategories = @{} } | Out-Null
        } catch { Write-SuiteLog "   could not restore the keep-seeding choices: $(Hide-Secrets $_.Exception.Message)" } }
        if ($restore.PrivateIndexerId) {
            try { Invoke-Deluno -Method DELETE -Path "/api/indexers/$($restore.PrivateIndexerId)" | Out-Null } catch { Write-SuiteLog "   could not delete the private indexer: $(Hide-Secrets $_.Exception.Message)" }
        }
        $script:Fx.IndexerIds.Remove('private')
    }
}
