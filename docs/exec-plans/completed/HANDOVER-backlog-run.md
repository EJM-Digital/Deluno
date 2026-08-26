# Handover — autonomous backlog run

**Repo:** `C:\Projects\Deluno` (private). `AGENTS.md` says
`C:\Users\User\Projects\Deluno` — that is wrong.
**Baseline:** `main` @ `392cc8a`. CI green, working tree clean (bar the
long-standing untracked `.tmp-*` files).
**Open issues:** 30 (six of them filed by the last session). Two are epics (#78, #106) — they close when their children
do. #158 is a note, not a work item.

---

## Read this first

The previous session shipped five PRs and then spent a long time on one large
piece without landing it. **Land things.** A merged PR that fixes one finding
beats a branch that fixes four. If a change is growing past a day's work, split
it at the first honest seam and ship the first half.

Nothing is left half-landed. Every branch from the last session is merged, the
working tree is clean, and the gate numbers below are what `main` produces
today. Start on the ordered list at the bottom.

---

## The single most important instruction

**Deluno must work at 20,000+ movies and TV shows, and libraries only grow.**

Nothing may be O(library size). Filter, sort and limit in SQL. Paginate. Never
load a whole catalogue to pick a few rows. A cap of 50,000 is the same bug with
a bigger number.

The owner's second standing instruction, given this session:

> **"Check over everything that SHOULD have been built and NEEDS to be built."**

That came from finding that the library list offered sorts and filters over
fields the API had never sent. Assume there are more of those. When a surface
offers a control, check something real is behind it.

---

## What the last session did

| PR | What | Issue |
|---|---|---|
| #170 | Existing-library import became a resumable background run with progress, pause, cancel and per-slice checkpoints | **#165 closed** |
| #172 | Intake stopped loading the whole catalogue every 5 min; episode recovery stopped reading every series | #164 |
| #173 | "Refresh everything" stopped silently doing 2.5% of it | #164 |
| #176 | Paged, searchable, sorted catalogue query (`/api/{movies,series}/page`), keyset, counted in SQL | #134, #164 |
| #177 | Proactive outbound throttling for indexers and metadata providers | #163 |
| #179 | The library list's codec, audio, release group, runtime, popularity, votes, path and size fields made real | #175 |

Filed while working: **#171** (dead lease recovery), **#174** (no virtualisation
anywhere in `apps/web`), **#175** (list fields the API never sends), **#178**
(per-indexer request interval), **#180** (imported titles keep the codec, audio
and release group).

---

## Where the last session stopped

### #180 — a defect found at the very end, filed not fixed

Real release names in the fixture generator exposed that the import's title
parser leaves file tokens in the title:
`Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS` imports as a film called
"Arrival 1080p BluRay x264 DTS-HD MA". A fix was attempted and **reverted rather
than half-landed**, because the widened token pattern did not take effect through
the import path and the reason was not established. #180 records exactly what was
tried, what was proven correct in isolation, and where to start looking. Do not
start by rewriting the regex.

### The library view rewrite — not started

This is the big one and the owner has chosen how it should land:

> **One PR, verified live** — server paging, filters, counts and virtualisation
> together, with before/after DOM-node and latency numbers at 20,000 items.

