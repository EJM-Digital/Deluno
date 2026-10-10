# Release notes

Plain-language notes for each Deluno version. The Updates card shows the
notes for every version you've actually run, newest applied first.

Add a new `## <version>` section here as part of cutting each release (see
`docs/ga-release-checklist.md`). Write for the person using Deluno, not for a
developer: say what changed in everyday words, not internal file or class
names. `npm run ci:check` warns (does not fail) when the version currently
being built has no section here yet.

## 1.0.0-rc.30.17

Deluno is early. It is still being tested hard and it is not finished. Expect
some rough edges, and please do not point it at the only copy of anything you
cannot replace. Nothing runs on its own until you switch it on, and everything
Deluno does is written to Activity. If something looks wrong, confusing or
just ugly, tell us at https://github.com/jampat000/Deluno/issues: every report
gets read. This notice is at the top of every release.

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### The tray
- The tray stays amber while a connection is down, even when a hand-off fails
  in the meantime, and turns green as soon as the connection answers again.
- The tray and the Dashboard use the same rule for a connection that is down.
- On a fresh install the tray shows a blinking "Deluno - setting up" icon from
  the start, with Open Deluno and Quit, while first-run setup runs.

### Downloads and your processor
- Removing a finished SABnzbd download removes its folder too, extras included.
  A removal only reads Done when the client really had the download and no
  longer has it.
- Removing a download closes its hand-off at the same moment, so it never comes
  back later as a problem.
- Whenever Deluno closes a hand-off without importing it, it tells your
  processor, so the processor can let go of its copy. Hand-offs closed earlier
  without that message get it once after this update.
- A second copy Deluno stopped because another copy was handed over reads "Not
  needed" and leaves In progress.

### Everything you can see, you can clear
- Every row counted under Activity's Failed has a Dismiss in its drawer.
- System has a Check now for its health checks, and a processor's capability
  rows clear as soon as it answers again.

## 1.0.0-rc.30.16

Deluno is early. It is still being tested hard and it is not finished. Expect
some rough edges, and please do not point it at the only copy of anything you
cannot replace. Nothing runs on its own until you switch it on, and everything
Deluno does is written to Activity. If something looks wrong, confusing or
just ugly, tell us at https://github.com/jampat000/Deluno/issues: every report
gets read. This notice is at the top of every release.

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Strikes
- "Not enough peers" keeps counting after a starved download stops moving, so
  it is removed at your count even with No progress switched off. The same goes
  for the dead-torrent and too-slow rules.
- A download takes one strike per check, from the rule that fits it best.
- No progress now means the download gained no bytes since the last check, so a
  client that reports a tiny speed on a stuck download no longer delays the
  strike.
- Each rule's removal says in Activity why that rule removed it, with your own
  numbers, for example "It had fewer than 2 peers for 3 checks in a row".

### Downloads and Weir
- Try again on a failed hand-off shows "Sending to Weir again" everywhere in the
  download's drawer, and the old failure no longer sits beside it.
- Queue import shows its result the moment you click, and other open screens
  follow at once.
- A download whose files have gone is not handed to your processor. It reads
  "Files are gone", also when the files vanish while it waits for the processor.
- A release you remove and send again no longer leaves its new hand-off stuck
  when your processor reports late on the first send.

### On a phone
- A film's file path and the cast names on films and shows wrap instead of
  being cut off.

## 1.0.0-rc.30.15

Deluno is early. It is still being tested hard and it is not finished. Expect
some rough edges, and please do not point it at the only copy of anything you
cannot replace. Nothing runs on its own until you switch it on, and everything
Deluno does is written to Activity. If something looks wrong, confusing or
just ugly, tell us at https://github.com/jampat000/Deluno/issues: every report
gets read. This notice is at the top of every release.

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Strikes that follow your rules exactly
- A strike lands once per "Check every" interval, never early, and a download
  is removed at exactly your strike count.
