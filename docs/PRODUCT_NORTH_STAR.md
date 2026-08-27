# Deluno product north star

## Purpose

Deluno is the single, local control plane for a personal media library. It helps a user decide what belongs in their library, find it, acquire it, verify it, import it, recover from failures, and understand every decision.

The product goal is to replace the *workflow* currently spread across Radarr, Sonarr, Prowlarr, SABnzbd or a torrent client, Huntarr, CleanUpArr, Configarr, and Recyclarr. It must do this as one coherent application, not as a set of lookalike screens or a stitched collection of services.

## The promise

> Tell Deluno the library you want. Deluno creates and operates the safe, explainable automation plan behind it.

Deluno is simpler because a user configures intent rather than a chain of technical tools. It is more powerful because that intent can be adapted per scenario, library, and title without losing visibility or control.

## Product shape

Deluno has two connected experiences:

1. **Dashboard** — the everyday home for browsing, adding, monitoring, searching, choosing releases, inspecting files, correcting metadata, resolving problems, and managing movies and TV.
2. **Library setup** — the deliberate configuration space for media plans, sources, downloads, storage/import, schedules, discovery, notifications, and advanced overrides.

The Dashboard is the default destination after setup. Library setup is powerful but never required for ordinary daily use.

## The user workflow

```text
Choose a library scenario
  -> Select or accept a media plan
  -> Connect sources and downloads when needed
  -> Deluno previews the resulting plan
  -> Add and manage media in the library
  -> Review decisions, exceptions, and activity
```

Every automated action must have a plain-English explanation: what Deluno did, why it did it, what rule or plan applied, and how the user can change that rule.

## Scenario-first configuration

Start with understandable scenarios, such as Family 1080p, Premium 4K HDR, Low Storage, Usenet-first, Private Tracker, or Mixed Sources. Advanced users can refine every part of the plan.

Policies layer safely:

```text
Global safety baseline
  -> Scenario/media plan
    -> Library override
      -> Individual title override
```

Changes must be previewable, attributable, and reversible. Deluno must never silently replace a user’s deliberate local choices.

## Capability intent

- **Movies and TV:** separate, capable media engines presented as one library workstation.
- **Sources and routing:** direct indexer/source management and library-aware routing; no dependency on syncing configuration into third-party Arr apps.
- **Downloads:** Deluno orchestrates external download clients. SABnzbd, NZBGet, qBittorrent, Transmission, Deluge, and similar apps own protocol work, queueing, repair, unpacking, retention, and seeding; Deluno dispatches, observes, imports, routes, and recovers media around them.
- **Search automation:** recurring, rate-limited missing and upgrade searches with budgets, retries, queue protection, and clear reasoning.
- **Import lists and discovery:** follow user-selected watchlists and curated lists (for example Trakt, IMDb, TMDb, RSS, or a plain URL) with per-list routing, filters, reviewable sync outcomes, duplicate protection, and an explicit choice between adding a title and automatically searching for it.
- **Media plans:** guide-informed quality, format, naming, and routing policies with versioning, diff/preview, local overrides, and rollback.
- **Recovery and cleanup:** imports, failed downloads, stalled or blocked releases, malware-like file patterns, seeding retention, orphaned download files, safe removal, replacement searches, retry and remediation belong in the same activity story as normal automation. Destructive cleanup is always explainable, previewable, and scoped to media Deluno can prove it owns.

## The bar, in James's words

This is [#194](https://github.com/jampat000/Deluno/issues/194), recorded here so
it cannot be lost with the issue. It was opened saying the whole point could be
forgotten, and asking that it be embedded somewhere permanent and checked
against constantly. This is that place.

> Now I want us to not forget and look at what this app is to be used for and
> what it will be replacing and doing it better. Radarr, Sonarr, Huntarr,
> Cleanuparr, Recyclarr, Upgradarr, Prowlarr and Trash Guides etc etc. We need
> to ensure we maintain and push beyond the level of standards of each of those
> apps — we need to do what they do but better and we need to make it look
> better and work better. […] This app needs to be the only app media buffs ever
> need for their unlimited personal media.

So the bar is not "Deluno has a feature for that". It is: **someone who uninstalls
all eight apps must not miss any of them.**

| It replaces | Which means Deluno owes |
|---|---|
| **Radarr** | movies: catalogue, quality profiles, search, import, rename, recovery |
| **Sonarr** | TV, at season and episode grain, with air dates and monitoring |
| **Prowlarr** | indexers managed in one place, with health, tests and rate limits |
| **Huntarr** | finding what is missing and what could be better, on a schedule that does not hammer trackers |
| **Cleanuparr** | stalled, blocked, malware-shaped and orphaned downloads, cleaned up safely |
| **Recyclarr / Trash Guides** | quality definitions and custom formats that are good by default, without importing YAML |
| **Upgradarr** | upgrades to a cutoff, and stopping there |
| **Bazarr** *(added by [#301](https://github.com/jampat000/Deluno/issues/301))* | subtitles: languages, providers, upgrades, and knowing what you already have |

### The standing check

Every piece of work answers these before it is called done. This is the "constantly
checked" #194 asked for, and it is why the issue can close: the check outlives it.

1. **Which of those apps does this belong to, and is Deluno's version better —
   not merely present?** A feature that exists but is worse than the tool it
   replaces is a reason someone reinstalls that tool.
2. **Is it simpler than the thing it replaces?** Simplicity is the product. If
   the answer is a new setting, ask first whether Deluno can decide and explain
   the consequence once in plain words.
3. **Is this rule already written somewhere else?** Every defect worth finding in
   this codebase so far has been one rule written twice in two places that could
   not check each other. After a fix, find where else that shape lives.
4. **Has it been seen working, on real software, with real data?** A green test
   suite has never yet been the thing that found these.
5. **Was it measured?** Memory, CPU and schedules are part of the product.
   Routes and functions inside Deluno must not fight each other for either.

## Non-negotiable experience rules

1. Operate by intent, not Arr terminology.
2. Hide complexity without hiding power.
3. Make the next useful action obvious.
4. Keep setup separate from daily media management, while linking decisions in both directions.
5. Explain every consequential decision and provide a safe path to change it.
6. Never overstate readiness: implemented, tested, and production-proven are distinct states.

## How to use this document

Treat this as the product contract for UI, API, automation, documentation, and release decisions. New work should strengthen the media library, improve scenario-based automation, or make the decision loop safer and easier to understand. Work that merely reproduces a legacy tool’s complexity needs an explicit reason.
