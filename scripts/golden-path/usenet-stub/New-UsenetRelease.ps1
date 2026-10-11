<#
.SYNOPSIS
    Turns a small video into a stub Usenet release: yEnc articles for the NNTP stub, a matching .nzb and a release.json (golden path, step 6).

.DESCRIPTION
    Takes -SourceFile, or makes a video with ffmpeg when none is given, then:
      - splits it into parts and yEnc-encodes each one into <OutDirectory>\articles\<message-id>.art
        (headers, a blank line, the yEnc body), the files Start-NntpStub.ps1 serves;
      - writes <Name>.nzb with one segment per part, and release.json (name, id, size, nzb file) for
        Start-NewznabStub.ps1.

    The generated video has real moving frames and a tone, long enough (600 s) that Deluno does not take it
    for a sample (under two minutes) and sized to clear both of Deluno's film size rules it meets, plus
    Weir's 50 MB floor. Deluno judges a film's size against the quality in its name and the film's runtime:
    the minimum and maximum in src/Deluno.Quality/QualityTypicalSizes.cs are per 2 hours, scaled by the
    runtime. Big Buck Bunny is 8 minutes on TMDb, so once Deluno knows that, WEB 720p allows at most 0.5 GB
    (a 1.1 GB file is refused: "above the 0.5 GB maximum ... for a 8-minute runtime", golden path rc.30.10),
    while with no runtime known it assumes 2 hours and WEB 720p needs at least 0.8 GB. No single WEB 720p
    size fits both. WEB 1080p does: 1.5 GB minimum for 2 hours, 25 GB per 2 hours scaled to about 1.67 GB for
    8 minutes. So the default name says WEB 1080p, which is also exactly what the guided setup's Balanced
    1080p profile targets (so it is the best match, not a lower quality held for review), and the default
    video is about 1.6 GB, inside both ranges. A film named for a quality whose range excludes its size is
    refused ("Send anyway" is then needed), so keep name, size and runtime in step.
    ffmpeg is
    looked for, in order: -FfmpegPath, Deluno's installed copy (%LOCALAPPDATA%\Deluno\current\tools\ffmpeg),
    tools\ffmpeg\win-x64 in a repository checkout, then PATH. It uses only encoders every ffmpeg build has
    (mpeg4 and aac), so Deluno's LGPL build is enough. Nothing is downloaded and no real file is used.

    Runs on Windows PowerShell 5.1.

.PARAMETER OutDirectory
    Where the release goes. Created when missing; an earlier release in it is replaced.

.PARAMETER Name
    The release name. Defaults to a Big Buck Bunny (2008, Creative Commons) WEB 1080p release name Deluno can match on TMDb.

.PARAMETER SourceFile
    A video to use as it is. Must be at least -MinimumMegabytes, or Weir would skip it.

.PARAMETER ExtraKilobytes
    When above 0 the release is a folder job: a small .nfo of about this many kilobytes is posted beside the film, in the same .nzb.

.PARAMETER MinimumMegabytes
    The smallest the video may be. Defaults to 1550, above Deluno's 1.5 GB film minimum for WEB 1080p at the 2-hour default runtime (1536 MB) and Weir's 50 MB floor.

