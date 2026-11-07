<#
    Four fixes proved live on the golden VM, each runnable alone (-Scenarios <id>). They use the helpers of the volumes group
    (G20-Volumes.ps1: New-VolumeLibrary, New-VolumeSource, Invoke-VolumeImport, ...), so that file loads first.

      proofs2.import-source-message   #1510. A manual import (POST /api/filesystem/import/execute-batch) of a real file by move, copy
                                      and hard link: the reply's message says what happened to the source, read against the disk.
                                      A move leaves nothing at the source and says moved; a hard link leaves the source and says it
                                      is kept; a copy's message agrees with whether the source is still there.
      proofs2.library-prefix-credit   #1513. Two libraries whose roots differ only by a trailing "2" (...\Pfx and ...\Pfx2): a film
                                      imported into the second is credited to it, not to the first; one imported into the first is
                                      still credited to the first. The scratch libraries and their files are removed afterwards.
      proofs2.client-refusal-on-row   #1470. The real qBittorrent is made to refuse the save folder Deluno sends after a grab (a
                                      drive that does not exist): the grab stands, the download's row carries the refusal with its
                                      fix, and Activity says it in a sentence. The client's folder is put back afterwards.
      proofs2.seerr-switch-refused    #1474. A Seerr connection pointing at a closed port is switched on and off from the screen: when
                                      the server refuses the request the switch says why and keeps the state the server holds, and
                                      when Deluno cannot be reached the message is "Deluno could not be reached", never "Failed to
                                      fetch". The connection is removed afterwards.
#>

# The film at the destination is looked up in the catalogue by title (a list may be paged under items).
function Find-CatalogMovie {
    param([Parameter(Mandatory)] [string] $Title)
    $mv = Try-Deluno -Path '/api/movies' -TimeoutSec 120
    @(if ($mv.Data -and $mv.Data.PSObject.Properties.Name -contains 'items') { $mv.Data.items } else { $mv.Data }) |
        Where-Object { $_ -and $_.title -eq $Title } | Select-Object -First 1
}

# What the connection's On switch shows on the open page: "true", "false", or "none" when it is not there.
function Get-SeerrSwitchState {
    param($Page, [Parameter(Mandatory)] [string] $Name)
    $js = '(function(){var e=document.querySelector(''[aria-label$=" ' + $Name.Replace("'", "'") + '"][role=switch]'');return e?e.getAttribute("aria-checked"):"none";})()'
    [string](Invoke-CdpEval -Page $Page -Script $js)
}

