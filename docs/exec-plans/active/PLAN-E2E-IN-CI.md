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
| `install-first-run` | `windows` | to port |
| `nzbget.setup-twice` | `linux-e2e:nzbgetsetup` | to port |
| `film.qbittorrent` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.transmission` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.deluge` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.sabnzbd` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.nzbget` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `film.extra-left-out` | `linux-e2e:films` | ported (tests/e2e/films.spec.ts) |
| `remove-copy.qbittorrent.folder` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.transmission.folder` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.deluge.folder` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.sabnzbd.folder` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.nzbget.folder` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.qbittorrent.file` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.transmission.file` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `remove-copy.deluge.file` | `linux-e2e:removal` | ported (tests/e2e/removal.spec.ts) |
| `resend.before.qbittorrent` | `linux-e2e:resend` | to port |
| `resend.before.transmission` | `linux-e2e:resend` | to port |
| `resend.before.deluge` | `linux-e2e:resend` | to port |
| `resend.before.sabnzbd` | `linux-e2e:resend` | to port |
| `resend.before.nzbget` | `linux-e2e:resend` | to port |
| `resend.during.qbittorrent` | `linux-e2e:resend` | to port |
| `resend.during.transmission` | `linux-e2e:resend` | to port |
| `resend.during.deluge` | `linux-e2e:resend` | to port |
| `resend.during.sabnzbd` | `linux-e2e:resend` | to port |
| `resend.during.nzbget` | `linux-e2e:resend` | to port |
| `resend.after.qbittorrent` | `linux-e2e:resend` | to port |
| `resend.after.transmission` | `linux-e2e:resend` | to port |
| `resend.after.deluge` | `linux-e2e:resend` | to port |
| `resend.after.sabnzbd` | `linux-e2e:resend` | to port |
| `resend.after.nzbget` | `linux-e2e:resend` | to port |
| `resend.single-file.takeover` | `linux-e2e:resend` | to port |
| `stuck.zero.qbittorrent` | `linux-e2e:stuck` | to port |
| `stuck.zero.transmission` | `linux-e2e:stuck` | to port |
| `stuck.zero.deluge` | `linux-e2e:stuck` | to port |
| `stuck.zero.sabnzbd` | `linux-e2e:stuck` | to port |
| `stuck.zero.nzbget` | `linux-e2e:stuck` | to port |
| `stuck.short.qbittorrent` | `linux-e2e:stuck` | to port |
| `stuck.short.transmission` | `linux-e2e:stuck` | to port |
| `stuck.short.deluge` | `linux-e2e:stuck` | to port |
| `stuck.short.sabnzbd` | `linux-e2e:stuck` | to port |
| `stuck.short.nzbget` | `linux-e2e:stuck` | to port |
| `stuck.peers.qbittorrent` | `linux-e2e:stuck` | to port |
| `stuck.peers.transmission` | `linux-e2e:stuck` | to port |
| `stuck.peers.deluge` | `linux-e2e:stuck` | to port |
| `stuck.slow.sabnzbd` | `linux-e2e:stuck` | to port |
| `stuck.slow.nzbget` | `linux-e2e:stuck` | to port |
| `keepseeding.moved.qbittorrent` | `linux-e2e:keepseeding` | to port |
| `keepseeding.moved.transmission` | `linux-e2e:keepseeding` | to port |
| `keepseeding.moved.deluge` | `linux-e2e:keepseeding` | to port |
| `keepseeding.public-removed.qbittorrent` | `linux-e2e:keepseeding` | to port |
| `keepseeding.public-removed.transmission` | `linux-e2e:keepseeding` | to port |
| `keepseeding.public-removed.deluge` | `linux-e2e:keepseeding` | to port |
| `outage.idle` | `linux-e2e:outage` | ported (tests/e2e/outage.spec.ts) |
| `outage.inflight` | `linux-e2e:outage` | ported (tests/e2e/outage.spec.ts) |
| `outage.tray` | `windows` | to port: the Deluno tray icon's hover text and dot going amber and green in the same watch (a tray exists only on Windows; the Linux outage group makes every other reading of `outage.idle` and `outage.inflight`) |
| `tryagain.qbittorrent` | `linux-e2e:tryagain` | ported (tests/e2e/tryagain.spec.ts) |
| `tryagain.transmission` | `linux-e2e:tryagain` | ported (tests/e2e/tryagain.spec.ts) |
| `tryagain.deluge` | `linux-e2e:tryagain` | ported (tests/e2e/tryagain.spec.ts) |
| `tryagain.sabnzbd` | `linux-e2e:tryagain` | ported (tests/e2e/tryagain.spec.ts) |
| `tryagain.nzbget` | `linux-e2e:tryagain` | ported (tests/e2e/tryagain.spec.ts) |
| `vanish.before.qbittorrent` | `linux-e2e:vanish` | to port |
| `vanish.before.transmission` | `linux-e2e:vanish` | to port |
| `vanish.before.deluge` | `linux-e2e:vanish` | to port |
| `vanish.before.sabnzbd` | `linux-e2e:vanish` | to port |
| `vanish.before.nzbget` | `linux-e2e:vanish` | to port |
| `vanish.waiting.qbittorrent` | `linux-e2e:vanish` | to port |
| `vanish.waiting.transmission` | `linux-e2e:vanish` | to port |
| `vanish.waiting.deluge` | `linux-e2e:vanish` | to port |
| `vanish.waiting.sabnzbd` | `linux-e2e:vanish` | to port |
| `vanish.waiting.nzbget` | `linux-e2e:vanish` | to port |
| `problems.dismiss-everywhere` | `linux-e2e:problems` | to port |
| `activity.one-story` | `linux-e2e:problems` | to port |
| `seasonpack.no-library` | `linux-e2e:seasonpack` | to port |
| `layout.390-folder-chain` | `linux-e2e:layout` | to port |
| `github-limit.messages` | `linux-e2e:github` | to port |
| `github-limit.picker` | `windows` | to port |
| `update.over-running-previous` | `windows` | to port |

