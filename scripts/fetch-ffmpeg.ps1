<#
.SYNOPSIS
    Puts FFmpeg where Deluno can find it, once, and caches it.

.DESCRIPTION
    Deluno ships FFmpeg rather than asking for it. Three separate features are
    dark without it and none of them says so loudly: stream validation on
    import, the embedded-subtitle half of the library scan, and subtitle timing
    sync, which cannot exist at all without an audio reference.

    "Install FFmpeg" was the honest answer while nothing depended on it. It stops
    being honest the moment a subtitle is silently left out of sync because a
    binary nobody mentioned is absent — the rig ran for a whole session with no
    ffprobe on it and nothing on screen ever said so.

    The build is BtbN's **LGPL shared** Windows build, pinned to a release line
    rather than `master-latest` so two publishes of the same commit produce the
    same bytes. LGPL and shared together are the redistributable combination:
    the GPL builds cannot ship inside a product, and a static LGPL build would
    oblige us to hand out object files for relinking. Shared means the DLLs
    travel beside the executables, which is why this lands in a folder of its own
    rather than loose next to Deluno.exe.

    The download is cached in tools\ffmpeg — gitignored, ~70 MB compressed — so
    this is a first-run cost, not a per-build one.
#>
param(
    [string]$RuntimeIdentifier = "win-x64",

    # The FFmpeg release line. Bump together with the pin below.
    [string]$Release = "n9.0",

    [switch]$Force
)

$ErrorActionPreference = "Stop"

# The pin. Tag, asset and hash move together or not at all.
$pinnedTag = "autobuild-2026-08-31-13-27"
$pinnedAsset = "ffmpeg-n9.0.1-11-ge47273f4d9-win64-lgpl-shared-9.0.zip"
$pinnedSha256 = "83a824f0729a69d143c9865125bb86988a11dd388325f0033711045522068aa0"

$root = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $root "tools\ffmpeg"
$target = Join-Path $toolsRoot $RuntimeIdentifier
$stamp = Join-Path $target ".release"

if ($RuntimeIdentifier -ne "win-x64") {
    throw "fetch-ffmpeg.ps1 only knows win-x64 so far; $RuntimeIdentifier needs its own asset name."
}

# Already have this exact build? Then this script is a no-op, which is what
# makes it safe to call from the publish script on every build. The stamp names
# the pinned asset and hash, so a changed pin fetches again.
$stampValue = "$Release $pinnedTag $pinnedAsset $pinnedSha256"
if (-not $Force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $stampValue)) {
    Write-Host "FFmpeg $Release ($pinnedAsset) is already in $target."
    return
}

$asset = $pinnedAsset
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$pinnedTag/$asset"

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("deluno-ffmpeg-" + [System.Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    $zip = Join-Path $staging $asset
    Write-Host "Fetching $asset ..."

    # Invoke-WebRequest's progress bar makes a 70 MB download roughly three times
    # slower in Windows PowerShell. It is not decoration; it is the transfer.
    $previousProgress = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $pinnedSha256) {
        throw "FFmpeg checksum mismatch for ${asset}: expected $pinnedSha256 but the download is $actual. Nothing was unpacked."
    }
    Write-Host "SHA-256 matches the pin."

    Write-Host "Extracting ..."
    Expand-Archive -Path $zip -DestinationPath $staging -Force

    # The archive holds one top-level folder whose name carries the build date,
    # so it is found rather than assumed.
    $extracted = Get-ChildItem -Path $staging -Directory | Select-Object -First 1
    if (-not $extracted) {
        throw "$asset did not contain the folder it was supposed to."
    }

    $binary = Join-Path $extracted.FullName "bin"
    foreach ($required in @("ffmpeg.exe", "ffprobe.exe")) {
        if (-not (Test-Path (Join-Path $binary $required))) {
            throw "$asset did not contain $required."
        }
    }

    if (Test-Path $target) {
        Remove-Item -Path $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # bin\ holds the executables and the shared DLLs they need. Everything else
    # in the archive is headers, import libraries and documentation that a
    # published Deluno has no use for.
    #
    # ffplay is a video player with an SDL window. Deluno runs as a service and
    # will never open one, and it is 17.3 MB of the 145 — the single biggest
    # saving available without building FFmpeg ourselves.
    Get-ChildItem -Path $binary -File |
        Where-Object { $_.Name -ne "ffplay.exe" } |
        Copy-Item -Destination $target -Force

    # The licence travels with the binaries. LGPL redistribution requires it, and
    # a licence file nobody copied is the sort of omission that is invisible
    # until it matters.
    $licence = Join-Path $extracted.FullName "LICENSE.txt"
    if (Test-Path $licence) {
        Copy-Item -Path $licence -Destination (Join-Path $target "FFMPEG-LICENSE.txt") -Force
    }

    Set-Content -Path $stamp -Value $stampValue -Encoding utf8

    $size = [math]::Round(((Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host "FFmpeg $Release is in $target ($size MB)."
}
finally {
    Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
