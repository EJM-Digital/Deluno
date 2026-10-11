<#
.SYNOPSIS
    A minimal BitTorrent tracker on 127.0.0.1, so two download clients in the golden VM can find each other (#1237). Runs INSIDE the VM.

.DESCRIPTION
    Answers /announce with the other peers that announced the same info hash (compact form), and nothing else. A torrent
    made with its address (Make-Torrent.ps1 -Tracker) lets one client seed a real film to another with no internet, DHT or
    peer-adding API, which is how a transfer "in flight" and one "stalled just short of the end" are recorded.
    Stop it with Ctrl+C or by ending its process. Binds 127.0.0.1 only.
#>
[CmdletBinding()]
param([int] $Port = 18999)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$swarms = @{}   # hex info hash -> hashtable "ip:port" -> expiry
function Bytes([string] $text) { [Text.Encoding]::ASCII.GetBytes($text) }
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        try {
            $query = @{}
            foreach ($pair in $context.Request.Url.Query.TrimStart('?').Split('&')) {
                if (-not $pair) { continue }
                $kv = $pair.Split('=', 2)
                $query[$kv[0]] = if ($kv.Count -gt 1) { $kv[1] } else { '' }
            }
            $out = New-Object IO.MemoryStream
            if ($context.Request.Url.AbsolutePath -ne '/announce' -or -not $query.ContainsKey('info_hash') -or -not $query.ContainsKey('port')) {
                $body = Bytes 'd14:failure reason15:not an announcee'
                $out.Write($body, 0, $body.Length)
            } else {
                $hash = ([BitConverter]::ToString([System.Web.HttpUtility]::UrlDecodeToBytes($query['info_hash']))).Replace('-', '')
                if (-not $swarms.ContainsKey($hash)) { $swarms[$hash] = @{} }
                $me = '127.0.0.1:' + $query['port']
                if ($query['event'] -eq 'stopped') { $swarms[$hash].Remove($me) } else { $swarms[$hash][$me] = (Get-Date).AddMinutes(10) }
                $others = @($swarms[$hash].Keys | Where-Object { $_ -ne $me -and $swarms[$hash][$_] -gt (Get-Date) })
                $compact = New-Object IO.MemoryStream
                foreach ($peer in $others) {
                    $port = [int]($peer.Split(':')[1])
                    $compact.Write([byte[]](127, 0, 0, 1, [byte]($port -shr 8), [byte]($port -band 255)), 0, 6)
                }
                $head = Bytes ("d8:completei$($others.Count)e10:incompletei0e8:intervali15e12:min intervali5e5:peers$($compact.Length):")
                $out.Write($head, 0, $head.Length)
                $peerBytes = $compact.ToArray(); $out.Write($peerBytes, 0, $peerBytes.Length)
                $tail = Bytes 'e'; $out.Write($tail, 0, 1)
            }
            $bytes = $out.ToArray()
            $context.Response.ContentType = 'text/plain'
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        } catch { } finally { $context.Response.Close() }
    }
} finally { $listener.Stop() }
