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
