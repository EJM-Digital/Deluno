<#
.SYNOPSIS
    Shared code for the scenario suite that runs inside the golden-path VM (docs/golden-path.md, "The scenario suite").

.DESCRIPTION
    Dot-source this file. It holds what every scenario needs: the results record, waiting with timings, Deluno's and
    Weir's own HTTP APIs, and finding the work Deluno handed to Weir. Everything runs on Windows PowerShell 5.1.

    Logins: the suite makes its own throwaway logins inside the VM (New-SuiteSecret), keeps them in a file there and
    redacts them from everything it writes (Hide-Secrets). A login is never printed and never leaves the VM.
#>

$script:S = $null

function Initialize-Suite {
    param(
        [string] $Root = 'C:\golden\suite',
        [string] $DelunoUrl = 'http://localhost:7879',
        [string] $WeirUrl = 'http://localhost:9347'
    )
    New-Item -ItemType Directory -Force -Path $Root, (Join-Path $Root 'results'), (Join-Path $Root 'logs') | Out-Null
    $secretFile = Join-Path $Root 'secrets.json'
    $secrets = @{}
    if (Test-Path $secretFile) {
        $loaded = Get-Content -Raw $secretFile | ConvertFrom-Json
        foreach ($p in $loaded.PSObject.Properties) { $secrets[$p.Name] = [string]$p.Value }
    }
    $script:S = @{
        Root = $Root; DelunoUrl = $DelunoUrl.TrimEnd('/'); WeirUrl = $WeirUrl.TrimEnd('/')
        Secrets = $secrets; SecretFile = $secretFile; Token = $null
        Results = New-Object System.Collections.ArrayList; Current = $null
        ResultsFile = Join-Path $Root 'results\results.json'
        LogFile = Join-Path $Root 'logs\suite.log'
        Started = [DateTime]::UtcNow
    }
}

function Get-SuiteRoot { $script:S.Root }

# ---------------------------------------------------------------- secrets

# A throwaway value made inside the VM. The name is only a label; the value is kept in secrets.json beside the suite
# and redacted from every line the suite writes.
function New-SuiteSecret {
    param([Parameter(Mandatory)] [string] $Name)
    if (-not $script:S.Secrets.ContainsKey($Name)) {
        $script:S.Secrets[$Name] = 'Gp-' + [guid]::NewGuid().ToString('N').Substring(0, 20)
        ($script:S.Secrets | ConvertTo-Json) | Set-Content -Path $script:S.SecretFile -Encoding ascii
    }
    $script:S.Secrets[$Name]
}

function Get-SuiteSecret {
    param([Parameter(Mandatory)] [string] $Name)
    if (-not $script:S.Secrets.ContainsKey($Name)) { throw "No secret named $Name has been made." }
    $script:S.Secrets[$Name]
}

function Hide-Secrets {
    param([string] $Text)
    if ($null -eq $Text) { return '' }
    $out = $Text
    if ($script:S) {
        foreach ($v in $script:S.Secrets.Values) { if ($v -and $v.Length -ge 6) { $out = $out.Replace($v, '***') } }
        if ($script:S.Token) { $out = $out.Replace($script:S.Token, '***') }
    }
    $out
}

# ---------------------------------------------------------------- log and results

function Write-SuiteLog {
    param([string] $Text)
    $line = '{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f (Get-Date), (Hide-Secrets $Text)
    Write-Host $line
    if ($script:S) { Add-Content -Path $script:S.LogFile -Value $line -Encoding utf8 }
}

function Save-Results {
    $payload = [ordered]@{
        startedUtc = $script:S.Started.ToString('o')
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        scenarios  = @($script:S.Results)
    }
    $tmp = $script:S.ResultsFile + '.tmp'
    ($payload | ConvertTo-Json -Depth 8) | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Force -Path $tmp -Destination $script:S.ResultsFile
}

