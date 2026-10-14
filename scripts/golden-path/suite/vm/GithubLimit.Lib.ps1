<#
.SYNOPSIS
    Uses up GitHub's anonymous hourly allowance from inside the VM, and reads where it stands (golden path scenario suite, #1181).

.DESCRIPTION
    Dot-source this file. GitHub lets an address make 60 anonymous calls to api.github.com an hour. Deluno's checks that need the
    API (the tracker definitions, a component's release when its file name carries a version, a release feed that the free route
    cannot read) must name that limit in plain words, with when it lifts, and Weir must still be installed through github.com's
    release feed. To meet that case for real, the VM makes the calls itself until GitHub answers that none are left.

    The calls are anonymous GETs of one public repository's page. They are the only thing in the suite that spends the allowance,
    they stop at the first answer that says none are left, and they never exceed -MaxCalls. The /rate_limit route does not count
    against the allowance, so it is used to read the state before and after. Nothing here touches the host, only the VM's address.
    Windows PowerShell 5.1.
#>

try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# The route that spends calls: a small public repository, so a 404 or a rename can never be the reason nothing was spent.
$script:GitHubSpend = @{ Uri = 'https://api.github.com/repos/octocat/Hello-World'; UserAgent = 'Deluno-golden-suite' }

# The two numbers GitHub puts on every answer, from a successful answer or from the error a 403 raises. Null when absent.
function Get-GitHubLimitHeaders {
    param($Headers)
    $remaining = $null
    $reset = $null
    if ($Headers) {
        try { $v = $Headers['X-RateLimit-Remaining']; if ($v -match '^\d+$') { $remaining = [int]$v } } catch { }
        try { $v = $Headers['X-RateLimit-Reset']; if ($v -match '^\d+$') { $reset = [long]$v } } catch { }
    }
    [pscustomobject]@{ Remaining = $remaining; ResetEpoch = $reset }
}

# Where the allowance stands, read from /rate_limit (which is free). Remaining and ResetUtc are null when GitHub did not answer.
function Get-GitHubLimitState {
    try {
        $r = Invoke-RestMethod -Uri 'https://api.github.com/rate_limit' -UseBasicParsing -TimeoutSec 20 -Headers @{ 'User-Agent' = $script:GitHubSpend.UserAgent }
        $c = $r.resources.core
        return [pscustomobject]@{ Known = $true; Limit = [int]$c.limit; Remaining = [int]$c.remaining; ResetEpoch = [long]$c.reset; ResetUtc = [DateTimeOffset]::FromUnixTimeSeconds([long]$c.reset).UtcDateTime; Error = '' }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Limit = 0; Remaining = $null; ResetEpoch = 0; ResetUtc = $null; Error = $_.Exception.Message }
    }
}

