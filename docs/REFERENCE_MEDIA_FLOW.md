# Supported Reference Media Flow

Deluno's first repeatable reference configuration is deliberately the simplest
useful flow: add a title, let Deluno send it to a configured download client,
then import, name, and place it in the library. It uses a **Torznab movie
source** and **qBittorrent**. It is intentionally narrow: it demonstrates one
complete safe flow without claiming that every indexer or external downloader
behaves identically.

In Guided setup, choose **qBittorrent** under Connections and provide its host,
port, and credentials. The guide verifies the connection before saving the
route. A Torznab source still needs its own successful connection test.

## What the reference flow proves

1. A movie is monitored with a `WEB 1080p` goal.
2. A configured Torznab-compatible source receives a normal search request.
3. Deluno parses the feed, rejects a CAM candidate, and selects an explainable preferred WEB candidate.
4. Deluno sends the approved release to qBittorrent.
5. qBittorrent queue telemetry and Deluno's dispatch record provide provenance.
6. Completed content is previewed, imported, named, and written to the selected library route.
7. The final import updates the catalog and its dispatch-resolution history.

The TV fixture follows the equivalent route for an episode and an external
Usenet download client.

## Advanced processing flow

An optional, separately configured route supports external processing before
the final import:

> Deluno acquisition → downloader → FileFlows or Weir → processed-complete
> folder → Deluno import, rename, and organise

This is the appropriate route for a library that removes unwanted subtitles or
non-English audio before import. It is not required for the basic setup.
FileFlows and Weir remain independent tools: Deluno does not need a
vendor-specific integration or to configure their flows. Instead, the advanced
library configuration maps an accessible processed-output path and preserves a
stable per-job folder/relative path so Deluno can match the finished output to
the download it owns. Deluno then validates, imports, renames, and organises
the result.

An optional generic completion callback can make that match immediate where a
user already has automation around the processor. It is an optional
disambiguation aid, not a FileFlows or Weir integration and not a
requirement for the watched-output flow. Unmatched or ambiguous files stay in
recovery rather than being guessed at or imported automatically.

## Repeatable evidence

Run the focused flow suite from the repository root:

```powershell
.\scripts\test-real-world-flows.ps1
```

The tests use a deterministic Torznab-compatible HTTP fixture, temporary SQLite databases, and temporary media/download folders. They also exercise failed-dispatch state, the user-facing retry endpoint, and bounded retry backoff. They never use real provider credentials or touch a configured library.

## Explicit limits

- This is synthetic integration evidence, not proof of a particular external provider account or downloader installation.
- A live source or external download client must pass its own capability check during setup before Deluno reports automated acquisition as ready.
- Cleanup is a configurable library policy, not a fixed product rule. The
  default is observation/preview. A person can deliberately enable a
  three-strike policy that blocks a release, starts a replacement search,
  removes its client entry, and purges residual payload files in approved,
  Deluno-owned paths. Every action remains scoped and audited; library and
  unproven shared/cross-seeded files are never targets.
