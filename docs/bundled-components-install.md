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
- `mustBeSigned` — for a component with no pinned hash (Transmission, Deluge,
  NZBGet: their official Windows builds change often enough that pinning one
  version would mean shipping an installer that goes stale), the downloaded
  file's own Authenticode signature is checked instead. A file with neither a
  matching pinned hash nor a valid signature is refused; nothing is ever run
  unverified.
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
3. **Verify.** Pinned SHA-256, or a required Authenticode signature. Anything
   that fails either check, or is implausibly small, is deleted and never run.
4. **Run.** Each ticked component's own installer runs as its own process
   (`Process.Start`, `UseShellExecute = true`), with the silent arguments the
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
   the qBittorrent and Weir banners appear, "Use qBittorrent" opens the drawer
   already showing a passed test, and "Use Weir" pre-fills the submission URL.
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

- **qBittorrent 5.2.1** will not start its Web UI without a user name and password ("Credentials are not set"). The seed carries `WebUI\Username` and `WebUI\Password_PBKDF2` for a generated password. The hash is qBittorrent's own format (`src/base/utils/password.cpp`, release-5.2.1): PBKDF2-HMAC-SHA512, 100000 iterations, 16-byte salt, 64-byte key, written as `"@ByteArray(<base64 salt>:<base64 key>)"`. The plain password is kept protected in `<data root>\secrets\component-credentials.json` (DPAPI, machine scope, so the tray and a Deluno running as a service can both use it) and handed to the screen by `GET /api/system/bundled-components/{kind}/credentials`, so "Use qBittorrent" opens with the sign-in filled in and tested. It is stored only when the config was actually written, never for a config that already existed.
- **NZBGet (Windows 21.1)**: `-D` is not a daemon switch on Windows; it hands the process to the service dispatcher and crashes when run by hand. Deluno starts `nzbget.exe -s -o ControlIP=127.0.0.1` with no console window. Its config is built from the `nzbget.conf.template` the installer ships (ControlIP 127.0.0.1, a generated user and password instead of the public default) and written to `%ProgramData%\NZBGet\nzbget.conf`.
- **Transmission 4.1.1 MSI** has six features; only `QtClient` is in a default install. `transmission-daemon.exe` is in `Daemon`, so the install passes `ADDLOCAL=CommonLibs,Daemon,WebUi`. The package also registers an auto-start LocalService service bound to every address; the same elevated step (one prompt) stops and disables it. Deluno runs `transmission-daemon.exe -f -g %LocalAppData%\Deluno\components\transmission -r 127.0.0.1` with no window, as the signed-in person, config seeded to bind the RPC to 127.0.0.1:9091.
- **Decided by the owner, 30 Sep 2026: each app keeps its own security defaults.** The installer installs only the apps that were ticked and adds only what Deluno needs to connect to them. It never switches a protection off and never gives an app a password it does not need: where an app's own default is no password, the installer does not add one. Where an app refuses to start without a sign-in (qBittorrent), the generated one is used only for that. Deluno's own API never hands out the credentials it has stored for an app, so the line above about the credentials route is being reworked: the screen connects the app for you and does not reveal the password. Older installs get no Health warning and no "Turn them on" button: setting an app up is the person's responsibility. The installer's own safety stays: downloaded installers have their signatures checked and run from a locked folder.
- **After a restart** the tray (the one thing that already starts at sign-in) starts again every download client Deluno started at install, if its port is not answering (`ComponentAutoStarter`, `%LocalAppData%\Deluno\config\started-components.json`, log in `logs\component-startup.log`).
- **Local only**: every start command and seeded config binds 127.0.0.1 (SABnzbd `--server 127.0.0.1:8080`, deluge-web `--interface 127.0.0.1`, qBittorrent `WebUI\Address=127.0.0.1`, NZBGet `ControlIP`, Transmission `rpc-bind-address`). `LocalOnlyTests` fails if any start argument or seeded config carries `0.0.0.0`, `::` or `*`. The torrent clients' peer ports (Transmission 51413, qBittorrent, Deluge) still listen on every address; that is BitTorrent, not a Web UI.