.PARAMETER VideoMegabitsPerSecond
    The first video bitrate tried. Defaults to 22.5, which makes about 1.6 GB for 600 s (the file must stay under about 1.67 GB, the WEB 1080p maximum for Big Buck Bunny's 8-minute runtime). The rates tried after it are only used when the file is under -MinimumMegabytes.

.EXAMPLE
    ./New-UsenetRelease.ps1 -OutDirectory C:\golden\usenet\release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutDirectory,
    [string] $Name = 'Big.Buck.Bunny.2008.1080p.WEB-DL.x264-GOLDEN',
    [string] $SourceFile,
    [string] $FfmpegPath,
    [int] $MinimumMegabytes = 1550,
    [double] $VideoMegabitsPerSecond = 22.5,
    [int] $DurationSeconds = 600,
    [int] $PartSizeKilobytes = 700,
    [string] $Group = 'alt.binaries.golden',
    [int] $ExtraKilobytes = 0
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
    if (-not (Test-Path -LiteralPath $SourceFile)) { throw "Source file not found: $SourceFile" }
    if ((Get-Item -LiteralPath $SourceFile).Length -lt $minBytes) { throw "$SourceFile is under $MinimumMegabytes MB, below the size this stub promises (Deluno's film minimum for its quality, and Weir's 50 MB floor)." }
    Copy-Item -LiteralPath $SourceFile -Destination $video -Force
}
else {
    $ffmpeg = Find-Ffmpeg
    # Noise keeps the frames from compressing to nothing; the video bitrate sets the size (22.5 Mbit/s is about 1.6 GB for 600 s).
    foreach ($rate in $VideoMegabitsPerSecond, ($VideoMegabitsPerSecond + 3), ($VideoMegabitsPerSecond + 6)) {
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

$partSize = $PartSizeKilobytes * 1KB
$sha1 = New-Object Security.Cryptography.SHA1Managed
$releaseHash = [BitConverter]::ToString($sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($Name))).Replace('-', '').Substring(0, 10).ToLowerInvariant()
$posted = [DateTimeOffset]::UtcNow.AddDays(-2)

# Posts one file: yEnc articles into <OutDirectory>\articles, and returns what the .nzb needs to list it.
function Add-PostedFile([string] $path, [string] $postedName, [int] $index) {
    $size = (Get-Item $path).Length
    $total = [int][math]::Ceiling($size / $partSize)
    # Whole-file CRC first, for the last part's =yend line.
    $crc = [uint32]0
    $fs = [IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] (1MB)
        while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $crc = [Golden.Crc32]::Update($crc, $buf, 0, $n) }
    }
    finally { $fs.Dispose() }
    $segments = New-Object System.Collections.Generic.List[object]
    $fs = [IO.File]::OpenRead($path)
    try {
        $chunk = New-Object byte[] $partSize
        for ($part = 1; $part -le $total; $part++) {
            $read = 0
            while ($read -lt $partSize) { $n = $fs.Read($chunk, $read, $partSize - $read); if ($n -le 0) { break }; $read += $n }
            $begin = [long]($part - 1) * $partSize + 1
            $body = [Golden.Yenc]::EncodePart($chunk, 0, $read, $begin, $part, $total, $size, $postedName, $crc, ($part -eq $total))
            $id = "golden.$releaseHash.$index.$part@golden.test"
            $subject = "[$index/$fileCount] - `"$postedName`" yEnc ($part/$total) $size"
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
    [pscustomobject]@{ Name = $postedName; Size = $size; Total = $total; Crc = $crc; Segments = $segments }
}

# A release is one file, or (with -ExtraKilobytes) a folder job: the film plus a small extra file beside it (a .nfo),
# which SABnzbd and NZBGet keep together in a folder named for the job (the #1199 case).
$extra = $null
if ($ExtraKilobytes -gt 0) {
    $extra = Join-Path $OutDirectory "$Name.nfo"
    $rng = New-Object Random 1199
    $text = New-Object System.Text.StringBuilder
    # Plain sentences: a line that ends in eight hex digits reads as an SFV checksum line, and SABnzbd then "verifies" the film against it and fails the job.
    $line = 0
    while ($text.Length -lt $ExtraKilobytes * 1KB) { $line++; [void]$text.AppendLine(('Line {0} of the note that travels beside the film in this stub release, here so the release holds more than one file.' -f $line)) }
    [IO.File]::WriteAllText($extra, $text.ToString(), (New-Object Text.UTF8Encoding($false)))
}
$fileCount = if ($extra) { 2 } else { 1 }
$postedFiles = @(Add-PostedFile $video $fileName 1)
if ($extra) { $postedFiles += Add-PostedFile $extra "$Name.nfo" 2 }
$size = (Get-Item $video).Length
$total = $postedFiles[0].Total
$crc = $postedFiles[0].Crc

# The .nzb: one <file> per posted file, one segment per part.
$xml = New-Object System.Text.StringBuilder
[void]$xml.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$xml.AppendLine('<!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">')
[void]$xml.AppendLine('<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">')
[void]$xml.AppendLine('  <head><meta type="name">' + [Security.SecurityElement]::Escape($Name) + '</meta></head>')
$fileIndex = 0
foreach ($f in $postedFiles) {
    $fileIndex++
    $subjectAttr = [Security.SecurityElement]::Escape("[$fileIndex/$fileCount] - `"$($f.Name)`" yEnc (1/$($f.Total)) $($f.Size)")
    [void]$xml.AppendLine("  <file poster=`"golden &lt;golden@golden.test&gt;`" date=`"$($posted.ToUnixTimeSeconds())`" subject=`"$subjectAttr`">")
    [void]$xml.AppendLine("    <groups><group>$Group</group></groups>")
    [void]$xml.AppendLine('    <segments>')
    foreach ($s in $f.Segments) { [void]$xml.AppendLine("      <segment bytes=`"$($s.Bytes)`" number=`"$($s.Number)`">$($s.Id)</segment>") }
    [void]$xml.AppendLine('    </segments>')
    [void]$xml.AppendLine('  </file>')
}
[void]$xml.AppendLine('</nzb>')
$nzbName = "$Name.nzb"
[IO.File]::WriteAllText((Join-Path $OutDirectory $nzbName), $xml.ToString(), (New-Object Text.UTF8Encoding($false)))

$meta = [ordered]@{
    name = $Name; id = "golden$releaseHash"; file = $fileName; nzb = $nzbName; group = $Group
    sizeBytes = $size; parts = $total; crc32 = $crc.ToString('x8'); sha256 = (Get-FileHash $video -Algorithm SHA256).Hash
    files = @($postedFiles | ForEach-Object { [ordered]@{ name = $_.Name; sizeBytes = $_.Size; parts = $_.Total; lastArticleId = $_.Segments[$_.Segments.Count - 1].Id } })
}
Set-Content -Path (Join-Path $OutDirectory 'release.json') -Value ($meta | ConvertTo-Json -Depth 5) -Encoding ascii
Write-Host ("Release {0}: {1:N1} MB in {2} parts. NZB: {3}" -f $Name, ($size / 1MB), $total, (Join-Path $OutDirectory $nzbName))
[pscustomobject]$meta
