param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$BaseUrl = "http://127.0.0.1:5099",
    [string]$ApiKey,
    [string]$RunId = ("soak-" + (Get-Date -Format "yyyyMMdd")),
    [string]$OutputRoot = "artifacts/soak"
)

$ErrorActionPreference = "Stop"
Set-Location $Root

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

function Invoke-SoakEndpoint {
    param(
        [string]$Name,
        [string]$Uri
    )

    try {
        $response = Invoke-WebRequest -Uri $Uri -Headers $headers -UseBasicParsing -SkipHttpErrorCheck
        return [PSCustomObject]@{
            Name = $Name
            Uri = $Uri
            StatusCode = [int]$response.StatusCode
            Succeeded = ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300)
            Body = [string]$response.Content
            Error = $null
        }
    }
    catch {
        return [PSCustomObject]@{
            Name = $Name
            Uri = $Uri
            StatusCode = 0
            Succeeded = $false
            Body = ""
            Error = $_.Exception.Message
        }
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

$dailyPath = Join-Path $runDirectory "daily.md"
if (-not (Test-Path $dailyPath)) {
    @(
        "# Soak daily snapshots: $safeRunId"
        ""
        "Raw endpoint responses are stored beside this file. A failed endpoint is recorded as data; it does not abort the day's collection."
        ""
        "| UTC date | Ready | Critical alerts | Jobs queued | Jobs failed | API error % | Free storage % | Indexers healthy/total | Clients healthy/total |"
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |"
    ) | Out-File -FilePath $dailyPath -Encoding utf8
}

$rowValues = @(
    $row.DateUtc, $row.Ready, $row.CriticalAlerts, $row.JobsQueued, $row.JobsFailed,
    $row.ApiErrorRatePercent, $row.StorageFreePercent,
    "$($row.IndexersHealthy)/$($row.IndexersTotal)",
    "$($row.ClientsHealthy)/$($row.ClientsTotal)"
) | ForEach-Object { if ([string]::IsNullOrWhiteSpace([string]$_)) { "n/a" } else { [string]$_ } }
("| " + ($rowValues -join " | ") + " |") | Add-Content -Path $dailyPath -Encoding utf8

Write-Host "Soak snapshot recorded: $runDirectory"
Write-Host "Daily evidence: $dailyPath"
foreach ($response in $responses) {
    $result = if ($response.Succeeded) { "PASS ($($response.StatusCode))" } else { "FAIL ($($response.StatusCode))" }
    Write-Host ("{0}: {1}" -f $response.Name, $result)
}

# A failed day is evidence that needs triage, not a script crash. Keep the
# command usable from a daily scheduler so it always leaves a row behind.
exit 0
