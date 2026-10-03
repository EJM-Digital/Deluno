<#
.SYNOPSIS
    Deletes old GitHub Actions runs for Deluno, keeping the newest ones (the owner, 3 Oct 2026: "all old ones cleaned and
    deleted").

.DESCRIPTION
    Deleting a run deletes its logs and artifacts for good, so the owner runs this himself. It lists what it would delete
    first and asks before deleting anything. By default it keeps the newest 20 runs and every run that is still in
    progress; -Keep changes how many are kept, and -Before keeps everything on or after a date instead.

.EXAMPLE
    ./scripts/delete-old-workflow-runs.ps1                 # keep the newest 20, delete the rest
    ./scripts/delete-old-workflow-runs.ps1 -Keep 0         # delete every finished run
    ./scripts/delete-old-workflow-runs.ps1 -Before 2026-10-04
#>
param(
    [string]$Repo = 'jampat000/Deluno',
    [int]$Keep = 20,
    [datetime]$Before
)
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

$i = 0
foreach ($run in $doomed) {
    gh api -X DELETE "repos/$Repo/actions/runs/$($run.Id)" --silent
    $i++
    if ($i % 50 -eq 0) { Write-Host "  $i of $($doomed.Count)" }
}
Write-Host "Deleted $i runs."
