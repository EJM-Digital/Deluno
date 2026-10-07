# Fix batch: the rc.30 live test (7–8 Oct 2026)

James, 7 Oct: finish the torrent round of the live test, then fix and close **every** issue, using as many agents as needed, "completely perfectly done". After that comes round two with Usenet and torrents, on the golden-path VM.

## The rules for every fix

- **Before touching an issue,** run `./scripts/issue-audit.ps1 <n>` and answer every acceptance line.
- **Tests do what the user does:** a real request or a Playwright click, never an attribute or label check. See [[feedback-deluno-prove-live-before-close]], recorded in #1158.
- **Nothing closes without live proof on the rig,** clicked or watched, with the evidence in the closing note. Green tests and a code read never close a user-facing issue.
- **One builder per area,** each in its own worktree. Builders never run `npm install` in a junctioned worktree. The coordinator merges onto `main`, runs `npm run gate` once for the batch, and re-runs only the affected steps after a small fix.
- **Commit messages say `Refs #NNN`.** Never put close, fixes or resolves next to a number.

## Areas and order

Data safety goes first: those defects destroy good files.

### A. Data safety: hand-offs and clean-up (backend first)
| # | What |
|---|---|
| 1154 | A finished download waiting on a failed hand-off is struck, blocklisted, removed and deleted (P0) |
| 1150 | Hand-off sent while the client is still moving the download out of incomplete |
| 1155 | Never re-send a hand-off for a cleaned or imported download. Deluno is the main stop; close leftover "working" hand-offs |
| 1148 | The integrity check fails 4:3 films (width only). Its "removed and blocklisted" message is false and repeats every minute |
| 1153 | A rejection says "and removed it" even when `sourceRemoved: false` |
| 1151 | Failed hand-off recovery: retry in the download's drawer, one button, blamed on the processor, no raw JSON |
| 1171 | Manual import deletes the file a client is seeding, and builds a show folder from the release name (P0) |
| 1167 | A stall Deluno handled itself shows as two Needs-action alarms; bulk Dismiss; the count matches the rows |

### B. Search and acquisition
| # | What |
|---|---|
| 1157 | A title's own quality profile is ignored by search, upgrades and the quality card |
| 1159 | A rejected candidate becomes the "best", so the episode is held for 6 h |
| 1147 | Client priority and ties: priority wins, and equals share work in turn |
| 1145 | Add dialog: profile and library choice; "search now" means what it says |
| 1163 | TV metadata search is slow and ranks the exact match last |
| 1166 | Same-name remakes: a different year in the release name is another show (Twilight Zone 1959 vs 2019) |
| 1173 | An episode title in the release name that belongs to another episode rejects it; unaired episodes aren't searched |
| 1169 | A profile change re-evaluates cutoff-met for every title using it, so upgrade-hunting runs |

### C. Installer, picker and setup (Windows tray plus web)
| # | What |
|---|---|
| 1133 | Picker clients set up for Deluno: categories, Label plugin, completed and incomplete roots |
| 1164 | A library downloads folder must not bypass the client's incomplete folder |
| 1140 | Picker progress and finish window, with "Open Deluno" |
| 1131 | NZBGet from nzbgetcom 26.x; pins current, with a check; not-checked message gives a next step |
| 1132 | Guided setup uses connected clients; no half-applied answers |
| 1141 | Guided setup polish (sidebar, ::ffff, other-devices result, account wording) |
| 1142 | Usenet clients: reveal or open the generated login; enter the provider once; not Healthy without a server |
| 1129 | Logo: confirm the tray and taskbar on the rig, then close |

### D. Indexers and routing
| # | What |
|---|---|
| 1134 | The drawer's test result is kept after Add; the healthy count is honest |
| 1135 | "Used for" and connected clients route to the libraries automatically |
| 1136 | Library Routing: the category paragraph is said once |
| 1137 | Tracker settings in Deluno's words, not Prowlarr's |
| 1143 | A per-indexer FlareSolverr proxy gets the long budget; one helper place, named correctly |

### E. Library, files and the recycle bin
| # | What |
|---|---|
| 1160 | The remove dialog says the files go to the recycle bin |
| 1161 | Restore brings the title back; adding again matches the existing file |

### F. Web UI, copy and polish
| # | What |
|---|---|
| 1146 | Artwork route answers anonymously (backend auth); posters everywhere |
| 1156 | Poster Search and Refresh clickable; no play glyph for Open |
| 1162 | Transfers cut-offs, a hand-off listed twice, help repeated; Media Naming repeats |
| 1138 | No issue numbers, "moved" notes or old page names; a copy check |
| 1149 | Account button shows the name only |
| 1139 | Profiles: custom TV formats, template names, TV 4K, 720p and SD templates |
| 1144 | Weir shown as Weir with its version; workflow copy names the connected processor |
| 1130 | Connect panel aligned and live |
| 1170 | Choose folder opens the in-app browser for remote devices; the native picker only on loopback, with a timeout |
| 1172 | Search page: a reason for every verdict, no "Rejected" on a free search, the actions don't overlap |
| 1165 | Sessions renew while in use; keep me signed in; the sign-in page says why and returns you |
| 1168 | The visual walk at 1920, 1366 and phone width (after the fixes, per James) |

### G. Operations and the release gate
| # | What |
|---|---|
| 1152 | Logs: HTTP and request chatter at Warning; one line per hand-off; size bounded |
| — | Release publish split (James via Weir, 8 Oct): `publish-windows` and `publish-docker` as independent jobs, each with only its own write permission; the moving tags last, never for an rc; docker `cache-from`; 30-minute Docker timeouts; the gate check enforces the shape. Copy Weir's ci-standard.md from `origin/release/1.0.0-rc.5` (sha256 `13c293ef60414bdb`) in the same commit (it already carries the Trivy HIGH,CRITICAL rule 9 that is uncommitted here) |
| 1158 | The golden-path gate: a release.yml `golden-path` job, `scripts/verify-golden-path-for-release.mjs`, manual-CI installers, the picker's local-Weir override, `docs/golden-path.md` byte-for-byte with Weir (recopy after Weir#907), ci-standard.md wording, the clean Hyper-V VM |

### James's own
| # | What |
|---|---|
| 1128 | Go public, after GitHub's purge confirmation |

## Still to finish in this round before fixing starts

- The overnight check (plan row 4.7).
- The rest of the screen walk at 2560, 1366 px and phone width. It needs the Chrome window visible.
- The Weir rc.4 rig checks (#900 and #903 evidence for the Weir session).
