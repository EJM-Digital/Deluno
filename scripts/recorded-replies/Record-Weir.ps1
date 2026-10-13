# Records Weir's intake API (capabilities, library folders, hand-offs, outcomes) on the golden VM (#1237).
# Runs INSIDE the VM after Record.Lib.ps1.
#
# Safe by construction (Weir session, 11 Oct 2026): it only accepts, refuses, polls and reports outcomes through the
# intake API. Hand-offs name COPIES of the film under C:\golden\recorded\weir, so even a Weir workflow that removes the
# original after success removes only a copy. Use a Deluno-linked workflow (or a Weir-only one with "remove original
# after success" OFF) on the VM's own folders. The film is a real public one (-VmMediaFile), never a made-up file.
#
# Records Weir's replies as app "weir". Its log files (C:\ProgramData\Weir\logs) are kept beside them.

$script:Logs = @{}

function Invoke-RecordWeir($p) {
    Assert-FreeSpace 6 'the Weir hand-offs'
    # The secret Weir shares with Deluno is read from Deluno's own connection to Weir, inside the VM (Record.Secrets.ps1).
    $secret = Get-WeirWebhookSecret
    if (-not $secret) { Add-Missing 'weir: Deluno''s connection to Weir, and so its webhook secret, was not found in the VM (connect Weir in Deluno first)'; return }
    $headers = @{ 'X-Webhook-Secret' = $secret }
    $w = New-RecordedClient -BaseUrl $p.WeirUrl -KeepHeaders @('content-type')
    $source = $p.WeirSource
    function Get($path, $h = $headers) { Send-Recorded -Client $w -Path $path -Headers $h }
    function Status($id) { Get "/api/v1/intake/handoffs/$source/$id" }
    function Outcome($id, $outcome, $path, $reason) {
        Send-Recorded -Client $w -Method POST -Path "/api/v1/intake/handoffs/$source/$id/outcome" -Headers $headers -Json ([ordered]@{
            outcome = $outcome; occurredUtc = (Get-Date).ToUniversalTime().ToString('o'); importedPath = $path; reason = $reason })
    }
    function Submit($payload, $h = $headers) { Send-Recorded -Client $w -Method POST -Path "/api/v1/intake/webhook/$source" -Headers $h -Json $payload }

    $caps = Get '/api/v1/intake/capabilities'
    $capsJson = Get-ReplyJson $caps
    $ver = if ($capsJson.version) { [string]$capsJson.version } else { $p.WeirVersion }
    if ($caps.reply.status -ne 200) { Add-Missing "weir: capabilities answered $($caps.reply.status) at $($p.WeirUrl); is Weir running and is the secret right?"; return }
    Save-State weir $ver 'capabilities' 'GET /api/v1/intake/capabilities with the shared secret.' @($caps)
    $folders = Get '/api/v1/intake/library-folders'
    Save-State weir $ver 'library-folders' 'GET /api/v1/intake/library-folders: the folder Weir watches for each library.' @($folders)
    $wrongSecret = Get '/api/v1/intake/capabilities' @{ 'X-Webhook-Secret' = 'not-the-secret' }
    Save-State weir $ver 'capabilities-wrong-secret' 'The capabilities call with a wrong secret.' @($wrongSecret)
    $noSecret = Get '/api/v1/intake/capabilities' @{}
    Save-State weir $ver 'capabilities-no-secret' 'The capabilities call with no secret.' @($noSecret)

    # The Movies workflow: the library key Deluno knows it by, and the folder it watches. Weir only accepts a hand-off for a file that is in
    # a watched folder, which is where a download client leaves a finished download.
    $movie = @((Get-ReplyJson $folders).libraries) | Where-Object { $_.media_type -eq 'movie' } | Select-Object -First 1
    $libraryId = if ($movie.manager_library_key) { [string]$movie.manager_library_key } else { 'recorded-library' }
    $watched = [string]$movie.watched_folder
    if (-not $watched) { Add-Missing 'weir hand-offs: Weir lists no watched folder for its movie workflow'; return }
    $script:WeirCopies = [System.Collections.Generic.List[string]]::new()

    function New-Payload($copyName, $title, $handoffId) {
        # A copy of the real film, put where the download client would have left it. Weir's linked workflow keeps the original.
        $copy = Join-Path $watched $copyName
        Remove-Item -LiteralPath $copy -Force -ErrorAction SilentlyContinue
        try { $null = New-Item -ItemType HardLink -Path $copy -Target $p.VmMediaFile -ErrorAction Stop } catch { Copy-Item -LiteralPath $p.VmMediaFile -Destination $copy -Force }
        $script:WeirCopies.Add($copy)
        [ordered]@{
            eventType = 'deluno.processor-handoff'; handoffId = $handoffId; libraryId = $libraryId; mediaType = 'movie'
            sourcePath = $copy; releaseName = $title; queueItemId = [guid]::NewGuid().ToString(); callbackPath = '/api/integrations/processors/events'
            title = $title; year = 2008
        }
    }
    function Wait-Handoff($id, $label) {
        $seen = [ordered]@{}
        $end = (Get-Date).AddMinutes(3)
        do {
            $s = Status $id
            $j = Get-ReplyJson $s
            $value = if ($j.state) { [string]$j.state } elseif ($j.status) { [string]$j.status } else { "http-$($s.reply.status)" }
            if (-not $seen.Contains($value)) { $seen[$value] = $s; Write-Host "weir hand-off $label is $value" }
            # Anything but a state that is still going (waiting its turn or being worked on) is where the hand-off ended up.
            if ($value -notmatch '^(queued|working|waiting|scheduled|accepted|running|received|paused)$') { break }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $end)
        $seen
    }

    # A: accepted, polled through each state, completed, then the outcomes.
    $idA = [guid]::NewGuid().ToString()
    $payloadA = New-Payload 'Recorded.Weir.A.mkv' 'Big Buck Bunny (2008) Recorded A' $idA
    $accepted = Submit $payloadA
    Save-State weir $ver 'handoff-accepted' 'A hand-off submitted for a copy of a real film: Weir accepts it.' @($accepted)
    $seenA = Wait-Handoff $idA 'A'
    foreach ($name in $seenA.Keys) { Save-State weir $ver "status-$($name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')" "The hand-off status poll while the hand-off was $name." @($seenA[$name]) }
    $last = Status $idA
    $lastJson = Get-ReplyJson $last
    $outPath = @($lastJson.outputPath, $lastJson.output_path, $lastJson.deliveredPath, $lastJson.path) | Where-Object { $_ } | Select-Object -First 1
    Save-State weir $ver 'completed-with-output-path' 'The status of the finished hand-off. (If this shows no output path, Weir handed the file back as it was.)' @($last)
    $delivered = Outcome $idA 'imported' $(if ($outPath) { $outPath } else { $payloadA.sourcePath }) $null
    Save-State weir $ver 'outcome-delivered' 'The outcome reported after Weir finished: Weir answers that the file is released.' @($delivered)
    $again = Outcome $idA 'imported' $(if ($outPath) { $outPath } else { $payloadA.sourcePath }) $null
    Save-State weir $ver 'outcome-already-recorded' 'The same outcome reported a second time.' @($again)

    # A hand-off still waiting its turn can be cancelled; one Weir has started cannot. Weir takes a few seconds per film, so a burst of
    # hand-offs leaves the later ones queued: the last is cancelled while it waits, and its outcome is then asked for (handoff_ended).
    $burst = 1..8 | ForEach-Object {
        $id = [guid]::NewGuid().ToString()
        $payload = New-Payload "Recorded.Weir.Q$_.mkv" "Big Buck Bunny (2008) Recorded Q$_" $id
        $null = Submit $payload
        [pscustomobject]@{ Id = $id; Payload = $payload }
    }
    $victim = $burst[-1]
    $queued = Wait-Until -Seconds 20 -EverySeconds 1 { $j = Get-ReplyJson (Status $victim.Id); if ($j.state -eq 'queued') { $j } }
    $statusQueued = Status $victim.Id
    if ($queued) { Save-State weir $ver 'status-queued' 'The status of a hand-off waiting behind others: queued, with its place in the line.' @($statusQueued) }
    else { Add-Missing 'weir status-queued: the burst of hand-offs was finished before the last could be seen queued' }
    # An outcome for a hand-off that has not been worked yet: 409 handoff_not_finished.
    $notFinished = Outcome $victim.Id 'imported' $victim.Payload.sourcePath $null
    # Named by what Weir really answered: 409 handoff_not_finished only when the hand-off was still unfinished, and a plain 200 when it had
    # already been worked by the time the outcome arrived (Weir takes only a few seconds per film).
    if ((Get-ReplyJson $notFinished).code) {
        Save-State weir $ver "outcome-$(((Get-ReplyJson $notFinished).code) -replace '_', '-')" 'The outcome reported for a hand-off that was not finished: Weir answers 409 with the code in this recording.' @($notFinished)
    } else {
        Save-State weir $ver "outcome-finished-in-burst-$($notFinished.reply.status)" 'The outcome reported for the last hand-off of a burst. Weir had already finished it by then, so this is a 200 for a finished hand-off, not an answer for one that was not finished (Record-WeirStates.ps1 records those).' @($notFinished)
    }
    $cancel = Send-Recorded -Client $w -Method DELETE -Path "/api/v1/intake/handoffs/$source/$($victim.Id)" -Headers $headers
    Save-State weir $ver $(if ($cancel.reply.status -ge 200 -and $cancel.reply.status -lt 300) { 'handoff-cancelled' } else { 'handoff-cancel-refused' }) 'A hand-off cancelled by Deluno (DELETE) while it was still waiting its turn (or, when Weir had already started it, refused).' @($cancel, (Status $victim.Id))
    $ended = Outcome $victim.Id 'not-imported' $null 'Recorded: the hand-off was cancelled'
    $endedCode = (Get-ReplyJson $ended).code
    Save-State weir $ver $(if ($endedCode) { "outcome-$($endedCode -replace '_', '-')" } else { "outcome-after-cancel-$($ended.reply.status)" }) 'The outcome reported for a hand-off that was cancelled: Weir answers 409 handoff_ended.' @($ended)
    # one that is working, cancelled too late
    $working = $burst[0]
    $late = Send-Recorded -Client $w -Method DELETE -Path "/api/v1/intake/handoffs/$source/$($working.Id)" -Headers $headers
    if ($late.reply.status -eq 409) { Save-State weir $ver 'handoff-cancel-too-late' 'A cancel for a hand-off Weir has already started or finished: refused with 409.' @($late) }
    else { Save-State weir $ver "handoff-cancelled-in-burst-$($late.reply.status)" 'A cancel for the first hand-off of a burst. Weir answered a success, so that hand-off was still queued; it is not a cancel that came too late.' @($late) }
    # The rest of the burst is cancelled while it waits, so Weir is not left working through copies of the same film.
    foreach ($entry in $burst | Select-Object -Skip 1) { $null = Send-Recorded -Client $w -Method DELETE -Path "/api/v1/intake/handoffs/$source/$($entry.Id)" -Headers $headers }
    $null = Wait-Handoff $burst[0].Id 'burst first'

    # Refused hand-offs and ids Weir has no record of.
    $bad = New-Payload 'Recorded.Weir.C.mkv' 'Recorded refused' ([guid]::NewGuid().ToString())
    $refusedSecret = Submit $bad @{ 'X-Webhook-Secret' = 'not-the-secret' }
    Save-State weir $ver 'handoff-refused-wrong-secret' 'A hand-off sent with a wrong secret.' @($refusedSecret)
    $noPath = [ordered]@{ eventType = 'deluno.processor-handoff'; handoffId = [guid]::NewGuid().ToString(); libraryId = $libraryId; mediaType = 'movie' }
    Save-State weir $ver 'handoff-ignored-no-source' 'A hand-off with no source path: Weir answers that it ignored it.' @((Submit $noPath))
    # A file outside every watched folder is refused on the spot.
    $outside = 'C:\golden\recorded\weir\Recorded.Weir.NW.mkv'
    New-Item -ItemType Directory -Force -Path (Split-Path $outside) | Out-Null
    Copy-Item -LiteralPath $p.VmMediaFile -Destination $outside -Force
    $elsewhere = New-Payload 'Recorded.Weir.NW.mkv' 'Recorded elsewhere' ([guid]::NewGuid().ToString())
    $elsewhere.sourcePath = $outside
    Save-State weir $ver 'handoff-refused-not-watched' 'A hand-off for a file that is in no folder a Weir workflow watches.' @((Submit $elsewhere))
    Remove-Item -LiteralPath $outside -Force -ErrorAction SilentlyContinue
    # A file that is not there (yet) in a watched folder is accepted, and fails later.
    $missing = New-Payload 'Recorded.Weir.D.mkv' 'Recorded missing file' ([guid]::NewGuid().ToString())
    Remove-Item -LiteralPath $missing.sourcePath -Force
    $missingAccepted = Submit $missing
    Save-State weir $ver 'handoff-accepted-missing-file' 'A hand-off naming a file that is not in the watched folder (yet): Weir accepts it.' @($missingAccepted)
    $seenMissing = Wait-Handoff ([string]$missing.handoffId) 'missing file'
    foreach ($name in $seenMissing.Keys) { Save-State weir $ver "missing-file-status-$($name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')" "The status of that hand-off while it was $name." @($seenMissing[$name]) }
    $unknownId = [guid]::NewGuid().ToString()
    Save-State weir $ver 'no-record' 'A status poll and an outcome report for a hand-off id Weir never had.' @((Status $unknownId), (Outcome $unknownId 'imported' 'C:\x.mkv' $null))

    # Completed without an output path (#1232): the same source sent twice at once. The first is cleaned; for the second Weir
    # says it already cleaned this file and skips it, so it finishes with no second output.
    $idR1 = [guid]::NewGuid().ToString()
    $idR2 = [guid]::NewGuid().ToString()
    $payloadR1 = New-Payload 'Recorded.Weir.R.mkv' 'Big Buck Bunny (2008) Recorded R' $idR1
    $payloadR2 = [ordered]@{} + $payloadR1
    $payloadR2.handoffId = $idR2
    $payloadR2.queueItemId = [guid]::NewGuid().ToString()
    $null = Submit $payloadR1
    $resent = Submit $payloadR2
    Save-State weir $ver 'handoff-resend-accepted' 'The same source sent again while the first send was still in Weir: Weir accepts it.' @($resent)
    $seenR1 = Wait-Handoff $idR1 'resend first'
    $seenR2 = Wait-Handoff $idR2 'resend second'
    foreach ($name in $seenR2.Keys) { Save-State weir $ver "resend-status-$($name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')" "The status of the resent hand-off while it was $name." @($seenR2[$name]) }
    $lastR1 = Status $idR1
    $lastR2 = Status $idR2
    $withoutPath = @($lastR1, $lastR2) | Where-Object { $j = Get-ReplyJson $_; $j.state -eq 'completed' -and -not $j.outputPath } | Select-Object -First 1
    if ($withoutPath) {
        Save-State weir $ver 'completed-without-output-path' 'The status of a hand-off that finished with no output path: Weir had already cleaned this file once.' @($withoutPath)
        $jp = Get-ReplyJson $withoutPath
        Save-State weir $ver 'outcome-delivered-without-output-path' 'The outcome reported for a hand-off that handed back no new file.' @((Outcome ([string]$jp.handoffId) 'imported' $payloadR1.sourcePath $null))
    } else {
        Save-State weir $ver 'completed-after-resend' 'Both sends of the same source finished with an output path (Weir did not skip the second).' @($lastR1, $lastR2)
        Add-Missing 'weir completed-without-output-path: sending one source twice at once did not make Weir skip the second (#1232 needs Weir busy with the first when the resend arrives)'
    }

    # What this run left in the VM: our copies of the film in the watched folder, and the cleaned files Weir wrote for them, which
    # Deluno would otherwise list as outputs it cannot match.
    foreach ($copy in $script:WeirCopies) {
        Remove-Item -LiteralPath $copy -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $movie.output_folder -File -Filter ([IO.Path]::GetFileNameWithoutExtension($copy) + '*') -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    # Weir's own logs, kept beside the recordings.
    $logDir = 'C:\ProgramData\Weir\logs'
    if (Test-Path $logDir) {
        foreach ($f in Get-ChildItem $logDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 5) {
            $lines = Get-Content -LiteralPath $f.FullName -Tail 600 -ErrorAction SilentlyContinue
            $script:Logs[$f.Name] = ($lines -join "`n")
        }
    } else { Add-Missing "weir logs: $logDir does not exist in the VM" }
}
