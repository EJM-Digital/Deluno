<#
.SYNOPSIS
    Warns when the version currently being built has no section in
    RELEASE_NOTES.md.

.DESCRIPTION
    RELEASE_NOTES.md carries each version's plain-language notes inside the
    build itself (#870): the repo is private, so an install can't read
    GitHub releases, and the Updates card shows these notes for every
    version it has actually run. That only works if whoever cuts a release
    remembers to add a "## <version>" section before shipping it.

    This reads <Version> from Directory.Build.props (the version currently
    being built - see the comment next to it) and checks RELEASE_NOTES.md
    for a matching "## <version>" heading. Missing is a WARNING, not a
    failure: docs/ga-release-checklist.md is where writing the section is a
    real release step, and a developer mid-feature on a version they have
    not written notes for yet should not be blocked from pushing.

.PARAMETER PropsPath
    Path to Directory.Build.props. Defaults to the repo root copy.

.PARAMETER NotesPath
    Path to RELEASE_NOTES.md. Defaults to the repo root copy.
#>
param(
    [string]$PropsPath = "Directory.Build.props",
    [string]$NotesPath = "RELEASE_NOTES.md"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PropsPath)) {
    throw "No such file: $PropsPath"
}

$propsText = Get-Content -LiteralPath $PropsPath -Raw
$versionMatch = [regex]::Match($propsText, "<Version>\s*([^<\s]+)\s*</Version>")
if (-not $versionMatch.Success) {
    # No <Version> element to check against - nothing to warn about.
    Write-Host "  (no <Version> in $PropsPath; nothing to check)"
    exit 0
}

$version = $versionMatch.Groups[1].Value

if (-not (Test-Path -LiteralPath $NotesPath)) {
    Write-Host "  $NotesPath does not exist; version $version has no release notes."
    exit 1
}

$notesText = Get-Content -LiteralPath $NotesPath -Raw
$headingPattern = "(?m)^##\s+" + [regex]::Escape($version) + "\s*$"
if ([regex]::IsMatch($notesText, $headingPattern)) {
    Write-Host "  $NotesPath has a section for $version."
    exit 0
}

Write-Host "  $NotesPath has no '## $version' section yet. Add one before release (docs/ga-release-checklist.md)."
exit 1
