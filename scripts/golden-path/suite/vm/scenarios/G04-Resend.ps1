<#
    "I don't want it", then a resend of the same release to the same client, in each timing:
      before  - Weir has not started the first send (Weir is paused);
      during  - Weir is working on the first send;
      after   - Weir has finished the first send but Deluno has not queued its import.
    The new hand-off must settle on its own with one import, the first send's late report must be quiet, and nothing may be
    left unmatched in Weir's output (#1208, #1226). Every client runs every timing.
#>

# Sends a film's release again, to the same client, as "Search now" on the title does after "I don't want it".
function Send-FilmAgain {
    param([Parameter(Mandatory)] $Film)
    Restore-FilmData -Film $Film
    # The library is routed to one client at a time and the other films were sent since (rc.30.22: a qBittorrent film sent
    # again while the library pointed at NZBGet answered 400, "no download client that can take a release from Golden feed").
    # "Search now" on the title sends to the client the title's library is routed to, so route it back first.
    Set-LibraryClient -Kind $Film.ClientKind | Out-Null
    $r = Try-Deluno -Method POST -Path "/api/movies/$($Film.MovieId)/grab" -Body @{
        releaseName = $Film.Release; indexerId = $Film.IndexerId; indexerName = $(if ($Film.Protocol -eq 'usenet') { 'Golden Newznab stub' } else { 'Golden feed' })
        candidateQuality = 'WEB 1080p'; downloadUrl = $Film.Url; sizeBytes = $Film.Size; seeders = 50; force = $true
        overrideReason = 'Golden scenario suite: sending the same release again'; duplicateAction = $null
    }
    if (-not $Film.PSObject.Properties["FirstSentUtc"]) { $Film | Add-Member -NotePropertyName FirstSentUtc -NotePropertyValue $Film.SentUtc -Force }
    $Film.SentUtc = [DateTime]::UtcNow
    $r
}

# After a torrent is added again to a client that deleted its data, the client needs its data checked. The removal is waited for
# first: a queue row still there is the removed download, and a client that kept it (rc.30.23: Deluge, which has the same id for
# the same torrent) made "finished" true at once, so the second send was never watched.
function Start-AgainDownload {
    param([Parameter(Mandatory)] $Film)
    if ($Film.Protocol -eq 'torrent') { Wait-Until -TimeoutSec 30 -IntervalSec 0.5 -Condition { -not (Find-QueueRow -Name $Film.Release) } | Out-Null }
    $r = Send-FilmAgain -Film $Film
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "Sending $($Film.Release) again answered $($r.Status). $($r.Raw)") }
    # The client must be told to look at the data again as soon as its row is there, as the first send was.
    if (Test-ClientNeedsSettle -Kind $Film.ClientKind) { Invoke-FilmSettle -Film $Film -WaitForRowSec 60 | Out-Null }
    $w = Wait-FilmFinished -Film $Film -TimeoutSec 180
    if (-not $w.Ok) { throw "$($Film.Release) did not finish in $($Film.ClientKind) after being sent again (its client row and folders are in the evidence)." }
}


# Deluno's own log files as lines (the suite also keeps them whole at the end of the run).
function Get-DelunoLogLines {
    param([string] $Match)
    $roots = @((Join-Path $env:LOCALAPPDATA 'Deluno\logs'), (Join-Path $env:LOCALAPPDATA 'DelunoData\logs')) | Where-Object { Test-Path $_ }
    foreach ($r in $roots) {
        Get-ChildItem -Path $r -Recurse -File -Include '*.log', '*.jsonl' -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 500MB } | ForEach-Object {
            $in = $null
            try {
                $in = [IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite,Delete')
                $reader = New-Object IO.StreamReader($in)
                while (($line = $reader.ReadLine()) -ne $null) { if ($line -match $Match) { $line } }
            } catch { } finally { if ($in) { $in.Dispose() } }
        }
    }
}

# Weir's "Hand-off report to .../processors/events returned HTTP 409" lines made at or after a moment. Weir's line does not name the
# film, so the window is the judge here; Deluno's own line (below) names the hand-off.
function Get-WeirReport409Lines {
    param([Parameter(Mandatory)] [DateTime] $SinceUtc)
    $lines = (Get-WeirLogText) -split "`r?`n" | Where-Object { $_ -match 'processors/events returned HTTP 409' }
    foreach ($l in $lines) {
        if ($l -match '"timestamp":\s*"([^"]+)"') {
            $t = [DateTime]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
            if ($t -ge $SinceUtc.ToUniversalTime().AddSeconds(-2)) { $l }
        }
    }
}