# Spends the allowance with anonymous GETs, one after the other, until GitHub says none are left or -MaxCalls is reached.
# Returns Reachable ($true when the answer "none left" was seen or the state already read 0), Calls made, the reset instant and why.
function Use-GitHubLimitUp {
    param([int] $MaxCalls = 70)
    $before = Get-GitHubLimitState
    if (-not $before.Known) {
        return [pscustomobject]@{ Reachable = $false; Calls = 0; RemainingAtStart = $null; Remaining = $null; ResetEpoch = 0; ResetUtc = $null; Reason = "GitHub's rate_limit route did not answer from the VM ($($before.Error))." }
    }
    if ($before.Limit -gt $MaxCalls) {
        return [pscustomobject]@{ Reachable = $false; Calls = 0; RemainingAtStart = $before.Remaining; Remaining = $before.Remaining; ResetEpoch = $before.ResetEpoch; ResetUtc = $before.ResetUtc; Reason = "GitHub allows $($before.Limit) calls an hour from this address, more than the $MaxCalls this suite will spend, so the limit cannot be used up here." }
    }
    $calls = 0
    $remaining = $before.Remaining
    $resetEpoch = $before.ResetEpoch
    while ($remaining -gt 0 -and $calls -lt $MaxCalls) {
        $calls++
        $headers = $null
        try {
            $resp = Invoke-WebRequest -Uri $script:GitHubSpend.Uri -UseBasicParsing -TimeoutSec 20 -Headers @{ 'User-Agent' = $script:GitHubSpend.UserAgent; Accept = 'application/vnd.github+json' }
            $headers = $resp.Headers
        }
        catch {
            # A 403 that says the allowance is gone is the answer this loop wants; any other error still carries the headers when GitHub sent them.
            if ($_.Exception.Response) { try { $headers = $_.Exception.Response.Headers } catch { } }
            if (-not $headers) { return [pscustomobject]@{ Reachable = $false; Calls = $calls; RemainingAtStart = $before.Remaining; Remaining = $remaining; ResetEpoch = $resetEpoch; ResetUtc = $null; Reason = "A call to GitHub failed after $calls call(s): $($_.Exception.Message)" } }
        }
        $h = Get-GitHubLimitHeaders -Headers $headers
        if ($null -ne $h.Remaining) { $remaining = $h.Remaining } else { $remaining-- }
        if ($h.ResetEpoch) { $resetEpoch = $h.ResetEpoch }
    }
    $after = Get-GitHubLimitState
    $left = if ($after.Known) { $after.Remaining } else { $remaining }
    $reset = if ($after.Known -and $after.ResetEpoch) { $after.ResetEpoch } else { $resetEpoch }
    $used = ($left -eq 0)
    $resetUtc = if ($reset) { [DateTimeOffset]::FromUnixTimeSeconds([long]$reset).UtcDateTime } else { $null }
    [pscustomobject]@{
        Reachable = $used; Calls = $calls; RemainingAtStart = $before.Remaining; Remaining = $left; ResetEpoch = $reset; ResetUtc = $resetUtc
        Reason = $(if ($used) { '' } else { "After $calls call(s) GitHub still reported $left left; the limit could not be used up from this address." })
    }
}

# The minute Deluno writes for a reset ("2026-10-09 00:35 UTC"): the instant rounded up to the minute (GitHubRateLimitException.UtcStamp).
function Get-GitHubLimitStamp {
    param([Parameter(Mandatory)] [long] $ResetEpoch)
    [DateTimeOffset]::FromUnixTimeSeconds($ResetEpoch).UtcDateTime.AddSeconds(59).ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture) + ' UTC'
}

# The reset as a viewer's own clock reads it: the hour and the minute in this machine's zone, in both the 12 and 24 hour forms, so a
# screen's "5:35 PM" and a log's "17:35" both match. The web app writes the time with toLocaleTimeString (reset-times.ts).
function Get-GitHubLimitLocalForms {
    param([Parameter(Mandatory)] [long] $ResetEpoch, [int] $PlusMinutes = 0)
    $utc = [DateTimeOffset]::FromUnixTimeSeconds($ResetEpoch).UtcDateTime.AddSeconds(59).AddMinutes($PlusMinutes)
    $minute = [DateTime]::SpecifyKind($utc.AddSeconds(-$utc.Second), [DateTimeKind]::Utc)
    $local = [TimeZoneInfo]::ConvertTimeFromUtc($minute, [TimeZoneInfo]::Local)
    $h12 = if ($local.Hour % 12 -eq 0) { 12 } else { $local.Hour % 12 }
    @(('{0}:{1:d2}' -f $h12, $local.Minute), ('{0:d2}:{1:d2}' -f $local.Hour, $local.Minute))
}

# True when the text names the reset in this machine's local time (the minute Deluno wrote, or the minute either side of it).
function Test-TextHasLocalReset {
    param([Parameter(Mandatory)] [string] $Text, [Parameter(Mandatory)] [long] $ResetEpoch)
    foreach ($delta in 0, -1, 1) {
        foreach ($form in (Get-GitHubLimitLocalForms -ResetEpoch $ResetEpoch -PlusMinutes $delta)) {
            if ($Text -match ('(?<![0-9:])' + [regex]::Escape($form) + '(?![0-9])')) { return $true }
        }
    }
    $false
}
