# Deluno UX rebuild — handover

Branch `ux/list-drawer-media-plans`, 13 commits, not pushed. Nothing here is merged; all of it is reviewable in one place.

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

`field` (Field/FieldRow, owns the control id so `<label htmlFor>` is always wired) · `select` · `switch` (Switch/SwitchRow) · `segmented-control` (real radiogroup, arrow keys) · `textarea` · `chip` · `disclosure` · `page-toolbar` · `list-card` (ListCard/ListTable/ListRow/ListCell/ListNameCell/ListEmpty/LIST_TRACK) · `drawer` (Drawer/DrawerSection/DrawerDanger/DrawerFooter, `saveEnabled` for tool-style drawers) · `page-footer` · `range-slider` (dual thumb, shared scale) · `media-type-split` · `preset-field` (rewritten: real placeholder, uses Select) · `button` (gained `destructive` / `destructive-solid`).

Hook: `hooks/use-unsaved-changes.ts`. Tokens: `--list-row-height`, `--list-header-height`, `--list-thead-height`, `--toolbar-height`, `--drawer-width`, `--drawer-header-height`, `--drawer-footer-height` in `index.css`.

## Converted (13)

Media plans · Libraries · Connections (indexers, download clients, library routing — `connections-screen.tsx` replaced the 2,180-line `indexers-screen.tsx`) · Import lists · Tags · Final destinations · Quality profiles · Notifications · Size rules · File handling · Processing workflow · Automation & recovery.

Things fixed along the way that were not cosmetic:

- **Indexers, download clients, quality profiles and webhooks were create-only.** Changing a URL or API key meant delete and re-create. All are editable now, patch-style, with a blank secret meaning "keep the current one".
- **`PageTransition` kept `filter: blur(0px)` at rest**, which made the route wrapper the containing block for every fixed/sticky descendant — no page-level bar could pin anywhere in the app. Now opacity + translate only.
- **Quality profiles wrote TRaSH tier ids** (`webdl-1080p`) into `allowedQualities`, which match no tier name in `/api/quality-model`. The page now writes real tier names and flags stored values that resolve to nothing. Repair of existing rows is #128.
- **Quality model had 12 tiers against Radarr's ~30.** Expanded to 26, additive: every existing tier keeps its name and rank so saved profiles still resolve, and installs that already saved a model get the new tiers merged in at defaults.
- **A size maximum of 0 was rejected** (validation required `max > min`, the save 400'd). 0 now means unlimited, the Radarr convention; the decision engine skips the too-large penalty and the row reads "0 GB–Unlimited".
- Duplicate accessible names in the two size tables; `window.confirm` on webhook delete; duplicate `8080` React keys in the client port presets.

## Known nits to review before the visual pass

James said quality "started to regress a little" on the later pages. Specifically worth a look:

1. **Automation & recovery** was written fast and only lightly exercised live — the drawer, the strip and the failed-download form need a proper walkthrough.
2. **"TV shows" wraps to two lines** in the Size rules toolbar segment at 1440px. Either shorten to "TV" or widen the control.
3. Size rules is still a long page even split: 26 rows for one media type. The resolution bands help; collapsing bands by default might help more.
4. The summary strip on Automation is a second block type that only that page uses — either promote it to a primitive or drop it.
5. Low-quality tiers (Unknown/CAM/…) render as near-zero bands at the left of a 0–150 scale. Correct, but visually cramped.

## Remaining queue

Release preferences (`settings-custom-formats-page`, 837 lines) · Metadata · General · Interface · Migration · Setup guide · then the operational pages: Transfers, Dashboard, System (#111 — condense to one summary strip + the live list; Transfers is ~2,600px tall with nothing in flight).

**Then, and only then:** collapse the sidebar sub-items in favour of the toolbar tabs. Doing it earlier strands any page that does not yet carry a `PageToolbar` — there would be no way to reach it. Every page in an area must be converted first.

## Working rules

- Run the app: `scripts/start-local-app.ps1` (API 5099, Vite 5173, login `admin` / `admin1234`). Vite dies when the launcher shell exits — if 5173 drops, run `node node_modules/vite/bin/vite.js --host 127.0.0.1` from `apps/web` as a background job.
- Backend changes need `dotnet build src/Deluno.Host/Deluno.Host.csproj` and a restart; the API must be stopped first or the DLL copy fails on a file lock.
- Verify every page live before committing: write a Playwright script to `apps/web/scripts/.tmp-*.mjs` (it must live there to resolve `@playwright/test`; use the Write tool, Bash heredocs mangle backslashes), drive the real create → edit → toggle → remove round trip, assert against the API, check the console is clean, then delete the script.
- **Do not run `npx playwright test` while James is using the app** — it builds a Release backend and takes the dev one down. Run the affected specs only, or the full suite when he is not in it. Smoke specs assert the old copy on pages not yet converted; update them with each conversion.
- `npx tsc -b` and `npm run validate:ui-spacing` before every commit.
- Windows Smart App Control is in enforcement mode on this machine and the Debug `Deluno.Host.exe` is unsigned, so Defender may warn after a rebuild. That is expected locally; the shipping implications are #129.
