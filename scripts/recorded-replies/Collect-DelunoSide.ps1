# Turns what Start-RecordingProxy.ps1 wrote into Deluno-side recordings (#1237). Runs INSIDE the VM after Record.Lib.ps1.
# app "deluno": Deluno's hand-off submissions to Weir, its outcome posts, and its replies to Weir's library-folders and
# events calls. Weir's tests replay them. Each state keeps up to -PerState distinct exchanges.

function Invoke-CollectDelunoSide($p) {
    $dir = 'C:\golden\recorded\proxy'
    if (-not (Test-Path $dir)) { Add-Missing 'deluno side: no proxy output at C:\golden\recorded\proxy. Run Start-RecordingProxy.ps1 in front of Weir and in front of Deluno during a golden hand-off, then run this again.'; return }
    $rows = @(Get-ChildItem $dir -Filter '*.jsonl' | ForEach-Object { Get-Content -LiteralPath $_.FullName | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } })
    if ($rows.Count -eq 0) { Add-Missing 'deluno side: the proxy files are empty; nothing crossed the proxies'; return }
    $version = $p.DelunoVersion
    $classes = [ordered]@{
        'handoff-submission'      = { param($r) $r.side -eq 'weir-bound' -and $r.request.method -eq 'POST' -and $r.request.path -match '/intake/webhook/' }
        'outcome-post'            = { param($r) $r.side -eq 'weir-bound' -and $r.request.method -eq 'POST' -and $r.request.path -match '/outcome$' }
        'handoff-status-ask'      = { param($r) $r.side -eq 'weir-bound' -and $r.request.method -eq 'GET' -and $r.request.path -match '/intake/handoffs/' }
        'handoff-cancel'          = { param($r) $r.side -eq 'weir-bound' -and $r.request.method -eq 'DELETE' }
        'capabilities-ask'        = { param($r) $r.side -eq 'weir-bound' -and $r.request.path -match '/intake/capabilities' }
        'library-folders-ask'     = { param($r) $r.side -eq 'weir-bound' -and $r.request.path -match '/intake/library-folders' }
        'library-folders-answer'  = { param($r) $r.side -eq 'deluno-bound' -and $r.request.path -match 'library-folders|download-destinations' }
        'events-callback'         = { param($r) $r.side -eq 'deluno-bound' -and $r.request.path -match '/processors/events' }
    }
    foreach ($name in $classes.Keys) {
        $hit = @($rows | Where-Object { & $classes[$name] $_ } | Select-Object -First $p.PerState)
        if ($hit.Count -eq 0) { Add-Missing "deluno $name : never crossed the proxies in this run"; continue }
        $exchanges = $hit | ForEach-Object {
            [pscustomobject]@{
                request = [ordered]@{ method = $_.request.method; path = $_.request.path; headers = $_.request.headers; bodyText = $_.request.bodyText }
                reply = [ordered]@{ status = $_.reply.status; headers = $_.reply.headers; bodyText = $_.reply.bodyText }
            }
        }
        Save-State deluno $version $name "Recorded by a proxy while the real Deluno and the real Weir talked ($($hit[0].side))." @($exchanges)
    }
}
