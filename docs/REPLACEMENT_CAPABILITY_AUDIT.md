# Replacement capability audit

This audit turns Deluno's replacement vision into capability boundaries. It is not a promise to clone every legacy screen. The goal is to retain the workflows users rely on while presenting one media-first product.

## Product boundary

Deluno owns the user intent, policies, discovery, search decisions, routing, safe import, recovery, and explanation.

Mature transfer engines remain integrations until Deluno's own equivalent is production-proven. In particular, SABnzbd and torrent clients already own protocol-heavy queue, repair, unpacking, retention, and seeding behaviour. Deluno must observe and orchestrate those systems rather than claim it has replaced them prematurely.

## Capability map

| Legacy product | User job worth preserving | Deluno shape | Current status / governing issue |
| --- | --- | --- | --- |
| Radarr | Manage movies, quality targets, upgrades, candidates, import, rename, calendar, and history | Movies is a first-class library surface; plans and Transfers explain the acquisition path | Foundation exists; canonical policy and supported-flow proof remain #88 and #93 |
| Sonarr | Manage shows, episodes/seasons, missing episodes, upgrades, manual import, calendar, and history | TV is a first-class library surface sharing the same policy and transfer story as Movies | Foundation exists; supported-flow proof remains #93 |
| Prowlarr | Configure, test, tag, route, and diagnose search sources across media apps | Connections owns sources, download clients, routing, testing, and health | Existing foundation; app/profile parity and real provider proof are part of #93 |
| SABnzbd / torrent clients | Perform the actual transfer, protocol work, queueing, repair/unpack, category and seeding management | External download-client connection with telemetry, dispatch, queue actions, and guarded cleanup | Keep as integration by default; do not claim full replacement until #93 and #78 evidence |
| Huntarr | Revisit missing or below-goal media safely and automatically | Explainable search cycles with schedule, budget, backoff, pause, retry, and title overrides | #90 |
| CleanUpArr | Identify unhealthy downloads, strike them, safely clean owned data, and request a replacement | Download health and cleanup policy within Transfers, Activity, title details, and plans | #95 |
| Recyclarr / Configarr / TRaSH Guides | Apply reusable quality, size, custom-format, naming, and media-management guidance with a safe update story | Bundled curated Media Plan preferences: scenario first, visible provenance/diff, local overrides, and rollback | #88 |
| Radarr / Sonarr Import Lists | Turn trusted watchlists and curated lists into managed titles without accidental library damage | Import Lists feed titles into the same library/plan/automation model as manual add | Existing fetch foundation; completion and safety work #100 |

## Import Lists

Import Lists are a discovery source, not an instruction to delete or reconcile an existing library. Each list must make four choices understandable:

1. **What it follows:** a Trakt, IMDb, TMDb, MDbList, RSS/Atom, Letterboxd-compatible, or plain public title list.
2. **Where matching titles go:** Movies or TV, a target library, and eventually an effective Media Plan.
3. **What happens on discovery:** review first, add only, or add and let normal automation search.
4. **How it stays safe:** duplicate detection, exclusions/temporary ignore, provenance, last result, and no destructive reconciliation by default.

Deluno currently supports TMDb list IDs/URLs, IMDb list/export URLs, Trakt public lists and watchlists through RSS, generic RSS/Atom/Letterboxd feeds, public plain text lists, and MDbList public list URLs. The ordinary Deluno flow mirrors Radarr: choose **Custom list URL**, paste the public MDbList URL, then choose the library and automatic-add behaviour. Deluno uses MDbList's documented Radarr/Sonarr-compatible public response without exposing or requiring an MDbList key. The authenticated MDbList API remains a legacy/advanced path for future account-only capabilities.

The remaining list work is explicitly tracked in #100: validation and read-only preview, selective approval, stable provider provenance, durable exclusions, authenticated personal-list flows, and Arr migration reporting.

## Shared workflow

```text
Manual add or Import List discovery
  -> library route + effective Media Plan
  -> candidate search and explainable decision
  -> external download client
  -> optional external processing into a watched, mapped output path
  -> safe import, rename, and library update
  -> Activity and recovery evidence
```

Every stage must state the next action and its reason. A title discovered from a list must never bypass plan constraints, connection readiness, automation budgets, or user overrides.

## Usability rules

- Use familiar terms where they reduce onboarding cost: **Import Lists**, **Search sources**, **Download clients**, **Quality**, **Custom formats**, **Transfers**, and **Activity**.
- Explain those terms in plain language in the moment rather than forcing Arr knowledge.
- Keep routine daily work in Dashboard, Movies, TV, Transfers, and Activity. Keep configuration decisions in Library setup.
- Present scenarios and safe defaults first; preserve granular controls under a clear advanced path.
- Never report fabricated health, capacity, activity, queue, or historical data. Unknown and not configured are valid states and must be shown honestly.
- Make destructive actions previewable, attributable, opt-in, and constrained to data Deluno can prove it owns.

## Primary source material

- [Radarr settings and Import Lists](https://wiki.servarr.com/radarr/settings)
- [Sonarr quick-start and import model](https://wiki.servarr.com/en/sonarr/quick-start-guide)
- [Prowlarr API surface](https://prowlarr.com/docs/api/)
- [CleanUpArr supported applications and safety controls](https://github.com/Cleanuparr/Cleanuparr/blob/main/README.md)
- [Recyclarr sync pipeline behaviour](https://recyclarr.dev/guide/sync-behavior/)
- [Configarr configuration and TRaSH template support](https://configarr.de/docs/configuration/config-file/)
- [MDbList API overview](https://docs.mdblist.com/docs/api) and [cursor pagination announcement](https://mdblist.com/new-features/)
- [TRaSH Guides repository](https://github.com/TRaSH-Guides/Guides)
