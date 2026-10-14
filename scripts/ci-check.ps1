param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $Root

$passCount = 0
$warnCount = 0
$failCount = 0

function Write-Ok {
    param([string]$Message)
    Write-Host "  OK  $Message"
    $script:passCount++
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  !!  $Message"
    $script:warnCount++
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  XX  $Message"
    $script:failCount++
}

function Test-EnvLock {
    param([string]$Text)
    return $Text -match 'MSB3027|MSB3021|MSB3492|CS2012|being used by another process|locked by:|cannot access the file'
}

function Invoke-LoggedCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    $exitCode = 1

    try {
        # Invoke directly instead of Start-Process.  The latter can keep the
        # wrapper alive after a caller has timed out when a tool spawns a
        # short-lived child that inherits one of the redirected handles.
        # Direct invocation preserves the real exit code and waits for the
        # complete command tree before we read its captured output.
        Push-Location $Root
        # Windows PowerShell turns every stderr line from a native command into
        # an ErrorRecord, and under "Stop" the first one ends the script. A tool
        # writing a note to stderr is not a tool failing: Vite prints a build
        # timing warning when a build is slow enough, and the gate failed on it
        # at random while the build itself succeeded (#579). The exit code is
        # what says whether the command failed, and it is read below.
        $wasErrorAction = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            & $FilePath @Arguments 1> $stdoutPath 2> $stderrPath
            $exitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $wasErrorAction
            Pop-Location
        }
    } finally {
        $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw } else { "" }
        $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw } else { "" }
        $text = @($stdout, $stderr) -join ""

        Remove-Item $stdoutPath -ErrorAction SilentlyContinue
        Remove-Item $stderrPath -ErrorAction SilentlyContinue
    }

    return @{
        ExitCode = $exitCode
        Output = $text
    }
}

function Resolve-DotnetPath {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnetCommand) {
        return $dotnetCommand.Source
    }

    $repoLocalDotnet = Join-Path $Root ".dotnet\dotnet.exe"
    if (Test-Path $repoLocalDotnet) {
        return $repoLocalDotnet
    }

    throw "dotnet was not found on PATH and repo-local SDK was not found at $repoLocalDotnet"
}

function Resolve-NpmPath {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -ne $npmCommand) {
        return $npmCommand.Source
    }

    $commonNpmPath = "C:\Program Files\nodejs\npm.cmd"
    if (Test-Path $commonNpmPath) {
        return $commonNpmPath
    }

    throw "npm.cmd was not found on PATH and no fallback path was found."
}

function Resolve-NodePath {
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -ne $nodeCommand) {
        return $nodeCommand.Source
    }

    $commonNodePath = "C:\Program Files\nodejs\node.exe"
    if (Test-Path $commonNodePath) {
        return $commonNodePath
    }

    throw "node.exe was not found on PATH and no fallback path was found."
}

function Resolve-PowerShellPath {
    $psCommand = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($null -ne $psCommand) {
        return $psCommand.Source
    }

    $commonPowerShellPath = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path $commonPowerShellPath) {
        return $commonPowerShellPath
    }

    throw "powershell.exe was not found on PATH and no fallback path was found."
}

$dotnetPath = Resolve-DotnetPath
$npmPath = Resolve-NpmPath
$nodePath = Resolve-NodePath
$powerShellPath = Resolve-PowerShellPath

Write-Host ""
Write-Host "Backend"

$restore = Invoke-LoggedCommand -FilePath $dotnetPath -Arguments @("restore", "Deluno.slnx")
if ($restore.ExitCode -eq 0) {
    Write-Ok "restore"
} else {
    if ($restore.Output) { Write-Host $restore.Output }
    Write-Fail "restore failed"
}

$build = Invoke-LoggedCommand -FilePath $dotnetPath -Arguments @("build", "Deluno.slnx", "--configuration", "Release", "--no-restore", "-m:1")
if ($build.ExitCode -eq 0) {
    Write-Ok "build"
} elseif (Test-EnvLock $build.Output) {
    Write-Warn "Release outputs locked by running server; CI will catch real errors"
} else {
    if ($build.Output) { Write-Host $build.Output }
    Write-Fail "build failed"
}

Write-Host ""
Write-Host "Tray (Linux CI simulation)"

$tray = Invoke-LoggedCommand -FilePath $dotnetPath -Arguments @(
    "build",
    "apps/windows-tray/Deluno.Tray.csproj",
    "-p:SimulateLinuxCI=true",
    "--configuration",
    "Release",
    "--no-restore"
)

