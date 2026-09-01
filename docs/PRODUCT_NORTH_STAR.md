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

Each of these was read from its own documentation and source rather than from
memory, because a replacement list nobody has checked is a list of assumptions.
What each one actually does, and therefore what Deluno owes:

| It replaces | What that tool actually does | So Deluno owes |
|---|---|---|
| **Radarr** | Movie PVR: catalogue, quality profiles and custom formats, RSS and interactive search, import and rename, failed-download handling, collections, calendar, lists | All of it, at 5,000+ titles, with filtering and sorting past what Radarr offers ([#306](https://github.com/jampat000/Deluno/issues/306)–[#310](https://github.com/jampat000/Deluno/issues/310)) |
| **Sonarr** | The same for TV, at series/season/episode grain: specials, multi-episode releases, air dates, scene numbering | Episode-grain everything, not a movie engine with seasons bolted on |
| **Prowlarr** | Indexer **manager and proxy**: 24 usenet indexers, 500+ trackers, Generic Newznab/Torznab, Cardigann YML definitions, per-indexer proxy (SOCKS4/5, HTTP, FlareSolverr), health, history, stats. Syncs config *into* the arrs; downloads nothing itself | Indexers configured once, with health, tests, rate limits and per-indexer proxying — and **no sync step**, because there is nothing to sync into |
| **Huntarr** | Actively hunts library gaps the arrs' RSS never revisits — missing and cutoff-unmet — in small indexer-safe batches with hourly caps, pausing when the queue fills | The library automation cycle: window, interval, `MaxItemsPerRun`, retry delay. **Already built** |
| **Cleanuparr** | Download-side cleanup: strike system for failed imports, stalled and metadata-stuck torrents, low-speed and slow-completion blocks, malware-pattern blocking, seed-time purging, orphaned/unlinked file removal. Also triggers replacement searches | Recovery, dead-letter, stalled and blocked handling, orphan cleanup — all explainable and previewable |
| **Recyclarr** | Syncs TRaSH Guides **into** Radarr/Sonarr: quality profiles, custom formats and scores, quality definitions (size ranges per tier), naming schemes. Config-sync only — touches no media | Good defaults **built in**, versioned and previewable, with no YAML and no second tool to run |
| **TRaSH Guides** | Documentation, not software: custom formats, quality/size definitions, naming, and hardlink-safe folder structure | The guidance encoded as defaults you can see and override, not a wiki you have to read first |
| **Upgradarr** | Walks the whole library looking for better releases, one title per cycle, with a configurable pause (default 5 min) so trackers do not ban you | Upgrade search paced by the same cycle, to a cutoff, and stopping there |
| **Bazarr** | Subtitles for whatever Sonarr/Radarr already indexed — 184 languages, 50+ providers, forced/foreign variants, upgrades, history, manual search. **Cannot scan disk itself** | Subtitles as part of the library, not a companion to it ([#301](https://github.com/jampat000/Deluno/issues/301)) |

### Two things that list makes obvious

**Most of these exist only because the arrs are separate applications.** Prowlarr
syncs config into other apps; Recyclarr syncs config into other apps; Bazarr
cannot see the disk and has to ask Radarr what exists; Huntarr and Upgradarr both
exist to do a search the arrs could have paced themselves. Deluno is one
application, so a large part of what these tools *are* simply does not arise —
that is the saving, and it is also the risk, because rebuilding their
architecture inside Deluno would import the problem along with the feature.

**Replacing a tool means replacing its ceiling too.** Radarr states in its own
Custom Filters dialog that filters are "available only for the properties of a
movie, they are not available for properties of the file(s) you may have". Doing
what Radarr does is the floor. See DESIGN-004.

### The standing check

Every piece of work answers these before it is called done. This is the "constantly
checked" #194 asked for, and it is why the issue can close: the check outlives it.

1. **Which of those apps does this belong to, and is Deluno's version better —
   not merely present?** A feature that exists but is worse than the tool it
   replaces is a reason someone reinstalls that tool.

   **"Better" includes the count.** James, on being shown that Deluno had 6
   filter fields to Radarr's 33 while gaining an axis Radarr does not have:
   *"shouldn't we add the missing and more as you suggested — instead of being
   ahead we will still be behind."* Right. A new axis does not excuse a smaller
   number on an old one. Where a tool offers N of something, Deluno offers all N
   and then more.
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
7. Treat a title disappearing from an external metadata provider as a recoverable,
   title-scoped condition—not a system emergency. Keep the title, files, history,
   monitoring, and local metadata; let the user dismiss the unchanged evidence;
   offer retry or remap; and make removal a separate, deliberate choice.

## How to use this document

Treat this as the product contract for UI, API, automation, documentation, and release decisions. New work should strengthen the media library, improve scenario-based automation, or make the decision loop safer and easier to understand. Work that merely reproduces a legacy tool’s complexity needs an explicit reason.
