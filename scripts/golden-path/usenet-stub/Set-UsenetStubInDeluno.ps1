<#
.SYNOPSIS
    Points Deluno at the Usenet stub through Deluno's own API: a Usenet provider on 127.0.0.1 and a Newznab indexer (golden path, step 5).

.DESCRIPTION
    Run inside the golden-path VM, after the first account exists and SABnzbd or NZBGet is connected (the guided
    setup does that). It signs in with the run's throwaway Deluno login, then:
      1. POST /api/download-clients/usenet-provider  host 127.0.0.1, the NNTP stub's port, a dummy login,
         which Deluno writes into every Usenet client it manages;
      2. POST /api/indexers  a Newznab indexer at http://127.0.0.1:<newznab port> with a dummy API key;
      3. reads the provider status and the indexer's health back and prints them.
    Both logins are throwaway values the stub accepts; no real credential is used or needed. It waits up to
    -WaitMinutes for a Usenet client to be connected, because the provider can only be written to one.

    Runs on Windows PowerShell 5.1.

.PARAMETER Username
    The throwaway Deluno login created for this run.

.PARAMETER Password
    Its password. Never printed.

.PARAMETER DelunoUrl
    Deluno's address inside the VM. Defaults to http://localhost:7879.

.EXAMPLE
    ./Set-UsenetStubInDeluno.ps1 -Username golden -Password <the run's password>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Username,
    [Parameter(Mandatory)] [string] $Password,
    [string] $DelunoUrl = 'http://localhost:7879',
    [int] $NntpPort = 1119,
    [int] $NewznabPort = 1180,
    [string] $IndexerName = 'Golden Newznab stub',
    [int] $WaitMinutes = 5
)

$ErrorActionPreference = 'Stop'
$DelunoUrl = $DelunoUrl.TrimEnd('/')

function Invoke-Deluno([string] $Method, [string] $Path, $Body = $null) {
    $params = @{ Uri = "$DelunoUrl$Path"; Method = $Method; UseBasicParsing = $true; TimeoutSec = 60; ContentType = 'application/json' }
    if ($script:token) { $params.Headers = @{ Authorization = "Bearer $script:token" } }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 6) }
    try {
        $resp = Invoke-WebRequest @params
        if ($resp.Content) { return ($resp.Content | ConvertFrom-Json) }
        return $null
    }
    catch {
        $detail = ''
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = $_.ErrorDetails.Message }
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        throw [Exception]::new("$Method $Path answered $code. $detail")
    }
}

$script:token = $null
$login = Invoke-Deluno 'POST' '/api/auth/login' @{ username = $Username; password = $Password; keepSignedIn = $false }
$script:token = $login.accessToken
if (-not $script:token) { throw 'Deluno did not return a sign-in token.' }
Write-Host "Signed in to Deluno as $Username."

# 1. The provider: a throwaway login the NNTP stub accepts.
$nntpUser = 'golden'
$nntpPassword = 'Gp-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$providerBody = @{ host = '127.0.0.1'; port = $NntpPort; useSsl = $false; username = $nntpUser; password = $nntpPassword; connections = 2 }
$deadline = (Get-Date).AddMinutes($WaitMinutes)
$saved = $null
while ($true) {
    try { $saved = Invoke-Deluno 'POST' '/api/download-clients/usenet-provider' $providerBody; break }
    catch {
        if ((Get-Date) -gt $deadline -or $_.Exception.Message -notmatch 'no SABnzbd or NZBGet connection') { throw }
        Write-Host 'No SABnzbd or NZBGet connection in Deluno yet; waiting 15 s (connect them in the guided setup).'
        Start-Sleep -Seconds 15 # reason: poll interval inside a loop bounded by its deadline
    }
}
foreach ($c in @($saved.clients)) { Write-Host ("Provider: {0} - {1}" -f $c.clientName, $c.message) }
if (-not $saved.saved) { throw 'Deluno could not give the provider to every Usenet client.' }

# 2. The indexer.
$baseUrl = "http://127.0.0.1:$NewznabPort"
$existing = @(Invoke-Deluno 'GET' '/api/indexers') | Where-Object { $_.baseUrl -like "$baseUrl*" }
if ($existing) {
    Write-Host "Indexer: $($existing[0].name) already points at $baseUrl."
}
else {
    $indexerBody = @{
        name = $IndexerName; protocol = 'newznab'; privacy = 'public'; baseUrl = $baseUrl; apiKey = 'golden'
        priority = 25; categories = '2000,2040,5000,5040'; mediaScope = 'both'; isEnabled = $true; keepName = $true; routeToLibraries = $true
    }
    $created = Invoke-Deluno 'POST' '/api/indexers' $indexerBody
    Write-Host "Indexer: added $($created.name) at $baseUrl."
}

# 3. Read it back.
Start-Sleep -Seconds 3 # reason: lets Deluno store the setting before it is read back
$status = Invoke-Deluno 'GET' '/api/download-clients/usenet-provider'
foreach ($c in @($status.clients)) { Write-Host ("Usenet client {0}: provider set = {1}" -f $c.clientName, $c.hasProvider) }
$indexer = @(Invoke-Deluno 'GET' '/api/indexers') | Where-Object { $_.baseUrl -like "$baseUrl*" } | Select-Object -First 1
if ($indexer) { Write-Host ("Indexer {0}: health '{1}' {2}" -f $indexer.name, $indexer.healthStatus, $indexer.lastHealthMessage) }
Write-Host 'Done. In Deluno: press Test on each Usenet client and on the indexer, add Big Buck Bunny (2008), and search.'
