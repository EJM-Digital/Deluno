param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $Root

$dotnet = Get-Command dotnet -ErrorAction Stop
$projects = @(Get-ChildItem -Path (Join-Path $Root "tests") -Filter "*.Tests.csproj" -Recurse | Sort-Object FullName)
if ($projects.Count -eq 0) {
    throw "No .NET test projects were found under $Root\tests."
}

Write-Host "Running $($projects.Count) .NET test projects serially."
foreach ($project in $projects) {
    Write-Host ""
    Write-Host "=== $($project.Name) ==="
    & $dotnet.Source test $project.FullName --configuration Release --no-build --no-restore --logger "trx;LogFileName=backend-tests.trx"
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed in $($project.FullName)."
    }
}

Write-Host ""
Write-Host "All .NET test projects passed serially."
