param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts\publish\$RuntimeIdentifier"

Push-Location $root
try {
    npm.cmd run build:web

    # A repo-local SDK if one has been pinned here, otherwise whatever `dotnet`
    # is on PATH. This used to name `.\.dotnet\dotnet.exe` outright, which does
    # not exist on a machine that installed the SDK normally — so the publish
    # failed on the first line for anyone who had not vendored a copy.
    $localDotnet = Join-Path $root ".dotnet\dotnet.exe"
    $dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

    & $dotnet publish .\src\Deluno.Host\Deluno.Host.csproj `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
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
