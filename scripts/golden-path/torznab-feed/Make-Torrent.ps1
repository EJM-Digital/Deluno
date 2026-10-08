# Golden path: makes a single-file .torrent. With -File it hashes the real file (and names no tracker, so the client that finds the
# file completes at once with no tracker error); with -Dummy -Length N it makes a torrent nobody can ever download (random piece
# hashes, a tracker that does not exist).
param([string]$File, [string]$Out, [string]$Name, [switch]$Dummy, [long]$Length = 30MB, [int]$PieceKb = 256)
$ErrorActionPreference = 'Stop'
function BStr([string]$s) { $b = [Text.Encoding]::UTF8.GetBytes($s); [Text.Encoding]::ASCII.GetBytes("$($b.Length):") + $b }
function BInt([long]$n) { [Text.Encoding]::ASCII.GetBytes("i${n}e") }
function JoinBytes([object[]]$parts) { $ms = New-Object IO.MemoryStream; foreach ($p in $parts) { $ms.Write($p, 0, $p.Length) }; $ms.ToArray() }
$pieceLen = $PieceKb * 1024
if ($Dummy) { $total = $Length; if (-not $Name) { throw 'name needed' }
  $n = [int][math]::Ceiling($total / $pieceLen); $pieces = New-Object byte[] (20 * $n); (New-Object Random).NextBytes($pieces) }
else { $fi = Get-Item $File; $total = $fi.Length; if (-not $Name) { $Name = $fi.Name }
  $sha = [Security.Cryptography.SHA1]::Create(); $ms = New-Object IO.MemoryStream
  $fs = [IO.File]::OpenRead($fi.FullName); $buf = New-Object byte[] $pieceLen
  try { while (($r = $fs.Read($buf, 0, $pieceLen)) -gt 0) { $h = $sha.ComputeHash($buf, 0, $r); $ms.Write($h, 0, 20) } } finally { $fs.Dispose() }
  $pieces = $ms.ToArray() }
# info dict, keys in sorted order: length, name, piece length, pieces
$info = JoinBytes @([Text.Encoding]::ASCII.GetBytes('d'), (BStr 'length'), (BInt $total), (BStr 'name'), (BStr $Name), (BStr 'piece length'), (BInt $pieceLen), (BStr 'pieces'), ([Text.Encoding]::ASCII.GetBytes("$($pieces.Length):")), $pieces, [Text.Encoding]::ASCII.GetBytes('e'))
$sha1 = [Security.Cryptography.SHA1]::Create(); $infoHash = ([BitConverter]::ToString($sha1.ComputeHash($info))).Replace('-', '').ToLowerInvariant()
# A torrent over a real file has no tracker at all. Giving it one that nobody answers (127.0.0.1:9) made Deluge report
# "Error: No connection could be made because the target machine actively refused it" on a torrent that was 100% done
# and seeding, and Deluno called that finished download Stalled / "queue failed" and did not hand it off (golden path
# rc.30.10). A dummy torrent keeps its dead tracker: it is meant to stall.
$parts = @([Text.Encoding]::ASCII.GetBytes('d'))
if ($Dummy) { $parts += (BStr 'announce'); $parts += (BStr 'http://127.0.0.1:9/announce') }
$parts += (BStr 'info'); $parts += $info; $parts += [Text.Encoding]::ASCII.GetBytes('e')
$torrent = JoinBytes $parts
[IO.File]::WriteAllBytes($Out, $torrent)
[pscustomobject]@{ infohash = $infoHash; size = $total; name = $Name; file = (Split-Path $Out -Leaf) } | ConvertTo-Json -Compress

