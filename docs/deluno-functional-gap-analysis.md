# Deluno Functional Gap Analysis

Updated: 2026-04-21

## Goal

Deluno should feel simpler, friendlier, and more premium than the current crop of media automation tools while still covering the hard real-world cases that make people keep multiple installs, helper tools, and manual workarounds.

This document turns that into a product plan.

## What Existing Tools Already Do Well

From the official Servarr docs, the leading apps in this space are strong at:

- Tracking monitored items and watching RSS feeds for newly posted releases
- Running manual or search-on-add searches when the user wants something immediately
- Applying quality, cutoff, delay, language, and release rules
- Handling upgrades when better releases appear later
- Importing, renaming, and organizing finished downloads
- Supporting advanced TV edge cases like daily shows, anime, specials, and episode naming modes
- Supporting multiple instances when users split libraries such as HD vs 4K

Important current behavior from the official docs:

- Existing tools do not regularly search the backlog of missing or cutoff-unmet items on their own. They rely on RSS for newly posted releases and manual or explicit automatic search commands for older backlog items.
- Current TV-focused tools expose different show types for Standard, Daily, and Anime because search and parsing behavior differs.
- Existing tools support upgrades through quality and cutoff rules.
- Servarr explicitly documents running multiple instances with separate data directories for split libraries such as 4K.

## What Deluno must do to be better

Deluno should not just reproduce the same features in one shell. It needs to remove the pain points that made helper tools and multi-instance setups necessary in the first place.

### 1. Native recurring wanted searches

This is the biggest product gap.

Deluno should own a first-class recurring scheduler for:

- missing movie searches
- missing TV episode searches
- upgrade searches for cutoff-unmet items
- optional per-library or per-rule schedules

This should be native, visible, and understandable.

The user should be able to say:

- check for missing movies every 6 hours
- check for TV upgrades every night
- do not retry the same item for 24 hours
- pause one library without pausing everything

That is core behavior Deluno should own natively.

### 2. Better missing-item coverage than the wanted queue alone

This solves an important gap in the current tool landscape.

The official wanted queues do not always represent the full monitored-missing universe, especially for unreleased or not-yet-available items. Fetcher works around that by walking the full monitored library and selecting monitored items without files, then applying cooldown and batching before dispatching searches.

Deluno should adopt that model natively:

- maintain a true "monitored and missing" view for movies
- maintain a true "monitored and missing" view for TV episodes
- distinguish that from "currently available in wanted queue"
- batch through the backlog instead of hammering page 1 forever

### 3. Per-item retry delay and search memory

Deluno should solve repeated search spam with a per-item cooldown log.

Deluno should track search attempts per item so it can say:

- searched 3 hours ago, wait 21 more hours
- skipped because retry delay has not expired
- this library has 412 missing items, 25 eligible right now

Without this, recurring wanted searches will feel noisy and wasteful.

### 4. Multi-library support without chaos

Some users run multiple separate installs today because they need:

- HD and 4K split libraries
- anime separated from normal TV
- kids libraries
- codec or size-rule variants
- different root folders, download clients, or upgrade behavior

Deluno should support that as a first-class concept inside one app.

The product model should be:

- one Deluno app
- many libraries
- each library belongs to either Movies or TV Shows
- each library has its own root folder, download category, profiles, rules, schedule, and automation behavior

Examples:

- Movies / Main
- Movies / 4K
- TV Shows / Main
- TV Shows / Anime
- TV Shows / Kids

This replaces the need for multiple Deluno instances in most cases while still allowing advanced users to stay organized.

### 5. Friendlier language everywhere

Existing tools expose a lot of internal concepts directly. Deluno should translate them.

Examples:

- "Cutoff unmet" should become "Ready for an upgrade"
- "Monitored" should become "Keep checking automatically"
- "Interactive search" should become "Choose a release"
- "Release profile / custom format / delay profile" should be grouped under a clearer concept like "Release rules"

The power stays. The wording gets easier.

### 6. Better visual surfaces than the existing *arr UIs

To feel premium and desirable, Deluno needs stronger product surfaces than just forms and tables.

Core screens Deluno should have:

- Overview
- Movies
- TV Shows
- Activity
- Settings
- Libraries
- Release Rules
- Queue
- Calendar
- Wanted
- Upgrades
- Connections
- Import Review

The important point is not just to add pages, but to make each page feel obvious.

## What Deluno needs to display

### Overview

This should be the home screen users actually want to open.

It should show:

- missing movies
- missing TV episodes
- items ready for upgrade
- active downloads
- recent imports
- libraries needing attention
- last scheduled search per library
- next scheduled search per library
- a clear "Search now" action for movies, TV, missing, and upgrades

### Libraries

This is where Deluno should solve the multi-instance problem.

