# Built-In Downloader Engine — Architecture Spec

Status: draft (revision 2 — incorporates independent review findings)
Started: 2026-05-25
Last revised: 2026-05-25
Owner: agent

> **Revision 2 changes** (in response to independent review):
> - §Security rewritten: the `Deluno.Platform.SecretsService` named in
>   revision 1 does not exist. Only `ISecretProtector` exists (ASP.NET
>   DataProtection wrapper) and on Linux it writes the master key
>   unprotected to `~/.aspnet/DataProtection-Keys/`. A new **Phase 0.5
>   "Cross-platform secrets hardening"** is now a prerequisite.
> - §Integration touched-files table corrected: `IDownloadClientActionService`
>   does not exist as a separate seam (actions are folded into
>   `DownloadClientTelemetryService`). `DownloadClientTelemetryProfiles.cs`
>   lives in `tests/`, not `src/`.
> - §Schema: all `*_enc BLOB` columns changed to TEXT, because
>   `ISecretProtector.Protect` returns a `dp:v1:`-prefixed string.
> - §BitTorrent private-tracker compliance expanded with six previously-
>   missing requirements (`event=stopped`, passkey preservation, byte
>   precision, HTTPS-downgrade, single-IP, UA allowlist).
> - §BitTorrent magnet handling: new subsection on the magnet-to-metadata
>   leak window (DHT/PEX used to fetch metadata *before* we know the
>   torrent is private — bans users).
> - §Phasing: NZB Phase 3a 6-8 → 10-14 weeks; Torrent Phase 3b 3-4 → 8-12
>   weeks. With Phase 3a and 3b running in parallel (per existing design),
>   total is **30-44 weeks (~7-10 months)** for one engineer. If a
>   single engineer serializes 3a and 3b, calendar total stretches to
>   37-54 weeks. See phasing table footer for both numbers.
> - §par2: Windows binary row contradicted the Decision; now consistent on
>   `par2cmdline-turbo` everywhere, plus GPLv2 source-availability clause.
> - §Risk Register: added 10 missing risks (TLS 1.3 0-RTT, cert rotation
>   mid-fetch, NTP/clock skew, DNS happy-eyeballs, SQLite WAL growth,
>   case-sensitivity, path length, disk-space pre-check, AV interference,
>   Velopack update mid-fetch). MonoTorrent bus factor bumped Medium → High
>   (21 months since last release).
> - Phase 1 work expanded to include the concrete `validate:agents`
>   text-pinning script (currently has no such check).
> - Reviewer was wrong on one point: `Deluno.Contracts` does exist as a
>   project (verified). Revision 1's reference graph was correct on that.

This is the authoritative design document for `Deluno.Downloader`, a
production in-process download engine covering **both Usenet (NZB) and
BitTorrent**. Users select "Deluno NZB (built-in)" or "Deluno Torrent
(built-in)" in Settings → Download Clients instead of pointing at a remote
SABnzbd / qBittorrent / etc. Nothing in `src/Deluno.Downloader/` ships
until the relevant section here is filled in, reviewed, and the
implementation conforms to it.

A validating spike for the NZB side was built during the design phase and
proved the .NET approach is sound, surfacing one real bug (8-bit byte
handling through `StreamReader`). **The spike is not the foundation.**
Most of it will be rewritten against this spec; the NZB parser and yEnc
decoder survive with extensions, the NNTP and orchestrator layers are
redesigned. The torrent side has no spike — MonoTorrent has been doing
this correctly for two decades, and our job is to wrap it well, not to
reimplement it.

## Intent

Give a Deluno user the choice, in Settings → Download Clients, between:

- Any existing remote client (SAB, NZBGet, qBittorrent, Deluge,
  Transmission, uTorrent), or
- **Deluno NZB (built-in)** — in-process Usenet downloader, or
- **Deluno Torrent (built-in)** — in-process BitTorrent client.

When they pick built-in, no external downloader process is required:
Deluno fetches the content, verifies, extracts, and hands the result to
the existing import pipeline. The two protocols can be enabled
independently (NZB only, torrent only, or both).

**Industry-standard means SAB-comparable completion rate, throughput,
integrity, and uptime on the NZB side; and qBittorrent-comparable
swarm behaviour, ratio handling, and private-tracker compliance on the
torrent side — on the same content against the same providers / swarms.**

## Non-Goals

Common:

- **Not a SAB/qBittorrent-compatible HTTP API.** Internal-only. Third-party
  tools that speak those APIs continue to point at a real SAB/qBit
  instance, not at us. (Revisit if user demand emerges.)
- **Not a remote-control protocol target.** No XML-RPC, no JSON-RPC server
  beyond what Deluno already exposes.

NZB-specific:

- **Not a Usenet poster.** Read-only. No `POST`, no `IHAVE`, no `TAKETHIS`.
- **Not a newsreader.** No text groups, no header browsing, no threading.
- **Not a Usenet indexer.** We consume NZBs; we don't index.

Torrent-specific:

- **Not a public tracker.** We are a leech/seed, not announce target.
- **Not a private tracker client that violates rules.** Compliance with
  private-tracker norms (no DHT/PEX/LSD on private torrents, mandatory
  peer-id format, ratio reporting) is a hard requirement, not optional.
