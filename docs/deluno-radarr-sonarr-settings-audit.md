# Deluno Settings Audit Against Radarr and Sonarr

## Objective

This document audits the settings surface area exposed by Radarr and Sonarr and maps each area into Deluno's product model.

The goal is not to clone their menus 1:1. The goal is to determine:

- what Deluno must support to be credible as a replacement
- what Deluno should merge or redesign
- what Deluno can postpone
- what Deluno should deliberately not copy

## Source Baseline

Reference sources used for this pass:

- Radarr settings docs: https://wikiold.servarr.com/Radarr_Settings
- Sonarr settings docs: https://wikiold.servarr.com/Sonarr_Settings
- Radarr product site: https://radarr.org/
- Radarr repository: https://github.com/Radarr/Radarr

These sources show the broad settings domains that existing users expect:

- Media Management
- Profiles
- Quality
- Custom Formats
- Indexers
- Download Clients
- Import Lists
- Connect
- Metadata
- Tags
- General
- UI
- System

## Deluno Product Rule

Deluno should split product concerns like this:

- `Movies` and `TV`: title-level operations, library triage, bulk actions, wanted state, manual search, imports, and per-title workflow
- `Indexers`: provider setup, routing, client orchestration, health, testing, and dispatch policy
- `Activity`: queue, audit trail, imports, failures, retry visibility
- `Settings`: platform policy, libraries, quality/media-management rules, naming, search automation, metadata/output behavior, app preferences

That means Deluno should not dump every legacy `*arr` setting into a single giant Settings page.

## Current Deluno State

As of this pass, Deluno already has real UI and backend support for:

- platform settings
- libraries
- quality profiles
- library automation cadence
- indexers
- download clients
- library routing
- library import existing
- library search now
- title monitoring
- title manual search
- TV episode monitoring
- activity and dispatch visibility

Current gaps are mostly in configuration depth, not product shell.

## Mapping Matrix

### 1. Media Management

Radarr/Sonarr cover:

- file and folder naming
- rename behavior
- import behavior
- hardlinks/copy behavior
- completed download handling
- torrent seeding/removal behavior integration
- permissions and root-folder path conventions
- upgrade/repack/proper handling

Deluno disposition:

- `required`

Deluno location:

- `Settings > Media Management`

Deluno should be better by:

- separating `Naming`, `Importing`, and `File Handling`
- showing filesystem implications clearly for Windows and Docker
- exposing route-aware import behavior rather than hiding it under download client assumptions

Recommended Deluno sections:

- Naming
- Importing
- File Handling
- Completed Download Handling
- Upgrade / Repack Rules

### 2. Profiles

Radarr/Sonarr cover:

- quality profiles
- language behavior
- upgrade ceilings
- custom-format scoring attachment to profiles
- profile assignment defaults

Deluno disposition:

- `required`

Deluno location:

- `Settings > Profiles`

Notes:

- Deluno already has quality profile foundations
- this needs to become its own first-class workspace, not a small card on the settings overview

Recommended Deluno sections:

- Quality Profiles
- Upgrade Rules
- Language / Audio Preferences later
- Profile Assignment Defaults

### 3. Quality

Radarr/Sonarr cover:

- quality definitions
- size limits
- ordering
- cutoff behavior
- repack/proper strategy interaction

Deluno disposition:

- `required`

Deluno location:

- `Settings > Quality`

Notes:

- This is distinct from Profiles
- Profiles say what is desired; Quality defines the raw qualities and constraints

Recommended Deluno sections:

- Quality Definitions
- Size Bounds
- Cutoff Rules
- Upgrade Gates

### 4. Custom Formats

Radarr explicitly centers custom-format scoring. Sonarr users also expect custom-format-like behavior, especially for language, source, HDR, codec, and release-group preference.

Deluno disposition:

- `required`, but `phase 2`

Deluno location:

- `Settings > Custom Formats`

Notes:

- Deluno should treat this as a proper rules engine, not just a list of scores
- this likely becomes one of Deluno's strongest differentiators if implemented cleanly

Recommended Deluno sections:

- Format Rules
- Matching Conditions
- Score Impact
- Profile Attachments
- Dry Run / Explanation

### 5. Indexers

Radarr/Sonarr expose:

- indexer definitions
- protocol/provider config
- test actions
- RSS/automatic/manual search enablement
- restrictions and categories

Deluno disposition:

- `required`

Deluno location:

- top-level `Indexers`

Notes:

- Deluno should not bury this inside Settings
- provider enablement by search mode is important and currently missing in Deluno

Required Deluno sections:

- Providers
- Search Capability Flags
- Categories / Restrictions
- Tags
- Health / Latency / Failures

### 6. Download Clients

Radarr/Sonarr expose:

- client connection config
- category mapping
- recent/older priority
- post-import category behavior
- torrent initial state
- completed download handling expectations

Deluno disposition:

- `required`

Deluno location:

- top-level `Indexers > Download Clients`

Notes:

- Deluno already has the basic CRUD surface
- missing deeper behavior fields and explanation

Required Deluno sections:

- Client Connections
- Category Templates
- Priority / Ordering
- Import Hand-off Behavior
- Health / Test

### 7. Import Lists

Radarr/Sonarr use lists to auto-add items from IMDb/Trakt/TMDb and similar sources.

Deluno disposition:

