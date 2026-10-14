<#
.SYNOPSIS
    Self-checks for the Usenet stub: starts both stubs and talks to them the way SABnzbd, NZBGet and Deluno do. Exits non-zero on any failure.

.DESCRIPTION
    No Pester, no ffmpeg and no real news server: it makes a small release from random bytes (which hit every
    yEnc escape case), starts Start-NntpStub.ps1 and Start-NewznabStub.ps1 as real child processes on free
    ports of 127.0.0.1, and checks:
      - NNTP: greeting, CAPABILITIES, MODE READER, AUTHINFO USER/PASS with any login, GROUP, DATE, STAT,
        a missing article (430), HEAD, ARTICLE, BODY, an unknown command (500) and QUIT;
      - every segment named in the .nzb downloads, decodes (yEnc) and its pcrc32 matches, and the parts joined
        are byte for byte the original file (SHA-256);
      - Newznab: t=caps, t=search and t=movie return the release with the .nzb link, t=tvsearch and an
        unrelated query return nothing, any API key (or none) works, and t=get serves the identical .nzb.
    Runs on Windows PowerShell 5.1.

.EXAMPLE
    ./Test-UsenetStub.ps1
#>
[CmdletBinding()]
param([int] $SizeKilobytes = 2500)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'UsenetStub.Lib.ps1')

$script:failures = 0
function Check([string] $Name, [bool] $Ok, [string] $Detail = '') {
    if ($Ok) { Write-Host "[ok]   $Name" -ForegroundColor Green }
    else { $script:failures++; Write-Host "[FAIL] $Name $Detail" -ForegroundColor Red }
}

function Get-FreePort {
    $l = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = ([Net.IPEndPoint]$l.LocalEndpoint).Port; $l.Stop(); $p
}

