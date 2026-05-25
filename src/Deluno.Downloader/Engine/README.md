# Engine

Shared cross-protocol orchestration. Per `docs/exec-plans/active/builtin-downloader-architecture.md`:

- Global priority queue (NOT per-job workers) that drains across all
  active jobs. Lands in Phase 2.
- Lifecycle state machine: Queued → Fetching → Reassembled → Verify →
  Verified → Extracting → Extracted → PostProcessed → ImportPending →
  Done, with a Torrent-specific Seeding branch off Done.
- Bounded in-flight bytes (default 256 MB) — backpressure on slow
  disk.
- Cancellation propagation through every async path; pause/resume
  serialization.

This folder is currently empty (Phase 1 scaffolding only).
