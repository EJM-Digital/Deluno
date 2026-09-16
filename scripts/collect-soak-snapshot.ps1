<#
.SYNOPSIS
    Takes one day's soak reading and says whether the day passed.

.DESCRIPTION
    The soak plan lists seven checks and calls them "a daily decision, not a
    suggestion". This used to record the numbers and leave the decision to
    whoever read the table, which over fourteen days is fourteen chances to
    glance at a column and move on. Six of the seven are arithmetic against a
    threshold the plan already states, so they are decided here.

    The seventh - discovery, grab, transfer, import and rename all accounted
    for - is the operator's eyes on the filesystem and cannot be faked from an
    endpoint. Supply it with -WorkflowNote. Without one the day is ATTENTION,
    not PASS: an unmade decision is not a passing one.

.PARAMETER WorkflowNote
    The operator's evidence for the workflow check: IDs, paths, and anything
    unexpected. Required for a day to read PASS.
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$BaseUrl = "http://127.0.0.1:5099",
    [string]$ApiKey,
    [string]$RunId = ("soak-" + (Get-Date -Format "yyyyMMdd")),
    [string]$OutputRoot = "artifacts/soak",
    [string]$WorkflowNote,
    [string]$ApiKeyFile,
    [switch]$InstallDailyTask,
    [switch]$RemoveDailyTask,
    [string]$DailyTaskAt = "09:00",
    # Who the daily task runs as. Defaults to the account installing it.
    [string]$TaskUserName,
    # Supply only if the account cannot be granted "log on as a batch job";
    # with no password the task is registered S4U, which needs no secret.
    [string]$TaskPassword,
    # Close a day that already has a reading, without taking another. Needs
    # -WorkflowNote. The operator's check does not always run on the UTC date it
    # is judging - the machine it runs on may have been off - and a note written
    # "today" would land on the wrong day.
    [string]$CloseDate
)

$ErrorActionPreference = "Stop"
Set-Location $Root

# ------------------------------------------------------- run me every day

# Fourteen consecutive days is the point of the gate, and fourteen chances to
# remember is not a plan. The task carries the run id, so a run that is restarted
# after a P0 gets a new id and a new task rather than appending to the old
# ledger.
$taskName = "Deluno soak $RunId"

if ($RemoveDailyTask) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Stopped the daily collection for $RunId."
    } else {
        Write-Host "No daily collection was scheduled for $RunId."
    }
    exit 0
}

if ($InstallDailyTask) {
    if (-not $ApiKeyFile) {
        throw "Scheduling needs -ApiKeyFile. The key must not go in the task's arguments, which anyone on the machine can read."
    }
    if (-not (Test-Path $ApiKeyFile)) {
        throw "The API key file does not exist: $ApiKeyFile"
    }

    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-Root", "`"$Root`"",
        "-BaseUrl", "`"$BaseUrl`"",
        "-RunId", "`"$RunId`"",
        "-OutputRoot", "`"$OutputRoot`"",
        "-ApiKeyFile", "`"$(Resolve-Path $ApiKeyFile)`""
    ) -join " "

    # Whether or not anybody is logged on.
    #
    # Registered without a principal, Task Scheduler defaults to LogonType
    # Interactive, which means "run only when this user is logged on". A soak
    # host is a server that nobody is sitting at, so the daily reading would
    # never have fired - and the failure is silent: the task sits Ready, the
    # trigger passes, and no row is written. Caught on the rig by reading the
    # registered task's principal back, which is why this now sets one.
    #
    # S4U runs without storing a secret and is the default here. It cannot reach
    # network resources, which this does not need: it reads a local HTTP
    # endpoint and writes to a local folder. Pass -TaskPassword where an account
    # cannot be granted the batch logon right.
    $taskUser = if ($TaskUserName) { $TaskUserName } else { "$env:USERDOMAIN\$env:USERNAME" }

    $register = @{
        TaskName = $taskName
        Force    = $true
        Action   = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments -WorkingDirectory $Root
        Trigger  = New-ScheduledTaskTrigger -Daily -At $DailyTaskAt
        Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    }
    if ($TaskPassword) {
        # A password-backed registration takes the account directly; LogonType
        # and RunLevel are not parameters of Register-ScheduledTask.
        $register.User = $taskUser
        $register.Password = $TaskPassword
    } else {
        $register.Principal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType S4U -RunLevel Highest
    }

    Register-ScheduledTask @register | Out-Null

    $registered = Get-ScheduledTask -TaskName $taskName
    if ($registered.Principal.LogonType -eq "Interactive") {
        throw "The task registered as Interactive, so it would only run while $taskUser is logged on. Re-run with -TaskPassword."
    }

    Write-Host "Collecting $RunId every day at $DailyTaskAt as $($registered.Principal.UserId) ($($registered.Principal.LogonType))."
    Write-Host "Each day lands as ATTENTION until its workflow check is recorded:"
    Write-Host "  npm run soak:snapshot -- -RunId $RunId -BaseUrl $BaseUrl -ApiKeyFile <path> -WorkflowNote '...'"
    Write-Host "Stop it with -RemoveDailyTask -RunId $RunId."
    exit 0
}

