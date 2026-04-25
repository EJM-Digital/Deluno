# Deluno Arr-Stack Strengths

Updated: 2026-04-25

This is the corrected product reference set for Deluno. The target is not Plex, Emby, or Jellyfin behavior. Deluno is replacing the operational strengths of Radarr, Sonarr, Prowlarr, Recyclarr, Huntarr, and Fetcher in one cleaner product.

## What To Keep

### Radarr

- Movie-first library management.
- Quality profiles with cutoff and upgrade behavior.
- Custom formats that affect release scoring.
- Wanted, missing, cutoff-unmet, manual search, calendar, import, and history views.
- Clear release comparison rules: quality, custom format score, protocol, indexer priority, age, size, and seed/peer context.

### Sonarr

- TV-first handling with show, season, and episode monitoring.
- Standard, daily, anime, and special episode shapes.
- Episode inventory and wanted-state visibility.
- Quality profile and custom-format scoring in current Sonarr versions.
- Import behavior that understands season packs, episode titles, hardlinks, and completed-download handling.

### Prowlarr

- Centralized indexer management.
- Torrent and Usenet source support.
- Newznab/Torznab generic support.
- Indexer health, history, statistics, category-aware searching, and parameter-based manual search.
- App/indexer synchronization as a concept, but Deluno should avoid needing separate app sync by owning routing directly.

### Recyclarr

- Guide-backed custom formats by ID.
- Custom-format groups and curated bundles.
- Quality profile sync, quality definitions, naming formats, and preview-before-apply behavior.
- Safe user overrides.
- Drift detection and safe cleanup for guide-owned definitions only.

### Huntarr

- Continuous missing and upgrade hunting.
- Per-library/per-instance schedules.
- Caps, intervals, retry windows, and state tracking so indexers are not hammered.
- Future-release filtering and quality-upgrade targeting.
- Plain status reporting for what was searched and why.

### Fetcher

- Scheduled and manual searches for monitored missing items.
- Cutoff-unmet upgrade automation for Sonarr and Radarr.
- Per-app retry delay so the same items are not hammered every tick.
- Dashboard run state: last run, next run, queue counts, and retry-delay context.
- Human-readable activity and job logs.
- Failed import cleanup with conservative classification and optional blocklist/remove-from-client behavior.
- Setup wizard, auth, backup/restore, and in-app update posture.

## Where Deluno Goes Further

- One setup flow instead of six apps and API keys between them.
- One search/routing model across movies, TV, indexers, download clients, and watchlist sources.
- Human-readable decisions: why this source, why this client, why this quality, why skipped.
- Genre/tag/rule-based destination folders so users do not need multiple Radarr/Sonarr instances just to route differently.
- Built-in guide presets that normal users can apply without YAML.
- Custom filters beyond Radarr/Sonarr: bitrate, release group, codec, HDR format, audio channels, tags, provider ratings, path, and source health.
- Normalized ratings model for TMDb, IMDb, Rotten Tomatoes, Metacritic, Trakt, and future providers.

## Implementation Direction

- Keep TMDb as the primary lookup/artwork provider.
- Use OMDb as an optional ratings enrichment provider for IMDb, Rotten Tomatoes, and Metacritic.
- Keep provider-specific data inside normalized metadata JSON and expose a single `ratings` array to the frontend.
- Keep recurring search inside Movies and TV Shows rather than as a visible extra app.
- Keep Prowlarr-like behavior inside Indexers, with direct Deluno routing rather than sync-to-another-app wording.
- Keep Recyclarr-like behavior inside guided settings/presets, with previews and plain-language changes before apply.
- Keep Fetcher-like behavior inside Movies, TV Shows, Activity, and System, with no separate helper-app mental model.
