# The Deluno-Weir contract

Every call Deluno makes to Weir, every report Weir makes to Deluno, each reply, what Deluno does with it, and the
recording and test that prove the row (#1239). Weir's own tests replay Deluno's side; Deluno's tests replay Weir's
recorded replies. A change to either side's shape fails a test before it ships.

- **Weir's replies** are recorded from the real Weir (`tests/fixtures/recorded/weir/<version>/<state>.json`, newest
  1.0.0-rc.13; how they are recorded: `tests/fixtures/recorded/README.md`). No test writes a Weir reply itself.
- **The tests** are in `tests/Deluno.Integrations.Tests/Processors/WeirContract/`:
  - `WeirContractClientTests`: the real `ProcessorConnectionService` reads each recording, and the request Deluno
    sends is held against the recorded request.
  - `WeirContractBehaviourTests`: the real outcome delivery, hand-off cancellation and hand-off watcher act on what the
    client read; each test names what is stored, what Activity says and whether Deluno asks again.
  - `EveryWeirRecordingIsUsedByAContractTestTests`: the guard. It fails on a recording no contract test names, and on
    a recording with no row in this document.
- **Deluno's reading is by code, never by Weir's wording**: a 409 to an outcome is read by its `code` field. Weir's
  words are kept and shown, not parsed.
- **Authentication**: every call carries the connection's secret in `X-Webhook-Secret` (the header name is the
  connection's). A hand-off also carries `Idempotency-Key: <handoffId>:<row revision>`.
- **Outage** (Weir switched off, connection refused): no Weir reply exists; the tests make the connection fail
  (`Down`) and assert the same rows below as "unreachable". See `tests/fixtures/recorded/MISSING.md`.

Words in the tables: *Test* names a method in `WeirContractClientTests` (client) or `WeirContractBehaviourTests`
(behaviour). Request rows are held by the `The_hand_off_Deluno_sends…` and `The_outcome_Deluno_sends…` tests.

## 1. Capabilities: `GET /api/v1/intake/capabilities`

Asked before a status poll, a cancel, an outcome and a library-folder read, and for the product, version and machine
name. Deluno never asks Weir a question the capabilities did not offer.

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `{ capabilities[], machine_name, product, version }` listing status, cancel, outcome, outcome codes, library folders | `capabilities` | can ask status, can cancel, can be told the outcome, has library folders; product `Weir`; version; machine | asks and tells accordingly; stores the version | `Capabilities_with_the_secret_say_what_Weir_can_be_asked_and_told`, `Capabilities_name_Weir_its_version_and_its_machine` |
| `401` `{ detail }` (no secret sent) | `capabilities-no-secret` | Forbidden, can do nothing | status: hand-offs are not judged and the connection shows "refused the webhook secret"; outcome: stays owed | `A_refused_secret_on_the_capabilities_call_is_said_as_that_and_nothing_is_assumed` |
| `401` `{ detail }` (wrong secret) | `capabilities-wrong-secret` | same | status: Activity `processing.status-forbidden`, connection "degraded", hand-off kept; outcome: `processing.outcome-delayed`, owed, no outcome post sent | `A_refused_secret_on_the_capabilities_call_is_said_as_that_and_nothing_is_assumed`, `A_hand_off_is_not_given_up_while_Weir_refuses_the_secret_and_the_connection_says_so`, `An_outcome_sent_with_a_secret_Weir_refuses_stays_owed_and_names_the_fix` |
| connection refused | none (simulated) | Unreachable | hand-off kept, silence noted on the hand-off; outcome owed | `A_hand_off_is_not_given_up_while_Weir_is_switched_off` |

## 2. Library folders: `GET /api/v1/intake/library-folders`

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `{ libraries[]: id, name, media_type, watched_folder, work_folder, output_folder, manager_library_key, folders_from_manager, minimum_file_size_bytes }` | `library-folders` | one entry per library: key, watched folder, output folder, folders-from-manager, minimum size | checks each library's folders against Weir's; a hand-off leaves out files under the minimum size | `Library_folders_list_each_library_with_its_folders_and_minimum_size` |

## 3. Hand-off: `POST /api/v1/intake/webhook/deluno`