- No download client treats 99.9% as finished any more, so a download stuck
  just short of the end is struck like any other.
- Seeds count as peers, so a download fed by one seed is not "Not enough peers".

### Updates
- System › Updates has three actions: Check now, Download update and Restart
  and apply.
- Before any update installs, including the automatic install when Deluno quits
  or starts, Deluno backs up its data. If the backup fails, the update waits and
  Deluno says why.

### Weir and your downloads
- Small extras below Weir's own minimum size are left out of the hand-off and
  stay untouched in the download folder, so seeding is unaffected.
- Removing a finished SABnzbd download now removes it from SABnzbd's history,
  folder and extras included. SABnzbd and NZBGet actions only count as done
  when the client says so.
- A download whose files are gone says so, and never comes back as ready to
  import.
- Weir's version updates by itself after Weir upgrades.

### The tray
- The tray changes colour as soon as something it relies on stops answering,
  and no longer calls Deluno unreachable for a few minutes after a restart.
- A Usenet client with no provider set up says so, instead of "isn't answering".

### Everywhere
- Search no longer sends a release your library already holds.
- When an update check cannot run, the page says it could not check.

## 1.0.0-rc.30.14

Deluno is early. It is still being tested hard and it is not finished. Expect
some rough edges, and please do not point it at the only copy of anything you
cannot replace. Nothing runs on its own until you switch it on, and everything
Deluno does is written to Activity. If something looks wrong, confusing or
just ugly, tell us at https://github.com/jampat000/Deluno/issues: every report
gets read. This notice is at the top of every release.

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Stalled and slow downloads, the way Cleanuparr does it
- Each check where a download is under your speed limit, not moving, or short
  of peers adds one strike. The first check where it recovers clears them. At
  your count (1 to 100, 3 by default) it is removed and cleaned up as you chose.
- You set how often Deluno checks (every 5 minutes by default), the speed limit
  in KB/s or MB/s, and the strikes for each rule. The screen says when strike 1
  and removal land.
- A download stuck at 99.9% is struck like any other, while one only finishing
  its last blocks reads "Finishing", not Stalled.
- New installs have nothing switched on: setup asks whether to remove downloads
  that stall or crawl. The ignore list can skip a tag or a tracker's domain.

### Your files are safer
- Deleting one film's or episode's file now sends it to the recycle bin, where
  you can restore it, and the title stays wanted.
- Restoring any file from the recycle bin puts it back on its film or episode.
- The check for an episode file holding the wrong episode now judges only the
  download that really produced it, including season packs, and leaves a file
  alone when it cannot be sure.

### Downloads and Weir
- A film is never sent to two download clients at once, and a failed duplicate
  never blocks the release or deletes files.
- A film Weir cleaned with an extra left out now reads Imported, and Weir is
  told.
- When Weir reports a download is gone, Deluno says so plainly and offers to
  search for another copy.

### The tray
- The tray dot shows Deluno's own health: green, amber when something it
  connects to is not answering, red when Deluno is down, blinking while it
  starts. The menu matches Weir's.

### Everywhere
- Screens that open just as something changes now catch that change.
- Release lookups no longer spend GitHub's hourly allowance, and when a check
  cannot run Deluno says so instead of claiming you are up to date.
- Several problems with the same cause each name their own item, so nothing is
  said twice.

## 1.0.0-rc.30

Deluno is early. It is still being tested hard and it is not finished. Expect
some rough edges, and please do not point it at the only copy of anything you
cannot replace. Nothing runs on its own until you switch it on, and everything
Deluno does is written to Activity. If something looks wrong, confusing or
just ugly, tell us at https://github.com/jampat000/Deluno/issues: every report
gets read. This notice is at the top of every release.

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### The right logo everywhere on Windows

- The installer, the tray icon, the taskbar, Alt-Tab and the Start menu now
  show the current Deluno logo, the same one as in the app. They had still
  been showing the old one.

## 1.0.0-rc.29

