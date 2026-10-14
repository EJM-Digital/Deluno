# Records the reports Weir sends to Deluno, and the Weir states that go with them, on the golden VM (#1239, #1262).
# Runs INSIDE the VM after Record.Lib.ps1, Record.Secrets.ps1 and Record-Weir.ps1. Windows PowerShell 5.1.
#
# Why a proxy: Weir reports a finished hand-off by POSTing to the "Deluno" connection it holds, so the only way to see the real
# report is to stand between Weir and Deluno. This script starts a pass-through listener on a free port, points Weir's connection
# to Deluno at it (Weir's own admin API, signed in with the throwaway login the scenario suite made; C:\golden\suite\secrets.json),
# and puts the connection back when it ends, even on an error (the original address is also written to
# C:\golden\recorded\proxy\weir-connection-original.txt first, so a run that is killed can be undone by hand).
# Every request and reply is kept. Real Deluno answers every report, except where a scenario needs a different answer (a 503, a 401,
# a 403, or an acceptance for a reject), and then the recording's note says exactly what the proxy answered and what Deluno said.
#
# The hand-offs are made by this script straight to Weir's intake API (as Record-WeirStates.ps1 does), so Deluno has no row for
# them: where it forwards a report to real Deluno it answers 409 "could not match this processor event". The Weir states and the
# bodies of the reports are Weir's own; Deluno's answers to these reports are NOT what a known hand-off gets (those are in
# deluno/1.0.0-rc.30.30/events-*), and each note says so.
#
# Hand-offs name hard links of (or copies of) a real public film, the workflow stays linked so Weir keeps the original, and
# everything this makes in Weir's watched and output folders is removed at the end. Every workflow setting it changes (the
# collision policy, the failure policy) is put back.
#
# What it records:
#   app weir    resend-takeover-*, output-collision-<policy>-*, failed-rejected-status, completed-season-pack-status,
#               failed-partial-season-pack-status                                          (the status polls of those hand-offs)
#   app deluno  events-superseded, events-completed-after-takeover, events-completed-collision-<policy>-first/second,
#               events-failed-rejected, events-completed-season-pack, events-failed-partial-season-pack,
#               events-completed-repeated-after-503, events-answered-401-not-repeated, events-answered-403-not-repeated

$script:RW = $null

# ----------------------------------------------------------------- the proxy (a runspace, so the main script keeps working)

function Start-RwProxy([int] $Port, [string] $Target) {
    $state = [hashtable]::Synchronized(@{
        Rows = (New-Object System.Collections.ArrayList); Faults = (New-Object System.Collections.ArrayList)
        Listener = $null; Error = $null
    })
    $runspace = [runspacefactory]::CreateRunspace(); $runspace.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $runspace
    $null = $ps.AddScript({
        param($state, $port, $target)
        try {
            Add-Type -AssemblyName System.Net.Http
            $listener = New-Object System.Net.HttpListener
            $listener.Prefixes.Add("http://127.0.0.1:$port/")
            $listener.Start()
            $state.Listener = $listener
            $http = New-Object System.Net.Http.HttpClient
            $http.Timeout = [TimeSpan]::FromMinutes(5)
            $keep = @('content-type', 'idempotency-key', 'accept')
            $skip = @('host', 'content-length', 'content-type', 'connection', 'expect', 'transfer-encoding', 'accept-encoding', 'keep-alive', 'proxy-connection')
            while ($listener.IsListening) {
                $context = $listener.GetContext()
                $req = $context.Request
                $reader = New-Object IO.StreamReader($req.InputStream, [Text.Encoding]::UTF8)
                $body = $reader.ReadToEnd()
                $path = $req.Url.PathAndQuery
                # A queued fault answers the first matching request: Status/Body/ContentType are what Weir is told; Forward also sends the
                # request on to real Deluno so its answer is kept beside it.
                $fault = $null
                foreach ($f in @($state.Faults)) {
                    if (-not $f.Used -and $req.HttpMethod -eq $f.Method -and $path -match $f.PathPattern) { $f.Used = $true; $fault = $f; break }
                }
                $real = $null
                if (-not $fault -or $fault.Forward) {
                    $forward = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($req.HttpMethod), ($target.TrimEnd('/') + $path))
                    if ($body.Length -gt 0) { $forward.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, $(if ($req.ContentType) { ($req.ContentType -split ';')[0] } else { 'application/json' })) }
                    foreach ($name in $req.Headers.AllKeys) { if ($skip -notcontains $name.ToLowerInvariant()) { $null = $forward.Headers.TryAddWithoutValidation($name, $req.Headers[$name]) } }
                    try {
                        $resp = $http.SendAsync($forward).GetAwaiter().GetResult()
                        $real = @{ Status = [int]$resp.StatusCode; Text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                                   Type = $(if ($resp.Content.Headers.ContentType) { $resp.Content.Headers.ContentType.ToString() } else { 'application/json' }) }
                    } catch { $real = @{ Status = 502; Text = ''; Type = 'text/plain' } }
                }
                if ($fault) { $status = [int]$fault.Status; $text = [string]$fault.Body; $ctype = [string]$fault.ContentType } else { $status = $real.Status; $text = $real.Text; $ctype = $real.Type }
                $bytes = [Text.Encoding]::UTF8.GetBytes($text)
                $context.Response.StatusCode = $status
                if ($ctype) { $context.Response.ContentType = $ctype }
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
                $reqHeaders = [ordered]@{}
                foreach ($name in $req.Headers.AllKeys) { if ($keep -contains $name.ToLowerInvariant()) { $reqHeaders[$name.ToLowerInvariant()] = $req.Headers[$name] } }
                $row = @{
                    at = [DateTime]::UtcNow; method = $req.HttpMethod; path = $path; reqHeaders = $reqHeaders; bodyText = $body
                    status = $status; replyType = $ctype; replyText = $text; injected = [bool]$fault
                    realStatus = $(if ($real) { $real.Status } else { $null }); realText = $(if ($real) { $real.Text } else { $null }); fault = $(if ($fault) { $fault.Name } else { $null })
                }
                $null = $state.Rows.Add($row)
            }
        } catch { if ($state.Listener -and $state.Listener.IsListening) { $state.Error = $_.Exception.Message } }
    }).AddArgument($state).AddArgument($Port).AddArgument($Target)
    $handle = $ps.BeginInvoke()
    $null = Wait-Until -Seconds 15 -EverySeconds 1 { $state.Listener -or $state.Error }
    if (-not $state.Listener) { throw "The report proxy did not start: $($state.Error)" }
    [pscustomobject]@{ State = $state; Ps = $ps; Runspace = $runspace; Handle = $handle; Port = $Port }
}

