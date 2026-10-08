<#
.SYNOPSIS
    Turns a small video into a stub Usenet release: yEnc articles for the NNTP stub, a matching .nzb and a release.json (golden path, step 6).

.DESCRIPTION
    Takes -SourceFile, or makes a video with ffmpeg when none is given, then:
      - splits it into parts and yEnc-encodes each one into <OutDirectory>\articles\<message-id>.art
        (headers, a blank line, the yEnc body), the files Start-NntpStub.ps1 serves;
      - writes <Name>.nzb with one segment per part, and release.json (name, id, size, nzb file) for
        Start-NewznabStub.ps1.

    The generated video has real moving frames and a tone, long enough (150 s) that Deluno does not take it
    for a sample (under two minutes) and big enough (60 MB by default) to clear Weir's 50 MB floor. ffmpeg is
    looked for, in order: -FfmpegPath, Deluno's installed copy (%LOCALAPPDATA%\Deluno\current\tools\ffmpeg),
    tools\ffmpeg\win-x64 in a repository checkout, then PATH. It uses only encoders every ffmpeg build has
    (mpeg4 and aac), so Deluno's LGPL build is enough. Nothing is downloaded and no real file is used.

    Runs on Windows PowerShell 5.1.

.PARAMETER OutDirectory
    Where the release goes. Created when missing; an earlier release in it is replaced.

.PARAMETER Name
    The release name. Defaults to a Big Buck Bunny (2008, Creative Commons) release name Deluno can match on TMDb.

.PARAMETER SourceFile
    A video to use as it is. Must be at least -MinimumMegabytes, or Weir would skip it.

.PARAMETER MinimumMegabytes
    The smallest the video may be. Defaults to 60, above Weir's 50 MB floor.

.EXAMPLE
    ./New-UsenetRelease.ps1 -OutDirectory C:\golden\usenet\release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutDirectory,
    [string] $Name = 'Big.Buck.Bunny.2008.1080p.BluRay.x264-GOLDEN',
    [string] $SourceFile,
    [string] $FfmpegPath,
    [int] $MinimumMegabytes = 60,
    [int] $DurationSeconds = 150,
    [int] $PartSizeKilobytes = 700,
    [string] $Group = 'alt.binaries.golden'
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'UsenetStub.Lib.ps1')

function Find-Ffmpeg {
    if ($FfmpegPath) { if (Test-Path $FfmpegPath) { return (Resolve-Path $FfmpegPath).Path } else { throw "ffmpeg not found at $FfmpegPath" } }
    $candidates = @()
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Deluno\current\tools\ffmpeg\ffmpeg.exe') }
    $candidates += (Join-Path $here '..\..\..\tools\ffmpeg\win-x64\ffmpeg.exe')
    foreach ($c in $candidates) { if (Test-Path $c) { return (Resolve-Path $c).Path } }
    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'ffmpeg not found. Pass -FfmpegPath or -SourceFile.'
}

$minBytes = [long]$MinimumMegabytes * 1MB
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$OutDirectory = (Resolve-Path $OutDirectory).Path
$articles = Join-Path $OutDirectory 'articles'
if (Test-Path $articles) { Remove-Item -LiteralPath $articles -Recurse -Force }
New-Item -ItemType Directory -Force -Path $articles | Out-Null
Get-ChildItem -Path $OutDirectory -File | Where-Object { $_.Extension -in '.nzb', '.json' } | Remove-Item -Force

$fileName = "$Name.mkv"
$video = Join-Path $OutDirectory $fileName
if ($SourceFile) {
    if (-not (Test-Path $SourceFile)) { throw "Source file not found: $SourceFile" }
    if ((Get-Item $SourceFile).Length -lt $minBytes) { throw "$SourceFile is under $MinimumMegabytes MB, below Weir's 50 MB floor." }
    Copy-Item -LiteralPath $SourceFile -Destination $video -Force
}
else {
    $ffmpeg = Find-Ffmpeg
    # Noise keeps the frames from compressing to nothing; the video bitrate sets the size (about 4 Mbit/s is 75 MB for 150 s).
    foreach ($rate in 4, 6, 9) {
        Write-Host "Making a $DurationSeconds s test video with $ffmpeg ($rate Mbit/s)"
        $ffArgs = @('-y', '-v', 'error',
            '-f', 'lavfi', '-i', "testsrc2=size=1280x720:rate=24:duration=$DurationSeconds",
            '-f', 'lavfi', '-i', "sine=frequency=440:sample_rate=48000:duration=$DurationSeconds",
            '-vf', 'noise=alls=25:allf=t+u', '-c:v', 'mpeg4', '-b:v', "${rate}M", '-g', '48',
            '-c:a', 'aac', '-b:a', '128k', '-shortest', $video)
        & $ffmpeg @ffArgs
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with exit code $LASTEXITCODE." }
        if ((Get-Item $video).Length -ge $minBytes) { break }
    }
    if ((Get-Item $video).Length -lt $minBytes) { throw "The generated video is under $MinimumMegabytes MB. Raise -DurationSeconds." }
}

