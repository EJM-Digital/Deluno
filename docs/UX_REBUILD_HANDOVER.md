# Deluno UX rebuild — handover

Branch `ux/list-drawer-media-plans`, 26 commits, not pushed. Nothing here is merged; all of it is reviewable in one place.

## The decision this implements

James's read of the old UI: the icons, logo and colour scheme are right, the **interactive part** — where you change and view things — is not. Two rounds of mockups were reviewed. Round one was rejected ("random, not aligned or uniform… all over the place"): it had seven container shapes, three row anatomies and per-page column sets. Round two was accepted and is the grammar below. He then approved implementing it in real components, page by page, with a full visual pass at the end.

**Do not restyle.** Colours, tokens, glyphs (`deluno-nav-glyph`), density tiers and the logo stay exactly as they are. The work is interaction structure only.

## The grammar — every page obeys this

| Element | Rule |
|---|---|
| Page | No `<h1>`; the topbar already names the page. Suppressed per route with `chrome: "none"` in `settingsPageMeta` (`components/app/settings-shell.tsx`). |
| Toolbar | 40px. Sub-page tabs left, at most two actions right, always "New …" plus one secondary. `components/ui/page-toolbar.tsx`. |
| Card | One kind: `ListCard`, 48px header (title · count · optional filter). Nothing lives outside a card. |
| Row | 56px, one column grammar: `Name+sub │ d1 │ d2 │ d3 │ Status 150px │ On 56px │ › 40px`, via `LIST_TRACK`. Click opens the drawer. |
| Drawer | 600px, fixed anatomy: 72px header → sections in a fixed order (Basics · domain · Fine-tune · Used by/Health · Remove) → 64px footer (status · Cancel · Save). |
| Page forms | Same footer as a drawer but page-level and pinned: `PageFooter`. |
| Fields | `Field` = label → control → one help line. **No per-field boxes.** Controls 36px. Advanced settings behind one `Disclosure` labelled "Fine-tune". |
| Feedback | Footer status (`Unsaved changes` / `Saving…` / `Saved just now`) is the feedback. Toasts only for outcomes that happen away from the open surface. Test results render in the drawer, never as a toast. |
| Dirty state | `useUnsavedChanges` (router blocker + `beforeunload`) plus a discard confirm. |
| Movies / TV | `useMediaTypeSplit` → `MediaTypeFilter` (All · Movies · TV) in the toolbar and sticky group headers in the list. One card, not two. |
| Type scale | 15 title · 13.5 body · 12.5 sub · 11 uppercase label. The label style is shared by table headers, strip labels and drawer section headings. |
| Spacing | 8 · 16 · 24 only. `npm run validate:ui-spacing` gates it. |

## Primitives (`apps/web/src/components/ui/`)

`field` (Field/FieldRow, owns the control id so `<label htmlFor>` is always wired) · `select` · `switch` (Switch/SwitchRow) · `segmented-control` (real radiogroup, arrow keys) · `textarea` · `chip` · `disclosure` · `page-toolbar` · `summary-strip` · `list-card` (ListCard/ListTable/ListRow/ListCell/ListNameCell/ListEmpty/LIST_TRACK) · `drawer` (Drawer/DrawerSection/DrawerDanger/DrawerFooter, `saveEnabled` for tool-style drawers) · `page-footer` · `range-slider` (RangeSlider dual thumb + RangeAxis; `scale="sqrt"` for wide value ranges, inputs run in position space so the native thumb tracks the fill, arrow keys step in value space) · `media-type-split` · `preset-field` (rewritten: real placeholder, uses Select) · `button` (gained `destructive` / `destructive-solid`).

Hook: `hooks/use-unsaved-changes.ts`. Tokens: `--list-row-height`, `--list-header-height`, `--list-thead-height`, `--toolbar-height`, `--drawer-width`, `--drawer-header-height`, `--drawer-footer-height` in `index.css`.

## Converted (19)

Media plans · Libraries · Connections (indexers, download clients, library routing — `connections-screen.tsx` replaced the 2,180-line `indexers-screen.tsx`) · Import lists · Tags · Final destinations · Quality profiles · Notifications · Size rules · File handling · Processing workflow · Automation & recovery · **Release preferences · Metadata · General · Interface · Migration · Setup guide**.

Every `/settings/*` route now carries a `PageToolbar` and `chrome: "none"`.

Things fixed along the way that were not cosmetic:

