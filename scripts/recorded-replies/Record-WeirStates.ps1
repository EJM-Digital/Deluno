# Records the Weir states Record-Weir.ps1 does not reach, against the Weir on the golden VM (#1237, #1239).
# Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1 and Record-Weir.ps1. Safe the same way: hand-offs name hard links of
# (or copies of) a real public film in Weir's watched folder, the workflow is linked to a media manager so Weir keeps the original,
# and everything it makes is removed again at the end, with the cleaned files Weir wrote for them.
#
# What it records, as app "weir" under the version Weir reports:
#   handoff-release-folder-accepted / release-folder-status-<state>    a hand-off whose sourcePath is a release folder (film and an .nfo)
#   handoff-source-files-accepted / source-files-status-<state>        the same, with sourceFiles naming the film
#   status-queued, status-working, status-cancelled, status-failed     the status poll in each state
#   outcome-handoff-not-finished-queued / -working                     409 handoff_not_finished
#   outcome-handoff-ended / outcome-handoff-ended-after-failed         409 handoff_ended after a cancel and after a failure
#   handoff-cancelled, handoff-cancel-working, handoff-cancel-too-late 204 for a queued hand-off, 409 while working, 409 once finished
#   outcome-not-imported-<kind>                                        one per reason Deluno sends (none, the default words, an import refusal)
#   outcome-imported-with-reason                                       imported, with the note a season pack carries
#   outcome-superseded                                                 an outcome for a hand-off whose source was sent again

