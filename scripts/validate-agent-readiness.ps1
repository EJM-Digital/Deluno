param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$Message) {
    $script:failures.Add($Message) | Out-Null
}

function Require-File([string]$RelativePath) {
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Failure "Missing required file: $RelativePath"
    }
}

Require-File "AGENTS.md"
Require-File "docs\README.md"
Require-File "docs\ARCHITECTURE.md"
Require-File "docs\QUALITY_SCORE.md"
Require-File "docs\repo-change-history.md"
Require-File "docs\deluno-capability-map.md"
Require-File "docs\deluno-ui-api-contract.md"
Require-File "docs\external-integration-api.md"
Require-File "docs\packaging.md"
Require-File "docs\DEPLOYMENT.md"
Require-File "docs\TROUBLESHOOTING.md"
Require-File "docs\exec-plans\completed\agent-first-realignment.md"
Require-File "docs\exec-plans\tech-debt-tracker.md"
Require-File "docs\exec-plans\templates\large-feature-work.md"
Require-File "docs\exec-plans\templates\post-merge-cleanup.md"
Require-File "scripts\start-local-app.ps1"

$agentsPath = Join-Path $Root "AGENTS.md"
if (Test-Path -LiteralPath $agentsPath) {
    $lineCount = (Get-Content -LiteralPath $agentsPath).Count
    if ($lineCount -gt 140) {
        Add-Failure "AGENTS.md is $lineCount lines; keep it at or below 140 lines and move detail into docs/."
    }
}

$textRoots = @(
    "AGENTS.md",
    "README.md",
    "docs"
)

foreach ($entry in $textRoots) {
    $path = Join-Path $Root $entry
    if (-not (Test-Path -LiteralPath $path)) {
        continue
    }

    $files = if (Test-Path -LiteralPath $path -PathType Container) {
        Get-ChildItem -LiteralPath $path -Recurse -File -Include *.md,*.txt
    } else {
        Get-Item -LiteralPath $path
    }

    foreach ($file in $files) {
        $lines = Get-Content -LiteralPath $file.FullName
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            $mentionsOldPath = $line -match "C:\\Users\\User\\Deluno" -or $line -match "C:/Users/User/Deluno"
            $isWarning = $line -match "Do not use" -or $line -match "old workspace" -or $line -match "old .*path"
            if ($mentionsOldPath -and -not $isWarning) {
                $relative = Resolve-Path -LiteralPath $file.FullName -Relative
                Add-Failure "Stale workspace path found in ${relative}:$($index + 1). Use C:\Users\User\Projects\Deluno or relative paths."
            }
        }
    }
}

$forbiddenReferences = @(
    @{ Project = "src\Deluno.Movies\Deluno.Movies.csproj"; Pattern = "Deluno.Series.csproj"; Message = "Movies must not reference Series." },
    @{ Project = "src\Deluno.Series\Deluno.Series.csproj"; Pattern = "Deluno.Movies.csproj"; Message = "Series must not reference Movies." },
    @{ Project = "src\Deluno.Integrations\Deluno.Integrations.csproj"; Pattern = "Deluno.Movies.csproj|Deluno.Series.csproj|Deluno.Filesystem.csproj"; Message = "Integrations must stay domain-neutral." }
)

foreach ($rule in $forbiddenReferences) {
    $projectPath = Join-Path $Root $rule.Project
    if (-not (Test-Path -LiteralPath $projectPath)) {
        Add-Failure "Missing project for architecture validation: $($rule.Project)"
        continue
    }

    $content = Get-Content -LiteralPath $projectPath -Raw
    if ($content -match $rule.Pattern) {
        Add-Failure $rule.Message
    }
}