`apps/web/src/components/app/library-view.tsx` (3,791 lines) currently searches,
filters, sorts and counts the **whole catalogue in the browser**. The server
side it needs already exists and is merged (#176) — `/api/{movies,series}/page`
with `search`, `status`, `sort`, `direction`, `pageSize`, `pageToken`, returning
`{ items, nextPageToken, totalCount, facets }`.

The work:

1. `library-view.tsx` fetches pages instead of receiving one array. Search,
   quick filter and sort become server parameters; the chip counts come from
   `facets`; the header total comes from `totalCount`.
2. Virtualisation (#174) — `@tanstack/react-virtual`, both the card grid and the
   table.
3. Delete the unbounded `GET /api/movies` and `GET /api/series` and their
   callers (`library-page.tsx`, `dashboard-page.tsx` — the dashboard only needs
   counts, which is most of #132's win).
4. Retire `DelunoPaging.Paginate`. Its only caller is `GET /api/libraries`,
   which is config-sized; drop the vestigial paging rather than extend it.
5. The sorts and filters now backed by real data (codec, audio, group, runtime,
   popularity, votes, size, path) move server-side too. Anything still without
   data behind it comes out of the menu.

Measured facts to reuse: the unbounded list is **12.4 MB / 241ms** at 20,505
movies; a page is **31 KB / 30ms**; 100 pages walked with a slowest page of 22ms.

---

## Decisions already made — do not re-ask

| Question | Answer |
|---|---|
| How work lands | **PR per issue.** Wait for every required check to be green, then manually squash-merge. `main` is not protected on the current private free-plan repository, so never use auto-merge and treat the green-check rule as a discipline. |
| Scale target | **20,000+ items, unbounded growth.** |
| Dead UI controls | **Make them work** (owner's instruction), not remove them. #179 did the data half; the UI half is the library view rewrite. |
| Library view delivery | **One PR, verified live.** |
| Naming (#149) | Settled. `docs/MEDIA_AUTOMATION_TERMINOLOGY.md` is normative. |
| Breaking the public API | **Allowed.** Update `docs/deluno-ui-api-contract.md` in the same PR. |
| Throttle intervals (#163) | Settled: 2s per indexer (Prowlarr's floor), 10/s for metadata providers. |
| Pagination shape | Settled: **keyset**, implemented and merged. |

---

## The gate — after every commit

```powershell
Get-Process -Name "Deluno.Host" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build Deluno.slnx
```

**Must not drop below** (current numbers, which include everything merged):

| Suite | Count |
|---|---|
| Persistence | **311** |
| Platform | **86** |
| Movies | **61** |
| Series | **24** |
| Integrations | **33** |
| Tray | **3** |
| Worker | **23** |
| Playwright (`npm run test:web`, from repo root) | **189** |

**Migrations:** Platform **19**, Jobs **11**, Movies **12**, Series **12**.
`MigrationRunnerTests` asserts all of them by count *and* by name.

---

## Traps

- **Branch protection is not actually on.** The repo is private without GitHub
  Pro, so `gh api .../branches/main/protection` returns 403 and
  `gh pr merge --auto` merges **immediately**, before CI finishes. One PR was
  merged this way. **Do not use `--auto`.** Wait for
  `gh pr checks <n>` to be fully green, then `gh pr merge <n> --squash`.
- **CI's "Install browser dependencies" step hangs occasionally.** It stuck for
  over an hour once. Cancel the run and push again (a rebase works) rather than
  waiting.
- **Never `git add -A`.** The untracked `.tmp-*` files at the repo root predate
  this work; `git add -A` swept them into a commit. Stage `src tests docs
  scripts apps` explicitly.
- **Stop `Deluno.Host` before any build**, or the DLL copy fails on a file lock
  with a misleading error.
- **`npm run test:web` runs from the repo root**, not `apps/web`. It kills the
  dev backend; restart with `powershell -File scripts\start-local-app.ps1`.
- **Bash heredocs mangle backslashes.** Use the Write tool for scripts, and
  Python with `sys.stdout.reconfigure(encoding='utf-8')` and `newline=''` for
  scripted C# edits.
- **Do not blanket-replace short strings in C#.** A `.replace('m.', 's.')` when
  generating the series twin of a movies query turned `System.` into `Systes.`.
- **Dev DB has real fixtures** — Breaking Bad (71 eps), The Simpsons (885 eps),
  Blade Runner, Top Gun ×2, Top Gun: Maverick, two UX fixture libraries. Never
  reset it. Copy it and work on the copy (see "Running it at scale").
- **`"Deluno.Platform.Secrets"`** in the secret protectors is a cryptographic
  purpose label, not a namespace. Renaming it makes every stored secret
  undecryptable.
- **The Dockerfile is gone**, along with the Docker release job. Do not
  reintroduce either.
- **Background automation is ON** in the dev database (`jobs.autoStart`).

---

## Running it at scale, without hurting anything

```powershell
powershell -File scripts\start-local-app.ps1    # API 5099, Vite 5173, admin/admin1234
```

For scale work, run against a **copy**:

1. Copy `.deluno\data` to a scratch path.
2. **Set `jobs.autoStart` to `false` in the copy's `system_settings` before
   starting.** This matters: a 20,500-title fixture with automation on sent
   **~20,000 requests to TMDB from the owner's API key, 394 of which came back
   429**. That evidence is recorded on #163 and is what motivated #177 — but it
   should not happen again. #177's throttle now paces it; automation off means
   it does not run at all.
3. Start `Deluno.Host` with `.env.local` loaded **and**
   `$env:Storage__DataRoot` pointing at the copy, or it silently builds an empty
   database. `scratchpad\start-live.ps1` from the last session does this; it
   takes `-Reuse` to keep the copy between restarts.
4. `scripts\new-large-library-fixture.ps1` builds the tree — 20,000 movies in
   28s, 2,000 shows / 120,000 episodes in 58s. It now emits realistic release
   names, so it exercises the file-name parser too.
5. Import it with `POST /api/libraries/{id}/import-existing` (#170). 20,500
   movies in 3.4s.

---

## Order

1. **The library view rewrite** — one PR, verified live, as above. This is the
   owner's stated priority and the largest remaining piece.
2. **#132 / #131** — the dashboard stops polling 17 endpoints every 5s and takes
   deltas over SignalR instead. Much of the win comes free once the dashboard
   stops fetching whole catalogues, which step 2 does.
3. **#178** — per-indexer request interval, the settings surface for #177's
   mechanism. Small and self-contained.
4. **#171** — a restarted worker waits out its own dead leases. Small, and it
   fixes a 2.6-minute stall after any restart.
5. **#180** — imported titles keep their file tokens. Small, but read the issue
   before starting: an obvious-looking fix did not work.
6. **The audit the owner asked for** — walk every UI surface and check that each
   control has something real behind it, the way #175 was found. File what you
   find before fixing it.
7. **Everything else by label.** `frontend` cluster together, `api` together.
   #78 and #106 epics close last.

---

## Ideas the owner agreed to, not yet filed

- **Prioritise work instead of doing all of it.** A 1987 film at cutoff quality
  needs checking essentially never; something released last week and missing
  needs checking often. Spend effort where it matters and 20,000 items becomes
  ~50 that matter today. **This is the single biggest remaining scale idea.**
- **Ask the provider what changed.** TMDB publishes a daily changes feed. One
  request replaces 20,000 "did this change?" lookups.
- **Make the ongoing work visible.** Users press "Update All" because they
  cannot see the app already handling it.
- **Bulk actions as one resumable operation** with a position marker, like the
  import in #170, not thousands of separate jobs.
- **Deleting a big library, or a series with hundreds of episodes** — must not
  be one enormous locking transaction. Not yet audited.

---

## Technique that paid off

**Measure, do not assume.** `EXPLAIN QUERY PLAN` against a seeded copy of the
dev database found two things that were invisible in the code: an index on a
bare column is not used for a `COALESCE` expression, and an id tiebreaker fixed
ascending against a descending sort re-sorts every tie group — harmless when
values are distinct, 35ms a page on a freshly imported library where nothing has
a rating yet. `USE TEMP B-TREE FOR ORDER BY` means it is still sorting
everything.

**Check the unhappy path.** Every scale fix in this backlog has one. The forced
metadata refresh would have re-selected unmatchable titles forever without a
cooldown that survives the force flag; the import would have replayed a batch
without an idempotent upsert.

**Look at what the UI actually does with the data** before changing a list
endpoint. That is how #175 was found, and it changed the shape of the work.