function Stop-RwProxy($Proxy) {
    if (-not $Proxy) { return }
    try { $Proxy.State.Listener.Stop() } catch { }
    try { $Proxy.Ps.Stop() } catch { }
    try { $Proxy.Runspace.Close() } catch { }
}

# Queues a fault: the next <Method> whose path matches <PathPattern> is answered as given.
function Add-RwFault($Proxy, [string] $Name, [int] $Status, [string] $Body = '', [string] $ContentType = 'application/json; charset=utf-8', [switch] $Forward, [string] $Method = 'POST', [string] $PathPattern = '/processors/events$') {
    $null = $Proxy.State.Faults.Add(@{ Name = $Name; Status = $Status; Body = $Body; ContentType = $ContentType; Forward = [bool]$Forward; Method = $Method; PathPattern = $PathPattern; Used = $false })
}

# The reports (POSTs to the events route) whose body names one of the hand-off ids, newest last.
function Get-RwReports([string[]] $Ids) {
    @($script:RW.Proxy.State.Rows.ToArray() | Where-Object {
        $row = $_
        $row.method -eq 'POST' -and $row.path -match '/processors/events$' -and (@($Ids | Where-Object { $_ -and $row.bodyText -like "*$_*" }).Count -gt 0)
    })
}

function Wait-RwReports([string[]] $Ids, [int] $Count = 1, [int] $Seconds = 90) {
    $found = $null
    $null = Wait-Until -Seconds $Seconds -EverySeconds 2 { $r = Get-RwReports $Ids; if ($r.Count -ge $Count) { $script:RwFound = $r; $true } }
    if ($script:RwFound) { $found = $script:RwFound; $script:RwFound = $null; return $found }
    Get-RwReports $Ids
}

function ConvertTo-RwExchange($Row) {
    [pscustomobject]@{
        request = [ordered]@{ method = $Row.method; path = $Row.path; headers = $Row.reqHeaders; bodyText = $Row.bodyText }
        reply = [ordered]@{ status = $Row.status; headers = [ordered]@{ 'content-type' = $Row.replyType }; bodyText = $Row.replyText }
    }
}

# What real Deluno said when the proxy gave Weir another answer, in words for a note.
function Get-RwRealNote($Rows) {
    $parts = @()
    foreach ($row in $Rows) {
        if ($row.injected) {
            $said = if ($null -ne $row.realStatus) { "real Deluno, asked as well, said $($row.realStatus)" } else { 'the request was not passed on to Deluno' }
            $parts += "the proxy answered $($row.status) itself ($($row.fault)); $said"
        } else { $parts += "Deluno answered $($row.status) (it has no row for this hand-off, as the script made it straight to Weir)" }
    }
    $parts -join '; '
}

# ----------------------------------------------------------------- Weir's admin API (the throwaway login the suite made)

function Get-RwSuiteLogin {
    $file = 'C:\golden\suite\secrets.json'
    if (-not (Test-Path $file)) { return $null }
    $s = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json
    if (-not $s.'weir-username' -or -not $s.'weir-password') { return $null }
    Register-Secret ([string]$s.'weir-password'); Register-Secret ([string]$s.'weir-username')
    [pscustomobject]@{ User = [string]$s.'weir-username'; Password = [string]$s.'weir-password' }
}

function Connect-RwWeir {
    $login = $script:RW.Login
    $origin = $script:RW.WeirUrl
    $headers = @{ Origin = $origin; 'X-Requested-With' = 'XMLHttpRequest' }
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $csrf = (Invoke-RestMethod -Uri "$origin/api/v1/auth/csrf" -WebSession $session -UseBasicParsing -TimeoutSec 20).csrf_token
    $body = @{ username = $login.User; password = $login.Password; csrf_token = $csrf } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "$origin/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body $body -WebSession $session -Headers $headers -UseBasicParsing -TimeoutSec 20
    $script:RW.Session = $session
}