# #1242: the first send's late report is settled (2xx), never refused. Weir's log must hold no HTTP 409 for the report, and
# Deluno's answer log (one Information line per answer) must show none for the film's hand-offs.
function Assert-NoReport409 {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] $First, [Parameter(Mandatory)] [DateTime] $SinceUtc, [Parameter(Mandatory)] [string] $What)
    $ids = @($First.id) + @(@(Find-Handoffs -Name $Film.Release) | ForEach-Object { $_.id }) | Where-Object { $_ } | Select-Object -Unique
    $weir = @(Get-WeirReport409Lines -SinceUtc $SinceUtc)
    Add-Check "$What : Weir's log shows no HTTP 409 for the hand-off report" ($weir.Count -eq 0) (($weir | ForEach-Object { if ($_.Length -gt 220) { $_.Substring(0, 220) } else { $_ } }) -join ' || ')
    $deluno = @(Get-DelunoLogLines -Match 'processors/events answered 409' | Where-Object { $line = $_; @($ids | Where-Object { $line -match [regex]::Escape($_) }).Count -gt 0 })
    Add-Check "$What : Deluno's /processors/events log shows no 409 for this film's hand-offs" ($deluno.Count -eq 0) (($deluno | ForEach-Object { if ($_.Length -gt 300) { $_.Substring(0, 300) } else { $_ } }) -join ' || ')
}

# What must be true once a resend has settled.
function Assert-ResendSettled {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] $First, [int] $TimeoutSec = 180, [string] $What, [switch] $SameHashMayCountAsImported, [switch] $FirstWasImported)
    $w = Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 1 -Condition {
        $second = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -ne $First.id } | Select-Object -First 1
        if ($second -and $second.status -eq 'completed' -and $second.importJobId) { $second } else { $null }
    }
    $second = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -ne $First.id } | Select-Object -First 1
    # After: Deluno imports a cleaned film within a second or two of Weir's report, so the first send is often imported before
    # "I don't want it" lands (rc.30.24: every client). A torrent sent again has the same info-hash, and Deluno takes it as the
    # download it has already imported, so no second hand-off is made; that is right when the first send was imported.
    $firstImported = $false
    if (-not $second -and $SameHashMayCountAsImported) {
        $firstNow = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -eq $First.id } | Select-Object -First 1
        $firstImported = [bool]($firstNow -and $firstNow.status -eq 'completed' -and $firstNow.importJobId)
        if ($firstImported) { Add-Note "$What : the first send was imported before the removal landed; the resent torrent (same info-hash) counts as that import." }
    }
    Add-Check "$What : the new hand-off settles on its own" ($w.Ok -or $firstImported) $(if ($second) { "status $($second.status), processor $($second.processor.state), output $($second.outputPath), after $($w.Seconds) s" } elseif ($firstImported) { 'the first send was imported; the same torrent counts as imported' } else { 'there is no second hand-off' })
    $lib = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { Find-Imported -Title $Film.Title }
    Add-Check "$What : the film is in the library" $lib.Ok ''
    $files = @(Get-ChildItem -Path 'C:\Media\Movies' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Film.Title) -and $_.Extension -in '.mkv', '.mp4' })
    Add-Check "$What : exactly one copy is in the library" ($files.Count -eq 1) "$($files.Count) files"
    # The import that follows a settled hand-off is given a minute to clear Weir's output: a hand-off with an import job is not
    # yet an import (rc.30.24: SABnzbd and NZBGet hand-offs read completed while their import failed again and again, #1254).
    $leftWait = Wait-Until -TimeoutSec 60 -IntervalSec 1 -Condition { @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Film.Title) }).Count -eq 0 }
    $leftInWeir = @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Film.Title) })
    Add-Check "$What : nothing of it waits in Weir's output" ($leftWait.Ok -and $leftInWeir.Count -eq 0) (($leftInWeir | ForEach-Object { $_.FullName }) -join '; ')
    # A resend reuses the film's hand-off row under a new id, so the first send's id is no longer listed once the new send exists:
    # that is the first send closed (it is kept as a replaced send and has its own closing line). Left open means it is still listed in an open state.
    $firstNow = @(Find-Handoffs -Name $Film.Release) | Where-Object { $_.id -eq $First.id } | Select-Object -First 1
    Add-Check "$What : the first send is closed, not left open" (-not $firstNow -or $firstNow.status -notin 'submitted', 'waiting', 'accepted', 'started') $(if ($firstNow) { $firstNow.status } else { 'replaced by the new send' })
    $unmatched = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Unmatched in processor output' })
    Add-Check "$What : no 'unmatched in processor output' problem was raised" ($unmatched.Count -eq 0) "$($unmatched.Count) rows"
    $import = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($Film.Title) -and $_ -match 'Imported into the library' })
    # When the first send was imported before the removal landed, a resend with a new download id (Usenet) is a second, real
    # import that replaces the first copy through the recycle bin (#1254): two rows, one file. Otherwise exactly one.
    $expected = if ($FirstWasImported -and $second) { 2 } else { 1 }
    Add-Check "$What : it was imported $(if ($expected -eq 2) { 'twice (the first send, then the resend replacing it)' } else { 'once' })" ($import.Count -eq $expected) "$($import.Count) 'Imported into the library' rows"
    Test-OneStory -Film $Film -What $What
    Assert-NoReport409 -Film $Film -First $First -SinceUtc $Film.FirstSentUtc -What $What
}