# A tiny NNTP client: raw sockets, nothing from the stub's own code.
function Connect-Nntp([int] $Port) {
    $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $Port)
    $c.ReceiveTimeout = 15000
    [pscustomobject]@{ Client = $c; Stream = $c.GetStream() }
}
function Read-NntpLine($Conn) {
    $sb = New-Object System.Text.StringBuilder
    while ($true) {
        $b = $Conn.Stream.ReadByte()
        if ($b -lt 0) { return $null }
        if ($b -eq 10) { return $sb.ToString().TrimEnd("`r") }
        [void]$sb.Append([char]$b)
    }
}
function Send-Nntp($Conn, [string] $Line) {
    $bytes = [Text.Encoding]::ASCII.GetBytes($Line + "`r`n")
    $Conn.Stream.Write($bytes, 0, $bytes.Length); $Conn.Stream.Flush()
}
# Sends a command and returns the status line; for a multi-line answer also the raw bytes after it, up to the lone ".".
function Invoke-Nntp($Conn, [string] $Command, [switch] $Multi) {
    Send-Nntp $Conn $Command
    $status = Read-NntpLine $Conn
    $data = $null
    if ($Multi -and $status -match '^(101|220|221|222)\b') {
        $ms = New-Object System.IO.MemoryStream
        $prev = [byte[]]@()
        $lineStart = $true
        $line = New-Object System.IO.MemoryStream
        while ($true) {
            $b = $Conn.Stream.ReadByte()
            if ($b -lt 0) { break }
            $line.WriteByte([byte]$b)
            if ($b -eq 10) {
                $l = $line.ToArray(); $line.SetLength(0)
                if ($l.Length -eq 3 -and $l[0] -eq 46) { break }
                if ($l.Length -gt 1 -and $l[0] -eq 46 -and $l[1] -eq 46) { $l = $l[1..($l.Length - 1)] }
                $ms.Write($l, 0, $l.Length)
            }
        }
        $data = $ms.ToArray()
    }
    [pscustomobject]@{ Status = $status; Data = $data }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('usenet-stub-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$procs = @()
try {
    # --- a release from random bytes
    $source = Join-Path $work 'source.bin'
    $rnd = New-Object byte[] ($SizeKilobytes * 1KB)
    (New-Object Random 42).NextBytes($rnd)
    [IO.File]::WriteAllBytes($source, $rnd)
    $sourceHash = (Get-FileHash $source -Algorithm SHA256).Hash
    $rel = Join-Path $work 'release'
    $made = & (Join-Path $here 'New-UsenetRelease.ps1') -OutDirectory $rel -SourceFile $source -MinimumMegabytes 1 -PartSizeKilobytes 300 -Name 'Big.Buck.Bunny.2008.720p.WEB-DL.x264-GOLDEN'
    $meta = Get-Content -Raw (Join-Path $rel 'release.json') | ConvertFrom-Json
    Check 'release.json names the release' ($meta.name -eq 'Big.Buck.Bunny.2008.720p.WEB-DL.x264-GOLDEN')
    Check 'release has several parts' ($meta.parts -ge 8) "parts=$($meta.parts)"
    Check 'one article file per part' ((Get-ChildItem (Join-Path $rel 'articles') -Filter *.art).Count -eq $meta.parts)

    # --- the default film is above Deluno's own size minimum for the quality its name says (golden path, 9 Oct: 74 MB
    # against 2.2 GB for Bluray 1080p needed "Send anyway"). WEB 720p's minimum is 0.8 GB in QualityTypicalSizes.cs.
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $here 'New-UsenetRelease.ps1'), [ref]$tokens, [ref]$parseErrors)
    $defaults = @{}
    foreach ($p in $ast.ParamBlock.Parameters) { if ($p.DefaultValue) { $defaults[$p.Name.VariablePath.UserPath] = $p.DefaultValue.SafeGetValue() } }
    $filmMinimumMb = 0.8 * 1024
    Check 'the default release name says WEB 720p, the quality its size matches' (($defaults['Name'] -match '720p') -and ($defaults['Name'] -match 'WEB'))
    Check "the default film is at least 20% above Deluno's 0.8 GB WEB 720p minimum" ([int]$defaults['MinimumMegabytes'] -ge ($filmMinimumMb * 1.2)) "MinimumMegabytes=$($defaults['MinimumMegabytes'])"
    Check 'the default film stays under about 1.5 GB' ([int]$defaults['MinimumMegabytes'] -le 1536) "MinimumMegabytes=$($defaults['MinimumMegabytes'])"
    Check 'the default film is longer than a sample (two minutes)' ([int]$defaults['DurationSeconds'] -gt 120)

    # --- start both stubs as real processes
    $nntpPort = Get-FreePort
    $newznabPort = Get-FreePort
    foreach ($spec in @(@('Start-NntpStub.ps1', $nntpPort, 'nntp.ready'), @('Start-NewznabStub.ps1', $newznabPort, 'newznab.ready'))) {
        $ready = Join-Path $work $spec[2]
        $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $here $spec[0])`" -Root `"$rel`" -Port $($spec[1]) -ReadyFile `"$ready`""
        $procs += Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path $ready) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 } # reason: poll interval inside a loop bounded by its deadline
        Check "$($spec[0]) is listening" (Test-Path $ready)
    }

    # --- NNTP protocol
    $c = Connect-Nntp $nntpPort
    $greeting = Read-NntpLine $c
    Check 'NNTP greeting is 200' ($greeting -like '200 *') $greeting
    $r = Invoke-Nntp $c 'CAPABILITIES' -Multi
    Check 'CAPABILITIES answers 101 with READER' ($r.Status -like '101*' -and [Text.Encoding]::ASCII.GetString($r.Data) -match 'READER') $r.Status
    Check 'MODE READER is 200' ((Invoke-Nntp $c 'MODE READER').Status -like '200*')
    Check 'AUTHINFO USER asks for the password' ((Invoke-Nntp $c 'AUTHINFO USER anyone').Status -like '381*')
    Check 'AUTHINFO PASS accepts anything' ((Invoke-Nntp $c 'AUTHINFO PASS whatever').Status -like '281*')
    Check 'GROUP is 211' ((Invoke-Nntp $c "GROUP $($meta.group)").Status -like '211*')
    Check 'DATE is 111 and 14 digits' ((Invoke-Nntp $c 'DATE').Status -match '^111 \d{14}$')

    $nzb = [xml](Get-Content -Raw (Join-Path $rel $meta.nzb))
    $ns = New-Object Xml.XmlNamespaceManager($nzb.NameTable); $ns.AddNamespace('n', 'http://www.newzbin.com/DTD/2003/nzb')
    $segs = @($nzb.SelectNodes('//n:segment', $ns))
    Check 'nzb has one segment per part' ($segs.Count -eq $meta.parts) "segments=$($segs.Count)"
    $first = $segs[0].InnerText
    Check 'STAT finds an article' ((Invoke-Nntp $c "STAT <$first>").Status -like '223*')
    Check 'STAT of a missing article is 430' ((Invoke-Nntp $c 'STAT <nope@golden.test>').Status -like '430*')
    Check 'a path-like message-id is refused' ((Invoke-Nntp $c 'BODY <..\..\x@golden.test>').Status -like '430*')
    $h = Invoke-Nntp $c "HEAD <$first>" -Multi
    Check 'HEAD answers 221 with the Message-ID' ($h.Status -like '221*' -and [Text.Encoding]::ASCII.GetString($h.Data) -match "Message-ID: <$([regex]::Escape($first))>")
    $a = Invoke-Nntp $c "ARTICLE <$first>" -Multi
    $aText = [Text.Encoding]::ASCII.GetString($a.Data)
    Check 'ARTICLE answers 220 with headers, a blank line and a yEnc body' ($a.Status -like '220*' -and $aText -match 'Subject: .*yEnc \(1/' -and $aText -match "\r\n\r\n=ybegin part=1 ")
    Check 'unknown command is 500' ((Invoke-Nntp $c 'XYZZY').Status -like '500*')

    # --- download every segment in nzb order and rebuild the file
    $out = New-Object System.IO.MemoryStream
    $bad = 0
    foreach ($s in $segs) {
        $b = Invoke-Nntp $c "BODY <$($s.InnerText)>" -Multi
        if ($b.Status -notlike '222*') { $bad++; continue }
        $text = [Text.Encoding]::ASCII.GetString($b.Data)
        $decoded = [Golden.Yenc]::Decode($b.Data)
        $pcrc = [regex]::Match($text, 'pcrc32=([0-9a-f]{8})').Groups[1].Value
        $actual = '{0:x8}' -f [Golden.Crc32]::Of($decoded, 0, $decoded.Length)
        if ($pcrc -ne $actual) { $bad++ }
        $out.Write($decoded, 0, $decoded.Length)
    }
    Check 'every segment downloaded with a matching pcrc32' ($bad -eq 0) "bad=$bad"
    $rebuilt = Join-Path $work 'rebuilt.bin'
    [IO.File]::WriteAllBytes($rebuilt, $out.ToArray())
    Check 'the parts joined are the original bytes (SHA-256)' ((Get-FileHash $rebuilt -Algorithm SHA256).Hash -eq $sourceHash)
    Check 'the whole-file crc32 on the last part matches' ([regex]::Match([Text.Encoding]::ASCII.GetString((Invoke-Nntp $c "BODY <$($segs[-1].InnerText)>" -Multi).Data), ' crc32=([0-9a-f]{8})').Groups[1].Value -eq $meta.crc32)
    Check 'QUIT is 205' ((Invoke-Nntp $c 'QUIT').Status -like '205*')
    $c.Client.Close()

    # --- several connections at once (a downloader opens many)
    $conns = 1..4 | ForEach-Object { Connect-Nntp $nntpPort }
    $okAll = $true
    foreach ($cn in $conns) { $null = Read-NntpLine $cn; if ((Invoke-Nntp $cn "STAT <$first>").Status -notlike '223*') { $okAll = $false }; $cn.Client.Close() }
    Check 'four connections at once all work' $okAll

    # --- Newznab
    $base = "http://127.0.0.1:$($newznabPort)/api"
    function Get-Http([string] $Url) {
        try { $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 20; [pscustomobject]@{ Code = [int]$resp.StatusCode; Content = [string]$resp.Content } }
        catch { $code = 0; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode } else { Write-Host "  request failed: $($_.Exception.Message)" -ForegroundColor DarkGray }; [pscustomobject]@{ Code = $code; Content = "" } }
    }
    # Counts the <item>s of a feed; -1 when the answer is not a feed at all, so an error can never read as "nothing found".
    function Get-ItemCount([string] $Url) {
        $answer = Get-Http $Url
        if ($answer.Code -ne 200) { return -1 }
        $x = [xml]$answer.Content
        if (-not $x.rss) { return -1 }
        $count = $x.rss.channel.SelectNodes("item").Count
        return $count
    }
    $caps = Get-Http "${base}?t=caps"
    $capsXml = [xml]$caps.Content
    Check 't=caps is XML with movie-search' ($caps.Code -eq 200 -and $capsXml.caps.searching.'movie-search'.available -eq 'yes')
    Check 't=caps lists Movies 2000' ([bool]($capsXml.caps.categories.category | Where-Object { $_.id -eq '2000' }))
    $feed = Get-Http "${base}?t=search&q=Big%20Buck%20Bunny%202008&apikey=anything&cat=2000,2040&limit=100"
    $rss = [xml]$feed.Content
    $items = @($rss.rss.channel.SelectNodes("item"))
    Check 't=search returns the release' ($feed.Code -eq 200 -and $items.Count -eq 1 -and $items[0].title -eq $meta.name)
    $attrs = @($items[0].ChildNodes | Where-Object { $_.LocalName -eq 'attr' })
    Check 'item has size attr, enclosure length and an nzb enclosure' (([long]($attrs | Where-Object { $_.name -eq 'size' }).value -eq $meta.sizeBytes) -and ([long]$items[0].enclosure.length -eq $meta.sizeBytes) -and $items[0].enclosure.type -eq 'application/x-nzb')
    Check 't=movie returns the release' ((Get-ItemCount "${base}?t=movie&q=Big%20Buck%20Bunny&imdbid=1254207&apikey=x") -eq 1)
    Check 'no API key is accepted too' ((Get-ItemCount "${base}?t=search&q=bunny") -eq 1)
    Check 'the feed with no query lists the release (RSS sync)' ((Get-ItemCount "${base}?t=search&cat=2000,5000") -eq 1)
    Check 't=tvsearch returns nothing' ((Get-ItemCount "${base}?t=tvsearch&q=Big%20Buck%20Bunny&season=1&ep=1") -eq 0)
    Check 'an unrelated query returns nothing' ((Get-ItemCount "${base}?t=search&q=Casablanca%201942") -eq 0)
    $link = $items[0].enclosure.url
    $original = [IO.File]::ReadAllBytes((Join-Path $rel $meta.nzb))
    $wc = New-Object System.Net.WebClient
    $gotBytes = $wc.DownloadData($link)
    $same = ($gotBytes.Length -eq $original.Length)
    if ($same) { for ($i = 0; $i -lt $original.Length; $i++) { if ($gotBytes[$i] -ne $original[$i]) { $same = $false; break } } }
    Check 't=get serves the identical .nzb' $same "got $($gotBytes.Length) bytes, expected $($original.Length)"
    Check 't=get is served as an nzb' ($wc.ResponseHeaders['Content-Type'] -like 'application/x-nzb*')
    Check 't=get of another id is 404' ((Get-Http "${base}?t=get&id=nope").Code -eq 404)
}
catch {
    $script:failures++
    Write-Host "[FAIL] test run aborted: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    foreach ($p in $procs) { try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { } }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All Usenet stub checks passed.' -ForegroundColor Green
exit 0
