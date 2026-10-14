# DESIGN-007 — Removing a title, and being able to get it back

> **Status: settled.** The audit is fact — it is what the code does today.
> Eighteen decisions were taken with James one at a time and are recorded below
> in his words; nothing is left open.
>
> Three of them corrected earlier claims of mine, every time for the same
> reason: the capability already existed and the design was about to duplicate
> it. The file presence check, the sharing rule that owns seeding, and the
> recycle bin retention that is already enforced on the heartbeat. **Check
> before building** — in this codebase the answer is usually already there under
> a name you did not search for.
>
> **Later authority.** The 26–27 Sep 2026 cleanup-rules decision sheet and its
> follow-on issues (#874, #585, #924, #925, #933…) are the latest decisions and
> override anything below that they touch — the block-release key and the
> Queue's removal dialog have both moved on from what this page first
> described. Where the two disagree, the sheet wins; this page's history stays
> as a record of how we got here.

James, on the scenario that started this:

> "If title already exists or was previously downloaded and has been deleted —
> currently radarr and sabnzbd etc keep that history and prevent the title from
> being downloaded again but doesn't really tell the user why or how to fix
> it."

And on why a table rather than another fix:

> "I think the better way is if we put a design together for delete scenarios
> and the outcomes... we dont want to have delete just being delete with nothing
> matching, it needs to be 1:1 mapping where possible for all delete routes and
> scenarios."

---

## Why this exists

Deluno removes things in **six** places, and they do not agree. Three spell the
action as a bare string, one builds an ownership opinion that nothing is
obliged to consult, and the two a person triggers deliberately — the queue
action and the force — consult nothing at all.

**And no removal path touches the download client.** Removing a title cancels
its jobs, moves its files to the recycle bin and deletes the row. The client
keeps the transfer, the infohash and the history; the processor keeps its
hand-off. Add the title again and it will not download. That is the direct
cause of the scenario at the top of this page, not an edge case near it.

## The audit

Every route below was read, not recalled. Where something is inferred rather
than verified it says so.

### Delete routes that touch media or acquisition state

| Route | What it verifiably does | What it leaves behind |
|---|---|---|
| `DELETE /api/{movies,series}/{id}`<br>`POST /api/{movies,series}/bulk` (remove) | Cancels pending jobs for the entity. If *delete files*: moves tracked files to the **recycle bin**. If *don't add it back*: writes an intake exclusion per origin source. Deletes the catalogue row. Records activity. | The client's transfer, the client's history, the processor hand-off, and every dispatch row |
| `POST /api/download-clients/{clientId}/queue/actions` | Passes **any** action string straight to `ExecuteActionAsync` — including `delete-with-data` | Nothing checked: no ownership test, no preview, no record of why |
| `GET /api/download-clients/{clientId}/queue/{id}/cleanup-preview` | Builds the ownership/threshold opinion — *"Deluno will not remove an external-client item or its payload without proven ownership"* | It is a **separate GET**. Nothing forces a caller to ask it, and the action endpoint above never does |
| `DELETE /api/v1/download-dispatches/{dispatchId}` | Soft-archives Deluno's own dispatch record with a reason | The client keeps the release — **and the evidence that Deluno ever fetched it is now gone** |
| `DELETE /api/libraries/{id}` | `DELETE FROM libraries WHERE id = @id`, then removes automation state and publishes events | Wanted rows, tracked files, dispatches and hand-offs keyed to that library. Media state lives in a **different database**, so no foreign key could have saved this |
| `DELETE /api/download-clients/{id}` | `DELETE FROM download_clients WHERE id = @id` | In-flight dispatches keep a `DownloadClientId` that no longer resolves |
| `DELETE /api/integrations/processors/connections/{id}` | `DELETE FROM processor_connections WHERE id = @id` | Open hand-offs keep a processor name that no longer resolves |
| `DELETE /api/exclusions/{id}` | Removes one exclusion row | Nothing — this is the "let it back in" route, and it is correct |
| `DELETE /api/recycle-bin/{id}`<br>`POST /api/recycle-bin/cleanup` | Permanently removes recycled files *(behaviour inferred from the contract; not read line by line)* | **The only genuinely irreversible deletion in the product** |
| `DELETE /api/{movies,series}/import-recovery/{id}` | Dismisses an import-recovery case *(inferred)* | The file it was about |
| ~~`DELETE /api/metadata/cache`~~ | Cleared cached metadata. **Removed in rc.27 (#1116):** nothing called it | Nothing that matters |

Config deletes — API keys, indexers, quality profiles, tags, custom formats,
policy sets, library views, webhooks, backups, path mappings, the setup draft —
are out of scope here. They delete a row and own nothing on disk.

### Re-download routes

| Route | What it does | Does it clear anything first? |
|---|---|---|
| `POST /api/{movies,series}/{id}/search` | Full decision pipeline, then grabs the winner | **No** |
| `POST /api/series/{id}/seasons/{n}/search`, `/episodes/search` | The same, scoped | **No** |
| `POST /api/{movies,series}/bulk/search` | The same, many titles | **No** |
| `POST /api/libraries/{id}/search-now` | The same, whole library | **No** |
| `POST /api/{movies,series}/{id}/grab` | Sends one chosen release to a client | **No** |
| ~~`POST /api/v1/download-dispatches/{dispatchId}/retry`~~ | Refused unless the grab status was `failed`; enqueued a library search job with `maxItems: 1`. **Removed in rc.27 (#1116):** no screen called it, and Search now does the same | **No** — so it walked into the same wall the first attempt hit |
| `POST /api/integrations/processors/handoffs/{id}/retry` | Re-submits a hand-off *(inferred)* | n/a |
| `POST /api/jobs/retry-failed` | Re-runs failed jobs | **No** |
| `POST /api/{movies,series}/{id}/force-redownload` (PR #432) | Clears hand-off, download and exclusions, then searches | **Yes — and it is the only one** |

**Of ten re-acquisition routes, exactly one clears anything at the client
first, and it is not merged yet.** Every other path asks a client for a release
it may already be refusing, and until PR #432's qBittorrent change, was told
`Ok.` when it was refused.

### The four questions, where the answer is interesting

**Removing a title — should it do more?** Yes: forget at the client. It is the
single change that stops the scenario arising rather than explaining it
afterwards. **Can it?** Yes — the dispatch record names the client and the
queue item. **Is it needed?** Yes; this is the cause.

**`queue/actions` — should it do more?** Yes: consult the same ownership
opinion the preview builds. **Can it?** Yes, `PreviewCleanupAsync` is on the
same service. **Is it needed?** Yes — it is the widest-open removal in the
product and the one with no record of why.

**Archiving a dispatch — is it needed?** Yes, for tidiness, but it should not
be the same act as *forgetting*. Archiving the only record that Deluno fetched
a title is what would blind the "previously downloaded" blocker. **Should it do
more?** It should keep the fetch fact even when the operational detail is
archived.

**Dispatch retry — should it do more?** Yes: clear before it searches, exactly
as the force does. **Is it needed at all?** Arguably not, once the force exists
and once failures are remembered per release — it is a search with extra steps
and one guard.

**Search / grab — should they clear first?** No. Clearing is a decision a
person makes; a routine search doing it silently would remove a seed nobody
asked to lose. They should *report* the refusal, which the qBittorrent honesty
change now makes possible, and offer the force.

### Cases nobody had enumerated

1. **Deleting a library orphans its media state.** Wanted rows, tracked files,
   dispatches and hand-offs survive it, in another database, unreachable and
   uncounted.
2. **Deleting a download client leaves dangling dispatches.** They still name a
   client id that resolves to nothing, so nothing can ever clear them.
3. **Deleting a processor connection leaves open hand-offs** naming a processor
   that no longer exists — and a hand-off is what blocks an import.
4. **Archiving a dispatch destroys the evidence** that Deluno ever fetched the
   title, which is the fact the "previously downloaded" blocker depends on.
5. **`queue/actions` is an unguarded removal API** that can delete any queue
   item with its data, owned by Deluno or not.
6. **Retry-after-failure hits the same wall,** because it clears nothing.
7. **Recycle-bin cleanup is the only irreversible delete** in the product, and
   nothing in the table above treats it differently from the reversible ones.


---

## The three questions

These are not "delete scenarios". Every case is a combination of three
questions, and only the third is about deleting.

1. **What does Deluno believe?** The wanted row: `HasFile`, quality, monitored,
   exclusion.
2. **What is actually there?** The file on disk.
3. **Who else is holding a copy?** The download client's transfer, the download
   client's *history*, the processor's hand-off.

**Question 2 is answerable, and nothing asks it.**

*(Corrected. The first draft of this document said Deluno had no way to check
whether a library file still exists. That was wrong — it was written from a
search for "scan", "rescan" and "refresh", and the feature is called
reconciliation. The mistake matters, because it would have had us build a
second one.)*

`FilesystemReconciliationService` already walks every library, calls
`File.Exists` on each tracked path, raises a `missingTrackedFile` issue, and
offers a `mark-missing` repair that calls `MarkTrackedFileMissingAsync` on the
catalogue. It refuses any path outside the library root and it never deletes
anything. It is reachable at `GET /api/filesystem/reconciliation` and
`POST /api/filesystem/reconciliation/repair`.

What is missing is not the capability. It is that **nothing ever runs it**:

- no schedule, so a file deleted outside Deluno stays "held" indefinitely;
- the repair is one manual action per issue, so noticing costs a person a trip
  through a screen most people will never open;
- and nothing that *reads* `HasFile` — the library grid, the search path, the
  acquisition-blockers card — consults it or triggers it.

So a title whose file was deleted outside Deluno still reports *Quality met*,
and the blockers card answers "already here at the quality you asked for" and
offers no override, because already-held is deliberately not clearable.

That is still a prerequisite for the table below, but the work is connecting
what exists rather than building a reconciler. **Do not write a second one.**

---

## Decisions settled with James

Taken one at a time, in plain terms. These are recorded as they were made; the
matrix below is being brought into line with them.

**1 — A download that turns out to be junk.** Deluno refuses that exact copy
and **tells you**. The next search takes the next best copy and reports that it
skipped one, and the refused copy appears on a list where it can be seen and
undone. Radarr's mechanism without Radarr's silence, which was the whole
complaint.

**2 — How long a refusal lasts.** Forever, until you clear it. Combined with
decision 1 that is a deliberate pairing: permanence is fine *because* nothing
is hidden. It also makes the management screen a requirement rather than a
nicety — a permanent refusal you cannot see is the failure mode we are
avoiding.

**3 — When the playability check fails.** Tell the two causes apart: the check
read the file and rejected it, versus Deluno could not get at the file at all.
Both are **called out** either way. Neither is allowed to retry indefinitely —
James: *"I dont want it trying again later and continue to try if it comes up
with the same behaviour... I think we need to be harsher"*. One retry, and if
it fails the same way again the copy is refused and added to the list.

**4 — Who decides.** Not us, in code, for ever. James: *"I think it should be
case by case and should be configurable options for the user in some blocklist
/ failure management section of the app"*.

So every row of the matrix below is a **shipped default**, not a law. The
product needs a **Failure and blocklist** section carrying both halves:

- **The list** — every refused copy, with the release, the reason, when it was
  refused, and an unblock button. Decisions 1 and 2 are unsafe without it.
- **The rules** — per kind of failure, what Deluno does: never refuse · one
  retry then refuse · refuse immediately · ask me. Shipped with the defaults
  below, changeable by the person whose library it is.

That is a better answer than any single policy, because the right harshness
depends on the library. Somebody on a fast connection with plenty of disk wants
it strict; somebody on a flaky share does not.

**5 — A download Deluno cannot identify.** Nothing is wrong with the copy; the
matching failed. So nothing is refused. It is raised for the person to identify,
with the file waiting, using the mechanism that already exists for unrecognised
files found on disk — and then imported. No second download for a file you have
already paid for.

**6 — A failure with no recorded reason.** One retry, then refuse and list it.
And each one is recorded as a **gap in Deluno's own reporting**, because "I do
not know why" is Deluno's shortcoming rather than yours, and collecting them is
what makes the unknown bucket shrink instead of being permanent.

**7 — Deluno kept your copy instead of replacing it.** James asked why this
case exists at all: *"there should never be a case of 2 copies of the same
title unless its being upgraded in which case the older one gets deleted and
the new one takes its place"*. He is right, and the answer is that this is the
last check before that swap.

The decision to upgrade is made from what the **indexer claimed** — quality,
size, format in the release name. The only way to learn what the file actually
is, is to download it and probe it. So Deluno probes both files at the moment
of overwrite and refuses if the incoming one is lower resolution, under 92% of
the existing runtime, or missing a video stream. The two copies exist for a few
seconds, deliberately, so the good one is never destroyed on a promise.

Which turns the row on its head. This is not Deluno being fussy about a fine
file — it is Deluno catching a release that **misrepresented itself**. It is
recorded as "kept your copy", with the comparison that decided it, and counts
as no kind of failure. And the release **is** refused, because every copy of it
lies in the same way. The surplus download is removed, being demonstrably worse
than what is already held.

**8 — Un-refusing a copy starts nothing.** James: *"there could be a number of
scenarios when the blocklist is being cleared and if an individual title is
removed the user is going to manually trigger the search anyway"*. Clearing in
bulk is the common case, and firing a search per cleared row would be a storm
nobody asked for. The screen still offers an explicit *search now* for the
single-title case, so the capability is there without being automatic.

**9 — Removing a film clears the download client, by default.** This is the
cause of the original scenario rather than a symptom near it: today removal
leaves the transfer, the infohash and the history behind, so the film cannot be
re-added and, until PR #432, nothing said why. One setting turns it off, for
people who seed and would rather keep the transfer than the ability to re-add.
The people the default would hurt are the ones who know to change it.

**10 — The ownership promise becomes true.** The codebase already says *"Deluno
will not remove an external-client item or its payload without proven
ownership"*, and honours it on exactly one path — the automatic one. The
instruction a person triggers never asks. It will check, and refuse what Deluno
cannot prove it created, with a deliberate override for the times you do want
it to tidy up something it did not make. The override is recorded.

**11 — Deluno checks its files are still there, in the background, and fixes
what it finds.** Today it never looks at a file again after importing it, so a
film you deleted yourself still shows as complete, is never searched for, and
answers "you already have this" when asked why it will not download. Three
wrong answers, all sounding certain. The check exists already and only ever
corrects Deluno's own notes — it never touches a file — so it runs on a
schedule and applies the correction itself, plus a single check on whichever
film is being asked about.

**12 — Deleting a library.** James split a case I had run together:

> "depends where a user is deleting it from? if its from the disk, deluno
> should mark it as missing, if its within deluno then it should ask if you
> want to prevent it being added by lists again and delete from disk right?"

*Deleted inside Deluno* is removing everything in it, so it asks exactly what
removing a film asks — keep or delete the files, prevent lists adding them
back — and clears the download client per decision 9. No new concepts, and no
orphaned records, which is what happens today.

*Gone from disk* is the reconcile at library scale, and it has a trap in it.

**The guard, which is a prerequisite rather than a decision.** Of the three
scans, `FindOrphanFiles` and `FindPartialImportArtifacts` both bail out when the
root is unreachable. The scan that marks files **missing** does not — it streams
every tracked file and asks whether each exists, so an unmounted drive returns
false for all of them and every film in the library becomes a *critical*
missing-file issue. With decision 11 applying corrections automatically, that
would mark a whole library missing and start re-downloading it. The guard is a
straight consistency fix with its two siblings.

**And the health check, which does not exist.** James expected one — *"thats
where other mechanisms come into play with a missing library being flagged as a
system health issue which would stop deluno doing anything at a library
level"*. That is the right design and it is not built: the readiness service
does not mention libraries, and nothing gates library work on whether its root
is reachable. So an unreachable root becomes a health issue naming the library
and the path, and searching, importing and reconciling pause for that library
until it returns. Other libraries carry on.

**13 — Deleting a download client mid-download.** The obvious answer was wrong.
Refusing while downloads are in flight sounds safe until you notice that people
delete a client precisely *because* it is dead — refusing would tell you to
cancel downloads on a machine that is not there, and leave you unable to remove
the entry at all. So Deluno warns, names how many it is about to lose track of,
and on confirming puts those films back to missing and closes the dead records.
Today they stay on "downloading" for ever, pointing at a client nothing can
reach.

**14 — Deleting a processor connection with files in flight.** Worse than the
client case, because an unfinished hand-off is the very thing that stops an
import running: the film downloaded, the file is there, and it waits for ever on
a tool that no longer exists. What makes it easy is that the file is complete
and importable — processing was an enhancement, not a requirement. So Deluno
warns and then releases them to import unprocessed. You can re-process later; a
film stuck for ever on a deleted tool helps nobody.

**15 — Emptying the recycle bin.** The only irreversible act in the product.
Everything else here can be undone or redone; this cannot, and today it is
presented exactly like the rest.

*(Corrected. I claimed `RetentionDays` and `MaxSizeMb` were settings nothing
enforced. Wrong — `SystemTasks.RecycleBinCleanup` runs on the heartbeat and
calls `CleanupAsync`, whose own log line says it "permanently removed N expired
or over-capacity item(s)". I had grepped `Deluno.Jobs` for "recycle" and never
looked in `Deluno.Worker`. **Third time in this document** the capability
already existed and I was about to rebuild it.)*

So the automatic half is done. What is left of this decision is the manual half:
an empty spells out precisely what it is about to take — "47 files, 312 GB, 3 of
them deleted in the last 24 hours" — and never touches anything still inside the
retention window. It is the one irreversible act in the product and it should
say so before it acts.

**16 — What happens to a copy that has been refused.** A hole in everything
above, and James found it: *"if we are refusing something, is it being deleted
and cleaned up so there are no traces of it or did we already figure that
out?"*. We had decided *whether* to refuse without deciding the fate of the
thing refused — so today a refused copy still costs disk, still sits in your
queue, and the client still remembers it.

Keep the distinction straight: the **record** of the refusal is deliberate, and
lives on the list where it can be undone. The **artefacts** are what this
decides — and they are cleaned up completely. The file is deleted, the queue
entry removed, and the client's memory of the release cleared.

That last part is not tidiness. Leave the client remembering a refused release
and the day you un-refuse it the client silently declines to fetch it — the
original trap, reappearing at the far end of the same feature.

---

### A seventeenth reason, found by the guard rather than by me

This table was written from a survey of the import pipeline, and the survey
missed one: `io`, an `IOException` during the move or copy itself. It was
invisible to the search that found the other fifteen because the code is two
letters long.

It surfaced because the table is executable. The test that walks it reads the
pipeline's own call sites and refuses any reason nobody has decided about — and
it failed the first time it ran, on a reason I had not seen. That is the whole
argument for building the table as code rather than prose: a document cannot
notice what its author did not.

`io` is environmental — still downloading, locked by another process, a network
path that went away — so it never refuses a release.

## Two rules that apply to everything above

James, having gone through all sixteen:

> "I think all these things we decided need to have configuration toggles to
> set them on and off in a management / blocklist console."
>
> "everything we have decided as far as a lot of the behaviour can have manual
> overrides like if a file is missing and the schedule hasn't run, a user can
> manually trigger a refresh of the library and it should come up as missing
> and then the user can manually trigger a search."

### Every decision is a default, and every default is a toggle

Nothing above is hard-coded behaviour. Each decision ships as the default and is
changeable in the **Failure and blocklist** console, which carries three things:

- **The list** — every refused copy: the release, the reason, when, and unblock.
- **The rules** — per kind of failure: never refuse · one retry then refuse ·
  refuse immediately · ask me. Plus the clean-up choice from decision 16, the
  clear-the-client-on-removal setting from decision 9, and the ownership
  override from decision 10.
- **The schedules** — how often the file check runs, and the recycle bin's
  retention. Both are settable here now; retention is enforced by the scheduled
  clean-up and on every write.

The right harshness depends on the library. Somebody on a fast line with spare
disk wants it strict; somebody on a flaky share does not.

### Nothing automatic is only automatic

Every scheduled or automatic behaviour has a manual equivalent, so a person
never has to wait for a timer to find out what Deluno thinks.

| Automatic | Manual equivalent | Built |
|---|---|---|
| Background file check (decision 11) | **Check now** on the system task, which also re-tests every library root | ✅ `POST /api/filesystem/file-check` |
| A film found missing is searched for | **Search now** | ✅ already existed |
| Retention clears the recycle bin (decision 15) | **Empty now**, saying exactly what it will take | ✅ `GET /api/recycle-bin/cleanup/preview`, then the existing `POST` |
| A refused copy is cleaned up (decision 16) | **Clean up now** on the blocklist row | ✅ `POST /api/blocked-releases/{id}/cleanup` |
| Removal clears the client (decision 9) | **Forget at the client** on a title, on its own | ✅ the acquisition override |
| An import fails and retries once | **Retry import now** — the **Retry** on its recovery case, on Transfers' Needs attention and in the bell | ✅ `POST /api/{movies,series}/import-recovery/{id}/retry`, which re-runs the import (`RecoveryCaseRetry`) and resolves the case. Until rc.27 this row named `POST /api/v1/download-dispatches/{id}/retry`; that route only queued another search, no screen called it, and it was removed in rc.27 (#1116) |
| A library pauses when its root is unreachable (decision 12) | **Re-check now**, rather than waiting | ✅ the same file check — it reports unreachable roots and touches nothing in them |

### Decision 12, built

A library whose root will not answer is now **paused**, not merely flagged.
Search planning and import automation both filter through one
`ILibraryAvailabilityService`, because two implementations of "is it there"
would eventually disagree and the way you would find out is a library that
imports but is never searched.

- The pause is **said once** when it starts and once when it ends. A pause
  nobody is told about is indistinguishable from Deluno having quietly stopped.
- The answer is **held for a minute**. The worker asks on every tick, and a stat
  call per library per tick against a sleeping NAS is its own problem.
- A path that does not answer **within five seconds is treated as gone**, which
  is what it is. An unreachable share fails when the network stack gives up,
  and blocking the worker for that on every library is worse than the outage.
- A library with **no root configured** is not called an outage. It is an
  unfinished setup, and saying "not reachable" would send somebody to check a
  drive that was never involved.

Jobs already queued when a library goes still run; the gate is on planning, so
what stops is Deluno *starting* new work it cannot finish.

**Every one of these calls the same code the schedule calls.** The library file
check and the refused-download clear-out were both bodies of lambdas inside the
worker's planner, reachable only by a timer; they are now
`ILibraryFileCheckService` and `IRefusedDownloadCleanupService`, and the
scheduled pass is a claim wrapped round the same call the button makes. The
moment there are two implementations the answer starts depending on which one
ran, which is the failure this whole document is about.

The manual clear-out still will not overrule the sharing rule. A button that
ignored the tracker would be a good way to lose an account, so it reports
*"left alone — your sharing rule still needs this copy seeded"* and waits.

The recycle bin needed the same treatment for the opposite reason. Its empty
deleted first and counted afterwards, which is a report rather than a choice —
and permanent deletion is the one place a report after the fact is worth
nothing. It now asks the server what retention would take, shows it, and waits,
with the items that have **not** expired named first and separately: those are
going because the bin is over its size limit, they are the only ones somebody
might have wanted back, and a single total hid them.

Retention is enforced on **every write**, not only by the schedule, so a bin
that is over its size limit drops its oldest item the moment the next one is
recycled. That was true already and written down nowhere; it now has a test,
and it is why a preview taken a second later usually has nothing to report.

Choosing what to take is now one function used by both the showing and the
deleting. It also stopped recounting the bin's size on every step, which meant
a file Deluno could not delete made it delete *another* one to make up the
space — so the dialog could say three and the empty take four. Failing to free
space is a reason to stop and retry, not a reason to take more than was shown.

James's own worked example is the shape of it: the file is gone, the schedule
has not run, so you refresh the library by hand, it comes up missing, and you
search for it by hand. At no point does the answer depend on a timer.

---

## Decision 17 — Seeding, and a correction to three decisions above

I was about to add a seeding warning to every action that removes a download.
James stopped it: *"this should be build into the seeding route or
mechanism... if a user chooses the option to seed deluno should know that"*.

He is right, and better than right — **it already is**. Deluno has a sharing
rule (#288): per-indexer settings for how long a site expects you to keep
sharing, inheriting from a global rule where an indexer says nothing, evaluated
by the worker into holds that carry a deadline in plain words — *"2 days left"* —
and a flag for when the rule can no longer be met and Deluno was told to ask
rather than act.

The import pipeline already defers to it, in as many words:

> "The sharing rule owns this file now. It knows how long the site the release
> came from expects you to keep sharing."
>
> "The download client is still sharing this, so Deluno left its copy alone. It
> will ask the client to remove it once your sharing rule is met."

**So decisions 9, 16 and the force are wrong as written.** Each reaches into
the download client directly and would remove a transfer the sharing rule
currently owns. That is not a missing warning — it is three new paths ignoring
an owner that the oldest path in the system already respects.

The correction: **every action that would remove a transfer goes through the
sharing rule**, exactly as the import pipeline does. A title under an active
hold is not removed; the removal is recorded as pending and happens when the
rule is met, and the existing ask-rather-than-act setting decides whether
Deluno waits or asks. No new mechanism, no new warning, and one place that
knows about seeding rather than four.

This is the second time in this document that the right answer was to connect
something that already existed rather than build one — the file-presence check
was the first. Worth noticing as a pattern.

**18 — The file check covers unmonitored titles too.** Unmonitored means "do
not go looking for it", not "lie to me about it". An unmonitored film, season
or episode can still have a file, and Deluno still claims to hold it.

Nothing can be downloaded as a result — unmonitored titles are never searched
for — so the only thing this changes is whether Deluno is telling the truth.
Episode counts, coverage and disk figures stop counting files that are not
there. And the case that actually bites: **the wrong answer is stored and
waits**. Re-monitor that season a year later and Deluno believes it already
holds those episodes, so it never looks — which would make unmonitoring
something a quiet way of making it permanently wrong.

Films are one file each. TV is three levels — series, season, episode — and all
three are checked.

---

## The vocabulary

Settled. One word, one meaning, and the words appear in the product exactly as
they appear here.

| Word | Means | Does **not** mean |
|---|---|---|
| **Block release** | A durable, visible record that this exact release is not to be used for this title again, matched by torrent hash or NZB item id when known and by release name otherwise — across **every** indexer, not only the one it came from (cleanup sheet, #874/#585: superseding the name-plus-indexer key this section first described). Listed on a screen, clearable by hand, may carry an expiry. | Blocking the title |
| **Skip** | What a search *does* when a candidate matches a blocked release. Always reported: "skipped 2 blocked releases". | Silently dropping it |
| **Unblock** | Removing a block record | Re-downloading |
| **Forget** | Clearing the *download client's* memory so it will accept the release again — infohash on a torrent client, history on a usenet one | Deleting files |
| **Failed** | The attempt did not produce a library file | Deluno chose not to |
| **Rejected** | Deluno deliberately declined | Failed |
| **Recycle** | Moved to the recycle bin, restorable | Deleted |
| **Propose** | Deluno recording that it *would* refuse a release, and waiting. Listed under "waiting for you", answerable both ways. Changes nothing until answered. | Blocking it |

**Nothing is silent.** A block that cannot be seen and cleared is Radarr's
blocklist, which is the thing that started this.

**Propose** is the eighth word, and it was not in the first draft. It arrived
with the rules screen: the four answers are *never · one retry · immediately ·
ask me*, and "ask me" has to leave something behind or it is a slower way of
doing nothing. So it leaves a proposal — recorded with its reason, invisible to
searches, waiting. The rule that makes it honest is that a proposal's
downloaded copy is **not** cleared up: destroying the evidence before the
question is answered would make "allow it" a lie.

## The outcomes

Thirteen, and every scenario answers all thirteen. Most inherit a family
default; only the differences are written per row.

| # | Outcome | Values |
|---|---|---|
| 1 | Library file you already have | untouched · replaced · recycled |
| 2 | Downloaded payload in the client's folder | leave · delete |
| 3 | Client queue entry | leave · remove · remove with data |
| 4 | Client memory of the release | keep · forget |
| 5 | Processor hand-off | none · leave · reset to waiting · mark failed |
| 6 | Dispatch record | status written · archived · untouched |
| 7 | Blocked release | no · yes, expiring · yes, permanent |
| 8 | Import exclusion | none · add · remove |
| 9 | Wanted state | untouched · missing · covered · gone |
| 10 | Job queue | nothing · cancel pending · enqueue search |
| 11 | Health strike against the client | counts · does not count |
| 12 | Import-recovery case | none · raised for a decision |
| 13 | What you are told | nothing · activity entry · needs-attention · notification |

Outcome 11 matters more than it looks: a strike is what eventually triggers
automatic remediation, so counting a *release's* fault against the *client* is
how a healthy client gets blamed for bad files.

---

## Family A — the import failed

**Family defaults**, true for every row unless it says otherwise: library file
untouched (1); payload left (2); queue entry left (3); memory kept (4);
dispatch written as failed (6); no exclusion (8); wanted state missing (9);
search after the normal retry delay (10); an activity entry (13).

| Reason | Block? | Strike? | Recovery case | Differs otherwise |
|---|---|---|---|---|
| `noVideoStream` | **permanent** | no | no | Payload **deleted** — it is not a film |
| `likelySample` | **permanent** | no | no | Payload **deleted** |
| `unsupportedFile` | **permanent** | no | no | — |
| `mediaProbeRejected` | **permanent** | no | no | ffprobe read it and refused it |
| `mediaProbeUnreadable` | **one retry** | no | no | Deluno could not get at the file; says nothing about the release |
| `unmatched` | **contentious** | no | **raised** | Deluno needs help identifying it |
| `importFailed` | **contentious** | no | **raised** | — |
| `replacementRejected` | **contentious** | no | no | Not a failure at all — see below |
| `replacementOwnershipMismatch` | no | no | **raised** | The guard working; a person must look |
| `missingLibraryRoot` | no | no | no | **No search** — every title will fail until fixed |
| `permission` | no | no | no | **No search** — same |
| `hardlinkUnavailable` | no | no | no | **No search**; needs-attention, it is configuration |
| `hardlinkFailed` | no | no | no | **No search**; needs-attention |
| `missingSource` | no | **counts** | no | The client said done and the file is gone — that is the client's fault |
| `samePath` | no | no | no | Nothing to do; activity only |
| `conflict` | no | no | **raised** | Something is already there; a person decides |
| `unsafeSourcePath` | no | no | no | The source path has `..` in it (#1306). Never retried, never refused; shown in plain words; the file is left alone |
| `io` | no | no | no | The move itself threw — still downloading, locked, or a network path that went away |

Three of those deserve their reason stated.

**Payload deleted for `noVideoStream` and `likelySample`.** These are the only
two rows where Deluno knows the file is not what was wanted. Leaving it costs
disk for something nobody will ever import. Every other failure might be
environmental, and deleting on a guess is unrecoverable.

**`missingSource` strikes the client.** The client reported the download
complete and the file was not there. That is the one import failure that is
genuinely the client's fault, and it is exactly what the three-strike policy
exists to catch.

**Configuration failures stop searching.** `missingLibraryRoot`, `permission`
and the hardlink pair will fail identically for every title. Continuing to
search is how you get a hundred failed imports and one root cause.

---

## Family B — the grab failed

**Defaults:** no library file involved; no payload; no hand-off; dispatch
written as failed; wanted state missing; activity entry.

| Scenario | Block? | Strike? | Search | Told |
|---|---|---|---|---|
| Client refused the release | no | counts | after delay | activity |
| Client unreachable | no | counts | after delay | needs-attention if repeated |
| Client address or API key missing | no | no | **none** | needs-attention — configuration |
| Action unsupported by this client | no | no | **none** | needs-attention — configuration |
| **Accepted, but nothing was added** | no | no | **none** | **needs-attention**, naming the release the client already holds, and offering the force |
| Category missing at the client | no | no | **none** | needs-attention — configuration |
| Grab timed out | no | counts | after delay | activity |

The fifth row is the scenario that started all of this, and it did not exist
before PR #432 — qBittorrent answered `Ok.` and Deluno recorded a successful
grab. It is deliberately **not** a blocked release: the release is fine, the
client simply already has it, and the answer is to forget it there, not to
refuse it for ever.

---

## Family C — it timed out

| Scenario | Client entry | Block? | Strike? | Told |
|---|---|---|---|---|
| Grab timeout — the client never confirmed | leave | no | counts | activity |
| Detection timeout — grabbed, never appeared in the queue | leave | no | counts | needs-attention |
| Import timeout — downloaded, never imported | leave | no | no | needs-attention, recovery case raised |

None of these blocks a release. A timeout says something about the moment, not
the file.

---

## Family D — the processor

| Scenario | Hand-off | Client | Told |
|---|---|---|---|
| Hand-off submitted, no answer | leave open | leave | needs-attention after the library's timeout |
| Processor reported failure | **mark failed** | leave | needs-attention, recovery case raised |
| Processor unreachable | leave open | leave | needs-attention — configuration |
| Processor connection deleted while hand-offs are open | **contentious** | leave | see Family G |

---

## Family E — nothing failed, it is just not downloading

These are the acquisition blockers PR #432 added. No outcomes change; they are
statements, not actions.

| Scenario | Clearable | Offered |
|---|---|---|
| Already held at the target quality | no | lower the cutoff, or delete the file |
| A download is with a client | yes | remove it and search again |
| A processor is holding the file | yes | reset the hand-off |
| An import exclusion covers it | yes | remove the exclusion |
| The next scheduled search was skipped | yes | put it back in |
| Searching is deferred after an earlier attempt | yes | clear the delay |
| Not obtainable yet | no | change the availability rule |
| **Previously downloaded and no longer held** | yes | forget it at the client and search again |
| **Every candidate is a blocked release** | yes | unblock one, or all |

The last two do not exist yet. The first is the scenario at the top of this
document; the second is the failure mode that the block-release mechanism
creates and must therefore answer for.

---

## Family F — you did something

| Scenario | File | Client entry | Memory | Hand-off | Exclusion | State | Search |
|---|---|---|---|---|---|---|---|
| Remove, keep files | untouched | remove | **forget** | reset | none | gone | none |
| Remove, delete files | **recycled** | remove with data | **forget** | reset | none | gone | none |
| Remove, don't add it back | per above | remove | **forget** | reset | **add** | gone | none |
| Delete the file, keep the title | **recycled** | remove with data | **forget** | reset | none | missing | **yes** |
| Force a re-download | untouched | remove with data | **forget** | reset | **remove** | missing | **yes** |
| Unblock a release | untouched | leave | leave | leave | none | untouched | **contentious** |
| Restore from the recycle bin | **restored** | leave | leave | leave | none | **covered** | none |
| Archive a dispatch | untouched | leave | leave | leave | none | untouched | none |
| Queue remove — "It's bad" (#924) | **deleted** | remove with data | leave | leave | none | missing | **yes**, and blocklisted everywhere |
| Queue remove — "I don't want it" (#924) | **deleted** | remove with data | leave | leave | none | missing (should be **unmonitored** per the cleanup sheet; not yet built — see the code-vs-decision list) | none |
| Queue remove — "Just stop tracking it" (#924) | untouched | leave | leave | leave | none | untouched | none |

The three rows above replaced the single "queue action (delete-with-data)" row
this table used to carry, once cleanup sheet #924 turned the queue's bare
confirm into a three-way "why" dialog and retired the "Allow removing client
queue entries" setting that used to gate it. The raw
`POST /api/download-clients/{clientId}/queue/actions` endpoint this section's
"unguarded removal API" finding described still exists unchanged alongside the
new reasoned route.

Two need stating.

**Restoring from the recycle bin has to set wanted state back to covered.**
Otherwise Deluno restores your file and immediately searches for another copy.
This is not currently done, and nothing in the product notices.

**Archiving a dispatch must keep the fetch fact.** Archive the operational
detail; keep "Deluno fetched this title, at this time, through this client",
because that is what the previously-downloaded blocker reads.

---

## Family G — you deleted something else

The cascade cases, all newly found, none currently handled.

| Scenario | What survives today | Proposed |
|---|---|---|
| Delete a library holding titles | Wanted rows, tracked files, dispatches, hand-offs — in another database, so no foreign key could catch it | **Refuse** while titles reference it, and say how many. Offer to move them |
| Delete a download client with live dispatches | Dispatches naming a client id that resolves to nothing, so nothing can ever clear them | **Refuse** while dispatches are unresolved, or mark them orphaned and say so |
| Delete a processor connection with open hand-offs | Hand-offs naming a processor that is gone — and an open hand-off blocks an import | **Release** the hand-offs to "no processor" so imports proceed |
| Recycle-bin cleanup | Files permanently gone | The **only irreversible delete in the product**; it should say so, and say what it is about to take |

---

## Family H — something changed underneath you

| Scenario | Detected by | Proposed |
|---|---|---|
| A library file was deleted outside Deluno | `FilesystemReconciliationService`, which nothing runs | Reconcile marks it missing, wanted state goes missing, search runs |
| A library file was **moved** outside Deluno | Nothing — a moved file reads as deleted | Re-link rather than re-download, if file identity is recorded. See "better ways" |
| The client reports a failure by webhook | `client-reported-failure` | Family A defaults, no block |
| Health remediation acted | Existing three-strike policy | Client entry removed, **memory kept** — the release genuinely failed |


---

## What still needs deciding

Nothing. All four of the questions this section originally held were settled
with James, along with fourteen more that came out of working through them:

| Was open | Settled by |
|---|---|
| Does removing a title forget it at the client? | 9 — yes, by default, with a setting |
| Should a force warn that it stops a seed? | 17 — no warning; it goes through the sharing rule that already owns the file |
| Should the reconcile run scheduled, on open, or on demand? | 11 — in the background, applying its own corrections |
| Does an unmonitored title get reconciled? | 18 — yes, at all three TV levels |

## Finding the gaps, rather than noticing them

A table written by hand only covers the cases whoever wrote it thought of. The
scenario at the top of this page is exactly that failure: nobody sat down and
decided that removing a title should leave the client holding it — the case was
never enumerated at all.

So the table is not the source. **The axes are**, and the table is their
cross-product with a decision in each cell:

| Axis | Values |
|---|---|
| Trigger | remove · remove+delete · remove+exclude · delete file only · force · remediation · reconcile |
| Library file | present · absent · never existed |
| Client transfer | none · downloading · stalled · completed · seeding |
| Client memory | none · history entry · still holds infohash |
| Hand-off | none · open · finished · failed |
| Monitored | yes · no |

Not every cell is reachable, and most collapse. The point is that the reachable
ones are **enumerable**, so a case that has never been decided is discoverable
by machine rather than by somebody remembering it at 1am. The table-driven test
below asserts that every reachable combination matches exactly one row: a cell
matching none is a gap, and a cell matching two is a contradiction. Both fail
the build.

That is the difference between a design that decays and one that keeps working:
when someone adds a seventh trigger or a client with a new state, the missing
cells announce themselves.

### The register of what we do not know

| # | Unknown | How we would find out |
|---|---|---|
| 1 | Does removing a title while it is still downloading leave an orphan in the client for ever? | Drive it on the rig: add, grab, remove mid-download, look at qBittorrent |
| 2 | Does SABnzbd's duplicate check key on the nzo id we store, or on the release name? | Its history delete takes the id; the *duplicate* check may not. Needs a SABnzbd instance — the lab has none |
| 3 | Does deleting the client's payload ever take the library file with it? | Only if they are one hardlink and the client deletes by inode. `SharingFootprint` knows whether they are one copy; the behaviour needs proving on the rig |
| 4 | What does the processor do with a hand-off it has already answered? | Now answerable — the idempotency key carries the row revision, so it should re-process. Unproven against Weir |
| 5 | Is a completed dispatch enough to conclude "the client still refuses this"? | No — the client may have been cleared by hand. Which is why the blocker must say "may still hold" rather than "holds" |

Number 5 is a design constraint rather than a question: **Deluno cannot see a
download client's memory without asking it, and it may not be reachable.** So
the "previously downloaded" blocker states what Deluno knows — that it fetched
this once and no longer holds the file — and offers the override, rather than
claiming a fact about the client it has not verified.

---

## Better ways to deliver the outcome

Each of these questions the mechanism rather than the table. Two are worth
doing; one is worth deciding against on the record.

### A. Decide from Deluno's own record, not the client's refusal — **recommended**

The blockers reader currently asks the world what is in the way. But Deluno
already stores every dispatch it ever made, so it can answer "I fetched this
before and no longer have the file" from its own tables — with no client, no
network, and no chance of a wrong answer because a client is offline.

This is strictly better and it is what makes row 2 of the work order possible.
The client's memory still has to be *cleared* to re-download; it just should
not be what Deluno *consults* to explain.

### B. Remember the release that failed, not the title — **recommended**

Radarr blocklists a release. Deluno's exclusion is title-level, which is a
blunter instrument than the problem: a failed import means *this file was bad*,
not *this film is bad*.

If Deluno recorded failure per release — name, indexer, infohash — the next
search could skip that release and take the next candidate **without anyone
being told anything**. That turns "why will this not download" into a question
that never gets asked, which beats answering it well. The force button then
exists only for the cases a person genuinely has to overrule.

This is the single biggest improvement available here, and it is larger than
the rest of this document.

### C. File identity instead of file path — **worth doing, later**

`HasFile` plus a path cannot tell *deleted* from *moved* from *renamed*. If
import recorded a durable identity — size, and the hardlink or inode where the
platform offers one — a reconcile could re-link a moved file instead of
declaring it missing and downloading it again.

The better outcome is not "we noticed it went and fetched another one". It is
"it moved, and Deluno followed it". That saves the download entirely, and it is
the difference between a tool that copes and one that pays attention.

### D. Make removal reversible instead of careful — **decided against**

Removal could be a transaction undoable for some days: catalogue row
soft-deleted, files already in the recycle bin, client state recorded well
enough to re-establish. Most of the risk in the table above would evaporate,
because mistakes would be recoverable rather than prevented.

Against it: the recycle bin already covers the irreversible half — the files —
and it has a restore. What remains recoverable-in-principle is a catalogue row,
which is cheap to recreate by adding the title again. Adding a second
undo system to protect the cheap half would be machinery earning its keep only
on the day somebody makes a mistake, and it would sit between every removal and
its outcome for ever. **Not doing this** is the decision; the recycle bin is
the recovery story.

---

## How this gets enforced

A document that five code paths can ignore is the problem, not the fix.

- **One policy object** — the table above expressed once, in code, as the only
  place the seven outcomes are decided.
- **Every path routed through it**: both removal endpoints, health remediation,
  `ReclaimCompletedAsync`, and force-redownload. The verbs come from
  `DownloadClientActions`, not from string literals.
- **A table-driven test** that walks every row and asserts all seven outcomes,
  in the manner of `gold-stays-gold` and `light-theme-brightness` — reading the
  real thing rather than a copy of it, so a row cannot drift without failing.
- **The ownership check applies to all of them**, including the force. It is
  currently reachable only from health remediation, which is the path a person
  did not ask for.

## Order of work

1. **Connect the reconcile that exists.** Until something asks, every answer
   the blockers card gives about a deleted title is confidently wrong. The
   cheapest honest version is a single presence check for the one title being
   asked about, routed through `MarkTrackedFileMissingAsync` so the state is
   corrected rather than only the display — plus a schedule for the sweep.
2. **The "previously downloaded" blocker.** A completed dispatch is invisible to
   `AcquisitionBlockerSources.FindAsync` today, which is why the exact scenario
   at the top of this page produces no card and no button.
3. **The policy and the routing**, absorbing the seeding warning and the shared
   ownership check.

---

## What was built

All of it, across six pull requests.

| | Landed as |
|---|---|
| The reconcile, connected, with a schedule | #435 |
| The "previously downloaded" blocker | #437 |
| A download client can be told to forget, not just delete | #436 |
| The failure table, the refusals, the clear-out, and the blocklist screen | #438 |
| The rules — every decision a default, "ask me" included | #439 |
| The manual triggers, and an empty that says what it takes | #440 |
| The schedules — how often Deluno checks | #441 |
| A library that is gone is paused, not worked on | #442 |

What is deliberately **not** built is the register of five unknowns above.
Every one of them needs the rig or a SABnzbd instance to answer, and guessing
at them in code would be worse than leaving them written down.

Two things are worth saying about how this went, because both were found by
building rather than by reading:

- **The check before the build.** Three times a capability was described as
  missing when it existed under a name nobody had searched for — the
  file-presence reconcile, the sharing rule, and recycle-bin retention. In this
  codebase the answer is usually already there.
- **The guards earned their keep.** The failure table's own guard found a
  seventeenth reason nobody had decided about, because the code spells it `io`.
  The one-spot guard caught a cadence being chosen at a call site. Playwright
  caught two buttons sharing a name. None of those would have been noticed by
  reading.

---

## Changes in 1.0.0-rc.27 (3 October 2026)

The clean-up of routes nothing called (#1116) touched four things this design relies on. Each one was checked against
this document, against Weir and the tray, and against the rig's own logs before it was kept removed. Weir calls only
`/api/integrations/processors/events` on Deluno, and that route is unchanged.

- **"Retry import now"** is the recovery case's **Retry**, `POST /api/{movies,series}/import-recovery/{id}/retry`. It
  re-runs the import and resolves the case, and it is offered on Transfers' Needs attention and in the bell. The
  dispatch retry route this document used to name only queued a search, nothing called it, and it is gone.
- **A recovery case's decisions** are unchanged: Retry, Search again, Import the original, Stop wanting it, Dismiss and
  delete. The `/resolve` routes that were removed were never called; Dismiss settles a case by hand.
- **The memory that keeps Deluno from reporting the same stuck download every hour** used to be "dispatch alerts". It
  now lives on each download's own timeline as problem rows (`IDispatchProblemLog`). The behaviour is the same: a
  problem is recorded once, stays recorded after its issue is dismissed, and is released, settling its issue, when the
  download moves on. The old `dispatch_alerts` table is left in place and read once on start, so an upgraded install
  does not re-report what it already held.
- **Family H's reconcile** still runs through `FilesystemReconciliationService`, on the schedule and through **Check
  now**. Only its unused read-out route, `GET /api/filesystem/reconciliation`, is gone.
- **Activity** became one list (#1114). Its **Needs a decision** rows show each case's evidence. The buttons that decide
  a case are on Transfers and in the bell.