# The key by reference rather than by value, so it is not in a task definition,
# a shell history, or this repository.
if (-not $ApiKey -and $ApiKeyFile) {
    if (-not (Test-Path $ApiKeyFile)) {
        throw "The API key file does not exist: $ApiKeyFile"
    }
    $ApiKey = (Get-Content -Path $ApiKeyFile -Raw).Trim()
}

$BaseUrl = $BaseUrl.TrimEnd('/')
$safeRunId = ($RunId -replace '[^A-Za-z0-9._-]', '-')
if ([string]::IsNullOrWhiteSpace($safeRunId)) {
    throw "RunId must contain at least one letter, number, dot, underscore, or hyphen."
}

$runDirectory = Join-Path $Root (Join-Path $OutputRoot $safeRunId)
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $headers["X-Api-Key"] = $ApiKey
}

# Two different things used to look identical in the evidence: an endpoint that
# answered with an error, and this script being unable to ask. -SkipHttpErrorCheck
# is PowerShell 7 only, and on Windows PowerShell 5.1 - which is what the machine
# that has to record the soak runs - it threw before the request was made, so
# every reading of every day was recorded as a service that was down. The gate
# could not be recorded on the only machine that could record it (#461).
#
# So: no edition-specific parameter, and a failure to ask is called that.
function Invoke-SoakEndpoint {
    param(
        [string]$Name,
        [string]$Uri
    )

    function New-Reading($StatusCode, $Body, $Error, $Unreachable) {
        [PSCustomObject]@{
            Name = $Name
            Uri = $Uri
            StatusCode = [int]$StatusCode
            Succeeded = ([int]$StatusCode -ge 200 -and [int]$StatusCode -lt 300)
            Body = [string]$Body
            Error = $Error
            Unreachable = [bool]$Unreachable
        }
    }

    try {
        $response = Invoke-WebRequest -Uri $Uri -Headers $headers -UseBasicParsing
        return New-Reading $response.StatusCode $response.Content $null $false
    }
    catch {
        # One untyped catch on purpose. Typing it needs
        # Microsoft.PowerShell.Commands.HttpResponseException on 7 and
        # System.Net.WebException on 5.1, and naming a type the running edition
        # does not have fails to resolve at throw time - which is the same
        # edition-specific trap that caused this bug in the first place.
        #
        # A refusal carries a response with a status. Anything without one -
        # a bad URI, a host that is not there, a cmdlet this shell does not
        # have - means nothing was asked, so nothing was learned about Deluno.
        $status = 0
        if ($_.Exception.PSObject.Properties.Match('Response').Count -gt 0 -and $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = 0 }
        }
        $body = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { [string]$_.ErrorDetails.Message } else { "" }
        return New-Reading $status $body $_.Exception.Message ($status -eq 0)
    }
}

function Write-RawResponse {
    param(
        [PSCustomObject]$Response,
        [string]$Extension
    )

    $path = Join-Path $runDirectory ("{0}.{1}" -f $Response.Name, $Extension)
    if ([string]::IsNullOrWhiteSpace($Response.Body)) {
        $content = "Request failed with HTTP status $($Response.StatusCode). $($Response.Error)"
    }
    else {
        $content = $Response.Body
    }

    $content | Out-File -FilePath $path -Encoding utf8
}

