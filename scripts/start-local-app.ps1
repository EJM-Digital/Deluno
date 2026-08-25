param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$BackendUrl = "http://127.0.0.1:5099",
    [string]$FrontendUrl = "http://127.0.0.1:5173",
    [string]$SecretsFile = ".env.local",
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

$repoDotnetPath = Join-Path $Root ".dotnet\dotnet.exe"
$npmFallbackPath = "C:\Program Files\nodejs\npm.cmd"
$powershellFallbackPath = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
$hostProject = Join-Path $Root "src\Deluno.Host\Deluno.Host.csproj"
$appStateRoot = Join-Path $Root ".deluno"
$logRoot = Join-Path $appStateRoot "logs"
$dataRoot = Join-Path $appStateRoot "data"
$statusPath = Join-Path $appStateRoot "boot-health.json"
$secretsPath = if ([System.IO.Path]::IsPathRooted($SecretsFile)) {
    $SecretsFile
} else {
    Join-Path $Root $SecretsFile
}

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

function Import-DeploymentSecrets([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }

    $allowedNames = @(
        "TMDB_API_KEY",
        "OMDB_API_KEY",
        "MDBLIST_API_KEY",
        "DELUNO_METADATA_PROVIDER_MODE",
        "DELUNO_METADATA_BROKER_URL"
    )
    $loadedNames = @()

    foreach ($line in Get-Content -LiteralPath $Path) {
        $match = [regex]::Match($line, '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$')
        if (-not $match.Success) {
            continue
        }

        $name = $match.Groups[1].Value
        if ($allowedNames -notcontains $name) {
            continue
        }

        $value = $match.Groups[2].Value.Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        if (-not [string]::IsNullOrWhiteSpace($value)) {
            [Environment]::SetEnvironmentVariable($name, $value, "Process")
            $loadedNames += $name
        }
    }

    return $loadedNames
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

function Resolve-NpmPath {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -ne $npmCommand) {
        return $npmCommand.Source
    }

    if (Test-Path -LiteralPath $npmFallbackPath -PathType Leaf) {
        return $npmFallbackPath
    }

    throw "npm.cmd was not found on PATH and no fallback path was found."
}

function Resolve-PowerShellPath {
    $psCommand = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($null -ne $psCommand) {
        return $psCommand.Source
    }

    if (Test-Path -LiteralPath $powershellFallbackPath -PathType Leaf) {
        return $powershellFallbackPath
    }

    throw "powershell.exe was not found on PATH and no fallback path was found."
}

function Resolve-DotNetPath {
    if (Test-Path -LiteralPath $repoDotnetPath -PathType Leaf) {
        return $repoDotnetPath
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnetCommand) {
        return $dotnetCommand.Source
    }

    throw "dotnet was not found on PATH and no repo-local SDK exists at $repoDotnetPath"
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

    $dotnetPath = Resolve-DotNetPath
    $dataRootLiteral = ConvertTo-SingleQuotedPowerShellString $dataRoot
    $dotnetLiteral = ConvertTo-SingleQuotedPowerShellString $dotnetPath
    $hostProjectLiteral = ConvertTo-SingleQuotedPowerShellString $hostProject
    $backendUrlLiteral = ConvertTo-SingleQuotedPowerShellString $BackendUrl
    $backendCommand = "`$env:Storage__DataRoot = $dataRootLiteral; & $dotnetLiteral run --project $hostProjectLiteral --urls $backendUrlLiteral"

    $powerShellPath = Resolve-PowerShellPath
    $process = Start-LoggedProcess `
        -FileName $powerShellPath `
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

    $npmPath = Resolve-NpmPath
    $process = Start-LoggedProcess `
        -FileName $npmPath `
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

$loadedSecretNames = Import-DeploymentSecrets $secretsPath
$backend = Start-OrReuseBackend
$readyHealth = Wait-ForUrl "$BackendUrl/api/health/ready" $TimeoutSeconds
$frontend = Start-OrReuseFrontend

$status = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    root = $Root
    dataRoot = $dataRoot
    deploymentSecrets = [ordered]@{
        path = $secretsPath
        found = Test-Path -LiteralPath $secretsPath -PathType Leaf
        loadedNames = $loadedSecretNames
    }
    backend = [ordered]@{
        url = $BackendUrl
        healthUrl = "$BackendUrl/health"
        readinessUrl = "$BackendUrl/api/health/ready"
        startedByScript = $backend.Started
        processId = $backend.ProcessId
        live = $backend.Health.Ready
        ready = $readyHealth.Ready
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
Write-Host "Backend:  $BackendUrl (pid: $($status.backend.processId), live: $($status.backend.live), ready: $($status.backend.ready))"
Write-Host "Frontend: $FrontendUrl (pid: $($status.frontend.processId), ready: $($status.frontend.ready))"
if ($loadedSecretNames.Count -gt 0) {
    Write-Host "Deployment credentials loaded for backend: $($loadedSecretNames -join ', ')"
} elseif (Test-Path -LiteralPath $secretsPath -PathType Leaf) {
    Write-Host "No Deluno deployment credentials were found in $secretsPath"
}
Write-Host "Logs:     $logRoot"

if (-not $backend.Health.Ready -or -not $readyHealth.Ready -or -not $frontend.Health.Ready) {
    exit 1
}
