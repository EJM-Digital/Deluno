# Runtime data and throwaway tooling must never be tracked (#1125): databases
# and their -wal/-shm files, data-protection keys, Python scripts (Deluno's
# tooling is PowerShell, C# and TypeScript) and real .env files.
# Only .env.example is allowed.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $tracked = @(git ls-files)
} finally {
    Pop-Location
}

$bad = New-Object System.Collections.Generic.List[string]
foreach ($file in $tracked) {
    $name = [System.IO.Path]::GetFileName($file)
    $reason = $null
    if ($file -match '\.db(-wal|-shm)?$') { $reason = "database file" }
    elseif ($file -match '(^|/)protection-keys/') { $reason = "data-protection key" }
    elseif ($file -match '\.py$') { $reason = "Python script" }
    elseif ($name -eq '.env' -or ($name -like '.env.*' -and $name -ne '.env.example') -or $name -like '*.env') {
        if ($name -ne '.env.example') { $reason = "environment file" }
    }
    if ($reason) { $bad.Add("$file ($reason)") }
}

if ($bad.Count -gt 0) {
    Write-Host "These tracked files do not belong in the repository:"
    $bad | ForEach-Object { Write-Host "  $_" }
    exit 1
}

exit 0
