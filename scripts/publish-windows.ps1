param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",

    # Lab iteration builds. Skips single-file bundling and ReadyToRun, so the
    # output is ordinary assemblies: a one-line change produces a few hundred
    # kilobytes of altered DLLs instead of a fresh 163 MB bundle, and
    # deploy-lab.ps1 can copy only what actually differs.
    #
    # Releases must not use this. The shipped artifact is the single file.
    [switch]$Fast
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts\publish\$RuntimeIdentifier"

Push-Location $root
try {
    # dotnet publish never removes what it no longer produces, and the web
    # assets are content-hashed, so every build left its chunks behind for
    # ever. This folder had reached 3,096 asset files where a build makes 83 -
    # 130 MB of dead weight, zipped and shipped to the lab on every deploy,
    # and thousands of orphaned files accumulating on the host.
    if (Test-Path $artifacts) {
        Write-Host "Clearing previous publish output at $artifacts"
        Remove-Item -Path $artifacts -Recurse -Force
    }

    npm.cmd run build:web

    # A repo-local SDK if one has been pinned here, otherwise whatever `dotnet`
    # is on PATH. This used to name `.\.dotnet\dotnet.exe` outright, which does
    # not exist on a machine that installed the SDK normally — so the publish
    # failed on the first line for anyone who had not vendored a copy.
    $localDotnet = Join-Path $root ".dotnet\dotnet.exe"
    $dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

    $singleFile = if ($Fast) { "false" } else { "true" }
    $readyToRun = if ($Fast) { "false" } else { "true" }
    if ($Fast) {
        Write-Host "Fast lab build: no single-file bundle, no ReadyToRun."
    }

    & $dotnet publish .\src\Deluno.Host\Deluno.Host.csproj `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=$singleFile `
        -p:PublishReadyToRun=$readyToRun `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $artifacts

    # FFmpeg travels with the publish. Three features are dark without it —
    # stream validation on import, the embedded-subtitle half of the library
    # scan, and subtitle timing sync, which has no reference to align against at
    # all. The fetch is cached, so this is a no-op on every build after the
    # first.
    #
    # It goes in a folder rather than loose beside Deluno.exe because it is a
    # shared build: the executables are useless without their DLLs, and
    # FfmpegTools.BundledFolder is the other half of this agreement.
    & (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1") -RuntimeIdentifier $RuntimeIdentifier

    $ffmpegSource = Join-Path $root "tools\ffmpeg\$RuntimeIdentifier"
    $ffmpegTarget = Join-Path $artifacts "tools\ffmpeg"
    New-Item -ItemType Directory -Path $ffmpegTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $ffmpegSource "*") -Destination $ffmpegTarget -Recurse -Force

    Write-Host "Published Deluno to $artifacts"
}
finally {
    Pop-Location
}
