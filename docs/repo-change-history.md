# Deluno Repo Change History

Updated: 2026-05-14

## Purpose

This is the repo-forensics summary for the current `main` branch.
It answers: what changed recently, what shipped, and what users now see.

## Current Snapshot

- branch: `main`
- remote tracking: `origin/main`
- divergence: none
- open GitHub issues: none
- latest tag/release: `v0.1.5` (pre-release)

Release URL:

- <https://github.com/jampat000/Deluno/releases/tag/v0.1.5>

## Change Window Covered

This pass summarizes the production path from `v0.1.0` through `v0.1.5`.

Tags in order:

- `v0.1.0`
- `v0.1.1`
- `v0.1.2`
- `v0.1.3`
- `v0.1.4`
- `v0.1.5`

## What Changed Since v0.1.0

### 1) Windows Installer and Updater Overhaul

Core outcome:

- moved to Velopack-based Windows packaging and in-app update flow
- established setup + package + release-index asset model
- hardened update status/state handling and user controls

User-visible impact:

- reliable `System > Updates` controls
- restart-to-apply model with staged package handling
- clearer install-kind detection and migration visibility

### 2) Migration and Runtime Policy Hardening

Core outcome:

- canonical settings under `%LocalAppData%\Deluno\config\deluno.json`
- legacy `%ProgramData%\Deluno\data\deluno.json` fallback + migration support
- consistent runtime data-root behavior for packaged installs

User-visible impact:

- smoother transition from older/manual installs
- fewer broken-path failures after update

### 3) Reliability and Queue/Dispatch Corrections

Core outcome:

- retry scheduling and worker retry path now wired to real behavior
- updater state-machine reliability tests added
- warning noise cleanup in shared build/test layers

User-visible impact:

- fewer stuck/ambiguous operational states
- cleaner diagnostics when failures happen

### 4) Scoring and Decisioning Expansion

Core outcome:

- shipped ML.NET ranking lifecycle (training + inference + model lifecycle)
- added user-selectable scoring modes:
  - deterministic rules only
  - ML-assisted
  - combined behavior

User-visible impact:

- scoring behavior can match user preference and trust level
- model-assisted ranking is available without removing deterministic fallback

### 5) Routing Intelligence and Operational Insights

Core outcome:

- intelligent client routing preferences and anomaly surfaces
- monitoring dashboard improvements, alerting/export support, diagnostics API growth

User-visible impact:

- clearer "why this route/decision happened"
- improved operational visibility for queue and health troubleshooting

### 6) Docs, Repo Hygiene, and Naming Cleanup

Core outcome:

- removed obsolete updater/installer leftovers and stale public-root artifacts
- removed tool-specific naming/config references from tracked repository files
- refreshed Windows/Docker docs and troubleshooting guidance

User-visible impact:

- cleaner public repo footprint
- lower onboarding friction for Windows and Docker users

## Key Commits in This Window

- `3213c3f` velopack updater flow + dedicated upgrades screens
- `d59c3be` quality model pilot + route split hardening
- `fd4b007` tray settings migration + data-root defaults
- `2363ea6` legacy install migration assistant
- `be0817c` Windows/Docker docs refresh
- `c2863d4` updates UX clarity pass
- `c5c487f` updater state-machine tests
- `84e9cee` ML.NET ranking lifecycle
- `2a702d5` user-selectable scoring modes
- `4d37cc3` real retry scheduling/worker retry pass
- `b4aeedc` warning-noise cleanup in shared build/test props

## Notes For Future Audits

When updating this file, keep it tied to explicit Git refs (tags/commits) and state whether the branch is converged with origin.