if ($tray.ExitCode -eq 0) {
    Write-Ok "tray builds as empty Library"
} elseif (Test-EnvLock $tray.Output) {
    Write-Warn "Tray build locked by running server; CI will catch real errors"
} else {
    if ($tray.Output) { Write-Host $tray.Output }
    Write-Fail "tray would fail on Linux CI"
}

# The tray's tests too: on Linux they build as an empty net10.0 assembly, and a file linked in from outside the project
# folder escapes the project's own "**/*.cs" removal (rc.30.29: ScriptedStateFileSystem.cs broke CI's backend build).
# A separate obj and bin folder, so the Linux target does not disturb the Windows build's restore.
$linuxSim = Join-Path ([IO.Path]::GetTempPath()) "deluno-traytests-linuxsim"
$trayTests = Invoke-LoggedCommand -FilePath $dotnetPath -Arguments @(
    "build",
    "tests/Deluno.Tray.Tests/Deluno.Tray.Tests.csproj",
    "-p:SimulateLinuxCI=true",
    "-p:BaseIntermediateOutputPath=$linuxSim/obj/",
    "-p:OutputPath=$linuxSim/bin/",
    "--configuration",
    "Release"
)

if ($trayTests.ExitCode -eq 0) {
    Write-Ok "tray tests build as an empty assembly"
} elseif (Test-EnvLock $trayTests.Output) {
    Write-Warn "Tray tests build locked by running server; CI will catch real errors"
} else {
    if ($trayTests.Output) { Write-Host $trayTests.Output }
    Write-Fail "tray tests would fail on Linux CI"
}

Write-Host ""
Write-Host "Frontend"

$packageLock = Join-Path $Root "package-lock.json"
$nodeModules = Join-Path $Root "node_modules"
$nodeModulesLock = Join-Path $nodeModules ".package-lock.json"
$needsNpmCi = -not (Test-Path $nodeModules) -or ((Test-Path $packageLock) -and (-not (Test-Path $nodeModulesLock) -or (Get-Item $packageLock).LastWriteTimeUtc -gt (Get-Item $nodeModulesLock).LastWriteTimeUtc))

if ($needsNpmCi) {
    Write-Host "   npm ci (node_modules out of date)..."
    $npmCi = Invoke-LoggedCommand -FilePath $npmPath -Arguments @("ci", "--silent")
    if ($npmCi.ExitCode -eq 0) {
        Write-Ok "npm ci"
    } else {
        if ($npmCi.Output) { Write-Host $npmCi.Output }
        Write-Fail "npm ci failed"
    }
} else {
    Write-Ok "npm ci (node_modules current, skipped)"
}

$web = Invoke-LoggedCommand -FilePath $npmPath -Arguments @("--workspace", "apps/web", "run", "build", "--silent")
if ($web.ExitCode -eq 0) {
    Write-Ok "build:web"
} else {
    if ($web.Output) { Write-Host $web.Output }
    Write-Fail "build:web failed"
}

$sdk = Invoke-LoggedCommand -FilePath $npmPath -Arguments @("run", "typecheck:sdk", "--silent")
if ($sdk.ExitCode -eq 0) {
    Write-Ok "supported TypeScript SDK"
} else {
    if ($sdk.Output) { Write-Host $sdk.Output }
    Write-Fail "supported TypeScript SDK failed"
}

Write-Host ""
Write-Host "Metadata gateway"

$gatewaySyntax = Invoke-LoggedCommand -FilePath $nodePath -Arguments @("--check", "services/metadata-gateway/src/index.js")
$gatewayTvdbSyntax = Invoke-LoggedCommand -FilePath $nodePath -Arguments @("--check", "services/metadata-gateway/src/tvdb.js")
$gatewayCacheSyntax = Invoke-LoggedCommand -FilePath $nodePath -Arguments @("--check", "services/metadata-gateway/src/cache.js")
$gatewayTests = Invoke-LoggedCommand -FilePath $nodePath -Arguments @("--test", "services/metadata-gateway/test/index.test.js", "services/metadata-gateway/test/tvdb.test.js", "services/metadata-gateway/test/hardening.test.js")
if ($gatewaySyntax.ExitCode -eq 0 -and $gatewayTvdbSyntax.ExitCode -eq 0 -and $gatewayCacheSyntax.ExitCode -eq 0 -and $gatewayTests.ExitCode -eq 0) {
    Write-Ok "metadata gateway contract"
} else {
    if ($gatewaySyntax.Output) { Write-Host $gatewaySyntax.Output }
    if ($gatewayTests.Output) { Write-Host $gatewayTests.Output }
    Write-Fail "metadata gateway contract"
}