Each library should show:

- media type: Movies or TV Shows
- purpose: Main, 4K, Anime, Kids, etc.
- root folder
- download category or client mapping
- quality target
- upgrade behavior
- recurring search schedule
- retry delay
- connected indexers and download clients

This page should make split setups easy to understand.

### Wanted

Wanted should be a premium, high-signal surface.

Split it into:

- Missing now
- Waiting for release
- Ready for upgrade
- Skipped by retry delay
- Not eligible because of rules

Every row should explain itself in plain language.

### Activity

Activity should avoid raw internal noise.

It should answer:

- what Deluno just checked
- what it searched for
- what it grabbed
- what it imported
- what it skipped and why
- what needs attention

Technical detail can exist behind an expandable drawer, but the default view should stay human.

### Import Review

This is a major opportunity for Deluno to raise the bar.

Deluno should provide a friendly import review screen for:

- unmatched files
- quality rejections
- not-an-upgrade results
- wrong-content matches
- root-folder conflicts
- duplicate copies

## Multiple instance strategy

Deluno should support two levels:

### Level 1: Native Deluno libraries

Preferred long-term model.

One Deluno install can host many libraries with strict separation:

- separate root folders
- separate release rules
- separate schedules
- separate queues
- separate import behavior
- separate naming rules when needed

This is how Deluno replaces most multi-instance *arr setups.

### Level 2: Indexers and service setup

Important for Deluno's own operation.

Deluno should model its required outside services cleanly, for example:

- main indexer group
- backup indexer group
- primary download client
- notification service

Each connection should have:

- name
- app type
- base URL
- API key
- role or mapped Deluno library
- health status
- test connection action

This gives users a clear setup model without turning the app into a maze.

## Native recurring search behavior

Deluno should absorb these behaviors directly into Movies and TV Shows:

- recurring missing searches
- recurring upgrade searches
- per-app or per-library retry delay
- per-item cooldown memory
- backlog pagination instead of page-1 loops
- inclusive monitored-missing scanning, not just wanted queue totals
- truthful dashboard counts
- clear activity wording
- manual "search now" actions that bypass schedule windows but still respect cooldown where appropriate

This should not exist as a separate user-facing helper area. It should feel like a normal part of Deluno's movie and TV workflows.

## Recommended Deluno domain additions

The current codebase needs these product concepts next.

### Platform

- app connections
- notifications
- scheduler settings
- shared download clients
- shared indexers

### Movies

- movie libraries
- quality targets
- release rules
- missing state
- upgrade state
- import state

### TV Shows

- TV libraries
- show type: standard, daily, anime
- season and episode monitoring
- missing episode state
- upgrade state
- import state

### Jobs

- recurring schedules
- item-level cooldown log
- search runs
- import runs
- cleanup runs
- manual run tracking

### Integrations

- indexers
- download clients
- optional import and migration helpers for users coming from older setups

## Recommended UI wording model

Deluno should standardize on these phrases:

- `TV Shows`, not `Series`
- `Keep checking automatically`, not `Monitored`
- `Ready for an upgrade`, not `Cutoff unmet`
- `Choose a release`, not `Interactive search`
- `Release rules`, not scattered profile jargon
- `Background work`, not `Jobs`
- `Needs attention`, not `Failed` unless the failure is truly terminal

## Build order

### Phase 1: make the app feel like a product

- finish user-friendly copy pass
- add Libraries page
- add Connections page
- add release-rule placeholders
- redesign Activity into a stronger timeline

### Phase 2: build the missing Deluno behavior

- recurring schedules
- cooldown log
- real missing and upgrade state
- dashboard counts from true eligibility, not placeholders
- manual search-now actions

### Phase 3: cover advanced library setups

- multiple movie libraries
- multiple TV libraries
- anime and daily TV handling
- per-library schedules
- per-library rules

### Phase 4: migration and companion support

- import from older setups where it is useful
- keep Deluno's own rules and services as the primary model
- ingest or recreate the useful Fetcher behavior

## Recommended next implementation slice

Build these next in order:

1. `Libraries` domain and UI
2. `Indexers` domain and UI
3. `Schedules` and `RetryDelay` model in `platform.db` and `jobs.db`
4. real `Wanted` and `Ready for an upgrade` snapshots
5. native recurring missing and upgrade search runner

## Sources

Official sources reviewed on 2026-04-21:

- Servarr FAQ and settings pages for the current media automation ecosystem
- Servarr wanted-queue and profile documentation

Local product reference reviewed:

- `C:\Users\User\Fetcher\README.md`
- `C:\Users\User\Fetcher\app\arr_client.py`
- `C:\Users\User\Fetcher\app\service_logic.py`
- `C:\Users\User\Fetcher\CHANGELOG.md`
