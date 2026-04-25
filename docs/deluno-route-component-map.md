# Deluno Route And Component Map

## Purpose

This document translates the premium IA into frontend implementation slices.

Use it when:

- reshaping routes
- extracting reusable workspace components
- deciding where backend data should land in the UI

## Shell

### Root Shell

Path scope:

- all authenticated app pages

Responsibilities:

- left navigation rail
- active top-level area
- shell heading
- stage container

Current file:

- `apps/web/src/shell/root-layout.tsx`

### Shared Section Tabs

Responsibilities:

- render second-level navigation inside a top-level area

Current file:

- `apps/web/src/shell/section-tabs.tsx`

## Movies

### Route Tree

- `/movies/library`
- `/movies/wanted`
- `/movies/import`

### Library

Current file:

- `apps/web/src/routes/movies-page.tsx`

Future component extraction:

- `movies/movies-workspace-header.tsx`
- `movies/movies-summary-strip.tsx`
- `movies/movies-toolbar.tsx`
- `movies/movies-library-view.tsx`
- `movies/movies-bulk-footer.tsx`

### Wanted

Current file:

- `apps/web/src/routes/movies-wanted-page.tsx`

Future component extraction:

- `movies/wanted/movies-wanted-summary.tsx`
- `movies/wanted/movies-wanted-list.tsx`
- `movies/wanted/movies-search-history-panel.tsx`
- `movies/wanted/movies-dispatch-panel.tsx`

### Import

Current file:

- `apps/web/src/routes/movies-import-page.tsx`

Future component extraction:

- `movies/import/movies-import-summary.tsx`
- `movies/import/movies-import-issues-list.tsx`

### Detail

Planned route:

- `/movies/:movieId`

Planned components:

- `movies/detail/movie-detail-page.tsx`
- `movies/detail/movie-detail-hero.tsx`
- `movies/detail/movie-detail-tabs.tsx`
- `movies/detail/movie-overview-panel.tsx`
- `movies/detail/movie-files-panel.tsx`
- `movies/detail/movie-search-panel.tsx`
- `movies/detail/movie-history-panel.tsx`
- `movies/detail/movie-edit-panel.tsx`

## TV

### Route Tree

- `/tv/library`
- `/tv/wanted`
- `/tv/import`

### Library

Current file:

- `apps/web/src/routes/series-page.tsx`

Future component extraction:

- `tv/tv-workspace-header.tsx`
- `tv/tv-summary-strip.tsx`
- `tv/tv-toolbar.tsx`
- `tv/tv-library-view.tsx`
- `tv/tv-bulk-footer.tsx`

### Wanted

Current file:

- `apps/web/src/routes/tv-wanted-page.tsx`

Future component extraction:

- `tv/wanted/tv-wanted-summary.tsx`
- `tv/wanted/tv-wanted-list.tsx`
- `tv/wanted/tv-search-history-panel.tsx`
- `tv/wanted/tv-dispatch-panel.tsx`

### Import

Current file:

- `apps/web/src/routes/tv-import-page.tsx`

Future component extraction:

- `tv/import/tv-import-summary.tsx`
- `tv/import/tv-import-issues-list.tsx`

### Detail

Planned route:

- `/tv/:seriesId`

Planned components:

- `tv/detail/series-detail-page.tsx`
- `tv/detail/series-detail-hero.tsx`
- `tv/detail/series-detail-tabs.tsx`
- `tv/detail/series-overview-panel.tsx`
- `tv/detail/series-episodes-panel.tsx`
- `tv/detail/series-search-panel.tsx`
- `tv/detail/series-history-panel.tsx`
- `tv/detail/series-import-panel.tsx`
- `tv/detail/series-edit-panel.tsx`

## Indexers

### Route Tree

- `/indexers/providers`
- `/indexers/routing`
- `/indexers/download-clients`
- `/indexers/health`

Current files:

- `apps/web/src/routes/indexers-providers-page.tsx`
- `apps/web/src/routes/indexers-routing-page.tsx`
- `apps/web/src/routes/indexers-download-clients-page.tsx`
- `apps/web/src/routes/indexers-health-page.tsx`

Future component extraction:

- `indexers/indexers-header.tsx`
- `indexers/providers/providers-list.tsx`
- `indexers/routing/routing-grid.tsx`
- `indexers/download-clients/download-client-list.tsx`
- `indexers/health/indexer-health-panels.tsx`

## Activity

### Route Tree

- `/activity/queue`
- `/activity/history`
- `/activity/imports`
- `/activity/failures`

Current files:

- `apps/web/src/routes/activity-queue-page.tsx`
- `apps/web/src/routes/activity-history-page.tsx`
- `apps/web/src/routes/activity-imports-page.tsx`
- `apps/web/src/routes/activity-failures-page.tsx`

Future component extraction:

- `activity/activity-header.tsx`
- `activity/queue/queue-list.tsx`
- `activity/history/history-list.tsx`
- `activity/imports/import-events-list.tsx`
- `activity/failures/failure-panels.tsx`

## Settings

### Route Tree

- `/settings/libraries`
- `/settings/media-management`
- `/settings/quality`
- `/settings/search`
- `/settings/general`

Current files:

- `apps/web/src/routes/libraries-page.tsx`
- `apps/web/src/routes/settings-media-management-page.tsx`
- `apps/web/src/routes/settings-quality-page.tsx`
- `apps/web/src/routes/settings-search-page.tsx`
- `apps/web/src/routes/settings-page.tsx`

Future component extraction:

- `settings/settings-header.tsx`
- `settings/libraries/library-list.tsx`
- `settings/libraries/library-form.tsx`
- `settings/media-management/media-management-panels.tsx`
- `settings/quality/quality-profiles-list.tsx`
- `settings/search/search-policy-list.tsx`
- `settings/general/general-settings-form.tsx`

## Shared Future Components

These should be reusable across areas once the page structure settles:

- `workspace/page-header.tsx`
- `workspace/summary-strip.tsx`
- `workspace/metric-card.tsx`
- `workspace/sticky-toolbar.tsx`
- `workspace/view-switcher.tsx`
- `workspace/bulk-footer.tsx`
- `workspace/empty-state.tsx`
- `workspace/timeline-list.tsx`
- `workspace/status-pill.tsx`
