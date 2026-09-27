<#
.SYNOPSIS
    Fails when docs/exec-plans/active/BACKLOG-SWEEP.md contradicts itself
    about whether an issue is closed.

.DESCRIPTION
    BACKLOG-SWEEP.md is the source of truth for what has been audited and
    closed. Its own table can drift out of sync with the prose one row uses
    to describe another issue's children — #844 found #301's row saying
    "children #321/#329/#330 all closed" while #329's own row still read
    "no" / open, because #329 had been closed on GitHub after that row was
    last touched.

    This walks every table row, decides whether that row's OWN issue is
    closed (Verdict/Audited says so) or still open, then checks every OTHER
    issue number that row's text asserts is "closed" against that issue's
    own row. A mismatch is the exact contradiction #844 exists to prevent.

.PARAMETER Path
    The backlog file to check. Defaults to the real one.
#>
param(
    [string]$Path = "docs/exec-plans/active/BACKLOG-SWEEP.md"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    throw "No such file: $Path"
}

$lines = Get-Content -LiteralPath $Path

# Table rows look like: | #NNN Title | criteria | audited | verdict text |
$rowPattern = '^\|\s*(?:~~)?#(\d+)\b.*\|\s*[^|]*\|\s*([^|]*)\|\s*(.*?)\s*\|\s*$'

$rows = @()
foreach ($line in $lines) {
    $match = [regex]::Match($line, $rowPattern)
    if ($match.Success) {
        $rows += [pscustomobject]@{
            Issue   = [int]$match.Groups[1].Value
            Audited = $match.Groups[2].Value.Trim()
            Verdict = $match.Groups[3].Value.Trim()
            Line    = $line
        }
    }
}

if ($rows.Count -eq 0) {
    throw "No table rows matched in $Path - the row format may have changed; update the regex."
}

function Test-ClosedRow($row) {
    # "Already closed", "Closed 4 Sep", "**Closed**" etc. PARKED is its own
    # state and never counts as closed.
    return $row.Verdict -match '(?i)\bclosed\b' -and $row.Verdict -notmatch '(?i)\bPARKED\b'
}

# Own status per issue, from that issue's own row.
$ownStatus = @{}
foreach ($row in $rows) {
    $ownStatus[$row.Issue] = Test-ClosedRow $row
}

$contradictions = @()

foreach ($row in $rows) {
    # Every #NNN this row's own text mentions, other than the row's own
    # issue, together with whether the surrounding text calls it closed.
    $mentioned = [regex]::Matches($row.Verdict, '#(\d+)')
    if ($mentioned.Count -eq 0) { continue }

    $assertsClosed = Test-ClosedRow $row
    if (-not $assertsClosed) { continue }

    # A row that asserts closure and lists sibling issue numbers (e.g.
    # "children #321/#329/#330 all closed") is claiming those are closed
    # too, unless the row is only about itself.
    foreach ($m in $mentioned) {
        $other = [int]$m.Groups[1].Value
        if ($other -eq $row.Issue) { continue }
        if (-not $ownStatus.ContainsKey($other)) { continue }

        if ($ownStatus[$other] -eq $false) {
            $contradictions += "Issue #$other is called closed by #$($row.Issue)'s row (`"$($row.Verdict)`") but #$other's own row does not say closed."
        }
    }
}

if ($contradictions.Count -eq 0) {
    Write-Host "BACKLOG-SWEEP.md is internally consistent: no issue is both closed and open."
    exit 0
}

Write-Host ""
Write-Host "BACKLOG-SWEEP.md contradicts itself:" -ForegroundColor Yellow
foreach ($c in $contradictions) {
    Write-Host "  - $c" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Fix the stale row so both mentions of the issue agree, then re-run this check." -ForegroundColor Yellow
Write-Host ""

exit 1