# Sends a single-file film and holds its first send at one moment: 'during' (Weir is working on it) or 'after' (Weir has put the
# cleaned file in its output and Deluno has not queued the import). The first send is held at Weir (paused) until Weir is let go, so
# the moment is made, not waited for. Up to three films are tried; the moment is missed when Weir is quicker than the check.
function Get-OpenFirstSend {
    param([Parameter(Mandatory)] [string] $Kind, [Parameter(Mandatory)] [ValidateSet('during', 'after')] [string] $Timing, [string] $Label = $null)
    if (-not $Label) { $Label = "re$Timing" }
    $reached = $null; $film = $null; $first = $null; $what = ''
    for ($try = 1; $try -le 3 -and -not $reached; $try++) {
        $film = New-FilmFor -Kind $Kind -Layout 'file' -Label $Label -Size $(if ($Timing -eq 'during') { 'big' } else { 'small' })
        try {
            Set-WeirPause -Paused $true
            Send-Film -Film $film -Kind $Kind | Out-Null
            Wait-FilmFinished -Film $film -TimeoutSec 240 | Out-Null
            $queued = Wait-Until -TimeoutSec 120 -IntervalSec 0.5 -Condition { Get-FilmHandoff -Film $film }
            if (-not $queued.Ok) { $what = 'the first hand-off never appeared'; Add-Note "Attempt ${try}: $what."; continue }
            # Weir takes the first send now. During: removed and sent again straight away, while it works. After: once its
            # cleaned file is in its output, before Deluno has queued the import.
            Set-WeirPause -Paused $false
            if ($Timing -eq 'after') {
                Wait-Until -TimeoutSec 60 -IntervalSec 0.05 -Condition { @(Get-ChildItem -Path 'C:\Weir\Ready' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($film.Title) }).Count -gt 0 } | Out-Null
            }
            $h = Get-FilmHandoff -Film $film
            $open = $h -and -not $h.importJobId -and ($Timing -eq 'after' -or $h.status -ne 'completed')
            if ($open) { $reached = $true; $first = $h }
            else { $what = "status '$($h.status)', import $(if ($h.importJobId) { 'queued' } else { 'not queued' })"; Add-Note "Attempt ${try}: the $Timing moment was missed ($what); trying a new film." }
        }
        finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
    }
    [pscustomobject]@{ Reached = [bool]$reached; Film = $film; First = $first; What = $what }
}

# Weir's own answer for one hand-off (its intake status route, which only the secret Deluno shares with Weir opens). Null when Weir
# does not answer or the secret cannot be read.
function Get-WeirIntakeStatus {
    param([Parameter(Mandatory)] [string] $HandoffId)
    if (-not $script:S.Secrets.ContainsKey('weir-intake-secret')) {
        $secret = Get-WeirWebhookSecret
        if (-not $secret) { return $null }
        # Held in memory only, so Hide-Secrets masks it in everything the suite writes; it is not written to secrets.json.
        $script:S.Secrets['weir-intake-secret'] = $secret
    }
    try {
        $r = Invoke-WebRequest -Uri "$($script:S.WeirUrl)/api/v1/intake/handoffs/deluno/$HandoffId" -Headers @{ 'X-Webhook-Secret' = $script:S.Secrets['weir-intake-secret'] } -UseBasicParsing -TimeoutSec 15
        $r.Content | ConvertFrom-Json
    }
    catch { $null }
}

