# PLAN: the end-to-end suite runs in CI, with no rig and no VM

**Decided by James, 16 Oct 2026:** "once we are happy and we are no longer in RC there will be no VM or Rig to test, we will need to do it properly how Senior development teams do it." The golden-path suite (scripts/golden-path/, docs/golden-path.md) runs on a private Hyper-V machine. It takes over two hours, one group after another, and it disappears with the rig. This plan replaces it with suites that run on GitHub's own runners, from a clean checkout, every time.

## Where each kind of proof runs

| Kind | Runs on | How |
| --- | --- | --- |
| Deluno with real download clients, indexers and Weir | `ubuntu-latest`, Docker Compose | Deluno's own image (built by the same CI run). Weir's published image, pinned by digest. qBittorrent, Transmission, Deluge, SABnzbd and NZBGet as their published images, pinned by digest. The suite's local Torznab/Newznab feed and NNTP server as small fixture containers. Scenarios drive Deluno's API and screens (Playwright against the real server, as `apps/web/tests/real/` does) and read each client's own API, never Deluno's word for it. |
| Windows-only behaviour | `windows-latest` | Silent install from the candidate installer, the Windows service, the component picker, the update from the previous release, the tray through UI Automation, NTFS hard links and cross-volume moves, uninstall. |
| Contracts with other apps | every push | Already in place: tests replay recorded real replies (`tests/fixtures/recorded/`). |
| Drift in the other apps | scheduled, weekly | A job runs the pinned and the newest image of each app, re-records, and fails on any difference from the committed recordings. A difference becomes an issue, not a silent update. |

## Speed comes from sharding, not from shorter clocks

- **One job per group, in a matrix.** Each job runs its own Compose stack, so groups cannot interfere. The slowest groups are split further, for example `resend` by client. Wall time is the slowest shard, aimed at 15 minutes.
- **The real default timings stay proven.** The stall check interval and Weir's report time are not shortened.

## When it runs

- **Every push:** today's unit, integration, recorded-reply and smoke tests. Also the E2E shards whose area the change touches, chosen by the same paths filter `changes` uses.
- **Before a tag:** the full E2E matrix and the Windows job on the tagged commit, or a pass carried from an ancestor whose shipped files are identical (James, 16 Oct: the carry-over rule).
- **Nightly**, once the repository is public (Actions minutes for standard runners are then free): the full matrix, repeated, to catch flakes.

## Getting there without a gap

1. **Prove the stack.** Bring up Compose with Deluno, one client (qBittorrent) and the fixture feed, and port group `films`. It must pass in CI.
2. **Port group by group.** Use the times and evidence in docs/golden-path.md. Each port keeps every check the VM scenario makes, and the parity table below records it.
3. **The Windows job** takes install, picker, update, tray and NTFS.
4. **The rig suite stays the gate** until every row below is ported and green in CI twice in a row. Then the release check switches to the CI suite, and the VM suite is retired.

## Parity table

One row per golden-path scenario id (77 on 16 Oct 2026, 78 with `outage.tray`), each with its CI home (`linux-e2e:<group>` or `windows`) and its state (to port, ported, or green twice). The table lists every id in `scripts/golden-path/suite/ScenarioPlan.ps1`. The four other `film.*` clients joined the stack in step 2a (below).

