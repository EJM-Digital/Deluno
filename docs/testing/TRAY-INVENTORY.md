# Tray inventory (#1278)

Every menu item, icon state, hover text and balloon of the Windows tray (`apps/windows-tray`: `TrayModel.cs` for the menu, the dot and
the hover text, `TrayApplication.cs` for the balloons), with the golden-VM scenario that drives or observes it through UI Automation
(`scripts/golden-path/suite/vm/Tray.Lib.ps1`, `Desktop.Lib.ps1`) and, where UI Automation cannot see it, the reason and the unit test
that holds it. The standard is `docs/PRODUCT_NORTH_STAR.md`'s tray section and `docs/deluno-weir-contract.md` for the shared rules.

Run any scenario alone: `./scripts/golden-path/Invoke-ScenarioSuite.ps1 -RigHost <host> ... -Scenarios <id>` (`docs/golden-path.md`).
`node scripts/check-coverage-register.mjs` keeps this page honest: every scenario id below must exist in
`scripts/golden-path/suite/ScenarioPlan.ps1` (`Test-ScenarioSuite.ps1` reads this page), and the register rows for the tray name them.

How the VM reads the tray: the hover text is the Name of the icon's button; the dot colour and the pause bars and update arrow are read
from the button's pixels (`Get-TrayDot`, `Get-TrayIconLook`, `Get-TrayIconBluePixels`); menu items come from the open menu's elements
(name, enabled, ticked); a balloon is read from Windows' own notification record (`Get-ToastTextCount`, `Wait-ToastShown`).

## Menu (24 entries, in the standard's order)

| Item | Rule | Scenario |
| --- | --- | --- |
| Add Transmission's torrents back... | shown only while a re-add waits; pressing it restarts the daemon and adds each magnet | `tray.waiting-work-menu-lines` |
| Try again: install apps... | shown only while apps wait; says what and from when | `tray.waiting-work-menu-lines`, `tray.try-again` |
| Open Deluno | bold default, always enabled; left click opens once | `tray.menu-actions`, `tray.clicks-and-second-launch` (bold: see below) |
| Status line | greyed; the hover text | `tray.dot-and-hover`, `tray.amber-sources`, `tray.hover-fits` |
| Pause automation / Resume automation | flips; enabled only while running | `tray.menu-actions`, `tray.dot-and-hover`, `tray.start-failure` (greyed while it starts), `tray.red-when-host-fails` (greyed while down) |
| Restart Deluno | enabled running, errored, stopped; greyed while starting | `tray.menu-actions`, `tray.starting-dot`, `tray.start-failure`, `tray.red-when-host-fails` |
| Copy address | enabled only with a live address | `tray.menu-actions`, `tray.clicks-and-second-launch` |
| Allow other devices on your network... / Only allow this PC | shown only where a firewall step exists; enabled by the real rule | `firewall.tray-allow-and-revoke` |
| Change port (N)... | names the live port; refuses a busy port and bad text | `tray.change-port`, `tray.port-dialog`, `tray.port-override-cleared`, `tray.port-balloon` |
| Open data folder, Open logs folder | open Explorer | `tray.menu-actions` |
| Start with Windows | tick follows the Run key | `tray.menu-actions`, `tray.sign-in-entry` |
| Update item (five states) | Check, Checking... (grey), Downloading... (grey), Download update (vX), Restart to update (vX); a copy that is not installed opens the Updates page | `update.tray-menu` (Check, Download, Downloading, Restart), `tray.menu-details`, `tray.update-not-installed`, `service.updates-in-service-mode` |
| Version line | "Deluno v<version>", greyed | `tray.menu-details` |
| Report a problem... | opens the logs folder | `tray.menu-details` |
| Advanced: Run as a Windows service... | opens the Startup Mode dialog | `service.install-run-uninstall`, `service.mode-switching` |
| Advanced: Let any address in... | confirm, writes the override, restarts | `tray.allowed-hosts-recovery`, `tray.menu-details` |
| Quit Deluno | stops Deluno and removes the icon | `tray.quit` (tooltip: see below) |

## Icon states