# Runs one scenario. A thrown error is an "error" result (the harness or the VM broke, not necessarily the product);
# a failed Add-Check is a "fail" (the product did not do what the scenario expects). Both stop a pass.
function Invoke-Scenario {
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Title,
        [Parameter(Mandatory)] [scriptblock] $Body
    )
    # A run limited to some scenarios (-Scenarios) records nothing for the others; their setup is skipped by the group, which asks
    # Test-ScenarioSelected before it prepares anything.
    if ((Get-Command Test-ScenarioSelected -ErrorAction SilentlyContinue) -and -not (Test-ScenarioSelected -Id $Id)) { return }
    $rec = [ordered]@{
        id = $Id; title = $Title; status = 'running'; startedUtc = [DateTime]::UtcNow.ToString('o'); seconds = 0
        checks = (New-Object System.Collections.ArrayList); notes = (New-Object System.Collections.ArrayList); error = $null
    }
    [void]$script:S.Results.Add($rec)
    $script:S.Current = $rec
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-SuiteLog "== $Id : $Title"
    try { & $Body }
    catch {
        $rec.error = Hide-Secrets ($_.Exception.Message + ' @ ' + ($_.InvocationInfo.PositionMessage -replace '\s+', ' ') + $(if ($_.ScriptStackTrace) { ' [stack: ' + ($_.ScriptStackTrace -replace '\s*\r?\n\s*', ' <- ') + ']' } else { '' }))
        Write-SuiteLog "   ERROR $($rec.error)"
    }
    $sw.Stop()
    $rec.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($rec.checks | Where-Object { -not $_.ok })
    $rec.status = if ($rec.error) { 'error' } elseif ($failed.Count -gt 0) { 'fail' } elseif ($rec.checks.Count -eq 0) { 'error' } else { 'pass' }
    if ($rec.status -eq 'error' -and -not $rec.error) { $rec.error = 'The scenario ran no checks.' }
    Write-SuiteLog ("== {0} {1} in {2}s" -f $Id, $rec.status.ToUpperInvariant(), $rec.seconds)
    $script:S.Current = $null
    Save-Results
}

function Add-Check {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [bool] $Ok, [string] $Detail = '')
    $entry = [ordered]@{ name = $Name; ok = $Ok; detail = (Hide-Secrets $Detail); atUtc = [DateTime]::UtcNow.ToString('o') }
    if ($script:S.Current) { [void]$script:S.Current.checks.Add($entry) }
    Write-SuiteLog ("   {0} {1} - {2}" -f $(if ($Ok) { 'ok  ' } else { 'FAIL' }), $Name, (Hide-Secrets $Detail))
}

function Add-Note {
    param([string] $Text)
    if ($script:S.Current) { [void]$script:S.Current.notes.Add((Hide-Secrets $Text)) }
    Write-SuiteLog "   note $Text"
}

# ---------------------------------------------------------------- waiting

# Polls $Condition until it returns something truthy. Returns Ok, Seconds (how long it took) and Value (what it returned).
function Wait-Until {
    param(
        [Parameter(Mandatory)] [scriptblock] $Condition,
        [int] $TimeoutSec = 60,
        [double] $IntervalSec = 1
    )
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $value = $null
    while ($true) {
        try { $value = & $Condition } catch { $value = $null }
        if ($value) { return [pscustomobject]@{ Ok = $true; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Value = $value } }
        if ($sw.Elapsed.TotalSeconds -ge $TimeoutSec) { return [pscustomobject]@{ Ok = $false; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Value = $null } }
        Start-Sleep -Milliseconds ([int]($IntervalSec * 1000)) # reason: poll interval inside a loop bounded by its deadline
    }
}

# ---------------------------------------------------------------- Deluno

function ConvertFrom-ErrorResponse {
    param($ErrorRecord)
    $code = 0
    $body = ''
    if ($ErrorRecord.Exception.Response) {
        try { $code = [int]$ErrorRecord.Exception.Response.StatusCode } catch { }
    }
    if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) { $body = $ErrorRecord.ErrorDetails.Message }
    elseif ($ErrorRecord.Exception.Response) {
        try { $reader = New-Object IO.StreamReader($ErrorRecord.Exception.Response.GetResponseStream()); $body = $reader.ReadToEnd(); $reader.Dispose() } catch { }
    }
    [pscustomobject]@{ Status = $code; Raw = $body; Message = $ErrorRecord.Exception.Message }
}

function Connect-Deluno {
    param([string] $Username, [string] $Password)
    if (-not $Username) { $Username = 'golden' }
    if (-not $Password) { $Password = Get-SuiteSecret 'deluno-password' }
    $body = @{ username = $Username; password = $Password; keepSignedIn = $false } | ConvertTo-Json
    $resp = Invoke-RestMethod -Uri "$($script:S.DelunoUrl)/api/auth/login" -Method Post -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 30
    if (-not $resp.accessToken) { throw 'Deluno did not return a sign-in token.' }
    $script:S.Token = $resp.accessToken
    $script:S.Login = $resp
}