Deluno is early. It is still being tested hard and it is not finished. Expect
some rough edges, and please do not point it at the only copy of anything you
cannot replace. Nothing runs on its own until you switch it on, and everything
Deluno does is written to Activity. If something looks wrong, confusing or
just ugly, tell us at https://github.com/jampat000/Deluno/issues: every report
gets read. This notice is at the top of every release.

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### A README and a licence for newcomers

- The README is rewritten for someone trying Deluno for the first time: what
  it does, how to install it on Windows or in Docker, a worked example of a
  shared folder, and screenshots of the current screens.
- Deluno is now free software under the GNU Affero General Public License
  (AGPL-3.0), the same as Weir. The licence is in the LICENSE file. You can
  use it, change it and share it; if you share a changed version, or run one
  for other people over a network, you share your changes too.
- There is a SECURITY.md that says how to report a security problem
  privately through GitHub, rather than in a public issue.

### Works with Weir release candidates

- Deluno finds, installs and offers updates for a Weir that publishes release
  candidates, and takes the newest release by its version order, so a newer
  candidate is no longer passed over.

### Faster checks

- The checks that run behind every change are quicker, so fixes reach you
  sooner.

### A safer release process

- Releases go through more checks before they are published, including one
  that stops private details from leaking into files that anyone can read.
  Every release now carries the early-days notice above.

## 1.0.0-rc.28

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Lists

- Activity now sorts by Title, What happened or When across your whole
  history, not just the page on screen. Click a heading to sort by it, and
  click it again to reverse the order. Newest first is still the default.
- Delivery history on Settings › Notifications sorts and rearranges its
  columns like every other list, with the same Columns button.

### Filters

- System › Audit timeline filters with the same chips and picker as every
  other page, in its card's title line: a category chip with a count, a
  Severity picker and a search. The number of events is the card's count.
- The Dashboard's Health areas use the same chips too. Each area keeps its
  red, amber or green dot and how many of its checks pass. Point at an area
  for the full count.

### Fixes

- A manual import that failed is now listed on Activity under Failed, so the
  count on the sidebar can be cleared from the page it opens.
- Four API addresses that nothing used are removed. The stats address that
  dashboards such as Homepage or Homarr read stays, and is now documented
  for outside tools.

## 1.0.0-rc.27

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Your library on a smaller window

- The buttons that pick a library now sit in Your library's title line, so
  the posters get the room underneath.
- On a smaller window each poster keeps a short status under it: a gold dot
  and "Met" when it is at the quality you want, a green dot and "Upgradable"
  when it can still be upgraded. Point at a poster for the full details.
- The row of Speed, Working on now and Next can get a little shorter on a
  short window so the rest fits.
- Your library's title no longer has a dot before it, so it lines up with
  the other cards.

### Every page's tabs and buttons sit on its title line

- A page's tabs sit beside its title, and the ones that don't fit go under
  a More menu. The tab you're on always shows.
- A page's own buttons (New indexer, Refresh, Manual import and the rest)
  sit at the right of the title line too, so no page spends a row on them.
  On a smaller window they move back into a row under the header.
- The title line's buttons, search and filters now sit level with the page
  title, not a little above it.

### Activity is one list

- Activity is one list of what happened to each title, with All, In
  progress, Done, Failed and Needs a decision across the top, each with its
  count, and Any step and Any time beside them. It no longer has three tabs.
- Every number you click on the Dashboard opens Activity on the matching
  filter. The Pipeline's download stations still open Transfers.
- Jobs that aren't about a title, such as library-wide refreshes and
  backups, are on System's Background jobs, where a failed one can be
  retried or dismissed.
- Processor hand-offs, download-health issues and what your download
  clients report are under Any step: Processing, Download health and
  Download client.
- Rows say what happened in words, never a stored code.

### One look for every filter

- Filters are one row of chips with counts, the same everywhere, and sit
  in the header of the card they filter: Indexers, Profiles, Automation,
  Size Rules, History, Choose a release and Search results.
