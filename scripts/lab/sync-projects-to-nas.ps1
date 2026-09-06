<#
.SYNOPSIS
    Mirrors C:\Projects to a NAS share, and reports what git has not got.

.DESCRIPTION
    Two different jobs are often confused for one another, so this does both
    and keeps them apart.

    GitHub holds the source. It does not hold the 250 MB of vendor installers
    and fixture media that provisioning needs, it does not hold anything
    uncommitted, and it does not hold the four directories under C:\Projects
    that are not repositories at all. A repository being "pushed" is not the
    same as the machine being backed up.

    So: robocopy mirrors the whole tree to the share, and separately every git
    repository is checked for work that only exists on this disk. The second
    half matters more than the first - a backup of an uncommitted change is
    better than nothing, but knowing it is uncommitted is better still.

    Mirror mode deletes files on the NAS that are gone from C:\Projects. That is
    what "mirror" means and it is the right default for a working copy, but it
    means the share is a mirror rather than an archive: it will not save you
    from deleting something and not noticing for a week. Use -Archive for a
    dated copy that never deletes.

.PARAMETER Destination
    The share, e.g. \\nas\Backups\Projects.

.PARAMETER Archive
    Copy into a dated subdirectory and never delete. Slower and bigger; use it
    before anything drastic.

.PARAMETER ReportOnly
    Say what would move, and what git has not got, without copying.

.EXAMPLE
    ./scripts/lab/sync-projects-to-nas.ps1 -Destination \\nas\Backups\Projects -ReportOnly
    ./scripts/lab/sync-projects-to-nas.ps1 -Destination \\nas\Backups\Projects
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [string] $Source = 'C:\Projects',
    [switch] $Archive,
    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "No such source: $Source" }

# ------------------------------------------------------- what git has not got

Write-Host "`nWhat exists only on this disk" -ForegroundColor Cyan

$atRisk = 0
foreach ($dir in Get-ChildItem $Source -Directory) {
    if (-not (Test-Path (Join-Path $dir.FullName '.git'))) {
        $size = [math]::Round((Get-ChildItem $dir.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum / 1MB, 1)
        Write-Host ("  {0,-26} not a repository, {1} MB - the NAS is its only copy" -f $dir.Name, $size)
        $atRisk++
        continue
    }

    Push-Location $dir.FullName
    try {
        # git writes to stderr for perfectly ordinary states - a detached HEAD,
        # a branch with no upstream - and with ErrorActionPreference Stop that
        # aborts the whole audit on the first odd repository. One of these has a
        # detached HEAD, and that is exactly a repository worth reporting rather
        # than one worth crashing on.
        # git.exe, not git: PowerShell resolves command names case-insensitively
        # and prefers functions to executables, so a helper called Git calls
        # itself. Every repository then reports whatever the empty result looks
        # like - here, "detached HEAD" for all of them, which is the kind of
        # uniform wrong answer that is easy to believe.
        function Invoke-GitQuietly { param([string[]] $Arguments)
            $old = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { return @(& git.exe @Arguments 2>$null) } catch { return @() } finally { $ErrorActionPreference = $old }
        }

        $dirty  = (Invoke-GitQuietly @('status', '--porcelain')).Count
        $branch = (Invoke-GitQuietly @('rev-parse', '--abbrev-ref', 'HEAD')) | Select-Object -First 1
        $stashes = (Invoke-GitQuietly @('stash', 'list')).Count
        $upstream = (Invoke-GitQuietly @('rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}')) | Select-Object -First 1
        $unpushed = if ($upstream) { (Invoke-GitQuietly @('rev-list', '--count', '@{u}..HEAD')) | Select-Object -First 1 } else { $null }

        $problems = @()
        if ($dirty -gt 0) { $problems += "$dirty uncommitted" }
        if (-not $branch -or $branch -eq 'HEAD') { $problems += 'detached HEAD' }
        elseif (-not $upstream) { $problems += 'no upstream branch' }
        elseif ([int]$unpushed -gt 0) { $problems += "$unpushed unpushed" }
        if ($stashes -gt 0) { $problems += "$stashes stashed" }

        if ($problems.Count -gt 0) {
            Write-Host ("  {0,-26} {1} on {2}" -f $dir.Name, ($problems -join ', '), $branch) -ForegroundColor Yellow
            $atRisk++
        } else {
            Write-Host ("  {0,-26} clean and pushed" -f $dir.Name)
        }
    } finally { Pop-Location }
}

if ($atRisk -eq 0) {
    Write-Host "  Everything is in a repository and pushed." -ForegroundColor Green
} else {
    Write-Host "  $atRisk of these are not on GitHub. The NAS copy is what stands behind them." -ForegroundColor Yellow
}

# ------------------------------------------------------- the mirror

$target = if ($Archive) { Join-Path $Destination (Get-Date -Format 'yyyy-MM-dd-HHmm') } else { $Destination }

Write-Host "`n$(if ($Archive) { 'Archiving' } else { 'Mirroring' }) $Source -> $target" -ForegroundColor Cyan

if (-not (Test-Path $Destination)) {
    throw "Cannot reach $Destination. Check the share is mounted and this account can write to it."
}

# /MIR deletes on the far side; /E does not. Everything else is the same either
# way: restartable copies, timestamps kept, and a handful of retries rather than
# the default million, which turns one locked file into an overnight hang.
$switches = @(
    $(if ($Archive) { '/E' } else { '/MIR' })
    '/Z'                 # restartable, so a dropped share does not start over
    '/COPY:DAT'          # data, attributes, timestamps - not ACLs, which mean nothing on the share
    '/DCOPY:DAT'
    '/R:2'; '/W:5'       # two retries, five seconds - fail fast and say so
    '/MT:16'
    '/NP'; '/NDL'
    '/XJ'                # do not follow junctions, or node_modules links loop
)

# Excluded by name, not by .gitignore: robocopy cannot read one, and these are
# the trees that are large, machine-specific and rebuilt by a single command.
$excludeDirs = @('node_modules', '.venv', '__pycache__', 'bin', 'obj', '.vs', 'dist', '.next')
$switches += '/XD'
$switches += $excludeDirs

if ($ReportOnly) { $switches += '/L' }

$log = Join-Path $env:TEMP "projects-nas-sync-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
$switches += "/LOG+:$log"; $switches += '/TEE'

& robocopy.exe $Source $target @switches | Select-Object -Last 12

# Robocopy's exit codes are a bit-field: under 8 is success of some flavour,
# 8 and above is a real failure. Treating any non-zero as failure is the classic
# way to make a working backup look broken.
$code = $LASTEXITCODE
Write-Host ""
if ($code -ge 8) {
    Write-Host "robocopy failed with $code. Log: $log" -ForegroundColor Red
    exit 1
}
Write-Host ("robocopy exit {0} - {1}" -f $code, $(switch ($code) {
    0 { 'nothing needed copying' }
    1 { 'files copied' }
    2 { 'extra files on the destination were removed' }
    3 { 'files copied, and extras removed' }
    default { 'copied, with mismatches or skips - read the log' }
}))
Write-Host "Log: $log"
