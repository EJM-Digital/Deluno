param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$BackendUrl = "http://127.0.0.1:5099",
    [string]$FrontendUrl = "http://127.0.0.1:5173",
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

$dotnetPath = Join-Path $Root ".dotnet\dotnet.exe"
$hostProject = Join-Path $Root "src\Deluno.Host\Deluno.Host.csproj"
$appStateRoot = Join-Path $Root ".deluno"
$logRoot = Join-Path $appStateRoot "logs"
$dataRoot = Join-Path $appStateRoot "data"
$statusPath = Join-Path $appStateRoot "boot-health.json"

New-Item -ItemType Directory -Force -Path $logRoot, $dataRoot | Out-Null

function Get-LogPath([string]$Name) {
    Join-Path $logRoot $Name
}

function Test-Url([string]$Url) {
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
        return @{
            Ready = $true
            StatusCode = [int]$response.StatusCode
            Error = $null
        }
    } catch {
        $statusCode = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        return @{
            Ready = $false
            StatusCode = $statusCode
            Error = $_.Exception.Message
        }
    }
}

function Wait-ForUrl([string]$Url, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null

    while ((Get-Date) -lt $deadline) {
        $last = Test-Url $Url
        if ($last.Ready) {
            return $last
        }

        Start-Sleep -Seconds 1
    }

    if ($null -eq $last) {
        $last = Test-Url $Url
    }

    return $last
}

function Get-ListeningProcessId([int]$Port) {
    try {
        $connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
            Select-Object -First 1
        if ($connection) {
            return $connection.OwningProcess
        }
    } catch {
        return $null
    }

    return $null
}

function Get-Port([string]$Url) {
    ([Uri]$Url).Port
}

function ConvertTo-SingleQuotedPowerShellString([string]$Value) {
    "'" + $Value.Replace("'", "''") + "'"
}

function Start-LoggedProcess(
    [string]$FileName,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [string]$StdoutPath,
    [string]$StderrPath
) {
    Start-Process `
        -FilePath $FileName `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $StdoutPath `
        -RedirectStandardError $StderrPath `
        -WindowStyle Hidden `
        -PassThru
}

function Start-OrReuseBackend {
    $healthUrl = "$BackendUrl/health"
    $existing = Test-Url $healthUrl
    if ($existing.Ready) {
        return @{
            Started = $false
            ProcessId = Get-ListeningProcessId (Get-Port $BackendUrl)
            Health = $existing
        }
    }

    if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
        throw "Missing repo-local .NET SDK: $dotnetPath"
    }

    $dataRootLiteral = ConvertTo-SingleQuotedPowerShellString $dataRoot
    $dotnetLiteral = ConvertTo-SingleQuotedPowerShellString $dotnetPath
    $hostProjectLiteral = ConvertTo-SingleQuotedPowerShellString $hostProject
    $backendUrlLiteral = ConvertTo-SingleQuotedPowerShellString $BackendUrl
    $backendCommand = "`$env:Storage__DataRoot = $dataRootLiteral; & $dotnetLiteral run --project $hostProjectLiteral --urls $backendUrlLiteral"

    $process = Start-LoggedProcess `
        -FileName "powershell" `
        -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $backendCommand) `
        -WorkingDirectory $Root `
        -StdoutPath (Get-LogPath "backend.log") `
        -StderrPath (Get-LogPath "backend.err.log")

    return @{
        Started = $true
        ProcessId = $process.Id
        Health = Wait-ForUrl $healthUrl $TimeoutSeconds
    }
}

function Start-OrReuseFrontend {
    $existing = Test-Url $FrontendUrl
    if ($existing.Ready) {
        return @{
            Started = $false
            ProcessId = Get-ListeningProcessId (Get-Port $FrontendUrl)
            Health = $existing
        }
    }

    $process = Start-LoggedProcess `
        -FileName "npm.cmd" `
        -Arguments @("--workspace", "apps/web", "run", "dev", "--", "--host", "127.0.0.1") `
        -WorkingDirectory $Root `
        -StdoutPath (Get-LogPath "frontend.log") `
        -StderrPath (Get-LogPath "frontend.err.log")

    return @{
        Started = $true
        ProcessId = $process.Id
        Health = Wait-ForUrl $FrontendUrl $TimeoutSeconds
    }
}

$backend = Start-OrReuseBackend
$frontend = Start-OrReuseFrontend
$readyHealth = Test-Url "$BackendUrl/api/health/ready"

$status = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    root = $Root
    dataRoot = $dataRoot
    backend = [ordered]@{
        url = $BackendUrl
        healthUrl = "$BackendUrl/health"
        readinessUrl = "$BackendUrl/api/health/ready"
        startedByScript = $backend.Started
        processId = $backend.ProcessId
        ready = $backend.Health.Ready
        statusCode = $backend.Health.StatusCode
        error = $backend.Health.Error
        readinessStatusCode = $readyHealth.StatusCode
        readinessError = $readyHealth.Error
        log = Get-LogPath "backend.log"
        errorLog = Get-LogPath "backend.err.log"
    }
    frontend = [ordered]@{
        url = $FrontendUrl
        startedByScript = $frontend.Started
        processId = $frontend.ProcessId
        ready = $frontend.Health.Ready
        statusCode = $frontend.Health.StatusCode
        error = $frontend.Health.Error
        log = Get-LogPath "frontend.log"
        errorLog = Get-LogPath "frontend.err.log"
    }
}

$status | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusPath -Encoding UTF8

Write-Host "Deluno local app status written to $statusPath"
Write-Host "Backend:  $BackendUrl (pid: $($status.backend.processId), ready: $($status.backend.ready))"
Write-Host "Frontend: $FrontendUrl (pid: $($status.frontend.processId), ready: $($status.frontend.ready))"
Write-Host "Logs:     $logRoot"

if (-not $backend.Health.Ready -or -not $frontend.Health.Ready) {
    exit 1
}