- **Indexers, download clients, quality profiles and webhooks were create-only.** Changing a URL or API key meant delete and re-create. All are editable now, patch-style, with a blank secret meaning "keep the current one".
- **`PageTransition` kept `filter: blur(0px)` at rest**, which made the route wrapper the containing block for every fixed/sticky descendant — no page-level bar could pin anywhere in the app. Now opacity + translate only.
- **Quality profiles wrote TRaSH tier ids** (`webdl-1080p`) into `allowedQualities`, which match no tier name in `/api/quality-model`. The page now writes real tier names and flags stored values that resolve to nothing. Repair of existing rows is #128.
- **Quality model had 12 tiers against Radarr's ~30.** Expanded to 26, additive: every existing tier keeps its name and rank so saved profiles still resolve, and installs that already saved a model get the new tiers merged in at defaults.
- **A size maximum of 0 was rejected** (validation required `max > min`, the save 400'd). 0 now means unlimited, the Radarr convention; the decision engine skips the too-large penalty and the row reads "0 GB–Unlimited".
- **Release rules were create-and-delete-only.** `PUT /api/custom-formats/{id}` had existed all along; the page never called it, so changing a score or a condition meant deleting the rule and rebuilding it. Same defect class as indexers, clients, profiles and webhooks.
- Duplicate accessible names in the two size tables; `window.confirm` on webhook delete; duplicate `8080` React keys in the client port presets.

## Quality pass over the 13 converted pages — done

All five nits are closed, and the pass turned up one real bug that was not cosmetic.

**A drawer's Save was submitting the page form underneath it.** The `Drawer` is portaled to `<body>`, but React bubbles synthetic events up the *component* tree, not the DOM tree. On the three pages that wrap their own `<form>` around the list, the drawer's submit therefore reached that form too: one click on "Save schedule" fired both `PUT /api/libraries/{id}/automation` and `PUT /api/settings`, silently writing failed-download settings the user never touched. Fixed in the primitive, so it cannot come back on a future page.

1. **Automation & recovery** — walked live end to end. It was the only converted page missing `chrome: "none"`, so the shell drew an H1 over a page that carries its own toolbar. Its failed-download form also never showed "Saved just now": the baseline was derived from loader `settings`, so between the save landing and the 10s revalidation returning it still compared dirty, and the effect that clears a stale status wiped the confirmation. The baseline is state now. "Recent cycles" counted 50 runs while rendering 12; it says "Latest 12 of 50 runs".
2. **"TV shows" wrapping** — fixed in `SegmentedControl`: `flex-1` gives each segment a 0 basis, so any two-word label wrapped. Segments are `whitespace-nowrap`, and the label is "TV" to match the vocabulary `MediaTypeFilter` already uses.
3. **Size rules length** — resolution bands collapse, with only 1080p and 2160p open by default and "Expand all" in the card header. A collapsed band still states its own span.
4. **The summary strip** — promoted to `summary-strip.tsx` (`SummaryStrip`, 2–5 read-only cells). Nothing in it is a row and nothing opens a drawer, which is why it is not a `ListCard`. #111 puts Transfers, Dashboard and System on the same shape.
5. **Cramped low-quality bands** — the ruler is square-root scaled, and drawn. A per-band ruler was tried first and rejected: it fixed the slivers but silently changed scale at a band boundary, so "Remux 1080p · 12–60 GB" filled the track and "HDTV 2160p · 4–40 GB" one row below filled a third of it. One sqrt ruler for all 26 rows keeps the ladder comparable end to end and still gives the bottom of the range room: the first low-quality bar went from 2.9% to 9%. `RangeAxis` draws the ticks (0 · 5 · 20 · 50 · 100 · 150 GB) under the column header, because an undrawn non-linear ruler is worse than a cramped linear one.
6. **0 min and 0 max now reads as off** — empty track, both thumbs parked left, the row subtitled "any size". It used to fill the track end to end, since a max of 0 means unlimited; 0 *and* 0 is no rule at all, which is not the same thing. A tier switched off this way no longer drags its band's summary to "Unlimited".
7. **The table is one grid** — the column header, the axis, the band rows and the tier rows all share `SIZE_GRID`, so a band's summary sits over the Min and Max columns it summarises instead of floating at the card edge. Per-band tier counts are gone.

### Grammar audit

An automated pass over all 14 converted routes measured every page against the grammar table. Uniform everywhere: toolbars 40px, card headers 48px, list rows 56px, column headers 36px, drawers 600 / 72 / 64. No console errors, no 4xx/5xx, no horizontal overflow, Escape closes a clean drawer. The only loose prose outside a card anywhere was the Size rules intro paragraph, since cut down to the card header line.

Import lists, Notifications and Automation carry a `PageToolbar` with actions but no tabs, because their nav area has a single item. That is correct today and resolves itself when the sidebar sub-items collapse into toolbar tabs.

**Caveat:** the dev database is empty of indexers, download clients, import lists and destination rules, so those four drawers were not re-exercised in this pass — they were verified when they were built. Libraries, tags and a webhook were seeded to walk Libraries, Media plans, Library routing, Processing workflow, Tags, Notifications and Automation, then removed again.

## Second batch — the rest of the settings area

Six more pages, one per commit, each verified live before committing.

- **Release preferences** (837 → ~700 lines) — had seven container shapes: preset cards with their own progress bars, a bespoke four-tab pill bar, a `<details>` block, two flavours of media-type pill. Now one toolbar, three cards, three drawers. The TRaSH catalogue became "Start from" inside the rule drawer instead of a browsable tab that could only add at a default score, and `cf-creator.tsx` + `cf-library-browser.tsx` (660 lines of one-off UI) are deleted.
- **Metadata** — per-field boxes and raw checkboxes gone; the title-matching check and the refresh jobs now report **in the row that asked**, not through a toast that vanishes.
- **General** and **Interface** — both were "form left, read-only stat cards right" splits; the stat cards are what `SummaryStrip` is for. Interface's 2×2 grid of density cards plus a whole card explaining what each density does collapsed into one segmented control whose help line describes the selected option. General now says out loud that a bind-address or port change needs a restart.
- **Migration** — the change report was a stack of full-width cards with raw checkboxes and a 4-column key/value grid each. Now one row per operation with an action chip and an Apply switch; the row opens a drawer with the reason, every field that would be written, and the warnings. Safety notes moved behind one `Disclosure`.
- **Setup guide** — the wizard shape is deliberately **left alone**: a linear first-run flow with a step rail and a Back/Continue footer is the right surface. What was replaced were its three private "label over value" block types (all now `SummaryStrip`) and the readiness checklist (now a `ListCard` with standard rows and Chips).

`systemSettingsNavItems` (General · Interface · Notifications · Migration) gives those four a shared tab bar. The "System & settings" sidebar area covers eleven destinations and `/system/*` already has its own bar, so splitting the two keeps either one scannable. Notifications had no tabs at all before.

## Remaining queue

The operational pages: **Transfers, Dashboard, System** (#111 — condense each to one `SummaryStrip` plus the live list; Transfers is ~2,600px tall with nothing in flight). `SummaryStrip` exists for exactly this now.

### Navigation — done for five areas

The sidebar used to list an area's children *and* the page toolbar listed the same children as tabs. Areas now carry `tabsInToolbar`: when every page in an area has a `PageToolbar` with those items as its tabs, the sidebar shows one row and the toolbar is the only way between siblings.

Collapsed today: Library setup, Connections, Media Plans, Discover media, Automation & recovery, Preferences. **System is the only row still holding an expander** — `/system/*` has no toolbar yet, so collapsing it would leave no way in. It loses the expander the moment #111 lands; add `systemHealthNavItems` as the toolbar tabs on those pages and flip `tabsInToolbar` to `true`.

"System & settings" is split: **Preferences** (General · Interface · Notifications · Migration) and **System** (`/system/*` plus Guided setup). Eleven destinations under one heading was a scrolling tab bar.

## Working rules

- Run the app: `scripts/start-local-app.ps1` (API 5099, Vite 5173, login `admin` / `admin1234`). Vite dies when the launcher shell exits — if 5173 drops, run `node node_modules/vite/bin/vite.js --host 127.0.0.1` from `apps/web` as a background job.
- Backend changes need `dotnet build src/Deluno.Host/Deluno.Host.csproj` and a restart; the API must be stopped first or the DLL copy fails on a file lock.
- Verify every page live before committing: write a Playwright script to `apps/web/scripts/.tmp-*.mjs` (it must live there to resolve `@playwright/test`; use the Write tool, Bash heredocs mangle backslashes), drive the real create → edit → toggle → remove round trip, assert against the API, check the console is clean, then delete the script.
- **Do not run `npx playwright test` while James is using the app** — it builds a Release backend and takes the dev one down. Run the affected specs only, or the full suite when he is not in it. Smoke specs assert the old copy on pages not yet converted; update them with each conversion.
- `npx tsc -b` and `npm run validate:ui-spacing` before every commit.
- Windows Smart App Control is in enforcement mode on this machine and the Debug `Deluno.Host.exe` is unsigned, so Defender may warn after a rebuild. That is expected locally; the shipping implications are #129.
