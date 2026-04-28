# Deluno Intelligence and Workflow Builder Direction

## Purpose

Deluno should eventually help users make better release and import decisions without turning the product into an opaque black box.

Two future capabilities are worth preserving space for:

- predictive scoring from past user outcomes
- a visual workflow builder for advanced import and processing pipelines

Neither should replace deterministic rules in the near term. They should sit on top of clear scoring, policy, and audit models.

## Predictive Scoring

Predictive scoring can become useful after Deluno has enough local history to learn from:

- releases the user accepted
- releases the user rejected
- forced grabs
- failed imports
- successful upgrades
- replacements that were later regretted
- release groups that are consistently good or bad for that library
- codec, source, HDR, language, subtitle, bitrate, and size patterns

The first implementation should not be cloud ML or a black-box model. It should be a local recommendation layer that produces a transparent adjustment on top of the deterministic score.

Recommended model:

- deterministic quality/custom-format score remains the source of truth
- predictive adjustment is optional and bounded
- every adjustment must expose an explanation
- users can disable it globally or per library/profile
- force override remains available for manual search
- automation should require confidence thresholds before auto-grabbing based on prediction

Example explanation:

> Deluno prefers this release because this profile has accepted this release group five times, similar WEB-DL 2160p files imported cleanly, and the bitrate is within your usual range.

Things predictive scoring must not do:

- override a hard block such as language, unwanted group, banned format, corrupt probe, or wrong episode/movie match
- hide why a release won
- auto-download risky releases without a configured confidence threshold
- send private user library data to an external service without explicit consent

## Visual Workflow Builder

A visual workflow builder could make advanced import paths easier for users who currently need multiple Arr instances, scripts, or external cleanup tooling.

Near-term Deluno should keep workflows simple:

- standard import
- refine before import
- destination-rule routing
- post-import metadata refresh
- activity and recovery visibility

The future visual builder should only appear when users choose advanced controls.

Potential blocks:

- search release
- send to download client
- wait for completion
- hand off to processor
- wait for processor output
- probe media file
- apply destination rule
- rename and hardlink/move
- refresh metadata
- notify external app
- stop monitoring when cutoff is met

Each block should have:

- clear input and output
- validation before save
- human-readable explanation
- dry-run preview
- audit trail when executed
- failure handling policy

The builder should not become a general automation toy. It should stay focused on media operations and ship with safe presets first.

## Product Guardrails

Deluno's order of operations should be:

1. Deterministic scoring and import correctness.
2. Full auditability and force override for manual decisions.
3. Dry-run previews for search, routing, and import outcomes.
4. Optional predictive recommendations based on local history.
5. Optional visual workflow builder for advanced users.

This keeps Deluno simple for beginners, powerful for advanced users, and safer than an opaque automated downloader.
