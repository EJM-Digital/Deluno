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

# Every project runs, even after one fails.
#
# This used to throw on the first failure, which is the wrong shape for a
# gate whose job is to describe the state of the repository. When CI was
# switched back on, five failures in Deluno.Integrations.Tests -- the
# alphabetically first project -- stopped the run there, and nothing could say
# whether the other six projects were healthy or hiding the same defect. One
# fix, one push, one wait, to find out. Serially is still one project at a
# time, because SQLite and file locks do not want two.
# Build what is about to be tested, rather than trusting what is lying around.
#
# The tests below run `--configuration Release --no-build`, which is right for
# CI because the workflow builds Release immediately before calling this. Run
# by hand it is a trap: `dotnet test` and `dotnet build` produce **Debug**
# output, so the gate quietly exercised whatever Release binaries were last
# left behind and reported a confident green on code it had never compiled.
# That happened on 2026-09-05 -- a local run counted 1,232 tests in
# Deluno.Persistence.Tests where CI counted 1,261, and three real failures
# reached the pull request.
#
# So the build happens here, in the one place that knows which configuration
# the tests use. It is a no-op of a few seconds when the output is current,
# including on CI.
Write-Host "Building Release before testing it."
& $dotnet.Source build (Join-Path $Root "Deluno.slnx") --configuration Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed, so the tests below would have run against stale binaries."
}

Write-Host ""
Write-Host "Running $($projects.Count) .NET test projects serially."
$failed = [System.Collections.Generic.List[string]]::new()
foreach ($project in $projects) {
    Write-Host ""
    Write-Host "=== $($project.Name) ==="
    # NOTE on --blame-hang, which was here and is deliberately not any more.
    #
    # It was added to tell a hung project from a slow one, and it answered in
    # one run: "All tests finished running" for every project, and then eight
    # minutes of nothing in Deluno.Persistence.Tests. So no test hangs — every
    # test passes and the test host then does not exit. That also reproduces on
    # Windows, where a leftover testhost holds the built DLLs and the next build
    # fails to copy them.
    #
    # It is off again because it turns that slow exit into a failed build, which
    # stops work on everything else while a test-host lifetime problem is
    # diagnosed. The problem is real and is written down; this gate should
    # report on the tests.
    & $dotnet.Source test $project.FullName --configuration Release --no-build --no-restore `
        --logger "trx;LogFileName=backend-tests.trx"
    if ($LASTEXITCODE -ne 0) {
        $failed.Add($project.Name)
    }
}

Write-Host ""
if ($failed.Count -gt 0) {
    throw "Tests failed in $($failed.Count) of $($projects.Count) projects: $($failed -join ', ')."
}

Write-Host "All .NET test projects passed serially."
