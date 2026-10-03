<#
.SYNOPSIS
    Brings SABnzbd's own settings to the shape Deluno expects.

.DESCRIPTION
    SABnzbd's configuration had gone on the rig: no category folder, relative
    complete/incomplete paths, and an API key Deluno no longer agreed with. A rig
    you have to reconfigure by hand before each run is a rig that will be
    unavailable again, so this is a script rather than a paragraph in a handover.

    Three parts, each one skipped when it is already right:

      folders   absolute complete and incomplete folders
      category  the tv category lands in TV
      deluno    the stored API key, read from SABnzbd rather than typed

    It does not add a news server. That is a real provider account and is
    entered in SABnzbd by the person who owns it.

.EXAMPLE
    ./scripts/lab/provision-usenet.ps1
#>
[CmdletBinding()]
param(
    [string] $ComputerName,
    [string] $UserName,
    [string] $Password,
    [string] $DelunoUrl,
    [string] $DelunoUser,
    [string] $DelunoPassword
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = Get-Rig
if (-not $ComputerName)    { $ComputerName = $rig.host }
if (-not $UserName)        { $UserName = $rig.userName }
if (-not $Password)        { $Password = $rig.password }
if (-not $Password)        { throw 'Pass -Password; the Windows administrator password is not stored in rig.json.' }
if (-not $DelunoUrl)       { $DelunoUrl = $rig.deluno.url }
if (-not $DelunoUser)      { $DelunoUser = $rig.deluno.userName }
if (-not $DelunoPassword)  { $DelunoPassword = Get-RigAppPassword }

$credential = New-Object System.Management.Automation.PSCredential(
    $UserName, (ConvertTo-SecureString $Password -AsPlainText -Force))

function Step($text) { Write-Host "  $text" }
function Head($text) { Write-Host "`n$text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- sabnzbd

Head 'SABnzbd'

$apiKey = Invoke-Command -ComputerName $ComputerName -Credential $credential -ScriptBlock {
    ((Get-Content 'C:\Deluno\Data\sabnzbd\sabnzbd.ini' |
        Select-String -Pattern '^api_key\s*=' | Select-Object -First 1).Line -split '=', 2)[1].Trim()
}
if (-not $apiKey) { throw 'Could not read the SABnzbd API key from its ini.' }
Step 'read the api key from sabnzbd.ini'

$sabApi = "http://${ComputerName}:8085/api"
function Sab([string] $query) {
    Invoke-RestMethod -Uri "$sabApi`?$query&output=json&apikey=$apiKey"
}

$config = (Sab 'mode=get_config').config

# The owner's layout (24 Sep 2026): finished downloads in C:\Downloads\Completed,
# SABnzbd's own downloading in D:\Incomplete\SABnzbd.
if ($config.misc.complete_dir -ne 'C:\Downloads\Completed') {
    Sab 'mode=set_config&section=misc&keyword=complete_dir&value=C:\Downloads\Completed' | Out-Null
    Sab 'mode=set_config&section=misc&keyword=download_dir&value=D:\Incomplete\SABnzbd' | Out-Null
    Step 'set absolute complete/incomplete folders'
} else {
    Step 'folders already absolute'
}

if (($config.categories | Where-Object name -eq 'tv').dir -eq 'TV') {
    Step "tv category already lands in TV"
} else {
    Sab 'mode=set_config&section=categories&keyword=tv&dir=TV&pp=3&priority=0' | Out-Null
    Step 'pointed the tv category at TV'
}

# ---------------------------------------------------------------- deluno

Head 'Deluno'

$token = (Invoke-RestMethod -Uri "$DelunoUrl/api/auth/login" -Method Post -ContentType 'application/json' `
    -Body (@{ username = $DelunoUser; password = $DelunoPassword } | ConvertTo-Json)).accessToken
$headers = @{ Authorization = "Bearer $token" }

$client = (Invoke-RestMethod -Uri "$DelunoUrl/api/download-clients" -Headers $headers) |
    Where-Object { $_.name -eq 'SABnzbd' }
if (-not $client) { throw 'Deluno has no SABnzbd download client configured.' }

$test = Invoke-RestMethod -Uri "$DelunoUrl/api/download-clients/$($client.id)/test" -Method Post -Headers $headers
if ($test.healthStatus -ne 'healthy') {
    # The key is read from SABnzbd rather than typed, so the two cannot drift
    # apart the way they had: Deluno was holding a key from an older install and
    # reporting a clear 403 that nobody was reading.
    Invoke-RestMethod -Uri "$DelunoUrl/api/download-clients/$($client.id)" -Method Put -Headers $headers `
        -ContentType 'application/json' `
        -Body (@{ password = $apiKey; tvCategory = 'tv'; moviesCategory = 'movies'; isEnabled = $true } | ConvertTo-Json) | Out-Null
    $test = Invoke-RestMethod -Uri "$DelunoUrl/api/download-clients/$($client.id)/test" -Method Post -Headers $headers
    Step "synced the api key ($($test.healthStatus))"
} else {
    Step 'client already healthy'
}
if ($test.healthStatus -ne 'healthy') { throw "Deluno still cannot reach SABnzbd: $($test.message)" }

Head 'SABnzbd and Deluno agree.'