## Step 1 result (17 Oct 2026)

The stack is `tests/e2e/compose.yaml`: Deluno's image, qBittorrent (linuxserver, by digest), Weir (by digest) and the fixture (`tests/e2e/fixture`: a Torznab feed, a web seed and a read-only look at the shared folders). The film is a real 130-second video made by ffmpeg inside the fixture. Its torrent carries no tracker and names the fixture as a web seed (BEP 19), so qBittorrent downloads every byte over HTTP from the fixture with DHT, PEX and LSD off and no internet. `tests/e2e/films.spec.ts` ports `film.qbittorrent` and `film.extra-left-out`, keeping each of their checks and reading the client's, Weir's and the folders' own state. The `e2e-linux` job in `ci.yml` runs it as the matrix group `films`. Two things the stack taught: Deluno sends only to a client whose connection test passed, and it treats a film under two minutes as a likely sample.

## Step 2a result (17 Oct 2026)

Transmission 4.1.3, Deluge 2.2.0, SABnzbd 5.1.3 and NZBGet 26.3 (linuxserver, each by digest) joined `tests/e2e/compose.yaml`, and `tests/e2e/films.spec.ts` now runs `film.<client>` for all five clients plus `film.extra-left-out`, with the checks of G02-Films.ps1: the client's own API says it finished, Deluno handed the film to Weir, the hand-off settled, the cleaned file is in the library, and Activity says sent, handed over and imported. All five films are sent first, so the clients and Weir work at once, as on the VM.

How each client downloads with no internet:

- **Transmission and Deluge** both support web seeds (BEP 19, libtorrent and Transmission's own), so they use the same tracker-less torrent as qBittorrent and download every byte over HTTP from the fixture. No tracker and no second client were needed. Their spec setup, through their own RPC, turns DHT, PEX and local discovery off and sets the incomplete folder.
- **SABnzbd and NZBGet** download from the fixture's news server: the fixture posts the film and an .nfo as real yEnc articles (a folder job, as in `scripts/golden-path/usenet-stub`), serves them over NNTP on port 1119 (any login), lists them on a Newznab feed (`/newznab/api`) and serves the .nzb. SABnzbd's first settings file is `tests/e2e/sabnzbd/sabnzbd.ini` (key, host whitelist, folders, news server); NZBGet's server and folders are set through its own JSON-RPC.
- Deluno runs in a container and cannot tell that Transmission and Deluge see the same folders, so the stack adds the path mapping a person adds (`/downloads` to `/downloads`); without it Deluno names no save folder and the film lands outside the folder Weir watches.

## Step 2b result (17 Oct 2026)

Groups `removal`, `tryagain` and `outage` are ported, each passing on a fresh stack: `tests/e2e/removal.spec.ts` (8 scenarios: the five clients as a folder and the three torrent clients as a single file; the fixture now makes single-file torrents with `layout: "file"`), `tests/e2e/tryagain.spec.ts` (5) and `tests/e2e/outage.spec.ts` (2). Shared helpers are in `tests/e2e/flow.ts`.

- **Removal** keeps every check of G03-Removal.ps1 (on disk before, first removal succeeds, nothing left in the completed folders, library file untouched, second removal reads as a failure, no second Activity row) and also asks the client itself whether it still holds the download, before and after.
- **Try again** stops Weir's container, sends the five films, reads each client's own API for the finish, waits for the failed hand-offs, starts Weir, and reads the drawer in a real browser (Playwright's Chromium, installed by a CI step for the groups that need it) every 200 ms. It also checks Weir's own record of the file at the end.
- **Outage** stops and starts Weir's container and watches System, the Dashboard and Deluno's record of the Weir connection every 300 ms, with the same one-minute limits; the in-flight scenario pauses Weir through its own API first. The tray readings of the VM scenario cannot exist on Linux: they are the new `outage.tray` row for the Windows job.
