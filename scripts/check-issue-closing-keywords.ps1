<#
.SYNOPSIS
    Fails when a commit message would close an issue by accident.

.DESCRIPTION
    GitHub closes an issue when a merged commit message or pull-request body
    contains a closing verb next to a reference to it. The parser does not read
    negation and does not care that you are quoting, so "this does not close
    #354" closes 354, and so does quoting that sentence as an example of what
    not to write.

    That happened three times in one day here, twice after the rule had been
    written down in AGENTS.md. An advisory note did not stop it, so this is the
    mechanical version.

    Use "Refs #NNN" and put the scope in a sentence that keeps the number away
    from any of those verbs.

.PARAMETER Path
    A file containing the message to check. Defaults to the current HEAD commit
    message.

.PARAMETER Text
    The message to check, inline.
#>
param(
    [string]$Path,
    [string]$Text
)

$ErrorActionPreference = "Stop"

if (-not $Text) {
    if ($Path) {
        if (-not (Test-Path -LiteralPath $Path)) { throw "No such file: $Path" }
        $Text = Get-Content -LiteralPath $Path -Raw
    } else {
        $Text = git log -1 --pretty=%B 2>$null
    }
}

if ([string]::IsNullOrWhiteSpace($Text)) {
    Write-Host "Nothing to check."
    exit 0
}

# GitHub's own list. It allows punctuation and a link between the verb and the
# reference, so match loosely rather than assuming "verb space hash".
$verbs = "close[sd]?|fix(e[sd])?|resolve[sd]?"
$reference = "(#\d+|https://github\.com/[^/\s]+/[^/\s]+/issues/\d+)"
$pattern = "(?i)\b($verbs)\b[^\w#]{0,40}$reference"

$matches = [regex]::Matches($Text, $pattern)

if ($matches.Count -eq 0) {
    Write-Host "No accidental issue-closing keywords."
    exit 0
}

Write-Host ""
Write-Host "This message would close an issue on merge:" -ForegroundColor Yellow
foreach ($match in $matches) {
    Write-Host "  $($match.Value.Trim())" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "If that is intended, this check is not for you - close it by hand after merging." -ForegroundColor Yellow
Write-Host "If it is not, write 'Refs #NNN' and keep the number away from close/fixes/resolves." -ForegroundColor Yellow
Write-Host "GitHub does not read negation: 'does not close #123' closes 123." -ForegroundColor Yellow
Write-Host ""

exit 1
