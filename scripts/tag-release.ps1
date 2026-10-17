param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

# The only way a release tag is made (AGENTS.md, Releases). Refuses, with a plain message, unless the working
# tree is clean, HEAD is origin/main, Directory.Build.props and RELEASE_NOTES.md carry $Version, ci.yml passed on
# HEAD and ran the jobs, and the golden path passed on HEAD or on an earlier commit with the same shipped files (scripts/ship-paths.mjs). Then it creates the annotated tag v$Version and pushes
# it. The checks live in scripts/tag-release-checks.mjs and reuse the release workflow's own verify scripts.

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $root
& git fetch origin --quiet
& node (Join-Path $PSScriptRoot "tag-release.mjs") --version $Version
exit $LASTEXITCODE
