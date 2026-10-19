# Recorded replies: the capture scripts (#1237)

`Invoke-RecordReplies.ps1 -RigHost <host> -VmMediaFile <a real film in the VM>`
re-records SABnzbd, NZBGet, qBittorrent, Transmission, Deluge and Weir on the golden VM into
`tests/fixtures/recorded/<app>/<version>/<state>.json` (format: `tests/fixtures/recorded/README.md`). The version in
the path is what the app reports, so a new app version is recorded by running the same command again.

- It needs the VM running with the apps installed and the golden path's stubs up. It restores no checkpoint, so run it
  only when no golden run is using the VM.
- No password or key is a parameter. `Record.Secrets.ps1` reads, inside the VM only, qBittorrent's generated login (and Transmission's if saved) from Deluno's component-credentials file, Weir's webhook secret from Deluno's connection to Weir, and SABnzbd's key from sabnzbd.ini; each value is also removed from the recordings by exact match.
- It builds what the VM lacks: it brings the Usenet stub up to this repository and adds a folder release and one whose last article the news server never answers (`hang.txt`); it makes folder torrents (`Make-Torrent.ps1 -Folder`); and it runs `Start-LocalTracker.ps1` so one client seeds the film (rate-limited) to another, giving a transfer in flight and one stalled near the end.
- `Record.Lib.ps1` is the shared HTTP recorder; `Record-Usenet.ps1`, `Record-Torrents.ps1` and `Record-Weir.ps1` drive
  each app through its states; `Collect-DelunoSide.ps1` and `Start-RecordingProxy.ps1` record Deluno's own side of its
  talk with Weir (put the proxy between them during a golden hand-off, then run with `-DelunoSide`).
- `scrub.mjs` removes keys, passwords, cookies, LAN addresses and machine names (its patterns follow
  `scripts/check-public-exposure.ps1`; `scrub.test.mjs` fails if they drift) and the run fails if anything is left.
- States the VM cannot produce are written to `tests/fixtures/recorded/MISSING.md` with what each needs.
- `scripts/check-recorded-replies.mjs` (in `npm run ci:check`) fails a test that builds one of these apps' replies inline.
  `tests/recorded-reply-allow-list.json` lists the tests that still do: `pending` until converted, `deliberate` (with a
  reason) for a malformed reply no real app sends.