Request body (pinned member by member, in order, against the recorded request): `eventType`, `handoffId`, `libraryId`,
`mediaType`, `sourcePath`, `releaseName`, `queueItemId`, `callbackPath`, then when Deluno knows them `title`, `year`,
`tmdbId`, `tvdbId`, `imdbId`, `season`, `episode`, `posterUrl`, then `sourceFiles` when the hand-off names files.

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `{ status: "ok", source, event, media_scope, enqueued }` for a file | `handoff-accepted` | sent | hand-off recorded as submitted | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for a file not in the watched folder yet | `handoff-accepted-missing-file` | sent | same; Weir's status then says what it found | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for a release folder | `handoff-release-folder-accepted` | sent | same | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for a release folder with `sourceFiles` | `handoff-source-files-accepted` | sent | same | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for the same source sent again | `handoff-resend-accepted` | sent | same; the older send's own report is settled apart (section 7) | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` `{ status: "ignored", source }` (no source path in the body) | `handoff-ignored-no-source` | any 2xx is "sent" | recorded as submitted. Deluno never sends a body without a source path, which the request test pins | `A_2xx_that_says_ignored_is_read_as_sent_because_Deluno_always_names_a_source_path` |
| `400` `{ detail }` (no workflow watches the folder) | `handoff-refused-not-watched` | refused with Weir's `detail` | hand-off marked failed with "The processor refused the hand-off with HTTP 400: <Weir's words>" | `A_folder_no_workflow_watches_is_refused_with_Weirs_own_words` |
| `401` `{ detail }` (wrong secret) | `handoff-refused-wrong-secret` | refused with Weir's `detail` | same, HTTP 401 | `A_wrong_secret_on_a_hand_off_is_refused_with_Weirs_own_words` |
| connection refused | none (simulated) | could not reach | "Could not submit the hand-off. Check the processor connection"; sent again by the planner | `A_hand_off_with_Weir_switched_off_is_not_sent_and_says_to_check_the_connection` |

Request tests, for the recorded bodies of `handoff-accepted`, `handoff-accepted-missing-file`,
`handoff-release-folder-accepted`, `handoff-source-files-accepted`, `handoff-resend-accepted`,
`handoff-refused-not-watched`, `handoff-refused-wrong-secret` and `outcome-superseded`:
`The_hand_off_Deluno_sends_is_the_hand_off_Weir_was_recorded_receiving`;
headers: `A_hand_off_carries_the_secret_and_a_key_that_changes_when_the_row_is_put_back_to_waiting`.

## 4. Status: `GET /api/v1/intake/handoffs/deluno/{handoffId}`

Asked on every planning pass for each hand-off still with Weir. Body: `{ handoffId, state, queuePosition,
scheduledFor, lastChangedUtc, outputPath, outputFiles, message }`. All reads are held by
`A_status_is_read_as_Weir_said_it`.

| Weir answers | Recording | Deluno reads | Deluno does (stored, shown, Activity) | Test |
|---|---|---|---|---|
| `200` `queued` with `queuePosition` (12, 6) | `status-queued` | state, place in line | stored on the hand-off and shown as the place in line; waits; no Activity line | `A_hand_off_Weir_is_still_working_on_stays_open_and_the_screen_shows_what_Weir_said`, `A_queued_hand_off_says_its_place_in_line_and_a_completed_one_with_no_output_path_says_none` |
| `200` `working`, message "Weir has claimed this file and is checking it now." | `status-working`, `missing-file-status-working`, `release-folder-status-working`, `source-files-status-working` | state, message | stored and shown; waits; no stall until the library's limit | same |
| `200` `completed` with `outputPath` and `outputFiles` | `status-completed`, `completed-with-output-path`, `release-folder-status-completed`, `source-files-status-completed` | state, output path | stored and shown; waits for Weir's report or the output folder to bring the file in; never ended on the status alone | `A_hand_off_Weir_says_is_completed_stays_open_for_its_own_report_or_the_output_folder` |
| `200` `completed`, `outputPath` null (Weir had cleaned the file for an older send) | `completed-without-output-path`, `resend-status-completed` | state, no output path | stored; for a hand-off that replaced an earlier send Deluno matches the cleaned file it finds at once and queues the import (`processing.completed.import-queued`); otherwise waits | `A_resend_Weir_answers_completed_without_an_output_path_is_matched_to_the_cleaned_file_at_once`, `A_completed_answer_with_no_output_path_for_a_hand_off_that_replaced_nothing_waits_for_Weirs_report` |
| `200` `failed`, message (file under the minimum size) | `status-failed` | state, message | hand-off ends failed with "Weir reports failed for <release>: <Weir's message>"; Activity `processing.failed`; a case to clear is raised (`processor-failed`) | `A_hand_off_Weir_says_failed_ends_failed_in_Weirs_words_and_leaves_a_case_to_clear` |
| `200` `cancelled` | `status-cancelled`, `handoff-cancelled` (second call) | state | hand-off ends stopped, "Weir stopped work on <release>."; Activity `processing.cancelled` | `A_hand_off_Weir_says_it_cancelled_ends_stopped` |
| `404` `{ detail: "Weir has never received this hand-off." }` | `no-record` | not found | inside 15 minutes of the last change: nothing (not believed). After: hand-off ends lost, Activity `processing.lost`, case `processor-lost` raised | `A_hand_off_Weir_has_no_record_of_is_not_believed_inside_the_grace_window`, `A_hand_off_Weir_has_no_record_of_after_the_grace_window_is_lost_and_a_case_is_raised`, `A_hand_off_Weir_never_received_is_not_found` |
| status of a hand-off read after a refused cancel | `handoff-cancel-working`, `handoff-cancel-too-late`, `handoff-cancel-refused`, `outcome-superseded`, `outcome-superseded-newer` | as above | as above | `A_status_is_read_as_Weir_said_it` |
| connection refused | none (simulated) | Unreachable | hand-off kept; the observation records when Weir went quiet; never counted against the file | `A_status_asked_while_Weir_is_switched_off_is_unreachable_not_an_answer`, `A_hand_off_is_not_given_up_while_Weir_is_switched_off` |

## 5. Cancel: `DELETE /api/v1/intake/handoffs/deluno/{handoffId}`

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `204` (still queued) | `handoff-cancelled`, `handoff-cancelled-in-burst-204` | answered | hand-off ends stopped, "...and it was asked to drop <release>"; Activity `processing.cancelled` | `A_cancel_Weir_accepts_with_204_is_answered`, `A_cancel_Weir_accepts_ends_the_hand_off_as_stopped_and_says_Weir_was_asked_to_drop_it` |
| `409` `{ detail: "This hand-off is working, so Weir did not cancel it." }` | `handoff-cancel-working` | busy, with Weir's words | nothing changed; "Weir has started work ... Deluno will not interrupt it" (a removed title ends it anyway and its output is not imported) | `A_cancel_Weir_refuses_with_409_is_busy_and_carries_Weirs_words`, `A_cancel_Weir_refuses_leaves_the_hand_off_alone_and_says_why`, `A_cancel_Weir_refuses_for_a_title_that_was_removed_ends_the_hand_off_anyway_and_does_not_import_its_output` |
| `409` `{ detail: "...is completed, so Weir did not cancel it." }` | `handoff-cancel-too-late`, `handoff-cancel-refused` | busy, with Weir's words | same: nothing changed; the next status poll shows `completed` | same |
| connection refused | none (simulated) | Unreachable | hand-off ends stopped and says "it could not be told, so it may still have <release>" | `A_cancel_while_Weir_is_switched_off_ends_the_hand_off_and_says_Weir_may_still_have_it` |

## 6. Outcome: `POST /api/v1/intake/handoffs/deluno/{handoffId}/outcome`

Request body (pinned against every recorded request): `outcome` (`imported` or `not-imported`), `occurredUtc` (when it
happened, the same on every attempt), `importedPath` (only for `imported`), `reason` (the note a season pack sends about
skipped files, or why it was not imported).

| Weir answers | Recording | Deluno reads | Deluno does (stored, Activity, asks again?) | Test |
|---|---|---|---|---|
| `200` `{ handoffId, outcome, released: true, message }` | `outcome-delivered-without-output-path`, `outcome-finished-in-burst-200` | answered, released | delivered, one attempt; `processing.outcome-reported` "... It released its copy."; never asked again | `An_outcome_Weir_acknowledges_with_200_is_answered_and_says_whether_it_let_go_of_its_copy`, `An_outcome_Weir_acknowledges_is_stored_delivered_and_said_in_Activity_and_never_asked_again` |
| `200` `released: false` (Weir's copy is the library file) | `outcome-delivered`, `outcome-already-recorded`, `outcome-superseded`, `outcome-superseded-newer` | answered, not released | delivered; Activity says Deluno told Weir it has the file and repeats Weir's own words | same |
| `200`, imported with a `reason` note | `outcome-imported-with-reason` | answered | delivered; Activity carries the note in brackets | same |
| `200`, `not-imported`, no reason / the default words / an import refusal | `outcome-not-imported-no-reason`, `outcome-not-imported-default-reason`, `outcome-not-imported-import-refused` | answered | delivered; Activity "Deluno told Weir it will not import <release>: <reason>"; Weir kept its copy | same |
| `409` `{ detail, code: "handoff_not_finished" }` (queued, working) | `outcome-handoff-not-finished-queued`, `outcome-handoff-not-finished-working` | busy | stays owed: pending, next attempt a minute later, then doubling to an hour; Activity `processing.outcome-delayed` said once; gives up after a week | `A_409_outcome_is_read_by_its_code_never_by_its_words`, `A_hand_off_Weir_has_not_finished_stays_owed_and_is_asked_again_in_a_minute`, `An_outcome_sent_before_and_during_Weirs_work_is_kept_and_retried_until_Weir_has_finished` |
| `409` `{ detail, code: "handoff_ended" }` (cancelled, failed) | `outcome-handoff-ended`, `outcome-handoff-ended-after-failed` | refused for good | refused, one attempt, never asked again; Activity `processing.outcome-not-reported` with Weir's words | `A_409_outcome_is_read_by_its_code_never_by_its_words`, `A_hand_off_Weir_says_has_ended_is_refused_for_good_and_Weirs_words_are_in_Activity` |
| `409` `{ detail, code: "outcome_already_recorded" }` | `outcome-outcome-already-recorded` | settled | settled, never asked again; Activity `processing.outcome-kept` "kept its earlier answer" | `A_409_outcome_is_read_by_its_code_never_by_its_words`, `An_answer_Weir_already_has_is_settled_and_Deluno_stops_telling_it` |
| `404` `{ detail }` | `no-record` | not found | "never received", not a failure, never asked again; Activity `processing.outcome-never-received` | `An_outcome_for_a_hand_off_Weir_never_received_is_not_found`, `A_hand_off_Weir_never_received_is_not_a_failure_and_is_not_asked_again` |
| connection refused | none (simulated) | Unreachable | stays owed with "Deluno could not reach Weir."; `processing.outcome-delayed` | `An_outcome_sent_while_Weir_is_switched_off_is_unreachable_and_so_owed_again`, `An_outcome_sent_while_Weir_is_switched_off_stays_owed_and_says_it_could_not_be_told` |

Other replies a processor may give an outcome (`401`/`403`, `5xx`, `400`/`422`) are read by plain HTTP rules and have no
Weir recording; they are listed in `tests/fixtures/recorded/MISSING.md`.

## 7. Reports Weir sends to Deluno: `POST /api/integrations/processors/events`

Replies are documented in `docs/external-integration-api.md` ("Replies and limits"), each held by
`tests/Deluno.Persistence.Tests/Api/ProcessorCallbackContractTests.cs` and `AProcessorIsNotTakenAtItsWordTests`;
a late report for a superseded send is held by `ALateReportForARemovedSendIsSettledTests` and
`AResentReleaseSettlesItsOwnHandOffTests`. **No Weir report body has been recorded** (the report is a request Weir makes,
and `tests/fixtures/recorded/deluno/` does not exist yet), so those tests build the report themselves from the
documented shape. This is gap 1 in `tests/fixtures/recorded/MISSING.md`.

## 8. Deluno's side as fixtures Weir can load

`tests/fixtures/recorded/deluno/` (written by `scripts/recorded-replies/Collect-DelunoSide.ps1` from a recording proxy
between the real Deluno and the real Weir) does not exist yet. Until it does, the request side of Weir's own recordings
(`weir/<version>/*.json`) is what Deluno's requests are held against. **Those requests were sent by the recording scripts
(`Record-Weir.ps1`, `Record-WeirStates.ps1`), written to the documented hand-off and outcome shapes, not by the real
Deluno.** So `The_hand_off_Deluno_sends…` and `The_outcome_Deluno_sends…` keep Deluno, the scripts and Weir's replies in one
shape (a change to what Deluno sends fails them), but they are not yet a record of what the real Deluno sent. Request
headers are not recorded at all; the headers are asserted by the tests above from the code. Both are gap 2 in
`tests/fixtures/recorded/MISSING.md`.