- A card no longer repeats the tab you clicked. Indexers says "12 of 12
  healthy" and Tags says "14 tags" where they used to say Indexers and Tags.
- Discover's Movies and TV are filter chips beside the title, and Discover
  and Schedule remember what you last chose.
- Transfers' Recent activity card is replaced by a link to Activity, which
  lists the same things with filters.
- The hidden Wanted episodes page is gone; its address opens TV Shows.

### Lists you can arrange

- Every list's columns can be dragged into a new order, and clicking a
  heading sorts by it; click again to reverse. Deluno remembers both for
  each list in your browser, and the Columns button on the card puts them
  back.
- Lists where the order means something, such as episodes by season or the
  ranked releases for a title, can be rearranged but not re-sorted.
- Indexer History sorts the whole history, not just the page you can see.

### Know when your apps have an update

- Every download client, media server, Seerr and processor shows the
  version it is running, and the newer version when one is out, for example
  "4.6.7 · 5.2.4 available".
- A new version is a notice in the bell. Its connection shows the release
  notes and, for Weir, qBittorrent and SABnzbd that Deluno installed on this
  computer, Update now: Deluno checks the installer, updates the app,
  restarts it and checks the version again. Anything else gets a short
  how-to.
- The notice clears itself once the new version is running, or Dismiss
  this version clears it until an even newer one comes out.

### Collections

- Each film in a collection you follow can be excluded, so Deluno never
  adds or searches for it, and included again later.

### Activity

- A case under Needs a decision can be decided from its panel in Activity,
  with the same buttons as the bell.

### Tidied up

- Fifty server routes nothing used are gone, along with the code behind
  them, and a few links that led to old addresses now go straight to the
  right page.
- Notification settings no longer list switches that did nothing, and the
  ones that remain are saved, so a restart no longer resets them.

### Fixed

- The account menu at the bottom of the sidebar no longer catches clicks
  meant for the page while it is closed.
- Dropdown arrows sit inside their boxes, and a row of dropdowns stays on
  one line.
- Season Pass is the same height as the buttons beside it.

## 1.0.0-rc.26

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Deluno and Weir look the same

- Text, the header and the sidebar now match Weir's, so switching between
  the two apps moves nothing. Nothing grows on a very wide screen any more.
- The sidebar has a collapse button. Collapsed, it is a narrow strip of
  icons with their names on hover, and Deluno remembers your choice.
- The Live and System tabs, and every card heading, sit in the same place
  in both apps.

### Live

- The Pipeline has a new look: posters without artwork show the title's
  initials, a stuck download is edged amber and a failed one red, and an
  imported poster arcs into Your library.
- Your library is one shelf of today's arrivals with buttons to pick a
  library. Each poster shows its library and, under it, whether the quality
  you want was met or it can still be upgraded, its size and when it came in.
- On a tall window the spare space goes to bigger Pipeline cards.
- Activity shows only whole entries, with a link to the rest.
- The Speed chart has a scale; pointing at any graph on Live or System says
  what it was at that moment.

### Elsewhere

- A processor such as Weir is now told which film or episode a download is,
  so it can show the right poster.
- System and title pages credit TMDb and TheTVDB, where Deluno's film and TV
  details come from.

## 1.0.0-rc.25

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Live and System, rebuilt

- Live shows what Deluno is doing right now: the speed, what is running,
  what comes next, every download on its way through the Pipeline, what
  arrived today, what was turned down and why, and your indexers, download
  clients and connections.
- System shows Deluno and the computer it runs on: health checks, the
  machine's processor, memory, disks and network, your connections with a
  Test button, scheduled tasks, the log and backups.
- Both update the moment something happens, without waiting to refresh.

### Activity

- Activity opens on a History of everything that happened, which you can
  filter by title, file, kind, source, result and day.
