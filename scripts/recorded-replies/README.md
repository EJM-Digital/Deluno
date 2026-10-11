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
