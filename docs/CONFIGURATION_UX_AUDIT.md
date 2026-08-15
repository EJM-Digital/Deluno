# Configuration UX audit

Updated: 2026-08-14

## Product rule

Deluno has two distinct places:

1. **Dashboard and library views** are where people browse, add, monitor, fix,
   and enjoy media.
2. **Library setup** is where an installation is configured. It must explain
   outcomes in media language, keep advanced control available, and never make
   a person understand an implementation detail just to add a movie.

Every configuration screen must answer three questions before exposing a
control: what decision is this for, what will change, and where does the user
go next? A setting belongs to one owner only; other pages link to that owner
instead of duplicating it.

## Navigation and configuration map

Persistent navigation is deliberately compact: it identifies a destination,
not a workflow manual. Group labels orient people, but explanatory paragraphs
belong in the route header and the destination page after it opens. This keeps
the application scannable on a workstation and on a phone without hiding why a
screen exists.

| User outcome | Primary home | Details that stay available, but are not primary navigation |
| --- | --- | --- |
| Where media lives and what is saved beside it | **Library** | Destination rules, Library details, tags, processor settings |
| Which titles should be added | **Discover media / Import lists** | Per-list filters, preview, public MDbList URLs |
| What version of a title is wanted | **Media preferences** | Quality goals, size rules, custom formats, destination rules |
| Where Deluno searches and downloads | **Find and download** | Source/client tests and migration |
| What Deluno is doing now | **Automation and recovery** | Queue and Activity drill-downs |
| Installation health and preferences | **System** | General host settings, notifications, UI preferences, API, backups, updates |

## Required information-architecture correction

The current setup surface is an improvement over the original scattered
settings screens, but it is not the final target. It still exposes three
overlapping ways to navigate (the application sidebar, a Library setup strip,
and overview cards), while splitting single user decisions across technical
pages. Reordering those controls alone cannot make the product feel simple.

The target is a **task-based Library setup home**, with five primary decisions:

1. **Library** — folders, destinations, naming, import behaviour, metadata
   output, and optional processed-output handling.
2. **Find and download** — search sources, download clients, routing, and
   connection tests.
3. **Media preferences** — a simple Media Plan first; quality profiles, size
   ranges, release groups, custom formats, and title/library overrides appear
   as named Advanced sections of the same decision.
4. **Automation and recovery** — search schedules, upgrades, retry, download
   health, cleanup, and replacement behaviour. The live operational view stays
   in the main application navigation; it is not another setup step.
5. **Discover media** *(optional)* — import lists, previews, exclusions, and
   their automatic-add choice.

**System** remains separate maintenance: health, updates, backups, access,
notifications, API keys, diagnostics, and display preferences.

The Library setup home should show readiness and the next useful action, not a
permanent numbered wizard. A short guided checklist is appropriate only for a
new or incomplete installation and must disappear once its essentials are
ready. Existing deep URLs remain available for bookmarks and advanced users,
but they are reached from the owning primary decision rather than presented as
peer settings tabs.

Library setup is one two-level settings navigator: the top row exposes
Library, Connections, Media preferences, Discover media, Automation, and
Deluno settings; the second row immediately exposes the chosen section's
configuration routes. Deluno-wide controls live under **Deluno settings**
(General, Notifications, Interface, Migration, Guided setup, and System
health), never in an ambiguous "Other configuration" bucket. Progressive
disclosure can simplify a page; it must not make a real
configuration option discoverable only from a contextual link, wizard branch,
or direct URL.

## Decisions implemented in this pass

- `/settings/metadata` is now **Library details**, visibly reachable from the
  **Library** decision. It only manages user-facing output choices:
  language, ratings region, NFO files, and artwork files.
- The repeated four-step setup banner appears only on the Library setup
  overview. It no longer pushes people through a generic wizard on every
  configuration page.
- TMDb title matching is a Deluno-managed service. Provider route, broker URL,
  and credentials are not shown in Library details, guided setup, or Add Media.
  OMDb enrichment is deferred from the launch service.
- Add Movie/Show and guided setup retain a manual-title path when title matching
  is unavailable and say who owns the fix.
- MDbList stays with Import lists as a normal public custom-list URL. Deluno
  detects and resolves it internally; ordinary setup neither names MDbList as
  a provider nor asks the library owner for a token.
- Desktop and mobile navigation use labels and group headings only. Every
  primary route has a visible one-line route context after navigation,
  including at phone widths.

## Follow-up simplification order

These are the remaining high-value simplifications. They need individual
implementation and regression coverage; they must not silently discard
existing configuration.

1. **Media plans:** make the guided plan the canonical editor, then put quality
   profiles, file-size rules, and custom formats behind named advanced sections.
   This remains dependent on the product decision in issue #88.
2. **Connections:** split the combined indexer/download-client surface into
   clear "find releases" and "download releases" sections, with tests and
   capability hints next to each connection.
3. **Library & storage:** make folders, destination rules, naming, processor
   hand-off, and Library details a single progressive flow. Keep existing URLs
   as bookmarked deep links.
4. **System:** move host/runtime-only controls out of General, group
   notification, backup, update, and API maintenance behind clear purpose
   cards, and make diagnostics explicitly advanced.
5. **List review:** add selective approval, exclusion/provenance, and
   provider-specific validation to import lists before treating automatic list
   sync as production complete.

## Guardrails for future screens

- Do not expose a secret, backend route, protocol, or provider fallback unless
  the person on that screen is expected to operate it.
- Start with a safe preset and a one-sentence result; use an Advanced section
  for granular controls.
- Put destructive or library-wide actions in an explicit maintenance area with
  a clear scope.
- Keep a manual path whenever an optional external service is unavailable.
- Preserve direct URLs and existing values while routes are consolidated.
- Keep persistent navigation to labels, group headings, status, and attention
  counts. Put explanatory copy beside the decision or live work after the
  destination opens.