- `Invoke-RecordReplies.ps1 -Extra controls|nzbget-config|weir-states` records only the extra states in `Record-Controls.ps1` (resume of a paused job, a pause of an unknown id, a wrong login for each client, NZBGet paused over a configuration error) and `Record-WeirStates.ps1` (Weir's release-folder and `sourceFiles` hand-offs, queued, working, cancelled and failed, each 409 code, each kind of outcome). It leaves `MISSING.md` alone and puts every app back as it found it: NZBGet's `nzbget.conf` is restored byte for byte and reloaded, and Transmission's wrong-login recording uses a second copy of the daemon rather than changing the running one. `-Apps` picks which clients `controls` covers.
- Weir 1.0.0-rc.18 in one run (#1239, #1262): `Invoke-RecordReplies.ps1 -RigHost <host> -VmName <vm> -Apps weir -Extra weir-states,weir-reports,weir-errors -IncludeBaseWeir -DelunoVersion <Deluno version> -VmMediaFile <film>`. The VM must have Deluno and Weir set up by the scenario suite (`Initialize-SuiteEnvironment`), because `Record-WeirReports.ps1` signs in to Weir with the suite's throwaway login (`C:goldensuitesecrets.json`) to point Weir's connection to Deluno at a recording proxy for the run and put it back after (the original address is first written to `C:golden
ecordedproxyweir-connection-original.txt`). `-ReportSteps '<step>[,<step>]'` runs only those steps of the reports recorder (`takeover`, `collision`, `rejected`, `season packs`, `repeat after 503`, `answered 401`, `answered 403`), for a re-run of one that failed; restore the VM's ready checkpoint first. `Record-WeirErrors.ps1` starts a second Weir server of its own for the 503 `database_busy` and the JSON 500 `internal_error`. What each records is listed at the top of those two files and in `tests/fixtures/recorded/MISSING.md`.


## Apps the VM does not already have: one command each (`Invoke-RecordRealApp.ps1`)

`Invoke-RecordRealApp.ps1 -RigHost <host> -App <app>` installs one real app in the golden VM from its own release source (a checksum
or signature is checked wherever the vendor publishes one; the recording's note says how each file was checked), starts it on
`127.0.0.1`, drives it into the states Deluno's tests read and writes `tests/fixtures/recorded/<app>/<version>/<state>.json` the same way.
It reaches the VM and writes recordings exactly like `Invoke-RecordReplies.ps1`, restores no checkpoint, and prints what it could not
record as `MISSING ...` lines (added to `MISSING.md` by hand). Logins and keys are made inside the VM and never printed.

| `-App` | Records | In-VM script |
|---|---|---|
| `utorrent` | uTorrent Classic's Web UI through the torrent scenario, plus token page, positional `list=1`, `getprops`, `setprops`, `getfiles`, `getsettings`, wrong login | `Record-UTorrent.ps1` |
| `rtorrent` | rTorrent 0.9.x (Ubuntu under WSL1, XML-RPC through nginx) through the torrent scenario, plus `d.multicall2`, `t.multicall`, `f.multicall`, `d.custom1`, `load.*`, the fault for an unknown hash, wrong login | `Record-Rtorrent.ps1` |
| `jackett` | A real Torznab endpoint over public trackers that need no account: caps, feed, text, movie and TV searches, errors, a download link | `Record-IndexerFeeds.ps1` |
| `nzbhydra2` | A real Newznab API (no indexer; those need accounts): caps, empty searches, errors | `Record-IndexerFeeds.ps1` |
| `jellyfin`, `emby` | Wizard, identity, libraries, movie and series catalogue, watched, sessions (playing), refresh, directory browser, collections, item delete, user create, wrong key | `Record-MediaServers.ps1` |
| `plex` | The same for an UNCLAIMED Plex Media Server (no account is made); what needs plex.tv is listed in MISSING.md | `Record-MediaServers.ps1` |
| `seerr` | Seerr built from source (no Docker) and linked to the VM's Jellyfin: status, setup, requests in every state, media, users, errors. Run `-App jellyfin` first | `Record-Seerr.ps1` |
| `flaresolverr` | FlareSolverr's Windows release: index, sessions, `request.get` / `request.post`, errors, a challenge page if one shows | `Record-FlareSolverr.ps1` |

`Install.Lib.ps1` holds the shared in-VM helpers (verified downloads, GitHub release lookup, starting a program, secrets kept DPAPI-protected).
rTorrent turns on the Windows Subsystem for Linux the first time, which needs one restart: the host script restarts the VM and runs the app again.

## The Docker recorders (`docker/`, from 17 October 2026)

The apps that run in a container are recorded on the developer's machine, not in the golden VM, from their published images, with
Docker in WSL and a `wsl` session held open. `docker/apps.compose.yaml` is a compose project of its own (`name: rec2`, ports on 127.0.0.1) beside
`tests/e2e/compose.yaml`; copy it, `tracker.mjs` and a copy of `tests/e2e/sabnzbd/sabnzbd.ini` to a folder on WSL's disk, then
`docker compose -p rec2 -f apps.compose.yaml up -d <service>`, and run the recorder with Node from the repository:

| Recorder | Records | Needs |
|---|---|---|
| `docker/rtorrent.mjs` | rTorrent (XML-RPC) through every state Deluno reads: adds, downloading from a second rTorrent, paused, stalled, finished, removed, label, faults, the login | services `rtorrent`, `rtseed`, `tracker` |
| `docker/media.mjs jellyfin\|emby\|plex` | Jellyfin, Emby and an UNCLAIMED Plex: wizard, libraries, catalogue, watched, sessions, refresh, folder browser, collections, item delete; `REC_ONLY=state,state` writes only those states | the service; a fresh volume for Jellyfin and Emby; Plex waits out its start-up |
| `docker/seerr.mjs` | Seerr linked to a Jellyfin: setup, requests pending, approved, declined, media available and deleted, and the webhooks Seerr sends | services `jellyfin` and `seerr` on fresh volumes, `tracker` |
| `docker/notify.mjs` | ntfy (a topic, an RFC 2047 encoded title, and a server that wants an access token: accepted, refused, wrong token) and Gotify (an application's message, refused without a key) | the containers listed at the top of the script, no compose file |
| `docker/sabnzbd.mjs` | SABnzbd's refused adds | service `sabnzbd` |
| `docker/clientgaps.mjs qbittorrent|qbittorrent4|sabnzbd|nzbget` | qBittorrent's address ban, queue moves, force start and the 4.x pause names (a second container on the stack's network for 4.6.x), SABnzbd and NZBGet paused as a whole and their queue moves | the e2e stack's `qbittorrent`, `sabnzbd`, `nzbget` and `fixture` under a project name of your own |
| `docker/clients.mjs sabnzbdFiles nzbgetFiles` | the files inside a Usenet job (SABnzbd `get_files`, NZBGet `listfiles`): a film and an `.nfo` posted as real articles, listed while the job is paused in the queue and again once it has finished and left the queue (both clients then answer an empty list) | the e2e stack (`tests/e2e/compose.yaml`: `fixture`, `sabnzbd`, `nzbget`) with `REC_*_PORT` for its published ports |
| `docker/tmdb.mjs` | the deployed metadata gateway's replies to the broker calls (real GETs; no Docker) | internet |

`docker/lib.mjs` is the shared recorder (cookies, the scrub, the leak check); `scrub.mjs` keeps a Plex item address under `key`. Nothing here
writes a reply by hand: a state a recorder cannot reach is listed in `tests/fixtures/recorded/MISSING.md`.
