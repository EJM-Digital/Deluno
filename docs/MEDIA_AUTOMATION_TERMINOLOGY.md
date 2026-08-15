# Media Automation Terminology

Deluno replaces a collection of media tools with one media-first workflow. It deliberately uses familiar words where they help a person find the right control, but it does not claim to be a screen-for-screen replacement for every application.

The normal path is: choose media to keep, set the result you want, then let Deluno explain how it searches, downloads, imports, improves, or safely stops.

## Where to start

Open **Library setup** to see what is configured and ready. Each card is a real status and opens the controls for that concern. The detailed controls are never removed; they are simply kept behind the decision they affect.

## Navigation in plain language

- **Your media** is the catalogue: browse, add, and plan the movies and shows you care about.
- **Manage live work** is where you act on work underway. Use it to inspect and intervene in downloads, searches, retries, and items needing attention. It is not a passive status-only area.
- **Set up your library** is the lasting configuration: folders, search sources, download clients, media plans, and rules that Deluno uses every day.
- **Maintain Deluno** is for the application itself: health, backups, updates, and advanced access. It should not be needed for normal library work.

| If you know it as… | In Deluno, start at… | What it means here |
| --- | --- | --- |
| Radarr/Sonarr root folder or Media Management | **Library & storage** | The folders Deluno imports to, naming, metadata, destination rules, processors, and storage behaviour. |
| Prowlarr indexer | **Search sources** | A service Deluno asks for releases. The technical term **indexer** remains visible in connection details. |
| SABnzbd/qBittorrent download client | **Download clients** | The destination for an approved release before Deluno imports it. Health, categories, and routing stay available as detail. |
| SABnzbd Queue/History | **Transfers** and **Activity** | Transfers shows downloads and imports in progress or needing attention. Activity records the explanation and result. |
| Radarr/Sonarr quality profile, cutoff, size rules, custom formats | **Media plans** | Begin with the outcome: quality, files, upgrades, and destination. Quality profiles, size limits, release preferences, format scores, and overrides remain available within the plan. |
| Huntarr missing/upgrade searches | **Automation** | What Deluno will check next, why it is waiting, retries, work budgets, and safe pause/defer/skip controls. |
| CleanUpArr | **Download health & cleanup** | Evidence for unhealthy downloads, a safe preview before cleanup, and a controlled replacement search. It is not a separate cleanup console. |
| Recyclarr/Configarr/TRaSH configuration | **Guide-backed media plans** | Presets and imported configuration must show provenance, effective rules, and safe overrides. Deluno never silently claims a source configuration maps exactly. |

## Important differences

- **Search source** is the friendly term; **indexer** is retained as technical context for people coming from Prowlarr or the Arr apps.
- **Media plan** groups decisions that otherwise become scattered across quality profiles, file-size definitions, custom formats, routing, and automation. It is not intended to hide those controls.
- **Downloads & imports** are one handoff: approved release, download, post-processing, import, rename, and a clear recovery path. Queue and history remain distinct views where that helps diagnose a problem.
- **Download health & cleanup** defaults to observation and a preview. Deluno must never imply malware protection or delete unproven shared/cross-seeded data.
- A familiar name describes an entry point, not a promise of feature parity. Supported capabilities and limitations belong in the relevant setup, plan, and health screens.

## Reference terminology

- [Radarr settings](https://wiki.servarr.com/radarr/settings) and the [Sonarr quick start](https://wiki.servarr.com/en/sonarr/quick-start-guide) use Media Management, Indexers, Download Clients, Quality Profiles, monitoring, and library import.
- [SABnzbd](https://sabnzbd.org/wiki/extra/queue-history-searching) uses Queue, History, Categories, and post-processing states.
- [Prowlarr](https://prowlarr.org/) manages indexers and application assignments.
- [Recyclarr](https://recyclarr.dev/reference/configuration/quality-profiles/) and [Configarr](https://configarr.de/docs/configuration/config-file/) use quality profiles, custom formats, guide settings, and explicit overrides.
- [CleanUpArr](https://github.com/Cleanuparr/Cleanuparr) covers unhealthy downloads, safe cleanup, replacement searches, seeding, and orphan handling.
