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

## Canonical vocabulary and internal naming (resolves [#149](https://github.com/jampat000/Deluno/issues/149))

The sections above are the user-facing Rosetta stone. This section is the
normative name-per-concept table across the UI, the URL, and the code —
where those three disagree, the UI wins and the others are tracked here as a
known gap, not left to drift further. New code must use the **Canonical**
column.

| Concept | Canonical | URL | Legacy code names (not renamed here) |
|---|---|---|---|
| TV shows | TV | `/tv` | `Deluno.Series`, `SeriesListItem`, `series_*` tables |
| Media plans | Media Plans | `/settings/policy-sets` | `PolicySetItem`, `policy_sets` table |
| Release scoring rules | Release preferences | `/settings/custom-formats` | `CustomFormatItem`, `custom_formats` table |
| Quality ladders | Quality profiles | `/settings/profiles` | `QualityProfileItem` (already aligned) |
| File-size boundaries | Size rules | `/settings/quality` | quality profile "tier" fields (already aligned in spirit) |
| Final file destinations | Final destinations | `/settings/destination-rules` | `DestinationRuleItem` (already aligned) |
| List-based auto-add sources | Import lists | `/settings/lists` | `Deluno.Intake`, `IntakeSourceItem`, `intake_*` tables |
| Library folders | Library folders / Libraries | `/settings/libraries` | (already aligned) |
| Download/import backlog | Transfers | `/queue` | `job_queue` table, `IJobQueueRepository` |
| Scheduled search + retry behaviour | Automation | `/search-cycles` | `search_cycle_runs` table (already aligned) |
| Connections (indexers, download clients, routing) | Connections | `/indexers/indexers` | `IndexerItem` (already aligned) |
| Event timeline (system) | Audit | `/system/audit` | — |
| Event feed (live activity) | Activity | `/activity` | — |

### Decisions

1. **TV, not Shows.** The UI already says "TV" / "TV Shows" everywhere, and
   `/tv` is the only live route (`/shows` and `/shows/:id` already redirect).
   `Deluno.Series` stays as the backend project/table name — renaming a
   project referenced by 6,629 lines and multiple SQLite tables is its own
   project, not a naming-doc fix, and nothing user-visible depends on it.

2. **Media Plans, not Policy Sets.** The UI, the settings-shell area label,
   and this doc's own "Important differences" section above all already say
   "Media Plans". `PolicySetItem` and the `policy_sets` table are legacy code
   names, deferred.

3. **Release preferences, not Custom Formats.** The UI already says "Release
   preferences" (`settingsPageMeta` title for `/settings/custom-formats`).
   36 files reference `CustomFormat` in code — deferred, needs its own PR.

4. **Import lists, not Intake.** The UI says "Import lists" everywhere users
   see it; `Deluno.Intake` is a young backend project name (created this
   session) that nothing external depends on yet, so it is the cheaper side
   to eventually rename — but not in this PR, which is UI/route scoped.

5. **Transfers, not Queue.** The UI already says "Transfers"
   (`/queue`'s nav label, and this doc's own mapping table). `job_queue`/
   `IJobQueueRepository` are legacy code names, deferred — the job queue also
   backs background automation jobs that are not "transfers" in the user
   sense (metadata refresh, catalogue sync), so a rename here needs more
   thought than a mechanical find-replace.

6. **Activity is the live feed at `/activity`. Audit is the searchable
   timeline at `/system/audit`.** They are different screens with different
   jobs — Activity is "what is Deluno doing right now", Audit is "show me
   everything that happened, searchable". They were inconsistently labelled:
   `systemHealthNavItems` called the `/system/audit` tab "Activity" while
   `systemNavItems` called the same route "Audit". Fixed in this PR to
   "Audit" everywhere, so the name collision with the real Activity feed is
   gone.

7. **`catalog`, not `catalogue`.** Job types already agree on this
   (`movies.catalog.refresh`, `series.catalog.refresh`); the one outlier is
   the activity-log category string `metadata.series.catalogue`. Deferred —
   activity category strings are not currently migrated, but changing one
   changes what old activity rows filter as, so it needs the same care as a
   job-type rename.

8. **Job type grammar: `<area>.<entity>.<verb>`, plural areas, imperative
   verbs** — e.g. `movies.metadata.refresh`. Most job types already follow
   this (`movies.catalog.refresh`, `series.quality.recalculate`,
   `filesystem.import.execute`). `episode.search` and `library.search` are
   the two-part exceptions; renaming them changes values persisted in the
   `job_queue` table on live databases and needs a migration, deferred to its
   own issue.

### Collapsed in this PR

Two route pairs were genuinely live duplicates — both routes rendered the
same page, with no redirect between them — as opposed to the router's many
other `Navigate` aliases that already collapse a legacy path onto a
canonical one:

- **`/indexers` → now redirects to `/indexers/indexers`.** The Connections
  area's first tab lives at `/indexers/indexers` (alongside
  `/indexers/download-clients` and `/indexers/library-routing`); the bare
  `/indexers` used to independently render the same `IndexersPage`, which
  meant two URLs served identical content with no canonical one.
- **`/settings/automation` → now redirects to `/search-cycles`.** Nothing in
  the app links to `/settings/automation` — it rendered `SearchCyclesPage` a
  second time at an unreferenced URL, reachable only by typing it directly.

Everything else the original issue's "same thing, two routes" table listed
(`shows`, `import-lists`, `quality-sizes`, `root-folders`, `settings/media`,
`settings/indexers`, `settings/download-clients`, `settings/connect`) was
already a `Navigate` redirect onto a canonical route by the time this issue
was picked up — the router had moved on since the audit that filed it.
`quality` vs `profiles` in the original table turned out not to be a
duplicate: they are two already-distinct concepts (Size rules and Quality
profiles) that merely sit under the same "Media plans" area.

### Deferred

Everything under "Legacy code names" above, plus:

- Renaming public API routes (e.g. `/api/policy-sets`) — the issue orders
  this last, and not before API versioning exists. #142 (API versioning) is
  merged, so this is now unblocked, but is still its own PR: it needs
  `docs/external-integration-api.md` updated in the same change, per the
  handover's rule on breaking API changes.
- Job-type renames — need a migration for in-flight rows on live databases,
  and are worth batching once the grammar in decision 8 is fully agreed
  rather than doing one at a time.
