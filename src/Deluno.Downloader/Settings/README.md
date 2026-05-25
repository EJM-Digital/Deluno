# Settings

Engine configuration model + per-protocol config (NZB news servers,
torrent listen port / DHT / PEX / LSD toggles, etc.). Shared shell;
protocol-specific subsections.

Mirrors the Settings UI surfaces described in the architecture doc:

- Built-in NZB Engine: news servers (CRUD with tier/priority/retention),
  download paths, post-processing, limits, diagnostics.
- Built-in Torrent Engine: trackers and identity, network, limits,
  seeding policy, file allocation, diagnostics.

Lands in Phase 2 (shell) + Phase 6 (UI bindings).
