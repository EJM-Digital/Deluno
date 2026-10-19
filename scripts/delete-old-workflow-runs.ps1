<#
.SYNOPSIS
    Deletes old GitHub Actions runs for Deluno, keeping the newest ones (James, 3 Oct 2026: "all old ones cleaned and
    deleted").

.DESCRIPTION
    Deleting a run deletes its logs and artifacts for good, so James runs this himself. It lists what it would delete
    first and asks before deleting anything. By default it keeps the newest 20 runs and every run that is still in
    progress; -Keep changes how many are kept, and -Before keeps everything on or after a date instead.

.EXAMPLE
    ./scripts/delete-old-workflow-runs.ps1                 # keep the newest 20, delete the rest
    ./scripts/delete-old-workflow-runs.ps1 -Keep 0         # delete every finished run
    ./scripts/delete-old-workflow-runs.ps1 -Before 2026-10-04
#>
param(
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [int]$Keep = 20,
    [datetime]$Before
)
# One value for the repository (repository.json); the Actions environment's own name wins when set.
if (-not $Repo) { $Repo = (Get-Content -Raw (Join-Path $PSScriptRoot '..\repository.json') | ConvertFrom-Json).deluno }

$ErrorActionPreference = 'Stop'

Write-Host "Reading every Actions run in $Repo ..."
$runs = gh api "repos/$Repo/actions/runs?per_page=100" --paginate -q '.workflow_runs[] | [.id, .status, .created_at, .name] | @tsv' |
    ForEach-Object { $f = $_ -split "`t"; [pscustomobject]@{ Id = $f[0]; Status = $f[1]; Created = [datetime]$f[2]; Name = $f[3] } } |
    Sort-Object Created -Descending

$finished = $runs | Where-Object Status -eq 'completed'
$doomed = if ($PSBoundParameters.ContainsKey('Before')) { $finished | Where-Object Created -lt $Before } else { $finished | Select-Object -Skip $Keep }

Write-Host "$($runs.Count) runs in all; $($doomed.Count) would be deleted:"
$doomed | Group-Object Name | ForEach-Object { Write-Host ("  {0,-20} {1}" -f $_.Name, $_.Count) }
if ($doomed.Count -eq 0) { return }

if ((Read-Host "Delete these $($doomed.Count) runs and their logs for good? Type DELETE") -cne 'DELETE') {
    Write-Host 'Nothing deleted.'
    return
}

# One call per run, and the hourly GitHub allowance (5,000) is shared by every session, script and agent on this
# login. Keep a reserve so the other sessions can still push and release: check what is left every 50 deletions,
# and when it falls to the reserve, wait for the hourly reset instead of running it dry (3 Oct 2026, when a bulk
# clean-up emptied the allowance and blocked Weir's release).
$reserve = 1500
$i = 0
foreach ($run in $doomed) {
    if ($i % 50 -eq 0) {
        # Read the budget from a real call's headers: on 3 Oct 2026 `gh api rate_limit` kept reporting 5000 left
        # while real calls were refused with remaining 0.
        $headers = gh api -i user 2>$null
        $remaining = [int](($headers | Select-String -Pattern '^x-ratelimit-remaining:\s*(\d+)' | Select-Object -First 1).Matches.Groups[1].Value)
        $reset = [long](($headers | Select-String -Pattern '^x-ratelimit-reset:\s*(\d+)' | Select-Object -First 1).Matches.Groups[1].Value)
        if (-not $reset) { $remaining = 0; $reset = [DateTimeOffset]::UtcNow.AddMinutes(5).ToUnixTimeSeconds() }
        if ($remaining -le $reserve) {
            $resetAt = [DateTimeOffset]::FromUnixTimeSeconds($reset).LocalDateTime
            Write-Host "  $remaining GitHub calls left this hour; pausing until $($resetAt.ToString('HH:mm')) so other work is not blocked."
            Start-Sleep -Seconds ([Math]::Max(30, [int]($resetAt - (Get-Date)).TotalSeconds + 30))
        }
        if ($i -gt 0) { Write-Host "  $i of $($doomed.Count)" }
    }
    $deleted = $false
    foreach ($wait in 0, 5, 30, 120) {
        if ($wait) { Start-Sleep -Seconds $wait }
        gh api -X DELETE "repos/$Repo/actions/runs/$($run.Id)" --silent 2>$null
        if ($LASTEXITCODE -eq 0) { $deleted = $true; break }
    }
    if (-not $deleted) { Write-Host "  Could not delete run $($run.Id) after retrying; it is left as it was." }
    $i++
    # A second apart: a burst of writes trips GitHub's secondary limit, which refuses calls even while rate_limit
    # still shows thousands left (3 Oct 2026).
    Start-Sleep -Seconds 1
}
Write-Host "Done: $i runs processed."
