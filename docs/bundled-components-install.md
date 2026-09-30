# Bundled component install (#640)

What the Windows installer's component picker does, where it lives, and why.

## Where the picker lives, and why

Velopack has no custom installer UI: `Setup.exe` unpacks the app per-user,
without elevation, and runs it once. There is no page in that sequence a
component picker could occupy. The picker therefore runs from
`InstallHooks.OnFirstRun` (`apps/windows-tray/InstallHooks.cs`) — the one
interactive moment in the whole install lifecycle. Velopack's other callbacks
(`OnAfterInstall`, `OnBeforeUninstall`) must return within seconds and show
nothing, which is also why #623's firewall UAC prompt lives in the same place.
First run already does one interactive Windows prompt (the firewall rule); the
component picker is the second, shown right after it.

The alternative considered was a step inside Deluno's own web setup wizard.
Rejected: installing qBittorrent, Weir, and the rest means downloading and
running Windows `.exe`/`.msi` files, which needs a Windows process with
`Process.Start`, not a browser tab. The tray already is that process — it is
what shows the firewall UAC prompt — so the picker is a WinForms dialog next
to it (`apps/windows-tray/ComponentPickerDialog.cs`), not a web page.

## What ships, and where it is described

`installer/bundled-components.json` is the single source of truth for the six
components the picker offers: the five download clients Deluno's dispatcher
recognises (`Deluno.Connections.DownloadClientProtocolCatalog` carries the
same five protocol keys — qBittorrent, Transmission, Deluge, SABnzbd, NZBGet)
plus Weir. It is embedded into the tray executable at build time
(`Deluno.Tray.csproj`'s `EmbeddedResource` for `bundled-components.json`), so
the shipped list and the documented list can never drift apart, and read back
by `apps/windows-tray/BundledComponents.cs`.

Each entry carries:

- `licence` / `licenceUrl` — shown by the picker's "View licences" button
  before anything is ticked, non-blockingly (#640 item 7).
- `url` / `fileName` / `sha256` — a version pinned the same way
  `scripts/lab/rig-software.json` pins qBittorrent and SABnzbd: staged once,
  hashed once, and refused later if it ever stops matching. qBittorrent and
  SABnzbd reuse those exact already-vetted values.
- `mustBeSigned` — for a component with no pinned hash (Transmission and
  SABnzbd, whose official Windows builds are signed), the exact file that
  will be run has its own Authenticode signature checked and the publisher
  name must match exactly. Deluge and NZBGet are unsigned by their vendors,
  so they are accepted only by the checksum the vendor publishes. A file with
  neither a matching checksum nor a valid signature is refused; nothing is
  ever run unverified.
- `defaultPort` — the same defaults already shown in the connections screen
  (`apps/web/src/routes/connections/presets.ts` `CLIENT_PRESETS`) and in
  Weir's own documentation (port 9347).

Weir is resolved differently (`apps/windows-tray/GithubLatestRelease.cs`):
rather than a version pinned in this repo, it always resolves the *latest*
GitHub release of `jampat000/Weir`, because Weir is Deluno's own sibling
product and auto-updates itself once installed — "whatever is currently
published" is the right answer for it in a way it is not for the five
third-party clients. Every Weir release publishes `SHA256SUMS.txt` next to
`Weir-win-Setup.exe`, so the download is checked against Weir's own published
hash rather than one this repository would have to keep refreshing by hand.

## What the picker does, in order

1. **Detect.** `ComponentInstallService.IsAlreadyInstalled` reads the Windows
   uninstall registry (`Deluno.Api.Network.RegistryInstalledProgramsReader` —
   the same reader the running Deluno uses for its own detection, see below)
   for each component's known display name. Anything already found is shown
   in the list, disabled, and cannot be ticked: re-running an installer over
   an already-configured client is exactly the surprise a "super easy"
   installer must not produce.
2. **Download.** Only a ticked, not-already-installed component is fetched,
   from the official source named in the manifest (or resolved live for
   Weir), to a temp file.
3. **Verify.** Pinned SHA-256 (the vendor's published checksum for unsigned Deluge and NZBGet), or a valid Authenticode signature checked on the exact file that will run. Every installer that runs with administrator rights (the .msi, each vendor .exe setup, the Visual C++ runtime a package needs first, and the older "Get the tools you need" path for qBittorrent and SABnzbd) is copied into a brand-new folder under `%ProgramFiles%` that only administrators can create, links are refused, and the hash (and, where a signature is the gate, the signature and the publisher's exact CN or O) is checked again on that copy before it runs from there; the folder is deleted afterwards. The elevated launch uses the full path to Windows PowerShell and msiexec. A signature rule that names no publisher, with no checksum beside it, is refused. Weir's Setup is the one exception to the elevated run: it installs for the signed-in person alone, Deluno does not raise it, and it is checked against its published checksum and run as that person. Anything that fails either check, or is implausibly small, is deleted and never run.
4. **Run.** Each ticked component's own installer runs as its own process
   (from the locked folder above, or as the signed-in person for Weir), with the silent arguments the
   manifest names where the vendor documents one — never merged into Deluno's
   own payload.
5. **Report.** Outcomes are appended to `install-hook.log`, the same log
   `InstallHooks` already writes to.

Uninstalling Deluno never touches a bundled component: `InstallHooks` has no
uninstall-time code for any of the six, on purpose (#640 item 8) — the same
scope boundary #623 already applies to Deluno's own firewall rule.

## Auto-wire: how the setup wizard finds what the picker installed

A second, independent mechanism — not the picker reporting back to the
backend — makes the setup wizard show a pre-filled connection:

- `GET /api/system/bundled-components`
  (`src/Deluno.Api/Network/BundledComponents.cs`) reads the same registry
  detection plus a loopback TCP probe of each component's default port, live,
  every time it is called. It needs no admin rights and writes nothing.
- The connections screen (`apps/web/src/routes/connections-screen.tsx`) and
  the processing workflow screen
  (`apps/web/src/routes/settings-media-management-page-v2.tsx`) each show a
  `DetectedComponents` banner
  (`apps/web/src/components/app/bundled-components-detected.tsx`) above their
  "New client" / "Connect processor" actions, listing anything installed or
  reachable that is not already configured. Clicking "Use qBittorrent
  (localhost:8080)" opens the existing create drawer pre-filled with that
  protocol, host and port, and — for a download client — runs the existing
  draft "Test" call automatically, so the drawer opens already showing a
  tested connection.

## Connecting what the installer installed (#1061, #968)

The installer installs the ticked apps with only what each needs to start
safely (this computer only, qBittorrent's required Web UI login). Deluno
connects **nothing** by itself: `InstalledComponentWiringService` only forgets
stored logins for apps that are gone, deletes any login an earlier version
stored for a tool (including Weir's generated administrator login, in the
`companion_tool_logins` table), and gives an older Weir connection its health
address. Connections made by an earlier version stay as they are.

The person connects the apps from the setup guide's "Connect the apps you
installed" step or from Connections:

- `POST /api/system/bundled-components/{kind}/connect` connects one app;
  `POST /api/system/bundled-components/connect-all` connects every installed,
  not yet connected download client (Weir is skipped and reports
  `needsAccount`). `GET /api/system/bundled-components` reports, per app,
  `installed`, `connected`, and for Weir `needsAccount` / `accountExists`.
- A download client is connected server-side with the installer's stored
  qBittorrent login or the app's shipped login, and a connection whose test
  failed is never saved (`needsSignIn` with host and port opens the form).
- Weir needs the person's own account (`username` and `password` in the
  connect body). If Weir has no account yet Deluno creates it with those
  values through Weir's bootstrap, otherwise it signs in with them; then it
  makes Deluno's API key (read, imports), registers Deluno in Weir and adds
  Weir as the processor (`WeirCompanion.ConnectAsync`). The password is used
  in memory for that call only: never stored, logged, written to Activity or
  put in an error message.
- No API returns an app's password. The companion-tools install
  (`POST /api/companion-tools/install`) only installs.

This is deliberately decoupled from the install step: detection works exactly
the same whether qBittorrent was ticked in the picker two minutes ago or was
already running before Deluno was ever installed. Both are "detected on this
machine" as far as the wizard is concerned.

**Weir is a partial exception**, on purpose: the banner pre-fills Weir's
submission URL (`http://<host>:<port>/api/v1/intake/webhook/deluno`, the shape
`ProcessorStatusEndpoints` already expects), but not its webhook secret. Weir
generates that secret itself when its account is created in its own browser
UI — there is no API to script that step without Deluno reaching into Weir's
internals, which the project keeps decoupled from on purpose (Weir is
media-manager agnostic; Deluno only ever uses Weir's public installer and its
public `/intake/webhook` API). The person still has to open Weir once, create
an account, and paste the secret in — everything else is pre-filled and
tested.

## What still needs live proof

Everything above is covered by unit tests
(`tests/Deluno.Persistence.Tests/Api/BundledComponentsAreDetectedTests.cs`,
`tests/Deluno.Tray.Tests/ComponentInstallServiceTests.cs`,
`tests/Deluno.Tray.Tests/BundledComponentsTests.cs`,
`tests/Deluno.Tray.Tests/GithubLatestReleaseTests.cs`, and the frontend
`apps/web/src/components/app/bundled-components-detected.test.tsx`) that
exercise the decision logic — verification, detection, dedupe — through
injected fakes. None of it has run a real vendor installer, because this was
built on a workstation that must not install or run Deluno, qBittorrent, or
Weir. Live proof, on a clean Windows VM or machine:

1. Run the packaged installer, tick qBittorrent and Weir, leave the rest
   unticked. Confirm: Deluno installed; qBittorrent installed and its WebUI
   answers on 8080; Weir installed, running, and asks for its port on first
   launch; Transmission/Deluge/SABnzbd/NZBGet untouched.
2. Open Deluno's connections screen and processing workflow screen. Confirm
   the qBittorrent and Weir banners appear, "Use qBittorrent" connects it
   there and then (Deluno does it itself; no password is shown), and "Use Weir" pre-fills the submission URL.
3. Re-run the installer on the same machine. Confirm qBittorrent is shown
   disabled/already-installed and is not re-run.
4. Uninstall Deluno. Confirm qBittorrent and Weir are still installed and
   running.
5. Confirm the Authenticode check actually rejects a tampered file (swap a
   downloaded installer's bytes and confirm the service refuses to run it)
   for at least one `mustBeSigned` component, since this workstation cannot
   download real vendor binaries to test that path end-to-end.

## Sign-in, start, restart and local-only (#1050 - #1053)

Read from the real packages and source, not guessed:

- **qBittorrent 5.2.1** will not start its Web UI without a user name and password ("Credentials are not set"). This is the only thing the installer adds to qBittorrent: the seed carries `WebUIUsername` and `WebUIPassword_PBKDF2` for a generated password (PBKDF2-HMAC-SHA512, 100000 iterations, 16-byte salt, 64-byte key, qBittorrent's own `@ByteArray(<salt>:<key>)` format). The plain password is kept protected in `<data root>\secrets\component-credentials.json` (DPAPI, machine scope) for the tray and a service-run Deluno, and only when the config was actually written, never for one that already existed. Deluno's API never returns it, to a signed-in session or to an API key: the person's Connect click connects the app on the server; Deluno connects nothing by itself after an install. The stored copy stays for as long as the app is installed, even after its connection is deleted, so "Use qBittorrent" can always connect it again (nothing serves the copy to a caller, so keeping it is safe); "Use" for an installed NZBGet, Deluge or Transmission goes through the same connect route with the login each app ships with. The copy is removed only when the app is uninstalled (neither installed nor answering on its port) or for a kind Deluno does not connect (`InstalledComponentWirer.SweepStoredSignInsAsync`). A connection is matched by host and port, so the person's own qBittorrent on 8080 never stands in for the installer's on 8081. Every other qBittorrent setting stays at the app's own default.
- **NZBGet (Windows 21.1)**: `-D` is not a daemon switch on Windows; it hands the process to the service dispatcher and crashes when run by hand. Deluno starts `nzbget.exe -s -o ControlIP=127.0.0.1` with no console window. The installer builds its config from NZBGet's own template with only two changes, `ControlIP=127.0.0.1` (local only) and `MainDir` moved out of Program Files. It adds no user or password: NZBGet keeps the login it ships with, and Deluno connects with NZBGet's own default.
- **Transmission 4.1.1 MSI** has six features; only `QtClient` is in a default install. `transmission-daemon.exe` is in `Daemon`, so the install passes `ADDLOCAL=CommonLibs,Daemon,WebUi`. The package also registers an auto-start LocalService service bound to every address; the same elevated step (one prompt) stops and disables it. Deluno runs `transmission-daemon.exe -f -g %LocalAppData%\Deluno\components\transmission -r 127.0.0.1` with no window, as the signed-in person. The installer adds nothing to its settings; the app keeps its own defaults.
- **Decided by James, 30 Sep 2026: each app keeps its own security defaults.** The installer installs only the apps that were ticked and adds only what Deluno needs to connect to them. Only qBittorrent gets a login (the Web UI login it refuses to start without). Transmission, Deluge, SABnzbd and NZBGet get no login and no password: each keeps its own defaults, and Deluno connects with them (NZBGet's shipped user and password, Deluge's shipped Web UI password, Transmission's open localhost RPC, SABnzbd's own API key read from its settings). Deluge's seed carries only its local address and port; NZBGet's carries only the local address and the download folder. It never switches a protection off and never gives an app a password it does not need. Deluno's API never hands out stored credentials, to an API key or anything else. Older installs get no Health warning and no "Turn them on" button: setting an app up is the person's responsibility.
- **After a restart** the tray (the one thing that already starts at sign-in) starts again every download client Deluno started at install, if its port is not answering (`ComponentAutoStarter`, `%LocalAppData%\Deluno\config\started-components.json`, log in `logs\component-startup.log`).
- **Local only**: every start command (and qBittorrent's seeded `WebUIAddress`) binds 127.0.0.1 (SABnzbd `--server 127.0.0.1:8080`, deluge-web `--interface 127.0.0.1`, qBittorrent `WebUI\Address=127.0.0.1`, NZBGet `ControlIP`, Transmission `rpc-bind-address`). `LocalOnlyTests` fails if any start argument or seeded config carries `0.0.0.0`, `::` or `*`. The torrent clients' peer ports (Transmission 51413, qBittorrent, Deluge) still listen on every address; that is BitTorrent, not a Web UI.
