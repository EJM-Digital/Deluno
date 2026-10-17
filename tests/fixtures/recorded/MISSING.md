# Recordings the golden VM could not produce

Written by scripts/recorded-replies/Invoke-RecordReplies.ps1 on 2026-10-11. Each line says what is needed to record it.

- tests not yet moved onto recordings: see tests/recorded-reply-allow-list.json (kind "pending"): only the uTorrent and rTorrent parts of eleven files, each entry saying what to record
- found while moving the tests onto recordings (#1237), replies a test needed that are not recorded; each was answered with the nearest recorded reply changed in the one value the test is about, and says so:
  - weir: the outcome and status replies that the recordings hold for one hand-off only
  - plex, jellyfin, emby and seerr: the identity and status replies that carry their version (ConnectionVersionParserTests still holds those shapes)

## Deluno-Weir contract gaps (#1239, docs/deluno-weir-contract.md)

Recorded on 13 October 2026 (Deluno 1.0.0-rc.30.30, Weir 1.0.0-rc.13): Deluno's own requests with their headers, Weir's reports to Deluno, and Weir's refusals of a wrong secret, an unknown id, a refused body and an Origin without X-Requested-With. What is still missing:

- weir plain-text `500 Internal Server Error`, treated by Deluno as retryable: Weir has no deliberate 5xx or 429 on its intake routes. The only 5xx is the web server's plain-text 500 for an exception nothing handled (SQLite still busy after 30 s, say), and the VM cannot be made to produce that on purpose. `WeirPlainTextFiveHundredTests` builds it. `429` is never sent by Weir.
- weir reports `accepted`, `started` and `rejected`: in this run Weir sent only `completed` and `failed` reports to Deluno (and `source_gone` as a failure class). A `started` or `accepted` report was never sent, and `rejected` needs a reject-capable workflow.
- a report for a hand-off Deluno has no row for at all: every hand-off the reports named still had a row (a late report was answered 409 for a dismissed one). It needs a hand-off row that is gone, for example a title removed while Weir was working.
- the person's "I don't want it" on a hand-off Weir is working on (`409` working, as Deluno sent it): the cancel Deluno sent was taken while Weir still held the hand-off queued, so only the 204 is a Deluno-sent cancel. `handoff-cancel-working` and `handoff-cancel-too-late` are Weir's replies to a cancel sent by the recording script.
- weir connection refused (Weir switched off) and a timeout: no reply to record. The contract tests make the connection fail.
- weir capabilities carrying `watchedPath`: rc.13 does not send it.
- weir `passed-through` on the status poll: never seen from rc.13 or rc.18; it may be a state Weir no longer uses (ask the Weir session).
- weir `outcome` for a hand-off Weir finished but whose stored note belongs to another send of the same release (#1232): the reply carried an ordinary message; a reply whose message contradicts the outcome has not been seen from the real Weir.

## GitHub (`github/2022-11-28`, #1237, #1263)

Every state is a real unauthenticated GET from the public internet, on 14 October 2026, with the user agent `Deluno-test-recording`; a recording's own note says what was asked and how it was shortened (release-note bodies and most of a long list removed). About 14 calls to `api.github.com` were spent in all, and the hourly limit was never reached.

- `weir-newest-release-has-no-files`, `nzbget-latest-release`, `release-feed-not-found`: recorded for #1263 (Weir's rc.17 was a published pre-release with no files).
- `sabnzbd-latest-release`: `api.github.com/repos/sabnzbd/sabnzbd/releases/latest`.
- `qbittorrent-tags`, `deluge-tags`: the tags API (`?per_page=100`) and `tags.atom` of two projects that tag and do not publish releases; Deluge's list carries development tags such as `deluge-2.2.1.dev0`.
- `nzbget-release-feed`: NZBGet's `releases.atom`, whose newest entry is the rolling tag `testing`, not a version.
- `weir-velopack-feed`: the `releases.win.json` file that Weir (built with Velopack) publishes beside its installer, rc.13.
- `api-not-found`: the API's own 404 body for a repository that does not exist (a private repository answers the same).
- not recorded: the anonymous rate-limit reply (a 403 or 429 with `X-RateLimit-Remaining: 0`) and a bare 503. Recording the limit would mean using the whole hourly allowance of a shared address, so tests of those states build the status and its `X-RateLimit-*` / `Retry-After` headers by hand (no body of an app's shape is involved). `jampat000/Deluno` has no public release feed yet, so Deluno's own update feed is tested with Weir's, which is built the same way.
- states a test needs that GitHub was not in (the newest release a draft, a release changed into a stable one, the feed listed oldest first) are the nearest recorded reply with only that value changed, and each test says so.

### Reports Weir sends Deluno that the tests still build inline (#1239, needs a Weir re-record on the golden VM)

Deluno's own replies to the calls Weir makes are now recorded (`deluno/1.0.0-rc.30.32/reply-*`, from the server code, see `docs/deluno-weir-contract.md` section 9). The reports Weir sends to `POST /api/integrations/processors/events` are the real Weir's wherever one was recorded (`deluno/1.0.0-rc.30.30/events-*`): `ProcessorCallbackContractTests`, `AProcessorIsNotTakenAtItsWordTests`, `ALateReportForARemovedSendIsSettledTests` and `AResentReleaseSettlesItsOwnHandOffTests` post those bodies (through `RecordedWeirReports`) for a finished single file, release folder and Usenet job, a report after the person stopped waiting, and the held, unreadable and source-gone failures. The cases below have no real Weir report, so their bodies stay inline in those four classes, each marked "deliberate" here with the Weir state that would produce it. `scripts/check-recorded-replies.mjs` does not see them (it recognises another app's reply, and these are requests to Deluno), so they need no allow-list entry.

- `accepted` and `started` reports (DELIBERATE and never to be recorded: the generic processor contract allows a processor to send them, but Weir never does. Its push statuses are only `completed` and `failed`; "accepted" is Deluno's own 2xx answer to a hand-off and "started" is the status poll saying `working`) (`A_report_may_list_exactly_the_maximum_number_of_files`, `A_report_repeated_for_a_hand_off_already_started_is_accepted_again...`): these two stay inline for good, each test marked deliberate for that reason.
- `failed` with `disposition: "rejected"`: RECORDED with rc.18 (`events-failed-rejected`, an audio-only file under the reject policy; `sourceRemoved` is false, Weir left the file), and the test (`A_failed_report_for_a_removed_send_that_was_asked_for_again_is_settled_too`) now posts it.
- `failed` carrying `outputFiles` and a `message` that counts what failed: RECORDED for a TV season pack with one unreadable episode (`events-failed-partial-season-pack`, no `outputPath`, `disposition: "held"`), and `A_partial_failure_imports_what_succeeded...` and `A_main_video_the_processor_failed_still_fails_the_hand_off` post it. Still inline, deliberate: a film plus a featurette under the 50 MB minimum (`A_small_extra_the_processor_left_out...`, `A_failed_report_that_names_its_output_files_but_no_output_path...`, `A_later_failed_report_for_one_small_extra...`), which the recorder does not make.
- `completed` for a TV season pack naming several episodes in `outputFiles` and a folder `outputPath`: RECORDED with rc.18 (`events-completed-season-pack`), and `A_folder_hand_off_with_several_output_files_imports_as_one_pack` posts it. `Output_files_the_processor_names_but_are_not_there_are_reported` stays inline (a file Weir named that is not there is Deluno's own check).
- `completed` from an older Weir with no `outputFiles` (`A_completion_with_no_output_files_from_an_older_processor_still_works`): current Weir always sends it, so this one is a deliberate old-shape case.
- `completed` that names a file belonging to another hand-off, or a path outside the library's output folder (`A_file_that_is_another_downloads...`, `...claimed_by_another_hand_off...`, `An_output_path_outside_the_configured_folder...`, `A_file_that_names_no_download_at_all...`): needs a Weir whose output folder or path mapping disagrees with Deluno's, so that the path it reports is outside Deluno's.
- the same `completed` report twice (`The_same_completion_reported_twice_imports_once`, `A_second_completion_of_a_completed_hand_off...` second post, `The_first_sends_late_completed_report_with_its_output...`): the first post is Weir's recorded body. Weir's own retry was NOT seen: after the proxy answered a report 503, nothing more arrived in 300 seconds (`events-completed-answered-503-not-repeated`), so the repeats stay the test's own second post of the recorded body, deliberate.
- a report that lists more than 1000 files or a path over 4096 characters, a status word Deluno does not know (`halfway`), a `completed` with no `outputPath`, a report for a library Deluno does not have (the `400` cases): Weir never sends these, so they are deliberate hand-built limits and are not expected to be recorded.
- `401`, `403` and `429` for the events route (`No_sign_in_is_401_and_a_key_without_the_imports_scope_is_403`): Deluno's own replies to a missing credential and a key without the scope are recorded for the four routes in `reply-no-sign-in` and `reply-key-without-imports-scope`. What Weir does on being refused is now recorded with the proxy answering a finished report 401 and 403 (`events-answered-401-not-repeated`, `events-answered-403-not-repeated`: Weir sent nothing more in 130 seconds); a Weir whose own key is wrong was not made. `429` needs more than 3000 requests a minute from one key and is not expected.
- the library-wide `download-destinations` reply with a routed download client (`destinations[]` filled): the recordings hold a library with no client routed, because the test host cannot ask a real client. It needs the golden VM's Deluno asked while a qBittorrent, a SABnzbd and a Transmission are routed to a library.

## Processor output layout (#1262)

`AProcessorsFinishedFileIsFoundWhereItPutItTests` uses Weir 1.0.0-rc.13's recorded replies for a flat single file, a file in a release folder, a folder hand-off and a resend whose finished status has no `outputPath` (`resend-status-completed`). Two layouts have no real Weir reply behind them yet:

- weir `stem (n).ext` name collision: RECORDED with rc.18 (`output-collision-keep-both-*`, `events-completed-collision-keep-both-*`): `stem (2).ext`, numbered from 2. The test reads it from the recording.
- weir completed status with no `outputPath` after a takeover: rc.18 does NOT clear the path (see the rc.18 section below); the null-path shape stays the rc.13 recording `completed-without-output-path`, kept for an older Weir.

## The Weir 1.0.0-rc.18 recording, 15 October 2026

Made on the golden VM (Deluno 1.0.0-rc.30.32, Weir 1.0.0-rc.18, set up by the scenario suite) by `Invoke-RecordReplies.ps1 -Apps weir -Extra weir-states,weir-reports,weir-errors -IncludeBaseWeir`, with three re-runs of single steps (`-ReportSteps`) after the recorder's own faults were fixed (an array of one report read as a hash table; two files of a collision placed in different folders, so they never met; a truncated film Weir would not read). The moves of the tests onto it are in `docs/deluno-weir-contract.md` section 10.

Recorded: `weir/1.0.0-rc.18` (the whole base set plus `resend-takeover-*`, `output-collision-replace-*`, `output-collision-keep-both-*`, `failed-rejected-status-*`, `completed-season-pack-status-*`, `failed-partial-season-pack-corrupt-status-*`, `database-busy-outcome`, `database-busy-retried`, `internal-error-outcome`) and `deluno/1.0.0-rc.30.32/events-*` (`events-superseded`, `events-completed-after-takeover`, `events-completed-collision-*`, `events-failed-rejected`, `events-completed-season-pack`, `events-failed-partial-season-pack` and `-corrupt`, `events-completed-answered-503-not-repeated`, `events-answered-401-not-repeated`, `events-answered-403-not-repeated`).

What the real Weir did that the earlier notes did not expect:

- **A resend's finished status has an `outputPath` in rc.18.** Sending one source twice at once (a 362 MB film, the second sent while the first was queued or working) made the first send `cancelled` ("This release was sent again ...") and the second finish `completed` WITH its output path, three times over (the base resend, the takeover, the reports run). `completed-without-output-path` could not be produced from rc.18 and stays the rc.13 recording. The "rc.14 and later clear the path" note below is not what rc.18 does.
- **No repeat after a 503.** The proxy answered a finished report 503 once; nothing more reached it in 300 seconds. The state is recorded as `events-completed-answered-503-not-repeated`; an `events-completed-repeated-after-503` (the same body twice) could not be produced, and the tests hold only that Deluno imports once however often the same body arrives.
- **`keep_both` numbers from 2** (`stem (2).ext`); "replace" writes the same path.
- **Weir's status has a `rejected` state**, and a `failed` report with `disposition: "rejected"` and `failureClass: "preflight"` for an audio-only file under its reject policy.
- **A 503 for a hand-off is not produced.** `database-busy-handoff`: under a held lock a hand-off was refused with 400 (no such library on the second Weir server) before it wrote anything, so only the outcome report's 503 is recorded.

Not recorded, and why:

- `failed` carrying `outputFiles` and a `message` for a film with a featurette under Weir's 50 MB minimum, with and without `outputPath` (`A_small_extra_the_processor_left_out...`, `A_failed_report_that_names_its_output_files_but_no_output_path...`, `A_later_failed_report_for_one_small_extra...`): the recorder makes a season pack with one unreadable episode, not a film plus a small extra. These tests keep their inline bodies, marked deliberate.
- `completed` that names a file of another hand-off or a path outside the output folder: Weir would have to disagree with Deluno's folders; unchanged.
- `not_taken` (needs a second processor connection to the same Weir), a report for a hand-off Deluno has no row for as Deluno's own state (the recordings above have no row only because the script handed off straight to Weir), `accepted` and `started` reports (Weir never sends them): unchanged.
- 401 and 403 are recorded as Weir's behaviour on being refused (not repeated); a Weir whose Deluno key is wrong in Weir's own settings was not made.

### `resend.single-file.takeover` on the VM, 15 October 2026

Run alone on a fresh set-up (`-Scenarios resend.single-file.takeover`, installer built from the commit under test, 86 seconds for the scenario): **failed**, two checks.

- ok: the during moment was reached with the first send still open; the new hand-off settled by itself; the film is in the library, exactly one copy; nothing waits in Weir's output; the first send is closed ("replaced by the new send"); no 'unmatched in processor output' problem; one 'Imported into the library' row; the closing lines tell one story; no HTTP 409 in Weir's or Deluno's logs; Deluno imported from `<output folder>\<source path relative to the watched folder>`.
- FAILED: "Weir's final answer for the resend is 'completed' and names no output path": Weir rc.18 named it (`C:\Weir\Ready\Movies\<file>.mkv`).
- FAILED: "Deluno's log names that it found the cleaned file itself": 0 rows, which follows from the first: with the path in Weir's answer Deluno had nothing to find.

Both failures are the scenario expecting rc.14's behaviour (no path after a takeover); rc.18 does not do it. The import side passed. The scenario's two checks need changing to what rc.18 does (the product reads both shapes), which is the Weir session's call on whether the no-path answer is meant to come back.

## Real apps recorded on the record VM, 14 October 2026 (scripts/recorded-replies/Invoke-RecordRealApp.ps1)

The run was stopped by the owner before every app was recorded. Recorded and committed: **Jackett 0.24.2850** (real Torznab over the public trackers YTS and The Pirate Bay: caps, feed, text, movie-by-IMDb and TV searches, wrong or missing key, unknown indexer or function, a download link), **NZBHydra2 9.1.1** (real Newznab with no indexer behind it), **FlareSolverr 3.5.2** (Windows release) and **Jellyfin 12.2.0** (the setup wizard, identity, empty and populated libraries, movie and series catalogue, watched, a play in progress and stopped, refresh, folder browser, collections, user create). Tests moved onto them: IndexerCapsParsingTests, FlareSolverrCloudflareHelperTests, CloudflareHelperKindTests, CloudflareHelperHealthCheckTests, the Jellyfin line of ConnectionVersionParserTests, and the new RecordedJellyfinTests and RecordedIndexerAndHelperRepliesTests.

Not recorded, and how to produce each (every one is one command, run with the VM idle, `./scripts/recorded-replies/Invoke-RecordRealApp.ps1 -RigHost <host> -VmName <vm> -App <app>`; restore the VM to a clean checkpoint first; the film at the default media path must exist, the runs used Sintel 1080p from download.blender.org, MD5 published beside it):

- **utorrent**: `-App utorrent`. Not run. Needs a signed-in desktop session in the VM (the script starts uTorrent through a scheduled task for that session). The installer's silent switch (`/S`) and the hand-written `settings.dat` that switches the Web UI on are untested; if the silent install does not work, install it by hand and run again. Fills: the token page, positional `list=1`, getprops/setprops/getfiles/getsettings and the whole torrent scenario (see the "pending" entries in tests/recorded-reply-allow-list.json for the ten files that wait on it).
- **rtorrent**: `-App rtorrent`. Not run. Turns on WSL1 (the VM restarts once; the script does that itself), imports Canonical's Ubuntu 22.04 root file system, installs rtorrent and nginx with apt, and records XML-RPC through nginx's scgi mount. Fills the eleven files' rTorrent parts.
- **emby**: `-App emby`. Not run. The Windows x64 .7z needs 7-Zip's `7zr.exe` (fetched from the 7-Zip project); Emby may listen on all interfaces inside the VM because its bind address could not be confirmed to be settable.
- **plex**: `-App plex`. Not run. Records an UNCLAIMED server (no account is made). If an unclaimed server refuses local calls without a token, only /identity is recorded. The plex.tv replies (users, shared servers, resources) need a claimed server and stay unrecorded.
- **seerr**: `-App seerr` after `-App jellyfin` (same VM, no restore between). Not recorded: the build from source (Node 22, pnpm install, pnpm build) was started but not finished. The first attempts failed on a GitHub API 403 and on npm's stderr notices; both are fixed in Record-Seerr.ps1. Seerr's setup flow changed in 3.5 (the release notes warn of new library endpoints), so expect to adjust the first-sign-in calls.
- **Newznab with a real indexer**: search results, `t=get` and account errors need an account at a Usenet indexer; none is made. NZBHydra2's own replies are recorded.
- **Torznab `tracker-failing`**: a public tracker that fails Jackett's own test only exists on a day one is blocked; the run found all configured trackers answered.
- **FlareSolverr "Challenge solved!"**: needs a page that actually shows a Cloudflare challenge to the VM; the public pages tried did not (`challenge-not-seen` is recorded instead).
- **Jellyfin delete by id**: Jellyfin 12.2.0 answered `DELETE /Items/{id}` of a scanned throwaway film with 500 "Error processing request." (recorded as `item-deleted`, with 400 for an unknown id). Whether the 500 is a permissions problem of the service account or Jellyfin's real answer was not investigated; record it again as a user with delete rights before treating it as Jellyfin's behaviour. The setup wizard's `RemoteAccess` call also answered 500 and the wizard still completed.
- **Tests still building these apps' replies inline** (listed "pending" in tests/recorded-reply-allow-list.json with the reason): the Torznab and Newznab feed tests that need crafted releases, MediaServerClientTests and MediaServerVersionTests (Emby and Plex parts; the Jellyfin parts of MediaServerClientTests were not rewritten), LeavingSoonCollectionClientTests (Plex), SeerrClientTests and MigrationAssistantServiceTests (Seerr), the Emby, Plex and Seerr lines of ConnectionVersionParserTests, and the uTorrent and rTorrent parts of the eleven download-client files.

## Needed to close the integration coverage gaps (#1301)

Found while proving each integration operation on recordings. Each line is a state a row of `docs/testing/coverage-register.json` needs and no recording holds; the row stays a gap until it is recorded.

- qbittorrent: a sign-in answered 403 (the address banned after too many wrong logins; q7), and a sign-in answered 200 with the body `Fails.` (the "rejected right after install, then accepted" variant of q6).
- deluge: `core.get_torrent_status` asking for the `label` field (the keep-seeding move reads it before and after; the test changes the one value in the recorded files reply), `core.remove_torrent` answered `false`, and `label.get_labels` with the Label plugin switched off.
- nzbget: `listfiles` (n17), and `append` answered with an error object for a release it will not take.
- sabnzbd: `get_files` (s21).
- utorrent and rtorrent: no recording at all, so every utorrent and rtorrent row stays a gap.
- emby, plex and seerr: no recording at all, so every row that names only them stays a gap.
- jackett and nzbhydra2: a `t=search` (not `t=caps`) answered for a wrong key, a 3xx redirect on a search, feed read or test (tn10, tn35), a torrent file download that is redirected, answered 429 with Retry-After, or timed out (tf10), and a Newznab search that returns releases (the recorded searches hold none), whose NZB links a Usenet grab hands to SABnzbd or NZBGet (tn48).
- weir: `HEAD` on the hand-off address and `GET` on a health address (the connection Test; w1), and a processor whose hand-off address redirects to another host (w14).
- jellyfin: an API key that is not an administrator's (the recorded `/Users` 401 is a key Jellyfin does not know at all, and the key-cannot-list-users test uses it), a collection taken off the home screen (un-favourite), and `Library/Media/Updated` answered with an error.
- github: the vendor installer download itself (a 20 MB binary; the downloader tests use the real `SHA256SUMS.txt` of a Weir release as the downloaded file).

## Refused-login states for #1312

Every method of qBittorrent, Transmission, Deluge, NZBGet and SABnzbd is replayed against that client's recorded refused login (`EveryClientMethodKeepsItsContractOnARefusedLoginTests`). Not recorded, so not tested:

- uTorrent and rTorrent: no recording of any state exists for either client, so a refused login on them is not replayed.
- qbittorrent: the API-key variant of a refused sign-in is recorded (`api-key-refused`) but only the username and password refusal is replayed; the banned-address 403 is listed above.

## Engines rows that need a recording nobody has made yet (#1296, coverage register, area `engines`)

Each line is a register row group that cannot be proven by a recording-replay test until the reply below is recorded from the real app. None of them was built from a hand-written shape.

- seerr (Overseerr and Jellyseerr): the webhook delivery they send on approval (the agent's JSON body, with its Authorization header), for a movie and for a TV request, from a stock webhook template and from a custom one; and their API replies Deluno reads (`/api/v1/status`, `/api/v1/request`, a media record by TMDb id, a delete of a media record, a refused key). Rows: the whole `webhook-ingestion-seerr-outb` Seerr group (secret handling, media id read, tv/movie mapping, availability reports, "the payload shape is an assumption").
- plex: `GET /library/sections`, a section refresh, `emptyTrash`, the identity reply, an unreachable server and a wrong token. Row: Plex trash is emptied after a Deleted change; "a real Plex, Jellyfin or Emby is told about an imported film".
- emby: the same library refresh and library list calls Jellyfin has (jellyfin is recorded; Emby is not).
- tmdb (api.themoviedb.org v3): search/movie, search/tv, movie/{id}, tv/{id}, tv/{id}/season/{n}, movie/{id}/release_dates, collection/{id}, a deleted id (404), a 5xx and a connection refused. Rows: the TMDb broker/direct/hybrid group (`metadata-broker-monitoring-e`).
- tvdb (api4.thetvdb.com): login, search, series extended, an expired token. Rows: TV search asks TMDb and TheTVDB together; the TVDB routes of the gateway.
- the metadata gateway's own replies (services/metadata-broker): its search card, detail, catalogue, release dates, collection, popular and trending replies, recorded from the gateway code run over a recorded TMDb reply, so `MetadataSearchResult` can be deserialized from them. Row: the cross-language contract.
- download clients' own completion webhooks (qBittorrent's "run on finished" call, SABnzbd's and NZBGet's scripts): the body each posts. Rows: a failed event records import outcome failed; a dispatchId that belongs to another client is not matched.
- deluno (Deluno's own replies to Weir): `GET /api/integrations/external/manifest` and `GET /api/integrations/external/health`, as scenarios of `DelunoRepliesToWeirTests` (with `DELUNO_RECORD_REPLIES=1`), then the manifest and the contract document. Rows: manifest capabilities, manifest libraries, external health.
- jackett / nzbhydra2 search replies through a running Deluno host: a search of a movie and of an episode with the release the test then grabs, and a download link that answers dead. Rows: search against a real indexer; the RSS feed watch; a dead link ends up blocklisted.
- a download client's reply to a grab of a duplicate and of a dead link per client, taken through the whole grab service: SABnzbd's duplicate-add (see the SABnzbd line above) is the one still missing. Row: Force against a real download client, the same release is accepted again.
- rTorrent and uTorrent removal replies (see the allow-list entries): the five-kinds row for queue removal names five clients and these two are not recorded.

## Needed by the route proofs (coverage register, area api)

- sabnzbd whole-client resume: `GET /api?mode=resume&output=json` (what `POST /api/download-clients/{clientId}/resume` sends) with SABnzbd paused as a whole (queue.paused true before, false after). Only the per-job `mode=queue&name=resume` is recorded.
- jackett: the bytes of a downloaded `.torrent` (the `download-link` recording keeps a placeholder, not the file), so a grab by `.torrent` link cannot be replayed; the search-and-grab route proof sends the recorded magnet link instead.
- jackett (or any torznab/newznab app): a Cloudflare challenge reply (403/503 with the challenge page) that makes Deluno send the request through an indexer proxy (`POST /v1 request.get` to FlareSolverr). The indexer-proxy routes (`POST /api/indexer-proxies`, `PUT/DELETE /api/indexer-proxies/{id}`) therefore stay gaps.
- weir: the reply to the processor connection's own health check (`GET` of the connection's health-check address, or `HEAD` of its hand-off address when it has none), which `POST /api/integrations/processors/connections/{id}/test` sends. Only the capabilities and hand-off routes are recorded, so that route stays a gap.
- seerr, trakt, subtitle providers (OpenSubtitles and the others Deluno signs in to), and the services notification webhooks post to (Discord, Slack, Telegram, ntfy): nothing of theirs is recorded, so the routes that test, save or call them (`/api/seerr-servers*`, `/api/trakt/*`, `/api/subtitle-providers*`, `/api/notification-webhooks*`) stay gaps. Each needs its real reply to a sign-in or test call, a webhook delivery and its refusal.
- tmdb (or the managed metadata broker): the reply to a provider-record lookup (`ResolveProviderRecordAsync`) for one film and one show, so `POST /api/movies/{id}/metadata/link` and `POST /api/series/{id}/metadata/link` can be replayed.
- sabnzbd and nzbget: the replies to a Usenet provider (news server) being written (`set_config` for a server section; NZBGet `saveconfig`), for `POST /api/download-clients/usenet-provider`.

## Integrations area (#1301), what the high and medium rows still need

Rows of the integrations area that need a real reply nobody has recorded yet. Each is a gap until the state is recorded from the real app; the rows that need uTorrent, rTorrent, Emby, Plex, Seerr, Sonarr or Radarr are being recorded by another session and are not repeated here.

- qbittorrent: the reply to a login from an address qBittorrent has banned after too many wrong logins (HTTP 403, q7); the reply to `torrents/stop` and `torrents/start` on a 4.x client (404, then `pause`/`resume`, q12); the replies to `torrents/topPrio`, `increasePrio`, `decreasePrio`, `bottomPrio` (q40); `torrents/setForceStart` (q41).
- transmission: `queue-move-up`, `queue-move-down`, `queue-move-bottom` (only `queue-move-top` is recorded, t33); a daemon older than RPC 17 refusing labels (t8); a daemon that sends no `trackerStats` (t20).
- deluge: `core.queue_up`, `core.queue_down`, `core.queue_bottom` (d32); `core.add_torrent_url` (a link, not a magnet, d10); the Label plugin switched off (d8, d14, d35).
- sabnzbd: the answer to `mode=get_files` for a job (s21, the dangerous-file check); `mode=resume` for the whole client (s23); `mode=switch` with the priority tiers for move to top, up, down and bottom (s31, only the unknown id is recorded); a history row for a disk-full job, a script warning and a script failure (s16); a duplicate refused with its own fail message (s11/s12).
- nzbget: the answer to `listfiles` for a job (n17); `resumedownload`, `resumepost`, `resumescan` for the whole client (n19); `GroupMoveTop`, `GroupMoveOffset`, `GroupMoveBottom` (n27); `append` answered with `0` (n9).
- every download client: a reply that arrives after the client's timeout, and an HTTP 429 with `Retry-After` (q9, q11, t5, t7, d5, d7, s5, s7, n4, n6).
- jackett and nzbhydra2 (Torznab and Newznab indexers): the reply to a test or search when the address answers HTTP 401, 403, 404, 429 and 5xx; an HTML page in place of the feed; a 3xx redirect (tn5, tn7, tn9, tn10, tn28, tn29, tn35); the download link of a Newznab/Usenet release as the indexer gives it (tn48); Prowlarr, NZBGeek, NZBPlanet, DrunkenSlug and NinjaCentral replies (tn49); a request through an HTTP or SOCKS proxy (tn11, tn36, tn41, px6, px9).
- flaresolverr and byparr: Byparr's landing page and its refusal of a POST or a proxy (cf3, cf16); a FlareSolverr that lost its sessions after a restart (cf11).
- weir: the health address answering 2xx and a HEAD to the hand-off address (w1); the capabilities reply carrying `watchedPath` and a library folder disagreeing with the watched folder (w28); a redirect to another host, to another scheme and a redirect loop (w14); a slow answer after an update (w4).
- github (the tray and the companion downloader): `releases/download/<tag>/<installer>` answering 200 with the installer's bytes (the checksum files are recorded, the installers are not); the API's rate-limit answer (403 with `X-RateLimit-Remaining: 0`) is built in the test.
- trakt, mdblist, tmdb, tvdb, the artwork cache and the metadata gateway: every reply (device-code start, poll states 400/410/418/404/409/429, refresh, list paging, lookups, artwork bytes, a key refused, the broker's 404/5xx/503).
- every subtitle provider (OpenSubtitles, Assrt, Ktuvit, Podnapisi, SubDL, Subf2m, SubSource, SuperSubtitles, Titlovi, Gestdown): a sign-in, a search and a download, and the reply to a wrong key, a wrong password, an expired token and a quota.
- notifications (Gotify, ntfy, Pushover, Telegram and the rest): a send, a wrong token, a wrong chat and a 401/403/404.
- the Cardigann-style tracker engine: a recorded public HTML site, a JSON site and a private form login (wrong password, expired session, cookie login, API-key trackers), and the GitHub archive download of the tracker definitions.
- outbound indexer proxies: an HTTP proxy and a SOCKS proxy carrying a real search.
- the tray's client set-up (`DownloadClientSetup.ConfigureAsync`) and data-folder repair (`ComponentDataFolder`): the replies of qBittorrent to `torrents/editCategory` and `app/setPreferences`, of SABnzbd to `set_config` for its folders and categories, of Deluge to `core.enable_plugin` and `core.set_config`, of Transmission to `session-set`, and of NZBGet to `saveconfig` (only the read replies are recorded).
- weir's account calls the companion makes when it connects Weir (`CompanionTools` connect, account state, add processor connection): the replies to creating the person's account, signing in, a wrong password and a missing account.
- a Jackett or Prowlarr tracker address asked for its name (`AggregatorIndexerNames`): Prowlarr's `api/v1/indexer/{id}` reply and a Jackett caps reply that carries a tracker's own title (Jackett 0.24.2850 titles its caps just "Jackett").
- the instance-wide hourly search cap and an indexer's grab limit (the figures of a refused grab) as a real indexer sees them: not an indexer reply, but the grab path needs a recorded Usenet or torrent indexer download link with its bytes (the recorded Jackett `download-link` keeps no bytes).

## Needed by the System coverage rows (#1294, 15 Oct 2026)

- weir: a processor output folder that holds a stray finished file, a stray season folder, and a second copy of a film the library holds, each beside a hand-off in each state (working, done, handed back), as Weir leaves the folder: the System > Health rows "Import", "Clear", "Keep" and "Remove" on a processor-output row act on those folders and have no recorded Weir state to run against
- an indexer whose own test answers unhealthy or unreachable (a Torznab site that refuses, one that times out), recorded as the reply the Indexers screen stores, for the System > Health "Indexer" row and its grace for a new one
- a download client with leftover finished downloads (qBittorrent, Deluge, Transmission, SABnzbd, NZBGet): the torrents/history list of a client that holds a finished item no library file shares and one a library file still shares, for System > System tasks "Run now" on the leftover-downloads task
- TMDb (metadata provider) replies for the film, show and Discover screens: a title search for two or more characters with a held and a new result, a title link preview and link (confirmation token, a conflict with another held title, a stale token), and a TV popular list. Needed by the library and movie-detail rows that search the provider, re-link a title or add a TV show from Discover (areas library, movie-detail, show-detail, search-grab).
- Each download client's torrent or NZB add replies for a Search-page grab, a hand send asked again while the first copy downloads, and a title removed while its download runs (qBittorrent, Transmission, Deluge, SABnzbd, NZBGet): the add, the duplicate add, the remove with data. Needed by the search-grab and library rows that name each real client.

## Guided setup (coverage register, area guided-setup)

- jackett `t=search` answered to a wrong or missing API key: the guided setup's "Test search source" sends `t=search&limit=1` first and `t=caps` after, and only the `t=caps` replies to a wrong key (`caps-wrong-api-key`) and to no key (`caps-no-api-key`) are recorded. The "Failed + the server's own message" case of the search-source test cannot be proven against a real reply until the search reply to a wrong key is recorded.

## Coverage gaps in the api, services and health areas (#1297, #1298, #1299, 15 Oct 2026)

Rows left open because the other app's reply is not recorded. Each line says what is needed.

- seerr (no recording at all): the identity and status replies, the reply to a test of a connection, the webhook calls Seerr sends (request approved, request declined, a 4K approval) and the reply to a request lookup. Needed by `POST/PUT/DELETE /api/seerr-servers`, `POST /api/seerr-servers/test`, `/{id}/test`, `/{id}/webhook-secret` and `/{id}/webhook`.
- trakt (no recording): the device-code reply, the poll while pending, the poll once approved, the token refresh and the revoke. Needed by `/api/trakt/credentials`, `/connect`, `/connect/poll`, `/disconnect`.
- subtitle providers (no recording): a provider's login reply, its search reply and its refusal of a wrong account. Needed by `PUT/DELETE /api/subtitle-providers/{key}` and `/{key}/test`.
- notification webhooks (no recording): what a webhook target (Discord, Slack, a generic URL) answers to a test message, to a rate limit and to a refused request. Needed by `POST/PUT/DELETE /api/notification-webhooks`, `/{id}/test` and `/deliveries/{id}/replay`.
- sabnzbd 5.1.3: whole-queue pause and resume (`mode=pause`, `mode=resume`), and `mode=set_config&section=servers` (saving a news server). nzbget 26.3: `pausedownload`, `resumedownload` and `saveconfig` for a server. Needed by `POST /api/download-clients/{id}/resume` and `POST /api/download-clients/usenet-provider`.
- weir 1.0.0-rc.13: the `HEAD /api/v1/intake/webhook/deluno` connection check and the `/ready` health-check address (what the installer sets). Needed by `POST /api/integrations/processors/connections/{id}/test`.
- flaresolverr 3.5.2 with an indexer that answers a Cloudflare challenge page (the challenge reply, then the solved page): the indexer-proxy routes only act when an indexer meets a challenge. Needed by `POST/PUT/DELETE /api/indexer-proxies`.

## File listings of SABnzbd, NZBGet (coverage, queue and automation areas)

- sabnzbd `get_files` (the files inside one job) and nzbget `listfiles`: neither is in any recorded state, so the dangerous or junk file rule cannot yet be proven on those two clients from a real reply. Needed: a SABnzbd job and an NZBGet job holding a film and a small extra file, with the client's file-listing reply recorded.
- deluge: only `location-moved` holds a file listing (one file); a folder torrent's listing (a film and an `.nfo`) is not recorded.
- uTorrent and rTorrent: no recordings at all (see the allow-list), so the strike rules, the removal verbs and the re-check on those two have no recorded reply.
- qbittorrent, transmission, deluge, sabnzbd, nzbget: the reply to a re-check (`torrents/recheck`, `torrent-verify`, `force_recheck`, and what the two Usenet clients answer to a re-check) has not been recorded.


## Docker recording, 17 October 2026 (scripts/recorded-replies/docker/)

Recorded from the published images in Docker: qBittorrent 5.2.4 (`auto-management-set`, `tracker-not-working`, `add-link`, `add-link-dead`), Deluge 2.2.0 (`add-url`, `add-url-dead`, `status-unknown-hash`, `label-plugin-off`), Transmission 4.1.3 (`finished-seed-fields`, `label-set-refused-comma`, `label-set-refused-empty`), SABnzbd 5.1.3 (`duplicate-discarded`, `duplicate-failed`), Weir 1.0.0-rc.18 (`openapi`, `status-scheduled-file-on-hold`, `status-not-taken`, `status-other-connections-secret`, `status-deluno-source-no-secret`, and the hand-offs around them), Deluno 1.0.0-rc.30.33 into Weir (`handoff-collision-*`, `handoff-season-pack`, `handoff-partial-season-pack`, `handoff-rejected-release`, `handoff-second-connection`, `events-failed-not-taken`, `events-failed-rejected-known-handoff`), Sonarr 4.0.20, Radarr 6.4.4 and Prowlarr 2.6.5 (system status, lists, profiles, tags, root folders, custom formats, download clients, indexers) and TheXEM (`thexem/2026-10-17`).

Still missing, and why:

- tmdb, through the metadata gateway: `services/metadata-gateway` is a Cloudflare Worker that needs the `TMDB_API_KEY` secret to answer a search, and there is no key here. The deployed gateway was not used (it was to run locally or not at all). The TMDb and gateway lines above stay open.
- recyclarr: Deluno reads no Recyclarr file. The migration assistant takes pasted JSON, and Recyclarr keeps its configuration in YAML and has no JSON export; nothing to record.
- sonarr indexers: the stack's Torznab feed answers movie searches only (`tests/e2e/fixture/server.mjs` ignores TV searches) and Sonarr tests an indexer with a TV search, so Sonarr's `indexers` recording is an empty list.
- weir `partial-season-pack` final state: with the failure policy hold, a pack with one unreadable episode was still queued 150 s after the hand-off, so only Deluno's request (`handoff-partial-season-pack`) is recorded for it, not Weir's end state.
- qbittorrent, deluge, transmission, sabnzbd, weir: the other lines in this file that name states the Docker run did not need are unchanged.
- plex, jellyfin, emby, seerr, utorrent, rtorrent: not asked for in this run; their lines above are unchanged.
