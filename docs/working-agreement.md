# Working agreement (shared with Weir)

Kept permanently, the same in Weir's AGENTS.md (the owner, 7 Nov 2026: "we waste too much time getting to the end
result only to go backwards"; "thats awesome, this needs to match for Deluno as well then we keep pushing forward now").

**Each issue**
- An issue is raised by the owner, by the weekly mutation run, by a test failure, or by anything either session finds.
- A builder agent fixes it on its own branch in its own worktree, with a test that fails before the fix.
- A stronger model (Opus) reviews the fix before it merges; a risky or contested fix gets a round table. The review
  asks whether the fix is right and whether the test would fail if the fix were reverted.
- Deluno has no pull requests (see AGENTS.md, First Checks): the reviewed branch is merged into `main` locally and pushed, and CI
  on that push is the gate. A red run is fixed at once.
- It is proved live: the CI build goes onto the rig straight away and the fix is shown working on real use. Nobody
  waits for a release.
- The issue closes as soon as the proof is in, with the evidence in the closing note. It never sits waiting on the
  owner without something set up to finish it.
- Nothing per issue waits on mutation or any other long run.

**Tests and CI**
- GitHub CI does the heavy checks; this PC writes code and runs targeted tests.
- Every change keeps coverage, and every route, setting and screen control is tied to a test in
  `docs/testing/COVERAGE.md`. Deluno closes its register gaps toward 0 (216 on 7 Nov 2026), then turns on `--strict`.
- A test that fails sometimes is a bug: root-caused, never re-run until it passes.
- Tests actually run in CI: no skip guard that CI never meets (#1536).

**Mutation**
- Weekly on a schedule (`mutation-nightly.yml`, Saturday 11:17 UTC; Weir runs Sunday) and by hand. The scheduled run
  covers everything.
- It never gates a merge, a release or an issue: not releases, CI, `ci-passed`, branch protection or any check script.
- Each week's findings become issues, fixed during the week through this workflow; the next run checks them.
- Floors only go up. One cheap CI check, which never runs mutation, fails a change that lowers a floor in
  `docs/testing/mutation-thresholds.json` without a stated reason.
- Target: at least 80% in every area, then 100% accounted for (every mutant killed, or marked equivalent with a
  written reason that the review checks). Risky areas first (`docs/testing/mutation-risky-areas.json`; tracked in #1534).

**Releases**
- Fixes are batched into a release, not one release per change.
- A release needs the build, the golden-VM scenario round on that exact version (see AGENTS.md, Releases) and a clean
  audit. No mutation.

**Between sessions**
- Any instruction of the owner's that covers both apps goes to the other session straight away, in full and word for word.
- Shared design and behaviour rules are agreed once, applied to both apps, then measured side by side on the rig.
- Each session owns its own repo. The other reports what it finds and never changes it.
