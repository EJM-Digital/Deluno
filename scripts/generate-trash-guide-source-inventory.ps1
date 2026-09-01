[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\Deluno.Quality\Guides\trash-guide-source-inventory.json'),
    [string]$ExpectedRevision = 'a63c1d05510d73887be1b0198f95a363c4f7bef6'
)

$ErrorActionPreference = 'Stop'

$resolvedSource = (Resolve-Path -LiteralPath $SourceRoot).Path
$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
$revision = (git -C $resolvedSource rev-parse HEAD).Trim()
if ($revision -ne $ExpectedRevision) {
    throw "Expected TRaSH Guides revision $ExpectedRevision but source is $revision."
}

function Get-JsonFiles([string]$RelativePath) {
    Get-ChildItem -LiteralPath (Join-Path $resolvedSource $RelativePath) -File -Filter '*.json' |
        Sort-Object FullName
}

function Convert-SourceCustomFormat([System.IO.FileInfo]$File, [string]$MediaType) {
    $raw = Get-Content -LiteralPath $File.FullName -Raw | ConvertFrom-Json -Depth 100
    $scores = [ordered]@{}
    if ($null -ne $raw.trash_scores) {
        $raw.trash_scores.PSObject.Properties |
            Sort-Object Name |
            ForEach-Object { $scores[$_.Name] = [int]$_.Value }
    }
    $clauses = @($raw.specifications | ForEach-Object {
        $fields = if ($null -eq $_.fields) { 'null' } else { $_.fields | ConvertTo-Json -Compress -Depth 100 }
        [ordered]@{
            name = [string]$_.name
            implementation = [string]$_.implementation
            negate = [bool]$_.negate
            required = if ($null -eq $_.PSObject.Properties['required']) { $true } else { [bool]$_.required }
            fieldsJson = $fields
        }
    })
    [ordered]@{
        trashId = [string]$raw.trash_id
        name = [string]$raw.name
        description = if ($null -eq $raw.trash_description) { $null } else { [string]$raw.trash_description }
        mediaType = $MediaType
        sourcePath = $File.FullName.Substring($resolvedSource.Length + 1).Replace('\', '/')
        scores = $scores
        includeWhenRenaming = [bool]$raw.includeCustomFormatWhenRenaming
        matcherClauses = $clauses
    }
}

function Convert-SourceFormatGroup([System.IO.FileInfo]$File, [string]$MediaType) {
    $raw = Get-Content -LiteralPath $File.FullName -Raw | ConvertFrom-Json -Depth 100
    $entries = @($raw.custom_formats | ForEach-Object {
        [ordered]@{
            trashId = [string]$_.trash_id
            name = [string]$_.name
            required = [bool]$_.required
        }
    })
    $profileIds = @()
    if ($null -ne $raw.quality_profiles -and $null -ne $raw.quality_profiles.include) {
        $profileIds = @($raw.quality_profiles.include.PSObject.Properties |
            Sort-Object Name |
            ForEach-Object { [string]$_.Value })
    }
    [ordered]@{
        trashId = [string]$raw.trash_id
        name = [string]$raw.name
        description = if ($null -eq $raw.trash_description) { $null } else { [string]$raw.trash_description }
        mediaType = $MediaType
        sourcePath = $File.FullName.Substring($resolvedSource.Length + 1).Replace('\', '/')
        customFormats = $entries
        qualityProfileIds = $profileIds
    }
}

function Convert-SourceQualityProfile([System.IO.FileInfo]$File, [string]$MediaType) {
    $rawText = Get-Content -LiteralPath $File.FullName -Raw
    $raw = $rawText | ConvertFrom-Json -Depth 100
    $assignments = @()
    if ($null -ne $raw.formatItems) {
        $assignments = @($raw.formatItems.PSObject.Properties |
            Sort-Object Name |
            ForEach-Object { [ordered]@{ name = $_.Name; trashId = [string]$_.Value } })
    }
    [ordered]@{
        trashId = [string]$raw.trash_id
        name = [string]$raw.name
        description = if ($null -eq $raw.trash_description) { $null } else { [string]$raw.trash_description }
        mediaType = $MediaType
        sourcePath = $File.FullName.Substring($resolvedSource.Length + 1).Replace('\', '/')
        formatAssignments = $assignments
        definitionJson = $rawText.Trim()
    }
}

$customFormats = @(
    Get-JsonFiles 'docs\json\radarr\cf' | ForEach-Object { Convert-SourceCustomFormat $_ 'movies' }
    Get-JsonFiles 'docs\json\sonarr\cf' | ForEach-Object { Convert-SourceCustomFormat $_ 'tv' }
)
$groups = @(
    Get-JsonFiles 'docs\json\radarr\cf-groups' | ForEach-Object { Convert-SourceFormatGroup $_ 'movies' }
    Get-JsonFiles 'docs\json\sonarr\cf-groups' | ForEach-Object { Convert-SourceFormatGroup $_ 'tv' }
)
$profiles = @(
    Get-JsonFiles 'docs\json\radarr\quality-profiles' | ForEach-Object { Convert-SourceQualityProfile $_ 'movies' }
    Get-JsonFiles 'docs\json\sonarr\quality-profiles' | ForEach-Object { Convert-SourceQualityProfile $_ 'tv' }
)

$inventory = [ordered]@{
    schemaVersion = 1
    upstreamRevision = $revision
    customFormats = @($customFormats | Sort-Object mediaType, trashId)
    formatGroups = @($groups | Sort-Object mediaType, trashId)
    qualityProfiles = @($profiles | Sort-Object mediaType, trashId)
}

$directory = Split-Path -Parent $resolvedDestination
if (-not (Test-Path -LiteralPath $directory)) {
    throw "Destination directory '$directory' does not exist."
}

$json = $inventory | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText($resolvedDestination, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($customFormats.Count) custom formats, $($groups.Count) groups, and $($profiles.Count) quality profiles to $resolvedDestination"
