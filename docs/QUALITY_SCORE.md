# Deluno Quality Score

Updated: 2026-04-30

This file tracks product and architecture quality in a way agents can update continuously.

## Current Grade

Overall: B-

The core app is coherent and validated by backend tests, web build, and Playwright smoke coverage. The largest remaining risk is that live integration state is still more pull-driven than persisted/evented.

## Domain Scores

| Domain | Score | Notes |
| --- | --- | --- |
| Services/Broker | B- | Indexer and client setup is guided. Download-client capabilities are normalized. Needs persisted telemetry and deeper torrent-client history. |
| Queue/Imports | B | Queue, import preview, manual import, recovery, and refine-before-import are connected. Needs evented state and exact client-item import outcome records. |
| Movies | B- | Search/grab/import paths exist. Needs more end-to-end scoring explanation and replacement protection coverage. |
| TV | B- | Episode/search flows exist. Needs stronger episode-level import and monitoring coverage. |
| Metadata | C+ | TMDb-first direction is present. Broker-ready paths need hardening and clearer fallback behavior. |
| UI System | B | Dense, premium surface exists. Needs continued consistency around typography, menus, route loading, and health/action affordances. |
| Agent Readiness | C+ | Agent map and validation now exist. Needs more mechanical architecture checks, observability hooks, and execution-plan discipline. |

## High-Interest Debt

- Persist normalized download-client telemetry snapshots and last-poll metadata.
- Emit SignalR queue/import telemetry changes instead of relying on page reloads.
- Expand per-client history adapters and mark queue-derived history distinctly.
- Add architecture validation for project references and duplicated frontend status strings.
- Add local app boot/health scripts that produce agent-readable logs and URLs.

## Update Rule

When a change materially improves or worsens a domain, update this file in the same commit or add an item to `docs/exec-plans/tech-debt-tracker.md`.
