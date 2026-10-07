# Live test: rc.28 from a fresh install (3 October 2026)

James, 3 October 2026: "start from a fresh install I think would be the ultimate test and anything found gets logged
as an issue in the backlog", "we have to replicate it from a users perspective", and "testing needs to be done
properly end to end with different ranges of quality titles and a wide date range of titles and sizes".

This replaces the earlier lab-driven end-to-end plan. No fixture indexer,
no `deploy-lab.ps1`, no pre-made settings: a person installs the released build and uses it.

## Rules for the run

- **Findings:** every finding becomes a GitHub issue before it is fixed, with what was done, what happened and what
  should have happened, a screenshot, and the row number below ([[feedback-deluno-verify-then-log]]). A failed row
  records its issue number in the Outcome column.
- **Fixes wait:** nothing is fixed during the pass unless it blocks the rest of the run. Fixes are batched after it.
- **Folders:** the documented layout (James, 24 Sep 2026; `scripts/lab/provision-rig.ps1`), emptied for this run
  but kept:
  - finished downloads go to `C:\Downloads\Completed\Movies` and `\TV`;
  - each client downloads into `D:\Incomplete\<client>` (SABnzbd, NZBGet, Deluge, Transmission, qBittorrent);
  - Weir works in `E:\Weir\Temp\Movies|TV` and writes to `E:\Weir\Ready\Movies|TV`;
  - the libraries are the test share's `\\nas\media\Deluno-E2E-20260915-main-78a8287d\Movies` and `\TV`,
    emptied.
- **Media boundary:** the real `\\nas\media\Movies` and `\TV` are never chosen, never imported, never
  written.
- **Weir:** Deluno's component picker installs Weir too (James, 3 Oct: a true end-to-end test). The old Weir was
  retired (task disabled, `C:\Deluno\Weir3` renamed `Weir3-retired-20261003`, data backed up), and Setup waits
  until Weir 4.0.0 is published, because the picker takes the latest Weir release. The Weir session live-tests the
  Weir that Deluno installs.
- **Copyright:** not a concern for title choice ([[feedback-deluno-copyright-not-a-concern]]).
- **Done:** "done" means every row has an outcome; an untested row is said to be untested.

## Phase 0: the machine before the install

| # | State | Outcome |
|---|---|---|
| 0.1 | The lab Deluno is gone: no "Deluno Host" task, no `C:\Deluno\App` or `\Data` | |
| 0.2 | qBittorrent, SABnzbd, Deluge, NZBGet and Transmission are uninstalled, with their settings and downloads deleted | |
| 0.3 | No Weir runs (port 9347 free); Weir 4.0.0 is the latest published release; `E:\Weir\Temp` and `E:\Weir\Ready` are empty | |
| 0.3a | The documented folders on C:, D: and E: exist and are empty, and so are the test share's Movies and TV | |
| 0.4 | No pending Windows restart | |

## Phase 1: install, as a user

