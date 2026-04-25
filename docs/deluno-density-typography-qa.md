# Deluno Density And Typography QA

## Current Assessment

The density system is structurally improved, but it should not be considered final yet.

The runtime now normalizes legacy text utilities into a controlled type ladder, so density modes behave more consistently than before. The previous broad density overrides for generic height/width utilities have been removed because they could distort icons, separators, focus outlines, and small layout primitives. Density should now flow through named tokens such as `--shell-*`, `--type-*`, `--control-*`, and `--field-*`.

However, the source code still contains a large number of one-off typography classes. That means the app is protected by CSS normalization, but the component layer is not yet clean enough to call the design system mature.

## Static Audit Snapshot

Latest static scan:

| Area | Count |
| --- | ---: |
| Arbitrary text sizes, e.g. `text-[11px]` | 443 |
| Tailwind `text-xs` | 70 |
| Tailwind `text-sm` | 135 |
| Tailwind `text-base` | 22 |
| Tailwind `text-lg+` | 52 |
| Semantic type classes | 74 |
| Density field classes | 85 |
| Tokenized shell/type sizing | 99 |

## Interpretation

The app previously had three competing systems:

1. A new semantic/density token layer.
2. Older route-level and component-level hardcoded text classes.
3. Broad CSS overrides that changed generic Tailwind geometry utilities.

The third system has been removed. The token layer is doing useful work, but a product-grade UI should move toward explicit semantic roles rather than relying on global text normalization forever.

## Target Typography Roles

Use these roles everywhere possible:

| Role | Intended Use |
| --- | --- |
| `type-micro` | Tiny counters, compact badges, secondary technical metadata |
| `type-caption` | Eyebrows, table metadata, helper labels, compact timestamps |
| `type-body-sm` | Dense list rows, card secondary text, small controls |
| `type-body` | Default readable UI body text |
| `type-body-lg` | Important explanatory copy, empty states, onboarding text |
| `type-title-sm` | Card section titles |
| `type-title-md` | Page subheadings and panel heroes |
| `type-title-lg` | Page titles |
| `type-title-xl` | Major dashboard or marketing-style hero titles |

## Density Expectations

Density should change the interface posture, not randomly enlarge individual elements.

| Density | Expected Feel |
| --- | --- |
| Compact | Dense, operational, smaller controls, still readable |
| Comfortable | Default daily-use balance |
| Spacious | More premium and readable for 1440p desktop |
| Expanded | Screen-filling, strong navigation and larger content for 27-inch/ultrawide |

## Visual QA Matrix

Each route should be checked in dark and light mode across `compact`, `comfortable`, `spacious`, and `expanded`.

Required routes:

| Route | Priority | Check |
| --- | --- | --- |
| `/` | High | Dashboard cards, nav weight, page rhythm |
| `/movies` | High | Poster grid density, toolbar sizing, metadata text |
| `/tv` | High | Shared library component parity with movies |
| `/calendar` | Medium | Calendar card hierarchy and date readability |
| `/queue` | High | Dense telemetry rows, buttons, history panels |
| `/indexers` | High | Integration forms, telemetry cards, warning states |
| `/activity` | Medium | Timeline readability and row rhythm |
| `/system` | Medium | Audit timeline, backup/restore panels |
| `/settings` | High | Settings information architecture and submenu weight |
| `/settings/ui` | High | Density switcher must not visually break itself |

## Known Risks

- Arbitrary text classes are still too common in feature routes.
- Indexers and Activity contain many compact metadata elements that may feel too small unless converted to semantic roles.
- Login/setup screens use independent sizing and should be aligned with the same type system.
- The settings submenu is visually improved, but still needs real screenshot review across all density modes.
- Runtime CSS typography normalization may hide problems that remain in component source.
- Generic Tailwind sizing utilities should not be density-overridden globally; use named component tokens instead.

## Next Implementation Pass

1. Convert high-traffic route text classes to semantic roles:
   - Dashboard
   - Library shared component
   - Indexers
   - Queue
   - Settings shell

2. Convert auth/setup screens:
   - Login
   - First-run setup

3. Convert shared components:
   - Badges
   - Filter bar
   - Command palette
   - Audit timeline
   - Empty/error states
   - Path browser

4. Add a guardrail:
   - Prefer semantic classes for new code.
   - Allow raw text classes only for rare, documented exceptions.

## Figma Handoff Criteria

Do not hand this to Figma for polish until screenshots exist for at least:

- Dashboard in dark/light, comfortable/expanded.
- Movies in dark/light, comfortable/expanded.
- Settings UI in dark/light, all densities.
- Indexers in dark/light, comfortable/expanded.

Figma should receive:

- Current screenshots.
- This typography role map.
- Density expectations.
- Known route risks.

## Current Browser QA

Signed-in browser QA is available. Narrow/mobile rendering of `/settings/ui` is confirmed after reload, and the settings page no longer shows the earlier density-field connector artifact in that viewport. Desktop 1440p/ultrawide screenshot capture is still needed before a Figma polish pass.
