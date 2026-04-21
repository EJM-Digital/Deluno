# Weir Behaviors To Preserve In Deluno

Updated: 2026-04-22

This document captures the Deluno-relevant behavior extracted from the `Weir` repo before it is removed.

Deluno is not copying Weir's product boundaries. It is preserving the behavior that matters.

## Fetcher Behaviors To Preserve

### Independent recurring search lanes

Fetcher keeps these lanes independent:

- movie missing
- movie upgrades
- TV missing
- TV upgrades

That isolation matters. Retry windows and schedule decisions should never bleed between those lanes.

### Cooldown memory per lane and item

Fetcher records action attempts keyed by app, action, item type, and item id, then filters future candidates by a lane-specific cooldown window.

Deluno should preserve that principle as:

- per-library cooldown
- per-media-type cooldown
- per-action cooldown
- per-item retry memory

This is the backbone for recurring search that feels smart instead of noisy.

### Periodic enqueue loop, not synchronous blocking

Fetcher runs periodic enqueue tasks on independent timers and uses short failure cooldowns if the enqueue pass fails.

Deluno should preserve:

- separate scheduling per automation lane
- durable scheduling state in `jobs.db`
- short failure backoff for scheduler faults
- no single global timer that blocks all automation

### Monitored missing drains and capped backlog passes

Fetcher gathers monitored missing entries, applies cooldown filtering, and caps how many items are dispatched per pass.

Deluno should preserve:

- fairness through large backlogs
- per-library max items per run
- paged selection for huge wanted queues
- no uncontrolled "search everything" bursts

### Failed import cleanup planning

Fetcher routes failed import cleanup through app-specific planners instead of pretending movie and TV failures are interchangeable.

Deluno should preserve:

- separate movie and TV import recovery workflows
- separate classification and policy where needed
- a shared product surface, but not a forced shared planner

## Broker Behaviors To Preserve

### Federated search across enabled sources

Broker performs federated search across enabled indexers, filters by protocol and media type, and merges the results.

Deluno should preserve:

- federated source search
- media-aware category routing
- torrent and usenet filtering
- result deduplication
- deterministic ranking

### Source testing and prioritization

Broker uses enabled-only search sources and respects priority ordering.

Deluno should preserve:

- enabled/disabled source control
- priority-aware ranking
- clear source health
- source testing before live use

### Library-aware routing instead of external sync

Broker currently syncs managed indexers into other apps because those apps are separate.

Deluno should preserve the routing intent but improve the design:

- libraries choose which sources they can use
- libraries choose preferred download clients
- routing happens natively inside Deluno
- no external app sync loop should be required

## Deluno Translation

These Weir behaviors become Deluno features like this:

- `Fetcher recurring search` -> built-in automation inside `Movies` and `TV Shows`
- `Fetcher failed import cleanup` -> built-in `Import Recovery` inside `Movies` and `TV Shows`
- `Broker indexers and search` -> built-in `Indexers`
- `Broker sync logic` -> replaced with native Deluno library routing

## Files Reviewed

The behavior summary above was extracted from these Weir files:

- `.tmp/weir/apps/backend/src/weir/modules/fetcher/fetcher_arr_search_periodic_enqueue.py`
- `.tmp/weir/apps/backend/src/weir/modules/fetcher/fetcher_arr_search_selection.py`
- `.tmp/weir/apps/backend/src/weir/modules/fetcher/failed_import_cleanup_orchestration.py`
- `.tmp/weir/apps/backend/src/weir/modules/broker/broker_search_service.py`
- `.tmp/weir/apps/backend/src/weir/modules/broker/broker_arr_indexer_sync.py`
