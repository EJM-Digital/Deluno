# Deluno end-to-end media workflow

Status: product contract for the final-state Deluno workflow.

Deluno is the single control plane for a personal media library. Its job is
not complete when a folder exists or a title is visible in a catalogue. The
full workflow must take a media intention through discovery, acquisition,
verification, import, recovery, and explanation.

## The promise

The user tells Deluno what library they want. Deluno creates and operates a
safe, explainable plan that finds suitable releases, sends approved work to
external download clients, verifies the result, imports it, and keeps the
library healthy.

There is no v1/v2 split in this contract. A capability is part of the final
product promise only when its supported workflow is implemented, tested, and
production-proven. A fixture-only result is not live-provider proof, and a
passing build is not workflow proof.

## Canonical workflow

```text
Establish the installation baseline
  -> Define libraries, storage, and media intent
  -> Select the Media Plan and quality behaviour
  -> Connect and verify search sources and download clients
  -> Configure automation, recovery, and notifications
  -> Optionally configure discovery/import lists
  -> Add or discover a title
  -> Search and explain candidate decisions
  -> Dispatch an approved release to an external client
  -> Observe download, processing, and health state
  -> Verify, import, rename, and catalogue the media
  -> Explain the outcome and keep searching for missing/upgrades
```

Each arrow is a real product hand-off. The UI must show the current state,
the next useful action, the rule or plan that caused the decision, and the
safe recovery path when the hand-off fails.

## Workflow stages

### 1. Establish the installation baseline

The user creates the first account and confirms that Deluno can run safely:

- the application is authenticated and bound to its intended local surface;
- writable application data, media roots, and download/import paths are
  distinct and writable;
- backups, restore expectations, and update/rollback behaviour are visible;
- system health and diagnostics are available without exposing secrets.

This stage does not make the installation media-ready by itself.

### 2. Define libraries, storage, and media intent

The user creates movie and/or TV libraries, chooses final destinations, and
decides naming, metadata output, import behaviour, and optional processor
handoff. Existing media can be imported, but that is only one part of the
product and does not replace acquisition readiness.

Each library's import workflow also owns its source cleanup rule. The safe
default is to keep the completed source file. A user may instead choose to
remove that source only after Deluno has verified the import, and may opt to
remove folders that are genuinely empty afterwards. Deluno never removes the
configured download root, and it does not silently take over queue retention
or seeding decisions that belong to the external download client.

### 3. Select the Media Plan

The user chooses an understandable scenario or plan, such as balanced 1080p,
premium 4K, family-friendly, or storage-friendly. The plan owns quality, size, release,
naming, routing, and upgrade behaviour. Advanced controls remain available
behind the same decision rather than becoming a second unrelated setup system.

Every consequential decision must be explainable and reversible:

```text
Global safety baseline
  -> Media Plan
    -> Library override
      -> Title override
```

### 4. Connect and verify acquisition services

An installation is not ready to do the product's primary job until it has at
least one enabled, healthy search source and at least one enabled, healthy
download client for the intended workflow.

Deluno must:

- identify source and client capabilities instead of guessing from names;
- validate addresses, credentials, protocol, and path mappings;
- show the last real test and current health state;
- route an accepted candidate to a compatible client;
- preserve the boundary that the external client owns transfer, queueing,
  repair, unpacking, retention, and seeding.

Manual title entry remains available when metadata services are unavailable,
but skipping acquisition services is an incomplete setup state, not a green
"ready" result.

### 5. Configure automation, recovery, and notifications

The user chooses search schedules, upgrade rules, retry windows, queue
protection, failed-import handling, health thresholds, notifications, and
safe cleanup proposals. Automation may be paused deliberately, but Deluno
must make the consequence visible and must not describe a paused installation
as fully automated.

Recovery is part of the normal workflow. Deluno classifies failures in plain
language, preserves evidence, offers a safe action, and never deletes media
without ownership evidence and an explicit boundary.

### 6. Optionally configure discovery/import lists

Import lists and watchlists are optional. Users may add titles manually and
still use the complete acquisition workflow. When lists are configured,
Deluno must provide provenance, exclusions, duplicate protection, preview or
review where appropriate, and an explicit choice about automatic searching.

Optional does not mean invisible: the setup surface should show that this
capability is available and why it is not blocking readiness.

### 7. Execute the acquisition loop

For a missing or upgrade candidate, Deluno must be able to:

1. identify the wanted movie, show, season, or episode;
2. search compatible sources within rate and queue budgets;
3. score candidates against the active Media Plan;
4. explain accepted, held, rejected, and forced decisions;
5. dispatch the accepted release to the selected external client;
6. observe queue, progress, errors, completion, and import readiness;
7. coordinate processing or refine-before-import when configured;
8. verify the resulting file and resolve its final destination;
9. import without overwrite, record the decision trail, and update the
   catalogue;
10. notify the user and schedule the next missing or upgrade action.

Import readiness is deliberately conservative: a completed status from the
download client is not enough on its own. Deluno checks that the source file
has a non-zero size, is old enough to have settled, can be opened for reading,
and has not changed while it is checked. If the file is still being copied or
locked, Deluno leaves it alone and retries instead of probing, importing, or
creating a false recovery failure.

The same loop must work for movie and TV engines while keeping their internal
ownership separate.

### 8. Operate and recover continuously

After the first successful import, the product remains responsible for:

- missing and upgrade searches;
- queue, activity, health, and import visibility;
- paused, retrying, blocked, and failed states;
- recovery and requeue actions;
- safe cleanup and retention proposals;
- backups, migration, updates, and rollback evidence;
- explanation of what Deluno did, why it did it, and how to change the rule.

## Readiness language

The product must distinguish these states:

| State | Meaning |
| --- | --- |
| Library configured | Destinations and basic media handling exist. |
| Acquisition ready | A compatible healthy source and download client have passed real checks. |
| Operationally ready | Acquisition, automation, recovery, and observability are configured and the first end-to-end flow has passed. |
| Discovery configured | Optional lists or watchlists are configured and producing reviewable results. |

Only **Operationally ready** may be presented as the completed setup outcome.
An installation without an enabled healthy source or download client must
remain visibly incomplete, even if it can hold media in a library.

## Evidence required for the final-state promise

Every supported scenario needs a record of configuration, expected outcome,
actual outcome, and recovery evidence for:

- movie-only, TV-only, and mixed libraries;
- torrent and Usenet acquisition paths where supported;
- accepted, held, rejected, and manually forced decisions;
- move, hardlink, and copy imports, including collision and restart recovery;
- paused libraries or instances, retry windows, and safe resume;
- health findings and human-approved remediation proposals;
- existing-library/configuration migration without overwrite;
- clean Windows install, upgrade, rollback, backup/restore, and soak flows.

The final-state promise is maintained by evidence, not by comparing screen
counts with another application. The named replacement tools are useful
reference points, but Deluno's acceptance bar is the complete, explainable,
safe workflow above.
