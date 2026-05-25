# Built-In Downloader (NZB + Torrent)

Deluno ships an optional in-process download engine that can replace a
separate SABnzbd / NZBGet / qBittorrent install for users who'd rather
not run another service.

## Status

| Layer | State |
|---|---|
| Architecture spec | ✅ `docs/exec-plans/active/builtin-downloader-architecture.md` |
| Cross-platform secrets hardening (Phase 0.5) | ✅ Windows DPAPI + AES-GCM with `DELUNO_MASTER_KEY` / `master.key` |
| `Deluno.Downloader` project scaffolding + invariant rewrite (Phase 1) | ✅ |
| Shared layer (Phase 2) | ✅ Persistence schema, lifecycle state machine, extraction (zip/7z/tar/RAR via unrar binary), post-processing |
| NZB protocol (Phase 3a) | ✅ NZB parser, yEnc decoder, NNTP with TLS + AUTHINFO + CAPABILITIES + MODE READER + DATE keepalive + 30-min connection age, connection pool, multi-server tier-walk failover, streaming-write orchestrator |
| Torrent protocol (Phase 3b) | ✅ MonoTorrent 3.0.2 wrapper (`ITorrentEngine`), magnet URI parsing with v1/v2/base32, private-tracker policy (13-point compliance list) |
| par2 binary wrapper (Phase 4) | ✅ Wrapper code; ⏳ binary bundling per-platform deferred to installer work |
| Integration adapters (Phase 5) | ✅ `deluno-nzb` / `deluno-torrent` wired into existing `DownloadClientGrabService` + `DownloadClientTelemetryService` switch blocks |
| Settings UI for engine config (Phase 6) | ⏳ Backend ready; React UI in `apps/web` not yet written |
| Reference compliance vs SAB / qBittorrent (Phase 7) | ⏳ Requires live providers + real swarms |
| User docs (Phase 8) | ✅ This file |
| **Job execution worker** | ⏳ The thing that drives queued jobs through the lifecycle is the next polish step — until it lands, grabs queue but don't execute |

## What works today (verified by the test suite)

- A user can add a "Deluno NZB (built-in)" or "Deluno Torrent (built-in)"
  download-client row alongside SAB / qBit / etc.
- Grabs from indexers land as `Queued` rows in `downloader.db`.
- Telemetry returns the queue + state to the existing UI.
- Pause / resume / delete actions transition jobs through the
  lifecycle state machine (with audit-row writes to
  `state_transitions`).
- 430/430 unit tests across the solution.

## What doesn't work yet

- Jobs sit at `Queued` state forever. The hosted-service worker that
  pulls queued jobs and kicks off the NZB downloader / MonoTorrent
  engine is the next code increment.
- par2 binaries aren't bundled per-platform yet. NZB downloads that
  need repair will fail with an actionable error pointing at the
  bundling work.
- UnRAR binary isn't bundled per-platform yet (same story).
- Settings UI pages for the two engines don't exist yet — config has
  to be done in JSON / DB directly until React work lands.
- No magnet-leak-window guard for private trackers (architecture-doc
  requirement). Don't use the built-in torrent engine on a private
  tracker yet.

## Configuration

### Cross-platform secrets backend

Credentials (NNTP passwords, tracker passkeys, proxy passwords) are
encrypted at rest via `ISecretProtector`. The backend is selected at
startup:

| Platform | Backend | Notes |
|---|---|---|
| Windows | DPAPI (`dpapi:v1:` prefix) | Per-user scope; no setup. |
| Linux / macOS / Docker with `DELUNO_MASTER_KEY` env var | AES-256-GCM (`aes:v1:` prefix) | 32 bytes, base64-encoded. Recommended for container deployments. |
| Linux / macOS / Docker with `master.key` file | AES-256-GCM | Auto-generated under `$DATAROOT/secrets/master.key` on first run; back this file up. |
| Anything else | DataProtection fallback | **Master key written unencrypted to disk** — loud warning at startup. |

