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

# Invariant text-pin. AGENTS.md and docs/ARCHITECTURE.md historically said
# "Deluno orchestrates external indexers and download clients; it does not
# embed a downloader." That invariant was rewritten when the in-process
# Deluno.Downloader engine was scoped (see
# docs/exec-plans/active/builtin-downloader-architecture.md).
#
# Pin the new text by substring so accidental reverts or paraphrases get
# caught here, not at code-review time. We check three distinctive phrases
# from the canonical invariant — all three must be present in each file.
$invariantPhrases = @(
    "optional in-process download engine",
    "covering NZB (Usenet) and BitTorrent",
    "Domain modules and Integrations must remain agnostic"
)
$invariantFiles = @(
    @{ Path = "AGENTS.md"; Label = "AGENTS.md" },
    @{ Path = "docs\ARCHITECTURE.md"; Label = "docs/ARCHITECTURE.md" }
)
$forbiddenOldInvariant = "it does not embed a downloader"

foreach ($entry in $invariantFiles) {
    $path = Join-Path $Root $entry.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $content = Get-Content -LiteralPath $path -Raw

    if ($content.Contains($forbiddenOldInvariant)) {
        Add-Failure "Old downloader invariant text 'it does not embed a downloader' is back in $($entry.Label). The in-process Deluno.Downloader engine has been scoped; do not revert the invariant. See docs/exec-plans/active/builtin-downloader-architecture.md."
    }

    foreach ($phrase in $invariantPhrases) {
        if (-not $content.Contains($phrase)) {
            Add-Failure "Invariant phrase missing from $($entry.Label): '$phrase'. The downloader invariant must be present verbatim in both AGENTS.md and docs/ARCHITECTURE.md."
        }
    }
}

# Host-wiring parity. Deluno has two ASP.NET host entry points:
#   - src/Deluno.Host/Program.cs (Docker / Linux container)
#   - apps/windows-tray/DelunoServer.cs + ServiceHost.cs (Windows tray)
# Both MUST register the same set of services and map the same set of
# endpoints; if they diverge, the binary that ships in the Velopack
# installer (built from the tray) silently lacks features the Docker
# image has — see v1.1.0 → v1.1.1 hotfix where downloader endpoints
# 500'd because the tray wiring forgot AddDelunoBuiltInDownloaders +
# AddDelunoPlatformSecrets.
$hostWiringRequiredCalls = @(
    "AddDelunoBuiltInDownloaders",
    "AddDelunoPlatformSecrets",
    "MapDelunoDownloaderEndpoints",
    "MapDelunoSecretsDiagnostics"
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
            Add-Failure "Host-wiring parity: $($entry.Label) is missing required call '$call'. Both Deluno.Host/Program.cs and the windows-tray hosts must register and map the same Deluno modules — if one ships without these the runtime will resolve-fail at first /api/downloader request. See v1.1.1 hotfix."
        }
    }
}

$downloadTelemetryStatusPattern = '["''](downloading|queued|completed|stalled|processing|processed|processingFailed|waitingForProcessor|importReady|importQueued|imported|importFailed)["'']'
$downloadTelemetryFiles = @(
    "apps\web\src\routes\dashboard-page.tsx",
    "apps\web\src\routes\indexers-page.tsx",
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
            $line -match "case\s+[""']" -or
            $line -match "status:\s*[""']"

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
