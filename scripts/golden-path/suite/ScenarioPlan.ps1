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
        @{ Name = 'outage';   Function = 'Invoke-GroupOutage';   Ids = @('outage.idle', 'outage.inflight') }
        @{ Name = 'tryagain'; Function = 'Invoke-GroupTryAgain'; Ids = @($all | ForEach-Object { "tryagain.$_" }) }
        @{ Name = 'vanish';   Function = 'Invoke-GroupVanish';   Ids = @(foreach ($t in 'before', 'waiting') { foreach ($k in $all) { "vanish.$t.$k" } }) }
        @{ Name = 'problems'; Function = 'Invoke-GroupProblems'; Ids = @('problems.dismiss-everywhere', 'activity.one-story') }
        @{ Name = 'update';   Function = 'Invoke-GroupUpdate';   Ids = @('update.over-running-previous') }
    )
    $groups | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Function = $_.Function; Ids = @($_.Ids) } }
}

# What the issue lists, scenario by scenario, for the plan printout and the docs.
function Get-ScenarioTopics {
    @(
        [pscustomobject]@{ Group = 'install'; Proves = 'The tray icon blinks, with its two-item menu, while Deluno sets itself up and the picker runs; Weir is installed through the picker and answers on this PC only.' }
        [pscustomobject]@{ Group = 'films'; Proves = 'A film through each client: grabbed, downloaded, handed to Weir, cleaned and imported. One film carries a small extra that is left out of the hand-off and stays in the client''s folder.' }
        [pscustomobject]@{ Group = 'removal'; Proves = '"Remove this copy" on a finished download leaves nothing behind (a folder; and a single file on the torrent clients); removing twice reads as a failure and writes no second Activity row.' }
        [pscustomobject]@{ Group = 'resend'; Proves = '"I don''t want it", then a resend of the same release to the same client, before Weir starts the first send, while Weir works on it, and after Weir has finished it: the new hand-off settles with one import.' }
        [pscustomobject]@{ Group = 'stuck'; Proves = 'Downloads stuck at 0 bytes, stuck just short of the end, too slow, and with too few peers are each struck at their count with the right plural wording; one stuck at 99.99% never reads 100%.' }
        [pscustomobject]@{ Group = 'outage'; Proves = 'Weir quits with nothing in flight, and with a hand-off in flight: the tray, System and the Dashboard go amber within a minute and come back within a minute.' }
        [pscustomobject]@{ Group = 'tryagain'; Proves = 'Try again on a failed hand-off: the drawer, read every 200 ms, never shows the old failure and its header follows the real hand-off.' }
        [pscustomobject]@{ Group = 'vanish'; Proves = 'Files that vanish before the hand-off read "Files are gone" and are never handed off; files that vanish while the hand-off waits end it as "source gone" with nothing imported.' }
        [pscustomobject]@{ Group = 'update'; Proves = 'The candidate''s Setup run silently over a running previous release: Deluno comes back on its own (no hand start) as the candidate, with its download clients, libraries and Activity, and its start goes cleanly (the start marker reads ready, no retry, no give-up). Needs the previous release''s Setup.exe (-PreviousInstallerPath).' }
        [pscustomobject]@{ Group = 'problems'; Proves = 'A problem shown in several places clears everywhere when dismissed in one; every hand-off''s closing line in Activity tells one story.' }
    )
}
