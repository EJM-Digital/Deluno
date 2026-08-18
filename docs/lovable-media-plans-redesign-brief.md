# Lovable UX Handoff: Media Plans + Library Setup Context (Do Not Break Existing App)

## Goal for the next design pass

Build a cleaner visual flow for Deluno media plan setup without changing behavior:

- one-screen for beginners
- clear expansion for advanced settings
- reusable scenarios + editable defaults
- no scattered boxes, inconsistent card sizes, or random layout jumps
- preserve existing routes, API contracts, and assignment semantics

Use this as the non-negotiable source of truth before generating any UI proposal.

---

## 1) Product contracts to preserve

### Non-negotiable architecture

- Deluno has two primary experiences:
  - **Dashboard / media management** (daily use)
  - **Library setup** (folders, plans, connections, automation decisions)
- Keep this split in copy, navigation, and screen intent.

### Routes that must remain coherent

- `/settings/policy-sets` (media plan editor + saved plans)
- `/settings/libraries` (create/edit libraries + default plan assignment)
- `/` and `/dashboard` (setup status + “needs attention” panel)
- `/indexers` (connections and health)
- `/settings/automation` (search/retry/upgrades scheduling)

### Backend contracts that must stay intact

- Policy sets:
  - `POST /api/policy-sets`
  - `PUT /api/policy-sets/{id}`
  - `DELETE /api/policy-sets/{id}`
  - `GET /api/policy-sets`
- Policy-set fields:
  - `name`, `mediaType`, `qualityProfileId`, `destinationRuleId`, `customFormatIds`
  - `searchIntervalOverrideHours`, `retryDelayOverrideHours`, `upgradeUntilCutoff`, `isEnabled`, `notes`
- Library assignment:
  - `PUT /api/libraries/{id}/media-plan` with `{ policySetId?: string | null }`
- Keep direct fallback path:
  - `PUT /api/libraries/{id}/quality-profile`

### Contract behavior to preserve

- A Media Plan is the source-of-truth for quality/size/release/upgrade/search timing defaults.
- `DefaultPolicySetId` on `LibraryItem` is how a library inherits default decisioning.
- Updating a policy can reflow assigned libraries (via existing backend behavior).
- Do not remove fallback quality assignment from `/settings/libraries`.

---

## 2) Existing UI ownership and data sources

### `/settings/policy-sets`

Current ownership in the app:

- Plan creation/edit form
- Starter template injection (`Blank custom plan`, editable defaults)
- Library assignment from a plan detail form
- Advanced controls (search timing, retry delay, release preference chips, notes)
- links into:
  - `/settings/profiles`
  - `/settings/quality`
  - `/settings/custom-formats`
  - `/settings/destination-rules`

Behavior currently in code:

- media type switch resets type-incompatible selections
- toggles: `Enabled`, `Upgrade until cutoff`
- list of libraries currently using this plan with quick edit/remove

### `/settings/libraries`

Current ownership in the app:

- library media type + folder + optional downloads folder
- default plan selection:
  - saved enabled plans
  - editable starter templates
  - “Custom Media Plan...” deep link
- starter can auto-create a policy set when needed on create/save
- direct-quality fallback remains a secondary flow
- per-library default policy assignment in list cards

### Dashboard attention dependencies

`/dashboard` must continue showing setup attention for:

- no library
- missing enabled indexers/download clients
- no active media plans
- automation paused

Keep `/apps/web/src/lib/setup-status.ts` behavior and action text shape.

---

## 3) Naming and copy constraints

Keep these visible nouns:

- Media plan
- quality
- size
- release preferences
- upgrade
- default media plan
- direct quality profile fallback (as explicit advanced/simple exception)

Avoid adding technical decision points to the beginner path.
Use plain decision language:

- “Choose starter” / “Use this template”
- “Fine-tune” for advanced sections only
- “Used by” and clear “No libraries assigned” states

---

## 4) Visual and layout constraints (mandatory)

Do not replace Deluno spacing and density system.
Use existing tokens:

- `--content-pad-inline`, `--content-pad-block`
- `--page-gap`
- `--grid-gap`
- `--card-pad-x`, `--card-pad-y`
- `--tile-pad`
- density variants from existing CSS

Rules:

- uniform panel rhythm (no one-off oversized containers)
- stable control heights and labels
- one sidebar/content pattern and single setup shell behavior
- preserve light/dark behavior
Use status colors from theme tokens and existing system tone classes:

- `--primary`, `--warning`, `--success`, `--info`, `--destructive`, surfaces

Navigation/icon language should remain aligned with existing glyph/color pack (`deluno-nav-glyph`).

---

## 5) Recommended render layout shape (no behavior change)

For `/settings/policy-sets`:

1. Template lane (starters/custom)
2. Baseline form lane (basics + toggles + assignment)
3. Advanced lane collapsible (search/retry + release preferences + notes)
4. Summary lane (plan outputs + linkouts to rule surfaces)
5. Saved plans section below with clear usage badges

For `/settings/libraries`:

1. Primary add-library form at left
2. Compact right rail for existing libraries
3. Destination exception as an exception card, not primary path

---

## 6) Copy-paste prompt block for external design tools

Use this exact starting prompt:

```
You are redesigning `/settings/policy-sets` and `/settings/libraries` in Deluno without changing behavior.
Keep existing API contracts and navigation. Do not alter what endpoints are called or payload fields.

Requirements:
- Preserve current decision logic for saving/updating plans and library assignments.
- Preserve direct quality profile fallback in `/settings/libraries`.
- Preserve setup-attention triggers in dashboard.
- Keep light/dark mode and density variants.
- Use the current spacing rhythm and component baseline from the app.

Current flow must still work:
- media plan create/edit and library assignment in `/settings/policy-sets`
- starter templates and custom plan entry
- direct assignment and re-use of plans across libraries
- per-library default plan assignment in the libraries screen

Output: 2-3 polished layout variants for each screen with:
- structure map for each zone
- what existing behavior each zone maps to
- no behavior changes
```

---

## 7) Post-design validation checklist

Before accepting a visual proposal, verify:

- create/update media plan still hits `/api/policy-sets`
- library assignment still updates via `/api/libraries/{id}/media-plan`
- starter selection still works from both library create and plan edit flows
- direct quality fallback still functions
- setup-status rows still reflect real backend state and actions
- no route or API contract changes in code
- spacing and density remain coherent in both themes
