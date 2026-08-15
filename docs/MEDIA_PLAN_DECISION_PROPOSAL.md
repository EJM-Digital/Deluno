# Media Plan decision proposal

Status: approved product decision for GitHub issue #88. The delivery work remains explicitly separate; nothing here silently changes existing libraries or policies.

## The user-facing model

A **Media Plan** is the one place a person describes the media experience they want: for example, *Family movies in 1080p*, *Premium 4K home theatre*, *Compact TV*, or *Anime with original audio*.

The plan summary must always be readable as a sentence:

> For this library, prefer WEB 1080p to Blu-ray 1080p, keep typical movie files between 4 and 12 GB, prefer selected release groups, allow upgrades until the quality goal is met, and use the Movies destination.

Technical terms such as custom formats, score, and indexer category are supporting detail—not the normal entry point.

## One owner for each concern

| Concern | Canonical owner | Can be adjusted by |
| --- | --- | --- |
| Non-negotiable safety limits, blocked patterns, and proof requirements | Deluno global safeguards | Nobody through a plan |
| Curated defaults and provenance | Bundled, versioned Deluno preference pack | Media Plan adoption/local override |
| Quality ladder, size envelope, language/release preferences, upgrade rule, search budget | Media Plan | Library and title overrides |
| Destination, naming, import and storage mechanics | Library & storage | Library only |
| Whether a title is monitored, paused, deferred, or manually overridden | Title | Title only |
| Source/client availability and routing | Connections | Library routing context |

This keeps storage mechanics out of quality selection, while letting a plan state its intended destination experience in plain language. The library owns the actual filesystem route.

## Resolution order

When Deluno evaluates a release, it produces a decision trace in this order:

1. Apply global safeguards. Unsafe patterns, ownership boundaries, and explicit user blocks always win.
2. Resolve the plan's selected bundled preference-pack version, if any.
3. Apply the Media Plan's deliberate local settings.
4. Apply a library override only where the plan permits it.
5. Apply a title override, defer, or user-approved forced choice.
6. Evaluate availability, file size, quality, language, release preferences, source health, and budget together.
7. Explain accepted, held, or rejected status with the winning rule and its owner.

An override changes only the field it names. It never implicitly replaces unrelated plan choices. A hard safety rejection is never bypassed by an automatic override; a person may force a release only through the existing explicit, audited manual action.

## Quality, size, and release preferences

Quality goals and file sizes are one decision, not separate pages:

- A plan has an ordered quality ladder and an upgrade cutoff.
- Every plan exposes a simple, selectable **size tier** for each quality goal. The tier is described in ordinary language and its resulting ranges are visible before it is saved.
- Advanced editing exposes a per-quality minimum/typical/maximum size envelope, expressed in friendly units and optionally normalized by runtime for episodes.
- Deluno scores ordinary variance inside the chosen envelope and rejects obvious hard-boundary outliers. The detailed editor can make a boundary stricter, or choose hold/review behaviour where that better suits the library.
- Release groups are plan preferences: **preferred**, **acceptable**, **avoid**, or **blocked**. Preferred/acceptable/avoid affect scoring; blocked is an explicit reject.
- Deluno can offer a curated starter list of well-known groups, but it must also accept a user-entered group and must state the source and version of every supplied list.

The proposed ordinary defaults are intentionally small: no group requirement unless selected, and only broadly useful quality/size scenarios. A user who wants strict group enforcement chooses it knowingly.

## Bundled preference packs

Deluno ships a curated, versioned catalogue of well-known release groups and
starter preferences with the application. Version one does **not** fetch a
remote guide pack or make users manage a guide repository. That avoids adding
another Configarr/Recyclarr-like service to an otherwise simple setup.

A Media Plan can:

- inspect the bundled pack, its provenance, and exactly which fields it would set;
- choose a supplied release-group tier or start without group enforcement;
- add a release group manually, with a preference (preferred, acceptable,
  avoid, or blocked) and an optional score;
- keep those additions as explicit local rules; and
- preview the field-level changes that arrive with a newer Deluno release
  before adopting them.

No application update silently changes a deliberately edited plan field.
Deluno records the bundled pack/version, adopted time, and overridden fields
with the plan. A user can retain the previous version or roll back an adopted
change with its recorded local overrides.

## Simple first, granular when wanted

The first screen is deliberately small, but it must never conceal what a
preset will do. Selecting a scenario immediately shows a plain-language
summary and an expandable **What this includes** view. That view lists the
quality ladder, selected size tier and its ranges, language behaviour, upgrade
behaviour, release preferences, and bundled-pack/version (when one supplied
the defaults).

Choosing a preset is only a quick starting point. **Build a detailed plan**
is available from the same screen and opens the full granular editor. It
supports the same fields an advanced Arr/Recyclarr/Configarr user expects:

- quality tiers and upgrade cutoff;
- per-tier size envelopes, including runtime-normalised episode sizes;
- required/preferred/acceptable/avoid/blocked release groups;
- language, audio, subtitle, edition, codec, HDR, remux, and source rules;
- scoring and custom-format-style rules, with an explanation trace; and
- per-library and per-title overrides without duplicating the entire plan.

The detailed editor groups these rules by outcome, keeps the current decision
summary visible, and offers a release-preview before saving. A person can
start simple, open one section, make a precise change, and still understand
the resulting rule. No advanced setting is removed or silently flattened into
a preset.

## Scenario-first flow

1. Choose a scenario: Family 1080p, Everyday TV, Premium 4K, Low storage, Anime, or Start custom.
2. Read the short result sentence and select movie/TV scope.
3. Optionally refine quality, size, language, or release-group preferences.
4. Attach the plan to a library; show the actual destination and connected sources as context.
5. Preview how a known release would be decided before enabling automated searches.

Advanced users can inspect and edit score components and guide provenance, but never have to construct a custom-format stack to get started.

## Migration

Existing quality profiles, custom formats, destination rules, and imported Arr/Recyclarr/Configarr data remain intact until a user explicitly maps them:

- exact mappings become a new local plan field;
- lossy mappings become a review item with the source value retained;
- unsupported settings remain in the immutable migration report rather than being guessed; and
- no migration deletes or overwrites the legacy configuration.

## Delivery sequence

1. Introduce the versioned plan record and explanation trace without changing existing selection behaviour.
2. Migrate the current policy-set starter UI to scenario-first plans, retaining direct routes.
3. Move quality, size, release-group, and custom-format ownership behind the plan with compatibility adapters.
4. Add bundled preference-pack preview/diff/adopt/rollback and manual
   release-group additions.
5. Add plan/library/title override tests, migration fixtures, and a real-provider end-to-end evidence run.

## Product decisions recorded

The product owner approved the following starting point on 14 August 2026:

1. The default journey is a quick scenario choice, but every preset must show
   exactly what it contains before it is applied.
2. Granular configuration remains a first-class capability. It is progressive
   disclosure, not a reduced feature set.
3. Plans resolve in this order: **Media Plan → Library override → title
   override**. A library is the ordinary assignment point; a title is the
   exception.
4. Deluno ships curated, bundled preference packs rather than depending on a
   remote guide-pack service. They remain versioned, previewable, and opt-in;
   they never silently replace local changes. Users can add release groups as
   explicit local rules.
5. Every plan includes a selectable size tier. Advanced users can set granular
   per-quality and runtime-normalised bounds, scoring, and enforcement.
