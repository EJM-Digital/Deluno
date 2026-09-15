# Lab tooling

The bits needed to run [`docs/exec-plans/active/E2E-full-product-test.md`](../../docs/exec-plans/active/E2E-full-product-test.md) against the simulation VM. Nothing here ships, and nothing here is used by the test suites.

These lived in a session scratchpad and had to be hunted for across old session directories every time. They live here now.

## `provision-rig.ps1`

Turns a fresh Windows machine into the rig: the service account, the folder
topology, qBittorrent, SABnzbd and Weir at pinned versions, the client
configuration, and the services.

```powershell
./scripts/lab/provision-rig.ps1 -ComputerName <ip> -Password <admin> `
    -ServiceAccount deluno -ServiceAccountPassword <pw> `
    -LibraryPath '\nas\share' -NasUser <u> -NasPassword <p>
```

Stages are separable (`-Stage folders`), because provisioning a machine is a
long sequence of things that each fail on their own and re-running all of it to
retry the last step wastes the afternoon.

**It stops before Deluno's first run, deliberately.** Phase 0.5 of the
end-to-end plan is "a clean install asks to create an account, not to sign in",
and phases 1 to 7 are the first-run experience, the libraries, the profiles and
the connections. Provisioning those would destroy the first thing the plan
tests, so this leaves Deluno installed, running and untouched.

It does not restate the service shape either - `ensure-rig-services.ps1` owns
that, and provisioning calls it.

Two things it gets right that the hand-built rig got wrong:

- The **NAS credential is stored in the service account's own vault**, through a
  one-shot task running as that account. `cmdkey` only ever writes to the vault
  of whoever runs it, so storing it as the administrator would leave the service
  refused - the same shape as the bug the service account exists to avoid.
- The **share is probed as the service account**, not from the provisioning
  session. A WinRM session has no delegatable network credentials, so testing
  the share from here fails whether or not the service could reach it, and sends
  you to debug the wrong machine.

## `rig-software.json`

The exact software the rig runs, pinned by SHA-256 - captured from the
simulation VM before it was retired, so a new rig starts from a set the
end-to-end plan has actually been walked against rather than from whatever is
current that day. Installers are staged on the developer machine and copied
over, so the rig needs no internet and cannot quietly get a different build.

## `assert-media-boundary.ps1`

The owner's real library shares a NAS with the test run, and the NAS account
Deluno uses can write to the whole share. Nothing on the NAS refuses a write,
so this refuses instead. **Run it before any step that can import, rename,
move, clean, or turn on automation, and stop if it fails.**

```powershell
./scripts/lab/assert-media-boundary.ps1
```

It reads, and never changes, every path Deluno or qBittorrent could write to:
library roots, downloads and processor folders, derived recycle bins, global
root settings, destination rules, and qBittorrent's save paths. It exits
non-zero if any NAS path is outside `rig.json`'s `mediaBoundary.isolatedRoot`,
if any path is inside a protected root, or if any path uses a drive letter
mapped to the share. The NAS is judged the same by name or by address.

Its first draft had a bug that would have let a production path through.
PowerShell 7 returns a JSON array as one object, so both libraries were judged
as one space-joined string, and the isolated Movies root at the front passed
whatever followed it. Test the guard against a rig file that must fail
(`-RigPath`), not only against one that passes.

## `ensure-rig-services.ps1`

Holds every service on the VM to one rule: it starts without a person, and it
comes back after a reboot. Reports drift, repairs only what has it.

```powershell
./scripts/lab/ensure-rig-services.ps1 -ReportOnly
./scripts/lab/ensure-rig-services.ps1
```

Deluno, qBittorrent and Weir are SYSTEM scheduled tasks with a boot trigger.
SABnzbd is a real Windows service, because it will not run as anything else: it
checks its own session id at startup, before parsing any argument, and every
process launched over WinRM or by a SYSTEM task is in session 0, so it always
decides it is a service. Its options live in the `CommandLine` value under its
own service key, which is where its `get_serv_parms` reads them from.

That one difference is why the end-to-end plan spent two runs recording "SABnzbd
needs an interactive session" as a fact about the rig.

## `provision-usenet.ps1`

Brings the usenet half of the rig up and proves it moved real bytes: the
NNTP/NZB fixture on the desktop, SABnzbd's news server and category, and the API
key Deluno holds for it — read from SABnzbd rather than typed, so the two cannot
drift apart again.

```powershell
./scripts/lab/provision-usenet.ps1 -Verify
```