| State | Scenario |
| --- | --- |
| Green dot, no mark | `tray.dot-and-hover`, `tray.not-a-problem` |
| Amber: download client does not answer | `tray.dot-and-hover` |
| Amber: indexer does not answer; two things down ("and 1 more") | `tray.amber-sources` |
| Amber: library folder has a problem | `tray.amber-sources`, `tray.not-a-problem` (snoozed: green again) |
| Amber: Weir (the processor) does not answer | `outage.idle`, `outage.inflight` |
| Not amber: a connection that is switched off | `tray.not-a-problem` |
| Red: the host gave up | `tray.red-when-host-fails`, `tray.start-failure` |
| Grey blinking dot: starting | `tray.starting-dot`, first run in `install-first-run` |
| Pause bars | `tray.dot-and-hover` |
| Blue update arrow | `update.tray-menu` |
| First-run icon (two-item menu) | `install-first-run` |

## Hover text and status line

| Text | Scenario |
| --- | --- |
| "Deluno - Running at <address>" | `tray.dot-and-hover` |
| One thing down, "and N more" | `tray.dot-and-hover`, `tray.amber-sources` |
| "Paused" | `tray.dot-and-hover` |
| "Starting..." | `tray.starting-dot` |
| "Stopped by itself - starting again (try N of 5)" | `tray.start-failure` |
| "Stopped - choose Restart Deluno" | `tray.red-when-host-fails`, `tray.start-failure` |
| "Update available (vX)" / "Update ready (vX)" | `update.tray-menu` |
| "Couldn't check for updates ..." | `update.tray-menu` |
| Longer than 127 characters: cut between whole words, ends in "..." | `tray.hover-fits` |

## Balloons

| Balloon | Scenario |
| --- | --- |
| Deluno is running (address, firewall note) | `tray.running-balloons`, `tray.clicks-and-second-launch` |
| Deluno is already running | `tray.running-balloons`, `tray.clicks-and-second-launch` |
| Deluno can't open yet | `tray.clicks-and-second-launch` |
| Address copied | `tray.menu-actions` |
| Deluno answers on any address again | `tray.allowed-hosts-recovery` |
| Deluno is now at port N | `tray.port-balloon` |
| Deluno did not start (each failed try) / Deluno could not start (gave up) | `tray.start-failure` |
| Deluno is running again | `tray.relaunch-waits` |
| Only this PC can reach Deluno, Other devices can still reach Deluno, Another firewall rule lets devices in | `firewall.tray-allow-and-revoke` |
| Deluno could not change Start with Windows | `tray.sign-in-entry` |
| A Deluno update is available / is ready | `update.tray-menu` |
| Couldn't check for updates | `update.tray-menu` |
| Deluno did not install the update (backup failed) | `update.blocked-note-shown` |
| GitHub's limit has lifted | `tray.try-again` |

## What the tray does at start and what it serves

| Behaviour | Scenario |
| --- | --- |
| The tray-hosted server serves the front end | `tray.serves-front-end` |
| A client Deluno started before that is not answering is started again at start; Weir never is | `tray.restarts-clients-at-start` |

## What UI Automation cannot drive

| Item | Why | Held by |
| --- | --- | --- |
| Bold on "Open Deluno" and the Quit tooltip | a WinForms menu does not expose font weight or tooltip text to UI Automation | `tests/Deluno.Tray.Tests/TrayStandardTests.cs` |
| Amber for the metadata service, the disk running low, a Usenet client with no news server, a health reading that fails | cannot be caused on the VM without breaking Deluno's own broker or filling the disk, or needs a news-less Usenet client that the VM's NZBGet and SABnzbd cannot be left in | `TrayStandardTests.cs`, `TrayOutageAndFirstRunTests.cs` |
| Change port greyed while updating, the grey "Updating..." dot | the updating state lasts only while Deluno is replaced by the installer | `TrayStandardTests.cs` |
| Deluno could not open your browser | the VM's browser always opens | `ABrowserOpensOnlyWhenThePersonClicksTests.cs` |
| Deluno stopped by itself, Deluno is restarting itself | the host runs inside the tray process; nothing outside can end it without ending the tray | `tests/Deluno.Tray.Tests/HostSupervisorTests.cs` |
| Transmission was moved / reset, Deluno did not move your data | need a Transmission folder layout or a data-folder move the VM does not hold | `tests/Deluno.Tray.Tests` |
| The first reading failing ("can't read its own health yet") | needs the host's own health call to fail | `TrayStandardTests.cs` |
| Balloon length (10 s information, 30 s problem) | Windows' notification record does not keep the duration | `TrayStandardTests.cs` |