function Get-PrometheusValue {
    param(
        [string]$Text,
        [string]$MetricName
    )

    $line = $Text -split "`r?`n" |
        Where-Object { $_ -match "^$([regex]::Escape($MetricName))(?:\{|\s)" } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        return ""
    }

    $parts = $line.Trim() -split "\s+"
    return $parts[-1]
}

function Get-CriticalAlertCount {
    param([string]$Json)

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return ""
    }

    try {
        $payload = $Json | ConvertFrom-Json
        $alerts = @($payload.alerts)
        return @($alerts | Where-Object { $_.severity -eq "critical" }).Count
    }
    catch {
        return ""
    }
}

$responses = @()
if (-not [string]::IsNullOrWhiteSpace($CloseDate)) {
    if ([string]::IsNullOrWhiteSpace($WorkflowNote)) {
        throw "-CloseDate records the workflow check for a day; supply the evidence with -WorkflowNote."
    }
    $closeHistoryPath = Join-Path $runDirectory "daily.jsonl"
    $recordedRows = @()
    if (Test-Path $closeHistoryPath) {
        $recordedRows = @(Get-Content $closeHistoryPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    }
    $row = $recordedRows | Where-Object { $_.DateUtc -eq $CloseDate } | Select-Object -Last 1
    if (-not $row) {
        # A note is evidence about a day; it is not a reading of it. A day with
        # no reading is a gap, and it stays one.
        throw "No reading was taken on $CloseDate in $safeRunId, so there is no day to close. A workflow note cannot stand in for a reading."
    }
    foreach ($field in 'WorkflowNote', 'Result', 'Reasons') {
        if (-not ($row.PSObject.Properties.Name -contains $field)) {
            $row | Add-Member -NotePropertyName $field -NotePropertyValue ''
        }
    }
} else {
$timestamp = (Get-Date).ToUniversalTime()
$responses = @(
    (Invoke-SoakEndpoint -Name "health-ready" -Uri "$BaseUrl/api/health/ready"),
    (Invoke-SoakEndpoint -Name "metrics" -Uri "$BaseUrl/api/monitoring/export/prometheus"),
    (Invoke-SoakEndpoint -Name "alerts" -Uri "$BaseUrl/api/monitoring/alerts"),
    (Invoke-SoakEndpoint -Name "jobs" -Uri "$BaseUrl/api/jobs?pageSize=100")
)

Write-RawResponse -Response $responses[0] -Extension "json"
Write-RawResponse -Response $responses[1] -Extension "prom"
Write-RawResponse -Response $responses[2] -Extension "json"
Write-RawResponse -Response $responses[3] -Extension "json"

$metricsText = $responses[1].Body
$ready = if ($responses[0].Succeeded) {
    try {
        if (($responses[0].Body | ConvertFrom-Json).ready -eq $true) { "1" } else { "0" }
    }
    catch { "0" }
}
else { "0" }

$row = [ordered]@{
    DateUtc = $timestamp.ToString("yyyy-MM-dd")
    Ready = $ready
    CriticalAlerts = Get-CriticalAlertCount -Json $responses[2].Body
    JobsQueued = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_jobs_queued"
    JobsFailed = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_jobs_failed"
    ApiErrorRatePercent = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_api_error_rate_percent"
    StorageFreePercent = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_storage_free_percent"
    IndexersHealthy = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_indexers_healthy"
    IndexersTotal = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_indexers_total"
    ClientsHealthy = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_clients_healthy"
    ClientsTotal = Get-PrometheusValue -Text $metricsText -MetricName "deluno_monitoring_clients_total"
}
}

# ---------------------------------------------------------------- the decision

# The thresholds are the soak plan's, quoted here so the two cannot drift:
#   Readiness       1 for the day
#   Critical alerts 0
#   Failed jobs     no upward trend for three consecutive days
#   API errors      < 5%
#   Free storage    > 12%
#   Services        all enabled indexers and clients healthy
#   Workflow        the operator's evidence
#
# "A missing endpoint response or missing metric fails the day and must be
# recorded as such", so a blank reading is a failure rather than a skip.
function Test-Threshold {
    param([string]$Value, [scriptblock]$Rule, [string]$Failure)

    if ([string]::IsNullOrWhiteSpace($Value)) { return "no reading" }
    $number = 0.0
    if (-not [double]::TryParse($Value, [ref]$number)) { return "unreadable value '$Value'" }
    if (& $Rule $number) { return $null }
    return $Failure -replace '\{0\}', $Value
}

$historyPath = Join-Path $runDirectory "daily.jsonl"
$history = @()
if (Test-Path $historyPath) {
    $history = @(Get-Content $historyPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

# One row per UTC date.
#
# The plan says to close a day by running this again with -WorkflowNote, and that
# used to append a second row for the same date. The ledger then showed the day
# twice, "Day N of the run" counted rows rather than days - fourteen days would
# have read as twenty-eight - and the failed-jobs trend treated a same-day
# re-reading as a day of its own. A later reading of a date now replaces the
# earlier one.
#
# A workflow note already recorded for the date is kept when a later reading
# brings none, so the unattended daily task running after the operator's check
# cannot quietly reopen a day that was closed.
$sameDate = @($history | Where-Object { $_.DateUtc -eq $row.DateUtc })
$history = @($history | Where-Object { $_.DateUtc -ne $row.DateUtc })
if ([string]::IsNullOrWhiteSpace($WorkflowNote)) {
    $recorded = $sameDate | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.WorkflowNote) } | Select-Object -Last 1
    if ($recorded) {
        $WorkflowNote = [string]$recorded.WorkflowNote
    }
}

$failures = @()

if ($row.Ready -ne "1") { $failures += "not ready" }

if ([string]::IsNullOrWhiteSpace([string]$row.CriticalAlerts)) {
    $failures += "critical alerts: no reading"
} elseif ([int]$row.CriticalAlerts -gt 0) {
    $failures += "$($row.CriticalAlerts) critical alert(s)"
}

$failures += Test-Threshold -Value $row.ApiErrorRatePercent `
    -Rule { param($n) $n -lt 5 } -Failure "api errors {0}% (threshold 5%)"
$failures += Test-Threshold -Value $row.StorageFreePercent `
    -Rule { param($n) $n -gt 12 } -Failure "free storage {0}% (threshold 12%)"

foreach ($pair in @(
    @{ Label = "indexers"; Healthy = $row.IndexersHealthy; Total = $row.IndexersTotal },
    @{ Label = "clients";  Healthy = $row.ClientsHealthy;  Total = $row.ClientsTotal })) {
    if ([string]::IsNullOrWhiteSpace([string]$pair.Healthy) -or [string]::IsNullOrWhiteSpace([string]$pair.Total)) {
        $failures += "$($pair.Label): no reading"
    } elseif ([int]$pair.Healthy -lt [int]$pair.Total) {
        $failures += "$($pair.Healthy)/$($pair.Total) $($pair.Label) healthy"
    }
}

# Three consecutive days of more failed jobs than the day before. One bad day
# is a retry; three in a row is a direction.
if (-not [string]::IsNullOrWhiteSpace([string]$row.JobsFailed)) {
    # The days before the one being judged, so closing an earlier day does not
    # count later ones as its past.
    $failedSeries = @($history | Where-Object { $_.DateUtc -lt $row.DateUtc } | Sort-Object -Property DateUtc | ForEach-Object { $_.JobsFailed }) + @($row.JobsFailed) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [int]$_ }
    if ($failedSeries.Count -ge 4) {
        $tail = $failedSeries[-4..-1]
        if ($tail[1] -gt $tail[0] -and $tail[2] -gt $tail[1] -and $tail[3] -gt $tail[2]) {
            $failures += "failed jobs rising three days running ($($tail -join ' -> '))"
        }
    }
} else {
    $failures += "failed jobs: no reading"
}

