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
Require-File "docs\deluno-capability-map.md"
Require-File "docs\deluno-ui-api-contract.md"
Require-File "docs\external-integration-api.md"
Require-File "docs\exec-plans\active\agent-first-realignment.md"
Require-File "docs\exec-plans\tech-debt-tracker.md"

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

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure -ErrorAction Continue
    }
    exit 1
}

Write-Host "Agent readiness validation passed."