Diagnostics: `GET /api/diagnostics/secrets-backend` returns the active
backend, whether it's hardened, and any warnings.

### Built-in NZB engine

Server configuration goes in `downloader.db` (`nzb_servers` table) via
the settings UI when that lands; until then, direct SQL or fixture
import. Per the architecture doc, each server has:

- `tier` (`Primary` / `Backup` / `Fill`) — failover ordering
- `priority` — within-tier ordering (lower = first)
- `retention_days` — articles older than this skip the server
- `max_connections` — pool size (respect your provider's cap)

Multi-server failover: a 430 ("article missing") on one server
escalates to the next server, not job failure. This is the single
biggest correctness gap in toy NZB clients — verified by
`MultiServerArticleFetcherTests`.

### Built-in Torrent engine

MonoTorrent-backed. Defaults:

- Listen port 51413 (qBittorrent default; common firewall rules
  already know it)
- IPv4 + IPv6 dual-stack bind
- UPnP / NAT-PMP on
- DHT / PEX / LSD on **for public torrents only**
- All three hard-OFF when `info.private = 1`, regardless of global
  setting (enforced at announce/connection time, not at config time)

The 13-point private-tracker compliance list (DHT off, PEX off, LSD
off, `event=stopped` on pause, passkey preservation, byte precision,
HTTPS-downgrade protection, single-IP enforcement, peer-id template,
UA allowlist, `key=` parameter, `compact=1`+`no_peer_id=1`, strict
min interval, encryption, no version downgrade) lives in
`PrivateTrackerPolicy.Required` and is asserted by
`PrivateTrackerPolicyTests`.

## Architecture

```
                 ┌─────────────────────────────────────┐
                 │  Frontend (React / SignalR)         │
                 └─────────────────┬───────────────────┘
                                   │
                 ┌─────────────────▼───────────────────┐
                 │  Deluno.Integrations.DownloadClients│   ← single seam
                 │  (Grab + Telemetry + Action)        │     for every
                 └─────────────────┬───────────────────┘     download client
                                   │
            ┌──────────────────────┼──────────────────────┐
            │                      │                      │
   ┌────────▼─────────┐   ┌────────▼─────────┐  ┌─────────▼────────┐
   │  Remote SAB,     │   │  Builtin/        │  │  Remote qBit,    │
   │  NZBGet (HTTP)   │   │  Adapters        │  │  Transmission,   │
   │                  │   │  (deluno-nzb /   │  │  Deluge, ...     │
   │                  │   │   deluno-torrent)│  │                  │
   └──────────────────┘   └────────┬─────────┘  └──────────────────┘
                                   │
                       ┌───────────▼────────────┐
                       │  Deluno.Downloader     │   ← new project
                       │  (Engine, Nzb, Torrent,│
                       │   Persistence,         │
                       │   Extraction, Postproc)│
                       └────────────────────────┘
```

Module boundary: `Deluno.Downloader` has zero domain knowledge.
Domain modules (Movies, Series) never reference it.
`Deluno.Integrations.DownloadClients.Builtin` is the adapter layer
that bridges the existing seam to the engine.

## Why all of this

The original behaviour was "Deluno hands a URL to SAB and SAB does the
work." That meant:

- Every Deluno user had to install SAB separately + configure it +
  keep it running.
- Connectivity issues meant two services to debug instead of one.
- Adding NZB-specific UX (refine-before-import, custom routing per
  library) required SAB cooperation.

The built-in option is for users who'd rather have everything in one
process. It does NOT replace remote SAB / qBit — both options can
coexist; pick per library.

## See also

- `docs/exec-plans/active/builtin-downloader-architecture.md` — the
  authoritative design.
- `src/Deluno.Downloader/` — implementation.
- `tests/Deluno.Downloader.Tests/` — 108 unit tests covering the
  protocol stacks.