$failures = @($failures | Where-Object { $_ })

$result = if ($failures.Count -gt 0) { "FAIL" }
    elseif ([string]::IsNullOrWhiteSpace($WorkflowNote)) { "ATTENTION" }
    else { "PASS" }

$reasons = if ($failures.Count -gt 0) { $failures -join "; " }
    elseif ($result -eq "ATTENTION") { "no workflow evidence recorded" }
    else { $WorkflowNote }

$row.WorkflowNote = if ([string]::IsNullOrWhiteSpace($WorkflowNote)) { "" } else { $WorkflowNote }
$row.Result = $result
$row.Reasons = $reasons

$allRows = @(@($history) + @([pscustomobject]$row) | Sort-Object -Property DateUtc)
($allRows | ForEach-Object { $_ | ConvertTo-Json -Compress }) | Set-Content -Path $historyPath -Encoding utf8

# Rewritten whole from the history, so the ledger always matches it.
$dailyPath = Join-Path $runDirectory "daily.md"
$markdown = @(
    "# Soak daily snapshots: $safeRunId"
    ""
    "Raw endpoint responses are stored beside this file. A failed endpoint is recorded as data; it does not abort the day's collection."
    ""
    "| UTC date | Ready | Critical alerts | Jobs queued | Jobs failed | API error % | Free storage % | Indexers healthy/total | Clients healthy/total | Result | Why |"
    "| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |"
)
foreach ($entry in $allRows) {
    $rowValues = @(
        $entry.DateUtc, $entry.Ready, $entry.CriticalAlerts, $entry.JobsQueued, $entry.JobsFailed,
        $entry.ApiErrorRatePercent, $entry.StorageFreePercent,
        "$($entry.IndexersHealthy)/$($entry.IndexersTotal)",
        "$($entry.ClientsHealthy)/$($entry.ClientsTotal)",
        $entry.Result, $entry.Reasons
    ) | ForEach-Object { if ([string]::IsNullOrWhiteSpace([string]$_)) { "n/a" } else { ([string]$_) -replace '\|', '/' } }
    $markdown += "| " + ($rowValues -join " | ") + " |"
}
$markdown | Set-Content -Path $dailyPath -Encoding utf8

