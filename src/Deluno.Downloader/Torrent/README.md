# Torrent

BitTorrent protocol via MonoTorrent (pinned tagged release). Our code
is a thin wrapper that adapts MonoTorrent's event model to our `Job`
lifecycle, persists state, and enforces policy (notably the 13-point
private-tracker compliance list and the magnet → metadata leak
mitigation).

Subfolders:

- `Engine/` — `ITorrentEngine` interface + MonoTorrent adapter. Stable
  internal API so the rest of Deluno never imports MonoTorrent types
  directly. Lets us swap implementations (or vendor a fork) without
  rewriting callers.
- `Trackers/` — announce / scrape lifecycle, BEP-12 multitracker
  semantics (tier 0 random order, fall through to tier 1 only if all
  tier-0 fail), passkey preservation across URL rewrites, per-tracker
  UA + peer-id overrides, `min interval` enforcement.
- `Magnet/` — magnet URI parsing, BEP-9 metadata fetch with
  private-suspect leak guard (tracker-only fetch when destination
  category is private; user opt-in for DHT/PEX fallback).
- `Network/` — UPnP / NAT-PMP toggle, listen-port reachability check,
  IP filter (ipfilter.dat + optional MaxMind GeoLite2 country
  blocklist supplied by user — no licensing burden on Deluno).

Lands in Phase 3b (8-12 weeks). Runs in parallel with Phase 3a NZB.