# Calls Deluno and returns what it answered as {Status, Data, Raw}. Never throws for an HTTP error. A 401 signs in again once.
function Try-Deluno {
    param([string] $Method = 'GET', [Parameter(Mandatory)] [string] $Path, $Body = $null, [int] $TimeoutSec = 60)
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $p = @{ Uri = "$($script:S.DelunoUrl)$Path"; Method = $Method; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
        if ($script:S.Token) { $p.Headers = @{ Authorization = "Bearer $($script:S.Token)" } }
        if ($null -ne $Body) { $p.ContentType = 'application/json'; $p.Body = ($Body | ConvertTo-Json -Depth 8) }
        try {
            $r = Invoke-WebRequest @p
            $data = $null
            if ($r.Content) { try { $data = $r.Content | ConvertFrom-Json } catch { } }
            return [pscustomobject]@{ Status = [int]$r.StatusCode; Data = $data; Raw = [string]$r.Content }
        }
        catch {
            $e = ConvertFrom-ErrorResponse $_
            if ($e.Status -eq 401 -and $attempt -eq 0 -and $script:S.Secrets.ContainsKey('deluno-password')) { Connect-Deluno; continue }
            $data = $null
            if ($e.Raw) { try { $data = $e.Raw | ConvertFrom-Json } catch { } }
            return [pscustomobject]@{ Status = $e.Status; Data = $data; Raw = $e.Raw }
        }
    }
}

# Calls Deluno and returns the parsed answer; throws with the status and body on an HTTP error.
function Invoke-Deluno {
    param([string] $Method = 'GET', [Parameter(Mandatory)] [string] $Path, $Body = $null, [int] $TimeoutSec = 60)
    $r = Try-Deluno -Method $Method -Path $Path -Body $Body -TimeoutSec $TimeoutSec
    if ($r.Status -lt 200 -or $r.Status -ge 300) { throw (Hide-Secrets "$Method $Path answered $($r.Status). $($r.Raw)") }
    $r.Data
}

function Get-Handoffs {
    param([int] $Take = 200)
    @(Invoke-Deluno -Path "/api/integrations/processors/handoffs?take=$Take")
}

# The hand-offs for one release name (newest first). A resent release has more than one.
function Find-Handoffs {
    param([Parameter(Mandatory)] [string] $Name)
    @(Get-Handoffs | Where-Object { $_.releaseName -like "*$Name*" } | Sort-Object createdUtc -Descending)
}

function Get-DownloadClients { @(Invoke-Deluno -Path '/api/download-clients') }

# Every client's queue rows, flattened, each carrying its clientId and protocol. With -IncludeHistory the clients' finished
# (history) rows come too: a finished Usenet job leaves the queue, and a torrent whose files were deleted shows up there. A
# history row gets the status of its outcome, progress 100 and isHistory = $true.
function Get-TelemetryRows {
    param([switch] $IncludeHistory)
    $rows = New-Object System.Collections.ArrayList
    $overview = Invoke-Deluno -Path '/api/download-clients/telemetry'
    foreach ($snap in @($overview.clients)) {
        foreach ($q in @($snap.queue)) {
            if ($q) { $q | Add-Member -NotePropertyName clientId -NotePropertyValue $snap.clientId -Force; $q | Add-Member -NotePropertyName clientProtocol -NotePropertyValue $snap.protocol -Force; [void]$rows.Add($q) }
        }
        if ($IncludeHistory) {
            foreach ($h in @($snap.history)) {
                if (-not $h) { continue }
                $h | Add-Member -NotePropertyName status -NotePropertyValue "$($h.outcome)" -Force
                $h | Add-Member -NotePropertyName progress -NotePropertyValue 100 -Force
                $h | Add-Member -NotePropertyName isHistory -NotePropertyValue $true -Force
                $h | Add-Member -NotePropertyName clientProtocol -NotePropertyValue $snap.protocol -Force
                [void]$rows.Add($h)
            }
        }
    }
    @($rows)
}

function Find-QueueRow {
    param([Parameter(Mandatory)] [string] $Name, [switch] $IncludeHistory)
    $all = @(Get-TelemetryRows -IncludeHistory:$IncludeHistory | Where-Object { $_.releaseName -like "*$Name*" -or $_.title -like "*$Name*" })
    # A live queue row is the better reading when there is both.
    @($all | Where-Object { -not $_.isHistory }) + @($all | Where-Object { $_.isHistory }) | Select-Object -First 1
}