| Scenario id | CI home | State |
| --- | --- | --- |
| `install-first-run` | `windows` | ported (needs first CI run): tests/e2e-windows, leg `install` |
| `nzbget.setup-twice` | `linux-e2e:nzbgetsetup` | ported (tests/e2e/nzbgetsetup.spec.ts) |
| `film.qbittorrent` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.transmission` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.deluge` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.sabnzbd` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.nzbget` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.extra-left-out` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `remove-copy.qbittorrent.folder` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.transmission.folder` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.deluge.folder` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.sabnzbd.folder` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.nzbget.folder` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.qbittorrent.file` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.transmission.file` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `remove-copy.deluge.file` | `linux-e2e:removal` | ported (tests/e2e/removalcases.ts) |
| `resend.before.qbittorrent` | `linux-e2e:resendbefore` | ported (tests/e2e/resendbeforecases.ts) |
| `resend.before.transmission` | `linux-e2e:resendbefore` | ported (tests/e2e/resendbeforecases.ts) |
| `resend.before.deluge` | `linux-e2e:resendbefore` | ported (tests/e2e/resendbeforecases.ts) |
| `resend.before.sabnzbd` | `linux-e2e:resendbefore` | ported (tests/e2e/resendbeforecases.ts) |
| `resend.before.nzbget` | `linux-e2e:resendbefore` | ported (tests/e2e/resendbeforecases.ts) |
| `resend.during.qbittorrent` | `linux-e2e:resendduringtorrent` | ported (tests/e2e/resendduringtorrent.spec.ts) |
| `resend.during.transmission` | `linux-e2e:resendduringtorrent` | ported (tests/e2e/resendduringtorrent.spec.ts) |
| `resend.during.deluge` | `linux-e2e:resendduringtorrent` | ported (tests/e2e/resendduringtorrent.spec.ts) |
| `resend.during.sabnzbd` | `linux-e2e:resendduringusenet` | ported (tests/e2e/resendduringusenet.spec.ts) |
| `resend.during.nzbget` | `linux-e2e:resendduringusenet` | ported (tests/e2e/resendduringusenet.spec.ts) |
| `resend.after.qbittorrent` | `linux-e2e:resendaftertorrent` | ported (tests/e2e/resendaftertorrent.spec.ts) |
| `resend.after.transmission` | `linux-e2e:resendaftertorrent` | ported (tests/e2e/resendaftertorrent.spec.ts) |
| `resend.after.deluge` | `linux-e2e:resendaftertorrent` | ported (tests/e2e/resendaftertorrent.spec.ts) |
| `resend.after.sabnzbd` | `linux-e2e:resendafterusenet` | ported (tests/e2e/resendafterusenet.spec.ts) |
| `resend.after.nzbget` | `linux-e2e:resendafterusenet` | ported (tests/e2e/resendafterusenet.spec.ts) |
| `resend.single-file.takeover` | `linux-e2e:resendduringtorrent` | ported (tests/e2e/resendduringtorrent.spec.ts) |
| `stuck.zero.qbittorrent` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.zero.transmission` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.zero.deluge` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.zero.sabnzbd` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.zero.nzbget` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.short.qbittorrent` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.short.transmission` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.short.deluge` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.short.sabnzbd` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.short.nzbget` | `linux-e2e:stuckprogress` | ported (tests/e2e/stuckprogresscases.ts) |
| `stuck.peers.qbittorrent` | `linux-e2e:stuckrate` | ported (tests/e2e/stuckratecases.ts) |
| `stuck.peers.transmission` | `linux-e2e:stuckrate` | ported (tests/e2e/stuckratecases.ts) |
| `stuck.peers.deluge` | `linux-e2e:stuckrate` | ported (tests/e2e/stuckratecases.ts) |
| `stuck.slow.sabnzbd` | `linux-e2e:stuckrate` | ported (tests/e2e/stuckratecases.ts) |
| `stuck.slow.nzbget` | `linux-e2e:stuckrate` | ported (tests/e2e/stuckratecases.ts) |
| `keepseeding.moved.qbittorrent` | `linux-e2e:keepseeding` | ported (tests/e2e/keepseeding.spec.ts) |
| `keepseeding.moved.transmission` | `linux-e2e:keepseeding` | ported (tests/e2e/keepseeding.spec.ts) |
| `keepseeding.moved.deluge` | `linux-e2e:keepseeding` | ported (tests/e2e/keepseeding.spec.ts) |
| `keepseeding.public-removed.qbittorrent` | `linux-e2e:keepseeding` | ported (tests/e2e/keepseeding.spec.ts) |
| `keepseeding.public-removed.transmission` | `linux-e2e:keepseeding` | ported (tests/e2e/keepseeding.spec.ts) |
| `keepseeding.public-removed.deluge` | `linux-e2e:keepseeding` | ported (tests/e2e/keepseeding.spec.ts) |
| `outage.idle` | `linux-e2e:outage` | ported (tests/e2e/outagecases.ts) |
| `outage.inflight` | `linux-e2e:outage` | ported (tests/e2e/outagecases.ts) |
| `outage.tray` | `windows` | ported (needs first CI run): tests/e2e-windows, leg `install`; the Deluno tray icon's hover text and dot going amber and green in the same watch (a tray exists only on Windows; the Linux outage group makes every other reading of `outage.idle` and `outage.inflight`) |
| `tryagain.qbittorrent` | `linux-e2e:tryagain` | ported (tests/e2e/tryagaincases.ts) |
| `tryagain.transmission` | `linux-e2e:tryagain` | ported (tests/e2e/tryagaincases.ts) |
| `tryagain.deluge` | `linux-e2e:tryagain` | ported (tests/e2e/tryagaincases.ts) |
| `tryagain.sabnzbd` | `linux-e2e:tryagain` | ported (tests/e2e/tryagaincases.ts) |
| `tryagain.nzbget` | `linux-e2e:tryagain` | ported (tests/e2e/tryagaincases.ts) |
| `vanish.before.qbittorrent` | `linux-e2e:vanishbefore` | ported (tests/e2e/vanishbefore.spec.ts) |
| `vanish.before.transmission` | `linux-e2e:vanishbefore` | ported (tests/e2e/vanishbefore.spec.ts) |
| `vanish.before.deluge` | `linux-e2e:vanishbefore` | ported (tests/e2e/vanishbefore.spec.ts) |
| `vanish.before.sabnzbd` | `linux-e2e:vanishbefore` | ported (tests/e2e/vanishbefore.spec.ts) |
| `vanish.before.nzbget` | `linux-e2e:vanishbefore` | ported (tests/e2e/vanishbefore.spec.ts) |
| `vanish.waiting.qbittorrent` | `linux-e2e:vanishwaiting` | ported (tests/e2e/vanishwaiting.spec.ts) |
| `vanish.waiting.transmission` | `linux-e2e:vanishwaiting` | ported (tests/e2e/vanishwaiting.spec.ts) |
| `vanish.waiting.deluge` | `linux-e2e:vanishwaiting` | ported (tests/e2e/vanishwaiting.spec.ts) |
| `vanish.waiting.sabnzbd` | `linux-e2e:vanishwaiting` | ported (tests/e2e/vanishwaiting.spec.ts) |
| `vanish.waiting.nzbget` | `linux-e2e:vanishwaiting` | ported (tests/e2e/vanishwaiting.spec.ts) |
| `problems.dismiss-everywhere` | `linux-e2e:problems` | ported (tests/e2e/problems.spec.ts) |
| `activity.one-story` | `linux-e2e:problems` | ported (tests/e2e/problems.spec.ts) |
| `seasonpack.no-library` | `linux-e2e:seasonpack` | ported (tests/e2e/seasonpack.spec.ts) |
| `layout.390-folder-chain` | `linux-e2e:layout` | ported (tests/e2e/layout.spec.ts) |
| `github-limit.messages` | `linux-e2e:github` | ported (tests/e2e/github.spec.ts) |
| `github-limit.picker` | `windows` | ported (needs first CI run): tests/e2e-windows, leg `githubpicker` |
| `update.over-running-previous` | `windows` | ported (needs first CI run): tests/e2e-windows, leg `update` |

