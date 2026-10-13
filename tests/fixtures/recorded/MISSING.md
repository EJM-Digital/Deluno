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

## Deluno-Weir contract gaps (#1239, docs/deluno-weir-contract.md)

Recorded on 13 October 2026 (Deluno 1.0.0-rc.30.30, Weir 1.0.0-rc.13): Deluno's own requests with their headers, Weir's reports to Deluno, and Weir's refusals of a wrong secret, an unknown id, a refused body and an Origin without X-Requested-With. What is still missing:

- weir plain-text `500 Internal Server Error`, treated by Deluno as retryable: Weir has no deliberate 5xx or 429 on its intake routes. The only 5xx is the web server's plain-text 500 for an exception nothing handled (SQLite still busy after 30 s, say), and the VM cannot be made to produce that on purpose. `WeirPlainTextFiveHundredTests` builds it. `429` is never sent by Weir.
- weir `401` for a secret that belongs to a different connection than the hand-off's owner (the same text as a wrong secret): needs a second connection of the same kind in Weir with its own secret. The VM has one.
- weir `403` "Set a webhook secret in Weir so a media manager can ask about hand-offs" on the status poll was recorded under a source key with no connection (`status-no-connection-for-source`), not on Deluno's own source with its secret removed; the second needs Weir's secret for the Deluno connection to be unset, which would break the real connection.
- weir report `not_taken` (Weir #956, Deluno #1250) (listed above too): it needs a second processor connection in Deluno pointing at the same Weir and watched folder.
- weir reports `accepted`, `started` and `rejected`: in this run Weir sent only `completed` and `failed` reports to Deluno (and `source_gone` as a failure class). A `started` or `accepted` report was never sent, and `rejected` needs a reject-capable workflow.
- a report for a hand-off Deluno has no row for at all: every hand-off the reports named still had a row (a late report was answered 409 for a dismissed one). It needs a hand-off row that is gone, for example a title removed while Weir was working.
- the person's "I don't want it" on a hand-off Weir is working on (`409` working, as Deluno sent it): the cancel Deluno sent was taken while Weir still held the hand-off queued, so only the 204 is a Deluno-sent cancel. `handoff-cancel-working` and `handoff-cancel-too-late` are Weir's replies to a cancel sent by the recording script.
- weir connection refused (Weir switched off) and a timeout: no reply to record. The contract tests make the connection fail.
- weir `scheduled` and `passed-through` states on the status poll: Weir 1.0.0-rc.13 was never seen giving them. A scheduled workflow run would give `scheduled`; `passed-through` may be a state Weir no longer uses (ask the Weir session).
- weir capabilities carrying `watchedPath`: rc.13 does not send it.
- weir `outcome` for a hand-off Weir finished but whose stored note belongs to another send of the same release (#1232): the reply carried an ordinary message; a reply whose message contradicts the outcome has not been seen from the real Weir.