- The browser tab shows which computer Deluno is running on.
- Russian titles from LimeTorrents now read properly.

## 1.0.0-rc.24

Deluno is not code-signed, so Windows SmartScreen shows "Windows protected
your PC" the first time you run the installer. Choose More info, then Run
anyway.

### Every screen opens at once

- Switching to the Dashboard, Transfers, Activity, Find & Download, System
  or any other screen from the sidebar now shows it straight away. On the
  test server the slowest screen went from 1.7 seconds to under a tenth of a
  second.
- A screen you have already visited opens on what it last showed and
  refreshes in the background. Nothing blanks the page while it loads.
- Screens read what Deluno already knows. They never wait on a download
  client, indexer or media server to answer.

### A new Dashboard

- The Dashboard is now four tabs across the top: Overview, Library,
  Downloads and System. Each fits on one screen.
- Overview gives one line per area, a single "Needs you" list where every
  problem has a button to fix it, and what Deluno just did.
- Library shows what you have and what Deluno is still looking for, with
  Search now on each title.
- Downloads shows what is downloading, your download clients and indexers,
  and imports that need a look, with Retry, Give up, Search again and Test
  again where they apply.
- System shows problems, this computer's CPU and memory, scheduled tasks,
  backups and free space on every drive, including network shares.
- A tab shows a badge only when something on it needs you. The count is
  the same one the sidebar's "Needs a look" shows.
- A paused SABnzbd or NZBGet can be resumed from the Dashboard.

### Smaller, cleaner databases

- Search history now keeps what the screens show (the release Deluno chose,
  why, and how many it compared) instead of every release it looked at.
  On the test server the TV database went from 1.8 GB to 22 MB.
- Something that keeps happening, such as a download waiting for its
  processor, is written to Activity once when it starts and again when it
  changes, not every few seconds.
- The first start after this update tidies the existing records and
  compacts the databases once. On a large install that start takes a little
  longer.

### Plain words everywhere

- Activity, System, Transfers and the film and show pages name every event,
  background task, what started it and what a decision was about in
  everyday words, never an internal code.
- A film's or show's search history shows the releases each search compared
  again.

### Backups

- A backup now takes a consistent copy of each database, even while Deluno
  is busy, so it always restores cleanly.

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
- The installer keeps each app's own security settings. It adds only what
  Deluno needs to connect to it, never switches a protection off, and does not
  add a password to an app that has none by default. Deluno never hands the
  passwords it stores for your apps to anything that asks. If you set up your
  apps before this version, setting them up properly is up to you: Deluno
  does not nag you about it.

### Installed apps

- The installer installs the apps you ticked and nothing more than each needs
  to start safely (only this computer, and the login qBittorrent insists on).
  Deluno connects nothing by itself. Connect them from the setup guide or from
  Connections: a Connect button per app, or Connect all.
- Weir asks for your own account there. If Weir has none yet Deluno creates it
  with the username and password you type, otherwise it signs in with them; it
  uses them once to link Weir and Deluno and does not keep them.
- Apps keep their own defaults. No screen or API ever shows an installed app's
  password, and Deluno now deletes any Weir login an earlier version stored.
- Deleting the qBittorrent connection no longer loses its login, and a
  connection is matched by port as well as address.
- Every installer that runs with administrator rights runs from a fresh, locked
  folder, and each download is checked by its signature or checksum first.

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

### TV

- A season that is still airing is trimmed one episode at a time: new episodes
  are fetched on their own, and a pack is never downloaded again to bring back
  episodes you removed. Saving a rule no longer waits for a running check.
- An episode with no air date counts as Missing and is searched; only an
  episode with a future air date is Upcoming.
- A show Deluno stopped wanting episodes of no longer reads Missing, on the
  ladder, the catalogue counts or the show page. Your own Search of a season
  still takes packs even if you unmonitored an episode yourself, while
  automatic searches leave packs alone.
- An episode whose file is gone is cleared, marked missing and unmonitored in
  one step.
