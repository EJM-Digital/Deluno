# Deluno Figma Polish Brief

Target Figma file: `Q3pojIhNpXSIikDfbsnv21`

Current blocker: Figma MCP returned a Starter-plan tool-call limit error before file inspection or canvas writes could run. No Figma nodes were created or modified.

## Purpose

Create a high-signal design QA and polish board for Deluno before deeper UI refinement.

The board should help decide whether the current product shell, density system, typography scale, navigation hierarchy, and settings information architecture are ready for final polish.

## Current Product Direction

Deluno is a premium media operations app replacing Radarr, Sonarr, and Prowlarr behavior with a unified user-first interface.

The current UI direction is:

- Premium desktop app feel.
- Dense but readable operations surface.
- Warm dark/light theme system.
- Token-driven density modes.
- Stronger navigation hierarchy than Radarr/Sonarr.
- User-first settings grouped by decisions, not technical backend modules.

## Figma Board Structure

Create one page named:

`Deluno / UI Polish QA`

Recommended top-level sections:

1. `Current App Capture`
2. `Navigation Hero`
3. `Density System`
4. `Settings IA`
5. `Library / Movie Grid`
6. `Download Telemetry`
7. `Design Risks`
8. `Next Polish Pass`

## Frames To Create

### 1. Desktop Dashboard

Frame size: `2560 x 1440`

Use this to judge:

- Top navigation scale.
- Hero/nav relationship.
- Content width and whitespace.
- KPI card rhythm.
- Download telemetry hierarchy.

### 2. Desktop Library

Frame size: `2560 x 1440`

Use this to judge:

- Poster grid density.
- Toolbar discoverability.
- Filter/sort custom filter model.
- Metadata readability under different density modes.

### 3. Settings Interface

Frame size: `2560 x 1440`

Use this to judge:

- Settings submenu scale.
- Form density.
- Whether density cards are too large or too technical.
- Field label/readability consistency.

### 4. Mobile Settings

Frame size: `390 x 844`

Use this to judge:

- Bottom nav.
- Settings tab overflow.
- Touch target sizing.
- Whether settings pages stay readable without feeling cramped.

## Token System To Represent

### Density Tokens

Deluno density must scale through named tokens, not generic Tailwind class overrides.

Core token groups:

- `--type-*`
- `--shell-*`
- `--control-*`
- `--field-*`
- `--card-*`
- `--content-*`

Density modes:

- `compact`
- `comfortable`
- `spacious`
- `expanded`

### Type Roles

Use these semantic text roles in Figma:

- `type-micro`: tiny counters, compact badges, technical metadata.
- `type-caption`: eyebrows, table metadata, helper labels, compact timestamps.
- `type-body-sm`: dense list rows, card secondary text, small controls.
- `type-body`: default readable UI body text.
- `type-body-lg`: explanatory copy, empty states, onboarding text.
- `type-title-sm`: card section titles.
- `type-title-md`: page subheadings and panel heroes.
- `type-title-lg`: page titles.
- `type-title-xl`: dashboard or hero display titles.

## Important Design Decisions

### Navigation Should Be A Hero

The menu is not secondary chrome. Users constantly navigate between Overview, Movies, TV, Queue, Indexers, Activity, System, and Settings.

The nav should feel:

- Confident.
- Clear.
- Larger in spacious/expanded density.
- Visually connected to the app theme.
- Not like a tiny utility strip.

### Density Must Be Systemic

Density should affect:

- Menus.
- Topbar.
- Mobile nav.
- Settings tabs.
- Form fields.
- Cards.
- Tables.
- Library poster metadata.
- Dashboard tiles.
- Queue rows.
- Indexer controls.

Density should not mutate random primitives such as every `.h-6` or `.w-6`, because that distorts icons and layout mechanics.

### Settings Should Be User-First

Settings should be grouped by what the user is trying to accomplish:

- Get media into Deluno.
- Decide where media should live.
- Choose quality and upgrade policy.
- Configure metadata output.
- Connect indexers and download clients.
- Manage system behavior.

Avoid making users understand Radarr/Sonarr implementation categories before they can configure the app.

## Current Visual QA Notes

Confirmed:

- Signed-in browser routes no longer throw app errors.
- `/settings/ui` renders after login.
- Broad density overrides for generic height/width utilities have been removed.
- Production web build passes.

Still needs desktop visual QA:

- 1440p and ultrawide screenshots for all density modes.
- Dark and light screenshots for Dashboard, Library, Settings UI, Indexers, Queue.
- Confirm settings nav does not clip or overflow awkwardly at expanded density.
- Confirm Library poster grid still feels premium after global sizing cleanup.

## Open Product Work To Remember

Download clients:

- Prioritize external clients first.
- qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, rTorrent, uTorrent should normalize into one telemetry model.
- Deluno should be orchestration/control plane, not a near-term embedded downloader.

Metadata:

- Primary metadata provider should be TMDb.
- Keep IMDb IDs as cross-links.
- Add Fanart.tv later for richer artwork.
- NFO/artwork export is an output layer after provider ingestion works.

Filesystem:

- Browse buttons should support local paths, UNC paths, Docker/container paths, and manual path entry.
- Users should not need to type fragile folder format tokens where presets/dropdowns can prevent mistakes.

Filters:

- Match Radarr/Sonarr filters.
- Extend beyond them with bitrate, tags, release group, source, codec, language, destination rule, download client, indexer, import state, and custom user filters.

## Figma Push Plan Once Unblocked

1. Inspect existing Figma pages, variables, and text styles.
2. Create or update `Deluno / UI Polish QA`.
3. Add dark/light token swatches.
4. Add density comparison cards.
5. Add desktop and mobile layout frames.
6. Add annotated problem callouts.
7. Add next-polish checklist.

