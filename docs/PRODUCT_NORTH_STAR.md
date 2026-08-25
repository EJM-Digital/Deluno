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

## Non-negotiable experience rules

1. Operate by intent, not Arr terminology.
2. Hide complexity without hiding power.
3. Make the next useful action obvious.
4. Keep setup separate from daily media management, while linking decisions in both directions.
5. Explain every consequential decision and provide a safe path to change it.
6. Never overstate readiness: implemented, tested, and production-proven are distinct states.

## How to use this document

Treat this as the product contract for UI, API, automation, documentation, and release decisions. New work should strengthen the media library, improve scenario-based automation, or make the decision loop safer and easier to understand. Work that merely reproduces a legacy tool’s complexity needs an explicit reason.
