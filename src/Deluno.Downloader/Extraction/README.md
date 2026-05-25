# Extraction

Shared archive extraction used by both NZB (post-par2-verify) and
Torrent (post-hash-verify, when archives are inside the torrent).

- RAR3 + RAR5: bundled `UnRAR.exe` / `unrar` binary (extraction-only
  is license-clean; document in `NOTICE`).
- 7z: `SharpCompress` NuGet (BSD).
- Zip: `System.IO.Compression` or `SharpCompress`.
- Tar: `SharpCompress`.
- Multi-volume RAR detection (`.part1.rar` / `.r00` patterns).
- Password handling: `<meta password>` from NZB → `{{password}}`
  filename convention → `password.txt` inside archive → user-supplied.

Lands in Phase 2.
