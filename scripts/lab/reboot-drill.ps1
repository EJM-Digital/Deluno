<#
.SYNOPSIS
    Restarts the rig with work in flight, and proves Deluno came back to it.

.DESCRIPTION
    A media manager runs on a machine that reboots — for updates, for power, by
    accident. The question this answers is not "does the service start again"
    but "does it still know what it was doing". So the state is recorded before
    the restart and compared after, rather than glancing at a health endpoint.

    What is captured on each side:

      catalogue   movie and series counts
      libraries   including each one's import workflow and processor
      clients     and their health as Deluno reports it
      dispatches  including archived, so the history is compared too
      handoffs    anything mid-flight with a processor
      episodes    Breaking Bad season 1, with the quality each one records

    The restart is a clean `Restart-Computer`, not a service bounce: a service
    bounce proves the service, and the point here is the machine.

.PARAMETER SkipReboot
    Capture the "before" reading and stop. Useful for checking what the drill
    will compare without restarting anything.
#>
[CmdletBinding()]
param(
    [string] $ComputerName,
    [string] $UserName,
    [string] $Password,
    [int]    $ReadyTimeoutMinutes = 10,
    [switch] $SkipReboot
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = Get-Rig
if (-not $ComputerName) { $ComputerName = $rig.host }
if (-not $UserName)     { $UserName = $rig.userName }
if (-not $Password)     { $Password = $rig.password }
if (-not $Password)     { throw 'Pass -Password; the Windows administrator password is not stored in rig.json.' }

$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($UserName, $secure)
$base = $rig.deluno.url

function Step($m) { Write-Host "  $m" }
function Head($m) { Write-Host ""; Write-Host $m }

function Get-Token {
    $login = Invoke-RestMethod "$base/api/auth/login" -Method Post -ContentType 'application/json' -Body (@{
        userName = $rig.deluno.userName; password = $rig.deluno.password } | ConvertTo-Json) -TimeoutSec 30
    @{ Authorization = "Bearer $($login.accessToken)" }
}

function Read-State {
    $h = Get-Token
    $episodes = (Invoke-RestMethod "$base/api/series/01a0a747cc9577f782df6ad8c227f374/inventory" -Headers $h -TimeoutSec 60).episodes |
        Where-Object { $_.seasonNumber -eq 1 -and $_.episodeNumber -le 3 } |
        ForEach-Object { "S01E{0:D2} hasFile={1} quality={2} cutoff={3}" -f $_.episodeNumber, $_.hasFile, $(if ($_.currentQuality) { $_.currentQuality } else { '-' }), $_.qualityCutoffMet }

    [ordered]@{
        movies     = (Invoke-RestMethod "$base/api/movies/page?pageSize=50" -Headers $h -TimeoutSec 60).items.Count
        series     = (Invoke-RestMethod "$base/api/series/page?pageSize=50" -Headers $h -TimeoutSec 60).items.Count
        libraries  = ((Invoke-RestMethod "$base/api/libraries" -Headers $h -TimeoutSec 60) | ForEach-Object { $p = if ($_.processorName) { $_.processorName } else { '-' }; "$($_.name):$($_.importWorkflow):$p" }) -join ', '
        clients    = ((Invoke-RestMethod "$base/api/download-clients" -Headers $h -TimeoutSec 60) | ForEach-Object { "$($_.name)=$($_.healthStatus)" }) -join ', '
        dispatches = (Invoke-RestMethod "$base/api/download-dispatches?pageSize=50&includeArchived=true" -Headers $h -TimeoutSec 60).items.Count
        handoffs   = ((Invoke-RestMethod "$base/api/integrations/processors/handoffs" -Headers $h -TimeoutSec 60) | ForEach-Object { "$($_.releaseName)=$($_.status)" }) -join ', '
        episodes   = $episodes -join ' | '
    }
}

Head 'Before the restart'
$before = Read-State
foreach ($k in $before.Keys) { Step ("{0,-11}{1}" -f $k, $before[$k]) }

if ($SkipReboot) {
    Head 'Stopped before restarting, as asked.'
    return
}

Head 'Restarting the rig'
Invoke-Command -ComputerName $ComputerName -Credential $credential -ScriptBlock { Restart-Computer -Force } -ErrorAction SilentlyContinue
Step 'restart requested'

# It has to go away before coming back, or "reachable" is just the old process.
$deadline = (Get-Date).AddMinutes($ReadyTimeoutMinutes)
$wentDown = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (-not (Test-Connection -ComputerName $ComputerName -Count 1 -Quiet -ErrorAction SilentlyContinue)) { $wentDown = $true; Step 'the rig went down'; break }
}
if (-not $wentDown) { Step 'warning: never saw the rig go down; it may have restarted faster than this could see' }

Head 'Waiting for Deluno'
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 10
    try {
        $r = Invoke-RestMethod "$base/api/health/ready" -TimeoutSec 10
        if ($r.ready) { $ready = $true; Step "ready after the restart ($($r.checks.Count) checks)"; break }
    } catch { }
}
if (-not $ready) { throw "Deluno did not become ready within $ReadyTimeoutMinutes minutes of the restart." }

Head 'After the restart'
$after = Read-State
foreach ($k in $after.Keys) { Step ("{0,-11}{1}" -f $k, $after[$k]) }

Head 'Comparison'
$differences = @()
foreach ($k in $before.Keys) {
    if ("$($before[$k])" -ne "$($after[$k])") {
        $differences += $k
        Step ("CHANGED {0}`n    before {1}`n    after  {2}" -f $k, $before[$k], $after[$k])
    }
}
if ($differences.Count -eq 0) {
    Head 'Deluno came back to exactly what it was doing.'
} else {
    Head ("Deluno came back, but {0} differed: {1}" -f $differences.Count, ($differences -join ', '))
    exit 1
}
