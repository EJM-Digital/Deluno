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
| #357 Metadata recovery | 7 | **yes** | **7/7 — closed 3 Sep.** Line 3 closed by [#376](https://github.com/jampat000/Deluno/pull/376) (real resilience policy, three paced attempts, `Unavailable` not `Missing`) and line 1 by the same PR's populated movie/series fixtures. Full evidence in the [audit comment](https://github.com/jampat000/Deluno/issues/357). |
| #338 Telemetry truthful/attributable | 2 | **yes** | **2/2 — closed 3 Sep.** The last swallowed external-service failure was the metadata path, fixed in [#377](https://github.com/jampat000/Deluno/pull/377): a bare 503 with no body, and a failure that could not name its own service. Front end re-swept; every remaining fixed sentence is a Deluno-local operation. Full evidence in the [audit comment](https://github.com/jampat000/Deluno/issues/338). |
| #351 Scoring migration | 8 | **yes** | **8/8 — closed 3 Sep.** Lines 3, 6, 7 and 8 closed by [#379](https://github.com/jampat000/Deluno/pull/379). Line 3's golden fixture found a shipped defect: a quality family built only from the allowed list could not place a held file better than it, so a profile allowing up to Bluray 1080p wanted to downgrade a Bluray 2160p file. Lines 1, 2, 4 and 5 were already met — 5 is per-title, through the stale-baseline hold. |
| #350 TRaSH translation | 9 | **yes** | **5/5 — closed 3 Sep.** The script's 9 bullets are 5 `Done when` lines plus the 4-item `References` list. Line 4 closed by [#380](https://github.com/jampat000/Deluno/pull/380): the plan diff was computed and reported as a count, and a retained version had no way back to it. Line 5 came free with [#379](https://github.com/jampat000/Deluno/pull/379). |
| #352 Release preference proof | 7 | **yes** | **4/7 — stays open.** Lines 3 and 4 closed by [#381](https://github.com/jampat000/Deluno/pull/381); writing line 4 found `ReevaluateLibraryWantedState` duplicated and drifted, and took a 20,000-title plan change from 9,390 ms to 1,770 ms. Unmet: **5** needs the seven-scenario real-software campaign; **6** needs the plain-language check with people; **7** is blocked by #349 being parked — an owner decision, not work. See the [audit comment](https://github.com/jampat000/Deluno/issues/352#issuecomment-5522478163). |
| #353 Release preference UX | 7 | no | |
| #343 Media Plans | 2 | no | Own lifecycle track. |
| ~~#354 Normative spec~~ | — | — | **Already closed** on 2 Sep. Left here so a later reader does not go looking for it. |
| #321 Subber delta | 15 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/321#issuecomment-5532406216). Eleven met; four recorded not planned with a reason each (Whisper, translation, post-process command, anti-captcha) plus the audio column, which needs a migration on both catalogues. |
| #301 Subber outcome | 6 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/301#issuecomment-5532427875). 6/6. Weir retired its own copy in `0010_drop_subber_tables` on 28 Aug; children #321/#329/#330 all closed. |
| #329 Whisper | 2 | **yes** | **Closed** — recorded not planned as one of #301's children: build on the lab VM and **measure** before recommending, and "not worth shipping" was the answer. See [#301's audit](https://github.com/jampat000/Deluno/issues/301#issuecomment-5532427875). |
| #337 Docker | 5 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/337#issuecomment-5532478355). 5/5 on clean-runner CI evidence plus the verified v1.0.0-rc.2 digest. Line 5 found the README pointing at an unverified `latest` (#391). |
| #339 Request portal | 2 | no | Needs email; Cloudflare available. Park if it becomes a project. |
| #341 Mobile web | 2 | no | |
| #340 Native mobile | 2 | no | iOS needs macOS + Xcode, which do not exist here. |
| #269 README / screenshots | 2 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/269#issuecomment-5532594658). 2/2. Screenshots retaken against current `main` (#392); install guidance had been pointing at an unverified image (#391). |
| #386 Seven-step Quality & Release | 5 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/386#issuecomment-5534228378). 5/5. Found the Remux acceptance defect on the deployed build. |
| #394 Nothing global in Quality & Release | 6 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/394#issuecomment-5534224388). 6/6. Size, when-to-stop, weighting and acquisition now per profile. |
| #322 Stack epic | 0 | no | Closes when its children do. |
| #349 Playback goals | 6 | — | **PARKED** by the owner. Do not work it. Do not close it. |
| #78 GA readiness epic | 12 | no | Last. Parent of #81/#82 — closes after them, never before. |
| #81 Clean-Windows matrix | 12 | **yes** | Closed 4 Sep — [audit](https://github.com/jampat000/Deluno/issues/81#issuecomment-5535058449). 10/12; lines 1 and 6 are the unsigned decision. Found four defects that made the installer unusable (#400–#403). |
| #82 14-day soak | 9 | — | **LAST**, runs in parallel with #81. Fourteen wall-clock days; cannot be compressed. |

## Closing an issue

Post a comment carrying: the linked pull requests, **every** acceptance line
with its evidence, and any limitation you are knowingly accepting. Then close
it and set the verdict here.

Keep the evidence bar — deployed-lab proof for runtime behaviour, real Chrome
for user-facing changes. The failure this file guards against is inventing
criteria, not demanding evidence.
