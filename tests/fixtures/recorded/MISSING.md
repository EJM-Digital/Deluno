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
- tests not yet moved onto recordings: see tests/recorded-reply-allow-list.json (kind "pending"): only the uTorrent and rTorrent parts of eleven files, each entry saying what to record
- found while moving the tests onto recordings (#1237), replies a test needed that are not recorded; each was answered with the nearest recorded reply changed in the one value the test is about, and says so:
  - qbittorrent: the reply to torrents/setAutoManagement; the tracker list of a tracker that is not working (status 4 and its message); the reply to an add of a link (not a file) and to a link that is dead
  - deluge: the reply to core.add_torrent_url; label.* with the Label plugin switched off (the "Unknown method" error); core.get_torrent_status for a hash it never had
  - transmission: torrent-get with the fields Deluno asks for now (uploadRatio, secondsSeeding, activityDate, doneDate, seedRatioMode, seedRatioLimit, seedIdleMode, seedIdleLimit; the recording's field list was shorter); a refused torrent-set
  - sabnzbd: the history entry of a duplicate it turned down, with the wording it gives in fail_message (the recorded duplicate-add shows only the add)
  - weir: the "scheduled" state (a library with processing hours); /openapi.json; a 403 (rc.13 answers 401 for a missing and for a wrong secret); the outcome and status replies that the recordings hold for one hand-off only
  - plex, jellyfin, emby and seerr: the identity and status replies that carry their version (ConnectionVersionParserTests still holds those shapes)
- Deluno's own side of its talk with Weir (tests/fixtures/recorded/deluno): not recorded; it needs the recording proxy (Start-RecordingProxy.ps1) between Deluno and Weir during a hand-off and the processor connection retargeted
