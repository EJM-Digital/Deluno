<#
.SYNOPSIS
    Prints an issue's acceptance criteria as an unticked checklist.

.DESCRIPTION
    A session that reads an issue for context tends to remember the shape of
    the work and forget the finish line. That is how a backlog sweep merges ten
    pull requests and closes one issue: each slice ends with a "remaining
    scope" paragraph invented on the spot, and nobody ever puts the issue's own
    list next to the evidence.

    This does one thing: pulls the "Done when" / acceptance section out of the
    issue and prints it as a checklist you have to answer line by line. The
    list is the whole contract. Do not add to it - if something extra seems
    necessary, that is a conversation with the owner, not a private raising of
    the bar.

.EXAMPLE
    ./scripts/issue-audit.ps1 357
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int]$Number,

    # Print the whole body rather than just the acceptance section.
    [switch]$Full
)

$ErrorActionPreference = "Stop"

$json = gh issue view $Number --json number,title,state,body 2>$null
if (-not $json) { throw "Could not read issue #$Number. Is gh authenticated?" }

$issue = $json | ConvertFrom-Json

Write-Host ""
Write-Host "#$($issue.number) [$($issue.state)] $($issue.title)"
Write-Host ("=" * 72)

if ($Full) {
    Write-Host $issue.body
    exit 0
}

$lines = $issue.body -split "`r?`n"

# Headings that mean "this is the finish line", across the shapes used in this
# repository: "## Done when", "## 17. Done when", "## Acceptance".
$startPattern = '^\s{0,3}#{1,6}\s*(\d+[\.\)]\s*)?(done when|acceptance|exit criteria|definition of done)\b'
$anyHeading = '^\s{0,3}#{1,6}\s'

$capturing = $false
$criteria = @()

foreach ($line in $lines) {
    if ($line -match $startPattern) { $capturing = $true; continue }
    if ($capturing -and $line -match $anyHeading) { break }
    if ($capturing) { $criteria += $line }
}

if ($criteria.Count -eq 0) {
    Write-Host ""
    Write-Host "No 'Done when' section found." -ForegroundColor Yellow
    Write-Host "Read the whole issue (-Full) and state the criteria yourself before working it." -ForegroundColor Yellow
    Write-Host ""
    exit 2
}

# Each bullet or checkbox is one criterion to answer.
$items = @($criteria | Where-Object { $_ -match '^\s*([-*]|\d+\.)\s' })
if ($items.Count -eq 0) { $items = @($criteria | Where-Object { $_.Trim() }) }

Write-Host ""
Write-Host "ACCEPTANCE CRITERIA - answer every line MET (with evidence) or NOT MET (with what would satisfy it)."
Write-Host ""

$index = 1
foreach ($item in $items) {
    $text = ($item -replace '^\s*([-*]|\d+\.)\s*', '' -replace '^\[[ xX]\]\s*', '').Trim()
    if (-not $text) { continue }
    Write-Host ("  {0,2}. [ ] {1}" -f $index, $text)
    $index++
}

Write-Host ""
Write-Host "$($index - 1) criteria. The list is the whole contract - do not add to it."
Write-Host "To leave the issue open you must name which line is unmet and what would close it."
Write-Host "'Broad issue', 'epic', 'only a slice' and 'more work exists' are not reasons."
Write-Host ""
