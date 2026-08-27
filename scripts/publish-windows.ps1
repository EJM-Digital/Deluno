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

    Write-Host "Published Deluno to $artifacts"
}
finally {
    Pop-Location
}