# Product boundary: Deluno orchestrates external indexers and download clients;
# it does not include an in-process transfer engine. Pin that boundary in the
# two canonical architecture documents so an accidental protocol implementation
# cannot return through a documentation-only change.
$invariantPhrases = @(
    "orchestrates external indexers and download clients",
    "does not embed a transfer engine",
    "external clients remain responsible"
)
$invariantFiles = @(
    @{ Path = "AGENTS.md"; Label = "AGENTS.md" },
    @{ Path = "docs\ARCHITECTURE.md"; Label = "docs/ARCHITECTURE.md" }
)
foreach ($entry in $invariantFiles) {
    $path = Join-Path $Root $entry.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $content = Get-Content -LiteralPath $path -Raw

    foreach ($phrase in $invariantPhrases) {
        if (-not $content.Contains($phrase)) {
            Add-Failure "External-download-client invariant phrase missing from $($entry.Label): '$phrase'."
        }
    }
}

# Host-wiring parity. Deluno has three ASP.NET host entry points:
#   - src/Deluno.Host/Program.cs (Docker / Linux container)
#   - apps/windows-tray/DelunoServer.cs (the Windows tray, which is what the
#     Velopack installer runs)
#   - apps/windows-tray/ServiceHost.cs (the same app as a Windows service)
#
# All three MUST register the same services and map the same endpoints. This
# check has always said so. Until 2026-09-04 it enforced exactly one call, and
# in the gap the hosts drifted badly enough that the installer shipped an
# application that could not start: Deluno.Host composed sixteen modules and
# mapped twenty-three endpoint groups where the tray composed twelve and mapped
# fifteen. The packaged build threw on IDispatchRecoveryHandler and never opened
# a listener, and every read route the tray had not mapped was answered by its
# SPA fallback with the web page and a 200.
#
# The fix was one composition and one endpoint map, so what this now checks is
# that all three use them rather than hand-listing anything. Naming the risk in
# a comment and not checking it is how the drift lasted.
$hostWiringRequiredCalls = @(
    "AddDelunoPlatformSecrets",
    "AddDelunoApplicationModules",
    "MapDelunoApplicationEndpoints"
)
$hostWiringFiles = @(
    @{ Path = "src\Deluno.Host\Program.cs"; Label = "src/Deluno.Host/Program.cs" },
    @{ Path = "apps\windows-tray\DelunoServer.cs"; Label = "apps/windows-tray/DelunoServer.cs" },
    @{ Path = "apps\windows-tray\ServiceHost.cs"; Label = "apps/windows-tray/ServiceHost.cs" }
)
foreach ($entry in $hostWiringFiles) {
    $path = Join-Path $Root $entry.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $content = Get-Content -LiteralPath $path -Raw
    foreach ($call in $hostWiringRequiredCalls) {
        if (-not $content.Contains($call)) {
            Add-Failure "Host-wiring parity: $($entry.Label) is missing required call '$call'."
        }
    }

    # A host that maps the shared endpoints must not then hand unknown /api
    # paths to the SPA. MapFallbackToFile has no way to exclude them, so a
    # missing route returns index.html with a 200 and a broken client call
    # looks like a successful page load.
    if ($content.Contains("MapFallbackToFile")) {
        Add-Failure "Host-wiring parity: $($entry.Label) uses MapFallbackToFile, which answers unknown /api paths with the app shell. Use MapFallback with a /api guard."
    }
}

# The shared pieces both hosts depend on, in the library both reference.
foreach ($shared in @(
    @{ Path = "src\Deluno.Worker\DelunoApplicationComposition.cs"; Label = "shared composition" },
    @{ Path = "src\Deluno.Worker\DelunoApplicationEndpointMapping.cs"; Label = "shared endpoint map" }
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $shared.Path) -PathType Leaf)) {
        Add-Failure "Host-wiring parity: missing $($shared.Label) at $($shared.Path -replace '\\', '/')."
    }
}

$sharedMapPath = Join-Path $Root "src\Deluno.Worker\DelunoApplicationEndpointMapping.cs"
if (Test-Path -LiteralPath $sharedMapPath -PathType Leaf) {
    $sharedMap = Get-Content -LiteralPath $sharedMapPath -Raw
    # A spot-check that the map still carries the groups whose absence was
    # invisible from the outside: reads answered by the fallback, writes 405.
    foreach ($required in @(
        "MapDelunoSecretsDiagnostics", "MapDelunoLibraries", "MapDelunoConnections",
        "MapDelunoQuality", "MapDelunoAutomationEndpoints")) {
        if (-not $sharedMap.Contains($required)) {
            Add-Failure "Host-wiring parity: shared endpoint map is missing required call '$required'."
        }
    }
}

