# Deluno Quality Score

Updated: 2026-05-13

This file tracks product and architecture quality in a way agents can update continuously.

## Current Grade

Overall: B

The product surface is materially broader than it was at the end of April: library routing, telemetry, custom-format dry-runs, operational media routes, and external processor coordination are all present. The biggest quality risk is now consistency across that breadth: docs drift, oversized route files, partial realtime coverage, and in-flight modules that are not fully integrated yet.

## Domain Scores

| Domain | Score | Notes |
| --- | --- | --- |
| Services/Broker | B | Indexer and client setup, health checks, routing, telemetry, queue actions, and webhook plumbing exist. Needs deeper client-history fidelity and clearer intelligent-routing maturity. |
| Queue/Imports | B | Queue, import preview, manual import, recovery, and refine-before-import are connected. Needs broader realtime import/recovery updates and exact outcome tracking. |
| Movies | B | Search, grab, metadata actions, wanted, import recovery, and bulk actions exist. Needs richer paging/filter contracts and stronger upgrade/replacement explainability. |
| TV | B- | Episode-aware search and monitoring have improved, and inventory routes exist. Needs tighter episode detail contracts and stronger end-to-end import/recovery coverage. |
| Metadata | C+ | TMDb-first direction and metadata fallback seams exist. Broker-ready fallback behavior still needs hardening and clearer ownership. |
| UI System | B- | Dense operational surfaces, library views, and bulk tooling exist. Contract drift between frontend needs and API shape is now a larger risk than missing UI breadth. |
| Realtime | B- | SignalR is wired and event coverage is wider, but import/recovery/wanted coverage is still incomplete and the event contract is not fully unified. |
| Agent Readiness | B | Repo maps, validation, local boot, completed-plan tracking, and docs breadth are in place. Needs more mechanical validation around architecture concentration and contract drift. |

## High-Interest Debt

- Split oversized endpoint registration files, especially the platform route surface, by concern.
- Broaden realtime coverage for import recovery, wanted-state transitions, and richer operational revalidation.
- Expand per-client history adapters and mark queue-derived history distinctly.
- Keep core docs aligned with implemented APIs, not just desired future shape.
- Decide whether `Deluno.Library` and `Deluno.Search` become stable modules or remain design-only seams.

## Update Rule

When a change materially improves or worsens a domain, update this file in the same commit or add an item to `docs/exec-plans/tech-debt-tracker.md`.
