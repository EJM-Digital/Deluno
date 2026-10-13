<#
    Helpers the scenario groups share (golden path scenario suite). Dot-sourced by Run-Suite.ps1 after the libraries.
#>

$script:AllFilms = New-Object System.Collections.ArrayList
$script:Kinds = @{ torrent = @('qbittorrent', 'transmission', 'deluge'); usenet = @('sabnzbd', 'nzbget') }

function Get-AllKinds { @('qbittorrent', 'transmission', 'deluge', 'sabnzbd', 'nzbget') }
function Test-IsTorrent { param([string] $Kind) $Kind -in 'qbittorrent', 'transmission', 'deluge' }

# Makes a film that fits the client kind (a torrent over a hard link, or a Usenet release) and remembers it for the Activity check.
function New-FilmFor {
    param([Parameter(Mandatory)] [string] $Kind, [string] $Layout = 'folder', [string] $Variant = 'complete', [string] $Size = 'small', [switch] $Extra, [string] $Label = 'film', [switch] $Slow, [switch] $HangLast, [switch] $HangAll)
    if (Test-IsTorrent $Kind) { $f = New-TorrentFilm -Client $Kind -Layout $Layout -Variant $Variant -Size $Size -Extra:$Extra -Label $Label }
    else { $f = New-UsenetFilm -Size $Size -Label $Label -Slow:$Slow -HangLast:$HangLast -HangAll:$HangAll }
    [void]$script:AllFilms.Add($f)
    $f
}

# The newest hand-off for a release, or $null.
function Get-FilmHandoff {
    param([Parameter(Mandatory)] $Film)
    Find-Handoffs -Name $Film.Release | Select-Object -First 1
}

# Waits for the hand-off of a film to exist and then to settle (completed with an import queued or done).
function Wait-HandoffSettled {
    param([Parameter(Mandatory)] $Film, [int] $TimeoutSec = 240)
    Wait-Until -TimeoutSec $TimeoutSec -IntervalSec 1 -Condition {
        $h = Get-FilmHandoff -Film $Film
        if ($h -and $h.status -eq 'completed' -and $h.importJobId) { $h } else { $null }
    }
}

# The whole path of one film: sent -> client finished -> handed to Weir -> cleaned -> imported. Returns the timings and what was found.
function Wait-FilmImported {
    param([Parameter(Mandatory)] $Film, [int] $FinishedTimeoutSec = 240, [int] $ImportTimeoutSec = 300)
    $t0 = $Film.SentUtc
    $fin = Wait-FilmFinished -Film $Film -TimeoutSec $FinishedTimeoutSec
    $finSec = [math]::Round(([DateTime]::UtcNow - $t0).TotalSeconds, 1)
    $handed = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { Get-FilmHandoff -Film $Film }
    $handSec = [math]::Round(([DateTime]::UtcNow - $t0).TotalSeconds, 1)
    $settled = Wait-HandoffSettled -Film $Film -TimeoutSec $ImportTimeoutSec
    $lib = Wait-Until -TimeoutSec 120 -IntervalSec 1 -Condition { Find-Imported -Title $Film.Title }
    $impSec = [math]::Round(([DateTime]::UtcNow - $t0).TotalSeconds, 1)
    [pscustomobject]@{
        Finished = $fin.Ok; FinishedSec = $finSec; HandedOff = $handed.Ok; HandedSec = $handSec; Settled = $settled.Ok
        Imported = $lib.Ok; ImportedSec = $impSec; Handoff = $(if ($settled.Ok) { $settled.Value } else { Get-FilmHandoff -Film $Film }); File = $lib.Value
    }
}

# Where a film's finished download sits in the client's folder (the job folder, the release folder, or the single file).
function Get-FilmClientPath {
    param([Parameter(Mandatory)] $Film)
    $row = Find-QueueRow -Name $Film.Release
    $path = $null
    if ($row -and $row.sourcePath) { $path = $row.sourcePath }
    if (-not $path) {
        $h = Get-FilmHandoff -Film $Film
        if ($h) { $path = $h.sourcePath }
    }
    $path
}

# Anything of the film still under the clients' completed folders (by its title).
function Get-FilmLeftovers {
    param([Parameter(Mandatory)] $Film)
    @(Get-ChildItem -Path 'C:\Downloads\Completed' -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match [regex]::Escape($Film.Title) })
}

# Deluno's removal of a download in a client, by the reason a button on the Remove dialog sends: failed, unwanted, forget, reclaim.
function Remove-FilmDownload {
    param([Parameter(Mandatory)] $Film, [Parameter(Mandatory)] [string] $Reason, [string] $QueueItemId)
    $h = Get-FilmHandoff -Film $Film
    $id = if ($QueueItemId) { $QueueItemId } elseif ($h) { $h.queueItemId } else { (Find-QueueRow -Name $Film.Release).id }
    $clientId = if ($h) { $h.clientId } else { $Film.ClientId }
    Try-Deluno -Method POST -Path "/api/download-clients/$clientId/queue/$([uri]::EscapeDataString($id))/remove" -Body @{ reason = $Reason }
}

# Puts a film's data back where a client that deleted it on removal expects it, then presses Deluno's own Re-check.
function Restore-FilmData {
    param([Parameter(Mandatory)] $Film)
    if ($Film.Protocol -ne 'torrent') { return }
    $src = Join-Path (Get-SuiteRoot) "films\$($Film.Release)"
    $dest = Get-ClientDataFolder -Kind $Film.ClientKind
    $base = if ($Film.Layout -eq 'folder') { Join-Path $src $Film.Release } else { $src }
    Get-ChildItem -LiteralPath $base -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length + 1)
        $to = if ($Film.Layout -eq 'folder') { Join-Path (Join-Path $dest $Film.Release) $rel } else { Join-Path $dest $rel }
        if (-not (Test-Path -LiteralPath $to)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
            New-Item -ItemType HardLink -Path $to -Target $_.FullName | Out-Null
        }
    }
    # The data is where the client keeps an unfinished torrent now, and nothing has told the client to check it yet.
    $Film | Add-Member -NotePropertyName DataFolder -NotePropertyValue $dest -Force
    $Film | Add-Member -NotePropertyName SettleKey -NotePropertyValue $null -Force
}

function Start-AutomationPause { Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $false } | Out-Null }
function Stop-AutomationPause { Invoke-Deluno -Method PUT -Path '/api/settings/automation' -Body @{ isEnabled = $true } | Out-Null }

# One reading of what the three screens and the tray say about Weir, for the outage scenarios.
function Get-WeirAnswering {
    $conns = @(Invoke-Deluno -Path '/api/integrations/processors/connections')
    $c = $conns | Where-Object { $_.name -match 'weir' } | Select-Object -First 1
    $c
}
