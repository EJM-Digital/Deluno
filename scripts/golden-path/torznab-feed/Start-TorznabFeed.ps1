# Golden path: a small Torznab feed on 127.0.0.1:8765 for the controlled films (hand-off, pause, retry, profile and stall tests). It serves the releases Add-Release.ps1 wrote and their .torrent files. Run it in the VM's signed-in session; add it to Deluno as a Torznab indexer at http://127.0.0.1:8765 with any API key.
param([int]$Port = 8765, [string]$Root = 'C:\golden\feed')
$ErrorActionPreference = 'Continue'
$l = New-Object System.Net.HttpListener; $l.Prefixes.Add("http://127.0.0.1:$Port/"); $l.Start()
function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }
while ($l.IsListening) {
  $ctx = $l.GetContext(); $req = $ctx.Request; $res = $ctx.Response
  try {
    $path = $req.Url.AbsolutePath; $q = $req.QueryString; $t = "$($q['t'])".ToLowerInvariant()
    $bytes = $null; $ctype = 'application/xml'
    "$((Get-Date).ToString('HH:mm:ss')) $($req.Url.PathAndQuery)" | Add-Content (Join-Path $Root 'feed.log')
    if ($path -like '/dl/*') { $f = Join-Path $Root ('torrents\' + [IO.Path]::GetFileName($path)); if (Test-Path $f) { $bytes = [IO.File]::ReadAllBytes($f); $ctype = 'application/x-bittorrent' } else { $res.StatusCode = 404; $bytes = [Text.Encoding]::UTF8.GetBytes('no') } }
    elseif ($t -eq 'caps') {
      $x = '<?xml version="1.0" encoding="UTF-8"?><caps><server version="1.0" title="Golden feed" /><limits max="100" default="100"/><searching><search available="yes" supportedParams="q"/><tv-search available="yes" supportedParams="q,season,ep,tvdbid"/><movie-search available="yes" supportedParams="q,imdbid,tmdbid"/></searching><categories><category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/><subcat id="2045" name="Movies/UHD"/></category><category id="5000" name="TV"><subcat id="5040" name="TV/HD"/><subcat id="5045" name="TV/UHD"/></category></categories></caps>'
      $bytes = [Text.Encoding]::UTF8.GetBytes($x)
    } else {
      $rel = @(); $rf = Join-Path $Root 'releases.jsonl'; if (Test-Path $rf) { $rel = @(Get-Content $rf | ? { $_.Trim() } | % { $_ | ConvertFrom-Json }) }
      $text = "$($q['q'])".ToLowerInvariant(); $cat = "$($q['cat'])"
      $words = @($text -split '[ .\-_()]' | ? { $_.Length -ge 3 })
      $isTv = ($t -eq 'tvsearch' -or $t -eq 'tv')
      $isMovie = ($t -eq 'movie')
      $items = foreach ($r in $rel) {
        $rIsTv = ([int]$r.cat -ge 5000)
        if ($isTv -and -not $rIsTv) { continue }
        if ($isMovie -and $rIsTv) { continue }
        if (-not $isTv -and -not $isMovie -and $cat) { if ($cat -match '^5' -and -not $rIsTv) { continue }; if ($cat -match '^2' -and $rIsTv) { continue } }
        if ($words.Count -gt 0) { $name = "$($r.title)".ToLowerInvariant(); $hit = $false; foreach ($w in $words) { if ($name.Contains($w)) { $hit = $true; break } }; if (-not $hit) { continue } }
        $url = "http://127.0.0.1:$Port/dl/$($r.torrent)"
        "<item><title>$(Esc $r.title)</title><guid isPermaLink=`"false`">$($r.infohash)</guid><link>$url</link><pubDate>$([DateTime]::UtcNow.AddHours(-3).ToString('r'))</pubDate><size>$($r.size)</size><enclosure url=`"$url`" length=`"$($r.size)`" type=`"application/x-bittorrent`"/><torznab:attr name=`"category`" value=`"$($r.cat)`"/><torznab:attr name=`"seeders`" value=`"$($r.seeders)`"/><torznab:attr name=`"peers`" value=`"$([int]$r.seeders + 2)`"/><torznab:attr name=`"infohash`" value=`"$($r.infohash)`"/><torznab:attr name=`"size`" value=`"$($r.size)`"/></item>"
      }
      $n = @($items).Count
      $x = '<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><title>Golden feed</title><description>local</description><link>http://127.0.0.1/</link><torznab:response offset="0" total="' + $n + '"/>' + ($items -join '') + '</channel></rss>'
      $bytes = [Text.Encoding]::UTF8.GetBytes($x)
    }
    $res.ContentType = $ctype; $res.ContentLength64 = $bytes.Length; $res.OutputStream.Write($bytes, 0, $bytes.Length)
  } catch { "ERR $($_.Exception.Message)" | Add-Content (Join-Path $Root 'feed.log') }
  finally { try { $res.Close() } catch {} }
}

