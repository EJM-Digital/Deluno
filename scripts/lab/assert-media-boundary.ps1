<#
.SYNOPSIS
    Refuses to let a test step run while any path Deluno could write to is
    outside the isolated E2E folder on the NAS.

.DESCRIPTION
    The owner's real library - \\nas\media\Movies and \TV - shares a
    NAS with this test run, and the NAS account Deluno runs as can write to the
    whole share. Nothing on the NAS refuses a write, so this script refuses
    instead. Run it before any step that can import, rename, move, clean or
    enable automation, and stop if it fails.

    It reads, and never changes:

      * every library's root, downloads and processor output folder, and the
        recycle bin Deluno derives beside the root (<root>.deluno-recycle-bin)
      * the global root, downloads and incomplete-downloads settings
      * every destination rule's root
      * qBittorrent's default and per-category save paths

    A path passes when it is local to the rig, or when it is on the NAS below
    rig.json's mediaBoundary.isolatedRoot. It fails when it is below a
    protected root, is anywhere else on the NAS share, or uses a drive letter
    mapped to the share: services cannot rely on a mapping, and a mapped letter
    hides which folder it really is.

    Paths are compared by their own stored shape, backslashes and case folded,
    and only on a separator boundary, so \\nas\media\Movies-old is not
    mistaken for a child of \Movies.

    A library whose root *is* the isolated root fails too: its recycle bin
    would be a sibling of the isolated folder, which is the share root itself.

.EXAMPLE
    ./scripts/lab/assert-media-boundary.ps1