$size = (Get-Item $video).Length
$partSize = $PartSizeKilobytes * 1KB
$total = [int][math]::Ceiling($size / $partSize)
$sha1 = New-Object Security.Cryptography.SHA1Managed
$releaseHash = [BitConverter]::ToString($sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($Name))).Replace('-', '').Substring(0, 10).ToLowerInvariant()

# Whole-file CRC first, for the last part's =yend line.
$crc = [uint32]0
$fs = [IO.File]::OpenRead($video)
try {
    $buf = New-Object byte[] (1MB)
    while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $crc = [Golden.Crc32]::Update($crc, $buf, 0, $n) }
}
finally { $fs.Dispose() }

$posted = [DateTimeOffset]::UtcNow.AddDays(-2)
$segments = New-Object System.Collections.Generic.List[object]
$fs = [IO.File]::OpenRead($video)
try {
    $chunk = New-Object byte[] $partSize
    for ($part = 1; $part -le $total; $part++) {
        $read = 0
        while ($read -lt $partSize) { $n = $fs.Read($chunk, $read, $partSize - $read); if ($n -le 0) { break }; $read += $n }
        $begin = [long]($part - 1) * $partSize + 1
        $body = [Golden.Yenc]::EncodePart($chunk, 0, $read, $begin, $part, $total, $size, $fileName, $crc, ($part -eq $total))
        $id = "golden.$releaseHash.$part@golden.test"
        $subject = "[1/1] - `"$fileName`" yEnc ($part/$total) $size"
        $head = "Message-ID: <$id>`r`nNewsgroups: $Group`r`nSubject: $subject`r`nFrom: golden <golden@golden.test>`r`nDate: $($posted.ToString('r'))`r`nLines: $([math]::Ceiling($body.Length / 130))`r`nBytes: $($body.Length)`r`n`r`n"
        $headBytes = [Text.Encoding]::ASCII.GetBytes($head)
        $out = New-Object byte[] ($headBytes.Length + $body.Length)
        [Array]::Copy($headBytes, 0, $out, 0, $headBytes.Length)
        [Array]::Copy($body, 0, $out, $headBytes.Length, $body.Length)
        [IO.File]::WriteAllBytes((Join-Path $articles "$id.art"), $out)
        $segments.Add([pscustomobject]@{ Number = $part; Bytes = $body.Length; Id = $id })
    }
}
finally { $fs.Dispose() }

# The .nzb: one file, one segment per part.
$xml = New-Object System.Text.StringBuilder
[void]$xml.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$xml.AppendLine('<!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">')
[void]$xml.AppendLine('<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">')
[void]$xml.AppendLine('  <head><meta type="name">' + [Security.SecurityElement]::Escape($Name) + '</meta></head>')
$subjectAttr = [Security.SecurityElement]::Escape("[1/1] - `"$fileName`" yEnc (1/$total) $size")
[void]$xml.AppendLine("  <file poster=`"golden &lt;golden@golden.test&gt;`" date=`"$($posted.ToUnixTimeSeconds())`" subject=`"$subjectAttr`">")
[void]$xml.AppendLine("    <groups><group>$Group</group></groups>")
[void]$xml.AppendLine('    <segments>')
foreach ($s in $segments) { [void]$xml.AppendLine("      <segment bytes=`"$($s.Bytes)`" number=`"$($s.Number)`">$($s.Id)</segment>") }
[void]$xml.AppendLine('    </segments>')
[void]$xml.AppendLine('  </file>')
[void]$xml.AppendLine('</nzb>')
$nzbName = "$Name.nzb"
[IO.File]::WriteAllText((Join-Path $OutDirectory $nzbName), $xml.ToString(), (New-Object Text.UTF8Encoding($false)))

$meta = [ordered]@{
    name = $Name; id = "golden$releaseHash"; file = $fileName; nzb = $nzbName; group = $Group
    sizeBytes = $size; parts = $total; crc32 = $crc.ToString('x8'); sha256 = (Get-FileHash $video -Algorithm SHA256).Hash
}
Set-Content -Path (Join-Path $OutDirectory 'release.json') -Value ($meta | ConvertTo-Json) -Encoding ascii
Write-Host ("Release {0}: {1:N1} MB in {2} parts. NZB: {3}" -f $Name, ($size / 1MB), $total, (Join-Path $OutDirectory $nzbName))
[pscustomobject]$meta
