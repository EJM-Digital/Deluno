# Release notes

Plain-language notes for each Deluno version. The Updates card shows the
notes for every version you've actually run, newest applied first.

Add a new `## <version>` section here as part of cutting each release (see
`docs/ga-release-checklist.md`). Write for the person using Deluno, not for a
developer: say what changed in everyday words, not internal file or class
names. `npm run ci:check` warns (does not fail) when the version currently
being built has no section here yet.

## 1.0.0-rc.23

rc.22 was not released: about 60 fixes landed on top of it, so this version
replaces it. Deluno is not code-signed, so Windows SmartScreen shows "Windows
protected your PC" the first time you run the installer. Choose More info,
then Run anyway.

### Installer and first run

- First run now goes in two steps: the computer and you (time zone, sign-in,
  addresses, updates), then Deluno itself.
- Every app you tick in the installer's list (qBittorrent, Transmission,
  Deluge, SABnzbd, NZBGet, Weir) is connected to Deluno by itself, once, and
  tested. Activity names each one.
- The download clients now actually start after install: qBittorrent's Web UI
  comes up with its own generated sign-in, NZBGet starts without the switch
  that crashed it on Windows, and Transmission's daemon runs without a
  window. They listen on this computer only, not on your whole network.
- Install and update no longer risk timing out on the firewall step, and the
  app starts faster the first time.
- You can choose to skip sign-in for devices on your home network, with a
  list of the addresses allowed.
- A Weir (processor) connection now tests healthy instead of always showing a
  warning, and any processor connection can be given its own health-check
  address.

### Tags

- Tags now do what they say. Tags on an indexer, download client or
  notification connection decide which titles use it, a download client
  tagged for a title is chosen before an untagged one, and a Destination Rule
  that matches a tag places the title.
- One tag picker everywhere a tag is set, with an explicit Create new tag, so
  a typo can no longer silently break a rule.
- Renaming a tag updates everything that uses it. A tag still in use cannot
  be deleted, and the Tags page lists what uses each one.
- Add a movie or show with tags, tag several indexers, clients or
  notification connections at once, and let lists and Seerr tag the titles
  they add. Notifications about a title carry its tags. Health warns when a
  tag leaves a connection with no titles.
- Release rules can apply to several tags, be put in order, and accept
  /pattern/ terms in Must contain and Must not contain.
- Titles can be tagged automatically by rules you make.

### Seerr

- Seerr requests are placed by Deluno's own library routing rules instead of
  a fixed setting. A 4K request that no rule places is refused rather than
  going into your normal library, and who asked is recorded.

### Trackers

- Deluno can now search 568 tracker sites from a built-in list, picked by
  name, kind and language. Your existing indexers move over with their
  settings.
- Each indexer's traffic goes through the proxy you gave it. A tracker blocked
  by Cloudflare says so, a Cloudflare helper that is not running is named as
  the problem, and Health warns when the helper does not answer.
- Health shows how many searches and grabs an indexer has used and when its
  limit resets. Searches and grabs made from the Search page now show in
  Indexer History.
- The Search page filters by category and basic type, and sends a grab
  through the same routing a real download uses.
- Indexer sign-in failures are recorded wherever they happen, and Activity
  says when a tracker definition is newly added versus just updated.

### TheTVDB and translations

- TV shows are described by TheTVDB first, with TMDb filling in anything it
  lacks. Episodes carry TheTVDB's aired and absolute numbers, and existing
  shows keep their numbering. Search and show pages credit TheTVDB.
- Titles, overviews and taglines show in the language you choose, with the
  original title kept.
- Anime episodes can show their absolute number.

### Retention

- Rules can look at audio language, each person's watching, a whole
  collection, smart collections, quality profile, monitored, show type,
  finales, missing episodes and keep-the-newest-N. Where the server's data is
  partial, a rule says "not known" and never removes on a guess.
- A rule can choose what it does on the day, wait while a title is being
  played, keep its list off your media server, take titles you add by hand,
  and postpone a removal by a number of days. Deluno checks every connected
  media server for something playing before it removes a title.

### Library pills and cards

- Every title sits in exactly one pill, clicking a pill changes no other
  number, the toolbar no longer moves, and All equals the pills added up.
- Cards say Unmonitored, in grey, for an unmonitored title with no file. The
  subtitle legend draws complete, partial and none as different strips.
- A title page has separate Rescan folder and Preview rename buttons, a wider
  Ratings & IDs panel, and says which indexers and clients it uses and why.
- Movies and TV keep showing matching titles while a new filter loads, and
  on a phone Sort and View no longer run off the screen.
- A followed movie collection can be unfollowed, and selected collections can
  be changed together.

### Downloads and searching

- Searches can repeat every few minutes, and automatic searching can pause
  while more than a set number of your downloads are waiting.
- Dead releases are handled more precisely: dead-link blocks carry the
  release's hash, Transmission, Deluge, rTorrent and uTorrent spot dead
  links, Search can block a release from its row, and Deluno searches again at
  once when a broken file is blocklisted at import.
- Transfers shows a torrent's availability, its place in the client's queue
  and its own seed limit, says "Unlinked from your library" when a hardlink
  is gone, and uses plain words instead of raw codes.
- Season packs are removed from the client after import, and removing or
  unmonitoring a series or season stops its downloads.
- Force import can bring in a file Deluno could not read, when you allow it.
- qBittorrent can choose where a download's files go. Leftover clean-up has
  Run now and a skip list. Processors can translate folder paths for one in
  Docker or on another machine.

### Notifications

- Each notification connection picks its own triggers with ticks, including
  Deluno update and an Include health warnings switch. Search, download,
  import, forced-import and clean-up messages have their own switches.
  Messages that are not about one title go to every connection.

### API

- Other programs can start actions at POST /api/command, read GET
  /api/system/status and GET /api/diskspace, find a film by IMDb id, and
  remove from the queue with a delete-from-client and blocklist choice, in
  Radarr's shapes. The processor callback's replies and limits are documented.

### Fixes

- Custom formats keep matching when the machine is busy.
- Subtitles: a used-up quota pauses the provider, a picture-based subtitle can
  be set not to count as a language, and each attempt keeps its score.
- Two settings that did nothing (write .nfo files, artwork) are gone.

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
