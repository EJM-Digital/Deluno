<#
    The scenarios of the suite, by group and in run order (docs/golden-path.md, "The scenario suite"). One list for both sides:
    the host uses it to know what must have run (a scenario that never reported is an error, not a skip), and the runner in
    the VM uses it to know which group function to call.

    Client names are the kinds Deluno calls them: qbittorrent, transmission and deluge (torrent); sabnzbd and nzbget (Usenet).
#>

$script:TorrentKinds = @('qbittorrent', 'transmission', 'deluge')
$script:UsenetKinds = @('sabnzbd', 'nzbget')
$script:AllClientKinds = $script:TorrentKinds + $script:UsenetKinds

function Get-ScenarioPlan {
    $all = $script:AllClientKinds
    $groups = @(
        @{ Name = 'install';  Function = 'Invoke-GroupInstall';  Ids = @('install-first-run') }
        @{ Name = 'films';    Function = 'Invoke-GroupFilms';    Ids = @($all | ForEach-Object { "film.$_" }) + @('film.extra-left-out') }
        @{ Name = 'removal';  Function = 'Invoke-GroupRemoval';  Ids = @($all | ForEach-Object { "remove-copy.$_.folder" }) + @($script:TorrentKinds | ForEach-Object { "remove-copy.$_.file" }) }
        @{ Name = 'resend';   Function = 'Invoke-GroupResend';   Ids = @(foreach ($t in 'before', 'during', 'after') { foreach ($k in $all) { "resend.$t.$k" } }) }
        @{ Name = 'stuck';    Function = 'Invoke-GroupStuck';    Ids = @($all | ForEach-Object { "stuck.zero.$_" }) + @($all | ForEach-Object { "stuck.short.$_" }) + @($script:TorrentKinds | ForEach-Object { "stuck.peers.$_" }) + @($script:UsenetKinds | ForEach-Object { "stuck.slow.$_" }) }
        @{ Name = 'keepseeding'; Function = 'Invoke-GroupKeepSeeding'; Ids = @($script:TorrentKinds | ForEach-Object { "keepseeding.moved.$_" }) + @($script:TorrentKinds | ForEach-Object { "keepseeding.public-removed.$_" }) }
        @{ Name = 'outage';   Function = 'Invoke-GroupOutage';   Ids = @('outage.idle', 'outage.inflight') }
        @{ Name = 'tryagain'; Function = 'Invoke-GroupTryAgain'; Ids = @($all | ForEach-Object { "tryagain.$_" }) }
        @{ Name = 'vanish';   Function = 'Invoke-GroupVanish';   Ids = @(foreach ($t in 'before', 'waiting') { foreach ($k in $all) { "vanish.$t.$k" } }) }
        @{ Name = 'problems'; Function = 'Invoke-GroupProblems'; Ids = @('problems.dismiss-everywhere', 'activity.one-story') }
        @{ Name = 'update';   Function = $null; Host = $true; Ids = @('update.over-running-previous') }
    )
    $groups | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Function = $_.Function; Host = [bool]$_.Host; Ids = @($_.Ids) } }
}

# ---------------------------------------------------------------- choosing single scenarios (-Scenarios)