- **Not a torrent creator.** No `.torrent` generation in v1. (Trivial to
  add later via MonoTorrent; we just don't expose it.)
- **Not embedded WebTorrent.** Browser-based peers are out of scope.

## Sources Of Truth

- Spike findings: `spikes/Deluno.Spike.Nzb/README.md` (when rebuilt)
- Current download-client surface: `src/Deluno.Integrations/DownloadClients/`
- Existing architecture invariants: `docs/ARCHITECTURE.md`
- Agent map: `AGENTS.md`
- Reference implementations:
  - SABnzbd: <https://github.com/sabnzbd/sabnzbd>
  - NZBGet: <https://github.com/nzbgetcom/nzbget>
  - qBittorrent: <https://github.com/qbittorrent/qBittorrent>
  - libtorrent (C++ canonical ref): <https://github.com/arvidn/libtorrent>
  - MonoTorrent (our chosen .NET implementation): <https://github.com/alanmcgovern/monotorrent>
- Protocol specs:
  - yEnc draft 1.3: <https://www.yenc.org/yenc-draft.1.3.txt>
  - NNTP RFC 3977, RFC 4642 (TLS), RFC 4643 (AUTHINFO SASL)
  - BitTorrent: BEP-3 (core), BEP-5 (DHT), BEP-6 (fast ext), BEP-9
    (magnet), BEP-10 (extension protocol), BEP-11 (PEX), BEP-12
    (multitracker), BEP-15 (UDP trackers), BEP-19 (web seeds), BEP-23
    (compact peer lists), BEP-27 (private torrents), BEP-29 (uTP),
    BEP-52 (v2 / SHA-256). Index: <https://www.bittorrent.org/beps/bep_0000.html>

## Invariants To Rewrite

Two repo-wide invariants currently forbid what we are about to build. They
must be updated in the same PR that introduces the engine.

| File | Line | Current text | Replacement |
|---|---|---|---|
| `docs/ARCHITECTURE.md` | 98 | "Deluno orchestrates external indexers and download clients; it does not embed a downloader." | "Deluno orchestrates external indexers and download clients. It also ships an optional in-process download engine (`Deluno.Downloader`) covering NZB (Usenet) and BitTorrent, which users can select per library instead of a remote SAB/NZBGet/qBittorrent/etc. The torrent protocol is implemented via MonoTorrent; the NZB protocol is implemented in-tree. Domain modules and Integrations must remain agnostic to which client is in use." |
| `AGENTS.md` | 28 | "Deluno orchestrates external indexers and download clients; it does not embed a downloader." | Same replacement. |

The `npm run validate:agents` script must be updated to enforce the new
text exactly, so we don't regress.

## Module Placement

```
src/
  Deluno.Downloader/                  ← new
    Engine/                           ← shared: orchestrator, lifecycle, queue
    Persistence/                      ← shared: SQLite schema + repos
    Settings/                         ← shared: engine + per-protocol config
    Extraction/                       ← shared: unrar/7z/zip (used by both)
    Postprocessing/                   ← shared: rename, flatten, sample filter
    Nzb/                              ← Usenet protocol implementation
      Nntp/                           ← NNTP client + connection pool
      Yenc/                           ← decoder
      Parser/                         ← NZB XML parser
      Par2/                           ← bundled-binary wrapper
      MultiServer/                    ← tier-walk failover
    Torrent/                          ← BitTorrent protocol implementation
      Engine/                         ← MonoTorrent wrapper + lifecycle adapter
      Trackers/                       ← announce/scrape config, ratio reporting
      Magnet/                         ← .torrent + magnet ingestion
      Network/                        ← UPnP/NAT-PMP, port forwarding, IP filter
    DependencyInjection/              ← AddDelunoBuiltInDownloaders()
  Deluno.Integrations/
    DownloadClients/
      Builtin/                        ← protocol adapters for "deluno-nzb" and "deluno-torrent"
```

### Project reference graph

- `Deluno.Downloader` references: `Deluno.Contracts`, `Deluno.Infrastructure`
  (SQLite), `Deluno.Jobs` (lifecycle integration only — engine raises events,
  doesn't depend on job execution).
- `Deluno.Downloader` **does not** reference `Deluno.Movies`, `Deluno.Series`,
  `Deluno.Filesystem`, `Deluno.Integrations`, `Deluno.Platform`. Same
  isolation rule as Integrations.
- `Deluno.Downloader.Torrent` references `MonoTorrent` NuGet (pinned exact
  version; see Decisions).
- `Deluno.Integrations.DownloadClients.Builtin` references
  `Deluno.Downloader` and adapts to the existing seams. There are two
  service interfaces today, not three:
  `IDownloadClientGrabService` (in `IDownloadClientGrabService.cs`) and
  `IDownloadClientTelemetryService` (in `IDownloadClientTelemetryService.cs`).
  Queue actions (pause/resume/delete) live as additional methods on
  `DownloadClientTelemetryService`, not as a separate interface. Our
  builtin adapter dispatches on protocol value `"deluno-nzb"` or
  `"deluno-torrent"` from inside each method's existing switch block.
- `Deluno.Host` and `Deluno.Worker` reference `Deluno.Downloader` to
  register hosted services (`AddDelunoBuiltInDownloaders()`).

### Why this shape

- Engine has zero domain knowledge — Movies/Series can't sneak into it.
- Integrations remains the single seam between Deluno and "any download
  client." Two protocols, two switch cases, same seam.
- The webhook layer (`DownloadClientWebhookService`) is bypassed for
  built-in; the engine raises `RecordDetectionAsync` /
  `RecordImportOutcomeAsync` directly on `IDownloadDispatchesRepository`.
- Shared lifecycle/persistence/extraction means torrent and NZB get
  identical post-completion handling — same import pipeline integration,
  same activity log shape, same SignalR events.

## Download Lifecycle State Machine

Every download is a `Job` row whose state advances through this machine,
**identical shape for both protocols** with one protocol-specific addition
(`Seeding` for torrents). Every transition is logged with timestamp + reason.
Every persisted state survives process restart.

```
                ┌──────────┐
                │ Queued   │  ← created on grab; waiting for engine slot
                └────┬─────┘
                     │ engine pulls highest-priority job
                ┌────▼─────┐
                │ Fetching │  ← NZB: articles in flight   Torrent: pieces in flight
                └────┬─────┘
                     │ all data attempted (success + permanent failures)
                ┌────▼─────────────┐
                │ Reassembled      │  ← bytes on disk in their final layout
                └────┬─────────────┘
                     │ protocol-specific verify
                ┌────▼─────────┐
        ┌──No───┤ Verify       ├──Yes──┐   NZB: par2 verify   Torrent: hash verify
        │       └──────────────┘       │
        │                              │   (torrent hash verify is normally
        │       ┌──────────────┐ pass  │    interleaved with Fetching; this
        │       │   Verified   ◄───────┴──── state catches force-recheck)
        │       └────┬─────────┘
        │            │ archive present?
        │            │                                ┌──────────────┐
        │       ┌────▼─────────┐                      │ Repair       │ ← NZB only
        │  ┌─No─┤ Extracting   │                      │ (par2)       │
        │  │    └────┬─────────┘                      └──┬───────────┘
        │  │         │ pass                              │ loop back to Verified
        │  │    ┌────▼─────────┐                         │ or fail
        │  │    │ Extracted    ◄─────────────────────────┘
        │  │    └────┬─────────┘
        │  │         │
        │  └─────────┤
        │            ▼
        │    ┌──────────────────┐
        └────► PostProcessed    │ ← rename, flatten, drop samples
             └────┬─────────────┘
                  │ raise import event on IDownloadDispatchesRepository
             ┌────▼─────────────┐
             │ ImportPending    │ ← Deluno heartbeat picks it up
             └────┬─────────────┘
                  │ Filesystem.ImportPipelineService completes
             ┌────▼──────────────────┐
             │ Done            ───┐  │
             │ (NZB: terminal)     ├──→ Torrent: enter Seeding (continue uploading)
             │                    │  │      Seeding has its own exit conditions:
             └────────────────────┘  │      ratio target, time limit, manual stop
                                     │      → on exit, return to Done (terminal)
                                     └─→ (NZB does not seed)

At every state, two failure transitions exist:
  → Failed (permanent — moved to history, never retried automatically)
  → Paused (user action — frozen, can resume)

From Failed: Retry (back to Queued, state reset) or Delete.
From Done (torrent in Seeding): Stop seeding manually.
```

### Failure classification (unified)

| Class | NZB examples | Torrent examples | Policy |
|---|---|---|---|
| **Transient** | network timeout, 503, TLS handshake fail, EOF mid-body | peer disconnect, tracker timeout, choked timeout | Retry with exponential backoff (1s, 2s, 4s, 8s, 16s, 32s; max 6 attempts). Then escalate (NZB → fill server; Torrent → DHT/PEX for new peers). |
| **Data-level permanent** | 430 article missing, CRC32 fail | piece hash fail | NZB: try next server. Torrent: re-request piece from different peer; ban peer after 3 bad pieces (MonoTorrent default). |
| **Auth-level permanent** | 481/482 auth fail | private tracker rejects passkey | Mark server/tracker unhealthy; pause dependent jobs; surface error. |
| **Throttle** | provider 502 throttle | tracker `min interval` exceeded | Reduce parallelism or pool size; back off announces. |
| **Unrecoverable** | disk full, par2 repair fails, all peers gone with <100% | same | Job → Failed. User must intervene. |

## Persistence Schema

Separate SQLite DB: `downloader.db`. Shared base tables + protocol-specific
extension tables.

```sql
-- Shared --------------------------------------------------------------------

CREATE TABLE jobs (
  id               TEXT PRIMARY KEY,
  protocol         TEXT NOT NULL CHECK (protocol IN ('nzb','torrent')),
  display_name     TEXT NOT NULL,
  source_path      TEXT NOT NULL,        -- on-disk copy of .nzb or .torrent
  source_kind      TEXT NOT NULL,        -- 'nzb' | 'torrent_file' | 'magnet'
  category         TEXT,
  priority         INTEGER NOT NULL DEFAULT 0,
  state            TEXT NOT NULL,        -- enum from state machine
  state_reason     TEXT,
  paused           INTEGER NOT NULL DEFAULT 0,
  password_protected TEXT,               -- archive password; "dp:v1:"-prefixed (via ISecretProtector)
  download_dir     TEXT NOT NULL,
  output_dir       TEXT,                 -- after post-processing
  total_bytes      INTEGER NOT NULL,
  downloaded_bytes INTEGER NOT NULL DEFAULT 0,
  uploaded_bytes   INTEGER NOT NULL DEFAULT 0,  -- torrents only; 0 for NZB
  dispatch_id      TEXT,
  library_id       TEXT,
  created_at       TEXT NOT NULL,
  updated_at       TEXT NOT NULL,
  completed_at     TEXT
);
-- Note on credential columns throughout this schema: all stored using
-- ISecretProtector (after Phase 0.5 hardening). Values are TEXT, prefixed
-- with "dp:v1:" or whatever the hardened protector emits. BLOB would
-- imply raw cipher bytes from a native API; we are not doing that.

CREATE TABLE files (
  id           TEXT PRIMARY KEY,
  job_id       TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  file_index   INTEGER NOT NULL,
  name         TEXT NOT NULL,
  is_par2      INTEGER NOT NULL DEFAULT 0,    -- NZB
  is_metadata  INTEGER NOT NULL DEFAULT 0,    -- torrent (e.g. .pad files)
  priority     TEXT NOT NULL DEFAULT 'normal',-- torrent file priority: skip|low|normal|high
  total_bytes  INTEGER NOT NULL,
  state        TEXT NOT NULL,
  output_path  TEXT
);

CREATE TABLE history (
  id               TEXT PRIMARY KEY,
  job_id           TEXT NOT NULL,
  protocol         TEXT NOT NULL,
  display_name     TEXT NOT NULL,
  category         TEXT,
  final_state      TEXT NOT NULL,
  total_bytes      INTEGER NOT NULL,
  downloaded_bytes INTEGER NOT NULL,
  uploaded_bytes   INTEGER NOT NULL,
  duration_ms      INTEGER NOT NULL,
  output_path      TEXT,
  failure_reason   TEXT,
  completed_at     TEXT NOT NULL,
  dedupe_key       TEXT
);

CREATE INDEX idx_jobs_state_priority ON jobs(state, priority);
CREATE INDEX idx_history_dedupe ON history(dedupe_key);
CREATE INDEX idx_history_completed ON history(completed_at);

-- NZB-specific --------------------------------------------------------------

CREATE TABLE nzb_servers (
  id                 TEXT PRIMARY KEY,
  name               TEXT NOT NULL,
  host               TEXT NOT NULL,
  port               INTEGER NOT NULL,
  use_tls            INTEGER NOT NULL,
  username_protected TEXT,   -- via ISecretProtector
  password_protected TEXT,   -- via ISecretProtector
  max_connections    INTEGER NOT NULL DEFAULT 8,
  priority           INTEGER NOT NULL DEFAULT 0,
  tier               TEXT NOT NULL CHECK (tier IN ('Primary','Backup','Fill')),
  retention_days     INTEGER,
  enabled            INTEGER NOT NULL DEFAULT 1,
  proxy_url_protected TEXT,  -- via ISecretProtector (whole URL incl. credentials)
  cert_pin_sha256    TEXT,
  created_at         TEXT NOT NULL,
  updated_at         TEXT NOT NULL
);

CREATE TABLE nzb_segments (
  id             TEXT PRIMARY KEY,
  file_id        TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
  number         INTEGER NOT NULL,
  bytes          INTEGER NOT NULL,
  message_id     TEXT NOT NULL,
  state          TEXT NOT NULL,
  attempts       INTEGER NOT NULL DEFAULT 0,
  last_server_id TEXT,
  last_error     TEXT,
  UNIQUE(file_id, number)
);

CREATE INDEX idx_nzb_segments_state ON nzb_segments(state);

CREATE TABLE nzb_server_stats (
  server_id      TEXT NOT NULL REFERENCES nzb_servers(id) ON DELETE CASCADE,
  window_start   TEXT NOT NULL,
  bytes          INTEGER NOT NULL DEFAULT 0,
  articles_ok    INTEGER NOT NULL DEFAULT 0,
  articles_404   INTEGER NOT NULL DEFAULT 0,
  errors         INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (server_id, window_start)
);

-- Torrent-specific ----------------------------------------------------------

CREATE TABLE torrent_metadata (
  job_id           TEXT PRIMARY KEY REFERENCES jobs(id) ON DELETE CASCADE,
  infohash_v1      TEXT,                  -- 40-char hex; nullable for pure v2
  infohash_v2      TEXT,                  -- 64-char hex; nullable for v1
  piece_length     INTEGER NOT NULL,
  piece_count      INTEGER NOT NULL,
  is_private       INTEGER NOT NULL DEFAULT 0,
  fast_resume_blob BLOB,                  -- MonoTorrent fast-resume snapshot
  comment          TEXT,
  created_by       TEXT,
  creation_date    TEXT
);

CREATE TABLE torrent_pieces (
  job_id        TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  piece_index   INTEGER NOT NULL,
  state         TEXT NOT NULL,            -- Pending | Downloading | Verified | Failed
  PRIMARY KEY (job_id, piece_index)
);
-- Note: piece-level state is mostly held in MonoTorrent's fast-resume blob;
-- this table only persists per-piece state when MonoTorrent gives us a
-- structured snapshot, used for UI display.

CREATE TABLE torrent_trackers (
  id            TEXT PRIMARY KEY,
  job_id        TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  tier          INTEGER NOT NULL,
  url           TEXT NOT NULL,
  status        TEXT NOT NULL,            -- Unknown | Working | Failure | Disabled
  last_announce TEXT,
  last_seeders  INTEGER,
  last_leechers INTEGER,
  last_message  TEXT
);

CREATE TABLE torrent_settings (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
-- Holds: listen_port, upnp_enabled, dht_enabled, lsd_enabled, pex_enabled,
-- max_global_connections, max_per_torrent_connections, max_upload_kbps,
-- max_download_kbps, ratio_target_default, seed_time_target_default,
-- private_torrent_overrides (disable DHT/PEX/LSD), peer_id_template.
```

Migrations live in `Deluno.Downloader.Persistence.Migrations`, follow the
same conventions as `Deluno.Integrations.Migrations`.

## NNTP Protocol Layer (NZB)

Spec for `Deluno.Downloader.Nzb.Nntp`. Builds on RFC 3977; named extensions
follow RFC numbers below.

### Connection lifecycle

```
[ConnectAsync]
  ├─ TcpClient connect (resolve A + AAAA, prefer per OS default)
  ├─ if TLS: SslStream + AuthenticateAsClient
  │           - TLS 1.2/1.3 only (no 1.0/1.1, no SSLv3)
  │           - ALPN: nothing (NNTP not in ALPN registry)
  │           - Hostname validation per RFC 6125
  │           - Optional cert pinning (SHA256 fingerprint in settings)
  ├─ Read server greeting (200/201; 502 = service unavailable)
  ├─ CAPABILITIES probe (best-effort, ignored if 500)
  ├─ if MODE READER present in CAPABILITIES or if server is INN-like:
  │     send MODE READER, expect 200/201
  ├─ if XFEATURE COMPRESS GZIP supported: enable (cuts overhead for headers,
  │     not body bytes)
  ├─ if username set:
  │     try AUTHINFO USER/PASS first (universal)
  │     fall back to AUTHINFO SASL PLAIN if server prefers (rare)
  └─ Record connection age start

[Lifetime management]
  - Forced reconnect after MaxConnectionAge (default 30 min — providers
    silently drop long-lived connections)
  - Idle disconnect after MaxIdleDuration (default 5 min)
  - Health check (DATE command) before reuse if idle > 60s

[DisposeAsync]
  - Send QUIT (best-effort), wait up to 1s for 205, then close.
```

### Commands implemented

| Command | Purpose | Notes |
|---|---|---|
| `CAPABILITIES` | Discover server features | Required for negotiation |
| `MODE READER` | Switch INN servers from feed to reader mode | Required by many backbones |
| `AUTHINFO USER/PASS` | Auth (universal) | First choice |
| `AUTHINFO SASL PLAIN` | SASL fallback | Required by some EU providers |
| `XFEATURE COMPRESS GZIP` | Stream compression | Helps header commands; body bytes already binary |
| `DATE` | Server time / liveness check | Used as keepalive |
| `STAT <msgid>` | Check article exists without download | Used by fill-server precheck (optional) |
| `BODY <msgid>` | Fetch article body only (no headers) | Primary command for binaries |
| `QUIT` | Close cleanly | |

Commands we do not implement and why: `GROUP/LISTGROUP` (binaries are
fetched by message-id, not group enumeration); `ARTICLE/HEAD` (BODY is
sufficient and saves bandwidth); `POST/IHAVE/TAKETHIS` (read-only).

### Response handling

Status lines: 3-digit code + space + ASCII text. Codes <400 = success/info,
4xx = transient, 5xx = permanent. Multi-line bodies terminated by `.` on
its own line, with `..` dot-stuffing inside the body. **Body reading is
byte-level** (no `StreamReader`/ASCII decode — yEnc payloads are 8-bit; the
spike caught this bug).

### Connection pool

Per-server pool with these properties:

- Bounded: `MaxConnections` per server (user configurable, hard ceiling
  per provider's terms — surface a warning if user exceeds it).
- Borrow/return semantics; borrowers that throw mark the connection bad.
- Bad connections are disposed, not returned.
- Idle connections retired after `MaxIdleDuration`.
- Age-based forced reconnect after `MaxConnectionAge`.
- Health check (`DATE` command) on borrow if idle > 60s.
- Per-connection counters surfaced to telemetry: bytes downloaded,
  articles fetched, age, idle time.

### Proxy support

Optional SOCKS5 and HTTP CONNECT, configured per server. Required for some
users (corporate networks, providers blocked by region). Implementation:
prefix `TcpClient` connect with the proxy handshake; `SslStream` then runs
over the tunneled socket.

### Security

- TLS 1.2/1.3 only; no opt-in to weaker.
- **TLS 1.3 0-RTT (early data) disabled** for NNTP `AUTHINFO` flows —
  early data is replayable and we never want a replayed AUTHINFO.
- Default cert validation (chain to system trust + hostname match per RFC
  6125).
- Optional cert pinning by SHA256 fingerprint.
- **Long-lived connection cert rotation**: providers frequently rotate
  TLS certs at 24h. `SslStream` does not gracefully renegotiate mid-
  connection. Mitigation: connection `MaxConnectionAge` is hard-bounded
  to 30 minutes (already required for the dead-connection problem;
  reinforced here).
- Credentials storage — **see the Cross-Platform Secrets Hardening
  prerequisite below.** TL;DR: the codebase today has
  `Deluno.Platform.Security.ISecretProtector` which wraps ASP.NET
  DataProtection. On Linux without explicit configuration, the master
  key is written **unprotected** to `~/.aspnet/DataProtection-Keys/`. That
  is not acceptable for downloader credentials (NNTP passwords, tracker
  passkeys, proxy passwords). Phase 0.5 builds proper backing stores per
  platform before any downloader credentials are persisted.
- Never log credentials. NNTP commands logged at trace level only, with
  `AUTHINFO PASS` redacted. Tracker announces logged with `passkey=`
  query-string parameter redacted.

#### Prerequisite: Cross-Platform Secrets Hardening (Phase 0.5)

This is platform work, not downloader work, but it gates the downloader
because credentials must not leak. The existing `ISecretProtector` API
shape is good (`Protect(purpose, plaintext) → string`,
`Unprotect(purpose, protected) → string?`). What needs to change is the
backing implementation: instead of (or in addition to) ASP.NET
DataProtection, wire platform-native secret stores:

| Platform | Backend | Notes |
|---|---|---|
| Windows | DPAPI via `ProtectedData.Protect` with `DataProtectionScope.LocalMachine` | Plus key persistence under `%LocalAppData%\DelunoData\secrets\`. |
| Linux | libsecret (`secret-tool`) via D-Bus | Requires `gnome-keyring` or `kwallet`. Docker / headless: see below. |
| macOS | Keychain via `SecKeychain*` P/Invoke | Per-app keychain entry. |
| Docker / headless Linux | File-encryption with `DELUNO_MASTER_KEY` env var (32 random bytes) | If env var absent, log a loud warning and refuse to start the downloader engine. |

Detection happens at startup; the chosen backend is exposed via
`/api/diagnostics/secrets-backend` for the UI. Migration from existing
DataProtection-protected secrets (if any) is handled by a one-time
re-protect pass on first start of the new implementation.

This work belongs in `src/Deluno.Platform/Security/` and is reviewed +
merged before downloader Phase 2 starts. **Estimated 2-4 weeks** of
focused work and is added to the phasing table below.

## Multi-Server Failover Policy (NZB)

The single most important difference between a toy implementation and SAB.

### Server tiers

Each server has: `Priority` (lower = higher), `Tier` (Primary | Backup |
Fill), `Enabled`, `RetentionDays` (optional, for opportunistic skip).

Article fetch algorithm per article:

```
for each tier in [Primary, Backup, Fill]:
  for each server in tier ordered by Priority:
    if server is healthy and article age <= server.RetentionDays:
      try BODY on a borrowed connection from server's pool
      if 222: return body
      if 430/423: try next server (article missing on this backbone)
      if transient: retry once on this server with backoff, then try next
      if auth/permanent: mark server unhealthy, skip
if all servers exhausted: mark article missing
```

The critical property: a 430 on one server is **not** a job failure. It's
just "try the next server." Most large downloads succeed only because
backbones differ (Highwinds vs UseNetExpress vs Omicron etc.) and the
union of their retention covers the article.

### Server health tracking

A server is `Unhealthy` if any of:

- Last 10 connection attempts all failed.
- Last 50 BODY requests all returned auth-level errors.
- Manual disable via UI.

Unhealthy servers are retried every 5 minutes (configurable) before being
re-enabled.

### Per-server throttling

If a server returns >50% transient errors within a sliding 60s window,
reduce its pool to half until errors subside. Surface as a warning.

## yEnc Decoder (NZB)

Requirements:

- yEnc draft 1.3 full compliance (=ybegin, =ypart, =yend; size + CRC32
  validation per part and per whole).
- Handle multi-part articles (=ypart begin=N end=M) and single-part.
- CRC32: IEEE polynomial via `System.IO.Hashing.Crc32`.
- **8-bit byte handling everywhere** — no string conversions in the hot
  path. Caught as a bug in the spike.
- Recovery from malformed input: a truncated escape at end-of-line should
  not crash the decoder; log a warning and treat the article as corrupt
  (will retry, then par2 will repair if possible).
- Pathological input: a line whose decoded prefix happens to spell `=yend`
  is theoretically possible. Mitigation: also require `=yend` to appear
  near the article's declared byte count.

Out of scope: yEnc 2.0 (rare, add if a real article needs it).

## NZB Parser

Requirements:

- NZB 1.1 spec compliance (`http://www.newzbin.com/DTD/2003/nzb`).
- Tolerate documents without the canonical namespace.
- Parse `<file>/<groups>/<segments>` with poster, subject, date.
- Sort segments by `number` attribute for deterministic reassembly.
- Classify par2 files by subject (case-insensitive `.par2`).
- Extract filename from subject using the double-quoted-filename convention.
- Parse `<meta>` block for `password`, `category`, `name` if present.
- Extract password from filename `{{password}}` convention as fallback.
- Deduplicate segments by message-id within a file.
- Tolerate malformed segments (missing bytes attribute, empty msgid):
  skip with a warning, par2 will recover.
- Fuzz-tested with `SharpFuzz` against a corpus of real-world NZBs.

## par2 Integration (NZB)

**Do not reimplement par2 in C#.** Two production implementations exist
(`par2cmdline-turbo`, `parpar`), both license-compatible. Ship the binary.

### Bundling

Decision: ship `par2cmdline-turbo` on every platform.

| Platform | Binary | Distribution |
|---|---|---|
| Windows | `par2.exe` from `par2cmdline-turbo` Windows build | Velopack installer payload, `tools/par2/win-x64/` |
| Linux (amd64) | `par2` from `par2cmdline-turbo` | Dockerfile `apt-get install` (Debian 13+ ships it) or bundled in `tools/par2/linux-x64/` |
| Linux (arm64) | same | Dockerfile architecture-aware install or bundled `tools/par2/linux-arm64/` |
| macOS | `par2` from `par2cmdline-turbo` | Static universal binary in app bundle `tools/par2/osx/` |

Path resolution: user-configured path → `PATH` → bundled fallback.
Surface health in UI ("par2 binary: bundled v1.2.3 / system v1.1.0 / not
found").

### GPLv2 distribution compliance

`par2cmdline-turbo` is licensed under GPLv2. We ship the **binary only**
(no static linking, no source-level integration). GPLv2 §3 still
requires that we make corresponding source available to any recipient
of the binary. Concretely:

- Include `tools/par2/COPYING` in the installer payload (the upstream
  GPLv2 text).
- Include a `tools/par2/SOURCE.md` file with: upstream URL, exact tag /
  commit hash, build instructions, and a written offer to provide
  source on request via a documented email address.
- Mention par2cmdline-turbo and link to its repo in the application's
  `NOTICE` file and in the About dialog.
- The Velopack installer manifest names the GPLv2 component so EULA
  reviews can find it.

This is paperwork, not engineering, but it must land with the binary in
the same release — not later. Owner: whoever ships the installer change.

### Invocation

```
par2 verify <basename>.par2
  → exit 0: ok
  → exit 1: needs repair, recoverable
  → exit 2: needs repair, not recoverable
  → exit 3: missing files

par2 repair <basename>.par2
  → exit 0: repaired
  → exit non-0: failed
```

Wrapped by `IPar2Service` with progress parsing (par2cmdline-turbo emits
parseable progress). Job state → `Repair`/`Verified` per the lifecycle.

### Failure paths

- par2 binary missing: warn user, job → Failed with actionable message.
- All par2 sets corrupt: job → Failed.
- Repair fails due to insufficient recovery blocks: job → Failed; user
  may retry to grab missing articles from additional fill servers.

## BitTorrent Protocol Layer (Torrent)

Spec for `Deluno.Downloader.Torrent`. The protocol itself is implemented by
**MonoTorrent**; our code is a thin, opinionated wrapper that:

- Adapts MonoTorrent's event model into our `Job` lifecycle.
- Persists state (job rows, fast-resume blobs, tracker stats).
- Enforces our policy decisions (private-torrent compliance, ratio
  targets, scheduler, integration with the shared post-processing
  pipeline).
- Exposes a stable internal API (`ITorrentEngine`) so the rest of Deluno
  never imports MonoTorrent types directly. This is what lets us swap
  implementations later without rewriting callers.

### MonoTorrent integration contract

```csharp
public interface ITorrentEngine : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task<TorrentJobHandle> AddAsync(TorrentSource source, TorrentAddOptions opts, CancellationToken ct);
    Task PauseAsync(string jobId, CancellationToken ct);
    Task ResumeAsync(string jobId, CancellationToken ct);
    Task RemoveAsync(string jobId, bool deleteData, CancellationToken ct);
    Task ForceRecheckAsync(string jobId, CancellationToken ct);
    IAsyncEnumerable<TorrentEngineEvent> Events { get; }
}
```

`TorrentSource` is `MagnetLink | TorrentFilePath | TorrentBytes`.
`TorrentAddOptions` covers: `Category`, `DownloadDir`, `Priority`,
`FilePriorities`, `RatioTarget`, `SeedTimeTarget`, `IsPrivateOverride`.

### Features required at v1

| Feature | BEP | Required for | Notes |
|---|---|---|---|
| Core peer wire protocol | 3 | All torrents | MonoTorrent default |
| Fast extension | 6 | Performance | Default on |
| Extension protocol | 10 | DHT/PEX/uTP support | Default on |
| PEX (Peer Exchange) | 11 | Public swarm discovery | **Off for private torrents** |
| Multitracker | 12 | Fallback announcing | Required |
| UDP trackers | 15 | Modern trackers | Required |
| Magnet metadata exchange | 9 | Trackerless ingestion | Required (BEP-9 is the metadata extension; magnet URI is the address scheme) |
| Compact peer lists | 23 | Bandwidth | Default on |
| Private torrents | 27 | Private tracker compliance | **Critical — see below** |
| uTP | 29 | ISP throttling avoidance | Default on |
| Web seeds (HTTP/FTP) | 19 | Hybrid swarms | On |
| DHT (Mainline) | 5 | Trackerless discovery | **Off for private torrents** |
| Local Service Discovery | 14 | LAN peers | **Off for private torrents** |
| BitTorrent v2 (SHA-256) | 52 | Newer trackers | Per MonoTorrent v3.0+ — full hybrid + pure-v2 + v2 magnet links supported. |
| MSE/PE peer encryption | (de-facto) | ISP / firewall traversal | On, mode `Encrypted_RC4_full` allowed but plaintext fallback unless private tracker forbids |

### Private tracker compliance

Private trackers ban for many subtle reasons beyond the obvious "no
DHT." This section enumerates every requirement; **all are enforced at
announce/connection time, not at config time, so a misconfigured public-
torrent global setting can never leak.**

Hard requirements when `is_private = 1` in the torrent metadata:

1. **DHT off, PEX off, LSD off.** Hard-disable. Any contribution of peer
   info to public networks is bannable.
2. **`event=stopped` announce on pause / app shutdown.** Failing to send
   `stopped` makes the tracker think you've abandoned the torrent (or
   are cheating ratio); many trackers HnR-flag stale clients. Best-
   effort with a 5s timeout, but always sent.
3. **Passkey preservation.** Tracker URLs frequently contain a passkey
   in the path (`/announce.php?passkey=...`) or as a query parameter.
   The passkey **must be re-sent verbatim on every re-announce, including
   after tracker URL rewrites** (some trackers send `301`/`307` to
   migrated URLs; the engine must follow and keep the passkey).
4. **Upload-byte precision.** Trackers police announce arithmetic.
   `downloaded=` / `uploaded=` / `left=` are bytes (not KiB or KB).
   Off-by-1024 errors get accounts banned for "credit cheating." Use
   `long` everywhere; never multiply or divide.
5. **HTTPS-downgrade protection.** If the tracker URL is `https://`,
   never fall back to `http://`. A man-in-the-middle could otherwise
   harvest the passkey. Treat HTTPS failure as a transient error.
6. **Single-IP enforcement.** Most private trackers ban for
   simultaneous connections from multiple source IPs (proxy + direct,
   IPv4 + IPv6 split). If user has multiple network interfaces, the
   engine picks one and binds outgoing sockets to it. Surface in UI:
   "Outbound IP for tracker announces: 198.51.100.42 — change?"
7. **Peer-ID template matches client identity.** User-configurable
   `peer_id_template` (default mimics current qBittorrent stable;
   parameterized, not hardcoded).
8. **User-Agent allowlist conformance.** Most private trackers require
   `User-Agent: qBittorrent/x.y.z` or similar. Engine offers per-tracker
   UA override; default UA identifies as Deluno (most public trackers
   accept this; private trackers will reject it, so users opt-in to
   client masquerade per-tracker).
9. **Mandatory `key=` parameter** on announces (random per-torrent,
   stable across re-announces, regenerated only on torrent removal+re-add).
10. **`compact=1` and `no_peer_id=1`** sent on announces (saves bandwidth
    and matches qBit/uTorrent behavior the tracker expects).
11. **Honor `min interval`** strictly; never announce more often. Even
    on force-reannounce, if `min interval` hasn't elapsed, refuse and
    surface "rate-limited" in UI.
12. **Encrypted peer connections only** if tracker demands; otherwise
    plaintext allowed but encryption preferred (configurable per-tracker).
13. **No client-version downgrade** (some trackers ban for using
    client versions with known bugs; engine surfaces a warning if user
    sets a too-old `peer_id_template`).

### Magnet handling for potentially-private torrents

A magnet URI does **not** carry the `private=1` flag — that lives only
inside the .torrent's `info` dict. Between "magnet added" and "metadata
downloaded via BEP-9," DHT/PEX must be used to fetch the metadata. If
the torrent turns out to be private, **the infohash has already leaked
to the public DHT** — and the user may already be banned by the time
they see the first announce.

Mitigation:

- When a magnet is added, prompt the user (or auto-detect from category)
  whether this is destined for a private tracker.
- If private-suspect: **prefer tracker-only metadata fetch** (BEP-9 over
  peer connections opened only through the trackers listed in the magnet),
  with DHT/PEX strictly disabled for that metadata-fetch phase.
- If tracker-only fetch fails after N seconds, surface a clear modal:
  "Metadata fetch requires DHT/PEX, which would leak this torrent's
  infohash to public networks. Continue (may risk tracker ban) or
  cancel?"
- Default: cancel. Users must explicitly opt-in.

Magnets for already-known-public torrents (e.g. category = "public"
library, or magnet contains only public trackers in `tr=`) skip this
guard and use the normal path.

### Listen port + NAT traversal

- User-configurable listen port (default random 49152-65535 per install).
- UPnP and NAT-PMP via MonoTorrent's built-in support; toggleable.
- Surface listen-port reachability check in UI (queries an external probe).
- Optional fixed port for users behind manually-configured port forwards.

### IP filter

- Optional ipfilter.dat (PeerGuardian/eMule format) blocklist.
- Optional country block (via MaxMind GeoLite2 — requires user license).
- Whitelist/blacklist override per private tracker if MonoTorrent supports it.

### Hash verification

- Per-piece SHA1 (v1) / SHA-256 (v2) verification on receive (MonoTorrent
  built-in).
- Force-recheck: walk all on-disk pieces, mark verified or pending.
- Bad piece → re-request from a different peer; ban peer after 3 bad
  pieces (MonoTorrent default; configurable).

### Seeding lifecycle

After download completes:

1. Verify state machine → `Verified` → `Extracted` (if archive present)
   → `PostProcessed` → `ImportPending` → `Done`.
2. On entering `Done`, transition to `Seeding` state automatically (unless
   user disabled seeding for this job).
3. Seeding continues until one of:
   - **Ratio target** reached (per-job or global default).
   - **Seed time** target reached.
   - User stops manually.
   - Tracker disappears (all trackers dead, no DHT/PEX).
4. On exit from `Seeding`, transition back to `Done` (terminal). Optionally
   move data out of the seeding-served dir (configurable).

**For private trackers, seeding is mandatory until the user-configured
ratio or seed-time target is met** — Deluno will surface a warning if a
user tries to stop seeding too early.

### Bandwidth and connection limits

- Global max upload kB/s, global max download kB/s.
- Per-torrent overrides.
- Per-tracker overrides (some private trackers cap per-peer rates).
- Global max connections, per-torrent max connections.
- Scheduler (time-of-week table for cap overrides).
- Alternative-rate-limit mode toggle (qBit-style).

### .torrent ingestion

- Parse bencode via MonoTorrent.
- Compute infohash v1/v2 + hybrid.
- Validate piece length sanity, file list, tracker list.
- Dedupe by infohash against `history` and active `jobs`.

## Trackers (Torrent)

### Announce / scrape lifecycle

- On `add`: announce `event=started` to all trackers in tier order
  (multitracker per BEP-12: try tier 0 trackers in random order; only fall
  through to tier 1 if all tier 0 fail).
- Periodic re-announce per tracker's `interval` (never below `min interval`).
- On `pause`: announce `event=stopped`.
- On `complete`: announce `event=completed`.
- Scrape (lightweight stats query) on a slower cadence (every 15 min for
  active torrents) to update seeders/leechers counts for UI.

### Persistence

Per-tracker rows in `torrent_trackers` with `status`, `last_announce`,
`last_seeders`, `last_leechers`, `last_message`. Surface to user in
"Trackers" tab.

### Manual tracker management

- Add/remove trackers per job (user UI).
- Force re-announce button.
- Tier reordering.

## Shared: Orchestrator

The layer that was wrong in the spike. The production design is:

### Global priority queue

**Not** per-job workers. A single priority-ordered scheduler dispatches
work units (NZB articles or torrent piece-requests) across all active jobs.

For NZB this means: a channel of `(NzbJob, NzbFile, NzbSegment)` tuples
drained by NNTP workers. For torrent this is handled inside MonoTorrent's
own scheduler — our wrapper enforces priority by adjusting MonoTorrent's
per-torrent `priority` and starting/pausing torrents to enforce job-level
priority. We do **not** try to reorder MonoTorrent's piece selection.

### Streaming writes (NZB)

For each NzbFile:

1. On first scheduled article, open the output file with
   `FileStream(FileMode.Create, FileAccess.Write, FileShare.Read)` and
   `SetLength(totalDeclaredSize)` to pre-allocate.
2. Per-file write lock.
3. As each worker decodes an article: take lock, `Seek(article.PartBegin - 1)`,
   `Write(article.Payload)`, release lock, **null the Payload reference
   immediately** so the GC can reclaim.
4. When all articles for the file are accounted for (success or permanent
   failure): close the FileStream.

For torrent, MonoTorrent handles disk writes (its own threading model).
We configure MonoTorrent's `DiskWriter` to use streaming writes with
pre-allocation (`FileAllocationMode = Sparse` or `Preallocate` per user
setting).

### Bounded in-flight bytes

Global cap on bytes currently in memory awaiting write (default 256 MB).
Workers block when the cap is hit. Prevents a slow disk causing memory
blow-up. For torrent side, configured via MonoTorrent's
`MaximumDiskWriteBufferSize`.

### Cancellation

`CancellationToken` propagates through every async path. User-pause
snapshots the queue, cancels in-flight work, returns connections to pool.
Resume re-enqueues unfetched articles (NZB) or unpauses MonoTorrent
managers (torrent).

## Shared: Extraction

Used by both protocols (torrents also ship RARs).

| Format | Library | Notes |
|---|---|---|
| RAR3 + RAR5 | Bundled `UnRAR.exe` / `unrar` binary | Official binary from rarlab. Extraction-only use is license-clean; document in `NOTICE`. Do NOT use UnRAR source to build a competing archiver. |
| 7z | `SharpCompress` NuGet (managed, BSD) | |
| Zip | `System.IO.Compression` or `SharpCompress` | |
| Tar | `SharpCompress` | |
| Multi-volume RAR | `unrar` handles natively | Detect `.part1.rar` / `.r00` patterns |

### Password handling

- Password from NZB `<meta password="...">` block.
- Password from filename `{name{{pass}}.ext}` convention.
- Password from `password.txt` inside the archive.
- User-supplied password via job UI (if all above fail).
- Encrypted-header RARs (`-hp`) — handle with password prompt.

### Sample / proof filtering

Detect files matching `*sample*`, `*proof*`, `*screens*` patterns and
discard. Configurable per category.

## Settings Surface

Two pages in the existing Settings area, plus extensions to the existing
Download Clients page.

### Settings → Download Clients (extend existing)

- "Add Download Client" picker gains:
  - **Deluno NZB (built-in)** → protocol `"deluno-nzb"`
  - **Deluno Torrent (built-in)** → protocol `"deluno-torrent"`
- Selecting either: no host/port/API-key form. Single "Configure built-in
  engine →" link to the relevant page below, plus standard
  category/path/library-routing fields.
- Multiple "built-in" rows allowed (e.g. one per library) but they share
  the same underlying engine instance.

### Settings → Built-in NZB Engine (new)

- **News servers** (CRUD list)
  - Name, host, port, TLS, username, password, max connections, priority,
    tier, retention days, proxy, cert pin.
  - Test-connection button (uses `CAPABILITIES` + `DATE`).
- **Download paths** (incomplete dir, complete dir, per-category).
- **Post-processing** (par2 verify/repair toggles, extraction toggle,
  sample filter patterns, cleanup policy).
- **Limits** (global speed limit, per-server speed limits, scheduler,
  bounded in-flight bytes).
- **Diagnostics** (par2 binary status, connection pool view, server health).

### Settings → Built-in Torrent Engine (new)

- **Trackers and identity**
  - Default peer-ID template (with per-tracker overrides).
  - Default User-Agent.
  - Listen port (or random per-install).
- **Network**
  - UPnP / NAT-PMP toggle.
  - DHT / PEX / LSD toggles (master switches; auto-disabled per private
    torrent regardless of setting).
  - MSE/PE encryption mode (Enabled / Forced / Disabled).
  - IP filter (ipfilter.dat path + country blocklist).
  - Proxy (SOCKS5 for tracker + peer connections).
- **Limits**
  - Max global / per-torrent connections.
  - Max upload / download kB/s (global + alt mode + scheduler).
- **Seeding policy**
  - Default ratio target (e.g. 2.0, or unlimited).
  - Default seed-time target (e.g. 168 hours = 1 week).
  - Per-category overrides.
  - Stop-seeding action (just stop / move data / delete).
  - **Private torrents always seed until configured target met.**
- **File allocation** (Sparse / Preallocate).
- **Diagnostics** (listen-port reachability, DHT node count,
  per-torrent peer view).

## Integration With Existing Deluno

Touched files:

| File | Change |
|---|---|
| `src/Deluno.Integrations/DownloadClients/DownloadClientGrabService.cs:163` | Add `"deluno-nzb"` and `"deluno-torrent"` cases dispatching to `IBuiltinGrabAdapter`. |
| `src/Deluno.Integrations/DownloadClients/DownloadClientTelemetryService.cs:162` | Add both protocol cases to the `ExecuteActionAsync` switch (pause/resume/delete; these are methods on the same service, not a separate `IDownloadClientActionService`). |
| `src/Deluno.Integrations/DownloadClients/DownloadClientTelemetryService.cs:186` | Add both protocol cases to the `GetLiveSnapshotCoreAsync` switch (queue/history telemetry). |
| `src/Deluno.Platform/PlatformEndpointRouteBuilderExtensions.cs:1528-1679` | CRUD endpoints; add validation that built-in protocols don't accept host/port/api-key on create/edit. |
| `src/Deluno.Platform/PlatformEndpointRouteBuilderExtensions.cs:2982` | Test-connection branches for both protocols return OK iff respective engine is healthy. |
| Frontend `client-protocols.ts` (or equivalent) | Add `deluno-nzb` and `deluno-torrent` with labels/icons. |
| `tests/Deluno.Persistence.Tests/Integrations/DownloadClientTelemetryProfilesTests.cs` | Extend with profile assertions for both protocols (this is where profile coverage is enforced; it is a test file, not a `src/` registration). |

### Webhook path

Bypassed for both built-in protocols. Engine has in-process access to
`IDownloadDispatchesRepository` and calls `RecordDetectionAsync` /
`RecordImportOutcomeAsync` directly when jobs transition.

### Dispatch correlation

When Deluno grabs and routes to a built-in protocol, it passes the
`DispatchId`. Engine stores `dispatch_id` on the job and propagates it
through every event. Same shape for both protocols.

### Grab semantics

| Protocol | Grab input | Engine action |
|---|---|---|
| `deluno-nzb` | URL from indexer (Newznab `enclosure`) | Engine downloads .nzb bytes, parses, queues |
| `deluno-torrent` | URL from indexer (Torznab `enclosure`) **or** magnet URI | Engine downloads .torrent bytes (or parses magnet) and queues |

Critical change from current behaviour: for NZB, **Deluno currently hands
the URL to SAB and SAB downloads the .nzb**. For built-in we have to
download the .nzb ourselves. The grab adapter does this fetch (with auth
to the indexer if needed) and passes the bytes to the engine. Same for
torrent .torrent files.

## Observability

- **Structured logging** via `ILogger`:
  - `Deluno.Downloader.Engine`
  - `Deluno.Downloader.Nzb.Nntp.{server-name}`
  - `Deluno.Downloader.Nzb.Par2`
  - `Deluno.Downloader.Torrent.Engine`
  - `Deluno.Downloader.Torrent.Trackers.{tracker-host}`
  - `Deluno.Downloader.Extraction`
- **Metrics** via `System.Diagnostics.Metrics`:
  - `downloader.jobs.active` / `.queued` (gauge, tagged by protocol)
  - `downloader.bytes.downloaded` / `.uploaded` (counter, tagged by protocol)
  - `downloader.nzb.articles.fetched` / `.missing` (counter)
  - `downloader.nzb.par2.repairs` (counter)
  - `downloader.nzb.server.{id}.{bytes,errors}` (counter, tagged)
  - `downloader.torrent.peers.connected` (gauge)
  - `downloader.torrent.pieces.verified` / `.failed` (counter)
  - `downloader.torrent.tracker.{host}.announces` (counter, tagged)
- **SignalR events**:
  - `BuiltinEngineJobChanged`
  - `BuiltinEngineQueueChanged`
  - `BuiltinNzbServerHealthChanged`
  - `BuiltinTorrentTrackerChanged`
  - `BuiltinTorrentPeerStatsChanged` (throttled, ~1/sec)

## Testing Strategy

### Layer 1: unit tests (per module)

NZB:
- Parser, decoder, NNTP against `FakeNntpServer` (loopback `TcpListener`),
  state machine, persistence — as already detailed.

Torrent:
- `ITorrentEngine` contract tests using MonoTorrent's in-process test
  surface (verify public API; if internal-only, build our own harness —
  see V3 below).
- Magnet parsing, .torrent parsing, infohash computation.
- Tracker URL building, peer-ID/User-Agent overrides.
- Private-torrent compliance: assert DHT/PEX/LSD off when `is_private=1`,
  regardless of global settings.
- Persistence: fast-resume round-trip, tracker stats.

### Layer 2: fuzz tests

- `SharpFuzz` on NZB parser, yEnc decoder, bencode parser (via MonoTorrent
  surface).
- Corpus: real-world NZBs + .torrent files (anonymized).
- Nightly CI run with 10-min budget.

### Layer 3: reference compliance

NZB:
- Capture 1000+ real yEnc articles from a live provider; decode with us
  AND with SAB; byte-compare. Any divergence = bug in us.
- Parse 100+ real NZBs with us and with SAB; compare extracted file lists
  + segment lists + classifications.

Torrent:
- Parse 100+ real .torrent files with us and with qBittorrent's libtorrent
  layer; compare extracted file lists, infohashes, tracker lists, piece
  layouts.
- Magnet resolution: subset of 20 well-seeded torrents, resolve metadata
  with us and with qBittorrent within 5 minutes; assert success.

### Layer 4: integration tests against real providers / swarms

NZB:
- Configurable provider creds via env vars; CI job runs against a
  dedicated test account.
- Matrix: small (10 MB), medium (500 MB), large (5 GB), forced-incomplete
  (engineer in 1 missing article, expect par2 recovery), encrypted RAR,
  multi-volume RAR.
- Assert integrity (SHA256 vs. known-good), completion rate, throughput
  within 10% of SAB.

Torrent:
- Public well-seeded torrents (debian.org ISO list — guaranteed legal and
  always available).
- Matrix: single-file torrent, multi-file, magnet (no .torrent), webseed
  fallback.
- Assert hash verification, completion, ratio reaches 1.0 within
  reasonable time on a seeded swarm.
- Private-tracker compliance: dry-run announce against a mock private
  tracker that records announce parameters; assert no DHT/PEX/LSD traffic,
  correct peer-id template, correct min-interval honoring.

### Layer 5: scale + uptime

- 10,000-article NZB download and 50-torrent simultaneous swarm — both
  must complete without leaks.
- 7-day continuous-run test: enqueue 100 mixed jobs, verify no leaks
  (memory bounded), no degradation, fast-resume survives mid-run restart.

### Memory invariant test (hard CI gate)

- 5 GB synthetic NZB download (loopback fake server): peak working set
  must stay under 200 MB.
- 5 GB single-file torrent (in-process MonoTorrent harness with synthetic
  swarm): peak working set under 400 MB (torrents naturally hold more
  in-flight pieces).
- Regression fails the build.

## Phasing

**Revision 2 estimates** — based on review feedback that revision 1
under-budgeted both protocols by ~50% (qBit's 4.x release notes show
private-tracker compliance + fast-resume + tracker tier handling is
years of fixes; SAB has 15 years of NNTP edge-case work). Estimates
below reflect realistic effort, not optimistic.

The shared lifecycle/persistence layer lands first; NZB and Torrent
protocol implementations then run in **parallel** because they have
near-zero coupling.

| Phase | Weeks | Scope | Ships when |
|---|---|---|---|
| 0 | (done) | NZB spike validation | Spike validated approach; documented in spike README (when rebuilt). |
| **0.5** | **2-4** | **Cross-platform secrets hardening** (Platform-side prerequisite — see §Security). Build native DPAPI/libsecret/Keychain/file-encryption backends behind existing `ISecretProtector` API. | Diagnostics endpoint reports correct backend per platform. Master key never appears on disk unprotected. |
| 1 | 1-2 | Architecture doc review + invariant rewrite + project scaffolding + `validate:agents` text-pinning script extension | This doc approved, project boundaries enforced, invariant text pinned. |
| 2 | 4-5 | **Shared**: persistence schema + repositories, lifecycle state machine (including mid-flight crash semantics), integration seam adapters, extraction module, post-processing module, settings shell, SQLite WAL tuning. | All layer-1 shared unit tests pass. State machine + persistence are protocol-agnostic. Restart-resume integration test passes. |
| 3a | **10-14** | **NZB**: NNTP layer (full spec) + connection pool + multi-server failover + yEnc + parser + orchestrator with streaming writes + global priority queue + happy-eyeballs DNS + XFEATURE-quirk handling + per-server throttle detection. | Layer-1 NZB unit tests pass. Memory invariant test passes (5 GB NZB → <200 MB). Provider-quirk tests pass against ≥3 real providers. |
| 3b | **8-12** | **Torrent**: MonoTorrent wrapper + magnet/.torrent ingestion + trackers + **all 13 private-torrent compliance requirements** + magnet-leak-window handling + persistence adapter + fast-resume + UPnP/NAT-PMP + IP filter + scheduler. | Layer-1 torrent unit tests pass. Private-tracker compliance suite passes (mock private tracker records every announce parameter; assertions on each requirement). |
| 4 | 2-3 | **NZB**: par2 binary bundling + integration + repair flow + GPLv2 distribution paperwork. | par2 integration tests pass with seeded missing segments. Installer includes COPYING + SOURCE.md + offer. |
| 5 | 2-3 | **Integration seam**: protocol `"deluno-nzb"` and `"deluno-torrent"` wired in switch blocks, webhook bypass, dispatch correlation. | End-to-end: grab in Deluno → built-in engine → import pipeline. |
| 6 | 3-4 | **Settings UI**: both engine pages + download-clients picker extension + diagnostics (secrets-backend status, par2/unrar binary status, listen-port reachability, DHT node count). | UI complete; user can switch from SAB/qBit to built-in entirely via UI. |
| 7 | 4-5 | **Reference compliance + real-provider/real-swarm testing** (public-domain corpus: Debian ISOs for torrent, anonymized public-test articles for NZB). CI secrets management for live credentials. | Layer 3 + 4 + 5 all green for both protocols. Reference parity within 10%. |
| 8 | 1-2 | **Docs** (user-facing), release-note draft, migration guide. | Documentation complete; merged. |

Phases 3a + 3b run in parallel after Phase 2 lands. Phase 0.5 is a
serial prerequisite (work cannot start without it).

**Totals** (consistent across this section):

- **One engineer, 3a+3b in parallel as designed**:
  Σ(0.5 + 1 + 2 + max(3a, 3b) + 4 + 5 + 6 + 7 + 8) = **30-44 weeks (~7-10 months)**.
- **One engineer, 3a then 3b serialized**: add max(3b) since one person
  can't parallelize themselves → **37-54 weeks (~9-13 months)**.
- **Two engineers, one per protocol after Phase 2**: ~**24-32 weeks
  (~6-8 months)** calendar, since Phases 4-8 still serialize.

The 30-44 figure is the right one to plan against if you intend to ship
this with focused dev time. Revision 1's 22-32 estimate
under-budgeted phases 3a and 3b by ~50%; reviewer correctly identified
this. The bigger number is the honest one.

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| par2 binary GPLv2 distribution non-compliance | Low | High | Ship COPYING + SOURCE.md + written offer in installer; mention in NOTICE + About. See §par2 GPLv2 distribution compliance. |
| UnRAR license challenge | Low | Medium | Extraction-only is permitted. We don't link source; we ship official binary. Document in `NOTICE`. |
| **MonoTorrent maintainer bus factor** | **High** | High | Last release Aug 2024 (21 months ago as of 2026-05-25). Pin to exact version; vendor-fork preflight in CI (compile from source); commit to a 12-month re-evaluation; have a maintained fork plan ready. **Bumped from Medium → High based on release-cadence audit.** |
| **Private tracker compliance bug → user banned** | Medium | High | Compliance unit tests asserting every requirement in §Private tracker compliance against a mock private tracker. Manual audit by a private-tracker user before v1 release. Magnet-to-metadata leak window explicitly handled (see §Magnet handling). |
| NNTP edge cases in long-tail providers | High | Medium | Capability probing + provider overrides. Real-provider integration tests against multiple providers. |
| Memory blow-up under load | Medium | High | Memory invariant tests as CI gate. Streaming writes designed in from day one. Bounded in-flight bytes. |
| par2 / unrar binary missing on user machine | Medium | High | Bundle in installer + Docker image. Health check surfaced in UI. Actionable error. |
| Provider rate-limit / connection cap exceeded (NZB) | Medium | Medium | Per-server throttle detection. Surface warning when user exceeds documented cap. |
| Tracker abuse complaint (torrent) | Low | High | Honor `min interval` strictly. No automated tracker scraping abuse. |
| ISP DMCA / regional legal exposure | High | Variable | Out of scope to mitigate at engine level. UI surfaces a one-time "use VPN/SOCKS proxy" suggestion for torrents. |
| Port-forwarding broken (torrent low connectivity) | Medium | Medium | UPnP/NAT-PMP attempts. Surface reachability check in UI. Fall back to outgoing-only. |
| MonoTorrent API churn between versions | Medium | Medium | Wrap behind `ITorrentEngine`; pin exact version (not major). Test against a future-version branch in nightly CI. |
| Cross-platform binary distribution complexity | High | Medium | Velopack for Windows; Dockerfile for containers; per-platform CI matrix. |
| Concurrent-write corruption on output files | Low | High | Per-file write lock for NZB; MonoTorrent owns its own disk thread safety. Stress test with deliberate write contention. |
| Process restart mid-download corrupts queue | Low | High | All state mutations in SQLite transactions. Fast-resume blob persisted on each MonoTorrent state change. Restart-resume integration test in CI. |
| SAB/qBit feature drift (we miss a feature users rely on) | Medium | Medium | Reference compliance harness catches divergence on parsers. User-survey early adopters for missing-feature reports. |
| **TLS 1.3 0-RTT replay of AUTHINFO** | Low | High | 0-RTT (early data) disabled for NNTP. Documented in §Security. |
| **Long-lived TLS cert rotation mid-connection** | Medium | Medium | `MaxConnectionAge` hard-bounded to 30 minutes; forces clean reconnect through new handshake before any provider 24h cert rotation can hit a live socket. |
| **NTP / clock skew breaking tracker announces** | Low | Medium | Surface system-clock skew in diagnostics; private trackers may reject announces with skewed time. Document `chrony`/`w32time` requirement. |
| **DNS happy-eyeballs missing in `TcpClient`** | Medium | Medium | `TcpClient.ConnectAsync(host, port)` resolves A+AAAA but doesn't race them per RFC 8305; on IPv6-broken networks first attempt eats a 30s timeout. Mitigation: custom connect using `Dns.GetHostAddressesAsync` + parallel race with 250ms head-start to IPv6, fall through to IPv4. |
| **SQLite WAL growth under heavy article-completion writes** | Medium | High | Explicit `PRAGMA wal_autocheckpoint=1000` + periodic `PRAGMA wal_checkpoint(TRUNCATE)` from a background timer. Without this, a single 5GB NZB download can produce a multi-GB WAL file that survives across restarts. |
| **Case-sensitivity collisions on Linux** | Low | Medium | When an NZB declares files differing only in case (Windows-authored archive → Linux disk), one overwrites the other. Detect on file-list build; warn + suffix-disambiguate. |
| **File-path length limits** | Medium | Medium | Windows MAX_PATH (260 chars without long-path opt-in) and Linux per-component 255-byte limit can both bite for deeply-nested torrent contents. Pre-validate and refuse-with-error before allocation; surface clear message. |
| **Disk-space pre-check before pre-allocate** | Medium | High | `FileStream.SetLength` for a 60GB sparse file can succeed even when there isn't 60GB free (sparse), but the actual writes will then fail mid-fetch. Mitigation: `DriveInfo.AvailableFreeSpace` check at job creation; refuse if insufficient + 10% headroom. |
| **AV / Defender rewriting `unrar.exe` headers mid-extraction** | Medium | Medium | Windows-only; Defender flags some extraction patterns as ransomware-adjacent. Mitigation: surface a clear error if `unrar.exe` exits with `STATUS_DLL_INIT_FAILED`; document `Add-MpPreference -ExclusionPath` for the bundled binary directory. |
| **Velopack update applied mid-fetch** | Low | High | Velopack restarts the process to apply updates. If it fires during an active fetch, jobs must resume cleanly. Mitigation: register a Velopack pre-update hook that snapshots queue state + cancels in-flight work; resume on next start picks up exactly where it stopped (covered by the restart-resume integration test). |
| **XFEATURE COMPRESS GZIP disconnects on some providers** | Medium | Low | Some providers (usenet.farm, cheapnews.eu) return 500 or even drop the connection on unrecognized X-features. Treat both 500 and connection-drop-after-XFEATURE as "feature unsupported"; never retry; remember per-server. |

## Open Questions

All initial open questions resolved on 2026-05-25 — see Decisions below.

**V1 (Cross-platform secrets)** — was a verification task in revision 1.
**Revision 2 reclassified it as a Phase 0.5 deliverable** because the
audit confirmed the existing `ISecretProtector` is an ASP.NET
DataProtection wrapper that does not meet our cross-platform threat
model (master key written unprotected to disk on Linux). New work, ~2-4
weeks, sequenced before Phase 2. See §Security / Cross-Platform Secrets
Hardening.

**V2 (BEP-52 verification)** — per the reviewer, MonoTorrent v3.0+
already ships full v1/hybrid/v2 BEP-52 support. The "partial support"
caveat is removed. The actual verification task is now narrower:
**confirm the specific tagged version we pin has the v3.0 v2 codepaths
enabled by default**, and document the exact MonoTorrent version
number in the dependency manifest + release notes.

**V3 (MonoTorrent test harness verification — NEW in revision 2)** —
The torrent unit-test plan in §Testing Strategy says we will use
"MonoTorrent's in-process test harness." Verify this is a public,
supported API surface (e.g. an `MonoTorrent.Testing` NuGet or an
ITorrentEngineHarness type) and not just internal test infrastructure
in MonoTorrent's own repo. If it's not publicly exposed, the entire
torrent unit-test plan needs a rewrite (build our own harness against
the public MonoTorrent API surface — bigger work, ~1-2 weeks added to
Phase 3b). Verify before Phase 3b kickoff, not during.

## Decisions

- 2026-05-25: Spike validated NZB approach; production rebuild greenlit.
- 2026-05-25: Single built-in engine instance, two protocols (NZB +
  Torrent), internal-only (no SAB/qBit API shim), MonoTorrent for torrent
  protocol wrapped behind `ITorrentEngine` (same pattern as bundled par2
  binary), unified architecture doc.
- 2026-05-25: **Credentials** — keep the `ISecretProtector` API surface
  in `Deluno.Platform.Security`, but **replace the backing implementation**
  with native DPAPI/libsecret/Keychain/file-encryption per platform via
  Phase 0.5 work. (Revision 1's "reuse `Deluno.Platform.SecretsService`"
  line is retracted — that named type does not exist; only
  `ISecretProtector` / `DataProtectionSecretProtector` do, and the
  DataProtection backend is not acceptable as-is for cross-platform
  credential storage. See §Security.)
- 2026-05-25: **Categories** — both. Engine stores freeform string
  (SAB/qBit convention); Integrations adapter maps to library_id for
  routing. Best of both worlds; no loss of compatibility, no loss of
  Deluno's library-routing precision.
- 2026-05-25: **Scheduler** — independent per protocol. Different
  bottlenecks (NNTP pool vs BitTorrent peer wire), different policies
  (multi-server failover vs piece selection). A shared scheduler would
  force awkward compromises in both.
- 2026-05-25: **par2 binary** — `par2cmdline-turbo`. GPLv2 binary, shipped
  as external (not statically linked → license-compatible), SIMD-optimized.
  Same distribution pattern as ffmpeg today (Velopack on Windows, apt in
  Dockerfile, Homebrew on macOS).
- 2026-05-25: **MonoTorrent version policy** — pin to tagged release.
  Predictable behavior, reproducible builds, deliberate upgrades. Vendor a
  fork only reactively (if upstream becomes unmaintained or we need an
  unreleased fix).
- 2026-05-25: **BEP-52 scope** — ship whatever the pinned MonoTorrent
  version supports; document the actual coverage in user-facing release
  notes. Most real-world torrents are still v1; we don't gate launch on
  full v2 parity. Verification deferred to Phase 1 (V2 above).
- 2026-05-25: **Country / IP blocking** — user-supplied MaxMind GeoLite2
  DB only. No licensing burden on Deluno. UI surfaces a one-line setup
  hint pointing at MaxMind's free-account signup. PeerGuardian-format
  `ipfilter.dat` blocklists also supported (no licensing concern).
- 2026-05-25: **Peer transport** — both TCP and uTP enabled by default
  (qBittorrent's standard). uTP helps with ISP throttling and NAT
  traversal; TCP for peers that don't speak uTP. MonoTorrent handles both.
- 2026-05-25 (revision 2): **Phasing realism** — total estimate revised
  22-32 → **30-44 weeks** for one engineer running 3a + 3b in parallel
  as designed (37-54 weeks if serialized; 24-32 weeks with two
  engineers). Reviewer correctly identified revision 1 under-budgeted
  Phase 3a NZB and Phase 3b Torrent by ~50%.
- 2026-05-25 (revision 2): **Private-tracker compliance scope** — the
  requirement list expanded from 7 items in revision 1 to 13 (added:
  `event=stopped` on pause/shutdown, passkey preservation through URL
  rewrites, byte-precision arithmetic, HTTPS-downgrade protection,
  single-IP enforcement, no-client-version-downgrade). All enforced at
  announce/connection time, not at config time.
- 2026-05-25 (revision 2): **Magnet → metadata leak window** — new
  handling subsection. Magnets for private-suspect categories prefer
  tracker-only metadata fetch; user must explicitly opt in to
  DHT/PEX-based fetch with a warning.
- 2026-05-25 (revision 2): **par2 binary** — recommitted to
  `par2cmdline-turbo` across all platforms (revision 1 table listed
  inconsistent choices for Windows). GPLv2 distribution paperwork
  (COPYING + SOURCE.md + offer + NOTICE) added to Phase 4 deliverables.

## Validation Notes

- Doc reviewed end-to-end by: independent critical reviewer agent
  (revision 1 → revision 2 fixes incorporated; revision 2 re-reviewed
  and approved with mechanical fixes applied).
- Invariant rewrite tests added to `validate:agents`: pending (Phase 1).
- Phase 1 exit criteria met: pending.

## Completion Criteria

- Every section above has been reviewed; "(pending)" items resolved.
- A user can install Deluno fresh, pick "Deluno NZB (built-in)" **or**
  "Deluno Torrent (built-in)" in Download Clients, configure servers /
  trackers, and complete a 5 GB download to their library:
  - For NZB: SHA256 parity with the same NZB downloaded via SAB,
    including par2 repair if needed.
  - For Torrent: SHA256 parity with the same torrent downloaded via
    qBittorrent, including seeding to ratio target.
- Reference compliance harness shows zero divergence on a 1000-article
  yEnc corpus and 100-torrent .torrent corpus.
- Memory invariant tests pass (5 GB NZB → <200 MB peak; 5 GB torrent →
  <400 MB peak).
- Private-torrent compliance test suite passes (zero DHT/PEX/LSD traffic
  on `is_private=1`).
- `AGENTS.md` and `docs/ARCHITECTURE.md` invariants updated and enforced
  by `validate:agents`.
- Migration guide published for users moving from SAB/qBit to built-in.
