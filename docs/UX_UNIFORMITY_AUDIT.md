# Deluno UI Uniformity Audit

Updated: 2026-08-15

## Non-negotiable interaction standard

Deluno has two application modes, each with one consistent navigation model:

| Mode | Purpose | Navigation contract |
| --- | --- | --- |
| **Manage media** | Browse, add, inspect, and correct movies and shows; see work in progress. | Persistent application navigation selects the domain. Each screen keeps its filter, view, and action controls inside the page body. |
| **Configure your library** | Change how Deluno finds, downloads, processes, and stores media. | The persistent Library setup tree is the single configuration navigator. A label opens its family; its chevron independently shows or hides children. |
| **Maintain Deluno** | Change installation-wide behaviour and keep the app healthy. | One persistent System & settings tree contains General, Notifications, Interface, system health, backups, updates, API access, migration, and guided setup. |

No route may create an alternative settings nav, hide a sibling setting behind a
contextual link, or make a person return to the overview just to move to a
related configuration screen.

## Configuration navigator

The shared navigator is mandatory on these route families:

- `/settings/*`
- `/indexers`

Its top level is constant:

`Library · Connections · Media plans & quality · Discover media · Automation & recovery`

Its second level changes only with the selected family:

| Family | Submenu |
| --- | --- |
| Library | Parent opens Files, processing & import; children: Processing & import; Final destinations; Metadata & sidecars; Tags |
| Connections | Indexers; Download clients; File locations; Library routing |
| Media plans & quality | Media Plan, Quality profiles, Quality & size limits, Release preferences |
| Discover media | Import lists |
| Automation & recovery | Search, retries & failed downloads |
| Maintain Deluno | System health, System activity, Backups, Updates, API access, Help & guides, General, Notifications, Interface, Migration, Guided setup |

This is implemented in the existing application sidebar on desktop and the
mobile More drawer. The parent label navigates and its chevron only expands or
collapses the subtree; navigating never removes the tree. Configuration and
maintenance pages retain their own relevant tree; live Automation remains in
What Deluno is doing. No page owns its own settings menu.

## Audit results and follow-up work

| Surface | Current standard | Follow-up |
| --- | --- | --- |
| Library setup, Connections, Automation, System | Shared configuration navigation now persists across family changes. | Keep every future configuration route in this shell. |
| Dashboard, Movies, TV, Schedule | Media-management pages. Persistent app navigation selects the domain. | Audit duplicated in-page headings and consolidate page-level actions in a dedicated pass. |
| Transfers and Activity | Operational pages. Persistent app navigation selects the domain; the page explains live state. | Standardise filters, empty states, and action placement. |
| Detail pages | Media-management drill-downs. | Standardise back navigation, title action bars, and status summaries. |

## Page-level rules

1. The application top bar names the current domain. A page body only adds a
   title when it communicates a distinct task or state; it must not restate the
   same navigation label.
2. Use one primary action per page header. Secondary actions belong beside the
   affected data, never in a global catch-all panel.
3. Use concise status rows for setup readiness; do not turn navigation into a
   grid of promotional cards.
4. Keep destructive actions with their item or in explicit maintenance flows.
5. Every empty state must say what is absent, why that matters, and offer the
   next action where one exists.
6. Desktop and mobile must expose the same destinations and use the same
   labels; responsive layout may change placement, not information architecture.

## Spatial rhythm (site-wide)

The interface uses one compact scale rather than page-specific whitespace.
All full-page views, configuration screens, dashboard panels, media libraries,
and detail pages compose the same tokens:

| Token | Standard density | Use |
| --- | ---: | --- |
| `--content-pad-block` | 12–20px | Space between the app header and page content; page bottom. |
| `--page-gap` | 14px | Between independent page sections, such as a library summary and its tools. |
| `--grid-gap` | 12px | Between peer panels, fields, and cards in a grid. |
| `--control-gap` | 8px | Tight alignment within an individual control, label group, or icon row. |
| `--tile-pad` | 18px | Internal padding for a normal content panel. |

Density preferences scale these tokens proportionally; they do not create a
separate layout language. Empty states, dialogs, and artwork-led media heroes
may use deliberate larger space, but ordinary content must not use arbitrary
macro stack/grid utilities in place of the tokens. `npm run validate:ui-spacing`
enforces this for every application source file. Sticky toolbars must use the
real one-row application-header offset and only their compact control
separation, never an extra page-sized spacer.
