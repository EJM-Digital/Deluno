# Backlog sweep — audit state

**This file is the anti-drift mechanism. Update it as you go.**

A long session forgets. It reads an issue for context, remembers the shape of
the work, and loses the finish line — so each slice ends with a "remaining
scope" paragraph invented on the spot, and nobody ever puts the issue's own
list next to the evidence. That is how a sweep merges ten pull requests and
closes one issue.

Audit state therefore lives on disk, not in a conversation. If your context has
been summarised and you cannot remember whether you audited something, the
answer is in this table, not in your memory.

## The rule

Run `./scripts/issue-audit.ps1 <number>` **before** touching an issue. It prints
the acceptance criteria as an unticked checklist. Answer every line MET (with
the evidence) or NOT MET (with what would satisfy it), then set `Audited` below.

**The list is the whole contract.** You may not add criteria the issue does not
state. If something extra seems necessary, that is a conversation with the
owner, not a private raising of the bar — inventing extra criteria and then
failing the issue against your own invention is the specific mistake this file
exists to stop.

**The burden is on leaving an issue open**, not on closing it. To leave one open
you must name the unmet line and the evidence that would close it. *Broad
issue*, *epic*, *only a slice* and *more work exists* are not reasons.

**Tripwire:** merging a second pull request against an issue whose `Audited`
column is still `no` means you are working without a finish line. Stop and
audit.

## State

`Criteria` is what `issue-audit.ps1` extracts. `0` means the issue has no
acceptance section — read it whole and write the criteria down yourself before
starting.

| Issue | Criteria | Audited | Verdict / unmet line |
|---|---|---|---|
| #357 Metadata recovery | 7 | no | Believed 6/7. Gap is line 3 — a test proving a 503 is **not** read as deletion. Code already treats only 404 as `Missing`; nothing tests it. |
| #338 Telemetry truthful/attributable | 2 | no | Four slices merged 2–3 Sep. "History states its source" already done. Audit before assuming anything remains. |
| #351 Scoring migration | 8 | no | Next in the release-preference chain. |
| #350 TRaSH translation | 9 | no | |
| #352 Release preference proof | 7 | no | |
| #353 Release preference UX | 7 | no | |
| #343 Media Plans | 2 | no | Own lifecycle track. |
| #354 Normative spec | — | no | Closes last of the chain; its done-when is downstream of #347–#353. Verify rather than assume. |
| #321 Subber delta | 0 | **partial** | Audited 2 Sep — see the [audit comment](https://github.com/jampat000/Deluno/issues/321#issuecomment-5508112058). Items 1, 2 and `.sdh.srt` naming already built. Unbuilt: Language Equals, must/must-not-contain profiles, custom post-process command, anti-captcha, audio column. |
| #301 Subber outcome | 0 | no | Parent of #321/#329. |
| #329 Whisper | 2 | no | Build on the lab VM and **measure** before recommending. "Not worth shipping" is a welcome answer. |
| #337 Docker | 5 | no | Needs a container runtime on the VM. Owner gave free rein on Hyper-V. |
| #339 Request portal | 2 | no | Needs email; Cloudflare available. Park if it becomes a project. |
| #341 Mobile web | 2 | no | |
| #340 Native mobile | 2 | no | iOS needs macOS + Xcode, which do not exist here. |
| #269 README / screenshots | 2 | no | Last. |
| #322 Stack epic | 0 | no | Closes when its children do. |
| #349 Playback goals | 6 | — | **PARKED** by the owner. Do not work it. Do not close it. |
| #78 GA readiness epic | 12 | no | Last. Parent of #81/#82 — closes after them, never before. |
| #81 Clean-Windows matrix | 12 | — | **LAST.** Stop when you reach it. |
| #82 14-day soak | 9 | — | **LAST**, runs in parallel with #81. Fourteen wall-clock days; cannot be compressed. |

## Closing an issue

Post a comment carrying: the linked pull requests, **every** acceptance line
with its evidence, and any limitation you are knowingly accepting. Then close
it and set the verdict here.

Keep the evidence bar — deployed-lab proof for runtime behaviour, real Chrome
for user-facing changes. The failure this file guards against is inventing
criteria, not demanding evidence.
