<#
.SYNOPSIS
    Fails when a repository PowerShell script uses something Windows PowerShell 5.1 does not have.

.DESCRIPTION
    Every .ps1 here has to run on both editions, because run-powershell.mjs
    falls back from pwsh to powershell and the machine Deluno is developed on
    has only the latter.

    This is the third time that has bitten:

      #445  run-ga-regression.ps1 could not complete unattended, and nothing
            said so - it just never reached step two.
      -     run-powershell.mjs was written because three scripts died with
            "'pwsh' is not recognized". Its header describes this exact class.
      #461  collect-soak-snapshot.ps1 used -SkipHttpErrorCheck, which is
            PowerShell 7 only. On 5.1 it threw before the request was made, the
            catch recorded it as "the endpoint is down", and the script exited
            0. The soak collector is a prerequisite of a GA gate and it had
            never once taken a reading on the only machine that could take one.

    Each was found by a person noticing. A note in a document did not stop the
    second; this is meant to stop the fourth.

    Only distinctive tokens are checked - parameter names and type names that
    cannot plausibly mean anything else - so this stays quiet rather than
    becoming something people learn to ignore. Ternaries and pipeline chain
    operators are deliberately not checked: they cannot be told apart from
    ordinary text without parsing, and a noisy check is a disabled check.

    A line that genuinely needs a 7-only construct can say so:

        $x = Get-Thing -AsHashtable   # ps7-ok: only ever run from the workflow
#>
[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = 'Stop'

# Not a param default. See the ScriptRootInParamDefault rule below: this very
# script hit it, which is how the rule got written.
if (-not $Root) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Root = (Resolve-Path (Join-Path $here '..')).Path
}

# Token, and what to do instead. The advice matters more than the detection:
# somebody hitting this needs the alternative, not a scolding.
$forbidden = @(
    @{ Pattern = '-SkipHttpErrorCheck';  Advice = 'catch the error and read $_.Exception.Response.StatusCode' },
    @{ Pattern = '-AsHashtable';         Advice = 'ConvertFrom-Json returns a PSCustomObject on 5.1; read properties, or build a hashtable yourself' },
    @{ Pattern = '-SkipCertificateCheck'; Advice = 'set [System.Net.ServicePointManager]::ServerCertificateValidationCallback for the call' },
    @{ Pattern = '-ResponseHeadersVariable'; Advice = 'read .Headers off the response object' },
    @{ Pattern = '-AllowInsecureRedirect'; Advice = 'follow the redirect explicitly' },
    @{ Pattern = 'ForEach-Object\s+-Parallel'; Advice = 'use jobs or a runspace pool, or just do it sequentially' },
    @{ Pattern = '-ThrottleLimit';       Advice = 'only exists alongside -Parallel, which 5.1 does not have' },
    @{ Pattern = 'Microsoft\.PowerShell\.Commands\.HttpResponseException'; Advice = 'the type does not exist on 5.1 and a catch naming it fails to resolve; use an untyped catch' },
    @{ Pattern = '\$Is(Windows|Linux|MacOS|CoreCLR)\b'; Advice = 'these are $null on 5.1, so a check on them silently reads as false; use $PSVersionTable or [System.Environment]::OSVersion' },
    @{ Pattern = '\?\?=?'; Advice = 'null-coalescing does not parse on 5.1; use an if or a fallback expression' }
)

$scripts = Get-ChildItem -Path $Root -Recurse -Filter *.ps1 -File |
    Where-Object { $_.FullName -notmatch '\\(node_modules|artifacts|bin|obj|\.git)\\' }

$findings = @()