- `required`, but redesigned

Deluno location:

- likely `Settings > Sources` or `Settings > Lists`

Notes:

- This should not be copied blindly
- Deluno should think of this as `Discovery / Intake Sources`
- future model should support curator lists, watchlists, and maybe service-linked acquisition sources

Recommended Deluno naming:

- `Lists` or `Intake Sources`

### 8. Connect

Radarr/Sonarr expose notification and webhook integrations.

Deluno disposition:

- `required`, but later than core acquisition settings

Deluno location:

- `Settings > Connect`

Notes:

- useful for Discord, webhooks, email, push, media server triggers
- not essential for MVP, but expected by serious users

### 9. Metadata

Radarr/Sonarr expose metadata generation and export behavior.

Deluno disposition:

- `required`, but later

Deluno location:

- `Settings > Metadata`

Notes:

- NFO/image export matters for Kodi/Jellyfin/Plex-adjacent users
- Deluno should support this cleanly, but it is not as urgent as core search/import/routing

### 10. Tags

Radarr/Sonarr use tags as cross-cutting selectors for restrictions and targeting.

Deluno disposition:

- `required`

Deluno location:

- `Settings > Tags`

Notes:

- tags should also surface inside Movies/TV bulk actions
- tags should influence routing, quality/profile application, and discovery/list rules

### 11. General

Radarr/Sonarr expose:

- bind address
- port
- URL base
- auth
- SSL
- analytics/update/instance behavior

Deluno disposition:

- `required`

Deluno location:

- `Settings > General`

Notes:

- some of this overlaps packaging/runtime concerns
- Deluno should separate `Host`, `Security`, and `App Identity`

### 12. UI

Radarr/Sonarr expose UI preferences and presentation options.

Deluno disposition:

- `required`, but slim

Deluno location:

- `Settings > UI`

Notes:

- Deluno does not need dozens of toggles here
- useful settings are likely:
  - theme
  - density / canvas mode
  - default library view
  - time/date format
  - maybe poster density or wall/list default

### 13. System

Radarr/Sonarr expose logs, tasks, backups, health, updates, events, and runtime diagnostics.

Deluno disposition:

- `required`

Deluno location:

- separate top-level `System`

Notes:

- this should not be merged into Settings
- it belongs beside Activity as a user/admin surface

Recommended Deluno sections:

- Health
- Logs
- Scheduled Jobs
- Backups
- Runtime / Version / Diagnostics

## Deluno-Specific Expansions

Deluno should go beyond the `*arr` apps in these places:

### Unified Routing

Radarr/Sonarr split provider and client logic awkwardly across multiple pages.

Deluno should add:

- effective routing preview
- library-to-provider-to-client trace
- tag-based routing visibility
- explanation of why a search used a given route

### Import Recovery

The `*arr` stack has import and failed-download tooling, but the user experience is fragmented.

Deluno should add:

- import recovery policy in Settings
- recovery cases in Activity
- title-scoped recovery in Movies and TV workspaces
- default retry/rematch/delete rules

### Search Automation

Radarr/Sonarr distribute search-related behavior across indexers, download clients, profiles, and media-management settings.

Deluno should centralize:

- recurring search cadence
- retry windows
- search-on-add behavior
- upgrade search behavior
- manual search cooldown override behavior
- episode-vs-series search policy

### Library Policy at the Library Layer

Deluno already trends in this direction and should continue.

Library-level settings should own:

- root paths
- automation flags
- retry/search cadence
- default profile
- routing assignment
- media-type behavior defaults

This is cleaner than scattering these rules across unrelated pages.

## Implementation Priority

### Priority 0: Must build next

- `Settings > Media Management`
- `Settings > Profiles`
- `Settings > Quality`
- `Settings > General`
- `Settings > Tags`
- top-level `System`
- deeper `Indexers > Providers`
- deeper `Indexers > Download Clients`

### Priority 1: Strong follow-on

- `Settings > Custom Formats`
- `Settings > Lists / Intake Sources`
- `Settings > UI`
- `Settings > Metadata`

### Priority 2: Later

- `Settings > Connect`
- advanced notification targets
- richer metadata exporters

## Concrete Deluno IA Recommendation

### Top-level nav

- Dashboard
- Movies
- TV
- Calendar
- Activity
- Indexers
- Settings
- System

### Settings children

- Media Management
- Profiles
- Quality
- Custom Formats
- Lists
- Metadata
- Tags
- General
- UI

### Indexers children

- Providers
- Download Clients
- Routing
- Health

### System children

- Health
- Logs
- Jobs
- Backups
- About

## Current Gap Summary

Deluno currently has enough to demonstrate the product shell, but not enough settings depth to replace Radarr/Sonarr operationally.

The biggest missing settings domains are:

- Media Management
- true Quality workspace depth
- Custom Formats
- Lists / Intake Sources
- Metadata
- Tags
- UI preferences
- System admin/diagnostics

The biggest missing design decision is:

- whether some Radarr/Sonarr settings should remain standalone, or be merged into Deluno's stronger library/routing model

## Recommendation

Do not implement more ad hoc settings cards.

Instead, use this order:

1. lock the final Deluno settings IA
2. build the missing settings route skeletons
3. map backend models/endpoints needed for each area
4. then implement settings area by area in priority order

That avoids rebuilding Settings three times.