function Invoke-RwWeir([string] $Method, [string] $Path, $Body) {
    if (-not $script:RW.Session) { Connect-RwWeir }
    $origin = $script:RW.WeirUrl
    $p = @{ Uri = "$origin/api/v1$Path"; Method = $Method; WebSession = $script:RW.Session; UseBasicParsing = $true; TimeoutSec = 60; Headers = @{ Origin = $origin; 'X-Requested-With' = 'XMLHttpRequest' } }
    if ($Method -ne 'GET') {
        $csrf = (Invoke-RestMethod -Uri "$origin/api/v1/auth/csrf" -WebSession $script:RW.Session -UseBasicParsing -TimeoutSec 20).csrf_token
        $payload = @{}
        if ($Body) { foreach ($k in $Body.Keys) { $payload[$k] = $Body[$k] } }
        $payload['csrf_token'] = $csrf
        $p.ContentType = 'application/json'; $p.Body = ($payload | ConvertTo-Json -Depth 8)
    }
    try { Invoke-RestMethod @p } catch {
        $detail = ''
        try { $detail = $_.ErrorDetails.Message } catch { }
        throw (Remove-KnownSecrets ("Weir answered $Method $Path with: $($_.Exception.Message) $detail"))
    }
}

# The fields a library PUT accepts (Weir refuses any other), taken from what GET returns.
$script:RwLibraryFields = 'name media_type enabled watched_folder work_folder output_folder media_extensions_csv exclude_markers_csv include_patterns_csv exclude_patterns_csv min_file_size_mb max_file_size_mb rejected_file_action ready_after_seconds created_after created_before modified_after modified_before exclude_hidden top_level_only sidecar_patterns_csv preserve_original_timestamps output_collision_policy ffmpeg_strictness remux_writer scan_interval_seconds ignore_size_changes skip_access_tests max_attempts retry_backoff_seconds retry_execution_failures failure_policy retry_preflight_failures schedule_grid file_system_events_enabled schedule_enabled schedule_hours_limited schedule_days schedule_start schedule_end max_concurrent_files priority rule_set_id manager_connection_ids remove_original_after_success minimum_free_disk_space_mb' -split ' '

function Get-RwLibraryBody($Library, [hashtable] $Changes = @{}) {
    $body = @{}
    foreach ($field in $script:RwLibraryFields) {
        if ($Library.PSObject.Properties.Name -contains $field) { $body[$field] = $Library.$field }
    }
    foreach ($k in $Changes.Keys) { $body[$k] = $Changes[$k] }
    $body
}

# Changes a library's settings; the original is kept so Restore-RwLibraries can put it back.
function Set-RwLibrary($Library, [hashtable] $Changes) {
    if (-not $script:RW.Restore.ContainsKey($Library.id)) { $script:RW.Restore[$Library.id] = $Library }
    $null = Invoke-RwWeir 'PUT' "/processing/libraries/$($Library.id)" (Get-RwLibraryBody $Library $Changes)
}

function Restore-RwLibraries {
    foreach ($id in @($script:RW.Restore.Keys)) {
        try { $null = Invoke-RwWeir 'PUT' "/processing/libraries/$id" (Get-RwLibraryBody $script:RW.Restore[$id]) }
        catch { Add-Missing "weir reports: could not put library $id back as it was ($($_.Exception.Message)); check its collision and failure policy" }
    }
    $script:RW.Restore = @{}
}

function Get-RwLibrary([string] $MediaType) {
    $all = Invoke-RwWeir 'GET' '/processing/libraries'
    $list = if ($all.libraries) { @($all.libraries) } elseif ($all.items) { @($all.items) } else { @($all) }
    $list | Where-Object { $_.media_type -eq $MediaType -and $_.enabled } | Select-Object -First 1
}

# ----------------------------------------------------------------- hand-off helpers

function New-RwPayload($Library, [string] $MediaType, [string] $SourcePath, [string] $Title, [string] $HandoffId) {
    $payload = [ordered]@{
        eventType = 'deluno.processor-handoff'; handoffId = $HandoffId; libraryId = $Library.Key; mediaType = $MediaType
        sourcePath = $SourcePath; releaseName = $Title; queueItemId = [guid]::NewGuid().ToString(); callbackPath = '/api/integrations/processors/events'
        title = $Title; year = 2008
    }
    if ($MediaType -eq 'tv') { $payload.season = 1 }
    $payload
}

function Send-RwHandoff($Payload) {
    Send-Recorded -Client $script:RW.W -Method POST -Path "/api/v1/intake/webhook/$($script:RW.Source)" -Headers $script:RW.Headers -Json $Payload
}

function Get-RwStatus([string] $Id) {
    Send-Recorded -Client $script:RW.W -Path "/api/v1/intake/handoffs/$($script:RW.Source)/$Id" -Headers $script:RW.Headers
}

function Get-RwState($Exchange) { $j = Get-ReplyJson $Exchange; if ($j.state) { [string]$j.state } else { "http-$($Exchange.reply.status)" } }

