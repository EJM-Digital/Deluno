# Release preference contract

This is the repository copy of the normative release-preference contract in
[#354](https://github.com/jampat000/Deluno/issues/354). It is the meaning used
by the typed API, persistence, migration, import and search code. A score is
legacy provenance only; it is never Deluno's decision value.

## Vocabulary

- A **fact** is a normalized observation with a stable trait id, state
  (`present`, `absent`, `unknown`, or `conflicting`), evidence source,
  confidence, detector/version and an `open-world` or `closed-world` model.
- A **family** is a finite ordered set of levels, best first. A release gets
  one effective level per family after implications and core relationships are
  normalized. Levels are not added together.
- A **plan** is immutable and versioned. Its hash, ordered dimensions, targets,
  compatibility scope, provenance and overrides are recorded with every
  evaluation.
- An **evaluation** is the typed result for one release or installed file: gate
  outcomes, family states, targets, unknowns and explanation tokens.
- A **comparison** is a deterministic current-versus-candidate result under
  the same plan: rejected, needs review, equivalent, candidate better, or
  current better.

## Owner choices

| Choice | Meaning | Automatic effect |
| --- | --- | --- |
| Must have | Required hard gate | Present passes; absent rejects; unknown/conflicting needs review |
| Must not have | Forbidden hard gate | Present rejects; absent passes; unknown/conflicting needs review |
| Prefer | Ordered family with an explicit “stop when” target | Better proven level may upgrade only until that target |
| Nice to have | Ordered tie-break family | Chooses among equal candidates in the same search; never creates upgrades |
| I don't care | Neutral, recorded for information | Ignored by comparison |

The UI and API must not allow a ranked preference to drive upgrades without an
explicit target. An untargeted preference compiles as a tie-break.

## Comparison rules

Hard gates always run first. The default persistent order is quality/source,
HDR/video compatibility, video codec and bit depth, audio format, audio
channels/language, edition, release group/service, and proper/repack revision.
The order is stored in `dimensionOrder` and may be changed only as a new plan
version. Tie-break families and transient acquisition signals run last.

Comparison is lexicographic: the first differing persistent dimension decides.
There is no aggregate score and a lower-priority improvement cannot pay for an
earlier regression. Seeders, indexer priority, age, client load, ML confidence
and similar transient signals cannot create installed-file upgrade work.

For a family whose target has rank `T`, a current rank `R` meets the target when
`R <= T`; lower ranks are better. Once all gates and upgrade-driving targets
are met, scheduled automation retains the file even if a later release is
ranked above that target. Manual search may still show it.

## Evidence truth table

| Gate | Present | Absent | Unknown/conflicting |
| --- | --- | --- | --- |
| Required | Pass | Reject | Needs review |
| Forbidden | Reject | Pass | Needs review |

Open-world absence is never inferred from a missing token. A manual force may
override one action only, with confirmation and a stored reason.

Specific traits must not be counted twice: for example, TrueHD Atmos may imply
TrueHD and DTS:X may carry a DTS-HD MA core, but each is one effective audio
family level. Multiple tracks are evaluated as a set for required languages and
capabilities, and the best proven compatible track supplies a ranked audio
level.

## Required API/persistence invariants

1. Same facts plus the same plan hash produce the same result after restart.
2. A file compared with itself is equivalent; input ordering cannot change the
   winner.
3. A hard gate dominates preference and ML signals.
4. Unknown never becomes false, absent, zero or worst-ranked without a
   closed-world detector.
5. Every upgrade names a below-target persistent dimension that improves.
6. Equivalent content is deduplicated independently of ranking.
7. Every stored evaluation records the plan id/version/hash and canonical facts.
8. Normal UI/API output contains no aggregate release score. Original TRaSH or
   legacy scores may appear only as labelled provenance.

The canonical implementation is
`src/Deluno.Quality/ReleasePreferences/ReleasePreferenceContracts.cs`,
`ReleasePreferenceEvaluator.cs` and `PreferenceTraitRegistry.cs`. The
versioned endpoints are under `/api/v1/release-preferences`.
