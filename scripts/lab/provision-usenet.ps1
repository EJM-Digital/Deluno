<#
.SYNOPSIS
    Brings the usenet half of the rig up, and proves it moved real bytes.

.DESCRIPTION
    The end-to-end plan wrote the usenet path off as unavailable. Two separate
    things were wrong, and only one of them was the one that got recorded.

    The first was that SABnzbd would not start unattended. That is fixed in
    ensure-rig-services.ps1, and the reason is written up there.

    The second is that SABnzbd's configuration had gone: no news server, no
    category folder, relative complete/incomplete paths, and an API key Deluno
    no longer agreed with. A rig you have to reconfigure by hand before each run
    is a rig that will be unavailable again, so this is a script rather than a
    paragraph in a handover.

    Four parts, each one skipped when it is already right:

      desktop  the NNTP/NZB fixture, serving one genuine yEnc article
      sabnzbd  the news server, the tv category, absolute folders
      deluno   the stored API key, read from SABnzbd rather than typed
      verify   with -Verify, actually fetch the article and compare hashes

    The fixture runs on the desktop rather than the VM, for the same reason
    torznab_seed.py does: there is no Python on the VM, and the point of these
    fixtures is that they are deterministic, not that they are remote.

.PARAMETER Verify
    Push the fixture NZB through SABnzbd, wait for it to complete, compare the
    decoded bytes against the source, and then remove what the check created.

.EXAMPLE
    ./scripts/lab/provision-usenet.ps1
    ./scripts/lab/provision-usenet.ps1 -Verify
