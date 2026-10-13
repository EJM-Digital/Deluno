# Recordings the golden VM could not produce

Written by scripts/recorded-replies/Invoke-RecordReplies.ps1 on 2026-10-11. Each line says what is needed to record it.

- qbittorrent downloading / stalled-near-end: no peer connection formed between transmission and qbittorrent (the clients may refuse peers on one address)
- weir status-queued: the burst of hand-offs was finished before the last could be seen queued
- weir failed with failureClass not_taken (Weir #956, Deluno #1250): Weir answers it when a second connection of the same kind hands off a file whose processing pass belongs to the first; the VM has one Deluno connection to Weir, so it cannot be produced there. It needs a second processor connection in Deluno pointing at the same Weir and watched folder
- rtorrent: not installed in the VM; install it there (or on a machine the script can reach) and add a recorder
- utorrent: not installed in the VM; same
- emby, jellyfin, plex: not installed in the VM; their library, item and playback replies need a server with real media
- torznab and newznab feeds: only the golden path stubs exist in the VM, and a stub is not a real indexer; a real indexer's caps and search replies need its API key
- sabnzbd duplicate-add: SABnzbd 5.1.3 answers an addurl of a duplicate with a new job id (it fetches the NZB afterwards), so the empty-id answer was not recorded; the capture script now sends the NZB as a file (addlocalfile) for that, but it was not re-run
- qbittorrent downloading / stalled-near-end seeded by transmission: no peer connection formed; it was recorded with Deluge as the seeder
- tests not yet moved onto recordings: see tests/recorded-reply-allow-list.json (kind "pending"): the Deluge, Transmission and qBittorrent client tests, the Weir processor tests, the Tray client-setup test and KeepSeedingMoveOnEveryTorrentClientTests
- Deluno's own side of its talk with Weir (tests/fixtures/recorded/deluno): not recorded; it needs the recording proxy (Start-RecordingProxy.ps1) between Deluno and Weir during a hand-off and the processor connection retargeted

## Deluno-Weir contract gaps (#1239, docs/deluno-weir-contract.md)

- weir reports to Deluno (`POST /api/integrations/processors/events`): no real Weir report body is recorded. The contract tests (`ProcessorCallbackContractTests`, `AProcessorIsNotTakenAtItsWordTests`) build the report from the documented shape. Needed: the proxy (Start-RecordingProxy.ps1) in front of Deluno during a golden hand-off, so the accepted, started, completed, failed and rejected reports Weir really sends, and Deluno's 202/409 replies to them, are recorded as `deluno/<version>/events-callback`.
- deluno's own requests to Weir (`tests/fixtures/recorded/deluno`): not recorded. The requests inside `weir/<version>/*.json` were sent by Record-Weir.ps1 and Record-WeirStates.ps1 from the documented shapes, so they pin Deluno to the scripts, not to what the real Deluno sent. Request headers (the secret header, `Idempotency-Key`) are not recorded either. Needed: Collect-DelunoSide.ps1 run after a proxied golden hand-off, which records the hand-off, outcome, status, cancel and capabilities requests with their headers (secrets removed).
- weir `401`/`403` on the status poll, the cancel and the outcome post: only the capabilities call and the hand-off submit have a recorded refusal. Needed: the same three calls with a wrong secret. (Deluno reads any 401/403 the same way on every call; the tests cover that path with the capabilities refusal.)
- weir `404` on the cancel call: only the status poll and the outcome post have a recorded "no record". Needed: a DELETE for a hand-off id Weir never had.
- weir `5xx`, `429` and `400`/`422` on the status poll and the outcome post (the replies Deluno treats as "not now" and "the report was wrong"): never produced by Weir on the VM. Needed: a Weir that is starting up or failing (stop its worker during a call), and an outcome body Weir rejects.
- weir connection refused (Weir switched off) and a timeout: no reply to record. The contract tests make the connection fail; a recording of the refusal text (status 0 with the error) from each call would let them use it.
- weir `scheduled` and `passed-through` states on the status poll: Deluno reads them, Weir 1.0.0-rc.13 was never seen giving them. Needed: a scheduled workflow run for `scheduled`; `passed-through` may be a state Weir no longer uses (ask the Weir session).
- weir capabilities carrying `watchedPath`: rc.13 does not send it. Needed: confirm with the Weir session whether it is ever sent.
- weir `outcome` for a hand-off Weir finished but whose stored note belongs to another send of the same release (#1232): `outcome-superseded` records the older and newer replies, but both carried an ordinary message; a reply whose message contradicts the outcome has not been seen from the real Weir.
