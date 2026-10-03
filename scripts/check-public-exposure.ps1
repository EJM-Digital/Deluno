# Fails when something private to the maintainer's own network, or a lab
# password, appears in a file an outsider will read.
#
# Why: Deluno is going public. The README, the release notes, the user-facing
# docs and the web app's source (what ships in the browser bundle) are what
# strangers see. They must not carry the maintainer's LAN addresses, NAS or
# machine names, or a personal e-mail address. The lab password must appear in
# no tracked file at all (#1122; the git history is scrubbed separately, #1124).
#
# Not checked on purpose: docs/exec-plans/ and scripts/lab/ are internal
# working files that may name the real rig, and test files, which may use any
# placeholder they like.
#
# One list of patterns, here and nowhere else: add a new private thing once.
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
Set-Location $Root

# Fails in user-facing files.
$userFacingPatterns = [ordered]@{
    "a LAN address (192.168.1.x)"      = '10\.1\.1\.'
    "the NAS name"                  = 'nas'
    "the rig's machine name"        = 'MEDIA-SERVER'
    "the workstation's name"        = 'WORKSTATION'
    "a lab password"                = ('Deluno-Lab-' + '2026')
    # Any address, unless it is a noreply one (checked below).
    "an e-mail address"             = '[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}'
}

# Fails in every tracked text file.
$everywherePatterns = [ordered]@{
    "a lab password" = ('Deluno-Lab-' + '2026')
}

$textExtensions = @(".md", ".txt", ".json", ".ts", ".tsx", ".js", ".mjs", ".cjs", ".css", ".html", ".yml", ".yaml", ".ps1", ".cs", ".csproj", ".props", ".sh", ".xml", ".slnx", ".toml", ".config")

$tracked = @(git ls-files) | Where-Object { $textExtensions -contains ([System.IO.Path]::GetExtension($_).ToLowerInvariant()) }

function Test-UserFacing([string]$path) {
    $p = $path.Replace([string][char]92, '/')
    if ($p -eq "README.md" -or $p -eq "RELEASE_NOTES.md") { return $true }
    if ($p -like "docs/exec-plans/*") { return $false }
    if ($p -like "docs/*" -and ($p.Substring(5) -notlike "*/*")) { return $true }
    # The web app's own source ships in the browser bundle. Tests do not.
    if ($p -like "apps/web/src/*" -and $p -notmatch '\.(test|spec)\.[a-z]+$') { return $true }
    return $false
}

$problems = New-Object System.Collections.Generic.List[string]

foreach ($file in $tracked) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $userFacing = Test-UserFacing $file
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines((Join-Path $Root $file))) {
        $lineNumber++
        foreach ($name in $everywherePatterns.Keys) {
            if ($line -match $everywherePatterns[$name]) {
                $problems.Add("${file}:${lineNumber}: $name")
            }
        }
        if (-not $userFacing) { continue }
        foreach ($name in $userFacingPatterns.Keys) {
            if ($name -eq "a lab password") { continue }   # already reported above
            $pattern = $userFacingPatterns[$name]
            foreach ($match in [regex]::Matches($line, $pattern)) {
                if ($name -eq "an e-mail address" -and $match.Value -match 'noreply') { continue }
                $problems.Add("${file}:${lineNumber}: $name")
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "Private details found in files an outsider will read:"
    $problems | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
    Write-Host "Use a placeholder such as 192.168.1.10, <server> or <nas> instead."
    exit 1
}

Write-Host "No private details in user-facing files; no lab password in any tracked file."
exit 0