# A rule that needs two lines to see, so it does not fit the token table above.
#
# On Windows PowerShell 5.1, a script with [CmdletBinding()] launched with a
# relative -File path - exactly how run-powershell.mjs and ci-check launch these
# - sees $PSScriptRoot as EMPTY while parameter defaults are being evaluated.
# Without [CmdletBinding()] it is populated. Two scripts here had it, and both
# were dead on arrival in a way that blamed something else:
# generate-trash-guide-source-inventory.ps1 demanded an unrelated parameter, and
# this very file died inside Join-Path.
#
# Compute the path in the body instead, with a $MyInvocation fallback.
function Get-ScriptRootInParamDefault {
    param([string[]] $Lines)

    $hasCmdletBinding = $false
    $inParamBlock = $false
    $depth = 0
    $hits = @()

    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $Lines[$index]
        if ($line -match '^\s*\[CmdletBinding\b') { $hasCmdletBinding = $true; continue }

        if (-not $inParamBlock -and $line -match '^\s*param\s*\(') {
            if (-not $hasCmdletBinding) { break }
            $inParamBlock = $true
            $depth = 0
        }

        if ($inParamBlock) {
            $depth += ([regex]::Matches($line, '\(')).Count
            $depth -= ([regex]::Matches($line, '\)')).Count
            if ($line -match '\$PSScriptRoot') {
                $hits += [pscustomobject]@{ Line = $index + 1; Text = $line.Trim() }
            }
            if ($depth -le 0) { break }
        }
    }

    return $hits
}

foreach ($script in $scripts) {
    $relative = $script.FullName.Substring($Root.Length).TrimStart('\', '/')
    $lineNumber = 0
    $inBlockComment = $false
    foreach ($line in [System.IO.File]::ReadAllLines($script.FullName)) {
        $lineNumber++
        $trimmed = $line.Trim()

        # This file names every token it forbids, and a waiver says so on the line.
        if ($script.Name -eq 'check-powershell-portability.ps1') { continue }
        if ($line -match '#\s*ps7-ok\b') { continue }

        # Prose is not code. A comment explaining why a construct is avoided
        # would otherwise fail the check for naming the thing it warns about,
        # which is the sort of result that gets a check switched off.
        if ($inBlockComment) {
            if ($trimmed -match '#>') { $inBlockComment = $false }
            continue
        }
        if ($trimmed -match '^<#') {
            if ($trimmed -notmatch '#>') { $inBlockComment = $true }
            continue
        }
        if ($trimmed.StartsWith('#')) { continue }

        foreach ($rule in $forbidden) {
            if ($line -match $rule.Pattern) {
                $findings += [pscustomobject]@{
                    File = $relative
                    Line = $lineNumber
                    Found = $Matches[0]
                    Advice = $rule.Advice
                    Text = $line.Trim()
                }
            }
        }
    }
}

foreach ($script in $scripts) {
    if ($script.Name -eq 'check-powershell-portability.ps1') { continue }
    $relative = $script.FullName.Substring($Root.Length).TrimStart('\', '/')
    foreach ($hit in Get-ScriptRootInParamDefault -Lines ([System.IO.File]::ReadAllLines($script.FullName))) {
        if ($hit.Text -match '#\s*ps7-ok\b') { continue }
        $findings += [pscustomobject]@{
            File = $relative
            Line = $hit.Line
            Found = '$PSScriptRoot in a [CmdletBinding()] param default'
            Advice = 'empty on 5.1 when the script is launched with a relative -File path; compute it in the body with a $MyInvocation fallback'
            Text = $hit.Text
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host "  OK  every .ps1 avoids PowerShell 7 only constructs ($($scripts.Count) scripts)"
    exit 0
}

Write-Host "PowerShell 7 only constructs, in scripts that must run on Windows PowerShell 5.1:"
Write-Host ""
foreach ($finding in $findings) {
    Write-Host ("  {0}:{1}" -f $finding.File, $finding.Line)
    Write-Host ("    {0}" -f $finding.Text)
    Write-Host ("    {0} -> {1}" -f $finding.Found, $finding.Advice)
    Write-Host ""
}
Write-Host "If one of these is genuinely fine, add '# ps7-ok: <why>' to the line."
exit 1
