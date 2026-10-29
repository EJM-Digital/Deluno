# Coverage register

The owner's bar is that no behaviour of Deluno goes untested. This page and `coverage-register.json` make that a number
that a script checks, instead of a claim. Weir keeps a register of the same shape; the schema below is the shared one, and the
ids under `contract.` are shared with it.

## The schema (shared with Weir's register)

`coverage-register.json` is `{ "version": 1, "generatedFrom": string, "areas": { <area>: { "code": string, "name": string } }, "rows": [ ... ] }`, with an optional `"gapIncrease"` (see the gap ratchet below). `gapCeiling` is no longer used.
One row is one behaviour a person or another app can rely on:

| Field | Required | Allowed values |
| --- | --- | --- |
| `id` | yes | A unique string, never reused: `area.screen-or-subsystem.behaviour-slug`. Contract rows use the shared form `contract.<subject>.<behaviour>` (below). |
| `area` | yes | A key of `areas`. |
| `behaviour` | yes | One plain sentence. |
| `kind` | yes | `screen action`, `state`, `API route`, `job`, `integration-operation`, `failure-mode`, `migration`, `install-update`, `tray`. |
| `severity` | yes | `high` (data loss, wrong file or media handling, security, or a main flow: add a title, search, grab, import, remove), `medium` (a feature not working), `low` (cosmetic). |
| `touches` | yes | `other-app` (a download client, indexer, media server, Seerr, FlareSolverr, Weir, GitHub, metadata), `disk` (import, move, hard-link, delete, recycle bin, restore, backups), `install` (install, update, uninstall, service, tray, migrations on upgrade), `none`. |
| `required` | yes | Derived, and the check refuses any other value: `real` for a `high` row whose `touches` is not `none`; `inproc` for every other row. |
| `vm` | no | Only `true`, only on a row whose `touches` is `disk` or `install`. Marks a row that only a real install can prove: NTFS-specific behaviour (alternate streams, case rules, long paths, ACLs), network shares (UNC/SMB/NAS), cross-drive or cross-volume moves and hard links, the installer, the update, the tray and Windows service mode. A `vm` row reaches `real` by a `scenario`, `e2e` or `windows-e2e` proof alone; `real-fs` and every other type stay `inproc` for it. |
| `proofs` | yes | A list of `{ "type", "file", "name" }`. **Empty when the row is a gap.** |
| `covers` | no | Inventory keys the row accounts for (`route:GET /api/movies`, `web:/movies`, `service:X`, `lane:x`, `handler:X`, `task:x`, `healthcheck:X`, `migration:movies/V0007`, `integration:qbittorrent[.Op]`, `tray:Open`, `scenario:film.qbittorrent`). Weir's register uses the keys its own inventory produces. |
| `partial` | no | Proofs that touch the behaviour but do not prove it. Not counted; kept so a gap shows where to start. Checked like `proofs`. |
| `incomplete` | no | Only `true` (the check refuses `false` and any other value). Says the row's proofs reach its level but do not yet cover its whole claim; the row is then a **gap** until the missing part is proven and the mark is removed. `partial` keeps its meaning: part-proofs that never count toward the level. |
| `needs` | no | `{ "kind", "test" }`: the test that would close the gap. |
| `issue` | no | The number (a positive integer) of the open coverage issue that will close the row's gap. Counted in the statistics. A gap (a row below its level, or `incomplete`) must carry it, and a row that is no gap must not (rule 4). |
| `account` | no | Only on a gap that names an `issue`: the outside service (Trakt, Discord, Slack, Telegram, Pushover, a signed-in subtitle provider) whose account the proof needs. The row waits for that account and its issue; it is not counted as work a builder can finish. |
| `audit` | no | Where the row came from. |

A proof's `type` is one of:

- `vitest`: a `*.test.ts(x)` file.
- `smoke`: a Playwright spec in `apps/web/tests`.
- `backend`: an xUnit, node or other test.
- `scenario`: a golden-path scenario script run on the golden VM.
- `recording-replay`: a test that replays the other app's recorded real reply from `tests/fixtures/recorded`.
- `real-fs`: a backend (C#) test that runs Deluno's real code against real files on the real file system (a temp folder; real hard links, moves, deletes, permissions) or a real SQLite database file (migrations, upgrades from released versions), with no mocked file system or storage. It satisfies `real` for a row whose `touches` is `disk` or `install`, unless the row is `vm`; for any other row it is an ordinary in-process test. The check verifies the claim statically: the test, or its class setup (the text before the class's first test), creates a real temp folder or database file (`Path.GetTempPath`, `Directory.CreateTempSubdirectory`, `Path.GetTempFileName`, `TestStorage.Create`, `MigratedDatabaseTemplate`, `ApplicationTestHost`, or a `Temp*`/`Scratch*` folder helper), does file or database work on it (`File.`, `Directory.`, `FileInfo`, `CreateHardLink`, `SqliteConnection`, a migration), and names no fake or in-memory file system or store (`ScriptedStateFileSystem`, `IFileSystem`, `Fake*`/`InMemory*` file system, storage, repository or database, `Null*Repository`, `:memory:`, `Mode=Memory`, a mocked file or storage interface). A class name, a fragment or `"*"` is judged over every test it covers, and every one must qualify. If the check cannot decide, the claim is refused: it is reported as a proof that claims a type it does not have, the row keeps only `inproc`, and the refusals are counted in the totals. A real-fs claim is never taken on trust; the check re-types nothing the test does not show.
- `e2e`: a Playwright test in `tests/e2e` (the CI end-to-end suite, `docs/exec-plans/active/PLAN-E2E-IN-CI.md`): the shipped Deluno image, real download clients and Weir under Docker Compose, real files. Its `name` is the test's title as written, template parts (`${kind}`) included. It is `real` for every row, whatever it `touches`, and it satisfies a `vm` row. Cite it only for a row the test's own checks prove.
- `windows-e2e`: the same, for the Windows job's tests in `tests/e2e-windows` (a tray, an installer, a share); the type is accepted now and has no tests yet.
- `container-smoke`: a step of a workflow in `.github/workflows` (named by its `- name:`), or a container script in `scripts` (a name containing `docker`, `compose` or `container`, named `"*"`), that starts the shipped container image. It satisfies `real` for a row whose `touches` is `disk` or `install`, unless the row is `vm`; for any other row it is an ordinary in-process proof. The check verifies the claim from the step's text: the named step (or the whole script) must run `docker run` or `docker compose up`. A step that only builds, configures or scans an image (`docker build`, `docker compose config`, a vulnerability scan) is refused: it is reported as a proof that claims a type it does not have, the row keeps only `inproc`, and the refusals are counted in the totals. A step that only calls a script is not enough; name the script. The same type and rule are in Weir's register.

Its `name` is a test, a test group or a class in `file` (or, for a scenario, a scenario id); `"*"` means the whole file.

### Contract ids

Every call, reply and report of `docs/deluno-weir-contract.md` has a row with id `contract.<subject>.<behaviour>` (area `contract`),
for example `contract.events.completed`, `contract.handoff.accepted`, `contract.send-again.delivered`. Subjects are `capabilities`,
`library-folders`, `handoff`, `status`, `cancel`, `outcome`, `events`, `handoffs`, `download-destinations`, `import-original`,
`send-again` and `auth`. Weir's register uses identical ids for its side of the same exchange, so a pair of rows can be read together.

## Proof levels

- **real**: a `scenario`; an `e2e` or `windows-e2e` test; a `recording-replay`; a verified `real-fs` test or `container-smoke` step, for a row that touches `disk` or `install` and is not `vm`; or, for a screen behaviour only (a row in a screen area), a `smoke` spec against the real
  Deluno host whose API calls are **not stubbed**. A smoke test is stubbed when its body, a `beforeEach`/`beforeAll` of its file, or a
  helper of its file that it calls uses `page.route`, `route.fulfill` or `serveLive*`. This is decided from the spec's text; what cannot be
  decided (the test is not found by its name, or the proof names the whole file) counts as stubbed. The totals say how many.
- **inproc**: any other `vitest`, `smoke` or `backend` test. It runs Deluno's code, but the other side is Deluno's own stand-in.
- **none**: the row is a gap.

A row is **proven** when its best proof reaches `required` and it is not marked `incomplete`; every other row is a gap.

## The rules

1. **Everything is mapped.** `scripts/coverage/inventory.mjs` reads the source for every API route (each `Map*` call), web route
   (`apps/web/src/router.tsx`), hosted service, job lane, job handler, system task, health check, migration, integration client and
   operation, tray menu action and golden-path scenario id. Every item must be in the `covers` of some row. New code does not
   arrive unmapped: CI fails (`scripts/check-coverage-register.mjs`), and `--stubs` prints the rows to add.
2. **Every proof exists.** The file is in the repository and the name is in the file. A renamed or deleted test fails CI until the row is
   fixed; the row is then a gap again.
3. **Only those fail the check without `--base`; with `--base` a rise in the gaps fails it too** (and the check's own self-test). A gap, a malformed row, a cover of something that is gone, a proof of a type
   it does not have and the determinism rules are reported with counts; `--strict` fails on all of them. When the gaps are closed, CI runs `--strict`.
4. **Every gap has an owner, and only a gap has one.** A row that is no gap but still carries `issue` is stale, and a gap with no `issue` is unowned; both fail the check
   (CI's `(e)` and `(f)`), listed by row id. Set `issue` to the area's coverage issue ("Coverage: prove every behaviour in the <area> area") and remove it when the row is proven.
5. **Determinism.** No fixed `Start-Sleep`, `Task.Delay`, `Thread.Sleep` or `waitForTimeout` in a test without a `// reason:`
   (`# reason:` in PowerShell) comment on its line. Every golden-path scenario is runnable alone (registered under its own id in
   `ScenarioPlan.ps1`, with a defined group function). Every test of an external app is on a recording or listed in
   `tests/recorded-reply-allow-list.json`.

## Commands

```
node scripts/check-coverage-register.mjs               # without --base: (a) and (b) fail it
node scripts/check-coverage-register.mjs --strict      # also fails on any gap and any rule breach
node scripts/check-coverage-register.mjs --list-gaps   # every gap, high first
node scripts/check-coverage-register.mjs --stubs       # row stubs for inventory items with no row
node scripts/check-coverage-register.mjs --base origin/main   # the check CI runs, with the gap ratchet
node scripts/check-coverage-register.mjs --report      # writes artifacts/coverage-register.md (the statistics)
node scripts/coverage/inventory.mjs --list             # the inventory
node --test scripts/check-coverage-register.test.mjs   # the check's own tests
```

When you add a route, screen, job, migration, integration operation, tray action or scenario: add its row (with the key in `covers`
and, if a test proves it, the proof) in the same change. When you add a test that proves a gap, put it in `proofs` and the gap closes.

## Statistics

The totals (rows, gaps by severity, area and proof level, the open issues, the inventories) are not kept in this repository, so pull
requests that change the register do not conflict on them. CI writes them to the `Repository checks` job summary and uploads them as the
`coverage-register` artifact; locally, `node scripts/check-coverage-register.mjs --report` writes `artifacts/coverage-register.md` (not
committed) and `--list-gaps` and `--json` print them.

### The gap ratchet

The gaps may only go down. CI passes the register as it was before the change (`--base`: on a pull request the base commit, on a push to
main the previous commit), and the check fails when this commit has more gaps than that. Locally, use `--base origin/main`. No gap count is
stored in the register. A first push (an all-zero "before") and a force push have nothing to compare with: CI says so and skips the ratchet.

A rise that cannot be avoided needs an entry in the register, in a pull request of its own, matching the rise exactly:

```json
"gapIncrease": { "from": 5201, "to": 5203, "reason": "two rows for a screen that cannot be tested until issue N lands" }
```

`from` must be the gaps on the base and `to` the gaps now, and `reason` must not be blank. An entry that does not match the rise changes
nothing, and the entry is removed once the rise is merged. `gapCeiling` is not used and the shape check refuses it.

## Measured line and branch coverage (issue 1268)

The register above says what Deluno does and which test proves it. This section says how much of the code runs under a test.
CI's `coverage` job measures every production file and writes the table below from the merged report; the target is 100% line and
100% branch for every file.

CI's `coverage` job writes the per-file table to its job summary and to the `coverage-report` artifact. This page keeps the per-area
summary of the checked-in baseline (the floor each file is held to), and `coverage-gaps.md` lists every file still below 100% with the
open issue that closes it. Both are generated by `node scripts/coverage/coverage-docs.mjs --write`, and `npm run ci:check` fails when
either is out of date, so they cannot drift from the baseline. `coverage-areas.json` names the issue of each area.

<!-- coverage-table:start (generated by scripts/coverage/coverage-docs.mjs; do not edit) -->

| Area | Files | At 100% line and branch | Below | Average line | Average branch | Issue |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| apps/web | 411 | 98 | 313 | 73.5% | 62.8% | #1329 |
| scripts | 14 | 1 | 13 | 78.9% | 85.9% | #1268 |
| src | 4 | 1 | 3 | 94.3% | 88.5% | #1268 |
| src/Deluno.Api | 47 | 23 | 24 | 95.2% | 72.2% | #1297 |
| src/Deluno.Companions | 10 | 1 | 9 | 68.1% | 50.7% | #1295 |
| src/Deluno.Connections | 46 | 21 | 25 | 88.7% | 79.8% | #1301 |
| src/Deluno.Contracts | 98 | 32 | 66 | 90.8% | 84.4% | #1296 |
| src/Deluno.Filesystem | 37 | 7 | 30 | 76.9% | 64.1% | #1296 |
| src/Deluno.Host | 1 | 0 | 1 | 0.0% | 0.0% | #1302 |
| src/Deluno.Infrastructure | 44 | 11 | 33 | 85.9% | 73.8% | #1296 |
| src/Deluno.Intake | 12 | 7 | 5 | 92.7% | 66.3% | #1296 |
| src/Deluno.Integrations | 157 | 41 | 116 | 87.6% | 69.8% | #1301 |
| src/Deluno.Jobs | 114 | 86 | 28 | 93.8% | 74.5% | #1296 |
| src/Deluno.Libraries | 27 | 21 | 6 | 91.0% | 76.4% | #1296 |
| src/Deluno.Media | 45 | 24 | 21 | 92.2% | 87.9% | #1296 |
| src/Deluno.Movies | 85 | 75 | 10 | 95.3% | 44.4% | #1296 |
| src/Deluno.Notifications | 8 | 5 | 3 | 95.8% | 76.1% | #1301 |
| src/Deluno.Platform | 230 | 180 | 50 | 96.0% | 79.5% | #1302 |
| src/Deluno.Quality | 78 | 19 | 59 | 90.0% | 74.9% | #1296 |
| src/Deluno.Realtime | 7 | 4 | 3 | 93.2% | 98.3% | #1296 |
| src/Deluno.Recovery | 12 | 2 | 10 | 75.4% | 69.4% | #1296 |
| src/Deluno.Retention | 13 | 1 | 12 | 91.6% | 69.2% | #1296 |
| src/Deluno.Security | 43 | 15 | 28 | 84.0% | 66.3% | #1302 |
| src/Deluno.Series | 110 | 92 | 18 | 94.2% | 75.0% | #1296 |
| src/Deluno.Worker | 135 | 68 | 67 | 88.5% | 65.3% | #1296 |
| **All** | 1788 | 835 | 953 | 87.1% | 69.8% | |

Averages are per file, from the checked-in baseline (the floor the ratchet holds each file to), not weighted by size.

<!-- coverage-table:end -->

What is measured, and by what:

| Code | Tool | Where it is configured |
| --- | --- | --- |
| Every Deluno .NET assembly (the seven test projects, merged per file) | coverlet (`XPlat Code Coverage`, opencover), merged by ReportGenerator | `tests/coverage.runsettings`, `scripts/test-dotnet-serial.ps1 -Coverage`, `.config/dotnet-tools.json` |
| `apps/web/src` | vitest with v8 (lcov) | the `test.coverage` block of `apps/web/vite.config.ts`, `npm run test:unit:web:coverage` |
| `scripts/**/*.mjs` and `services/metadata-gateway/src` | `node --test --experimental-test-coverage` (lcov) | the `coverage-node` job of `.github/workflows/ci.yml` |

`sdk/typescript` has no tests yet, so it has no coverage; PowerShell scripts are not measured by any tool yet. Both are gaps to close, not exclusions.

The merge is `scripts/coverage/merge-coverage.mjs` (self-test: `node --test scripts/coverage/merge-coverage.test.mjs`). It writes one row per
production file with line %, branch % and covered/total, as `coverage-report.json` and `coverage-report.md` in the `coverage-report` CI
artifact (also on the run's summary page), next to an HTML report of the .NET code.

### The ratchet

`docs/testing/coverage-baseline.json` holds, per file, the line and branch percent last reached (rounded down to two places).
`node scripts/check-coverage-ratchet.mjs --report <coverage-report.json>` fails when any file measures below its baseline. With
`--update` it raises the baseline to the report and never lowers a number (add `--prune` to drop files that no longer exist).
The `coverage` job runs on every push that changes either side (backend, web, or both) and on a manual run. It measures the side that ran;
a file the run did not measure is left out of the comparison (the ratchet only holds files that are in the report), so a one-sided push
checks that side without a half report looking like a drop. Self-test: `node --test scripts/check-coverage-ratchet.test.mjs`.

### Exclusions

`docs/testing/coverage-exclusions.json` is the only place a file may be left out. Each entry is a path or glob with a reason of at least
one plain sentence; the ratchet fails on an exclusion without one. Excluded files are still listed, with their reasons, in the report.
Nothing is excluded by an attribute in source: `tests/coverage.runsettings` drops only compiler-generated code (`GeneratedCode`,
`CompilerGenerated`, the `obj` folder), not `[ExcludeFromCodeCoverage]` or `[Obsolete]`.

### How the baseline is filled

The checked-in baseline was first filled from CI run 38639740644. To raise it:

1. Run `ci.yml` by hand (`gh workflow run ci.yml`); a manual run runs every job, including `coverage`.
2. When it finishes, download the artifact: `gh run download <run-id> -n coverage-report -D coverage-report`.
3. Review `coverage-report/coverage-report.md` (the per-file table). Decide which files, if any, belong in `coverage-exclusions.json`
   (with reasons) and re-run if you add some.
4. Copy `coverage-report/coverage-baseline.json` over `docs/testing/coverage-baseline.json` (this is the `--update` step, run by CI on a
   copy), run `node scripts/coverage/coverage-docs.mjs --write`, and commit both together.

From then on every run holds each file to its baseline. After a push raises coverage, repeat steps 2 to 4 with that run's artifact to
raise the baseline; the `--update` step cannot lower it.