#>
[CmdletBinding()]
param(
    [string] $ComputerName,
    [string] $DesktopAddress,
    [string] $UserName,
    [string] $Password,
    [string] $DelunoUrl,
    [string] $DelunoUser,
    [string] $DelunoPassword,
    [int]    $NntpPort,
    [int]    $NzbPort,
    [switch] $Verify
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = Get-Rig
if (-not $ComputerName)    { $ComputerName = $rig.host }
if (-not $UserName)        { $UserName = $rig.userName }
if (-not $Password)        { $Password = $rig.password }
if (-not $DelunoUrl)       { $DelunoUrl = $rig.deluno.url }
if (-not $DelunoUser)      { $DelunoUser = $rig.deluno.userName }
if (-not $DelunoPassword)  { $DelunoPassword = $rig.deluno.password }
if (-not $DesktopAddress)  { $DesktopAddress = $rig.fixtures.host }
if (-not $NntpPort)        { $NntpPort = $rig.fixtures.nntpPort }
if (-not $NzbPort)         { $NzbPort = $rig.fixtures.nzbPort }

$repoRoot   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# Repo-relative for the same reason torznab_seed.py is: nothing on the developer
# machine lives outside C:\Projects, and C:\Deluno meant something different on
# the rig than it did here.
$fixtureRoot = Join-Path $repoRoot 'vendor\e2e-fixtures'
$fixtureDir = Join-Path $fixtureRoot 'data'
$sourceMp4  = Join-Path $fixtureDir 'bbb.mp4'
$release    = 'Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO'
$article    = Join-Path $fixtureDir "$release.mkv"

$credential = New-Object System.Management.Automation.PSCredential(
    $UserName, (ConvertTo-SecureString $Password -AsPlainText -Force))

function Step($text) { Write-Host "  $text" }
function Head($text) { Write-Host "`n$text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- desktop

Head 'Fixture on the desktop'

if (-not (Test-Path $sourceMp4)) {
    throw "The fixture media is missing: $sourceMp4. See scripts/lab/README.md."
}

if (-not (Test-Path $article)) {
    # A short cut of the same Creative Commons source. Small on purpose: the
    # fixture yEnc-encodes the whole article into memory at startup, in Python,
    # so a 60 MB file costs a minute of startup and most of a gigabyte to prove
    # exactly what 2.5 MB proves.
    $ffmpeg = Join-Path $repoRoot 'tools\ffmpeg\win-x64\ffmpeg.exe'
    if (-not (Test-Path $ffmpeg)) { throw "ffmpeg not found at $ffmpeg" }
    & $ffmpeg -y -loglevel error -i $sourceMp4 -t 25 -c copy $article
    Step "cut $release.mkv from bbb.mp4"
} else {
    Step "article already present ($([int]((Get-Item $article).Length / 1KB)) KB)"
}

$listening = Get-NetTCPConnection -State Listen -LocalPort $NntpPort -ErrorAction SilentlyContinue
if ($listening) {
    Step "nntp fixture already listening on $NntpPort"
} else {
    $logDir = Join-Path $fixtureRoot 'logs'
    New-Item -ItemType Directory -Force $logDir | Out-Null
    Start-Process -FilePath (Get-Command python).Source -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logDir 'nntp.out') `
        -RedirectStandardError  (Join-Path $logDir 'nntp.err') `
        -ArgumentList @(
            '-u', (Join-Path $PSScriptRoot 'fake-nntp-server.py'),
            '--bind', '0.0.0.0', '--port', $NntpPort, '--http-port', $NzbPort,
            '--article', $article,
            '--message-id', 'deluno-e2e@fixture',
            '--nzb-name', 'fixture.nzb',
            '--log', (Join-Path $logDir 'nntp.log'))

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and
           -not (Get-NetTCPConnection -State Listen -LocalPort $NntpPort -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 2
    }
    if (-not (Get-NetTCPConnection -State Listen -LocalPort $NntpPort -ErrorAction SilentlyContinue)) {
        throw "The nntp fixture did not come up. See $logDir\nntp.err."
    }
    Step "started nntp on $NntpPort and the nzb on $NzbPort"
}

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

if ($config.misc.complete_dir -ne 'C:\Deluno\Downloads-Complete') {
    Sab 'mode=set_config&section=misc&keyword=complete_dir&value=C:\Deluno\Downloads-Complete' | Out-Null
    Sab 'mode=set_config&section=misc&keyword=download_dir&value=C:\Deluno\Downloads-Incomplete' | Out-Null
    Step 'set absolute complete/incomplete folders'
} else {
    Step 'folders already absolute'
}

$server = $config.servers | Where-Object { $_.host -eq $DesktopAddress -and $_.port -eq $NntpPort }
if ($server) {
    Step "news server already points at ${DesktopAddress}:$NntpPort"
} else {
    Sab (@(
        'mode=set_config', 'section=servers', 'keyword=deluno-e2e-fixture',
        "host=$DesktopAddress", "port=$NntpPort", 'username=deluno', 'password=fixture',
        'connections=2', 'enable=1', 'ssl=0', 'priority=0', 'displayname=Deluno E2E fixture'
    ) -join '&') | Out-Null
    Step "added the news server at ${DesktopAddress}:$NntpPort"
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

# ---------------------------------------------------------------- verify

if (-not $Verify) {
    Head 'Up. Re-run with -Verify to move real bytes through it.'
    return
}

Head 'Verifying with a real transfer'

$nzbUrl = "http://${DesktopAddress}:$NzbPort/fixture.nzb"
$added = Sab "mode=addurl&name=$([uri]::EscapeDataString($nzbUrl))&cat=tv&nzbname=$release"
if (-not $added.status) { throw "SABnzbd refused the nzb: $($added | ConvertTo-Json -Compress)" }
$nzoId = $added.nzo_ids[0]
Step "queued $nzoId"

$deadline = (Get-Date).AddMinutes(5)
$entry = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 4
    $entry = (Sab 'mode=history&limit=10').history.slots | Where-Object { $_.nzo_id -eq $nzoId }
    if ($entry -and $entry.status -in 'Completed', 'Failed') { break }
}
if (-not $entry) { throw 'The download never reached history.' }
if ($entry.status -ne 'Completed') { throw "SABnzbd reported $($entry.status): $($entry.fail_message)" }
Step "completed into $($entry.storage)"

$expected = (Get-FileHash $article -Algorithm SHA256).Hash
$actual = Invoke-Command -ComputerName $ComputerName -Credential $credential -ArgumentList $entry.storage -ScriptBlock {
    param($path) (Get-FileHash $path -Algorithm SHA256).Hash
}
if ($expected -ne $actual) { throw "The decoded file does not match the fixture.`n  fixture $expected`n  decoded $actual" }
Step "sha256 matches the fixture ($expected)"

Sab "mode=history&name=delete&value=$nzoId&del_files=1" | Out-Null
Invoke-Command -ComputerName $ComputerName -Credential $credential -ScriptBlock {
    Remove-Item 'C:\Deluno\Downloads-Complete\TV' -Recurse -Force -ErrorAction SilentlyContinue
}
Step 'removed what the check created'

Head 'The usenet path moves real bytes and lands them where Deluno looks.'
