# A src project that pulls a file from the repository root (..\..\X) builds
# fine on a developer machine and in ci.yml, but the container's publish stage
# only sees what the Dockerfile copies. RELEASE_NOTES.md broke six Docker runs
# in a row that way (#1037), and the local gate never builds the container.
# This fails when a root file a project needs is not copied into the image.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$dockerfile = Get-Content -Raw (Join-Path $repoRoot "Dockerfile")

$copied = New-Object System.Collections.Generic.List[string]
foreach ($match in [regex]::Matches($dockerfile, '(?m)^COPY\s+(?!--from)(.+)$')) {
    $parts = $match.Groups[1].Value.Trim() -split '\s+'
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $copied.Add($parts[$i].TrimEnd('/').Replace('\', '/'))
    }
}

$missing = New-Object System.Collections.Generic.List[string]
$projects = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -Filter *.csproj
foreach ($project in $projects) {
    $text = Get-Content -Raw $project.FullName
    foreach ($match in [regex]::Matches($text, 'Include="\.\.[\\/]\.\.[\\/]([^"*]+)"')) {
        $relative = $match.Groups[1].Value.Replace('\', '/')
        $top = ($relative -split '/')[0]
        # apps/web/dist comes from the web-build stage, not a plain COPY.
        if ($relative -like 'apps/web/dist*') { continue }
        if ($copied -contains $relative -or $copied -contains $top) { continue }
        $missing.Add("$($project.Name): $relative")
    }
}

if ($missing.Count -gt 0) {
    Write-Host "These repository-root files are used by a src project but not copied by the Dockerfile:"
    $missing | ForEach-Object { Write-Host "  $_" }
    exit 1
}

exit 0