# Activity as lines of text (each entry's whole JSON, compact), newest first, following the pages (500 an entry page, up to 6 pages).
# Scenarios search it for the words a person reads.
function Get-ActivityText {
    param([int] $PageSize = 500, [int] $MaxPages = 6)
    $lines = New-Object System.Collections.ArrayList
    $token = $null
    for ($i = 0; $i -lt $MaxPages; $i++) {
        $path = "/api/activity?pageSize=$PageSize"
        if ($token) { $path += "&pageToken=$([uri]::EscapeDataString($token))" }
        $page = Invoke-Deluno -Path $path
        $items = if ($page.PSObject.Properties.Name -contains 'items') { @($page.items) } else { @($page) }
        foreach ($item in $items) { if ($item) { [void]$lines.Add(($item | ConvertTo-Json -Depth 6 -Compress)) } }
        $token = if ($page.PSObject.Properties.Name -contains 'nextPageToken') { $page.nextPageToken } else { $null }
        if (-not $token) { break }
    }
    @($lines)
}

function Test-ActivityHas {
    param([Parameter(Mandatory)] [string] $Pattern, [string] $Within)
    $lines = Get-ActivityText
    if ($Within) { $lines = @($lines | Where-Object { $_ -match [regex]::Escape($Within) }) }
    @($lines | Where-Object { $_ -match $Pattern }).Count -gt 0
}

function Get-Library {
    param([ValidateSet('movies', 'tv')] [string] $MediaType = 'movies')
    @(Invoke-Deluno -Path '/api/libraries') | Where-Object { $_.mediaType -eq $MediaType } | Select-Object -First 1
}

# ---------------------------------------------------------------- Weir

# What Weir needs on a request that changes something, sent the way its own page sends it: Weir answers 403 to any change that
# carries an Origin header but no X-Requested-With (its protocol middleware), so the suite's login and every PUT/POST were refused
# (rc.30.22: "403 Forbidden" in the resend and vanish groups, and the Weir login Connect Weir made "does not sign in").
function Get-WeirHeaders {
    param([Parameter(Mandatory)] [string] $Origin)
    @{ Origin = $Origin; 'X-Requested-With' = 'XMLHttpRequest' }
}

# Weir keeps a session cookie and a CSRF token; the suite signs in with the throwaway login made for it.
function Connect-Weir {
    $sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $origin = $script:S.WeirUrl
    $csrf = (Invoke-RestMethod -Uri "$origin/api/v1/auth/csrf" -WebSession $sess -UseBasicParsing -TimeoutSec 20).csrf_token
    $body = @{ username = (Get-SuiteSecret 'weir-username'); password = (Get-SuiteSecret 'weir-password'); csrf_token = $csrf } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$origin/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body $body -WebSession $sess -Headers (Get-WeirHeaders -Origin $origin) -UseBasicParsing -TimeoutSec 20 | Out-Null
    }
    catch { throw (Hide-Secrets ("Weir's sign-in answered: {0} {1}" -f $_.Exception.Message, $_.ErrorDetails.Message)) }
    $script:S.WeirSession = $sess
}

function Invoke-Weir {
    param([string] $Method = 'GET', [Parameter(Mandatory)] [string] $Path, $Body = $null)
    # A session that Weir no longer knows (a restart, an expiry) answers 401 or 403: sign in again once and repeat the request.
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if (-not $script:S.WeirSession) { Connect-Weir }
        $origin = $script:S.WeirUrl
        $p = @{ Uri = "$origin/api/v1$Path"; Method = $Method; WebSession = $script:S.WeirSession; UseBasicParsing = $true; TimeoutSec = 30; Headers = (Get-WeirHeaders -Origin $origin) }
        try {
            if ($Method -ne 'GET') {
                $csrf = (Invoke-RestMethod -Uri "$origin/api/v1/auth/csrf" -WebSession $script:S.WeirSession -UseBasicParsing -TimeoutSec 20).csrf_token
                $payload = @{}
                if ($Body) { foreach ($k in $Body.Keys) { $payload[$k] = $Body[$k] } }
                $payload['csrf_token'] = $csrf
                $p.ContentType = 'application/json'; $p.Body = ($payload | ConvertTo-Json -Depth 6)
            }
            return (Invoke-RestMethod @p)
        }
        catch {
            $code = $null
            try { $code = [int]$_.Exception.Response.StatusCode } catch { }
            if ($attempt -eq 1 -and $code -in 401, 403) { $script:S.WeirSession = $null; continue }
            throw (Hide-Secrets ("Weir answered {0} to {1} {2}: {3} {4}" -f $code, $Method, $Path, $_.Exception.Message, $_.ErrorDetails.Message))
        }
    }
}

