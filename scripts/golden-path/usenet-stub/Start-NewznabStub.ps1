<#
.SYNOPSIS
    A minimal Newznab API on 127.0.0.1 that offers one stub Usenet release (golden path, step 6).

.DESCRIPTION
    t=caps, t=search, t=movie (and an empty answer for t=tvsearch) return the release written by
    New-UsenetRelease.ps1, with its .nzb link; t=get serves the .nzb. Any API key, or none, is accepted. It
    never reaches the internet. Runs on Windows PowerShell 5.1 and blocks until it is stopped.

.PARAMETER Root
    The folder New-UsenetRelease.ps1 wrote (it holds release.json and the .nzb).

.PARAMETER Port
    The port on 127.0.0.1. Defaults to 1180. Use 0 for any free port.

.PARAMETER ReadyFile
    When given, "ready <port>" is written here once the server listens.

.EXAMPLE
    ./Start-NewznabStub.ps1 -Root C:\golden\usenet\release -Port 1180
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Root,
    [int] $Port = 1180,
    [string] $ReadyFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UsenetStub.Lib.ps1')

$Root = (Resolve-Path $Root).Path
$metaFile = Join-Path $Root 'release.json'
if (-not (Test-Path $metaFile)) { throw "No release.json under $Root. Run New-UsenetRelease.ps1 first." }
$meta = Get-Content -Raw $metaFile | ConvertFrom-Json

$server = New-Object Golden.NewznabServer
$server.Port = $Port
$server.ReleaseName = [string]$meta.name
$server.ReleaseId = [string]$meta.id
$server.SizeBytes = [long]$meta.sizeBytes
$server.NzbPath = Join-Path $Root ([string]$meta.nzb)
$server.Start()
Write-Host "Newznab stub listening on http://127.0.0.1:$($server.Port)/ offering $($server.ReleaseName)"
if ($ReadyFile) { Set-Content -Path $ReadyFile -Value "ready $($server.Port)" -Encoding ascii }

$last = -1
try {
    while ($true) {
        Start-Sleep -Seconds 5 # reason: the stub's report interval; it runs until stopped
        if ($server.Requests -ne $last) {
            $last = $server.Requests
            Write-Host ("{0:HH:mm:ss} requests {1}" -f (Get-Date), $server.Requests)
        }
    }
}
finally { $server.Stop() }