- When TheTVDB is down, every show it holds appears in one Health note and one
  Activity line that you can clear. Choosing the right TheTVDB or TMDb record on
  purpose is never blocked by that hold.

### Retention

- Rules can look at audio language, each person's watching, a whole
  collection, smart collections, quality profile, monitored, show type,
  finales, missing episodes and keep-the-newest-N. Where the server's data is
  partial, a rule says "not known" and never removes on a guess.
- A rule can choose what it does on the day, wait while a title is being
  played, keep its list off your media server, take titles you add by hand,
  and postpone a removal by a number of days. Deluno checks every connected
  media server for something playing before it removes a title.
- Nothing upcoming is ever removed. An episode or show with a future air date
  is shown as Upcoming (the word already used for movies), and retention never
  touches it. An episode with no air date at all counts as Missing and is
  searched for, so it is not hidden as Upcoming. A running show whose next
  season has no dates yet is never removed whole by a Show rule, and your media
  servers are told only about the episodes that were removed.
- A rule that is queued to run uses the rule as you last edited it, and one
  saved as delete starts its wait again before any check can run.
- Retention waits when a film is playing on a media server Deluno does not read
  and cannot tell apart from the one being removed.
- A rule whose test cannot be answered, such as "is not" on a value Deluno
  does not know, leaves the title alone and shows it as undecided in the
  preview instead of treating it as a match.
- Before a removal Deluno checks again that the title is not being played. If
  it cannot rule out that the playing title is the one being removed, it waits
  for the next run.
- A Plex "watched by nobody" or zero-views rule only decides when plex.tv
  confirms your server is not shared with anyone. Otherwise it leaves titles
  alone.
- Rules now follow a tag rename, and a tag a retention rule uses cannot be
  deleted: the Tags page shows which rule uses it.
- Retention rules see every title that has a file, monitored or not, and
  confirming a title you added by hand removes it as you asked.

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
- Deleting a film's file, from a title's Files tab or outside Deluno, clears
  the file and makes the film Unmonitored in one step, so it shows under
  Unmonitored and Deluno does not go looking for it again. A film you
  deliberately left at Quality met is no longer flipped back to Upgradable, and
  films that were flipped are put back. A check never unmonitors a film that is
  in the middle of an upgrade, and a download you re-run outside Deluno always
  searches.
- A film in two libraries: deleting its file in one library stops Deluno
  wanting it only in that library. The other library's copy stays monitored and
  keeps being upgraded. The film page's Libraries card marks the library it is
  no longer wanted in and offers Want it again, and the catalogue card and
  calendar show the film as not monitored there. Importing a file into that
  library, or watching the whole title again, wants it there again. Monitoring
  a film that is already monitored no longer brings back a library you stopped
  wanting it in.
- If a library folder is empty, holds only system folders such as lost+found,
  or a share drops out mid-check, Deluno treats the library as unreachable and
  changes nothing: no title is marked missing or unmonitored, and one note you
  can clear says so. The note clears itself when the files are back. If the
  files really are gone, choose "These files really are gone". Rescan folder
  asks first and says exactly what it did, and the scheduled library check
  follows the same rule. One missed sighting never unmonitors a title.
- A film's Files tab shows every library's file and deletes exactly that
  library's copy. A film filed straight in the library folder lists and deletes
  only its own files, and links that lead out of the folder are refused.
- Renaming or moving a title's folder takes the title's remembered file paths
  with it, so the next check does not think a file is gone. If the records
  cannot follow the folder, Deluno puts the folder back or says where it is,
  records it and carries on; a move to another drive gives a plain reason.
  Folder names with letters such as an accented E match correctly.
- Rename folder and Move only ever touch a title's own folder. A library
  folder, a whole drive, a folder shared with other titles or a group folder is
  refused with a plain reason. Moving a title to another library moves only its
  own folder, and a loose file goes into a new Title (Year) folder.