# Pause or resume Weir's processing. Keep looking for new files stays on, as in a person's "Pause until I resume".
function Set-WeirPause {
    param([Parameter(Mandatory)] [bool] $Paused)
    Invoke-Weir -Method PUT -Path '/pause' -Body @{ paused = $Paused; scan_while_paused = $true } | Out-Null
}

function Test-WeirAnswering {
    try { $r = Invoke-WebRequest -Uri "$($script:S.WeirUrl)/ready" -UseBasicParsing -TimeoutSec 4; return ($r.StatusCode -eq 200) } catch { return $false }
}

function Get-WeirExe {
    Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Weir') -Recurse -Filter 'Weir.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
}

# Quits Weir the way its tray's Quit does, as far as the processes go (the tray menu item is tried first, see Tray.Lib.ps1).
function Stop-WeirApp {
    $quit = $false
    if (Get-Command Invoke-TrayMenuItem -ErrorAction SilentlyContinue) {
        try { $quit = [bool](Invoke-TrayMenuItem -Icon 'Weir' -Item '^Quit Weir') } catch { $quit = $false }
    }
    $gone = Wait-Until -TimeoutSec 15 -IntervalSec 0.5 -Condition { -not (Get-Process -Name 'WeirServer' -ErrorAction SilentlyContinue) }
    # GoneAt is the moment Weir's server was seen gone, so a quit that had to be forced is timed from when Weir really
    # went down, not from the 15 s the tray menu was given (rc.30.28: amber "after 64 s" was 49 s after Weir stopped).
    $goneAt = if ($gone.Ok) { [DateTime]::UtcNow } else { $null }
    if (-not $gone.Ok) {
        Get-Process -Name 'Weir', 'WeirServer' -ErrorAction SilentlyContinue | Stop-Process -Force
        Wait-Until -TimeoutSec 10 -IntervalSec 0.2 -Condition { -not (Get-Process -Name 'WeirServer' -ErrorAction SilentlyContinue) } | Out-Null
        $goneAt = [DateTime]::UtcNow
        Add-Note ("Weir did not quit from its tray menu in 15 s; its processes were stopped.{0}" -f $(if (-not $quit -and $script:TrayMenuMiss) { " Why the menu was not used: $($script:TrayMenuMiss)." } elseif ($quit) { ' The menu item was clicked, but Weir did not go.' } else { '' }))
    }
    $script:S.WeirSession = $null
    [pscustomobject]@{ ViaTray = $quit; At = [DateTime]::UtcNow; GoneAt = $goneAt }
}

# Starts Weir again, in the signed-in desktop session (it is a tray app), as a person double-clicking it would.
function Start-WeirApp {
    $exe = Get-WeirExe
    if (-not $exe) { throw 'Weir.exe is not installed.' }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $exe.FullName -WorkingDirectory $exe.DirectoryName
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName 'GoldenSuiteWeirStart' -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName 'GoldenSuiteWeirStart'
    Start-Sleep -Seconds 3
    Unregister-ScheduledTask -TaskName 'GoldenSuiteWeirStart' -Confirm:$false -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- files

function Test-FolderEmptyOf {
    param([string] $Path, [string] $Pattern)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $Pattern }).Count -eq 0
}

function Find-Imported {
    param([Parameter(Mandatory)] [string] $Title, [string] $Root = 'C:\Media\Movies')
    @(Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Title) -and $_.Extension -in '.mkv', '.mp4', '.avi' }) | Select-Object -First 1
}

# Copies a log or evidence file into the run's results folder.
function Save-Evidence {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Text)
    $dir = Join-Path $script:S.Root 'results\evidence'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -Path (Join-Path $dir $Name) -Value (Hide-Secrets $Text) -Encoding utf8
}

# Shared by the update runner and the scenarios that restart Deluno.
function Test-DelunoAnswering {
    $r = Try-Deluno -Path '/api/auth/bootstrap-status' -TimeoutSec 5
    $r.Status -eq 200
}

function Get-DelunoProcess {
    @(Get-Process -Name 'Deluno' -ErrorAction SilentlyContinue)
}
