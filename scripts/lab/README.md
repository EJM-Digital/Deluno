# Lab tooling

The bits needed to run [`docs/exec-plans/active/E2E-full-product-test.md`](../../docs/exec-plans/active/E2E-full-product-test.md) against the simulation VM. Nothing here ships, and nothing here is used by the test suites.

These lived in a session scratchpad and had to be hunted for across old session directories every time. They live here now.

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
