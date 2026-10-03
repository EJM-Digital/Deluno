# Live test: rc.28 from a fresh install (3 October 2026)

The owner, 3 October 2026: "start from a fresh install I think would be the ultimate test and anything found gets logged
as an issue in the backlog", "we have to replicate it from a users perspective", and "testing needs to be done
properly end to end with different ranges of quality titles and a wide date range of titles and sizes".

This replaces the lab-driven [E2E-full-product-test.md](E2E-full-product-test.md) for this run. No fixture indexer,
no `deploy-lab.ps1`, no pre-made settings: a person installs the released build and uses it.

## Rules for the run

- **Findings:** every finding becomes a GitHub issue before it is fixed, with what was done, what happened and what
  should have happened, a screenshot, and the row number below ([[feedback-deluno-verify-then-log]]). A failed row
  records its issue number in the Outcome column.
- **Fixes wait:** nothing is fixed during the pass unless it blocks the rest of the run. Fixes are batched after it.
- **Folders:** the documented layout (the owner, 24 Sep 2026; `scripts/lab/provision-rig.ps1`), emptied for this run
  but kept:
  - finished downloads go to `C:\Downloads\Completed\Movies` and `\TV`;
  - each client downloads into `D:\Incomplete\<client>` (SABnzbd, NZBGet, Deluge, Transmission, qBittorrent);
  - Weir works in `E:\Weir\Temp\Movies|TV` and writes to `E:\Weir\Ready\Movies|TV`;
  - the libraries are the test share's `\\nas\media\Deluno-E2E-20260915-main-78a8287d\Movies` and `\TV`,
    emptied.
- **Media boundary:** the real `\\nas\media\Movies` and `\TV` are never chosen, never imported, never
  written.
- **Weir:** Weir is not installed by Deluno's picker. Weir 4.0.0 is already on the rig (the Weir session installed it).
- **Copyright:** not a concern for title choice ([[feedback-deluno-copyright-not-a-concern]]).
- **Done:** "done" means every row has an outcome; an untested row is said to be untested.

## Phase 0: the machine before the install

| # | State | Outcome |
|---|---|---|
| 0.1 | The lab Deluno is gone: no "Deluno Host" task, no `C:\Deluno\App` or `\Data` | |
| 0.2 | qBittorrent, SABnzbd, Deluge, NZBGet and Transmission are uninstalled, with their settings and downloads deleted | |
| 0.3 | Weir 4.0.0 runs (task "Weir", port 9347); `E:\Weir\Temp` and `E:\Weir\Ready` are empty | |
| 0.3a | The documented folders on C:, D: and E: exist and are empty, and so are the test share's Movies and TV | |
| 0.4 | No pending Windows restart | |

## Phase 1: install, as a user

| # | Do | Must be true | Outcome |
|---|---|---|---|
| 1.1 | Download `Deluno-rc-Setup.exe` from the rc.28 release and run it | SmartScreen shows "More info, Run anyway"; nothing else blocks it | |
| 1.2 | First run | One firewall prompt, then the component picker. Each component shows what it is and its licence | |
| 1.3 | Tick qBittorrent, SABnzbd, Deluge, NZBGet, Transmission; leave Weir unticked | Each downloads, passes its checksum or signature check, installs, starts and is wired into Deluno with no typing | |
| 1.4 | The browser opens on Deluno | It asks to create an account, not to sign in | |
| 1.5 | Create the account | It lands on setup, not on an empty dashboard pretending all is well | |
| 1.6 | Read the Dashboard before finishing setup | Nothing automatic is on; each off thing says so (#936) | |
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

Filled in as the run goes: row, outcome, issue number.