# Deluno ships unsigned by decision. What must stay true is that signing is
# automatic the moment a certificate exists, that verification only runs
# against something actually signed, and that the SmartScreen consequence is
# documented wherever a user meets the installer.
$releaseWorkflowPath = Join-Path $Root ".github\workflows\release.yml"
if (-not (Test-Path -LiteralPath $releaseWorkflowPath -PathType Leaf)) {
    Add-Failure "Missing release workflow: .github/workflows/release.yml"
} else {
    $releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw

    # Deluno ships unsigned by decision (2026-09-02), so the old assertion --
    # that a 1.x build hard-fails without a certificate -- would now block
    # every release. What still has to hold is the pair that keeps the
    # decision honest: signing happens automatically the moment a certificate
    # exists, and signature verification only runs against something that was
    # actually signed. Verifying an unsigned build against a signature
    # requirement is how a release nobody intended to sign fails at the end.
    foreach ($requiredReleaseGate in @(
        "if: `${{ env.CERT_PATH != '' }}",
        "if: `${{ env.CERT_PATH != '' && steps.skipcheck.outputs.skip != 'true' }}"
    )) {
        if (-not $releaseWorkflow.Contains($requiredReleaseGate)) {
            Add-Failure "Release workflow is missing '$requiredReleaseGate'. Signing must stay opt-in on certificate presence, and verification must only run when something was signed."
        }
    }

    # And the consequence must never become undocumented. Somebody meeting
    # SmartScreen with no warning assumes the download is broken.
    $smartScreenDocs = @("README.md", "docs/ga-release-checklist.md", "docs/release-notes-1.0.0-draft.md")
    foreach ($docPath in $smartScreenDocs) {
        $full = Join-Path $Root $docPath
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Add-Failure "Missing release document: $docPath"
        } elseif ((Get-Content -LiteralPath $full -Raw) -notmatch 'SmartScreen') {
            Add-Failure "$docPath does not warn that Deluno is unsigned and Windows SmartScreen will prompt on first install."
        }
    }
}

$downloadTelemetryStatusPattern = '(downloading|queued|completed|stalled|processing|processed|processingFailed|waitingForProcessor|importReady|importQueued|imported|importFailed)'
$downloadTelemetryFiles = @(
    "apps\web\src\routes\dashboard-page.tsx",
    "apps\web\src\routes\connections-screen.tsx",
    "apps\web\src\routes\queue-page.tsx",
    "apps\web\src\lib\ui-adapters.ts"
)

foreach ($relativePath in $downloadTelemetryFiles) {
    $path = Join-Path $Root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Failure "Missing frontend telemetry file for status validation: $relativePath"
        continue
    }

    $lines = Get-Content -LiteralPath $path
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line -notmatch $downloadTelemetryStatusPattern) {
            continue
        }

        $isStatusLogic =
            $line -match "\.status\s*[!=]==" -or
            $line -match "\.status\s+is" -or
            $line -match 'case\s+' -or
            $line -match 'status:\s*'

        if (-not $isStatusLogic) {
            continue
        }

        $usesSharedHelper = $line -match "downloadQueueStatuses"
        $isKnownNonTelemetryStatus =
            $line -match "job\.status" -or
            $line -match "data\.automation" -or
            $line -match "automation\.filter" -or
            $line -match "item\.outcome"

        if (-not $usesSharedHelper -and -not $isKnownNonTelemetryStatus) {
            Add-Failure "Duplicated download telemetry status literal in ${relativePath}:$($index + 1). Use apps/web/src/lib/download-telemetry.ts helpers."
        }
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure -ErrorAction Continue
    }
    exit 1
}

Write-Host "Agent readiness validation passed."