Write-Host ""
Write-Host "Workflows"

# The same checks CI's repo-checks job runs, so the two cannot drift. Every
# action pinned to a commit SHA with a release comment, every workflow
# least-privilege with a timeout on every job, the release publishing only from
# its one publish job, and the CI verdict and release gate tests (#1124).
$workflowChecks = @(
    @{ Label = "every GitHub Action is pinned to a commit SHA"; Args = @("scripts/check-github-action-pins.mjs") },
    @{ Label = "every workflow has least-privilege permissions and job timeouts"; Args = @("scripts/check-workflow-hygiene.mjs") },
    @{ Label = "release workflow publishes only from its publish job, and ci-passed judges every CI job"; Args = @("scripts/check-release-workflow-gates.mjs") },
    @{ Label = "ci-passed and release-gate tests"; Args = @("--test", "scripts/ci-passed.test.mjs", "scripts/verify-ci-for-release.test.mjs", "scripts/verify-golden-path-for-release.test.mjs", "scripts/tag-release.test.mjs", "scripts/check-release-workflow-gates.test.mjs") },
    @{ Label = "the installer's pinned download clients agree across the pin files, and the stale-pin check works"; Args = @("--test", "scripts/check-component-pins.test.mjs") },
    @{ Label = "the installer's pin files name the same versions and checksums"; Args = @("scripts/check-component-pins.mjs", "--offline") },
    @{ Label = "no test builds an external app's reply inline instead of loading a recording (#1237)"; Args = @("scripts/check-recorded-replies.mjs") },
    @{ Label = "the recorded-reply rule and scrubber work"; Args = @("--test", "scripts/check-recorded-replies.test.mjs", "scripts/recorded-replies/scrub.test.mjs") },
    @{ Label = "Deluno's recordings of its side of the Deluno-Weir talk match their manifest (#1239)"; Args = @("scripts/check-recorded-manifest.mjs") },
    @{ Label = "the recordings manifest check works"; Args = @("--test", "scripts/check-recorded-manifest.test.mjs") },
    @{ Label = "the coverage register names a row for every route, screen, job, migration, integration, tray action and scenario, and every proof it names exists"; Args = @("scripts/check-coverage-register.mjs") },
    @{ Label = "the coverage register check and its inventories work"; Args = @("--test", "scripts/check-coverage-register.test.mjs") }
)
foreach ($check in $workflowChecks) {
    $result = Invoke-LoggedCommand -FilePath $nodePath -Arguments $check.Args
    if ($result.ExitCode -eq 0) {
        Write-Ok $check.Label
    } else {
        if ($result.Output) { Write-Host $result.Output }
        Write-Fail $check.Label
    }
}

Write-Host ""
Write-Host "Agent readiness"

$agents = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/validate-agent-readiness.ps1")
if ($agents.ExitCode -eq 0) {
    Write-Ok "agent readiness"
} else {
    if ($agents.Output) { Write-Host $agents.Output }
    Write-Fail "agent readiness"
}

# Every .ps1 here has to run on both PowerShell editions, because
# run-powershell.mjs falls back from pwsh to powershell and the machine Deluno
# is developed on has only the latter. Three separate outages came from that:
# #445, the reason run-powershell.mjs exists at all, and #461 - where the soak
# collector had never once taken a reading, and exited 0 while not doing so.
$portability = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-powershell-portability.ps1")
if ($portability.ExitCode -eq 0) {
    Write-Ok "scripts run on Windows PowerShell too"
} else {
    if ($portability.Output) { Write-Host $portability.Output }
    Write-Fail "scripts run on Windows PowerShell too"
}

# The golden-path scenario suite (#1238) runs on a VM only a release uses, so its own code is checked here: every script parses,
# the plan and the groups agree, the host judges results and builds the status right, and the Deluno/Weir/DevTools/news-stub code
# works against local mocks.
$scenarioSuite = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/golden-path/Test-ScenarioSuite.ps1")
if ($scenarioSuite.ExitCode -eq 0) {
    Write-Ok "golden-path scenario suite code"
} else {
    if ($scenarioSuite.Output) { Write-Host $scenarioSuite.Output }
    Write-Fail "golden-path scenario suite code"
}

