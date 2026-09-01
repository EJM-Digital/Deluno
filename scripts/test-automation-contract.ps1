[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [string]$Username = 'admin',
    [string]$Password,
    [string]$ApiKey
)

$ErrorActionPreference = 'Stop'

function Read-ResponseBody {
    param([object]$Response)

    if ($null -eq $Response) {
        return ''
    }

    $stream = $Response.GetResponseStream()
    if ($null -eq $stream) {
        return ''
    }

    try {
        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    } finally {
        $stream.Dispose()
    }
}

function Invoke-JsonRequest {
    param(
        [ValidateSet('GET', 'POST')]
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body
    )

    $requestBody = if ($null -eq $Body) { $null } else { $Body | ConvertTo-Json -Depth 20 -Compress }
    try {
        $response = Invoke-WebRequest `
            -Method $Method `
            -Uri $Uri `
            -Headers $Headers `
            -ContentType 'application/json' `
            -Body $requestBody `
            -UseBasicParsing
        $statusCode = [int]$response.StatusCode
        $content = [string]$response.Content
    } catch {
        $response = $_.Exception.Response
        $statusCode = if ($null -eq $response -or $null -eq $response.StatusCode) { 0 } else { [int]$response.StatusCode }
        $content = if (-not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            $_.ErrorDetails.Message
        } elseif ($null -ne $response -and $null -ne $response.PSObject.Methods['GetResponseStream']) {
            Read-ResponseBody $response
        } else {
            ''
        }
    }

    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $json = $content | ConvertFrom-Json } catch { $json = $null }
    }

    return [pscustomobject]@{
        StatusCode = $statusCode
        Content = $content
        Json = $json
    }
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw "Automation contract failed: $Message"
    }

    Write-Host "PASS  $Message" -ForegroundColor Green
}

function Assert-Status {
    param(
        [object]$Response,
        [int[]]$Expected,
        [string]$Operation
    )

    Assert-Condition ($Expected -contains $Response.StatusCode) `
        "$Operation returned HTTP $($Response.StatusCode), expected $($Expected -join ' or ')."
}

$root = $BaseUrl.TrimEnd('/')
$api = "$root/api/v1"
$headers = @{}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Password)) 'a password is supplied when no API key is used'
    $login = Invoke-JsonRequest `
        -Method 'POST' `
        -Uri "$root/api/auth/login" `
        -Headers @{} `
        -Body ([ordered]@{ username = $Username; password = $Password })
    Assert-Status $login @(200) 'login'
    Assert-Condition ($null -ne $login.Json.accessToken) 'login returns an access token'
    $headers['Authorization'] = "Bearer $($login.Json.accessToken)"
} else {
    $headers['X-Api-Key'] = $ApiKey
}

$openApi = Invoke-JsonRequest -Method 'GET' -Uri "$root/api/openapi/v1.json" -Headers $headers
Assert-Status $openApi @(200) 'OpenAPI document'
Assert-Condition ($null -ne $openApi.Json.paths.'/api/automation/catalogue/bulk') 'OpenAPI documents the bulk catalogue route'
Assert-Condition ($null -ne $openApi.Json.paths.'/api/automation/summary') 'OpenAPI documents the automation summary route'

$templates = Invoke-JsonRequest -Method 'GET' -Uri "$api/api-keys/scope-templates" -Headers $headers
Assert-Status $templates @(200) 'API-key scope templates'
$templateIds = @($templates.Json | ForEach-Object { $_.id })
foreach ($requiredTemplate in @('dashboard-read', 'automation', 'home-assistant', 'native-mobile')) {
    Assert-Condition ($templateIds -contains $requiredTemplate) "scope template '$requiredTemplate' is published"
}

$readiness = Invoke-JsonRequest -Method 'GET' -Uri "$api/health/ready" -Headers $headers
Assert-Status $readiness @(200, 503) 'versioned readiness endpoint'
Assert-Condition ($null -ne $readiness.Json) 'readiness returns structured JSON'

$summary = Invoke-JsonRequest -Method 'GET' -Uri "$api/automation/summary" -Headers $headers
Assert-Status $summary @(200) 'versioned automation summary'
Assert-Condition ($null -ne $summary.Json.readiness) 'automation summary includes readiness'
Assert-Condition ($null -ne $summary.Json.queue) 'automation summary includes queue counts'
Assert-Condition ($null -ne $summary.Json.imports) 'automation summary includes import counts'

$idempotencyKey = "automation-contract-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))"
$recipeBody = [ordered]@{
    dryRun = $true
    items = @(
        [ordered]@{
            clientItemId = 'contract-movie-1'
            mediaType = 'movies'
            title = 'Deluno automation contract movie'
            year = 2024
            isReleased = $true
        },
        [ordered]@{
            clientItemId = 'contract-tv-1'
            mediaType = 'tv'
            title = 'Deluno automation contract series'
            year = 2024
            isReleased = $true
            episodes = @([ordered]@{ seasonNumber = 1; episodeNumber = 1; title = 'Pilot' })
        },
        [ordered]@{
            clientItemId = 'contract-invalid-1'
            mediaType = 'not-media'
            title = ''
        }
    )
}

$requestHeaders = @{}
foreach ($entry in $headers.GetEnumerator()) {
    $requestHeaders[$entry.Key] = $entry.Value
}
$requestHeaders['Idempotency-Key'] = $idempotencyKey

$first = Invoke-JsonRequest `
    -Method 'POST' `
    -Uri "$api/automation/catalogue/bulk" `
    -Headers $requestHeaders `
    -Body $recipeBody
Assert-Status $first @(200) 'mixed catalogue dry-run'
Assert-Condition ($first.Json.dryRun -eq $true) 'bulk result remains a dry-run'
Assert-Condition ($first.Json.total -eq 3) 'bulk result reports all three input items'
Assert-Condition ($first.Json.invalidCount -eq 1) 'bulk result reports the invalid item'
Assert-Condition ($first.Json.createdCount -eq 2) 'bulk result reports both valid items as would-create'
Assert-Condition (@($first.Json.items | Where-Object status -eq 'would-create').Count -eq 2) 'movie and TV items receive per-item would-create outcomes'
Assert-Condition (@($first.Json.items | Where-Object status -eq 'invalid').Count -eq 1) 'invalid input receives a per-item error outcome'

$second = Invoke-JsonRequest `
    -Method 'POST' `
    -Uri "$api/automation/catalogue/bulk" `
    -Headers $requestHeaders `
    -Body $recipeBody
Assert-Status $second @(200) 'idempotent replay of mixed catalogue dry-run'
Assert-Condition ((($first.Json | ConvertTo-Json -Depth 20 -Compress)) -eq (($second.Json | ConvertTo-Json -Depth 20 -Compress))) 'idempotent replay returns the same response'

$conflictingBody = [ordered]@{
    dryRun = $true
    items = @(
        [ordered]@{
            clientItemId = 'contract-movie-1'
            mediaType = 'movies'
            title = 'A different request using the same key'
            year = 2024
            isReleased = $true
        }
    )
}
$conflict = Invoke-JsonRequest `
    -Method 'POST' `
    -Uri "$api/automation/catalogue/bulk" `
    -Headers $requestHeaders `
    -Body $conflictingBody
Assert-Status $conflict @(409) 'conflicting reuse of the catalogue idempotency key'

Write-Host ''
Write-Host "Automation contract passed against $root" -ForegroundColor Cyan
