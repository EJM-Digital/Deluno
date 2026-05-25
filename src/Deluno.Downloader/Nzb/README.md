# Nzb

NZB protocol implementation. Subfolders:

- `Nntp/` — RFC 3977 client, connection pool, multi-server tier walk,
  TLS hardening (TLS 1.2/1.3 only, 0-RTT disabled for AUTHINFO,
  cert pinning, 30-min `MaxConnectionAge` for cert rotation).
- `Yenc/` — yEnc 1.3 decoder, byte-level (no `StreamReader` — the
  spike caught this trap), CRC32 via `System.IO.Hashing.Crc32`,
  malformed-input recovery.
- `Parser/` — NZB 1.1 XML parser, namespace-tolerant, segment dedupe,
  `<meta>` block + `{{password}}` filename convention.
- `Par2/` — wrapper around bundled `par2cmdline-turbo` binary.
- `MultiServer/` — per-article tier walk: Primary → Backup → Fill,
  with per-server health and throttle detection.

Lands in Phase 3a (10-14 weeks). par2 in Phase 4.
