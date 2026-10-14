# Helpers the recorders share. Runs INSIDE the golden VM (Windows PowerShell 5.1, PowerShell Direct), so it
# uses only .NET and built-in cmdlets, and it holds no host or credential: everything a recorder needs comes in
# through its parameters. Dot-sourced text, assembled by Invoke-RecordReplies.ps1 (#1237).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# A running log inside the VM (C:\golden\recorded-run.log), so a long run can be watched from outside: the host only gets the
# result when the run ends. Never holds a login: it is written from the same text Save-State and Add-Missing show.
function Write-RunLog([string] $Text) {
    try { Add-Content -LiteralPath 'C:\golden\recorded-run.log' -Value ((Get-Date).ToString('HH:mm:ss') + ' ' + $Text) -Encoding utf8 } catch { }
}

# The recorder copies and downloads real films, which fill a small VM disk (a run once left it with 0.2 GB free and every client failed
# with "not enough space"). It checks before each phase, and every phase removes what it made.
function Assert-FreeSpace([int] $Gigabytes, [string] $Phase) {
    $free = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
    Write-RunLog "free space on C: $free GB before $Phase"
    if ($free -lt $Gigabytes) { throw "only $free GB are free on C:, and $Phase needs $Gigabytes GB. Free space in the VM and run it again." }
}

# What every recorder fills in.
$script:Recordings = [System.Collections.Generic.List[object]]::new()
$script:Missing = [System.Collections.Generic.List[string]]::new()

# One client of one app: a cookie jar so a login sticks, and the headers worth keeping on a reply.
function New-RecordedClient {
    param([string] $BaseUrl, [string[]] $KeepHeaders = @('content-type'), [pscredential] $BasicAuth, [hashtable] $DefaultHeaders = @{})
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    $handler.AllowAutoRedirect = $false
    $http = New-Object System.Net.Http.HttpClient($handler)
    $http.Timeout = [TimeSpan]::FromSeconds(60)
    $http.BaseAddress = [Uri]($BaseUrl.TrimEnd('/') + '/')
    if ($BasicAuth) {
        $pair = $BasicAuth.UserName + ':' + $BasicAuth.GetNetworkCredential().Password
        $http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Basic', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair)))
    }
    foreach ($name in $DefaultHeaders.Keys) { $null = $http.DefaultRequestHeaders.TryAddWithoutValidation($name, [string]$DefaultHeaders[$name]) }
    [pscustomobject]@{ Http = $http; Keep = $KeepHeaders; Base = $BaseUrl }
}

# Sends one request and returns the exchange exactly as the app answered it. A reply of any status is a result,
# not an error: a 409 or a 404 is what is being recorded. Only a connection that gets no answer is recorded as
# status 0 with the reason, which is what a client that is not answering looks like to Deluno.
# -Body is text; -Form is a hashtable sent form-encoded; -Json is an object sent as JSON; -Multipart is name -> text or byte[].
function Send-Recorded {
    param(
        $Client, [string] $Method = 'GET', [string] $Path, [string] $Body, [hashtable] $Form, $Json, [hashtable] $Multipart,
        [hashtable] $Headers = @{}, [string] $ContentType, [string[]] $KeepHeaders
    )
    $relative = $Path.TrimStart('/')
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $relative)
    $recordedBody = $null
    if ($Form) {
        $request.Content = New-Object System.Net.Http.FormUrlEncodedContent([System.Collections.Generic.Dictionary[string, string]]$(
            $d = New-Object 'System.Collections.Generic.Dictionary[string,string]'; foreach ($k in $Form.Keys) { $d[$k] = [string]$Form[$k] }; $d))
        $recordedBody = ($Form.Keys | ForEach-Object { "$_=$($Form[$_])" }) -join '&'
    } elseif ($Multipart) {
        $mp = New-Object System.Net.Http.MultipartFormDataContent
        $shown = @()
        foreach ($k in $Multipart.Keys) {
            $v = $Multipart[$k]
            if ($v -is [byte[]]) {
                $part = New-Object System.Net.Http.ByteArrayContent(, $v)
                $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/x-bittorrent')
                $mp.Add($part, $k, 'file.torrent'); $shown += "$k=<$($v.Length) bytes>"
            } else { $mp.Add((New-Object System.Net.Http.StringContent([string]$v)), $k); $shown += "$k=$v" }
        }
        $request.Content = $mp
        $recordedBody = $shown -join '&'
    } elseif ($PSBoundParameters.ContainsKey('Json')) {
        $recordedBody = ConvertTo-Json -InputObject $Json -Depth 20 -Compress
        $request.Content = New-Object System.Net.Http.StringContent($recordedBody, [Text.Encoding]::UTF8, 'application/json')
    } elseif ($Body) {
        $recordedBody = $Body
        $request.Content = New-Object System.Net.Http.StringContent($Body, [Text.Encoding]::UTF8, $(if ($ContentType) { $ContentType } else { 'text/plain' }))
    }
    foreach ($name in $Headers.Keys) { $null = $request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }

    $requestPart = [ordered]@{ method = $Method.ToUpperInvariant(); path = '/' + $relative }
    if ($null -ne $recordedBody) { $requestPart.bodyText = $recordedBody }
    $keep = if ($KeepHeaders) { $KeepHeaders } else { $Client.Keep }
    try {
        $response = $Client.Http.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $headers = [ordered]@{}
        foreach ($h in @($response.Headers) + @($response.Content.Headers)) {
            if ($keep -contains $h.Key.ToLowerInvariant()) { $headers[$h.Key.ToLowerInvariant()] = ($h.Value -join ', ') }
        }
        $replyPart = [ordered]@{ status = [int]$response.StatusCode; headers = $headers; bodyText = $text }
    } catch {
        $inner = $_.Exception; while ($inner.InnerException) { $inner = $inner.InnerException }
        $replyPart = [ordered]@{ status = 0; headers = [ordered]@{}; error = $inner.Message }
    }
    [pscustomobject]@{ request = $requestPart; reply = $replyPart }
}

