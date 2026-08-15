[CmdletBinding()]
param(
    [switch]$FullSuite
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$flowFilter = 'FullyQualifiedName~ReferenceSearchFlowTests|FullyQualifiedName~MediaAcquisitionFlowTests|FullyQualifiedName~ImportPipelineServiceTests|FullyQualifiedName~DownloadDispatchesApiTests|FullyQualifiedName~DownloadRetryServiceTests'

Push-Location $repoRoot
try {
    Write-Host 'Running isolated real-world media acquisition fixtures...' -ForegroundColor Cyan
    dotnet test tests/Deluno.Persistence.Tests/Deluno.Persistence.Tests.csproj `
        --configuration Release `
        --no-restore `
        --filter $flowFilter
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    if ($FullSuite) {
        Write-Host 'Running the full Deluno release suite...' -ForegroundColor Cyan
        dotnet test Deluno.slnx --configuration Release --no-build
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
}
finally {
    Pop-Location
}
