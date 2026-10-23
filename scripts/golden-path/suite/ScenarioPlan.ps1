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
        @{ Name = 'nzbgetsetup'; Function = 'Invoke-GroupNzbgetSetup'; Ids = @('nzbget.setup-twice') }
        @{ Name = 'films';    Function = 'Invoke-GroupFilms';    Ids = @($all | ForEach-Object { "film.$_" }) + @('film.extra-left-out') }
        @{ Name = 'removal';  Function = 'Invoke-GroupRemoval';  Ids = @($all | ForEach-Object { "remove-copy.$_.folder" }) + @($script:TorrentKinds | ForEach-Object { "remove-copy.$_.file" }) }
        @{ Name = 'resend';   Function = 'Invoke-GroupResend';   Ids = @(foreach ($t in 'before', 'during', 'after') { foreach ($k in $all) { "resend.$t.$k" } }) + @('resend.single-file.takeover') }
        @{ Name = 'stuck';    Function = 'Invoke-GroupStuck';    Ids = @($all | ForEach-Object { "stuck.zero.$_" }) + @($all | ForEach-Object { "stuck.short.$_" }) + @($script:TorrentKinds | ForEach-Object { "stuck.peers.$_" }) + @($script:UsenetKinds | ForEach-Object { "stuck.slow.$_" }) }
        @{ Name = 'keepseeding'; Function = 'Invoke-GroupKeepSeeding'; Ids = @($script:TorrentKinds | ForEach-Object { "keepseeding.moved.$_" }) + @($script:TorrentKinds | ForEach-Object { "keepseeding.public-removed.$_" }) }
        @{ Name = 'outage';   Function = 'Invoke-GroupOutage';   Ids = @('outage.idle', 'outage.inflight') }
        @{ Name = 'tryagain'; Function = 'Invoke-GroupTryAgain'; Ids = @($all | ForEach-Object { "tryagain.$_" }) }
        @{ Name = 'vanish';   Function = 'Invoke-GroupVanish';   Ids = @(foreach ($t in 'before', 'waiting') { foreach ($k in $all) { "vanish.$t.$k" } }) }
        @{ Name = 'problems'; Function = 'Invoke-GroupProblems'; Ids = @('problems.dismiss-everywhere', 'activity.one-story') }
        @{ Name = 'seasonpack'; Function = 'Invoke-GroupSeasonPack'; Ids = @('seasonpack.no-library') }
        @{ Name = 'layout';   Function = 'Invoke-GroupLayout';   Ids = @('layout.390-folder-chain') }
        @{ Name = 'firewall'; Function = 'Invoke-GroupFirewall'; Ids = @('firewall.first-run-rule', 'firewall.screen-allow', 'firewall.screen-declined', 'firewall.tray-allow-and-revoke') }
        @{ Name = 'tray'; Function = 'Invoke-GroupTray'; Ids = @('tray.menu-actions', 'tray.change-port', 'tray.allowed-hosts-recovery', 'tray.red-when-host-fails') }
        @{ Name = 'traystates'; Function = 'Invoke-GroupTrayStates'; Ids = @('tray.dot-and-hover', 'tray.starting-dot', 'tray.menu-details', 'tray.quit') }
        @{ Name = 'service'; Function = 'Invoke-GroupService'; Ids = @('service.install-run-uninstall', 'service.wrong-password') }
        @{ Name = 'desktop'; Function = 'Invoke-GroupDesktop'; Ids = @('companions.tray-install-picker', 'filesystem.native-folder-picker') }
        # The last group that runs in the VM: it uses GitHub's hourly allowance up, which stays used up for up to an hour.
        @{ Name = 'github';   Function = 'Invoke-GroupGithub';   Ids = @('github-limit.messages') }
        # The host's own, each on a clean machine: they restore the clean checkpoint and install, so they run after the groups.
        # The host's own, on a clean machine: the guided setup walked through in the browser from a fresh install, with the clients and Weir the picker installed.
        @{ Name = 'guide'; Function = 'Invoke-GroupGuide'; Host = $true; Ids = @('guided-setup.walk') }
        # The other ways an installed Deluno is updated (#1295), each on a clean machine of its own with the previous release installed, against the candidate's real packages served from a local feed in the VM.
        @{ Name = 'updatepaths'; Function = $null; Host = $true; Ids = @('update.in-app', 'update.apply-on-quit', 'update.apply-at-start', 'update.corrupt-package', 'update.setup-interactive', 'update.setup-over-stopped', 'update.service-mode') }
        @{ Name = 'update';   Function = $null; Host = $true; Ids = @('update.over-running-previous') }
        # The github-limit scenarios use up GitHub's anonymous allowance (60 calls an hour for the whole network), and it stays used up
        # for up to an hour, so they run after every scenario and install that needs the network. The update scenario installs
        # and runs Deluno's picker twice and was running before this one; the picker now runs last of all (#1368).
        @{ Name = 'githubpicker'; Function = $null; Host = $true; Ids = @('github-limit.picker') }
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
        UpdatePathIds = @($chosen | Where-Object { $_ -in @('update.in-app', 'update.apply-on-quit', 'update.apply-at-start', 'update.corrupt-package', 'update.setup-interactive', 'update.setup-over-stopped', 'update.service-mode') })
        RunGithubPicker = ('github-limit.picker' -in $chosen)
        RunGuide = ('guided-setup.walk' -in $chosen)
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
        [pscustomobject]@{ Group = 'nzbgetsetup'; Proves = 'The Usenet provider is set a second time on an NZBGet that already has it, through Deluno''s API, before the films: NZBGet''s own status is not paused and its log holds no "Invalid option" and no "errors in configuration", and Deluno shows no settings-error warning on it. (#1257)' }
        [pscustomobject]@{ Group = 'films'; Proves = 'A film through each client: grabbed, downloaded, handed to Weir, cleaned and imported. One film carries a small extra that is left out of the hand-off and stays in the client''s folder.' }
        [pscustomobject]@{ Group = 'removal'; Proves = '"Remove this copy" on a finished download leaves nothing behind (a folder; and a single file on the torrent clients); removing twice reads as a failure and writes no second Activity row.' }
        [pscustomobject]@{ Group = 'resend'; Proves = '"I don''t want it", then a resend of the same release to the same client, before Weir starts the first send, while Weir works on it, and after Weir has finished it: the new hand-off settles with one import. One more: a single file sent again while Weir works on it, where Deluno imports from the output path Weir names in its finished answer (Weir 1.0.0-rc.18 on) or, when Weir names none, finds the cleaned file at the source''s path below the watched folder and says so; either way one library copy and nothing in Weir''s output. The branch that ran is recorded as a note. (#1262)' }
        [pscustomobject]@{ Group = 'stuck'; Proves = 'Downloads stuck at 0 bytes, stuck just short of the end, too slow, and with too few peers are each struck at their count with the right plural wording; one stuck at 99.99% never reads 100%.' }
        [pscustomobject]@{ Group = 'keepseeding'; Proves = 'A struck torrent from a private tracker, on qBittorrent, Transmission and Deluge, is moved to the keep-seeding category instead of being deleted: the client still holds it in that category, its data is on disk, it is gone from Transfers and stays gone, and Activity says it was moved and that Deluno is looking for another copy. A public tracker''s torrent with the same strike is removed as before. (rTorrent and uTorrent are not installed in the VM.)' }
        [pscustomobject]@{ Group = 'outage'; Proves = 'Weir quits with nothing in flight, and with a hand-off in flight: the tray, System and the Dashboard go amber within a minute and come back within a minute.' }
        [pscustomobject]@{ Group = 'tryagain'; Proves = 'Try again on a failed hand-off: the drawer, read every 200 ms, never shows the old failure and its header follows the real hand-off.' }
        [pscustomobject]@{ Group = 'vanish'; Proves = 'Files that vanish before the hand-off read "Files are gone" and are never handed off; files that vanish while the hand-off waits end it as "source gone" with nothing imported.' }
        [pscustomobject]@{ Group = 'seasonpack'; Proves = 'A season-pack replacement whose old episode files match no library (the library''s folder was changed after they were imported) leaves them on disk, byte for byte, and Deluno''s log says they were left because no library matched them and that nothing was deleted. (#1188)' }
        [pscustomobject]@{ Group = 'layout'; Proves = 'With a library whose client''s category folder does not agree with its downloads folder (a real folder-chain problem), every screen opens at 390 px wide in the VM without scrolling sideways or clipping a control, and the problem is raised by name: Health reads "The <library> library''s category and save path" and a screen that lists problems shows it. (#1191)' }
        [pscustomobject]@{ Group = 'firewall'; Proves = 'Windows Firewall and letting other devices in, on a real Windows with a real permission prompt (Deluno is started without administrator rights): the first run left Deluno''s own rule for its port on every network; with it removed, the guide''s Other devices step and the tray''s Allow other devices ask Windows, add the rule and say so; answered No they add nothing and say permission was not given; Only allow this PC removes only Deluno''s own rule, and says another rule still lets devices in when one does. (#1283, #1278, #1295)' }
        [pscustomobject]@{ Group = 'tray'; Proves = 'The tray icon on a real desktop, read and pressed through UI Automation: every menu item in the standard''s order and state; Pause and Resume, Copy address, the two folders, Start with Windows, Open Deluno and Restart Deluno each do what they say; Change port refuses a busy port and moves Deluno to a free one and back; Let any address in lets a turned-away address in again; and the icon goes red, reading Stopped, when another program holds Deluno''s port while it restarts. (#1278, #1295)' }
        [pscustomobject]@{ Group = 'service'; Proves = 'Deluno as a Windows service on a real Windows: Run as a Windows service... from the tray makes the service (auto start, description, restart-on-failure actions, LocalSystem), the tray leaves, the service answers with the same sign-in rules, Windows starts it again after it is killed, and --uninstall-service removes it and the tray comes back; a wrong password for a named user creates a service that does not run. (#1278, #1295)' }
        [pscustomobject]@{ Group = 'desktop'; Proves = 'Two things Deluno does on the computer''s own desktop: the Companion Tools page asks the tray to install an app only the tray installs, and the tray''s picker window opens naming it (a tool the tray does not install is refused); and the native folder picker opens Windows'' own folder window, returns a typed folder on OK, reports cancelled on Cancel and refuses a second ask while one is open. (#1283, #1278)' }
        [pscustomobject]@{ Group = 'github'; Proves = 'With GitHub''s anonymous hourly allowance used up from the VM, the tracker definitions'' Update now and the connections'' version check each name GitHub''s limit, with the reset in the viewer''s local time (never UTC), on the API and on the screen. Uses the limit up for up to an hour, so it is the last group in the VM. (#1181)' }
        [pscustomobject]@{ Group = 'githubpicker'; Proves = 'Run by the host on a clean machine of its own, after the groups: GitHub''s allowance is used up inside the VM before Setup runs, and the first-run picker still installs Weir through github.com''s release feed (releases.atom); the install log names Weir''s installer and checksum, never says the limit stopped it, and the installed Weir is a release the feed lists. (#1181)' }
        [pscustomobject]@{ Group = 'guide'; Proves = 'Run by the host on a clean machine of its own: the installed Windows app (the tray, the picker''s download clients and Weir) is set up end to end through the guided setup in the browser, nothing through the API: Connect all, create Weir''s account and connect it, answer every Yes/No question, Create baseline; the libraries, the Weir workflows and the Folder chain are then checked against the real clients and Weir. (#1283, #1302)' }
        [pscustomobject]@{ Group = 'update'; Proves = 'Run by the host after everything else, on a restored clean machine: the previous release is installed silently and given state; the candidate''s Setup runs silently over it while it runs. Deluno comes back on its own (no hand start) as the candidate, with its download clients, libraries and Activity, and its start goes cleanly (the start marker reads ready, no retry, no give-up). Needs the previous release''s Setup.exe (-PreviousInstallerPath).' }
        [pscustomobject]@{ Group = 'updatepaths'; Proves = 'Run by the host, each on a restored clean machine with the previous release installed: the other ways Deluno is updated, against the candidate''s real packages served from a feed in the VM. In the app, Check, Download (with progress) and Restart to update take the pre-update backup and bring the new build back with its data; a downloaded update installs on Quit (and starts nothing) or at the next start; a damaged package never leaves Deluno unusable; Setup run interactively leaves one Deluno, and Setup over a stopped Deluno starts nothing. Needs the previous release''s Setup.exe (-PreviousInstallerPath) and the candidate''s package and release list beside its Setup.exe. (#1295)' }
        [pscustomobject]@{ Group = 'problems'; Proves = 'A problem shown in several places clears everywhere when dismissed in one; every hand-off''s closing line in Activity tells one story.' }
    )
}
