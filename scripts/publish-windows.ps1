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

    & .\.dotnet\dotnet.exe publish .\src\Deluno.Host\Deluno.Host.csproj `
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