| # | Do | Must be true | Outcome |
|---|---|---|---|
| 1.1 | Download `Deluno-rc-Setup.exe` from the rc.28 release and run it | SmartScreen shows "More info, Run anyway"; nothing else blocks it | |
| 1.2 | First run | One firewall prompt, then the component picker. Each component shows what it is and its licence | |
| 1.3 | Tick qBittorrent, SABnzbd, Deluge, NZBGet, Transmission and Weir | Each downloads, passes its checksum or signature check, installs, starts and is wired into Deluno with no typing; Weir is 4.0.0, checked against its SHA256SUMS | |
| 1.4 | The browser opens on Deluno | It asks to create an account, not to sign in | |
| 1.5 | Create the account | It lands on setup, not on an empty dashboard pretending all is well | |
| 1.6 | Read the Dashboard before finishing setup | Nothing automatic is on; each off thing says so (#936) | |
| 1.6a | Open Transfers and the Dashboard within seconds of Deluno starting, before it has read the download clients once | They say they are still reading the clients, never "no downloads" or an empty client list, and fill in when the first reading arrives (a screen waits at most 1.5 s for that first reading) | |
| 1.7 | Reboot the rig | Deluno and the five clients come back by themselves, with no one signed in to Windows | |

## Phase 2: setup, as a user

| # | Do | Must be true | Outcome |
|---|---|---|---|
| 2.1 | Movies library at `\\nas\media\Deluno-E2E-20260915-main-78a8287d\Movies` (empty) | Saves; an empty library says so and what to do next | |
| 2.2 | TV library at `...\Deluno-E2E-20260915-main-78a8287d\TV` (empty) | Same | |
| 2.3 | The NAS share as the installed user (on 3 Oct the rig's Administrator has no saved NAS login) | Deluno can read and write it, or says exactly which account needs access and how to give it | |
| 2.4 | Each setup question that turns on automatic behaviour | Asked as a plain Yes/No; answering No leaves it off | |
| 2.5 | Quality: choose the plain choices (no numbers) | One-sentence reasons; TRaSH points stay behind them (#938) | |
| 2.6 | Indexers: add the public trackers and the Usenet indexers (nzbgeek, nzbplanet, althub) | Each tests green or says why in words | |
| 2.6a | A tracker behind Cloudflare (e.g. 1337x) | Deluno says it needs a FlareSolverr proxy and how to add one; after a user installs FlareSolverr and adds it, the tracker tests green. (The lab's FlareSolverr was removed on 3 Oct for the fresh start.) | |
| 2.7 | Connect Weir as the processor for Movies and TV, refine before import | Weir's version shows beside its name; Weir says which folder it watches | |
| 2.8 | Routing: torrents and Usenet each to more than one client (see the client column below) | Saved; each library shows where its downloads go | |
| 2.8a | Client folders | Each installed client saves to `C:\Downloads\Completed\<category>` and downloads into `D:\Incomplete\<client>`, or setup lets these be chosen | |
| 2.9 | Folders end to end | Every client save path, Weir input and output and library folder agree, and Health checks each one ([[feedback-deluno-folders-accounted-end-to-end]]) | |

## Phase 3: the title matrix

Each row is added the way a user would (search, add, choose monitoring), and then followed through:
**search → choose → send → download → hand to Weir → Weir cleans → import → rename → on disk → in the library →
Activity tells the story**. "Profile" is the quality the title is added with. Sizes are the expected release size.

### Movies

| # | Title (year) | Era | Profile | Size | Protocol / client | What it proves | Outcome |
|---|---|---|---|---|---|---|---|
| M1 | Nosferatu (1922) | 1920s, silent | 720p | 1–3 GB | torrent / qBittorrent | very old title, mono or no audio, odd frame rates | |
| M2 | Night of the Living Dead (1968) | 1960s | 1080p | 4–8 GB | Usenet / SABnzbd | black and white, many public-domain releases with odd names | |
| M3 | Alien (1979) | 1970s | 2160p remux, HDR | 50–80 GB | torrent / Deluge | largest size, HDR10, TrueHD Atmos, long hand-off and import | |
| M4 | The Terminator (1984) | 1980s | 1080p | 8–15 GB | Usenet / NZBGet | common title (remakes, sequels) chosen correctly by year | |
| M5 | Pulp Fiction (1994) | 1990s | 1080p x265 | 4–10 GB | torrent / Transmission | HEVC; a client that rarely gets used | |
| M6 | The Matrix (1999) | 1990s | 2160p WEB or UHD, x265 HDR | 15–30 GB | torrent / qBittorrent | 4K at moderate size; franchise name matching | |
| M7 | Spirited Away (2001) | 2000s, anime film | 1080p, dual audio | 5–12 GB | Usenet / SABnzbd | non-English original title, multiple audio and subtitle tracks | |
| M8 | Children of Men (2006) | 2000s | 720p, then upgrade to 1080p | 3–6 GB, then 10–15 GB | torrent / qBittorrent | upgrade: the 720p file is replaced, the old one goes to the recycle bin | |
| M9 | Mad Max: Fury Road (2015) | 2010s | 2160p Dolby Vision | 20–60 GB | Usenet / NZBGet | Dolby Vision profile handling; colon in the title (rename) | |
| M10 | Oppenheimer (2023) | 2020s | 1080p | 10–20 GB | torrent / Deluge | 3-hour runtime, IMAX releases | |
| M11 | Dune: Part Two (2024) | 2020s | 2160p WEB-DL | 15–30 GB | Usenet / SABnzbd | "Part Two" naming; recent big release | |
| M12 | A 2025 release still in cinemas or just on streaming | 2025 | 1080p | any | either | not yet out, or out only as cam or TS: must not grab junk; waits correctly | |
| M13 | A DVD-only title (e.g. a TV movie with no Blu-ray) | any | SD / 480p allowed | 0.7–2 GB | torrent | smallest size; SD quality path; low seeders | |
| M14 | A title with almost no releases | any | 1080p | n/a | either | nothing found: says so in words and keeps waiting, no false failure | |

### TV

| # | Show | Era | Profile | Size | Protocol / client | What it proves | Outcome |
|---|---|---|---|---|---|---|---|
| T1 | The Twilight Zone (1959), season 1 | 1950s | 1080p | 25–40 GB pack | torrent / qBittorrent | full season pack; black and white; old air dates | |
| T2 | Seinfeld (1989), two single episodes | 1980s–90s | 1080p | 1–2 GB each | Usenet / SABnzbd | single-episode grabs; long-running show | |
| T3 | The Wire (2002), season 1 | 2000s | 1080p | 20–40 GB | torrent / Deluge | 4:3 vs 16:9 remaster releases | |
| T4 | Chernobyl (2019), miniseries | 2010s | 2160p | 20–40 GB | Usenet / NZBGet | miniseries; 4K TV | |
| T5 | Severance (2022), season 2 | 2020s | 2160p WEB-DL HDR | 30–60 GB | torrent / Transmission | streaming source, HDR, recent season | |
| T6 | A show airing now, monitored for future episodes | 2025–26 | 1080p | 1–3 GB/ep | either | a new episode is found and grabbed by itself after it airs | |
| T7 | An anime series with absolute numbering (e.g. Frieren) | 2020s | 1080p | 0.5–1.5 GB/ep | torrent / qBittorrent | absolute vs season numbering; fansub release names | |
| T8 | A daily show (e.g. The Daily Show), two dates | date-based | 720p | 0.3–1 GB | Usenet / SABnzbd | date-based episodes match the right date | |
| T9 | A show with specials (Season 0) | any | 1080p | any | either | specials are not mistaken for regular episodes | |
| T10 | Multi-episode file (S01E01E02) | any | 1080p | any | either | one file, two episodes, named and counted right | |

## Phase 4: behaviour around the matrix

| # | Do | Must be true | Outcome |
|---|---|---|---|
| 4.1 | Turn on automatic upgrade-hunting for one movie and one show | An owned lower-quality file is replaced by itself once a better one exists (#639) | |
| 4.2 | Force a failed download (remove a torrent's data mid-way, or a broken NZB) | It is noticed, explained in words, blocklisted and searched again per the clean-up rules | |
| 4.3 | Send Weir a file it refuses | Shown under Needs a decision with words, and decided from the drawer | |
| 4.4 | Manual import of a file dropped into the downloads folder | Same resolver and naming as an automatic import | |
| 4.5 | Delete a movie with its files | Goes to the recycle bin, and can be got back | |
| 4.6 | Update now from a connection's notice (if a newer client is out) | Runs only on click, says what it did, re-checks the version | |
| 4.7 | Leave it running overnight | Next morning Dashboard, Transfers, Activity and System are clean: every counted problem is visible and clearable ([[feedback-deluno-ready-means-screens-clean]]) | |

## Phase 5: every screen, at the end

Walk each sidebar page and tab with real data at 1920, 1366 and a phone width: nothing cut, nothing repeated, every
count clearable, every filter on the standard control, sorting and dragging on every list. Each defect is filed as an issue.

## Results

Filled in as the run goes: row, outcome, issue number. Build under test: **1.0.0-rc.30** (the logo fix, #1129), Weir
1.0.0-rc.1, on the rig cleaned on 3 Oct; installed by James over RDP as the built-in Administrator on 7 Oct 2026.

| Row | Outcome | Notes |
|---|---|---|
| 0.1, 0.2 | Pass | No lab Deluno and no clients before install; the installer put all five clients and Weir on. |
| 0.x (clean-up) | **Finding 1** | The 3 Oct clean-up left the lab firewall rules: four "Deluno E2E lab" rules (one still named "MediaMop Server") and an old "Deluno (port 5099)". |
| 1.1 | Pass | rc.30 Setup.exe ran from the desktop. |
| 1.2 | **Finding (partly env)** | No firewall prompt. The install log says port 7879 was already open (a "Deluno (port 7879)" rule existed), and the built-in Administrator gets no elevation prompt anyway. To prove on a machine without the rule. |
| 1.3a | Pass, with **#1130, #1131** | Nothing connects by itself (as intended). "Connect all" connected all five healthy, with categories set, but the panel is misaligned and neither it nor the table updated live (#1130). NZBGet came from the archived nzbget/nzbget at 21.1 instead of nzbgetcom/nzbget v26.3 (#1131). |
| 1.4–1.5 | Pass | The account was created on the rig (via the tray). Signing in from the workstation in Chrome works on port 7879. |
| 1.6 | Pass | The Dashboard says plainly that nothing is set up and that automation is paused; the setup ladder is 0/4. |
| 2 (guided) | Pass, with **Findings 3–7** | The guided setup's 13 steps; time and region default correctly (en-AU, Sydney). Findings: (3) the sidebar highlights System during the guided setup; (4) "you are connecting from ::ffff:<workstation address>" shows the raw mapped form; (5) "Other devices" says "Deluno checks that now" but shows no result; (6) step 10 shows a blank download-client form although five clients are connected and readiness says Downloads ready; (7) the folder check's note tells a tray install to give "the Deluno service account" share permissions. |
| 2.1–2.3 | Pass | Both E2E share folders check readable and writable. The installed Deluno runs in the Administrator session, which saved the NAS login over RDP. |
| 2.5 | Pass | Plain choices with one-sentence reasons and the numbers hidden; it previews the 10 rules it will create. |
| 2.4 (by hand) | Pass, with **#1132** | The failed guided setup (#1132) left some answers applied and others not. The search schedules (Missing and Upgrades, every 6 h) were turned on by hand for both libraries, along with the integrity check and Download Health's Dangerous file, Dead torrent and No progress rules, each set to "Replace it". |
| 2.5 (by hand) | Pass, with **#1139** | Eight profiles: Movies 1080p, 4K Remux, 4K Streaming, 720p and SD, and TV 1080p, 4K and 720p. Custom TV profiles get no formats, the TV template's name repeats "TV", and there are no TV 4K, 720p or SD templates (#1139). The UI copy still shows issue numbers and old page names (#1138). |
| 2.6 | Pass for public, with **#1134, #1137** | YTS, EZTV, The Pirate Bay and Nyaa tested healthy (EZTV fell back to eztvx.to by itself). The test result is lost on Add (#1134). Nyaa and 1337x show Prowlarr's wording (#1137). Usenet indexers wait for James's API keys. |
| 2.6a | First half passes | 1337x: "None of the 5 addresses answered… behind a Cloudflare check… A Cloudflare helper (FlareSolverr or Byparr) gets Deluno past it". FlareSolverr 3.5.2 was installed on the rig (sha256 checked; it starts at sign-in). Through Preferences → General → Cloudflare Helper, 1337x tests Healthy ("read 80 releases"). As a per-indexer FlareSolverr proxy it always times out after 45 s, and the messages name a "Settings, General" page that doesn't exist (#1143). |
| 2.7 | Pass, with **#1144** | James made the Weir account in Weir itself, then pressed Connect Weir in Deluno: connected and healthy, with Deluno registered in Weir as media manager "Deluno". In Deluno it shows as "Processor on <rig machine name>" with no Weir name or version (#1144). Weir was updated to 1.0.0-rc.2 by its tray. The rig's network was set to Private and the three old lab firewall rules were removed (James ran it). |
| 2.9 | Partly | Both libraries "Process, then import" through Weir, with processed folders `E:\Weir\Ready\Movies|TV` (checked readable and writable). James set up Weir's workflows. |
| M1 | **Fail → #1148**, then **Weir 400** | Nosferatu (1922). Added through Add movie, which offers no profile and searched at once (#1145); grabbed `Nosferatu.1922.1080p.BluRay.x264-HD4U` (1440×1080, 7.0 GB) from TPB to Deluge, not the planned qBittorrent (#1147). It downloaded into `D:\Incomplete\Deluge` and Deluge moved it to `C:\Downloads\Completed\Movies`. The integrity check failed it as "1440 pixels wide", which is how a 4:3 film looks at 1080p; it said "removed and blocklisted" every minute but did neither (#1148). With the check off and the release un-refused, Deluno posted the hand-off to Weir, and Weir refused it: HTTP 400 "Weir's watched folder is not set for this media scope". Weir's workflows were still the blank install defaults (Weir#813 had covered only Weir's own first-run screen, so a Weir connected from Deluno never gets them; Weir rc.3 adds automatic sync). The Weir session filled them in at 03:19Z.<br>Weir's folder scan then cleaned Nosferatu without any hand-off, and **deleted the .nfo and .txt from Deluge's seeding folder** (the .mkv survived only because Deluge had it open). This is a Weir release blocker, and Weir was paused. The retried hand-off was accepted, but the retry exists only in the second drawer (#1151). Deluno imported Weir's output to `Movies\Nosferatu (1922)\Nosferatu (1922) [Bluray 1080p].mkv`: **first full chain done**, with workarounds. No posters anywhere (#1146). The logs grow about 70 MB in hours (#1152). |
| M10 | **Fail → #1150** | Oppenheimer was grabbed at once, to Deluge (#1147). Deluno handed it off from `D:\Incomplete\Deluge\…` while Deluge was still moving it to the completed folder. Waiting for Weir rc.3. |
| M5 | Pass, with Weir findings | Pulp Fiction (AV1 10-bit, DS4K) went through Deluge → Weir → library as `Pulp Fiction (1994) [Bluray 1080p].mkv`. Weir rc.2 processed it twice (once while "paused"), so a spare output was left in Ready (Weir bug, fixed for rc.4). |
| — | Weir rc.3 | Installed by the tray. The firewall widened to every profile with no prompt, no folder scans, and the workflows were adopted with no duplicates. On resume, the duplicates and the deleted Nosferatu seeding folder traced to rc.2 (pause didn't pause). James decided Deluno is the main stop against duplicates (#1155) and Weir has a quiet "already done" skip. |
| M10 | **Data loss → #1154** | The finished Oppenheimer, with its hand-off failed, was "stalled for an hour" by Deluno, then blocklisted, removed from Deluge and its files deleted. A replacement search was queued. |
| M6 | **Fail → #1157, #1147** | The Matrix was set to "Movies / 4K Streaming", but the search targeted the library's Bluray 1080p and sent a 1080p UHD BluRay release, to Deluge, despite Deluge's priority 50 against qBittorrent's 10. "Search now" gave no on-page feedback. It downloaded straight into `C:\Downloads\Completed\Movies`, not `D:\Incomplete\Deluge`, once the library's downloads folder was set. |
| T3 | Partly, **→ #1159** | The Wire, first season only (Season Pass: 13 of 86). Six episodes were sent to Transmission (not qBittorrent at priority 10). Seven were "held": the best candidate was a rejected anime release from another show, so there's no retry for 6 hours. |
| UI | **→ #1156, #1146** | James: the poster hover buttons only open the title (a card-wide Open button sits on top), and there are no posters (artwork 401). |
| 4.2 | Pass (torrent) | It happened naturally: The Wire S01E09/E11/E12 (1–2 seeders) were struck at the client's "stalled", then blocked, removed from Transmission and searched again after 3 strikes. Only stalled items were touched. Contrast #1154, where the same path wrongly hit a finished download. |
| 4.5 | **Fail → #1160, #1161** | Remove with files moved Nosferatu to the recycle bin (the dialog wrongly says "cannot be undone", #1160). Restore put the folder back, but the title stayed gone, and adding it again re-downloaded it (#1161). |
| 4.6 | N/A by design, **→ #1131** | Picker clients only get Update now to a newer pinned build. Every pin is behind (SABnzbd 5.0.4 vs 5.1.3, qBittorrent 5.2.1 vs 5.2.4, Transmission 4.1.1 vs 4.1.3, NZBGet 21.1 vs 26.3), and the message gives no next step. |
| Weir rc.3 under load | Pass | Weir handled six or more hand-offs, removing subtitle tracks and reporting back. Client originals and sidecars stayed and no scans ran. Imported so far: Pulp Fiction, Children of Men, Night of the Living Dead, Spirited Away, The Terminator, The Matrix, and The Twilight Zone S01E02 and more. |
| Phase 5 (part 1) | **→ #1162** | At 2560 CSS px: Transfers cuts off 225 cells, lists hand-offs twice and repeats its column help 3 times; Media Naming repeats sentences. The rest of the walk, and the 1366 px and phone widths, wait for the Chrome window to be visible, because a hidden tab is throttled and can't be resized. |
| Usenet | **Deferred** | James, 7 Oct: this run uses public torrents only. The Usenet rows (provider login in SABnzbd and NZBGet, the nzbgeek, nzbplanet and althub indexers, M2/M4/M7/M9/M11 and T2/T4/T8 on Usenet, the NZB failure case in 4.2) run in the second, post-fix run, on the golden-path VM (#1158). Their torrent counterparts were used where they exist. |
| Process | **→ #1158** | James: regressions are unacceptable. Live proof is required before closing, a golden-path run on a clean Hyper-V VM gates every tag (a `golden-path` commit status), mirrored in Weir#903. |
| 2.8 | Pass, with **#1135, #1136** | Every indexer and client had to be switched on again per library (#1135). The routing drawer repeats the category paragraph for each client (#1136). Status "Category route" is unclear. |
| 2.8a | **Fail → #1133** | The picker's clients had no categories (Deluge's Label plugin was off), and each saved to its own default folder. Fixed by hand on the rig (backups `*.pre-e2e`): completed downloads go to `C:\Downloads\Completed\Movies\|TV` and incomplete downloads to `D:\Incomplete\<client>`. The qBittorrent and Deluge categories are `deluno-movies`/`deluno-tv`; Transmission, SABnzbd and NZBGet use `Movies`/`TV`. Folder check: every category Matches for both libraries. |
| 1.3 | Pass, with **Finding 2** | The picker appeared and all six were ticked; each passed its checksum or signature check and installed (16:59–17:00). But after the picker nothing showed progress or completion: about 1½ minutes of silence, then nothing to say it was ready or how to open Deluno. |
| 4.7 | **Fail → #1165, #1166, #1167** | The next morning, the tab had signed out after 12 h (#1165). Overnight, Twilight Zone 2019 episodes were grabbed for the 1959 show, refused at import, then grabbed again (#1166). Needs action reached 88, nearly all stalls Deluno had already handled, each counted twice as "Import failed" plus "vanished" (#1167). |
| Width walk | **Split → #1168** | James, 8 Oct: functional tests first; the 1920, 1366 and phone-width walk runs separately later. The copy findings from the part already walked went to #1138. |
| 4.1 | **Fail → #1169** | TV profile "TV — 1080p Streaming" raised to stop at WEB 2160p, then Automation & Recovery → TV → Search now. All 34 owned WEB 1080p episodes stayed "covered, cutoff met, target WEB 1080p", and nothing was searched: the stored cutoff flag is never re-evaluated after a profile change. Profile put back to 1080p. No screen changes a library's quality profile after setup. |
| 4.4 | **Fail → #1170, #1171** | Transfers → Manual import. "Choose folder" opened a Windows dialog on the rig's desktop and the button stayed disabled (#1170). With the path typed, Chernobyl S01E05 (seeding in Transmission) was matched to the show, but landed in a new `TV\<release name>\` folder as "Episode 05", even with the Title/Year hint. The copy import then **deleted the seeding .mkv** (#1171). Transmission Re-check started a re-download. |
| M12 | Pass, with **#1159** evidence | Resident Evil (2026), from Discover with "Search for missing now". All 26 candidates were refused: every CAM and TELESYNC for the cam token, the WEB copies for no seeders or undersize. The page names a refused anime series as the "best candidate" and says "held for manual review" (comment on #1159). Discover posters are missing (#1146). |
| Search page | **Fail → #1172** | "The Simpsons S38", TV: 64 results, but every row says "Rejected" with no reason, and Rejected, Block and Grab overlap in one narrow column. |
| T6 | **Fail → #1173** | The Simpsons, Latest season only, Search for missing now. E01 was sent correctly; E02 was sent "S38E02 The Childrens Book Job…FLUX", which is E01's title (mislabelled), though a correctly titled copy was there. The unaired E03–E05 were searched and "held". Both queued in qBittorrent (queue full). Calendar shows the air dates correctly. |
| T9 | Pass (search) | Specials: S00E78 "Extreme Makeover: Homer Edition", monitored alone from the episode filter (the Specials season switch then reads "on" with 1 of 74 monitored). Episode search found 4 S00E78 copies; the 1080p DSNP was sent to Transmission. Import still to check. |
| 4.3 | Seen naturally, **→ #1151, #1167** | Weir refused Oppenheimer with HTTP 400 (watched folder not set). It showed under Needs action with Weir's words, but the retry sits only in a second drawer (#1151), and the alarm stayed after Oppenheimer was imported (#1167). Nosferatu, after remove → restore → add, sits "Waiting for processor" with no hand-off sent (comment on #1155). |
| Still running | To check | Simpsons S38E02 (the mislabelled E01 copy: is it imported as E02?), S00E78 (does a special import as a special?), and Chernobyl S01E05's re-download (imported a second time?). All three Simpsons downloads sit queued behind two stalled wrong-show grabs. |
| T10 | **Deferred** | No multi-episode release with seeders on the public trackers (only a 0-seed Swedish Chernobyl E01–E02). It moves to round two, with Usenet. |