# The path at which a processor that watches a folder and writes under an output folder leaves a source: the source's path below the
# watched folder, under the output folder (#1262).
function Get-ExpectedProcessorOutput {
    param([Parameter(Mandatory)] [string] $SourcePath, [Parameter(Mandatory)] [string] $WatchedFolder, [Parameter(Mandatory)] [string] $OutputFolder)
    $watched = $WatchedFolder.TrimEnd('\', '/')
    if (-not $SourcePath.StartsWith($watched + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $SourcePath.StartsWith($watched + '/', [StringComparison]::OrdinalIgnoreCase)) { return $null }
    Join-Path $OutputFolder $SourcePath.Substring($watched.Length + 1)
}

function Invoke-ResendTakeover {
    Invoke-Scenario -Id 'resend.single-file.takeover' -Title "A single file is sent again while Weir works on it: Weir's completed answer for the resend names no output path, and Deluno imports from where Weir puts it" -Body {
        $kind = 'qbittorrent'
        $moment = Get-OpenFirstSend -Kind $kind -Timing 'during' -Label 'retakeover'
        $film = $moment.Film; $first = $moment.First
        Add-Check 'The during moment was reached with the first send still open' $moment.Reached $(if ($first) { "processor '$($first.processor.state)', status '$($first.status)'" } else { "missed three times ($($moment.What))" })
        if (-not $moment.Reached) { return }
        Remove-FilmDownload -Film $film -Reason 'unwanted' | Out-Null
        Start-Sleep -Seconds 1
        $firstWasImported = (Wait-Until -TimeoutSec 5 -IntervalSec 0.5 -Condition { Find-Imported -Title $film.Title }).Ok
        if ($firstWasImported) { Add-Note "takeover/$kind : the first send was imported before the removal landed." }
        Start-AgainDownload -Film $film

        # What Weir itself says of the resend, read as soon as it is completed (before Deluno reports its outcome and Weir lets the hand-off go).
        $weir = Wait-Until -TimeoutSec 180 -IntervalSec 0.5 -Condition {
            $second = @(Find-Handoffs -Name $film.Release) | Where-Object { $_.id -ne $first.id } | Select-Object -First 1
            if ($second) { $s = Get-WeirIntakeStatus -HandoffId $second.id; if ($s -and $s.state -in 'completed', 'cancelled', 'failed') { [pscustomobject]@{ Id = $second.id; Status = $s } } }
        }
        $answer = if ($weir.Ok) { $weir.Value.Status } else { $null }
        Add-Check "Weir's final answer for the resend is 'completed' and names no output path" ([bool]$answer -and $answer.state -eq 'completed' -and -not $answer.outputPath) $(if ($answer) { "state '$($answer.state)', outputPath '$($answer.outputPath)'" } else { "Weir gave no final answer for the resend in 180 s (or its secret could not be read)" })

        Assert-ResendSettled -Film $film -First $first -What "takeover/$kind" -FirstWasImported:$firstWasImported

        # Deluno found the cleaned file where Weir puts it: the source's path below the watched folder, under the output folder.
        $lib = Get-Library -MediaType movies
        $watched = if ($lib.downloadsPath) { [string]$lib.downloadsPath } else { 'C:\Downloads\Completed\Movies' }
        $output = if ($lib.processorOutputPath) { [string]$lib.processorOutputPath } else { 'C:\Weir\Ready\Movies' }
        $second = @(Find-Handoffs -Name $film.Release) | Where-Object { $_.id -ne $first.id } | Select-Object -First 1
        $expected = if ($second) { Get-ExpectedProcessorOutput -SourcePath ([string]$second.sourcePath) -WatchedFolder $watched -OutputFolder $output } else { $null }
        Add-Check "Deluno imported the file from <output folder>\<source path relative to the watched folder>" ([bool]$expected -and [bool]$second -and [string]$second.outputPath -ieq $expected) $(if ($second) { "source '$($second.sourcePath)', expected '$expected', hand-off output '$($second.outputPath)'" } else { 'there is no second hand-off' })
        # Deluno's own record of what it did (its Activity feed): it names the cleaned file it found without being told where it was.
        $fallback = @(Get-ActivityText | Where-Object { $_ -match [regex]::Escape($film.Title) -and $_ -match 'found the cleaned file|matched processed output|Found the processed file' })
        Add-Check "Deluno's log names that it found the cleaned file itself" ($fallback.Count -gt 0) "$($fallback.Count) Activity rows"
    }
}

function Invoke-GroupResend {
    # ---- before: Weir paused, every client in one window.
    $films = @{}
    $firsts = @{}
    # Only the clients whose "before" scenario was chosen share the window; each client's result reads its own film.
    $beforeKinds = @(Get-SelectedKinds -IdFormat 'resend.before.{0}')
    if ($beforeKinds.Count -gt 0) {
    try {
        Set-WeirPause -Paused $true
        foreach ($kind in $beforeKinds) {
            $f = New-FilmFor -Kind $kind -Layout 'file' -Label 'rebefore'
            $films[$kind] = Send-Film -Film $f -Kind $kind
        }
        foreach ($kind in $beforeKinds) {
            $f = $films[$kind]
            Wait-FilmFinished -Film $f -TimeoutSec 240 | Out-Null
            $w = Wait-Until -TimeoutSec 90 -IntervalSec 1 -Condition { Get-FilmHandoff -Film $f }
            $firsts[$kind] = $w.Value
        }
        foreach ($kind in $beforeKinds) {
            $f = $films[$kind]
            if ($firsts[$kind]) {
                Remove-FilmDownload -Film $f -Reason 'unwanted' | Out-Null
                Start-Sleep -Seconds 1
                try { Start-AgainDownload -Film $f } catch { Write-SuiteLog "   $($f.Release): $(Hide-Secrets $_.Exception.Message)" }
            }
        }
        # Every client has its second hand-off before Weir is resumed.
        Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition {
            $missing = 0
            foreach ($k in $beforeKinds) {
                if (-not $firsts[$k]) { continue }
                $firstId = $firsts[$k].id
                $another = @(Find-Handoffs -Name $films[$k].Release) | Where-Object { $_.id -ne $firstId }
                if (-not $another) { $missing++ }
            }
            $missing -eq 0
        } | Out-Null
    }
    finally { try { Set-WeirPause -Paused $false } catch { Write-SuiteLog "   could not resume Weir: $($_.Exception.Message)" } }
    }
    foreach ($kind in Get-AllKinds) {
        Invoke-Scenario -Id "resend.before.$kind" -Title "I don't want it, then a resend to $kind, before Weir starts the first send" -Body {
            if (-not $firsts[$kind]) { throw "The first hand-off for $kind never appeared." }
            Assert-ResendSettled -Film $films[$kind] -First $firsts[$kind] -What "before/$kind"
        }
    }

    # ---- during and after: one client at a time. The first send is held at Weir (paused) until Weir is let go, so the moment is
    # made, not waited for: a film is cleaned in about a second and Deluno hears of it at once (rc.30.23: five clients, three tries
    # each, the hand-off was already imported when the check first looked, 60 s after the send).
    foreach ($timing in 'during', 'after') {
        foreach ($kind in Get-AllKinds) {
            Invoke-Scenario -Id "resend.$timing.$kind" -Title "I don't want it, then a resend to $kind, while Weir is $(if ($timing -eq 'during') { 'working on' } else { 'done with' }) the first send" -Body {
                $moment = Get-OpenFirstSend -Kind $kind -Timing $timing
                $film = $moment.Film; $first = $moment.First
                Add-Check "The $timing moment was reached with the first send still open" $moment.Reached $(if ($first) { "processor '$($first.processor.state)', status '$($first.status)'" } else { "missed three times ($($moment.What))" })
                if (-not $moment.Reached) { return }
                Remove-FilmDownload -Film $film -Reason 'unwanted' | Out-Null
                Start-Sleep -Seconds 1
                # Whether the first send reached the library before the removal (Deluno imports a cleaned film within a second or two).
                $firstWasImported = (Wait-Until -TimeoutSec 5 -IntervalSec 0.5 -Condition { Find-Imported -Title $film.Title }).Ok
                if ($firstWasImported) { Add-Note "$timing/$kind : the first send was imported before the removal landed." }
                Start-AgainDownload -Film $film
                Assert-ResendSettled -Film $film -First $first -What "$timing/$kind" -SameHashMayCountAsImported:($timing -eq 'after' -and $film.Protocol -eq 'torrent') -FirstWasImported:$firstWasImported
            }
        }
    }

    # ---- a single file taken over: Weir's answer for the resend has no output path, and Deluno finds the file where Weir puts it (#1262).
    Invoke-ResendTakeover
}