# Turns what a person typed after -Scenarios (ids, comma separated or as separate values; * and ? stand for any text, so
# "resend.*.sabnzbd" works) into the plan's ids, in plan order, with none twice. An id that matches nothing is an error that names
# the nearest ids, so a typo never quietly runs nothing.
function Resolve-ScenarioSelection {
    param([string[]] $Scenarios = @(), [object[]] $Plan = @(Get-ScenarioPlan))
    $wanted = @($Scenarios | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($wanted.Count -eq 0) { return @() }
    $all = @($Plan | ForEach-Object { $_.Ids })
    $picked = New-Object System.Collections.Generic.HashSet[string]
    $unknown = New-Object System.Collections.ArrayList
    foreach ($w in $wanted) {
        $hits = @($all | Where-Object { $_ -like $w })
        if ($hits.Count -eq 0) { [void]$unknown.Add($w) }
        foreach ($h in $hits) { [void]$picked.Add($h) }
    }
    if ($unknown.Count -gt 0) {
        $near = @($unknown | ForEach-Object { $stem = ($_ -split '\.')[0]; $all | Where-Object { $_ -like "$stem*" } } | Select-Object -Unique | Select-Object -First 12)
        throw ("Unknown scenario(s): {0}. {1}" -f ($unknown -join ', '), $(if ($near.Count) { 'Did you mean: ' + ($near -join ', ') + '?' } else { 'Run with -ListOnly to see every id.' }))
    }
    @($all | Where-Object { $picked.Contains($_) })
}

# What a run covers, from -Groups and -Scenarios: the chosen ids, whether it is the full run, whether any chosen scenario runs in
# the VM (the install, the setup and the groups are needed only then) and whether the host's own update scenario is chosen.
function Get-RunScope {
    param([object[]] $Plan = @(Get-ScenarioPlan), [string[]] $Groups = @(), [string[]] $Scenarios = @())
    $selection = @(Resolve-ScenarioSelection -Scenarios $Scenarios -Plan $Plan)
    if ($selection.Count -gt 0 -and $Groups.Count -gt 0) { throw 'Use -Groups or -Scenarios, not both.' }
    $chosen = @(if ($selection.Count -gt 0) { $selection } elseif ($Groups.Count -gt 0) { $Plan | Where-Object { $_.Name -in $Groups } | ForEach-Object { $_.Ids } } else { $Plan | ForEach-Object { $_.Ids } })
    $vmIds = @($Plan | Where-Object { -not $_.Host } | ForEach-Object { $_.Ids })
    [pscustomobject]@{
        Selection = $selection
        FullRun = ($Groups.Count -eq 0 -and $selection.Count -eq 0)
        ChosenIds = $chosen
        NeedVm = [bool](@($vmIds | Where-Object { $_ -in $chosen }).Count)
        RunUpdate = ('update.over-running-previous' -in $chosen)
    }
}

# The selection the runner is working to: $null means everything (a full run). Set once, at the start of a run.
$script:SelectedScenarioIds = $null

function Set-ScenarioSelection {
    param([string[]] $Ids = @())
    $ids = @($Ids | Where-Object { $_ })
    if ($ids.Count -eq 0) { $script:SelectedScenarioIds = $null; return }
    $set = New-Object System.Collections.Generic.HashSet[string]
    foreach ($i in $ids) { [void]$set.Add($i) }
    $script:SelectedScenarioIds = $set
}

function Test-ScenarioSelected {
    param([Parameter(Mandatory)] [string] $Id)
    ($null -eq $script:SelectedScenarioIds) -or $script:SelectedScenarioIds.Contains($Id)
}

function Test-AnyScenarioSelected {
    param([string[]] $Ids = @())
    foreach ($i in $Ids) { if (Test-ScenarioSelected $i) { return $true } }
    $false
}

# The kinds (clients) for which the scenario named by IdFormat (one {0} for the kind) is selected, in the order given.
function Get-SelectedKinds {
    param([Parameter(Mandatory)] [string] $IdFormat, [string[]] $Kinds = $script:AllClientKinds)
    @($Kinds | Where-Object { Test-ScenarioSelected ($IdFormat -f $_) })
}

# What the issue lists, scenario by scenario, for the plan printout and the docs.
function Get-ScenarioTopics {
    @(
        [pscustomobject]@{ Group = 'install'; Proves = 'The tray icon blinks, with its two-item menu, while Deluno sets itself up and the picker runs; Weir is installed through the picker and answers on this PC only.' }
        [pscustomobject]@{ Group = 'films'; Proves = 'A film through each client: grabbed, downloaded, handed to Weir, cleaned and imported. One film carries a small extra that is left out of the hand-off and stays in the client''s folder.' }
        [pscustomobject]@{ Group = 'removal'; Proves = '"Remove this copy" on a finished download leaves nothing behind (a folder; and a single file on the torrent clients); removing twice reads as a failure and writes no second Activity row.' }
        [pscustomobject]@{ Group = 'resend'; Proves = '"I don''t want it", then a resend of the same release to the same client, before Weir starts the first send, while Weir works on it, and after Weir has finished it: the new hand-off settles with one import.' }
        [pscustomobject]@{ Group = 'stuck'; Proves = 'Downloads stuck at 0 bytes, stuck just short of the end, too slow, and with too few peers are each struck at their count with the right plural wording; one stuck at 99.99% never reads 100%.' }
        [pscustomobject]@{ Group = 'keepseeding'; Proves = 'A struck torrent from a private tracker, on qBittorrent, Transmission and Deluge, is moved to the keep-seeding category instead of being deleted: the client still holds it in that category, its data is on disk, it is gone from Transfers and stays gone, and Activity says it was moved and that Deluno is looking for another copy. A public tracker''s torrent with the same strike is removed as before. (rTorrent and uTorrent are not installed in the VM.)' }
        [pscustomobject]@{ Group = 'outage'; Proves = 'Weir quits with nothing in flight, and with a hand-off in flight: the tray, System and the Dashboard go amber within a minute and come back within a minute.' }
        [pscustomobject]@{ Group = 'tryagain'; Proves = 'Try again on a failed hand-off: the drawer, read every 200 ms, never shows the old failure and its header follows the real hand-off.' }
        [pscustomobject]@{ Group = 'vanish'; Proves = 'Files that vanish before the hand-off read "Files are gone" and are never handed off; files that vanish while the hand-off waits end it as "source gone" with nothing imported.' }
        [pscustomobject]@{ Group = 'update'; Proves = 'Run by the host after everything else, on a restored clean machine: the previous release is installed silently and given state; the candidate''s Setup runs silently over it while it runs. Deluno comes back on its own (no hand start) as the candidate, with its download clients, libraries and Activity, and its start goes cleanly (the start marker reads ready, no retry, no give-up). Needs the previous release''s Setup.exe (-PreviousInstallerPath).' }
        [pscustomobject]@{ Group = 'problems'; Proves = 'A problem shown in several places clears everywhere when dismissed in one; every hand-off''s closing line in Activity tells one story.' }
    )
}
