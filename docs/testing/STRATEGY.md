# Deluno testing strategy

The bar, in the owner's words (14 Oct 2026): "robust full 100% app testing coverage, no gaps in testing at all... every screen, every
menu, every snippet of code, every function needs to have a test and be proven to work... designed in a way that a senior software
development team would do and do better." Weir holds the same bar. Both apps use one register format, and the calls between them use
shared `contract.*` ids.

"Covered" means all seven measures below hold together. Any one of them alone can be gamed: a line can be run by a test that asserts
nothing, and a behaviour can be listed without being proven. So no single number counts as coverage.

## The seven measures, each enforced in CI

| # | Measure | What it proves | Tool | Gate |
|---|---|---|---|---|
| 1 | **Behaviour register**: every screen, menu item, setting, API route, job, migration, integration operation, failure mode, tray action and install/update path has a row, and each row names the tests that prove it | nothing the app does is unlisted, and every listed thing is proven at its level | `docs/testing/coverage-register.json`, `scripts/check-coverage-register.mjs` | unmapped item or missing test fails now; any gap fails once `--strict` is on |
| 2 | **Line and branch coverage**, measured for every production assembly and module | every line and every branch of code runs under a test | coverlet (all .NET test projects), v8 (vitest, Node scripts), merged per file | a per-file ratchet that never goes down, ending at 100%; every exclusion is listed with a reason |
| 3 | **Mutation score**: the tests fail when the code is deliberately broken | the tests assert, they don't just run | Stryker.NET, StrykerJS | a per-project threshold that ratchets up; changed files must score 100% killed or explain each survivor |
| 4 | **End to end per screen and menu**: every screen and every menu or drawer action through the browser against the real Deluno server, with the calls it depends on not stubbed | the screens work with the real backend | Playwright, plus a guard that rejects `page.route` and `serveLive*` in specs marked real | each screen route and menu action in the inventory has a real spec |
| 5 | **Real other apps, real disk, real install**: anything that talks to another app, changes files on disk, or installs, updates or runs the tray | the app works with the real world, not with our guess of it | golden-VM scenario suite (each scenario runs alone), recorded real replies (`tests/fixtures/recorded/`) with a freshness check | the row's `touches` decides it: other-app, disk and install rows need one of these |
| 6 | **Properties**: parsers, matchers, quality and custom-format scoring, naming templates, path mapping, release-name and episode parsing hold for generated inputs, not only the examples someone thought of | edge cases nobody wrote by hand | FsCheck, fast-check | each listed module has property tests |
| 7 | **Determinism**: every test passes or fails the same way on every run | a red result is a real defect, and a green one is real proof | static check: no fixed sleeps without a reason, every wait bounded and on an event; nightly repeat of each suite N times | a flake is a defect with an issue, never a retry or a quarantine |

The supporting checks: every migration is tested from the previous schema with real data in it; every route answers 401 or 403 for a
missing or wrong credential (generated from the endpoint map); accessibility (axe) on every screen; and the Deluno–Weir contract is
replayed on both sides from the same recordings.

## Tiers

- **Every push (CI):** build; measures 1, 2 and 7's static check; unit, in-process and property tests; the Playwright real specs;
  recorded-reply replays; mutation on the changed files.
- **Nightly:** full mutation run; every suite repeated to catch flakes; recording freshness against the newest version of each app.
- **Before a tag:** the full golden-VM scenario suite on the exact commit. It is the final proof, not the place where bugs are found.
  A gap found there is a gap in measures 1 to 6, and gets a test at the lowest layer that can catch it.

## How a gap is closed

1. Its register row names the test to write and the proof level it needs.
2. The test is written to fail first, against the real code. For another app, it uses that app's recorded reply.
3. The row's `proofs` gets the test. The check confirms that the test exists and reaches the level.
4. The coverage, mutation and determinism gates pass for the files it touches.

## Where things are

- The register and its rules: [COVERAGE.md](COVERAGE.md)
- The golden-VM suite: [../golden-path.md](../golden-path.md)
- Recorded replies, and what is still missing: `tests/fixtures/recorded/README.md`, `tests/fixtures/recorded/MISSING.md`
- The Deluno–Weir contract: [../deluno-weir-contract.md](../deluno-weir-contract.md)
