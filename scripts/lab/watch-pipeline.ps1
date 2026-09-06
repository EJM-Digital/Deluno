<#
.SYNOPSIS
    Prints where an acquisition has got to, and where it stopped.

.DESCRIPTION
    The telemetry summary, each queue item's status, the processor hand-offs
    with their output paths and import job ids, the job queue, and the
    dispatch/processing/import activity. The fastest way to answer "is it
    stuck, and where".
#>
[CmdletBinding()]
param(
    [string] $DelunoUrl,
    [string] $UserName,
    [string] $Password
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = Get-Rig
if (-not $DelunoUrl) { $DelunoUrl = $rig.deluno.url }
if (-not $UserName)  { $UserName = $rig.deluno.userName }
if (-not $Password)  { $Password = $rig.deluno.password }

$login = Invoke-RestMethod -Uri "$DelunoUrl/api/auth/login" -Method Post -ContentType 'application/json' `
    -Body (@{ username = $UserName; password = $Password } | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($login.accessToken)" }

$o = Invoke-RestMethod -Uri "$DelunoUrl/api/download-clients/telemetry" -Headers $h
"summary : " + ($o.summary | ConvertTo-Json -Compress)
"queue   : " + (($o.clients.queue | ForEach-Object { "$($_.status)" }) -join ', ')

$hf = Invoke-RestMethod -Uri "$DelunoUrl/api/integrations/processors/handoffs" -Headers $h
$rows = if ($hf.items) { $hf.items } else { $hf }
"handoffs: " + ($rows.Count)
$rows | Select-Object releaseName, status, outputPath, importJobId | Format-List

"jobs    :"
(Invoke-RestMethod -Uri "$DelunoUrl/api/jobs" -Headers $h).items |
    Select-Object jobType, status, lastError | Format-Table -AutoSize

"activity:"
(Invoke-RestMethod -Uri "$DelunoUrl/api/activity" -Headers $h).items |
    Where-Object { $_.category -match 'processing|import|dispatch' } |
    Select-Object -First 8 category, message | Format-Table -AutoSize -Wrap
