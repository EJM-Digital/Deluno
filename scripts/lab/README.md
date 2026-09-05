# Lab tooling

The bits needed to run [`docs/exec-plans/active/E2E-full-product-test.md`](../../docs/exec-plans/active/E2E-full-product-test.md) against the simulation VM. Nothing here ships, and nothing here is used by the test suites.

These lived in a session scratchpad and had to be hunted for across old session directories every time. They live here now.

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
  --article 'C:\Deluno\e2e\data\Breaking.Bad.S01E01.1080p.WEB-DL.x264-DELUNO.mkv' `
  --log 'C:\Deluno\e2e\logs\nntp.log'
```

## `torznab_seed.py`

A real Torznab indexer that serves genuine `.torrent` files — correct bencode, correct SHA1 piece hashes — whose bytes a real qBittorrent fetches over BEP-19 webseeds. It is not a mock: the client does an actual transfer and an actual hash check, and would fail one if the bytes were wrong.

The media is Big Buck Bunny (Blender Foundation, CC-BY), which is genuinely redistributable.

**It runs on the desktop, not the VM, and it is not a service.** Start it before any acquisition step:

```bash
TORZNAB_BIND=0.0.0.0 TORZNAB_ADVERTISE=192.168.1.102 python scripts/lab/torznab_seed.py
```

`TORZNAB_ADVERTISE` must be the address the VM can reach the desktop on, because it is baked into the torrent's webseed URLs. Defaults to loopback, which the VM cannot use.

Expects source media at `C:\Deluno\e2e\data\bbb.mp4` and writes torrents to `C:\Deluno\e2e\torrents`.

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
$env:TORZNAB_OUT = 'C:\Deluno\e2e\torrents-season-replacement'
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
