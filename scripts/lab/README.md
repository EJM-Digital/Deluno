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

## `watch-pipeline.ps1`

One call that prints where an acquisition has got to: the telemetry summary, each queue item's status, the processor hand-offs with their output paths and import job ids, the job queue, and the dispatch/processing/import activity. It is the fastest way to answer "is it stuck, and where".

```powershell
.\scripts\lab\watch-pipeline.ps1
```

Credentials are the lab ones and are in the handover.
