<#
.SYNOPSIS
    A tiny NNTP server on 127.0.0.1 that serves a stub Usenet release to SABnzbd or NZBGet (golden path, step 6).

.DESCRIPTION
    Speaks what a downloader needs: the greeting, CAPABILITIES, MODE READER, AUTHINFO USER and PASS (any login is
    accepted), GROUP, STAT, ARTICLE, HEAD and BODY by message-id, DATE and QUIT. Articles come from
    <Root>\articles\<message-id>.art, which New-UsenetRelease.ps1 writes. It never reaches the internet and needs
    no account. Runs on Windows PowerShell 5.1 and blocks until it is stopped.

.PARAMETER Root
    The folder New-UsenetRelease.ps1 wrote (it holds articles\).

.PARAMETER Port
    The port on 127.0.0.1. Defaults to 1119. Use 0 for any free port; the port is printed and written to -ReadyFile.

.PARAMETER HangFile
    A file of message-ids (one a line) that are never answered, so a download stops just short of the end (Stuckfilm, #1225).

.PARAMETER ArticleDelayMs
    A pause before each article named in -SlowFile, so that download crawls (the "too slow" rule).

.PARAMETER SlowFile
    A file of message-ids (one a line) that -ArticleDelayMs applies to.

.PARAMETER ReadyFile
    When given, "ready <port>" is written here once the server listens, so a caller can wait for it.

.EXAMPLE
    ./Start-NntpStub.ps1 -Root C:\golden\usenet\release -Port 1119
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Root,
    [int] $Port = 1119,
    [string] $ReadyFile,
    # A file of message-ids (one a line) the server never answers, read on every request: a news server that stalls on its last article.
    [string] $HangFile,
    # A pause before each article named in -SlowFile, in milliseconds: a slow news server.
    [int] $ArticleDelayMs = 0,
    # A file of message-ids (one a line) that -ArticleDelayMs applies to.
    [string] $SlowFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UsenetStub.Lib.ps1')

$Root = (Resolve-Path $Root).Path
if (-not (Test-Path (Join-Path $Root 'articles'))) { throw "No articles folder under $Root. Run New-UsenetRelease.ps1 first." }

$server = New-Object Golden.NntpServer
$server.Root = $Root
$server.Port = $Port
$server.HangFile = $HangFile
$server.ArticleDelayMs = $ArticleDelayMs
$server.SlowFile = $SlowFile
$server.Start()
Write-Host "NNTP stub listening on 127.0.0.1:$($server.Port), serving $Root"
if ($ReadyFile) { Set-Content -Path $ReadyFile -Value "ready $($server.Port)" -Encoding ascii }

$lastServed = -1
try {
    while ($true) {
        Start-Sleep -Seconds 5 # reason: the stub's report interval; it runs until stopped
        if ($server.ArticlesServed -ne $lastServed) {
            $lastServed = $server.ArticlesServed
            Write-Host ("{0:HH:mm:ss} connections {1}, articles served {2}, not found {3}" -f (Get-Date), $server.Connections, $server.ArticlesServed, $server.ArticlesMissed)
        }
    }
}
finally { $server.Stop() }
