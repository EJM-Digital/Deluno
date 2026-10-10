# Golden path: makes a .torrent. With -File it hashes the real file (and names no tracker, so the client that finds the
# file completes at once with no tracker error); with -Folder it hashes every file under that folder, in path order, as a
# multi-file torrent named after the folder (a release that downloads into its own folder); with -Dummy -Length N it makes
# a torrent nobody can ever download (random piece hashes, a tracker that does not exist). The "stuck just short of the end"
# test hashes the good file and puts a copy with its last piece damaged where the client looks (see suitemFixtures.Lib.ps1).
param([string]$File, [string]$Folder, [string]$Tracker, [string]$Out, [string]$Name, [switch]$Dummy, [switch]$DeadTracker, [long]$Length = 30MB, [int]$PieceKb = 256)
$ErrorActionPreference = 'Stop'
function BStr([string]$s) { $b = [Text.Encoding]::UTF8.GetBytes($s); [Text.Encoding]::ASCII.GetBytes("$($b.Length):") + $b }
function BInt([long]$n) { [Text.Encoding]::ASCII.GetBytes("i${n}e") }
function JoinBytes([object[]]$parts) { $ms = New-Object IO.MemoryStream; foreach ($p in $parts) { $ms.Write($p, 0, $p.Length) }; $ms.ToArray() }
$pieceLen = $PieceKb * 1024
$filesEntry = $null
if ($Dummy) { $total = $Length; if (-not $Name) { throw 'name needed' }
  $n = [int][math]::Ceiling($total / $pieceLen); $pieces = New-Object byte[] (20 * $n); (New-Object Random).NextBytes($pieces) }
elseif ($Folder) {
  $root = (Get-Item $Folder).FullName.TrimEnd('\')
  if (-not $Name) { $Name = Split-Path $root -Leaf }
  $list = @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object { $_.FullName.Substring($root.Length + 1) })
  if ($list.Count -eq 0) { throw 'the folder holds no files' }
  $total = 0L; $sha = [Security.Cryptography.SHA1]::Create(); $ms = New-Object IO.MemoryStream
  $buf = New-Object byte[] $pieceLen; $fill = 0
  $entries = New-Object System.Collections.ArrayList
  foreach ($f in $list) {
    $rel = $f.FullName.Substring($root.Length + 1).Split([char]92)
    $pathParts = @(); foreach ($seg in $rel) { $pathParts += , (BStr $seg) }
    # a file entry is a dict with its keys in sorted order: length, path
    $entry = JoinBytes (@([Text.Encoding]::ASCII.GetBytes('d'), (BStr 'length'), (BInt $f.Length), (BStr 'path'), [Text.Encoding]::ASCII.GetBytes('l')) + $pathParts + @([Text.Encoding]::ASCII.GetBytes('ee')))
    [void]$entries.Add($entry); $total += $f.Length
    $fs = [IO.File]::OpenRead($f.FullName)
    try { while (($r = $fs.Read($buf, $fill, $pieceLen - $fill)) -gt 0) { $fill += $r; if ($fill -eq $pieceLen) { $h = $sha.ComputeHash($buf, 0, $fill); $ms.Write($h, 0, 20); $fill = 0 } } } finally { $fs.Dispose() }
  }
  if ($fill -gt 0) { $h = $sha.ComputeHash($buf, 0, $fill); $ms.Write($h, 0, 20) }
  $pieces = $ms.ToArray()
  $filesEntry = JoinBytes (@([Text.Encoding]::ASCII.GetBytes('l')) + @($entries.ToArray()) + @([Text.Encoding]::ASCII.GetBytes('e')))
}
else { $fi = Get-Item $File; $total = $fi.Length; if (-not $Name) { $Name = $fi.Name }
  $sha = [Security.Cryptography.SHA1]::Create(); $ms = New-Object IO.MemoryStream
  $fs = [IO.File]::OpenRead($fi.FullName); $buf = New-Object byte[] $pieceLen
  try { while (($r = $fs.Read($buf, 0, $pieceLen)) -gt 0) { $h = $sha.ComputeHash($buf, 0, $r); $ms.Write($h, 0, 20) } } finally { $fs.Dispose() }
  $pieces = $ms.ToArray() }
# info dict, keys in sorted order: (files | length), name, piece length, pieces
$infoParts = @([Text.Encoding]::ASCII.GetBytes('d'))
if ($filesEntry) { $infoParts += (BStr 'files'); $infoParts += $filesEntry } else { $infoParts += (BStr 'length'); $infoParts += (BInt $total) }
$infoParts += @((BStr 'name'), (BStr $Name), (BStr 'piece length'), (BInt $pieceLen), (BStr 'pieces'), ([Text.Encoding]::ASCII.GetBytes("$($pieces.Length):")), $pieces, [Text.Encoding]::ASCII.GetBytes('e'))
$info = JoinBytes $infoParts
$sha1 = [Security.Cryptography.SHA1]::Create(); $infoHash = ([BitConverter]::ToString($sha1.ComputeHash($info))).Replace('-', '').ToLowerInvariant()
# A torrent over a real file has no tracker at all. Giving it one that nobody answers (127.0.0.1:9) made Deluge report
# "Error: No connection could be made because the target machine actively refused it" on a torrent that was 100% done
# and seeding, and Deluno called that finished download Stalled / "queue failed" and did not hand it off (golden path
# rc.30.10). A dummy torrent keeps its dead tracker: it is meant to stall. -DeadTracker gives a torrent over a real file that same
# tracker on purpose: since rc.30.12 (#1183) a complete torrent whose tracker does not answer must still be handed off.
$parts = @([Text.Encoding]::ASCII.GetBytes('d'))
# -Tracker names a tracker (the local one of scripts/recorded-replies, so two clients on one machine can find each other).
if ($Tracker) { $parts += (BStr 'announce'); $parts += (BStr $Tracker) }
elseif ($Dummy -or $DeadTracker) { $parts += (BStr 'announce'); $parts += (BStr 'http://127.0.0.1:9/announce') }
$parts += (BStr 'info'); $parts += $info; $parts += [Text.Encoding]::ASCII.GetBytes('e')
$torrent = JoinBytes $parts
[IO.File]::WriteAllBytes($Out, $torrent)
[pscustomobject]@{ infohash = $infoHash; size = $total; name = $Name; file = (Split-Path $Out -Leaf) } | ConvertTo-Json -Compress