function Invoke-RecordWeirStates($p) {
    Assert-FreeSpace 4 'the Weir states'
    $secret = Get-WeirWebhookSecret
    if (-not $secret) { Add-Missing 'weir states: Deluno''s connection to Weir, and so its webhook secret, was not found in the VM'; return }
    $headers = @{ 'X-Webhook-Secret' = $secret }
    $w = New-RecordedClient -BaseUrl $p.WeirUrl -KeepHeaders @('content-type')
    $source = $p.WeirSource
    function Get($path) { Send-Recorded -Client $w -Path $path -Headers $headers }
    function Status($id) { Get "/api/v1/intake/handoffs/$source/$id" }
    function Outcome($id, $outcome, $path, $reason) {
        Send-Recorded -Client $w -Method POST -Path "/api/v1/intake/handoffs/$source/$id/outcome" -Headers $headers -Json ([ordered]@{
            outcome = $outcome; occurredUtc = (Get-Date).ToUniversalTime().ToString('o'); importedPath = $path; reason = $reason })
    }
    function Cancel($id) { Send-Recorded -Client $w -Method DELETE -Path "/api/v1/intake/handoffs/$source/$id" -Headers $headers }
    function Submit($payload) { Send-Recorded -Client $w -Method POST -Path "/api/v1/intake/webhook/$source" -Headers $headers -Json $payload }
    function StateOf($exchange) { $j = Get-ReplyJson $exchange; if ($j.state) { [string]$j.state } else { "http-$($exchange.reply.status)" } }
    function CodeOf($exchange) { [string](Get-ReplyJson $exchange).code }

    $caps = Get '/api/v1/intake/capabilities'
    if ($caps.reply.status -ne 200) { Add-Missing "weir states: capabilities answered $($caps.reply.status) at $($p.WeirUrl)"; return }
    $ver = if ((Get-ReplyJson $caps).version) { [string](Get-ReplyJson $caps).version } else { $p.WeirVersion }
    $movie = @((Get-ReplyJson (Get '/api/v1/intake/library-folders')).libraries) | Where-Object { $_.media_type -eq 'movie' } | Select-Object -First 1
    $libraryId = if ($movie.manager_library_key) { [string]$movie.manager_library_key } else { 'recorded-library' }
    $watched = [string]$movie.watched_folder
    if (-not $watched) { Add-Missing 'weir states: Weir lists no watched folder for its movie workflow'; return }
    $made = [System.Collections.Generic.List[string]]::new()

    function New-Link([string] $path) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        try { $null = New-Item -ItemType HardLink -Path $path -Target $p.VmMediaFile -ErrorAction Stop } catch { Copy-Item -LiteralPath $p.VmMediaFile -Destination $path -Force }
        $made.Add($path)
    }
    function New-Payload($sourcePath, $title, $handoffId, $sourceFiles) {
        $payload = [ordered]@{
            eventType = 'deluno.processor-handoff'; handoffId = $handoffId; libraryId = $libraryId; mediaType = 'movie'
            sourcePath = $sourcePath; releaseName = $title; queueItemId = [guid]::NewGuid().ToString(); callbackPath = '/api/integrations/processors/events'
            title = $title; year = 2008
        }
        if ($sourceFiles) { $payload.sourceFiles = @($sourceFiles) }
        $payload
    }
    # Polls quickly and keeps the first reply seen in each state, until the hand-off leaves queued and working.
    function Watch-Handoff($id, [int] $Seconds = 90) {
        $seen = [ordered]@{}
        $end = (Get-Date).AddSeconds($Seconds)
        do {
            $s = Status $id
            $state = StateOf $s
            if (-not $seen.Contains($state)) { $seen[$state] = $s }
            if ($state -notmatch '^(queued|working)$') { break }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $end)
        $seen
    }
    function Save-Watched($seen, [string] $prefix, [string] $what) {
        foreach ($name in $seen.Keys) { Save-State weir $ver "$prefix-status-$($name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')" "The status of $what while it was $name." @($seen[$name]) }
    }
    # A finished hand-off over a fresh link of the film: returns its id and where its file is.
    function New-Finished([string] $label) {
        $file = Join-Path $watched "Recorded.Weir.$label.mkv"
        New-Link $file
        $id = [guid]::NewGuid().ToString()
        $null = Submit (New-Payload $file "Big Buck Bunny (2008) Recorded $label" $id $null)
        $seen = Watch-Handoff $id
        [pscustomobject]@{ Id = $id; File = $file; Seen = $seen; Last = (Status $id) }
    }

    try {
        # ---- a release folder, and a release folder with sourceFiles
        $folder = Join-Path $watched 'Recorded.Weir.Folder.2008.1080p.WEB-DL.x264-GOLDEN'
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        $null = New-Item -ItemType Directory -Path $folder
        $made.Add($folder)
        New-Link (Join-Path $folder 'Recorded.Weir.Folder.mkv')
        Set-Content -LiteralPath (Join-Path $folder 'Recorded.Weir.Folder.nfo') -Value ('Golden stub extra file ' * 20) -Encoding ascii
        $idFolder = [guid]::NewGuid().ToString()
        $acceptedFolder = Submit (New-Payload $folder 'Big Buck Bunny (2008) Recorded Folder' $idFolder $null)
        Save-State weir $ver 'handoff-release-folder-accepted' 'A hand-off whose sourcePath is a release folder (the film and a small .nfo), with no sourceFiles: Weir accepts it.' @($acceptedFolder)
        Save-Watched (Watch-Handoff $idFolder) 'release-folder' 'the release-folder hand-off'

        $folder2 = Join-Path $watched 'Recorded.Weir.Files.2008.1080p.WEB-DL.x264-GOLDEN'
        Remove-Item -LiteralPath $folder2 -Recurse -Force -ErrorAction SilentlyContinue
        $null = New-Item -ItemType Directory -Path $folder2
        $made.Add($folder2)
        $film2 = Join-Path $folder2 'Recorded.Weir.Files.mkv'
        New-Link $film2
        Set-Content -LiteralPath (Join-Path $folder2 'Recorded.Weir.Files.nfo') -Value ('Golden stub extra file ' * 20) -Encoding ascii
        $idFiles = [guid]::NewGuid().ToString()
        $acceptedFiles = Submit (New-Payload $folder2 'Big Buck Bunny (2008) Recorded Files' $idFiles @($film2))
        Save-State weir $ver 'handoff-source-files-accepted' 'A hand-off whose sourcePath is a release folder and whose sourceFiles names the film (the .nfo is left out): Weir accepts it.' @($acceptedFiles)
        Save-Watched (Watch-Handoff $idFiles) 'source-files' 'the hand-off that names its files'

        # ---- a burst: the first is being worked, the later ones wait their turn
        $burst = 1..8 | ForEach-Object { $file = Join-Path $watched "Recorded.Weir.Q$_.mkv"; New-Link $file; [pscustomobject]@{ Id = [guid]::NewGuid().ToString(); File = $file; Title = "Big Buck Bunny (2008) Recorded Q$_" } }
        foreach ($entry in $burst) { $null = Submit (New-Payload $entry.File $entry.Title $entry.Id $null) }
        $first = $burst[0]; $last = $burst[-1]; $second = $burst[1]
        $statusWorking = Status $first.Id
        if ((StateOf $statusWorking) -eq 'working') {
            $outcomeWorking = Outcome $first.Id 'imported' $first.File $null
            $cancelWorking = Cancel $first.Id
            Save-State weir $ver 'status-working' 'The status of a hand-off Weir is working on (the first of eight sent together).' @($statusWorking)
            if ((CodeOf $outcomeWorking) -eq 'handoff_not_finished') { Save-State weir $ver 'outcome-handoff-not-finished-working' 'The outcome reported for a hand-off Weir is still working on: 409 handoff_not_finished.' @($outcomeWorking) }
            else { Add-Missing "weir outcome-handoff-not-finished-working: the outcome for a working hand-off was HTTP $($outcomeWorking.reply.status) $(CodeOf $outcomeWorking)" }
            if ($cancelWorking.reply.status -eq 409) { Save-State weir $ver 'handoff-cancel-working' 'A cancel for a hand-off Weir is working on: refused with 409.' @($cancelWorking, (Status $first.Id)) }
            else { Add-Missing "weir handoff-cancel-working: the cancel of a working hand-off was HTTP $($cancelWorking.reply.status), not 409" }
        } else { Add-Missing "weir status-working / outcome-handoff-not-finished-working / handoff-cancel-working: the first hand-off of the burst was already $(StateOf $statusWorking)" }

        $statusQueued = Status $last.Id
        if ((StateOf $statusQueued) -eq 'queued') {
            Save-State weir $ver 'status-queued' 'The status of a hand-off waiting behind others (the last of eight sent together), with its place in the line.' @($statusQueued, (Status $second.Id))
            $outcomeQueued = Outcome $last.Id 'imported' $last.File $null
            if ((CodeOf $outcomeQueued) -eq 'handoff_not_finished') { Save-State weir $ver 'outcome-handoff-not-finished-queued' 'The outcome reported for a hand-off still waiting its turn: 409 handoff_not_finished.' @($outcomeQueued) }
            else { Add-Missing "weir outcome-handoff-not-finished-queued: the outcome for a queued hand-off was HTTP $($outcomeQueued.reply.status) $(CodeOf $outcomeQueued)" }
            $cancel = Cancel $last.Id
            $afterCancel = Status $last.Id
            if ($cancel.reply.status -ge 200 -and $cancel.reply.status -lt 300) {
                Save-State weir $ver 'handoff-cancelled' 'A hand-off cancelled by Deluno (DELETE) while it was waiting its turn.' @($cancel, $afterCancel)
                if ((StateOf $afterCancel) -eq 'cancelled') { Save-State weir $ver 'status-cancelled' 'The status of a hand-off after it was cancelled while queued.' @($afterCancel) }
                else { Add-Missing "weir status-cancelled: after a cancel the status was $(StateOf $afterCancel)" }
                $ended = Outcome $last.Id 'not-imported' $null 'Recorded: the hand-off was cancelled'
                if ((CodeOf $ended) -eq 'handoff_ended') { Save-State weir $ver 'outcome-handoff-ended' 'The outcome reported for a hand-off that was cancelled: 409 handoff_ended.' @($ended) }
                else { Add-Missing "weir outcome-handoff-ended: the outcome after a cancel was HTTP $($ended.reply.status) $(CodeOf $ended)" }
            } else { Add-Missing "weir handoff-cancelled: the cancel of a queued hand-off was HTTP $($cancel.reply.status), not 2xx" }
        } else { Add-Missing "weir status-queued / outcome-handoff-not-finished-queued / handoff-cancelled: the last hand-off of the burst was $(StateOf $statusQueued), not queued" }
        # The rest of the burst is cancelled while it waits, so Weir is not left working through copies of the same film.
        foreach ($entry in $burst | Select-Object -Skip 1 | Select-Object -SkipLast 1) { $null = Cancel $entry.Id }
        $null = Watch-Handoff $first.Id
        # A cancel that comes too late: the hand-off is finished.
        $late = Cancel $first.Id
        if ($late.reply.status -eq 409) { Save-State weir $ver 'handoff-cancel-too-late' 'A cancel for a hand-off Weir has already finished: refused with 409.' @($late, (Status $first.Id)) }
        else { Add-Missing "weir handoff-cancel-too-late: the cancel of a finished hand-off was HTTP $($late.reply.status), not 409" }
        Start-Sleep -Seconds 4   # whatever was working when the rest were cancelled

        # ---- failed: a file under Weir's minimum size, which it refuses
        $tiny = Join-Path $watched 'Recorded.Weir.Tiny.mkv'
        Set-Content -LiteralPath $tiny -Value 'this is not a film' -Encoding ascii
        $made.Add($tiny)
        $idTiny = [guid]::NewGuid().ToString()
        $null = Submit (New-Payload $tiny 'Recorded Weir Tiny' $idTiny $null)
        $seenTiny = Watch-Handoff $idTiny
        $failed = Status $idTiny
        if ((StateOf $failed) -eq 'failed') {
            Save-State weir $ver 'status-failed' 'The status of a hand-off that failed: the file is under the workflow''s minimum size.' @($failed)
            $afterFailed = Outcome $idTiny 'not-imported' $null 'Recorded: the hand-off failed'
            if ((CodeOf $afterFailed) -eq 'handoff_ended') { Save-State weir $ver 'outcome-handoff-ended-after-failed' 'The outcome reported for a hand-off that failed: 409 handoff_ended.' @($afterFailed) }
            else { Add-Missing "weir outcome-handoff-ended-after-failed: the outcome after a failure was HTTP $($afterFailed.reply.status) $(CodeOf $afterFailed)" }
        } else { Add-Missing "weir status-failed: a file under the minimum size ended $(StateOf $failed), not failed" }

        # ---- not-imported, one per reason Deluno sends (docs/external-integration-api.md, "Telling the processor what became of the file")
        $reasons = [ordered]@{
            'no-reason'      = $null
            'default-reason' = 'The hand-off was closed, so Deluno will not import this file.'
            'import-refused' = 'Deluno could not match this file to a film in the library, so it will not import it.'
        }
        foreach ($kind in $reasons.Keys) {
            $done = New-Finished "N$($kind -replace '[^a-z]', '')"
            if ((StateOf $done.Last) -ne 'completed') { Add-Missing "weir outcome-not-imported-$kind : the hand-off ended $(StateOf $done.Last), not completed"; continue }
            Save-State weir $ver "outcome-not-imported-$kind" "A not-imported outcome for a finished hand-off$(if ($reasons[$kind]) { ', with a reason' } else { ', with no reason' }): Weir keeps its copy." @((Outcome $done.Id 'not-imported' $null $reasons[$kind]))
        }
        # imported, with the note a season pack that left some files alone carries
        $withNote = New-Finished 'Note'
        if ((StateOf $withNote.Last) -eq 'completed') {
            $outPath = (Get-ReplyJson $withNote.Last).outputPath
            Save-State weir $ver 'outcome-imported-with-reason' 'An imported outcome that also carries a reason: the note a season pack sends about the files it left alone.' @((Outcome $withNote.Id 'imported' $(if ($outPath) { $outPath } else { $withNote.File }) 'Left alone: Recorded.Sample.mkv, Recorded.Featurette.mkv'))
        } else { Add-Missing "weir outcome-imported-with-reason: the hand-off ended $(StateOf $withNote.Last), not completed" }

        # ---- superseded: the same source sent again, then the outcome of the older hand-off while the newer one is in Weir
        $older = New-Finished 'Superseded'
        if ((StateOf $older.Last) -eq 'completed') {
            $newerId = [guid]::NewGuid().ToString()
            $resend = Submit (New-Payload $older.File 'Big Buck Bunny (2008) Recorded Superseded' $newerId $null)
            $olderStatus = Status $older.Id
            $olderOutcome = Outcome $older.Id 'imported' (Get-ReplyJson $older.Last).outputPath $null
            Save-State weir $ver 'outcome-superseded' 'The same source sent again (a new handoffId), then the outcome of the OLDER hand-off reported while the newer one is still in Weir.' @($resend, $olderStatus, $olderOutcome, (Status $newerId))
            $null = Watch-Handoff $newerId
            Save-State weir $ver 'outcome-superseded-newer' 'The outcome of the NEWER hand-off, reported after Weir finished it, once the older hand-off''s outcome had been recorded.' @((Status $newerId), (Outcome $newerId 'imported' (Get-ReplyJson (Status $newerId)).outputPath $null))
        } else { Add-Missing "weir outcome-superseded: the first hand-off ended $(StateOf $older.Last), not completed" }
    } finally {
        # Weir may still be working through something: let it end, then take back our links and the cleaned files it wrote.
        Start-Sleep -Seconds 8
        foreach ($path in $made) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
        foreach ($root in $watched, [string]$movie.output_folder) {
            if ($root -and (Test-Path -LiteralPath $root)) { Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name -like 'Recorded.Weir.*' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
