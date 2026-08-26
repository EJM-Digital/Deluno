# AUDIT-002 — Status colours say different things in different places

Raised 2026-08-27, out of [#300](https://github.com/jampat000/Deluno/issues/300). Parked for a wider
discussion rather than fixed in passing, because the fix crosses every screen
that shows a state and #300's presentation depends on the outcome.

## The rule, which already exists

[#290](https://github.com/jampat000/Deluno/issues/290) settled it when it took hue away from navigation:

| Hue | Means |
|---|---|
| **green** — `success` / `ok` | Healthy. Done. *"A colour whose absence you scan for."* |
| **amber** — `warning` / `warn` | Needs **you**. Nothing happens until you act. |
| **blue** — `info` | Work in motion. #290 names the cases: *job running or queued, transfer moving, processing*. |
| **red** — `destructive` / `bad` | Failed. |
| **grey** — `muted` / `idle` | Nothing to do. Off, unknown, not applicable. |

Nothing written since has been checked against it, because nothing in the
codebase states it. Each screen decides its own tone at the point of use.

## One state, more than one colour

The same thing, at the same moment, in different parts of the app.

| State | Transfers list | Pipeline strip | Library card | Should be |
|---|---|---|---|---|
| A release ready to import | **green** — `queue-screen.tsx:1047` | **grey** — `acquisition-pipeline.tsx:83` | **blue** — `media-status-presentation.ts` `importReady` | blue: mid-pipeline, not finished |
| Monitored title with no file | **blue** "Watching for it" — `calendar-page.tsx:430` | — | **amber** "Missing" — `media-status-presentation.ts` | blue: Deluno is looking, nothing needs you |
| Held for the processor | **blue** — `queue-screen.tsx:1048` | **blue** — `acquisition-pipeline.tsx:81` | **amber** "Waiting for processor" — `media-status-presentation.ts` | blue: two of three already agree |

## Amber where nothing needs you

Amber is the one signal that has to stay trustworthy. Four places spend it on
work that is proceeding normally, which teaches people to stop reading it.

| Chip | Where | Why it is wrong |
|---|---|---|
| Running | `job-status-constants.ts:60` | A job in flight. #290 names this blue by name. |
| Rate-limited | `connections/format.ts:6` | Deluno backed off on purpose and resumes itself. |
| Missing | `media-status-presentation.ts` | It is out, Deluno is looking. You do nothing. |
| Waiting for processor | `media-status-presentation.ts` | In flight, and blue everywhere else. |

## Three smaller ones, same cause

| Chip | Where | Why it is wrong |
|---|---|---|
| Importing — green | `acquisition-pipeline.tsx:84` | Green is *done*. This is in motion. |
| Queued — grey | `job-status-constants.ts:57`, `acquisition-pipeline.tsx:74` | Grey is *nothing to do*. #290 names queued blue. |
| Category route — blue | `connections-screen.tsx:582` | A configuration fact, not motion. |

## And two vocabularies for the same five ideas

`Chip` takes `ok warn info muted bad`. `StatusLed` takes `ok warn info idle danger`.
Same five colours, two sets of words, so neither can be checked against the other
and nothing can assert that a state is coloured the same way in both.

## Proposal

One table mapping **state → tone → label**, the way `lib/configuration-areas.ts`
now holds the area explainers, with a test that pins it. Merge the two tone
vocabularies onto one set of names. Then a state cannot be coloured twice,
because there is only one place that colours it.

## Why this blocks part of #300

#300 splits `wanted_status` into four values, and the tone for each is the
question. Under the rule above the answers are not a matter of taste:

- `covered` → green. It is done.
- `upgrade` → blue. In motion.
- `missing` → blue, not amber. Deluno is looking; nothing needs you.
- not-out-yet → grey. Nothing to do.

The label for the fourth value is still open: *Not out yet* / *Unreleased* /
*Waiting*. "Waiting" matches the stored word but collides with the processor
hand-off status, which uses the same word for the opposite meaning.

A title inside its retry window stays `missing` under this scheme: Deluno is
working on it and the window is only pacing — the default is 24 hours per
library, set by `RetryDelayHours` and stamped after every attempt in
`LibrarySearchJobHandler.cs:204`. Grey would say nothing is happening, which
is false.