# Polls quickly and keeps the first reply seen in each state, until the hand-off leaves queued and working.
function Watch-RwHandoff([string] $Id, [int] $Seconds = 150) {
    $seen = [ordered]@{}
    $end = (Get-Date).AddSeconds($Seconds)
    do {
        $s = Get-RwStatus $Id
        $state = Get-RwState $s
        if (-not $seen.Contains($state)) { $seen[$state] = $s }
        if ($state -notmatch '^(queued|working|scheduled)$') { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $end)
    $seen
}

function Save-RwWatched($Seen, [string] $Prefix, [string] $What) {
    foreach ($name in $Seen.Keys) { Save-State weir $script:RW.Ver "$Prefix-$($name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')" "The status of $What while it was $name." @($Seen[$name]) }
}

# A hard link of the film (or a copy where a link is not possible) at the path.
function New-RwLink([string] $Path) {
    $dir = Split-Path $Path
    if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force; $script:RW.Made.Add($dir) }
    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    try { $null = New-Item -ItemType HardLink -Path $Path -Target $script:RW.Film -ErrorAction Stop } catch { Copy-Item -LiteralPath $script:RW.Film -Destination $Path -Force }
    $script:RW.Made.Add($Path)
}

# The first <Bytes> of the film as a file of its own: a truncated Matroska file, different from the film in size and content.
function New-RwTruncated([string] $Path, [long] $Bytes) {
    $dir = Split-Path $Path
    if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force; $script:RW.Made.Add($dir) }
    $in = [IO.File]::OpenRead($script:RW.Film)
    try {
        $out = [IO.File]::Create($Path)
        try {
            $buffer = New-Object byte[] (4MB)
            $left = $Bytes
            while ($left -gt 0) { $n = $in.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left)); if ($n -le 0) { break }; $out.Write($buffer, 0, $n); $left -= $n }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
    $script:RW.Made.Add($Path)
}