Write-Host "Soak snapshot recorded: $runDirectory"
Write-Host "Daily evidence: $dailyPath"
foreach ($response in $responses) {
    $endpointResult = if ($response.Succeeded) { "PASS ($($response.StatusCode))" } else { "FAIL ($($response.StatusCode))" }
    Write-Host ("{0}: {1}" -f $response.Name, $endpointResult)
}

$colour = switch ($row.Result) { "PASS" { "Green" } "ATTENTION" { "Yellow" } default { "Red" } }
Write-Host ""
Write-Host ("Day {0} of the run: {1}" -f $allRows.Count, $row.Result) -ForegroundColor $colour
Write-Host "  $($row.Reasons)"
if ($row.Result -eq "ATTENTION") {
    Write-Host "  Record the workflow check with -WorkflowNote to close the day." -ForegroundColor Yellow
}

# A soak that records fourteen red days because the collector is broken is worse
# than one that records nothing, because it looks like evidence. If nothing could
# even be asked, say so as its own thing and fail the command - the row is still
# written, so the gap is visible in the ledger too.
$unreachable = @($responses | Where-Object { $_.Unreachable })
if ($responses.Count -gt 0 -and $unreachable.Count -eq $responses.Count) {
    Write-Host ""
    Write-Warning "No reading was taken. Every request failed before it reached Deluno, so today's row says nothing about the product:"
    foreach ($response in $unreachable) {
        Write-Warning ("  {0}: {1}" -f $response.Name, $response.Error)
    }
    Write-Warning "Fix the collector or the connection and run the day again."
    exit 1
}
if ($unreachable.Count -gt 0) {
    Write-Warning ("{0} of {1} endpoints could not be reached at all; those readings are absent, not zero." -f $unreachable.Count, $responses.Count)
}

# A failed day is evidence that needs triage, not a script crash: the row is
# written and the command succeeds, so a scheduler keeps running tomorrow. The
# one exception is above - a day where nothing could be asked is not a failing
# day, it is a missing one, and that does exit non-zero.
exit 0
