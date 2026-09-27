# Release notes

Plain-language notes for each Deluno version. The Updates card shows the
notes for every version you've actually run, newest applied first.

Add a new `## <version>` section here as part of cutting each release (see
`docs/ga-release-checklist.md`). Write for the person using Deluno, not for a
developer: say what changed in everyday words, not internal file or class
names. `npm run ci:check` warns (does not fail) when the version currently
being built has no section here yet.

## 1.0.0-rc.23

Work in progress toward the next release.

- The Search page now lets you filter by category and basic type, links from
  the Dashboard, and sends a grab through the same routing a real download
  uses instead of a stub.
- Indexer sign-in failures are now recorded when they happen during a search
  or a grab, not only during the scheduled RSS sync, so a bad indexer key
  shows up wherever it actually fails.
- Activity now says when a tracker (indexer) definition is newly added versus
  when an existing one was just updated, instead of naming both the same way.

## 1.0.0-rc.22

- Deluno now tells Plex, Jellyfin and Emby about files it changes, so your
  media server picks up renames and replacements without you triggering a
  manual scan.
- Download clients are read more accurately: uTorrent decodes its own status
  instead of guessing, Transmission and Deluge report ratio and seeding time,
  and NZBGet duplicates are no longer treated as failures.
- Download health rules (stalled, no progress) are now settings you can
  tune, instead of fixed thresholds you couldn't see.
- Season and episode searching got smarter: a whole-season search and its own
  episode search no longer both grab the same release, unmonitored episodes
  are left alone, and one extra file in a season pack no longer blocks the
  episodes next to it.
- Deluno now writes its own tracker (indexer) definitions and searches
  torrent trackers itself, instead of relying on someone else's bundled copy.
- An import Deluno gets wrong is undone and searched for again automatically;
  a partial processor failure still imports whatever part did succeed.
- A long list of Windows installer and network-setup fixes: a fresh install
  no longer crashes on its first run, the installer offers download clients
  and Weir as tick boxes, and other devices on your network are correctly
  detected as able to reach Deluno.

## 1.0.0-rc.21

- A release Deluno refuses is now refused everywhere that release is known
  from, not just the one indexer it was found on.
- Indexer sign-in failures, tracker definition problems, and a processed
  file's own history are recorded more precisely, so Activity and Health
  explain what actually happened instead of a generic error.
- Whether a library uses a processor is now decided in one place, so movies
  and TV shows can no longer disagree about it.
- The Windows publish step now stops on a command that actually failed,
  instead of being confused by an unrelated logged note.

## 1.0.0-rc.20

- Deluno now takes a processor's word for less: it double-checks what a
  processor hands back instead of trusting it outright, and tells the
  processor what happened to the file afterwards.
- A processor's output path is now read the same way regardless of which
  kind of machine it runs on.
