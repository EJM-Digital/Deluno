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

    if ($null -eq $Response) { return '' }
    $stream = $Response.GetResponseStream()
    if ($null -eq $stream) { return '' }
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
        $response = Invoke-WebRequest -Method $Method -Uri $Uri -Headers $Headers -ContentType 'application/json' -Body $requestBody -UseBasicParsing
        $statusCode = [int]$response.StatusCode
        $content = [string]$response.Content
    } catch [System.Net.WebException] {
        $response = $_.Exception.Response
        $statusCode = if ($null -eq $response) { 0 } else { [int]$response.StatusCode }
        $content = Read-ResponseBody $response
    }

    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $json = $content | ConvertFrom-Json } catch { $json = $null }
    }

    [pscustomobject]@{
        StatusCode = $statusCode
        Content = $content
        Json = $json
    }
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) { throw "Guide package contract failed: $Message" }
    Write-Host "PASS  $Message" -ForegroundColor Green
}

function Assert-Status {
    param([object]$Response, [int]$Expected, [string]$Operation)

    Assert-Condition ($Response.StatusCode -eq $Expected) "$Operation returned HTTP $($Response.StatusCode), expected $Expected."
}

$root = $BaseUrl.TrimEnd('/')
$headers = @{}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Password)) 'a password is supplied when no API key is used'
    $login = Invoke-JsonRequest -Method 'POST' -Uri "$root/api/auth/login" -Headers @{} -Body ([ordered]@{ username = $Username; password = $Password })
    Assert-Status $login 200 'login'
    Assert-Condition ($null -ne $login.Json.accessToken) 'login returns an access token'
    $headers['Authorization'] = "Bearer $($login.Json.accessToken)"
} else {
    $headers['X-Api-Key'] = $ApiKey
}

$packageResponse = Invoke-JsonRequest -Method 'GET' -Uri "$root/api/v1/guides/trash/package" -Headers $headers
Assert-Status $packageResponse 200 'guide package'
$package = $packageResponse.Json
Assert-Condition (-not [string]::IsNullOrWhiteSpace($package.id)) 'package has a stable id'
Assert-Condition ($package.version -gt 0) 'package has a positive version'
Assert-Condition (-not [string]::IsNullOrWhiteSpace($package.integritySha256)) 'package has an integrity hash'
Assert-Condition (-not [string]::IsNullOrWhiteSpace($package.source.upstreamRevision)) 'package retains upstream revision provenance'

$inventoryResponse = Invoke-JsonRequest -Method 'GET' -Uri "$root/api/v1/guides/trash/inventory" -Headers $headers
Assert-Status $inventoryResponse 200 'guide capability inventory'
$inventory = $inventoryResponse.Json
Assert-Condition ($inventory.totalItemCount -gt 0) 'inventory contains guide capabilities'
Assert-Condition ($inventory.totalItemCount -eq @($inventory.items).Count) 'inventory total reconciles with item rows'
Assert-Condition (@($inventory.unaccounted).Count -eq 0) 'inventory has zero unexplained capabilities'
Assert-Condition (-not [string]::IsNullOrWhiteSpace($inventory.inventoryHash)) 'inventory has a deterministic hash'

$versionsResponse = Invoke-JsonRequest -Method 'GET' -Uri "$root/api/v1/guides/trash/versions" -Headers $headers
Assert-Status $versionsResponse 200 'guide package versions'
Assert-Condition (@($versionsResponse.Json | Where-Object { $_.package.id -eq $package.id -and $_.package.version -eq $package.version }).Count -ge 1) 'active package is represented in version history'

foreach ($profile in @($package.qualityProfiles)) {
    $profileId = [uri]::EscapeDataString([string]$profile.id)
    $mediaType = [uri]::EscapeDataString([string]$profile.mediaType)
    $compileResponse = Invoke-JsonRequest -Method 'GET' -Uri "$root/api/v1/guides/trash/profiles/$profileId/compile?mediaType=$mediaType" -Headers $headers
    Assert-Status $compileResponse 200 "profile '$($profile.id)' compilation"
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($compileResponse.Json.planHash)) "profile '$($profile.id)' has a compiled plan hash"
    Assert-Condition ($compileResponse.Json.profile.id -eq $profile.id) "profile '$($profile.id)' retains its stable profile id"
    Assert-Condition ($compileResponse.Json.plan.id -eq "guide/$($package.id)/$($profile.id)") "profile '$($profile.id)' compiles a package-scoped plan id"
}

Write-Host ''
Write-Host "Guide package contract passed against $root (package $($package.id) v$($package.version))" -ForegroundColor Cyan
