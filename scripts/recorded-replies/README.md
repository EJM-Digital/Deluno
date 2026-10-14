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