# The body of an exchange, parsed, for a recorder that has to read an id out of an answer. The recording keeps the raw text.
function Get-ReplyJson($Exchange) {
    if ($Exchange.reply.status -eq 0 -or -not $Exchange.reply.bodyText) { return $null }
    try { $Exchange.reply.bodyText | ConvertFrom-Json } catch { $null }
}

# Saves one state: what was done to reach it, and the exchanges that show it.
function Save-State {
    param([string] $App, [string] $Version, [string] $State, [string] $Note, [object[]] $Exchanges)
    if (-not $Exchanges -or $Exchanges.Count -eq 0) { Add-Missing "$App $Version $State (nothing was recorded)"; return }
    $safeVersion = ($Version -replace '[^0-9A-Za-z.\-+]', '_')
    $recording = [pscustomobject]@{
        app = $App; version = $safeVersion; state = $State; recordedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); note = $Note; exchanges = @($Exchanges)
    }
    # A second line behind scrub.mjs: any login read in the VM (Record.Secrets.ps1) is removed before it leaves the VM.
    $json = ConvertTo-Json -InputObject $recording -Depth 30 -Compress
    if (Get-Command Remove-KnownSecrets -ErrorAction SilentlyContinue) { $json = Remove-KnownSecrets $json }
    $script:Recordings.Add(($json | ConvertFrom-Json))
    # The host only gets the recordings when the whole run ends. A copy is kept in the VM as each state is saved (already scrubbed of the logins
    # read there; scrub.mjs still runs over it on the host), so a run that is stopped part-way loses nothing: C:\golden\recorded\saved-states.jsonl,
    # one recording per line, which Import-SavedStates.ps1 turns into the same files a finished run writes.
    try { $null = New-Item -ItemType Directory -Force -Path 'C:\golden\recorded'; Add-Content -LiteralPath 'C:\golden\recorded\saved-states.jsonl' -Value $json -Encoding utf8 } catch { }
    $line = ('recorded {0} {1} {2} ({3} exchange(s))' -f $App, $safeVersion, $State, @($Exchanges).Count)
    Write-Host $line; Write-RunLog $line
}

# A state this run could not produce, and what it needs. Listed in the run's summary and in the README of the library.
function Add-Missing([string] $Text) { $script:Missing.Add($Text); Write-Host "MISSING $Text" -ForegroundColor Yellow; Write-RunLog "MISSING $Text" }

# Polls until the test says the app reached a state, or the time is up. Returns the last value the test saw.
function Wait-Until {
    param([scriptblock] $Test, [int] $Seconds = 90, [int] $EverySeconds = 2)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $result = & $Test
        if ($result) { return $result }
        Start-Sleep -Seconds $EverySeconds
    } while ((Get-Date) -lt $deadline)
    return $null
}

# Where a client that is not answering is recorded: a port nothing listens on, which the operating system refuses at once.
function Get-ClosedPortClient([string] $Description) { New-RecordedClient -BaseUrl 'http://127.0.0.1:1' }

# The newest torrent the golden feed made (Add-Release.ps1 appends one line to releases.jsonl per release), read as bytes
# so a client is handed the file itself and needs no address of the feed.
function Get-FeedTorrent {
    param([string] $FeedRoot = 'C:\golden\recorded\feed', [string] $Title)
    $lines = @(Get-Content -LiteralPath (Join-Path $FeedRoot 'releases.jsonl') | Where-Object { $_.Trim() })
    $entries = $lines | ForEach-Object { $_ | ConvertFrom-Json }
    $entry = if ($Title) { $entries | Where-Object { $_.title -eq $Title } | Select-Object -Last 1 } else { $entries | Select-Object -Last 1 }
    if (-not $entry) { throw "The feed has no release $Title" }
    [pscustomobject]@{ Title = $entry.title; InfoHash = $entry.infohash; Size = $entry.size; Bytes = [IO.File]::ReadAllBytes((Join-Path $FeedRoot "torrents\$($entry.torrent)")) }
}

function Add-FeedRelease {
    param([string] $Title, [string] $File, [string] $Folder, [string] $Tracker, [int] $DummyMb = 0, [switch] $DeadTracker, [string] $FeedRoot = 'C:\golden\recorded\feed')
    $adder = Join-Path $FeedRoot 'Add-Release.ps1'
    if (-not (Test-Path $adder)) { throw "The torrent-making scripts are not in the VM ($adder); Invoke-RecordTorrentClients puts them there." }
    $arguments = @{ Title = $Title; Root = $FeedRoot }
    if ($Folder) { $arguments.Folder = $Folder } elseif ($File) { $arguments.File = $File } else { $arguments.DummyMb = $DummyMb }
    if ($Tracker) { $arguments.Tracker = $Tracker }
    if ($DeadTracker) { $arguments.DeadTracker = $true }
    $null = & $adder @arguments
    Get-FeedTorrent -FeedRoot $FeedRoot -Title $Title
}
