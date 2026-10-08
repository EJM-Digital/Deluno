# The rc.30 live test: one test per issue that does what the person did

Deluno issue 1158 (line 4) asks that every issue the rc.30 live test found has a test that does what the person did: a click-through, not only a unit test. This is the map, one row per issue, 1129 to 1157. Issue 1129 has no page on GitHub; the commits that name it are the tray and taskbar logo, and it is mapped as that.

Where a row names a browser spec, the spec is in `apps/web/tests/smoke/` and runs in `npm run test:web` with the rest. Each drives the page with real clicks, typing and drawers. Where the smoke host cannot hold the thing itself (the installer, the tray, real download clients, Weir), the spec stubs only the list the page reads (for example the installed apps, the downloads, the hand-offs) and everything the person clicks and reads is the real page. Where a row says "golden path", the issue needs the installer, real clients or Weir, so it is proved by the run in [the golden path](../../golden-path.md) at the step named, on the exact commit that will be tagged.

| Issue | What the person did | Click-through |
|---|---|---|
| 1129 | Looked at the installer, tray and taskbar icon | Golden path step 1 (install; the tray starts) |
| 1130 | Opened Download Clients after an install, read the "Connect the apps you installed" panel, pressed Connect all | `rc30-connect-panel-and-account-button.spec.ts`: "the Connect the apps panel lines up and every app shows Connected after Connect all, without a reload". Its second test, "every Connect button in the panel starts at the same left edge", is marked as a known failure: the buttons are right-aligned at their own widths, so their left edges differ, which the issue's first acceptance line says must line up. Remove the mark when the buttons line up |
| 1131 | Installed from the picker and got an old NZBGet | Golden path step 1 (the picker installs the clients; step 1 does not name the NZBGet version, so the install log's version is the thing to read). The pins are also checked by `scripts/check-component-pins.mjs` |
| 1132 | Built the guided setup with the picker's clients connected | Golden path step 3 |
| 1133 | Routed a library to the installed clients and read the folder check | Golden path step 2 |
| 1134 | Tested an indexer in its drawer, pressed Add, read the list | `rc30-indexers-clients-and-library-routing.spec.ts`: "an indexer added from its drawer keeps its test result and is searched by the library it is used for" |
| 1135 | Chose "Used for" on an indexer and connected clients, then opened Library Routing | Same file: the indexer test above, and "a connected download client reaches the library by itself, and the category paragraph is said once" |
| 1136 | Opened the Library Routing drawer with several clients | Same file, second test (the category paragraph appears once) |
| 1137 | Picked Nyaa.si in the New indexer drawer and read its settings | `rc30-built-in-tracker-words.spec.ts`: "a built-in tracker's settings read in Deluno's words" |
| 1138 | Walked a fresh install's screens | `rc30-screens-carry-no-issue-numbers-or-old-names.spec.ts`: opens every owner screen and refuses issue numbers, "moved" notes and old page names. Golden path step 11 repeats it on the installed build |
| 1139 | Opened New quality profile, read the templates, made a custom TV profile | `rc30-quality-profile-templates.spec.ts` |
| 1140 | Ran the picker and waited for progress and a finish button | Golden path step 1 (the picker shows progress and a finish screen) |
| 1141 | Walked the guided setup: sidebar, connecting address, Other devices, service-account wording | `rc30-guided-setup-walk.spec.ts` covers the sidebar, the address and the Other devices result. The service-account wording on a tray install is golden path step 3 |
| 1142 | Opened a Usenet client's drawer to find its login and enter a provider | Golden path step 5 (the provider is entered once; the clients show Healthy only after it is set) |
| 1143 | Picked 1337x, followed the Cloudflare note, tested through FlareSolverr | `rc30-built-in-tracker-words.spec.ts`: "the Cloudflare note on a built-in tracker names a page that opens, one click away" for the page. That the test now gets the long budget is golden path step 4 (1337x works through the Cloudflare helper) |
| 1144 | Pressed Connect Weir and read how it is named | Golden path step 3 (Connect Weir) |
| 1145 | Added a film, chose its library and profile, and left the search box unticked | `rc30-add-a-title-and-its-own-profile.spec.ts`: "adding a film lets you choose its profile, and an unticked search box means no search" |
| 1146 | Opened Movies and saw no posters | `rc30-posters-load-without-a-sign-in-header.spec.ts` asks for the artwork address the way an image tag does, with no sign-in header. Golden path step 6 (posters show) on the installed build |
| 1147 | Sent several torrents to equally ranked clients | None. Backend tests only. No golden path step sends more than one torrent, so none can show the clients taking turns |
| 1148 | Downloaded a 4:3 film with the integrity check on | Golden path step 6 (the 4:3 film) |
| 1149 | Looked at the sidebar's account button | `rc30-connect-panel-and-account-button.spec.ts`: "the sidebar account button shows the person's name and nothing that repeats it" |
| 1150 | A download was handed over while the client was still moving it | Golden path step 6 (hand-off with the completed path) |
| 1151 | Opened a download whose hand-off failed, tried to send it again, read Activity | `rc30-failed-hand-off-is-fixed-from-the-download.spec.ts`: the first two tests. Golden path step 8 (Try again from the download's drawer) on the installed build |
| 1152 | Watched the log grow on an idle install | None. It has no screen action: backend and `log-files-card.test.tsx` only. No golden path step reads Deluno's own log size |
| 1153 | A processor rejection said "and removed it" when it had not | None. The wording is a backend message; no golden path step has Weir refuse a file. Step 8 is the nearest |
| 1154 | A finished download waiting on a failed hand-off was struck and deleted | Golden path step 8 (a failed hand-off sits under Needs action and is never deleted by the stall rules) |
| 1155 | Sent a hand-off again for a download already cleaned | `rc30-failed-hand-off-is-fixed-from-the-download.spec.ts`: "a download the processor already cleaned offers no second send". Golden path step 9 for the real second send |
| 1156 | Hovered a poster and pressed Search | `poster-actions-are-clickable.spec.ts`: "clicking a poster's Search icon runs the search and does not open the title" (already existed) |
| 1157 | Gave a film its own profile in Edit and searched | `rc30-add-a-title-and-its-own-profile.spec.ts`: "a film given its own profile in Edit keeps it, and can go back to following its library". Golden path step 7 for the search targeting that profile |

## Where this leaves the issues

- Already had a click-through before this map: 1 (1156).
- Now have a browser click-through: 1130, 1134, 1135, 1136, 1137, 1138, 1139, 1141, 1143 (the page it names), 1145, 1146, 1149, 1151, 1155 (the screen) and 1157, in 9 new spec files.
- Golden path only: 1129, 1131, 1132, 1133, 1140, 1142, 1144, 1148, 1150 and 1154.
- Neither a click-through nor a golden path step: 1147, 1152 and 1153. Each needs a step added to the golden path (more than one torrent in step 6; Deluno's log size; a processor that refuses a file) or a decision that backend tests are enough. `docs/golden-path.md` is the same word for word in the Weir repository, so a step is added in both.

## Keeping the map true

A new issue found by a live test gets a row here, in the same fix that closes it. A spec that stubs a list says so in its opening comment. Do not turn a click into an API call to make a spec pass: the click is the thing being proved.
