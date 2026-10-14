# Coverage register

The owner's bar is that no behaviour of Deluno goes untested. This page and `coverage-register.json` make that a number
that a script checks, instead of a claim. Weir keeps a register of the same shape; the schema below is the shared one, and the
ids under `contract.` are shared with it.

## The schema (shared with Weir's register)

`coverage-register.json` is `{ "version": 1, "generatedFrom": string, "areas": { <area>: { "code": string, "name": string } }, "rows": [ ... ] }`.
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
| `vm` | no | Only `true`, only on a row whose `touches` is `disk` or `install`. Marks a row that only a real install can prove: NTFS-specific behaviour (alternate streams, case rules, long paths, ACLs), network shares (UNC/SMB/NAS), cross-drive or cross-volume moves and hard links, the installer, the update, the tray and Windows service mode. A `vm` row reaches `real` by a `scenario` proof alone; `real-fs` and every other type stay `inproc` for it. |
| `proofs` | yes | A list of `{ "type", "file", "name" }`. **Empty when the row is a gap.** |
| `covers` | no | Inventory keys the row accounts for (`route:GET /api/movies`, `web:/movies`, `service:X`, `lane:x`, `handler:X`, `task:x`, `healthcheck:X`, `migration:movies/V0007`, `integration:qbittorrent[.Op]`, `tray:Open`, `scenario:film.qbittorrent`). Weir's register uses the keys its own inventory produces. |
| `partial` | no | Proofs that touch the behaviour but do not prove it. Not counted; kept so a gap shows where to start. Checked like `proofs`. |
| `needs` | no | `{ "kind", "test" }`: the test that would close the gap. |
| `issue` | no | The number (a positive integer) of the open coverage issue that will close the row's gap. Listed in the totals below. |
| `audit` | no | Where the row came from. |

A proof's `type` is one of:

- `vitest`: a `*.test.ts(x)` file.
- `smoke`: a Playwright spec in `apps/web/tests`.
- `backend`: an xUnit, node or other test.
- `scenario`: a golden-path scenario script run on the golden VM.
- `recording-replay`: a test that replays the other app's recorded real reply from `tests/fixtures/recorded`.
- `real-fs`: a backend (C#) test that runs Deluno's real code against real files on the real file system (a temp folder; real hard links, moves, deletes, permissions) or a real SQLite database file (migrations, upgrades from released versions), with no mocked file system or storage. It satisfies `real` for a row whose `touches` is `disk` or `install`, unless the row is `vm`; for any other row it is an ordinary in-process test. The check verifies the claim statically: the test, or its class setup (the text before the class's first test), creates a real temp folder or database file (`Path.GetTempPath`, `Directory.CreateTempSubdirectory`, `Path.GetTempFileName`, `TestStorage.Create`, `MigratedDatabaseTemplate`, `ApplicationTestHost`, or a `Temp*`/`Scratch*` folder helper), does file or database work on it (`File.`, `Directory.`, `FileInfo`, `CreateHardLink`, `SqliteConnection`, a migration), and names no fake or in-memory file system or store (`ScriptedStateFileSystem`, `IFileSystem`, `Fake*`/`InMemory*` file system, storage, repository or database, `Null*Repository`, `:memory:`, `Mode=Memory`, a mocked file or storage interface). A class name, a fragment or `"*"` is judged over every test it covers, and every one must qualify. If the check cannot decide, the claim is refused: it is reported as a proof that claims a type it does not have, the row keeps only `inproc`, and the refusals are counted in the totals. A real-fs claim is never taken on trust; the check re-types nothing the test does not show.

Its `name` is a test, a test group or a class in `file` (or, for a scenario, a scenario id); `"*"` means the whole file.

### Contract ids

Every call, reply and report of `docs/deluno-weir-contract.md` has a row with id `contract.<subject>.<behaviour>` (area `contract`),
for example `contract.events.completed`, `contract.handoff.accepted`, `contract.send-again.delivered`. Subjects are `capabilities`,
`library-folders`, `handoff`, `status`, `cancel`, `outcome`, `events`, `handoffs`, `download-destinations`, `import-original`,
`send-again` and `auth`. Weir's register uses identical ids for its side of the same exchange, so a pair of rows can be read together.

## Proof levels

- **real**: a `scenario`; a `recording-replay`; or, for a screen behaviour only (a row in a screen area), a `smoke` spec against the real
  Deluno host whose API calls are **not stubbed**. A smoke test is stubbed when its body, a `beforeEach`/`beforeAll` of its file, or a
  helper of its file that it calls uses `page.route`, `route.fulfill` or `serveLive*`. This is decided from the spec's text; what cannot be
  decided (the test is not found by its name, or the proof names the whole file) counts as stubbed. The totals say how many.
- **inproc**: any other `vitest`, `smoke` or `backend` test. It runs Deluno's code, but the other side is Deluno's own stand-in.
- **none**: the row is a gap.

A row is **proven** when its best proof reaches `required`.

## The rules

1. **Everything is mapped.** `scripts/coverage/inventory.mjs` reads the source for every API route (each `Map*` call), web route
   (`apps/web/src/router.tsx`), hosted service, job lane, job handler, system task, health check, migration, integration client and
   operation, tray menu action and golden-path scenario id. Every item must be in the `covers` of some row. New code does not
   arrive unmapped: CI fails (`scripts/check-coverage-register.mjs`), and `--stubs` prints the rows to add.
2. **Every proof exists.** The file is in the repository and the name is in the file. A renamed or deleted test fails CI until the row is
   fixed; the row is then a gap again.
3. **Only those two fail CI** (and the check's own self-test). A gap, a malformed row, a cover of something that is gone, a proof of a type
   it does not have and the determinism rules are reported with counts; `--strict` fails on all of them. When the gaps are closed, CI runs `--strict`.
4. **Determinism.** No fixed `Start-Sleep`, `Task.Delay`, `Thread.Sleep` or `waitForTimeout` in a test without a `// reason:`
   (`# reason:` in PowerShell) comment on its line. Every golden-path scenario is runnable alone (registered under its own id in
   `ScenarioPlan.ps1`, with a defined group function). Every test of an external app is on a recording or listed in
   `tests/recorded-reply-allow-list.json`.

## Commands

```
node scripts/check-coverage-register.mjs               # the check CI runs; (a) and (b) fail it
node scripts/check-coverage-register.mjs --strict      # also fails on any gap and any rule breach
node scripts/check-coverage-register.mjs --list-gaps   # every gap, high first
node scripts/check-coverage-register.mjs --stubs       # row stubs for inventory items with no row
node scripts/check-coverage-register.mjs --write-doc   # rewrites the totals below
node scripts/coverage/inventory.mjs --list             # the inventory
node --test scripts/check-coverage-register.test.mjs   # the check's own tests
```

When you add a route, screen, job, migration, integration operation, tray action or scenario: add its row (with the key in `covers`
and, if a test proves it, the proof) in the same change. When you add a test that proves a gap, put it in `proofs` and the gap closes.

## Totals

<!-- coverage:generated:start -->
Generated by `node scripts/check-coverage-register.mjs --write-doc` from `coverage-register.json`. Do not edit by hand.

**10079** behaviours. **4415** reach their required proof level (44%); **5664** are gaps, **1291** of them high.

### By severity

| Severity | Rows | Reach their level | Gaps |
| --- | ---: | ---: | ---: |
| high | 1799 | 508 | 1291 |
| medium | 6166 | 3575 | 2591 |
| low | 2114 | 332 | 1782 |

### High rows by what they touch (these need `real`, except `none`, which needs `inproc`)

| Touches | High rows | High gaps |
| --- | ---: | ---: |
| other-app | 765 | 516 |
| disk | 528 | 412 |
| install | 138 | 78 |
| none | 368 | 285 |

### By best proof a row has

| Best proof | Rows |
| --- | ---: |
| none (a gap) | 5375 |
| inproc | 3228 |
| real | 1476 |

Smoke proofs: **889**; unstubbed (can reach `real` for a screen behaviour) **532**; stubbed **285**; not decidable statically, counted as stubbed **72**.

Real-fs proofs: **1477** claimed; **1393** verified and counted as real; **0** refused (the checker could not verify a real temp folder or database file with no fake, so they count as inproc); **84** verified but without effect (the row is "vm", or does not touch disk or install). **301** rows are "vm" (only a real install can prove them, so they need a scenario); **185** of them are still gaps.

### By area

| Area | What | Rows | Reach their level | Gaps | High rows | High gaps | No proof | Inproc | Real |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| activity | Activity, Indexer history, import recovery, dismiss-everywhere | 285 | 148 | 137 | 22 | 15 | 126 | 129 | 30 |
| api | API routes (every Map* call in src) | 608 | 162 | 446 | 220 | 187 | 423 | 154 | 31 |
| automation | Automation: search cycles, retention, download health (strikes), keep-seeding, blocklist, sharing rules | 439 | 171 | 268 | 66 | 56 | 254 | 131 | 54 |
| connections | Indexers, download clients, proxies, path mappings, library routing, media servers, subtitle providers, Seerr | 362 | 156 | 206 | 23 | 14 | 198 | 103 | 61 |
| contract | Deluno-Weir contract: every call, reply and report (ids shared with Weir) | 80 | 80 | 0 | 53 | 0 | 0 | 0 | 80 |
| dashboard | Dashboard (Live, System tabs, Needs you, health) | 290 | 168 | 122 | 10 | 7 | 116 | 153 | 21 |
| engines | Engines and external APIs: import/rename/delete engine, decisions, external integration API, webhooks, metadata broker, monitoring, workers | 2938 | 1378 | 1560 | 444 | 358 | 1475 | 1247 | 216 |
| guided-setup | Guided setup (setup guide), bundled components, companion tools | 336 | 131 | 205 | 45 | 42 | 197 | 115 | 24 |
| health | Health checks and the request pipeline | 46 | 24 | 22 | 5 | 4 | 20 | 24 | 2 |
| integrations | Integrations: download clients, Weir, indexers, media servers, metadata, Seerr, Trakt, subtitles, notifications | 809 | 156 | 653 | 209 | 173 | 634 | 105 | 70 |
| library | Movies and TV library lists, adding titles, bulk actions, collections, calendar, discover | 232 | 88 | 144 | 22 | 16 | 142 | 39 | 51 |
| migrations | Database migrations | 316 | 308 | 8 | 57 | 4 | 8 | 1 | 307 |
| movie-detail | Movie detail page and everything attached to one film | 195 | 62 | 133 | 26 | 19 | 130 | 38 | 27 |
| platform | Install, update, uninstall, service mode, tray, auth, backup, recycle bin, data, Docker, gates | 233 | 69 | 164 | 75 | 66 | 156 | 60 | 17 |
| platform-install | Windows tray, installer, self-update, Docker, packaging, Weir (processor) hand-off, golden-path scenarios | 337 | 175 | 162 | 67 | 49 | 125 | 154 | 58 |
| queue | Queue / Transfers, hand-off to the processor, stuck downloads, removal of downloads | 394 | 210 | 184 | 95 | 46 | 154 | 135 | 105 |
| scenarios | Golden-path scenarios (ScenarioPlan.ps1) | 77 | 77 | 0 | 77 | 0 | 0 | 0 | 77 |
| search-grab | Search, release choice, grab/dispatch, blocklist | 355 | 173 | 182 | 47 | 41 | 172 | 150 | 33 |
| services | Hosted services, job lanes, handlers and system tasks | 147 | 73 | 74 | 19 | 17 | 72 | 68 | 7 |
| settings-library | Settings 1: libraries, media management, naming, destination rules, policy sets, profiles, quality, custom formats, release rules, TRaSH guide sync | 375 | 126 | 249 | 44 | 39 | 246 | 68 | 61 |
| settings-lists | Settings 2: lists/intake, migration, metadata, auto-tagging, tags, general, notifications, UI, subtitles | 318 | 108 | 210 | 18 | 17 | 203 | 75 | 40 |
| shell | Shell, sign-in, first-run account, navigation, realtime plumbing, account/API-key security | 304 | 135 | 169 | 38 | 29 | 167 | 111 | 26 |
| show-detail | TV show detail page, episodes, numbering, Season pass | 203 | 68 | 135 | 27 | 20 | 134 | 46 | 23 |
| system | System screens: health, jobs, logs, backups/restore, recycle bin, updates, API access, security | 391 | 164 | 227 | 90 | 72 | 219 | 120 | 52 |
| web | Web routes (apps/web/src/router.tsx) | 9 | 5 | 4 | 0 | 0 | 4 | 2 | 3 |

### Gaps with an open issue

| Issue | Rows | Still gaps | High gaps |
| --- | ---: | ---: | ---: |
| #1281 | 169 | 169 | 29 |
| #1282 | 122 | 122 | 7 |
| #1283 | 206 | 205 | 42 |
| #1284 | 146 | 144 | 16 |
| #1285 | 137 | 133 | 19 |
| #1286 | 140 | 135 | 20 |
| #1287 | 182 | 182 | 41 |
| #1288 | 185 | 184 | 46 |
| #1289 | 141 | 137 | 15 |
| #1290 | 206 | 206 | 14 |
| #1291 | 252 | 249 | 39 |
| #1292 | 210 | 210 | 17 |
| #1293 | 276 | 268 | 56 |
| #1294 | 233 | 227 | 72 |
| #1295 | 165 | 161 | 48 |
| #1296 | 1603 | 1560 | 358 |
| #1297 | 456 | 446 | 187 |
| #1298 | 74 | 74 | 17 |
| #1299 | 22 | 22 | 4 |
| #1300 | 61 | 8 | 4 |
| #1301 | 653 | 653 | 173 |
| #1302 | 169 | 164 | 66 |
| #1303 | 4 | 0 | 0 |
| #1304 | 4 | 4 | 0 |

### Inventories (generated from source by `scripts/coverage/inventory.mjs`)

| Kind | Items |
| --- | ---: |
| route | 596 |
| webroute | 82 |
| service | 50 |
| lane | 25 |
| handler | 21 |
| task | 26 |
| healthcheck | 32 |
| migration | 295 |
| integration | 296 |
| tray | 20 |
| scenario | 77 |
<!-- coverage:generated:end -->

## Measured line and branch coverage (issue 1268)

The register above says what Deluno does and which test proves it. This section says how much of the code runs under a test.
CI's `coverage` job measures every production file and writes the table below from the merged report; the target is 100% line and
100% branch for every file.

<!-- coverage:measured:start -->
No measured report yet. CI's `coverage` job writes the first one (issue 1268); until then this block is a placeholder.

| Area | Line | Branch | Lines | Branches | Files |
| --- | ---: | ---: | ---: | ---: | ---: |
| not measured yet | n/a | n/a | 0/0 | 0/0 | 0 |
<!-- coverage:measured:end -->

What is measured, and by what:

| Code | Tool | Where it is configured |
| --- | --- | --- |
| Every Deluno .NET assembly (the seven test projects, merged per file) | coverlet (`XPlat Code Coverage`, cobertura), merged by ReportGenerator | `tests/coverage.runsettings`, `scripts/test-dotnet-serial.ps1 -Coverage`, `.config/dotnet-tools.json` |
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
The `coverage` job runs only when both the backend and the web tests run (any change touching both, or a manual run), so the numbers
are always whole-repository. Self-test: `node --test scripts/check-coverage-ratchet.test.mjs`.

### Exclusions

`docs/testing/coverage-exclusions.json` is the only place a file may be left out. Each entry is a path or glob with a reason of at least
one plain sentence; the ratchet fails on an exclusion without one. Excluded files are still listed, with their reasons, in the report.
Nothing is excluded by an attribute in source: `tests/coverage.runsettings` drops only compiler-generated code (`GeneratedCode`,
`CompilerGenerated`, the `obj` folder), not `[ExcludeFromCodeCoverage]` or `[Obsolete]`.

### How the baseline is filled

The checked-in baseline is an empty placeholder, which holds nothing back. The first time:

1. Run `ci.yml` by hand (`gh workflow run ci.yml`); a manual run runs every job, including `coverage`.
2. When it finishes, download the artifact: `gh run download <run-id> -n coverage-report -D coverage-report`.
3. Review `coverage-report/coverage-report.md` (the per-file table). Decide which files, if any, belong in `coverage-exclusions.json`
   (with reasons) and re-run if you add some.
4. Copy `coverage-report/coverage-baseline.json` over `docs/testing/coverage-baseline.json` (this is the `--update` step, run by CI on a
   copy), optionally copy `coverage-report/COVERAGE.md`'s measured block into this page, and commit both.

From then on every run holds each file to its baseline. After a push raises coverage, repeat steps 2 to 4 with that run's artifact to
raise the baseline; the `--update` step cannot lower it.
