# Library setup information-architecture audit

This is the implementation baseline for [#97](https://github.com/jampat000/Deluno/issues/97). It records the current configuration surface and the intended owner for each user outcome. It is deliberately separate from the Media Plan model decision in #88.

## Target navigation

1. **Plans** — desired quality, file size, release preferences, upgrades, and destination intent.
2. **Connections** — search sources, download clients, capability tests, and per-library routing.
3. **Automation** — current/next work, pause controls, decisions, recovery, and cleanup attention.
4. **Library & storage** — folders, naming, imports, metadata output, and processors.
5. **System** — account, backups, updates, notifications, API, and diagnostics.

The Dashboard remains the daily home; Library setup is optional advanced control.

## Current-route decisions

| Current route | Decision | Intended owner | Reason |
| --- | --- | --- | --- |
| `/settings` | Keep, redesign as outcome-led landing page | Library setup overview | Must show readiness, attention, and the next useful change rather than another settings index. |
| `/settings/policy-sets` | Keep temporarily; merge after #88 | Plans | Existing “Media plans” is a guided surface, not yet the canonical policy model. |
| `/settings/profiles` | Fold into Plans | Plans | Quality goals are a plan input, not an independent primary destination. |
| `/settings/quality` | Fold into Plans | Plans | File-size boundaries are quality-plan detail. |
| `/settings/custom-formats` | Fold into Plans, advanced detail | Plans | Release preferences must be explained as part of the desired experience. |
| `/indexers` | Keep as Connections home; split its mixed source/client UI into contextual sections | Connections | Search sources, clients, tests, and routing belong together; avoid duplicate connection setup under Settings. |
| `/settings/lists` | Move under Connections | Connections | Intake sources are title-discovery connections, not generic automation settings. |
| `/settings/migration` | Keep as advanced Connections/System tool | Connections | It imports connections and configuration; it is not daily automation. |
| `/search-cycles` | Keep; make the Automation landing route | Automation | It already exposes state, scheduling, and pause/resume controls. |
| `/queue` | Keep as Downloads & attention operational view | Automation | Downloads, import jobs, recovery, and manual import should be one operational journey. |
| `/activity` | Keep as a contextual decision/history view | Automation | Link from titles, queues, and automation; do not make users interpret a separate admin log. |
| `/settings/media-management` | Keep | Library & storage | Owns naming, import mode, hardlinks, and processors. |
| `/settings/destination-rules` | Fold into Library & storage | Library & storage | Destination rules are storage routing, not a separate mental model. |
| `/settings/metadata` | Fold into Library & storage, advanced | Library & storage | Output metadata is a library behavior. |
| `/settings/tags` | Make contextual to plans and library organization | Plans / Library & storage | Tags should not be primary configuration; link from the outcomes they influence. |
| `/settings/general` | Split host/runtime to System; remove notification overlap | System | General currently overlaps notifications and automation startup controls. |
| `/settings/notifications` | Keep under System | System | It has a single clear owner. |
| `/settings/ui` | Keep under System, advanced | System | Preference-only controls. |
| `/system/*` | Keep, group under System | System | Health, audit, API, backups, updates, and guide are operational/system controls. |

## Immediate implementation order

1. Change the Studio landing page to outcome-led cards: Plans, Connections, Automation, Library & storage, System.
2. Make `/indexers` the explicit Connections destination and remove duplicate route language elsewhere.
3. Promote `/search-cycles` as Automation’s operational home and link Queue/Activity as contextual drill-downs.
4. Consolidate Library & storage navigation without changing existing routes.
5. After #88, merge quality profiles, file sizes, custom formats, and policy sets into one canonical Plans experience.

### Implemented navigation boundary

The persistent Studio sub-navigation now contains only the five outcome owners:
**Plans, Connections, Automation, Library & storage, and System**. Secondary
editors (quality profiles, custom formats, destination rules, metadata, title
sources, migration, queue, and activity) remain at their existing URLs and are
reached contextually from the owning outcome. This preserves bookmarks and
configuration without making users choose among overlapping technical pages.

The legacy `/settings/connect` route redirects to Connections (`/indexers`),
not General settings.

## Non-negotiable checks

- No existing configuration is silently moved or changed.
- Legacy paths remain reachable or redirect safely.
- Every primary setting has one owner; secondary pages may link to it but not duplicate editing.
- Keyboard and small-screen navigation must be included in the browser flow tests.
