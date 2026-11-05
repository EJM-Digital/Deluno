# UI shell standard

The one reference Deluno and Weir both follow, so the top of the page does not shift when the owner switches apps
(#1461). Both apps use Inter Variable. Sizes are at the default font scale; both apps scale them together with the
owner's font-scale setting, so the ratios below hold at any scale.

| Element | Font size | Weight | Case | Letter spacing | Line height | Top of the box |
|---|---|---|---|---|---|---|
| Page eyebrow (the line above the title) | 11px | 700 | uppercase | 0.14em | 14px | 14px |
| Page title | 26px | 600 | as written | -0.03em | the app's own | 32px |
| Top-card label (Speed, Working on now, Next) | 11px | 600 | uppercase | 0.12em | 16px | follows from the header |
| Panel title (Pipeline, Sources, Your library, Activity ...) | 15px | 600 | as written | the app's own | 23px | 12px below the panel's top edge |

The header row's height and the gap to the first cards are the same in both apps. A panel's header is 48px high with
its title centred in it, which puts the title's box 12px below the panel's top edge (measured on the golden VM on 5 Nov
2026: Deluno 12px, Weir rc.24 11px, the only difference left). Both apps' Dashboard panel headers are 48px (11px top padding),
which makes the Pipeline's chrome 110px and its least height 328px (Weir's BOARD_MIN). No size is a fraction of a step:
11px is 11px, never 10.3125px.

## In Deluno

- The page header is `ContentTopbar` in `apps/web/src/layouts/app-layout.tsx` (eyebrow line and `TopbarTitle`).
- The top-card label is `BandCard` (`apps/web/src/routes/dashboard/live/band-card.tsx`; its line height is in `band.css`).
- Every Dashboard panel header (`LiveCard`, `.sy-card-h` on System) is `min-height: 48px`; the Live grid's rows are `gridRows` in `apps/web/src/routes/dashboard/live/layout.ts` ("Fit the window": the band gives way to 144, then the journey to 328, then the bottom row to 110; the grid is never shorter than 606px).
- The panel title is the `h2` of `ListCard` (`apps/web/src/components/ui/list-card.tsx`), which `Panel` uses.
- Sizes come from the `--type-*` tokens and the `.role-*` classes in `apps/web/src/index.css` (#1447).

## Proof

`apps/web/tests/smoke/ui-shell-standard.spec.ts` reads each element's computed font size, weight, letter spacing, line
height and top on the Dashboard and on a Settings page at 2560x1300 and 1366x768, and fails on any difference.