# A file that opens as a film and then turns to noise: the first megabyte of the film, then random bytes, to <Bytes> in all.
function New-RwCorrupt([string] $Path, [long] $Bytes) {
    $dir = Split-Path $Path
    if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force; $script:RW.Made.Add($dir) }
    $in = [IO.File]::OpenRead($script:RW.Film)
    try {
        $out = [IO.File]::Create($Path)
        try {
            $head = New-Object byte[] (1MB); $n = $in.Read($head, 0, $head.Length); $out.Write($head, 0, $n)
            $random = New-Object byte[] (1MB); $rng = New-Object Random 1262
            $left = $Bytes - $n
            while ($left -gt 0) { $rng.NextBytes($random); $take = [int][Math]::Min($random.Length, $left); $out.Write($random, 0, $take); $left -= $take }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
    $script:RW.Made.Add($Path)
}

function Get-RwOutputNames([string] $Folder, [string] $Like) {
    @(Get-ChildItem -LiteralPath $Folder -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like $Like } | ForEach-Object { $_.FullName.Substring($Folder.Length).TrimStart('\') })
}

# Hands a source to Weir, waits for the end, and returns what happened. The reports the proxy saw for it are in .Reports.
function Invoke-RwHandoff($Library, [string] $MediaType, [string] $Source, [string] $Title, [int] $ExpectReports = 1, [int] $ReportSeconds = 90) {
    $id = [guid]::NewGuid().ToString()
    $accepted = Send-RwHandoff (New-RwPayload $Library $MediaType $Source $Title $id)
    $seen = Watch-RwHandoff $id
    $last = Get-RwStatus $id
    $reports = Wait-RwReports @($id) $ExpectReports $ReportSeconds
    [pscustomobject]@{ Id = $id; Accepted = $accepted; Seen = $seen; Last = $last; State = (Get-RwState $last); Reports = $reports }
}

function Get-RwBody($Row) { try { $Row.bodyText | ConvertFrom-Json } catch { $null } }

# ----------------------------------------------------------------- the scenarios

function Invoke-RwTakeover($Movies) {
    Write-RunLog 'weir reports: takeover'
    # The same source sent twice at once: the second send takes over the first while Weir has it queued or is working on it.
    $file = Join-Path $Movies.Watched 'Recorded.Weir.Takeover.mkv'
    New-RwLink $file
    $idA = [guid]::NewGuid().ToString(); $idB = [guid]::NewGuid().ToString()
    $payloadA = New-RwPayload $Movies 'movie' $file 'Big Buck Bunny (2008) Recorded Takeover' $idA
    $payloadB = [ordered]@{} + $payloadA; $payloadB.handoffId = $idB; $payloadB.queueItemId = [guid]::NewGuid().ToString()
    $null = Send-RwHandoff $payloadA
    $resent = Send-RwHandoff $payloadB
    Save-State weir $script:RW.Ver 'resend-takeover-accepted' 'The same single file sent again while Weir still had the first send (queued or working): Weir accepts the second.' @($resent)
    $seenA = Watch-RwHandoff $idA; $seenB = Watch-RwHandoff $idB
    Save-RwWatched $seenB 'resend-takeover-second-status' 'the second send of a file that was sent twice at once'
    $lastA = Get-RwStatus $idA; $lastB = Get-RwStatus $idB
    Save-State weir $script:RW.Ver 'resend-takeover-first-status' "The final status of the FIRST send of a file that was sent twice at once: $(Get-RwState $lastA)." @($lastA)
    Save-State weir $script:RW.Ver 'resend-takeover-second-final-status' "The final status of the SECOND send of a file that was sent twice at once: $(Get-RwState $lastB); output path: $(if ((Get-ReplyJson $lastB).outputPath) { 'present' } else { 'none' })." @($lastB)
    # The shape #1262 needs is completed with no output path.
    if ((Get-ReplyJson $lastB).outputPath -or (Get-RwState $lastB) -ne 'completed') { Add-Missing "weir reports takeover: the second send ended $(Get-RwState $lastB) with $(if ((Get-ReplyJson $lastB).outputPath) { 'an output path' } else { 'no output path' }), not completed without one (the file may have been processed before the resend arrived; run again)" }
    $reports = Wait-RwReports @($idA, $idB) 2 90
    foreach ($row in $reports) {
        $body = Get-RwBody $row
        $isFirst = $row.bodyText -like "*$idA*"
        $name = if ($isFirst) { 'events-superseded' } elseif ($body.status -eq 'completed') { 'events-completed-after-takeover' } else { "events-$($body.status)-after-takeover" }
        Save-State deluno $script:RW.DelunoVersion $name "The report Weir sent for the $(if ($isFirst) { 'first' } else { 'second' }) of two sends of one file (status $($body.status)). $(Get-RwRealNote @($row))." @((ConvertTo-RwExchange $row))
    }
    if ($reports.Count -lt 2) { Add-Missing "weir reports takeover: $($reports.Count) of the 2 expected reports reached Deluno" }
}

function Invoke-RwCollision($Movies) {
    # Two different single files with one name, one after the other, the first left in the output folder (#1262). Weir's collision policy
    # decides: "replace" (the default) writes over it, "keep_both" writes the second beside it under a numbered name.
    $library = Get-RwLibrary 'movie'
    foreach ($policy in 'replace', 'keep_both') {
        Write-RunLog "weir reports: collision $policy"
        if ($library.output_collision_policy -ne $policy) { Set-RwLibrary $library @{ output_collision_policy = $policy } }
        $tag = $policy -replace '_', '-'
        $nameA = Join-Path (Join-Path $Movies.Watched "RecA$tag") 'Recorded.Weir.Collide.mkv'
        $nameB = Join-Path (Join-Path $Movies.Watched "RecB$tag") 'Recorded.Weir.Collide.mkv'
        New-RwLink $nameA
        New-RwTruncated $nameB ((Get-Item -LiteralPath $script:RW.Film).Length - 1MB)
        $first = Invoke-RwHandoff $Movies 'movie' $nameA 'Big Buck Bunny (2008) Recorded Collide'
        $afterFirst = Get-RwOutputNames $Movies.Output 'Recorded.Weir.Collide*'
        $second = Invoke-RwHandoff $Movies 'movie' $nameB 'Big Buck Bunny (2008) Recorded Collide'
        $afterSecond = Get-RwOutputNames $Movies.Output 'Recorded.Weir.Collide*'
        $outputs = "The movie output folder held [$($afterFirst -join ', ')] after the first and [$($afterSecond -join ', ')] after the second."
        Save-State weir $script:RW.Ver "output-collision-$tag-first-status" "The status of the first of two different single files with the same name, collision policy $policy. $outputs" @($first.Last)
        Save-State weir $script:RW.Ver "output-collision-$tag-second-status" "The status of the second of two different single files with the same name, collision policy $policy. $outputs" @($second.Last)
        foreach ($pair in @(@('first', $first), @('second', $second))) {
            foreach ($row in $pair[1].Reports) {
                Save-State deluno $script:RW.DelunoVersion "events-completed-collision-$tag-$($pair[0])" "The report Weir sent for the $($pair[0]) of two different single files with the same name, collision policy $policy. $outputs $(Get-RwRealNote @($row))." @((ConvertTo-RwExchange $row))
            }
            if ($pair[1].Reports.Count -eq 0) { Add-Missing "weir reports collision-$tag-$($pair[0]): no report reached Deluno" }
        }
        Get-ChildItem -LiteralPath $Movies.Output -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Recorded.Weir.Collide*' } | Remove-Item -Force -ErrorAction SilentlyContinue
        if (@($afterSecond | Where-Object { $_ -match '\(\d+\)\.mkv$' }).Count -eq 0) { Add-Missing "weir reports collision-${tag}: no numbered name appeared under policy $policy (the output folder held [$($afterSecond -join ', ')])" }
    }
}

function Invoke-RwSeasonPacks($Tv) {
    $library = Get-RwLibrary 'tv'
    $min = if ($library.min_file_size_mb) { [int]$library.min_file_size_mb } else { 50 }
    $filmSize = (Get-Item -LiteralPath $script:RW.Film).Length
    # completed, a season pack of two episodes
    Write-RunLog 'weir reports: season pack'
    $pack = Join-Path $Tv.Watched 'Recorded.Weir.Show.S01.1080p.WEB-DL.x264-GOLDEN'
    foreach ($e in 1, 2) { New-RwLink (Join-Path $pack ('Recorded.Weir.Show.S01E0{0}.1080p.WEB-DL.x264-GOLDEN.mkv' -f $e)) }
    $done = Invoke-RwHandoff $Tv 'tv' $pack 'Recorded Weir Show S01' 1 120
    Save-RwWatched $done.Seen 'completed-season-pack-status' 'a season-pack hand-off (two episodes)'
    foreach ($row in $done.Reports) { Save-State deluno $script:RW.DelunoVersion 'events-completed-season-pack' "The report Weir sent for a season-pack folder of two episodes (status $((Get-RwBody $row).status)). $(Get-RwRealNote @($row))." @((ConvertTo-RwExchange $row)) }
    if ($done.Reports.Count -eq 0) { Add-Missing 'weir reports completed season pack: no report reached Deluno' }
    Remove-RwOutput $Tv.Output 'Recorded*'

    # failed, with outputFiles and a count message: one episode that cannot be read, failure policy "hold"
    Write-RunLog 'weir reports: partial failure'
    if ($library.failure_policy -ne 'hold') { Set-RwLibrary $library @{ failure_policy = 'hold' } }
    $sizeBytes = [Math]::Min($filmSize, [long](($min + 10) * 1MB))
    foreach ($variant in 'corrupt', 'zero') {
        $pack2 = Join-Path $Tv.Watched "Recorded.Weir.Partial.$variant.S01.1080p.WEB-DL.x264-GOLDEN"
        foreach ($e in 1, 2) { New-RwLink (Join-Path $pack2 ('Recorded.Weir.Partial.S01E0{0}.1080p.WEB-DL.x264-GOLDEN.mkv' -f $e)) }
        $bad = Join-Path $pack2 'Recorded.Weir.Partial.S01E03.1080p.WEB-DL.x264-GOLDEN.mkv'
        if ($variant -eq 'corrupt') { New-RwCorrupt $bad $sizeBytes } else { $null = New-Item -ItemType File -Path $bad; $script:RW.Made.Add($bad) }
        $run = Invoke-RwHandoff $Tv 'tv' $pack2 "Recorded Weir Partial $variant S01" 1 120
        $withFiles = @($run.Reports | Where-Object { $b = Get-RwBody $_; $b.status -eq 'failed' -and @($b.outputFiles).Count -gt 0 })
        Save-RwWatched $run.Seen "failed-partial-season-pack-$variant-status" "a season-pack hand-off where one episode is $variant"
        foreach ($row in $run.Reports) {
            $b = Get-RwBody $row
            Save-State deluno $script:RW.DelunoVersion "events-$($b.status)-partial-season-pack-$variant" "The report Weir sent for a season pack of two good episodes and one $variant one, failure policy hold (status $($b.status), disposition $($b.disposition), $(@($b.outputFiles).Count) output files). $(Get-RwRealNote @($row))." @((ConvertTo-RwExchange $row))
        }
        Remove-RwOutput $Tv.Output 'Recorded*'
        if ($withFiles.Count -gt 0) {
            # The name the tests look for.
            Save-State deluno $script:RW.DelunoVersion 'events-failed-partial-season-pack' "The report Weir sent for a season pack of two good episodes and one $variant one, failure policy hold: failed, carrying outputFiles and a message that counts them. $(Get-RwRealNote @($withFiles[0]))." @((ConvertTo-RwExchange $withFiles[0]))
            break
        }
        if ($run.Reports.Count -eq 0) { Add-Missing "weir reports partial season pack ($variant): no report reached Deluno" }
        else { Add-Missing "weir reports partial season pack ($variant): the report was $((Get-RwBody $run.Reports[0]).status) with $(@((Get-RwBody $run.Reports[0]).outputFiles).Count) output files, not failed with outputFiles" }
    }
    if ($library.failure_policy -ne 'hold') { Restore-RwLibraries }
}

function Remove-RwOutput([string] $Folder, [string] $Like) {
    if ($Folder -and (Test-Path -LiteralPath $Folder)) { Get-ChildItem -LiteralPath $Folder -Force | Where-Object { $_.Name -like $Like } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
}

function Find-RwFfmpeg {
    $roots = @("$env:LOCALAPPDATA\Weir", 'C:\Program Files\Weir', 'C:\ProgramData\Weir', "$env:LOCALAPPDATA\Deluno", 'C:\ProgramData\Deluno', 'C:\golden') | Where-Object { Test-Path $_ }
    foreach ($r in $roots) { $f = Get-ChildItem -Path $r -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue | Select-Object -First 1; if ($f) { return $f.FullName } }
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $null
}

function Invoke-RwRejected($Movies) {
    Write-RunLog 'weir reports: rejected'
    $library = Get-RwLibrary 'movie'
    $ffmpeg = Find-RwFfmpeg
    if (-not $ffmpeg) { Add-Missing 'weir reports failed rejected: no ffmpeg.exe in the VM to make an audio-only .mkv; put one in C:\golden and run again'; return }
    $min = if ($library.min_file_size_mb) { [int]$library.min_file_size_mb } else { 50 }
    $seconds = [int][Math]::Ceiling((($min + 10) * 1MB) / 768000)
    $file = Join-Path $Movies.Watched 'Recorded.Weir.AudioOnly.mkv'
    $null = & $ffmpeg -y -loglevel error -f lavfi -i "sine=frequency=440:sample_rate=96000:duration=$seconds" -ac 2 -c:a pcm_s32le $file 2>&1
    if (-not (Test-Path -LiteralPath $file)) { Add-Missing 'weir reports failed rejected: ffmpeg did not make the audio-only file'; return }
    $script:RW.Made.Add($file)
    try { Set-RwLibrary $library @{ failure_policy = 'reject' } }
    catch { Add-Missing "weir reports failed rejected: Weir would not save the reject policy ($($_.Exception.Message))"; return }
    # Weir removes nothing until Deluno has accepted the report with a 2xx; these hand-offs are not Deluno's, so real Deluno would answer
    # 409. The proxy asks real Deluno as well (kept in the note) and tells Weir 202, the shape Deluno gives a report it takes.
    Add-RwFault $script:RW.Proxy 'accept-for-reject' 202 '{"status":"failed","importJobId":null}' -Forward
    $run = Invoke-RwHandoff $Movies 'movie' $file 'Big Buck Bunny (2008) Recorded AudioOnly' 1 120
    Save-RwWatched $run.Seen 'failed-rejected-status' 'a hand-off of an audio-only file under the reject failure policy'
    $rejected = @($run.Reports | Where-Object { (Get-RwBody $_).disposition -eq 'rejected' })
    foreach ($row in $run.Reports) {
        $b = Get-RwBody $row
        Save-State deluno $script:RW.DelunoVersion $(if ($b.disposition -eq 'rejected') { 'events-failed-rejected' } else { "events-$($b.status)-reject-policy-$($b.disposition)" }) "The report Weir sent for an audio-only file under the reject failure policy (status $($b.status), disposition $($b.disposition), sourceRemoved $($b.sourceRemoved)). $(Get-RwRealNote @($row))." @((ConvertTo-RwExchange $row))
    }
    if ($rejected.Count -eq 0) { Add-Missing "weir reports failed rejected: $($run.Reports.Count) report(s) arrived and none had disposition rejected (Weir ended $($run.State)); the reject policy needs Deluno's manifest to advertise processor-reject-regrab and the library to be linked" }
    Restore-RwLibraries
}

# The report for a finished single file is sent while the proxy answers as told; returns the hand-off and the reports.
function Invoke-RwFinishedWithFault($Movies, [string] $Label) {
    $file = Join-Path $Movies.Watched "Recorded.Weir.$Label.mkv"
    New-RwLink $file
    Invoke-RwHandoff $Movies 'movie' $file "Big Buck Bunny (2008) Recorded $Label" 1 90
}

function Invoke-RwRepeat($Movies) {
    Write-RunLog 'weir reports: repeat after 503'
    Add-RwFault $script:RW.Proxy 'busy-503' 503 'Service Unavailable' 'text/plain'
    $run = Invoke-RwFinishedWithFault $Movies 'Repeat503'
    # The heartbeat (every minute) asks Deluno, and once Deluno answers it Weir sends again what it owes.
    $reports = Wait-RwReports @($run.Id) 2 170
    if ($reports.Count -ge 2) {
        Save-State deluno $script:RW.DelunoVersion 'events-completed-repeated-after-503' "The same completed report twice: the proxy answered the first POST 503, and Weir sent it again on its next heartbeat (about $([int]($reports[1].at - $reports[0].at).TotalSeconds) seconds later). $(Get-RwRealNote @($reports[0], $reports[1]))." @((ConvertTo-RwExchange $reports[0]), (ConvertTo-RwExchange $reports[1]))
    } else { Add-Missing "weir reports completed repeated after 503: $($reports.Count) POST(s) arrived in 170 s; Weir did not send the report again" }
}

function Invoke-RwNotRepeated($Movies, [int] $Status, [string] $Body, [string] $Label) {
    Write-RunLog "weir reports: answered $Status"
    Add-RwFault $script:RW.Proxy "answer-$Status" $Status $Body $(if ($Body) { 'application/json; charset=utf-8' } else { 'text/plain' })
    $run = Invoke-RwFinishedWithFault $Movies $Label
    $first = @($run.Reports)
    if ($first.Count -eq 0) { Add-Missing "weir reports answered ${Status}: no report reached the proxy"; return }
    # Two heartbeats' worth: if Weir still owed the report it would send it again now.
    Start-Sleep -Seconds 130
    $after = @(Get-RwReports @($run.Id))
    $repeated = $after.Count - 1
    Save-State deluno $script:RW.DelunoVersion "events-answered-$Status-not-repeated" "A completed report the proxy answered $Status once. After waiting 130 s (two heartbeats) Weir had sent it $repeated more time(s)$(if ($repeated -eq 0) { ': a 4xx answer means Weir no longer owes the report' })." @((ConvertTo-RwExchange $after[0]))
    if ($repeated -gt 0) { Add-Missing "weir reports answered ${Status}: Weir sent the report $repeated more time(s) after the answer" }
}

# ----------------------------------------------------------------- the entry point

function Invoke-RecordWeirReports($p) {
    Assert-FreeSpace 8 'the Weir reports'
    $secret = Get-WeirWebhookSecret
    if (-not $secret) { Add-Missing 'weir reports: Deluno''s connection to Weir, and so its webhook secret, was not found in the VM'; return }
    $login = Get-RwSuiteLogin
    if (-not $login) { Add-Missing 'weir reports: C:\golden\suite\secrets.json has no Weir login, so Weir''s connection to Deluno cannot be pointed at the proxy. Set the VM up with the scenario suite''s Initialize-SuiteEnvironment first'; return }
    if (-not $p.DelunoVersion -or $p.DelunoVersion -eq 'current') { Add-Missing 'weir reports: pass -DelunoVersion <the version Deluno reports> so the reports are saved under it'; return }
    $film = $p.VmMediaFile
    if (-not (Test-Path -LiteralPath $film)) { Add-Missing "weir reports: the film $film is not in the VM"; return }

    $w = New-RecordedClient -BaseUrl $p.WeirUrl -KeepHeaders @('content-type')
    $headers = @{ 'X-Webhook-Secret' = $secret }
    $caps = Send-Recorded -Client $w -Path '/api/v1/intake/capabilities' -Headers $headers
    if ($caps.reply.status -ne 200) { Add-Missing "weir reports: capabilities answered $($caps.reply.status) at $($p.WeirUrl)"; return }
    $ver = if ((Get-ReplyJson $caps).version) { [string](Get-ReplyJson $caps).version } else { $p.WeirVersion }
    $folders = @((Get-ReplyJson (Send-Recorded -Client $w -Path '/api/v1/intake/library-folders' -Headers $headers)).libraries)
    function Pick($type) {
        $f = $folders | Where-Object { $_.media_type -eq $type } | Select-Object -First 1
        if (-not $f) { return $null }
        [pscustomobject]@{ Key = $(if ($f.manager_library_key) { [string]$f.manager_library_key } else { "recorded-$type" }); Watched = [string]$f.watched_folder; Output = [string]$f.output_folder }
    }
    $movies = Pick 'movie'; $tv = Pick 'tv'
    if (-not $movies -or -not $movies.Watched) { Add-Missing 'weir reports: Weir lists no watched folder for its movie workflow'; return }

    $script:RW = @{
        W = $w; Headers = $headers; Source = $p.WeirSource; Ver = $ver; DelunoVersion = $p.DelunoVersion; WeirUrl = $p.WeirUrl.TrimEnd('/'); Film = $film
        Login = $login; Session = $null; Restore = @{}; Made = (New-Object 'System.Collections.Generic.List[string]'); Proxy = $null
    }
    $proxyPort = 19000 + (Get-Random -Minimum 100 -Maximum 800)
    $connection = $null; $original = $null
    try {
        $all = Invoke-RwWeir 'GET' '/media-managers/connections'
        $list = if ($all.connections) { @($all.connections) } elseif ($all.items) { @($all.items) } else { @($all) }
        $connection = $list | Where-Object { $_.kind -match 'deluno' -or $_.base_url -match ":$($p.DelunoPort)" } | Select-Object -First 1
        if (-not $connection) { Add-Missing 'weir reports: Weir has no connection to Deluno to point at the proxy'; return }
        $original = [string]$connection.base_url
        New-Item -ItemType Directory -Force -Path 'C:\golden\recorded\proxy' | Out-Null
        Set-Content -LiteralPath 'C:\golden\recorded\proxy\weir-connection-original.txt' -Value "connection $($connection.id): $original" -Encoding ascii
        $script:RW.Proxy = Start-RwProxy $proxyPort $original
        $null = Invoke-RwWeir 'PUT' "/media-managers/connections/$($connection.id)" @{ base_url = "http://127.0.0.1:$proxyPort" }
        Write-RunLog "weir reports: Weir's Deluno connection $($connection.id) now points at the proxy on $proxyPort (was recorded in weir-connection-original.txt)"
        Start-Sleep -Seconds 3

        foreach ($step in 'takeover', 'collision', 'rejected', 'season packs', 'repeat after 503', 'answered 401', 'answered 403') {
            try {
                switch ($step) {
                    'takeover' { Invoke-RwTakeover $movies }
                    'collision' { Invoke-RwCollision $movies }
                    'rejected' { Invoke-RwRejected $movies }
                    'season packs' { if ($tv -and $tv.Watched) { Invoke-RwSeasonPacks $tv } else { Add-Missing 'weir reports season packs: Weir lists no watched folder for its TV workflow' } }
                    'repeat after 503' { Invoke-RwRepeat $movies }
                    'answered 401' { Invoke-RwNotRepeated $movies 401 '' 'Answer401' }
                    'answered 403' { Invoke-RwNotRepeated $movies 403 '{"message":"This API key does not include the required scope: imports."}' 'Answer403' }
                }
            } catch { Add-Missing ("weir reports $step stopped: " + (Remove-KnownSecrets $_.Exception.Message) + ' at line ' + $_.InvocationInfo.ScriptLineNumber) }
            # whatever this step left: put policies back, let Weir finish, take our files away
            try { Restore-RwLibraries } catch { }
            Start-Sleep -Seconds 5
            foreach ($root in $movies.Output, $(if ($tv) { $tv.Output })) { Remove-RwOutput $root 'Recorded*' }
        }
    } finally {
        try { Restore-RwLibraries } catch { }
        if ($connection -and $original) {
            try { $null = Invoke-RwWeir 'PUT' "/media-managers/connections/$($connection.id)" @{ base_url = $original } }
            catch { Add-Missing "weir reports: COULD NOT put Weir's connection to Deluno back to $original ($($_.Exception.Message)); set it by hand in Weir (Setup, Media managers) or run Connect Weir again" }
        }
        Stop-RwProxy $script:RW.Proxy
        Start-Sleep -Seconds 8
        foreach ($path in $script:RW.Made) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
        foreach ($root in $movies.Watched, $movies.Output, $(if ($tv) { $tv.Watched }), $(if ($tv) { $tv.Output })) {
            if ($root -and (Test-Path -LiteralPath $root)) { Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Recorded*' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
