# Deluno Premium IA Spec

## Goal

Deluno should feel like one premium media operations app, not a stitched-together set of admin pages.

The UX bar is:

- clearer than Radarr and Sonarr
- library-first, not settings-first
- bulk management first-class
- operational state visible without digging
- premium visual hierarchy on desktop and mobile

## Product Model

Deluno has five user-facing areas:

- `Movies`
- `TV`
- `Indexers`
- `Activity`
- `Settings`

Rules:

- `Movies` and `TV` are the primary library workspaces
- `Indexers` is the provider and routing control center
- `Activity` is the audit and operations surface
- `Settings` is policy and platform behavior only

Title-level management does not belong in `Settings`.

## Top-Level Routes

- `/movies`
- `/movies/:movieId`
- `/tv`
- `/tv/:seriesId`
- `/indexers`
- `/activity`
- `/settings`

Recommended nested route model:

- `/movies/library`
- `/movies/wanted`
- `/movies/import`
- `/tv/library`
- `/tv/wanted`
- `/tv/import`
- `/indexers/providers`
- `/indexers/routing`
- `/indexers/download-clients`
- `/indexers/health`
- `/activity/queue`
- `/activity/history`
- `/activity/imports`
- `/activity/failures`
- `/settings/libraries`
- `/settings/media-management`
- `/settings/quality`
- `/settings/search`
- `/settings/general`

The default path for each top-level area should redirect to the first sub-route above.

## Global Shell

The shell should have:

1. A left rail for top-level navigation.
2. A page header with title, subtitle, and primary action.
3. A secondary tab row for the active area.
4. A sticky action and filter bar for workspace pages.
5. A wide content canvas.

The shell should not rely on a generic landing dashboard for orientation. The primary routes themselves should be the product.

## Movies

### Purpose

`Movies` is a full library-management workspace.

Users come here to:

- browse their movie library
- filter by missing, quality, genre, tag, root folder, and status
- run bulk actions
- open title detail

### Secondary Tabs

- `Library`
- `Wanted`
- `Import`

### Movies Library

Structure:

1. Header
2. Summary strip
3. Sticky filter and action bar
4. Main content view
5. Bulk action footer when selection mode is active

Summary strip metrics:

- total movies
- missing
- upgrades needed
- unmonitored
- import issues
- recent dispatches

Core filters:

- text search
- monitored
- missing
- upgrade needed
- quality profile
- root folder
- genre
- year
- tag
- status
- has file
- source list

Views:

- `Table`
- `Cards`
- `Posters`

Table columns:

- title
- year
- monitored
- status
- quality
- profile
- root folder
- genres
- added date
- last search
- next retry
- tags

Bulk actions:

- monitor
- unmonitor
- change quality profile
- change root folder
- rename folders
- search selected
- tag selected
- delete and remove

### Movie Detail

Header content:

- poster and backdrop
- title and year
- monitored toggle
- status pill
- quality pill
- quick actions

Tabs:

- `Overview`
- `Files`
- `Search`
- `History`
- `Activity`
- `Edit`

Overview modules:

- title status summary
- quality target versus current
- file presence
- library path
- genres and tags
- recent activity
- recent search attempts
- recent dispatches

## TV

### Purpose

`TV` is a series and episode operations workspace.

Users come here to:

- browse all series
- filter by missing episodes, status, profile, genre, network, and tags
- bulk manage series
- drill down into seasons and episodes

### Secondary Tabs

- `Library`
- `Wanted`
- `Import`

### TV Library

Structure:

1. Header
2. Summary strip
3. Sticky filter and action bar
4. Main content view
5. Bulk action footer

Summary strip metrics:

- total series
- monitored series
- total seasons
- tracked episodes
- missing episodes
- episodes needing upgrade
- import issues

Core filters:

- text search
- monitored
- continuing or ended
- missing episodes
- upgrade needed
- quality profile
- root folder
- genre
- network
- tag
- year
- has files

Views:

- `Table`
- `Cards`
- `Posters`

Table columns:

- title
- year
- monitored
- status
- network
- profile
- root folder
- seasons
- tracked episodes
- missing episodes
- quality state
- last search
- tags

Bulk actions:

- monitor
- unmonitor
- change season monitoring mode
- change quality profile
- change root folder
- rename folders and files
- search selected
- tag selected
- delete and remove

### Series Detail

Header content:

- cinematic artwork
- title, year, and network
- monitored toggle
- quick metrics
- quick actions

Tabs:

- `Overview`
- `Episodes`
- `Search`
- `History`
- `Import`
- `Edit`

Overview modules:

- series health summary
- season breakdown
- wanted summary
- quality summary
- import summary
- recent activity

Episodes modules:

- season grouping
- per-episode rows
- episode filters
- season and episode bulk actions

Episode row fields:

- season and episode number
- title
- air date
- monitored
- has file
- quality
- wanted state
- import issue state

## Indexers

### Purpose

`Indexers` replaces the external broker and source-management role.

Secondary tabs:

- `Providers`
- `Routing`
- `Download Clients`
- `Health`

### Providers

Columns:

- name
- protocol
- privacy
- categories
- priority
- tags
- enabled
- health

Actions:

- add
- edit
- test
- enable or disable
- reprioritize
- retag

### Routing

Purpose:

- explain how Deluno routes searches and grabs
- show effective configuration per library and media type

Columns:

- library
- media type
- indexers
- download clients
- fallback behavior
- last used
- health

### Download Clients

Columns:

- name
- protocol
- endpoint
- category template
- priority
- enabled
- health

### Health

Panels:

- failing indexers
- failing download clients
- auth issues
- routing issues
- recent dispatch failures

## Activity

### Purpose

`Activity` is Deluno's audit and live-operations surface.

Secondary tabs:

- `Queue`
- `History`
- `Imports`
- `Failures`

Each row should answer:

- what happened
- what media item it affected
- why it happened
- what Deluno did next
- whether user action is needed

## Settings

### Purpose

`Settings` is for policy and platform behavior only.

Secondary tabs:

- `Libraries`
- `Media Management`
- `Quality`
- `Search`
- `General`

### Libraries

This is where users define library containers, defaults, search cadence, and import-existing actions.

This is not where users browse and edit title-level collections.

### Media Management

Sections:

- naming
- renaming
- import behavior
- file handling

### Quality

Sections:

- quality profiles
- cutoffs
- upgrade behavior

### Search

Sections:

- recurring search defaults
- retry policy
- manual search behavior

### General

Sections:

- app identity
- runtime basics
- UI preferences

## Design Direction

The premium look should come from structure before decoration.

Principles:

- large page headers
- calm spacing
- sticky control surfaces
- fewer but clearer panels
- list and detail workflows that feel intentional
- strong typography hierarchy
- no wall-of-cards dashboards

Content hierarchy on workspace pages:

1. Header
2. Summary strip
3. Sticky action and filter bar
4. Primary content
5. Bulk action surface when relevant

## API Needs

The current API is enough for an initial shell restructure, but the final UI requires richer list and detail endpoints.

Needed over time:

- paged and filtered movie list
- paged and filtered series list
- movie bulk edit
- series bulk edit
- movie detail endpoints
- series detail endpoints
- episode inventory and wanted state endpoints
- indexer routing visibility endpoints
- richer activity endpoints by category

## Implementation Order

1. Refactor shell and routes to match the IA.
2. Rebuild `Movies` as the reference workspace.
3. Rebuild `TV` with the same shell and add deeper episode UX.
4. Reshape `Indexers`, `Activity`, and `Settings`.
5. Add richer detail routes and supporting API surfaces.
6. Polish motion, spacing, and responsive behavior once structure is stable.
