# Deluno setup order

This is the setup journey derived from the [end-to-end media workflow](DELUNO_END_TO_END_WORKFLOW.md).
It is the canonical order shown by the setup overview. Deep links and
advanced pages remain available, but the readiness result must reflect the
whole workflow rather than only local library configuration.

## Ordered journey

### 0. Establish the installation baseline

Confirm the first account, writable application data, media roots,
download/import paths, backup expectations, and system health.

Exit condition: Deluno can start safely and explain where its data and media
will live.

### 1. Configure Library & storage

Create the movie and/or TV libraries. Choose final destinations, naming,
metadata output, import behaviour, and any processor hand-off.

Exit condition: at least one intended media library has a valid destination
and import policy.

### 2. Choose the Media Plan

Choose a scenario or plan, then review quality, size, release, naming, routing,
and upgrade behaviour. Advanced controls refine this plan; they do not replace
it with a separate setup path.

Exit condition: every enabled library has a plan that can explain what it will
accept, reject, hold, and upgrade.

### 3. Connect and test Find & download

Configure at least one search source and at least one download client for the
intended acquisition path. Test both, verify capabilities and path mappings,
and review the health result.

Exit condition: Deluno can find a compatible candidate and dispatch it to a
real external client. A missing or unhealthy source/client keeps setup
incomplete.

### 4. Configure Automation & recovery

Choose schedules, missing/upgrades behaviour, retries, queue protection,
failed-import recovery, notifications, and safe cleanup boundaries.

Exit condition: Deluno can run the acquisition loop, expose its current and
next actions, and present a safe recovery path for failures.

### 5. Configure Discover media (optional)

Add import lists or watchlists only when useful. Review provenance, filters,
exclusions, duplicates, and whether list results should be searched
automatically.

Exit condition: configured lists produce reviewable results. Skipping this
step never blocks operational readiness.

### 6. Prove the first end-to-end flow

Add or discover a first title, search it, inspect the decision explanation,
dispatch a release, observe the external client, import the result, and verify
the catalogue and activity trail.

Exit condition: the complete supported path has passed with evidence, including
the recovery behaviour relevant to the chosen environment.

## Readiness model

The setup overview should show separate outcomes rather than one misleading
percentage:

| Outcome | Required conditions |
| --- | --- |
| Library configured | Step 1 is complete. |
| Acquisition ready | Steps 1–3 are complete, including healthy source and client checks. |
| Operationally ready | Steps 1–4 are complete and Step 6 has passed. |
| Discovery configured | Step 5 is complete; this is optional. |

The overall setup journey is complete only at **Operationally ready**. Manual
title entry and existing-library import remain useful paths, but they do not
hide a missing acquisition setup.

## UI rules for the setup journey

- Show one canonical ordered journey; do not create a second competing tree.
- Each step shows its purpose, current evidence, next action, and owning page.
- Use `Complete`, `Next`, `Needs attention`, and `Optional` deliberately.
- A healthy-looking library must not override a missing source or client.
- Optional import lists are visible but never included in the blocking count.
- A paused automation state is explicit and cannot be presented as fully
  automated.
- The first-flow proof is server-backed and is recorded only after a
  dispatch-bound download has completed a catalogued import; there is no
  "mark complete" checkbox.
- The setup guide may accelerate first-run entry, but the setup overview is
  the durable source of truth after the wizard closes.
- Every status comes from real server-backed state and real connection checks;
  no static checklist item may claim readiness by itself.

## Relationship to issues #158 and #194

- **#158 — Order of setup:** implement this journey and its readiness model in
  the setup overview and guided setup surfaces.
- **#194 — Overall:** use the end-to-end workflow and evidence requirements as
  the product-wide quality bar. It is a final-state contract, not a promise
  limited to a numbered release.