## Step 1 result (17 Oct 2026)

The stack is `tests/e2e/compose.yaml`: Deluno's image, qBittorrent (linuxserver, by digest), Weir (by digest) and the fixture (`tests/e2e/fixture`: a Torznab feed, a web seed and a read-only look at the shared folders). The film is a real 130-second video made by ffmpeg inside the fixture. Its torrent carries no tracker and names the fixture as a web seed (BEP 19), so qBittorrent downloads every byte over HTTP from the fixture with DHT, PEX and LSD off and no internet. `tests/e2e/films.spec.ts` ports `film.qbittorrent` and `film.extra-left-out`, keeping each of their checks and reading the client's, Weir's and the folders' own state. The `e2e-linux` job in `ci.yml` runs it as the matrix group `films`. Two things the stack taught: Deluno sends only to a client whose connection test passed, and it treats a film under two minutes as a likely sample.

## Step 2a result (17 Oct 2026)

Transmission 4.1.3, Deluge 2.2.0, SABnzbd 5.1.3 and NZBGet 26.3 (linuxserver, each by digest) joined `tests/e2e/compose.yaml`, and `tests/e2e/films.spec.ts` now runs `film.<client>` for all five clients plus `film.extra-left-out`, with the checks of G02-Films.ps1: the client's own API says it finished, Deluno handed the film to Weir, the hand-off settled, the cleaned file is in the library, and Activity says sent, handed over and imported. All five films are sent first, so the clients and Weir work at once, as on the VM.