function Invoke-GroupProofs2 {
    # ------------------------------------------------------------------------------------------------ #1510
    Invoke-Scenario -Id 'proofs2.import-source-message' -Title 'A manual import by move, copy and hard link says what became of the source, and the source on disk agrees' -Body {
        $token = New-FilmToken
        $srcRoot = Join-Path $script:VolDir "src_Msg$token"
        $libRoot = "C:\Media\Msg$token"
        $lib = $null; $movies = @()
        try {
            $lib = New-VolumeLibrary -Name "Message $token" -Root $libRoot
            foreach ($mode in @('move', 'copy', 'hardlink')) {
                $title = "Msg $mode $token"
                $src = New-VolumeSource -Folder (Join-Path $srcRoot $mode) -Title $title
                $r = Invoke-VolumeImport -Source $src -Library $lib -Title $title -Mode $mode -Fallback $false
                Save-Evidence -Name "proofs2-import-message-$mode.json" -Text $r.Raw
                $sourceThere = Test-Path -LiteralPath $src
                $inLibrary = Find-VolumeFilm -Root $libRoot -Title $title
                if ($inLibrary) { $m = Find-CatalogMovie -Title $title; if ($m) { $movies += $m } }
                $msg = $r.Message
                Add-Check "$mode : the import succeeded, used $mode and the film is in the library" ($r.Succeeded -and $null -ne $inLibrary -and "$($r.Response.transferModeUsed)" -eq $mode) "succeeded $($r.Succeeded), mode used '$($r.Response.transferModeUsed)', file $($inLibrary.FullName)"
                switch ($mode) {
                    'move' {
                        Add-Check 'move : the source is gone from the disk' (-not $sourceThere) "source present: $sourceThere"
                        Add-Check 'move : the message says it was moved and does not say the source was kept' ($msg -match '(?i)moved' -and $msg -notmatch '(?i)\bkept\b') $msg
                    }
                    'hardlink' {
                        Add-Check 'hardlink : the source is still on the disk' $sourceThere "source present: $sourceThere"
                        Add-Check 'hardlink : the message says hardlinked and that the source is kept' ($msg -match '(?i)hard-?link' -and $msg -match '(?i)\bkept\b') $msg
                    }
                    'copy' {
                        # Whether a copy's source stays is the library's cleanup rule; the message must say whichever is true.
                        $agrees = if ($sourceThere) { $msg -match '(?i)\bkept\b' -and $msg -notmatch '(?i)\b(moved|removed)\b' } else { $msg -match '(?i)\b(removed|moved|gone)\b' -and $msg -notmatch '(?i)\bkept\b' }
                        Add-Check 'copy : the message agrees with whether the source is still on the disk' $agrees "source present: $sourceThere; message: $msg"
                    }
                }
            }
        }
        finally {
            foreach ($m in $movies) { Try-Deluno -Method DELETE -Path "/api/movies/$($m.id)?deleteFiles=true" -TimeoutSec 120 | Out-Null }
            Remove-VolumeLibrary -Library $lib
            Remove-Item -LiteralPath $srcRoot, $libRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ------------------------------------------------------------------------------------------------ #1513
    Invoke-Scenario -Id 'proofs2.library-prefix-credit' -Title 'A film imported into the library whose root is another root plus "2" is credited to that library, not the shorter one' -Body {
        $token = New-FilmToken
        $rootA = "C:\Media\Pfx$token"
        $rootB = "C:\Media\Pfx${token}2"
        $srcRoot = Join-Path $script:VolDir "src_Pfx$token"
        $libA = $null; $libB = $null; $movies = @()
        try {
            $libA = New-VolumeLibrary -Name "Prefix $token" -Root $rootA
            $libB = New-VolumeLibrary -Name "Prefix $token two" -Root $rootB
            foreach ($case in @(@{ Lib = $libB; Root = $rootB; Name = 'two' }, @{ Lib = $libA; Root = $rootA; Name = 'one' })) {
                $title = "Pfx $($case.Name) $token"
                # The film is in the catalogue first, as a person adds it: an import of a title Deluno has not got is held for a
                # choice (#1498), which left no row to read a library from.
                $made = New-VolumeMovie -Title $title -Library $case.Lib
                if ($made) { $movies += $made }
                $src = New-VolumeSource -Folder (Join-Path $srcRoot $case.Name) -Title $title
                $r = Invoke-VolumeImport -Source $src -Library $case.Lib -Title $title -Mode 'copy' -Fallback $true
                Save-Evidence -Name "proofs2-library-prefix-$($case.Name).json" -Text $r.Raw
                $file = Find-VolumeFilm -Root $case.Root -Title $title
                Add-Check "The film for '$($case.Name)' landed under $($case.Root)" ($r.Succeeded -and $null -ne $file) $r.Message
                $m = Find-CatalogMovie -Title $title
                $credited = if ($m) { Try-Deluno -Path "/api/movies/$($m.id)" } else { $null }
                $libId = if ($credited -and $credited.Data) { [string]$credited.Data.libraryId } else { '' }
                Add-Check "The film for '$($case.Name)' is credited to its own library ($($case.Lib.name)), not the other one" ($libId -eq $case.Lib.id) "title's library '$libId', expected '$($case.Lib.id)'"
            }
        }
        finally {
            foreach ($m in $movies) { Try-Deluno -Method DELETE -Path "/api/movies/$($m.id)?deleteFiles=true" -TimeoutSec 120 | Out-Null }
            Remove-VolumeLibrary -Library $libB
            Remove-VolumeLibrary -Library $libA
            Remove-Item -LiteralPath $srcRoot, $rootA, $rootB -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ------------------------------------------------------------------------------------------------ #1470
    Invoke-Scenario -Id 'proofs2.client-refusal-on-row' -Title 'qBittorrent refuses the save folder Deluno sends after a grab: the grab stands, the download''s row shows the refusal with its fix, and Activity says it in a sentence' -Body {
        $film = $null; $client = $null; $before = $null
        try {
            $client = Get-ClientByKind -Kind 'qbittorrent'
            # There is no single-client read; the list carries each client with its folder.
            $before = Get-DownloadClients | Where-Object { $_.id -eq $client.id } | Select-Object -First 1
            # A drive letter nothing holds: qBittorrent answers setLocation with "Cannot make save path" (recorded, 5.2.1).
            $letter = @('Z', 'Y', 'X', 'W', 'U') | Where-Object { -not (Test-Path "${_}:\") } | Select-Object -First 1
            if (-not $letter) { throw 'Every spare drive letter is in use; the scenario needs one that does not exist.' }
            $badFolder = "${letter}:\no-such-drive\golden-movies"
            $set = Try-Deluno -Method PUT -Path "/api/download-clients/$($client.id)" -Body @{ location = $badFolder }
            Add-Check 'The client''s save folder is set to a drive that does not exist' ($set.Status -in 200, 204) "HTTP $($set.Status) $($set.Raw)"

            $film = New-FilmFor -Kind 'qbittorrent' -Label 'refusedfolder'
            $film = Send-Film -Film $film -Kind 'qbittorrent'
            $row = Wait-Until -TimeoutSec 180 -IntervalSec 3 -Condition {
                $q = Find-QueueRow -Name $film.Release
                if ($q -and @($q.healthFindings | Where-Object { $_.kind -eq 'follow-up-refused' }).Count -gt 0) { $q } else { $null }
            }
            $q = if ($row.Ok) { $row.Value } else { Find-QueueRow -Name $film.Release }
            Save-Evidence -Name 'proofs2-client-refusal-row.json' -Text ($q | ConvertTo-Json -Depth 8)
            Add-Check 'The grab stands: the client holds the release' ($null -ne $q) 'no queue row for the release'
            $finding = @($q.healthFindings | Where-Object { $_.kind -eq 'follow-up-refused' }) | Select-Object -First 1
            Add-Check 'The download''s row carries the refusal, in the client''s words' ($null -ne $finding -and $finding.summary -match '(?i)did not take the save folder' -and $finding.summary -match '(?i)Cannot make save path') $(if ($finding) { $finding.summary } else { 'no follow-up-refused finding on the row' })
            Add-Check 'The row says what to do about it' ($null -ne $finding -and $finding.recommendedAction -match '(?i)folder' -and $finding.recommendedAction -match '(?i)send it again') $(if ($finding) { $finding.recommendedAction } else { '' })
            $said = Wait-Until -TimeoutSec 120 -IntervalSec 3 -Condition {
                $hit = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Release) -and $_ -match '(?i)did not take the save folder' -and $_ -match 'Cannot make save path' })
                if ($hit.Count -gt 0) { $hit } else { $null }
            }
            Add-Check 'Activity says it in a sentence (the client did not take the save folder for the release, and why)' $said.Ok $(if ($said.Ok) { ($said.Value | Select-Object -First 1) } else { 'no such Activity entry' })
        }
        finally {
            # The folder is put back as it was, and the release is taken off the client and the title off Deluno.
            if ($client -and $before) {
                $restore = if ($before.location) { @{ location = [string]$before.location } } else { @{ clearLocation = $true } }
                Try-Deluno -Method PUT -Path "/api/download-clients/$($client.id)" -Body $restore | Out-Null
            }
            if ($film) {
                try { Remove-FilmDownload -Film $film -Reason 'unwanted' | Out-Null } catch { }
                if ($film.MovieId) { Try-Deluno -Method DELETE -Path "/api/movies/$($film.MovieId)" | Out-Null }
            }
        }
    }

    # ------------------------------------------------------------------------------------------------ #1474
    Invoke-Scenario -Id 'proofs2.seerr-switch-refused' -Title 'Switching a Seerr connection off and on says why when the request is refused or Deluno cannot be reached, never "Failed to fetch"' -Body {
        $token = New-FilmToken
        $name = "Golden Seerr $token"
        $id = $null
        try {
            # A connection to a closed port is all the screen needs; nothing is sent to it by a switch.
            $made = Try-Deluno -Method POST -Path '/api/seerr-servers' -Body @{ name = $name; baseUrl = 'http://127.0.0.1:1'; apiKey = 'golden-scratch'; isEnabled = $true }
            Add-Check 'A Seerr connection pointing at a closed port is saved' ($made.Status -in 200, 201 -and $made.Data.id) "HTTP $($made.Status) $($made.Raw)"
            if (-not $made.Data.id) { return }
            $id = $made.Data.id
            # Deluno names a connection itself (the app on its host, made unique), whatever name was sent; the screen shows that name.
            $name = [string]$made.Data.name

            Start-Edge
            $page = New-CdpPage 'about:blank'
            try {
                Open-DelunoPage -Page $page -Path '/indexers/media-servers' -Login $script:S.Login
                $label = "(Pause|Enable) $([regex]::Escape($name))"
                $has = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { if ((Get-PageText -Page $page) -match [regex]::Escape($name)) { $true } else { $null } }
                Add-Check 'The connection is listed on the media servers screen' $has.Ok 'not on the page'
                if (-not $has.Ok) { return }
                # ---- the server refuses: the connection is gone, so the switch's request is answered with Not Found
                Add-Check 'The switch starts on' ((Get-SeerrSwitchState -Page $page -Name $name) -eq 'true') (Get-SeerrSwitchState -Page $page -Name $name)
                Try-Deluno -Method DELETE -Path "/api/seerr-servers/$id" | Out-Null
                $id = $null
                Click-PageText -Page $page -Pattern "^(Pause|Enable) $([regex]::Escape($name))$" | Out-Null
                $refused = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match [regex]::Escape("$name could not be switched off")) { $t } else { $null } }
                Save-Evidence -Name 'proofs2-seerr-refused-page.txt' -Text (Get-PageText -Page $page)
                Add-Check 'A refused switch says why on the screen ("<name> could not be switched off ...")' $refused.Ok 'no such message on the page'
                Add-Check 'The refusal does not read "Failed to fetch"' ((Get-PageText -Page $page) -notmatch '(?i)failed to fetch') ''
                Add-Check 'The switch shows the state the server last held (on), not a guess' ((Get-SeerrSwitchState -Page $page -Name $name) -eq 'true') (Get-SeerrSwitchState -Page $page -Name $name)

                # ---- Deluno cannot be reached: the page's request is rejected the way the browser rejects one nothing answered
                $made2 = Try-Deluno -Method POST -Path '/api/seerr-servers' -Body @{ name = $name; baseUrl = 'http://127.0.0.1:1'; apiKey = 'golden-scratch'; isEnabled = $true }
                $id = $made2.Data.id
                $name = [string]$made2.Data.name
                Open-DelunoPage -Page $page -Path '/indexers/media-servers' -Login $script:S.Login
                $again = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { if ((Get-PageText -Page $page) -match [regex]::Escape($name)) { $true } else { $null } }
                Add-Check 'The connection is listed again' $again.Ok 'not on the page'
                if (-not $again.Ok) { return }
                Invoke-CdpEval -Page $page -Script '(function(){window.__realFetch=window.fetch;window.fetch=function(u,o){var m=((o&&o.method)||"GET").toUpperCase();if(String(u).indexOf("/api/seerr-servers/")>=0&&m==="PUT")return Promise.reject(new TypeError("Failed to fetch"));return window.__realFetch.apply(this,arguments);};return 1;})()' | Out-Null
                Click-PageText -Page $page -Pattern "^(Pause|Enable) $([regex]::Escape($name))$" | Out-Null
                $down = Wait-Until -TimeoutSec 20 -IntervalSec 1 -Condition { $t = Get-PageText -Page $page; if ($t -match 'Deluno could not be reached') { $t } else { $null } }
                Save-Evidence -Name 'proofs2-seerr-unreachable-page.txt' -Text (Get-PageText -Page $page)
                Add-Check 'A request nothing answered says "Deluno could not be reached"' $down.Ok 'no such message on the page'
                Add-Check 'It never reads "Failed to fetch"' ((Get-PageText -Page $page) -notmatch '(?i)failed to fetch') ''
                Add-Check 'The switch still shows the state the server holds (on)' ((Get-SeerrSwitchState -Page $page -Name $name) -eq 'true') (Get-SeerrSwitchState -Page $page -Name $name)
                $held = Try-Deluno -Path "/api/seerr-servers"
                $mine = @($held.Data) | Where-Object { $_.id -eq $id } | Select-Object -First 1
                Add-Check 'The server really still holds it on' ($mine -and $mine.isEnabled -eq $true) $(if ($mine) { "isEnabled $($mine.isEnabled)" } else { 'connection not found' })
            }
            finally { Close-CdpPage -Page $page }
        }
        finally {
            if ($id) { Try-Deluno -Method DELETE -Path "/api/seerr-servers/$id" | Out-Null }
        }
    }
}
