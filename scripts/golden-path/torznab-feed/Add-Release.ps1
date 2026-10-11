<#
.SYNOPSIS
    Adds one release to the golden-path Torznab feed (Start-TorznabFeed.ps1): a real torrent over a file, or a dummy one that nobody can download.

.DESCRIPTION
    Writes a .torrent into <Root>\torrents (Make-Torrent.ps1) and appends the release to <Root>\releases.jsonl, which the feed reads on
    every request. With -File the torrent hashes that file, so a client that finds the file where it expects it completes at once (a
    controlled film for the hand-off, pause and retry tests). With -DummyMb the torrent declares that size with random piece hashes and a
    tracker that does not exist, so it stalls (the profile and stall tests). Needs no network.

.PARAMETER Title   The release name the indexer offers.
.PARAMETER Cat     Torznab category: 2000 to 2045 for movies, 5000 to 5045 for TV.
.PARAMETER File    A local file to make the torrent over.
.PARAMETER Folder A local folder to make a multi-file torrent over (the release downloads into its own folder).
.PARAMETER DummyMb A size in MB for a torrent with no data.
.PARAMETER Tracker A tracker address to put in the torrent (the local tracker of scripts/recorded-replies).
.PARAMETER DeadTracker With -File: name a tracker that does not answer, as a real swarm that is down would (a complete torrent must still be handed off, #1183).
.PARAMETER Root    Where the feed keeps its files. Defaults to C:\golden\feed in the VM.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Title,
    [int] $Cat = 2000,
    [int] $Seeders = 50,
    [string] $File,
    [string] $Folder,
    [string] $Tracker,
    [int] $DummyMb = 0,
    [switch] $DeadTracker,
    [string] $TorrentName,
    [string] $Root = 'C:\golden\feed'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force "$Root\torrents" | Out-Null
$id = [guid]::NewGuid().ToString('N').Substring(0, 12)
$out = "$Root\torrents\$id.torrent"
$maker = Join-Path $PSScriptRoot 'Make-Torrent.ps1'
if (-not (Test-Path $maker)) { $maker = "$Root\Make-Torrent.ps1" }
if ($Folder) { $info = & $maker -Folder $Folder -Out $out -DeadTracker:$DeadTracker -Tracker $Tracker }
elseif ($File) { $info = & $maker -File $File -Out $out -DeadTracker:$DeadTracker -Tracker $Tracker }
else { $info = & $maker -Dummy -Length ([long]$DummyMb * 1MB) -Name $(if ($TorrentName) { $TorrentName } else { $Title }) -Out $out }
$i = $info | ConvertFrom-Json
$line = [pscustomobject]@{ title = $Title; cat = $Cat; seeders = $Seeders; size = $i.size; infohash = $i.infohash; torrent = "$id.torrent" } | ConvertTo-Json -Compress
Add-Content "$Root\releases.jsonl" $line -Encoding utf8
"added $Title ($($i.size) bytes, $($i.infohash))"