# The HEAD commit message is what gets squashed onto main, and GitHub reads a
# closing verb next to an issue reference as an instruction - without reading
# negation, and without caring that you are quoting. Three issues were closed
# by accident in one day here, twice after the rule was written into AGENTS.md.
# A note in a document did not stop it; this does.
$closingKeywords = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-issue-closing-keywords.ps1")
if ($closingKeywords.ExitCode -eq 0) {
    Write-Ok "commit message closes no issue by accident"
} else {
    if ($closingKeywords.Output) { Write-Host $closingKeywords.Output }
    Write-Fail "commit message closes no issue by accident"
}

# BACKLOG-SWEEP.md is the audit source of truth (#844): a row narrating
# another issue's children as closed must agree with that issue's own row,
# or the document is lying to whoever reads it next.
$backlogSweep = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-backlog-sweep-consistency.ps1")
if ($backlogSweep.ExitCode -eq 0) {
    Write-Ok "BACKLOG-SWEEP.md agrees with itself"
} else {
    if ($backlogSweep.Output) { Write-Host $backlogSweep.Output }
    Write-Fail "BACKLOG-SWEEP.md agrees with itself"
}

# The container build sees only what the Dockerfile copies, and the local gate
# never builds it: a src project reaching for a repository-root file broke six
# Docker runs in a row before anyone looked (#1037).
$dockerCopies = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-dockerfile-copies.ps1")
if ($dockerCopies.ExitCode -eq 0) {
    Write-Ok "Dockerfile copies every root file the build needs"
} else {
    if ($dockerCopies.Output) { Write-Host $dockerCopies.Output }
    Write-Fail "Dockerfile copies every root file the build needs"
}

# RELEASE_NOTES.md ships inside the build so an install can show what
# changed without reading GitHub (#870, the repo stays private). This is a
# warning, not a failure: it is easy to forget the notes section while
# mid-feature, and the real gate is the release checklist, not this push.
$releaseNotes = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-release-notes-version.ps1")
if ($releaseNotes.ExitCode -eq 0) {
    Write-Ok "RELEASE_NOTES.md covers the version being built"
} else {
    if ($releaseNotes.Output) { Write-Host $releaseNotes.Output }
    Write-Warn "RELEASE_NOTES.md has no section for the version being built yet"
}

# Nothing private to the maintainer's own network may sit in a file a stranger
# reads (README, release notes, user-facing docs, the web app's source), and the
# lab password may sit in no tracked file (#1122).
$publicExposure = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-public-exposure.ps1")
if ($publicExposure.ExitCode -eq 0) {
    Write-Ok "No private addresses, names or passwords in public files"
} else {
    if ($publicExposure.Output) { Write-Host $publicExposure.Output }
    Write-Fail "No private addresses, names or passwords in public files"
}

# No databases, keys, Python scripts or .env files in git (#1125).
$trackedFiles = Invoke-LoggedCommand -FilePath $powerShellPath -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/check-tracked-files.ps1")
if ($trackedFiles.ExitCode -eq 0) {
    Write-Ok "No databases, keys, Python scripts or .env files tracked"
} else {
    if ($trackedFiles.Output) { Write-Host $trackedFiles.Output }
    Write-Fail "No databases, keys, Python scripts or .env files tracked"
}

# The Windows icon (installer, tray, taskbar, Start menu) is built from the brand logo; it may never fall behind it
# again (#1129: the installer kept the old logo for five months after the brand changed).
$windowsIcon = Invoke-LoggedCommand -FilePath $nodePath -Arguments @("scripts/check-windows-icon.mjs")
if ($windowsIcon.ExitCode -eq 0) {
    Write-Ok "The Windows icon is built from the current logo"
} else {
    if ($windowsIcon.Output) { Write-Host $windowsIcon.Output }
    Write-Fail "The Windows icon is built from the current logo"
}

Write-Host ""
Write-Host "--------------------------------------"
Write-Host "  Passed: $passCount  Warned: $warnCount  Failed: $failCount"
Write-Host "--------------------------------------"

if ($failCount -gt 0) {
    Write-Host "  XX  Fix the failures above before pushing."
    exit 1
}

if ($warnCount -gt 0) {
    Write-Host "  !!  Warnings present (env locks from running server); safe to push."
    exit 0
}

Write-Host "  OK  All checks passed; safe to push."