How each client downloads with no internet:

- **Transmission and Deluge** both support web seeds (BEP 19, libtorrent and Transmission's own), so they use the same tracker-less torrent as qBittorrent and download every byte over HTTP from the fixture. No tracker and no second client were needed. Their spec setup, through their own RPC, turns DHT, PEX and local discovery off and sets the incomplete folder.
- **SABnzbd and NZBGet** download from the fixture's news server: the fixture posts the film and an .nfo as real yEnc articles (a folder job, as in `scripts/golden-path/usenet-stub`), serves them over NNTP on port 1119 (any login), lists them on a Newznab feed (`/newznab/api`) and serves the .nzb. SABnzbd's first settings file is `tests/e2e/sabnzbd/sabnzbd.ini` (key, host whitelist, folders, news server); NZBGet's server and folders are set through its own JSON-RPC.
- Deluno runs in a container and cannot tell that Transmission and Deluge see the same folders, so the stack adds the path mapping a person adds (`/downloads` to `/downloads`); without it Deluno names no save folder and the film lands outside the folder Weir watches.

## Step 2b result (17 Oct 2026)

Groups `removal`, `tryagain` and `outage` are ported, each passing on a fresh stack: `tests/e2e/removalcases.ts` (8 scenarios: the five clients as a folder and the three torrent clients as a single file; the fixture now makes single-file torrents with `layout: "file"`), `tests/e2e/tryagaincases.ts` (5) and `tests/e2e/outagecases.ts` (2). Shared helpers are in `tests/e2e/flow.ts`.

- **Removal** keeps every check of G03-Removal.ps1 (on disk before, first removal succeeds, nothing left in the completed folders, library file untouched, second removal reads as a failure, no second Activity row) and also asks the client itself whether it still holds the download, before and after.
- **Try again** stops Weir's container, sends the five films, reads each client's own API for the finish, waits for the failed hand-offs, starts Weir, and reads the drawer in a real browser (Playwright's Chromium, installed by a CI step for the groups that need it) every 200 ms. It also checks Weir's own record of the file at the end.
- **Outage** stops and starts Weir's container and watches System, the Dashboard and Deluno's record of the Weir connection every 300 ms, with the same one-minute limits; the in-flight scenario pauses Weir through its own API first. The tray readings of the VM scenario cannot exist on Linux: they are the new `outage.tray` row for the Windows job.

## Step 2c result (17 Oct 2026)

The remaining Linux groups are ported, each passing on a fresh stack (`down --volumes`, then `up`): `nzbgetsetup`, `vanishbefore`, `vanishwaiting`, `problems`, `layout`, `keepseeding`, `seasonpack` and `github` (13 scenario ids, 78 rows in all with the 52 before; the Windows rows stay). Tests-only times on a workstation, stack already up: `nzbgetsetup` 0.4 min, `vanishbefore` 1.2, `vanishwaiting` 12.3 (Weir waits about 11 minutes before it reports a source gone, and all five clients wait together), `problems` 2.1, `layout` 2.3, `keepseeding` 7.0, `seasonpack` 0.8, `github` 0.4. `vanishwaiting` is the slowest and the only one near 15 minutes; it cannot be split, because the wait is Weir's own.

- **Vanish** deletes a finished download's files from the shared volume with `docker exec` in Deluno's container (a person at the file manager) and reads Deluno's telemetry rows ("Files are gone", within 30 s) and hand-offs. **Problems** stops and starts Weir's container and reads the places a person looks (Transfers, the bell, System); the story check sends a film through every client. **Layout** is real Chromium at 390 px and reads `window.innerWidth` on all 58 screens (the lists come from `apps/web/tests/helpers/route-lists.ts`, so a new screen is walked the day it is added).
- **Keep-seeding** adds the fixture's feed under another address (`/private/api`) as a private indexer, and asks each client itself (qBittorrent's category, Deluge's label, Transmission's labels) where the torrent is.
- **Season pack** is not the VM script's `/api/filesystem/import/jobs` call: that endpoint refuses a pack that replaces an owned episode (its preview blocks it, "replace it through an episode-scoped review"), so the VM scenario could never reach the pack path. The group drives Deluno's own season search instead (`POST /api/series/{id}/seasons/1/search`), which finds a five-episode pack on the fixture's Torznab feed under the TV categories, grabs it, qBittorrent downloads it from the web seed, and Deluno imports it as a replacement with the targets the dispatch recorded. The show's five episodes come from a metadata broker in the fixture (the contract in `docs/metadata-broker-contract.md`; Deluno refuses to invent an episode a catalogue does not list). Every check of the VM script is kept: the old files are hashed first and are still there byte for byte (at the `.deluno-pack-backup-` name the pack path gives them), the new files are under the library's new folder, and Deluno's log says once for each file that it was left because no library matched it and nothing was deleted.
- **GitHub**: a hosted runner's own address must never be used up, so the group runs with `--profile github`. What is real: Deluno's image and its own HTTPS calls to `api.github.com`, and GitHub's reply, recorded from the real API after the anonymous allowance was spent (`tests/fixtures/recorded/github/2022-11-28/rate-limit-used-up.json`, by `scripts/recorded-replies/Record-GithubLimit.mjs`: 60 real calls, then the 403). What is not GitHub's: the server (the name `api.github.com` resolves, inside the stack only, to `tests/e2e/fixture/github.mjs`, behind a certificate from an authority the stack makes and Deluno trusts through `SSL_CERT_DIR`), and one value, the reset time (the recorded one has long passed, so the stand-in sends one an hour after it started and its `/limit` says which). `github.com`, `www.github.com` and `codeload.github.com` resolve to a container with nothing listening, so the free release feed and the definitions archive cannot be read, as the VM's hosts file did. `github-limit.picker` stays `windows`.
- The Deluno restart the VM did from its tray menu is `docker restart` of Deluno's container, which empties its memory of earlier reads the same way.

## Step 3 result (17 Oct 2026): the Windows job, written and checked offline; not yet run on a runner

The four `windows` ids are ported to `tests/e2e-windows/` (a runner, `Run-WindowsE2E.ps1`, and one scenario file per leg) and run by the `e2e-windows` job in `ci.yml`. They are marked `ported (needs first CI run)` and become `green twice` only after two passing runs; the register's `windows-e2e` proofs are added after the first pass, never before.

- **One fresh runner per leg**, because each starts from a clean machine: `install` (`install-first-run`, then `outage.tray` on the same install), `githubpicker` (`github-limit.picker`) and `update` (`update.over-running-previous`). The job is a matrix of the three, needs `windows-installer`, is due exactly when it is (the same flag), and `ci-passed` judges it.
- **The checks are the golden path's own, run on the runner instead of in a VM.** The runner is already signed in to a desktop, so there is no PowerShell Direct and no scheduled task: Setup, the picker driver (`Invoke-ComponentPicker.ps1`) and the tray watcher (`Watch-Tray.ps1`) are hidden processes in the runner's own session. `install-first-run` is `G01-Install.ps1` unchanged, `update.over-running-previous` is `Update.Lib.ps1` unchanged, and `github-limit.picker` is judged by the host script's own `Get-GithubPickerChecks`. `outage.tray` takes the tray half of `G06-Outage.ps1`.
- **The tray is driven through UI Automation, as on the VM** (`Tray.Lib.ps1`): the icons are promoted onto the taskbar through `NotifyIconSettings`, the hover text is the Name of the taskbar button, and the dot's colour is read from the button's pixels. Weir quits through its own tray menu (UI Automation, the menu key), falling back to stopping its processes, and starts again as a process.
- **Installers.** The candidate is the artifact `windows-installer` uploaded in the same run (`deluno-windows-<sha>`). The previous release's is the newest published release's `*Setup*.exe`, from `gh release download` with the job's own token. The githubpicker leg gets no token: GitHub's anonymous allowance is what it tests.
- **Results** are in the golden path's JSON shape (`C:\e2e-windows\results\results.json`) with evidence, a picture of the desktop at the key moments, and Deluno's and Weir's logs, uploaded as `e2e-windows-<leg>`. A scenario a leg owns that never reports is an error.
- **Unproven until a runner has run it:** that the runner has a taskbar and notification area for UI Automation to read; that the picker window can be driven there (Server Manager, the first-run firewall step); how long the five clients and Weir take to download; and that anonymous GitHub calls from a hosted runner's address can be used up.
- **Offline checks:** `tests/e2e-windows/Test-WindowsE2E.ps1` (run by `npm run ci:check`).

## Step 4 result (18 Oct 2026): TV episodes

The films' path now runs for a real TV episode too (the show-detail rows `a-tv-episode-searched-grabbed-through-a-real`, `a-tv-episode-through-a-usenet-client-sabnzbd` and `remove-this-copy-resend-stuck-outage-try-again` of the register). These are not golden-path scenarios, so they are not in the parity table above; their ids start `tv.`.

- **The stack.** `tests/e2e/tv.ts` adds to the movies stack what an episode needs: Weir's TV library (watched `/downloads/completed/TV`, output `/weir/Ready/TV`, linked to Deluno), the qBittorrent TV category, the metadata broker (the fixture lists five aired episodes for a show whose title begins `Gppack`, as in `seasonpack`), a profile with no size limits and a TV library that refines before import through Weir. The fixture's `/films` call takes `tvEpisode: N` and makes `Title.S01E0N.1080p.WEB-DL.x264-E2E` as a torrent (folder or single file) or a Usenet post, offered under the TV categories. `sendFilm` on a TV stack adds the show, takes its episodes from the broker and presses Search for S01E01 (`POST /api/series/{id}/episodes/search`); a resend uses `POST /api/series/{id}/grab`. So every film group's helper runs a TV episode unchanged.
- **`tv.episode.<client>`** (group `tvepisodes`): all five clients. The checks are the films': the client's own API says it finished, Deluno handed it to Weir, the hand-off settled, the cleaned file is in the TV library and Activity says sent, handed over and imported. It also reads that the show owns exactly S01E01.
- **The variants**, one torrent client (qBittorrent) and one Usenet client (SABnzbd) each: `tv.remove-copy.<client>.folder` (`tvremove`), `tv.resend.before|during|after.<client>` (`tvredobefore`, `tvredoduring`, `tvredoafter`), `tv.stuck.zero|short.<client>` (`tvstalledprogress`), `tv.stuck.peers.qbittorrent` and `tv.stuck.slow.sabnzbd` (`tvstalledrate`), `tv.outage.inflight.<client>` (`tvweirout`), `tv.tryagain.<client>` (`tvretry`), `tv.vanish.before.<client>` (`tvgonebefore`) and `tv.vanish.waiting.<client>` (`tvgonewaiting`). The scenarios of removal, try again, outage, resend before and the two stuck phases moved from their spec files to `removalcases.ts`, `tryagaincases.ts`, `outagecases.ts`, `resendbeforecases.ts`, `stuckprogresscases.ts` and `stuckratecases.ts` (as the resend during and after already were), each taking `"movies"` or `"tv"`; the movie spec files only call them. The register's proofs follow.
- **The names matter.** Playwright picks a group by matching its name against the spec file's path, so a TV spec's name must not contain another group's name (`tvremove`, not `tvremoval`).
