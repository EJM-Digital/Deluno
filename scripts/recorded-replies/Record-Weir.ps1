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
    if (-not $p.WeirSecret) { Add-Missing 'weir: pass -WeirSecret (the webhook secret Weir shares with Deluno); nothing of Weir was recorded'; return }
    $headers = @{ 'X-Webhook-Secret' = $p.WeirSecret }
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

    $libraryId = @((Get-ReplyJson $folders)) | ForEach-Object { $_ } | Where-Object { $_.manager_library_key } | Select-Object -First 1 -ExpandProperty manager_library_key
    if (-not $libraryId) { $libraryId = 'recorded-library' }

    function New-Payload($copyName, $title, $handoffId) {
        $copy = New-WorkFile 'weir' $copyName $p.VmMediaFile
        [ordered]@{
            eventType = 'deluno.processor-handoff'; handoffId = $handoffId; libraryId = $libraryId; mediaType = 'movie'
            sourcePath = $copy; releaseName = $title; queueItemId = [guid]::NewGuid().ToString(); callbackPath = '/api/integrations/processors/events'
            title = $title; year = 2008
        }
    }
    function Wait-Handoff($id, $label) {
        $seen = [ordered]@{}
        $end = (Get-Date).AddMinutes(15)
        do {
            $s = Status $id
            $j = Get-ReplyJson $s
            $value = if ($j.state) { [string]$j.state } elseif ($j.status) { [string]$j.status } else { "http-$($s.reply.status)" }
            if (-not $seen.Contains($value)) { $seen[$value] = $s; Write-Host "weir hand-off $label is $value" }
            if ($value -match '^(completed|failed|cancelled|canceled|rejected|http-4)') { break }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $end)
        $seen
    }

    # A: accepted, polled through each state, completed, then the outcomes.
    $idA = [guid]::NewGuid().ToString()
    $payloadA = New-Payload 'Recorded.Weir.A.mkv' 'Big Buck Bunny (2008) Recorded A' $idA
    $accepted = Submit $payloadA
    Save-State weir $ver 'handoff-accepted' 'A hand-off submitted for a copy of a real film: Weir accepts it.' @($accepted)
    $early = Outcome $idA 'imported' 'C:\golden\recorded\weir\imported\x.mkv' $null
    $earlyCode = (Get-ReplyJson $early).code
    Save-State weir $ver $(if ($earlyCode) { "outcome-$($earlyCode -replace '_', '-')" } else { "outcome-before-finished-$($early.reply.status)" }) 'The outcome reported straight after the hand-off was accepted, before Weir had finished: Weir answers 409 handoff_not_finished.' @($early)
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

    # B: cancelled, then its outcome: the hand-off is over and handed back no file.
    $idB = [guid]::NewGuid().ToString()
    $payloadB = New-Payload 'Recorded.Weir.B.mkv' 'Big Buck Bunny (2008) Recorded B' $idB
    $null = Submit $payloadB
    $cancel = Send-Recorded -Client $w -Method DELETE -Path "/api/v1/intake/handoffs/$source/$idB" -Headers $headers
    Save-State weir $ver 'handoff-cancelled' 'A hand-off cancelled by Deluno (DELETE) right after it was accepted.' @($cancel, (Status $idB))
    $ended = Outcome $idB 'not-imported' $null 'Recorded: the hand-off was cancelled'
    $endedCode = (Get-ReplyJson $ended).code
    Save-State weir $ver $(if ($endedCode) { "outcome-$($endedCode -replace '_', '-')" } else { "outcome-after-cancel-$($ended.reply.status)" }) 'The outcome reported for a hand-off that was cancelled: Weir answers 409 handoff_ended.' @($ended)

    # Refused hand-offs and ids Weir has no record of.
    $bad = New-Payload 'Recorded.Weir.C.mkv' 'Recorded refused' ([guid]::NewGuid().ToString())
    $refusedSecret = Submit $bad @{ 'X-Webhook-Secret' = 'not-the-secret' }
    Save-State weir $ver 'handoff-refused-wrong-secret' 'A hand-off sent with a wrong secret.' @($refusedSecret)
    $noPath = [ordered]@{ eventType = 'deluno.processor-handoff'; handoffId = [guid]::NewGuid().ToString(); libraryId = $libraryId; mediaType = 'movie' }
    Save-State weir $ver 'handoff-refused-no-source' 'A hand-off with no source path.' @((Submit $noPath))
    $missing = New-Payload 'Recorded.Weir.D.mkv' 'Recorded missing file' ([guid]::NewGuid().ToString())
    $missing.sourcePath = 'C:\golden\recorded\weir\does-not-exist.mkv'
    Save-State weir $ver 'handoff-refused-missing-file' 'A hand-off naming a file that is not there.' @((Submit $missing))
    $unknownId = [guid]::NewGuid().ToString()
    Save-State weir $ver 'no-record' 'A status poll and an outcome report for a hand-off id Weir never had.' @((Status $unknownId), (Outcome $unknownId 'imported' 'C:\x.mkv' $null))
    Add-Missing 'weir completed-without-output-path: needs a film Weir leaves exactly as it was (check completed-with-output-path above; if it shows an output path, record one more hand-off with such a film)'

    # Weir's own logs, kept beside the recordings.
    $logDir = 'C:\ProgramData\Weir\logs'
    if (Test-Path $logDir) {
        foreach ($f in Get-ChildItem $logDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 5) {
            $lines = Get-Content -LiteralPath $f.FullName -Tail 600 -ErrorAction SilentlyContinue
            $script:Logs[$f.Name] = ($lines -join "`n")
        }
    } else { Add-Missing "weir logs: $logDir does not exist in the VM" }
}