#>
[CmdletBinding()]
param(
    [string] $DelunoUrl,
    [string] $DelunoUser,
    [string] $DelunoPassword,
    [string] $QbittorrentUrl,

    # Only for proving the guard itself refuses; normally leave it.
    [string] $RigPath
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Get-Rig.ps1')
$rig = if ($RigPath) { Get-Rig -Path $RigPath } else { Get-Rig }
if (-not $DelunoUrl) { $DelunoUrl = $rig.deluno.url }
if (-not $DelunoUser) { $DelunoUser = $rig.deluno.userName }
if (-not $DelunoPassword) { $DelunoPassword = $rig.deluno.password }
if (-not $QbittorrentUrl) { $QbittorrentUrl = $rig.services.qbittorrent }

$boundary = $rig.mediaBoundary
if (-not $boundary -or -not $boundary.isolatedRoot) {
    throw 'rig.json has no mediaBoundary.isolatedRoot; refusing to judge paths without one.'
}

function ConvertTo-BoundaryShape([string] $path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    $shape = $path.Trim().Replace('/', '\').TrimEnd('\')
    # The NAS answers to its name and its address; judge both as one share.
    foreach ($alias in @($boundary.nasShareRoots)) {
        $aliasShape = ([string] $alias).Replace('/', '\').TrimEnd('\')
        $canonical = ([string] @($boundary.nasShareRoots)[0]).Replace('/', '\').TrimEnd('\')
        if ($shape.Equals($aliasShape, [StringComparison]::OrdinalIgnoreCase) -or
            $shape.StartsWith("$aliasShape\", [StringComparison]::OrdinalIgnoreCase)) {
            return $canonical + $shape.Substring($aliasShape.Length)
        }
    }
    return $shape
}

function Test-Under([string] $shape, [string] $root) {
    $rootShape = ConvertTo-BoundaryShape $root
    return $shape.Equals($rootShape, [StringComparison]::OrdinalIgnoreCase) -or
        $shape.StartsWith("$rootShape\", [StringComparison]::OrdinalIgnoreCase)
}

$isolated = ConvertTo-BoundaryShape $boundary.isolatedRoot
$findings = New-Object System.Collections.Generic.List[object]

function Test-Path-Boundary([string] $where, [string] $path, [switch] $MustBeChildOfIsolated) {
    $shape = ConvertTo-BoundaryShape $path
    if (-not $shape) { return }

    $verdict = 'ok'
    $reason = 'local to the rig'
    foreach ($letter in @($boundary.mappedDriveLetters)) {
        if ($shape.StartsWith("$letter\", [StringComparison]::OrdinalIgnoreCase) -or $shape.Equals($letter, [StringComparison]::OrdinalIgnoreCase)) {
            $verdict = 'REFUSED'; $reason = "uses $letter, a drive letter mapped to the NAS share"
        }
    }
    foreach ($protected in @($boundary.protectedRoots)) {
        if (Test-Under $shape $protected) {
            $verdict = 'REFUSED'; $reason = "is inside the protected real library $protected"
        }
    }
    if ($verdict -eq 'ok' -and $shape.StartsWith('\\')) {
        if (Test-Under $shape $isolated) {
            if ($MustBeChildOfIsolated -and $shape.Equals($isolated, [StringComparison]::OrdinalIgnoreCase)) {
                $verdict = 'REFUSED'; $reason = 'is the isolated root itself; its recycle bin would land on the share root'
            } else {
                $reason = 'inside the isolated E2E folder'
            }
        } else {
            $verdict = 'REFUSED'; $reason = 'is on a network share outside the isolated E2E folder'
        }
    }

    $findings.Add([pscustomobject]@{ Verdict = $verdict; Where = $where; Path = $path; Reason = $reason })
}

# ---- Deluno, read-only ---------------------------------------------------
$login = Invoke-RestMethod -Method Post -Uri "$DelunoUrl/api/auth/login" -ContentType 'application/json' `
    -Body (@{ username = $DelunoUser; password = $DelunoPassword } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.accessToken)" }
# Invoke-RestMethod in PowerShell 7 hands a JSON array back as one object, so a
# foreach over it saw a single "library" whose name was every name and whose
# root was every root joined by spaces - and one isolated root at the front of
# that string passed a production root hiding behind it. Unroll it.
function Get-Deluno([string] $path) { Invoke-RestMethod -Uri "$DelunoUrl$path" -Headers $headers | ForEach-Object { $_ } }

foreach ($library in @(Get-Deluno '/api/libraries')) {
    $label = "library '$($library.name)'"
    Test-Path-Boundary "$label root" $library.rootPath -MustBeChildOfIsolated
    Test-Path-Boundary "$label downloads folder" $library.downloadsPath
    Test-Path-Boundary "$label processor output" $library.processorOutputPath
    if ($library.rootPath) {
        Test-Path-Boundary "$label recycle bin" ("{0}.deluno-recycle-bin" -f ([string] $library.rootPath).TrimEnd('\', '/'))
    }
}

$settings = Get-Deluno '/api/settings'
foreach ($key in @('movieRootPath', 'seriesRootPath', 'downloadsPath', 'incompleteDownloadsPath')) {
    Test-Path-Boundary "setting $key" ([string] $settings.$key)
}

foreach ($rule in @(Get-Deluno '/api/destination-rules')) {
    Test-Path-Boundary "destination rule '$($rule.name)'" $rule.rootPath -MustBeChildOfIsolated
}

# ---- qBittorrent, read-only (LAN-whitelisted on the rig) ------------------
try {
    $preferences = Invoke-RestMethod -Uri "$QbittorrentUrl/api/v2/app/preferences" -TimeoutSec 10
    Test-Path-Boundary 'qBittorrent default save path' $preferences.save_path
    if ($preferences.temp_path_enabled) { Test-Path-Boundary 'qBittorrent incomplete path' $preferences.temp_path }
    $categories = Invoke-RestMethod -Uri "$QbittorrentUrl/api/v2/torrents/categories" -TimeoutSec 10
    foreach ($category in $categories.PSObject.Properties) {
        Test-Path-Boundary "qBittorrent category '$($category.Name)'" $category.Value.savePath
    }
} catch {
    $findings.Add([pscustomobject]@{ Verdict = 'REFUSED'; Where = 'qBittorrent'; Path = $QbittorrentUrl; Reason = "could not be read, so its save paths are unproven: $($_.Exception.Message)" })
}

$findings | Sort-Object Verdict, Where | Format-Table Verdict, Where, Path, Reason -AutoSize -Wrap | Out-String -Width 220 | Write-Output

$refused = @($findings | Where-Object { $_.Verdict -ne 'ok' })
if ($refused.Count -gt 0) {
    Write-Error "Media boundary REFUSED: $($refused.Count) path(s) could let Deluno write outside $isolated. Stop and fix them first."
    exit 1
}
Write-Output "Media boundary OK: $($findings.Count) paths checked; every NAS path is inside $isolated."
exit 0