- A library's downloads folder now shows on the library screen with a folder
  picker, and saving a library never wipes it. The folder check gives up after
  a few seconds with a plain message and Check again, instead of spinning.

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
  It has a minimum size setting you can change, with help about samples and
  trailers.
- Deluno only shows and acts on downloads it sent. A download you add to your
  download client yourself is invisible to Deluno: not listed, counted,
  cleaned, paused, resumed, rechecked or touched on Transfers, Queue, Health,
  clean-up or the API, and another tool's leftovers in a shared folder are not
  listed. Deluno never claims a download by its name alone. Its own downloads
  can still be cleaned up after their record is archived.
- Every torrent is known by its hash before it is sent: Deluno works the hash
  out from the magnet link, or fetches the .torrent file and hashes it itself,
  and a grab is reserved first so a second grab of the same release follows the
  first instead of doubling up. Transmission torrents are followed by their
  hash instead of the session's number.
- When your download client already has a release that came from somewhere else,
  Deluno leaves it alone, fails that release with a plain reason and tries
  another. A release you chose yourself fails with its reason shown and nothing
  else is searched. Every search that lets Deluno choose the release blocklists
  a dead one and searches again. A release sent from the Search page now has its
  own download record.
- A download client's webhook can no longer change which item a download
  belongs to.
- The Force Import minimum size is a setting; leaving the "Smallest file to
  force" box empty saves the 100 MB default it says it will use. An unreadable
  file that looks like a sample is no longer force-imported.
- Transmission and Deluge downloads now go to the library's own downloads
  folder on every grab, and the folder check compares where a download really
  went. Deluno names a folder to a Transmission or Deluge on this computer only
  when it can show the client sees the same folders; one in Docker or WSL is
  not sent a Windows folder, and one that did not answer is asked again. Saving
  a library with no downloads folder leaves it alone.
- qBittorrent can choose where a download's files go. Leftover clean-up has
  Run now and a skip list. Processors can translate folder paths for one in
  Docker or on another machine.

### Connection names

- Connections are named for you, from the app and the computer, such as
  qBittorrent on RIG, with the port added when two of the same app share a
  computer. Indexers are named after their site. The Name box is gone from
  every connection form, and the name Deluno shows for itself is the
  computer's own name.
- Indexers behind Prowlarr or Jackett are named after their tracker, as the
  aggregator reports it, and no longer share its host name. The first-run guide
  and the processor form no longer ask for a name.
- Renaming a connection can no longer break anything: libraries, hand-offs to
  a processor and indexer history now follow a connection, not its label.
  Changing a connection's address or key still stops it working and shows on
  Health until you fix it.

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
- If TheTVDB does not answer, or sends a list that does not match the show's
  episodes, the show's episodes stay as they are and Deluno tries again next
  refresh. Activity says so.
- A site login is only ever sent to that site, a download served from another
  address is fetched without it, response sizes are capped, and a broken
  downloaded site file never replaces a working one.
- A background job that fails no longer stops the whole app: the failure is
  logged and shown on Health, where you can clear it.

### Reliability

- If Deluno stops itself after a failure, the Windows service restarts it after
  a minute and the tray starts it again, saying so in plain words if it cannot.
  Older installs get the restart settings when they start, and Health tells you
  if Windows did not accept them. Choosing Stop in the tray still stops it for
  good.
- No background job ends for good on a timeout or a slow start.
- Deluno waits up to two minutes for a locked database at start-up instead of
  failing on the first busy answer.

### Upgrading from rc.22

- Connections are named automatically from their address the first time you
  start rc.23. Indexers from a tracker definition, or behind Jackett or
  Prowlarr, take their new name at start-up, and their old grabs, stats and
  history stay with them.
- Transmission downloads Deluno already tracks move to their hash the first
  time Transmission is polled. Nothing needs doing.
- Downloads you added to your clients yourself stop appearing in Deluno.
  Nothing is removed from the clients.

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