Idempotent; every part is skipped when it is already right. `-Verify` pushes the
fixture NZB through SABnzbd, waits for it to complete, compares the decoded
SHA-256 against the source, and removes what the check created.

## `fake-nntp-server.py`

The deterministic NNTP server and NZB endpoint behind that. One genuine yEnc
article over a real NNTP conversation, which SABnzbd requests, decodes and
post-processes exactly as it would a commercial provider's. It proves the
protocol, client and import path; it proves nothing about a real provider's
authentication, retention or availability.

Started for you by `provision-usenet.ps1`. Directly:

```powershell
python -u scripts/lab/fake-nntp-server.py --port 1119 --http-port 1180 `
  --article 'vendor\e2e-fixtures\data\Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO.mkv' `
  --log 'vendor\e2e-fixtures\logs\nntp.log'
```

## `torznab_seed.py`

A real Torznab indexer that serves genuine `.torrent` files — correct bencode, correct SHA1 piece hashes — whose bytes a real qBittorrent fetches over BEP-19 webseeds. It is not a mock: the client does an actual transfer and an actual hash check, and would fail one if the bytes were wrong.

The media is Big Buck Bunny (Blender Foundation, CC-BY), which is genuinely redistributable.

**It runs on the desktop, not the VM, and it is not a service.** Start it before any acquisition step:

```bash
TORZNAB_BIND=0.0.0.0 TORZNAB_ADVERTISE=192.168.1.102 python scripts/lab/torznab_seed.py
```

`TORZNAB_ADVERTISE` must be the address the VM can reach the desktop on, because it is baked into the torrent's webseed URLs. Defaults to loopback, which the VM cannot use.

Expects source media at `vendor/e2e-fixtures/data/bbb.mp4` and writes torrents to `vendor/e2e-fixtures/torrents`, both repo-relative. Override with `TORZNAB_SOURCE` and `TORZNAB_OUT`.

Add it to Deluno as a Torznab indexer at `http://192.168.1.102:9117/api`. Any API key is accepted.

### Isolated multi-file season pack

For a whole-season replacement acceptance run, start a second, temporary
listener rather than changing the stable `9117` catalogue. Set
`DELUNO_E2E_SEASON_PACK_RELEASE` to a release name that contains an `Snn`
token, and `DELUNO_E2E_SEASON_PACK_EPISODES` to the exact episode numbers the
pack should carry. The fixture creates one separately hashed video entry per
episode plus its `.nfo`; qBittorrent still transfers and checks every byte.

```powershell
$env:TORZNAB_PORT = '9120'
$env:TORZNAB_BIND = '0.0.0.0'
$env:TORZNAB_ADVERTISE = '192.168.1.102'
$env:TORZNAB_OUT = 'vendor\e2e-fixtures\torrents-season-replacement'
$env:DELUNO_E2E_SEASON_PACK_RELEASE = 'Show.Name.S01.2160p.BluRay.x265-DELUNO'
$env:DELUNO_E2E_SEASON_PACK_EPISODES = '1,2,3,4,5'
python -u scripts\lab\torznab_seed.py
```

Point a temporary TV-only indexer at `http://192.168.1.102:9120/api`, route only
the bounded manual acceptance step to it, then restore the original routing.
The separate `TORZNAB_OUT` directory keeps the main listener's torrent metadata
untouched. With the pack variables set, a whole-season query intentionally
returns only that pack: ordinary single-episode fixture releases cannot become
the accidental first candidate in a replacement decision.

## `seed-library.py`

Fills a movies catalogue with N synthetic titles, because a shelf built for
20,000 cannot be judged on the rig's eleven films.

```bash
python scripts/lab/seed-library.py C:\path\to\movies.db 20000
```

Every seeded row carries a `seed` id prefix, so undoing it is
`DELETE FROM movie_entries WHERE id LIKE 'seed%';`. Edit the VM's database the
documented way — stop the host, copy `movies.db` *and* its `-wal`/`-shm` down,
run this locally, move the VM's stale sidecars aside, copy back — or the stale
WAL silently reverts it.

Used for [#312](https://github.com/jampat000/Deluno/issues/312): 20,000 titles
reached the client in 1.4 s over 41 requests, for 27.8 MB of heap and 3,507 DOM
nodes.

## `watch-pipeline.ps1`

One call that prints where an acquisition has got to: the telemetry summary, each queue item's status, the processor hand-offs with their output paths and import job ids, the job queue, and the dispatch/processing/import activity. It is the fastest way to answer "is it stuck, and where".

```powershell
.\scripts\lab\watch-pipeline.ps1
```

Credentials are the lab ones and are in the handover.
