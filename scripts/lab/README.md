# Lab tooling

The scripts that provision and check the real-client test rig: a physical machine running Deluno beside real qBittorrent, SABnzbd and Weir. Testing is done with real clients, real indexers and a real news server, from a fresh install of the released build (see `docs/exec-plans/active/E2E-live-test-rc28-2026-10-03.md`). There are no fake indexers, fake news servers or generated catalogues here. Nothing here ships, and nothing here is used by the test suites.

These lived in a session scratchpad and had to be hunted for across old session directories every time. They live here now.

## `provision-rig.ps1`

Turns a fresh Windows machine into the rig: the service account, the folder
topology, qBittorrent, SABnzbd and Weir at pinned versions, the client
configuration, and the services.

```powershell
./scripts/lab/provision-rig.ps1 -ComputerName <ip> -Password <admin> `
    -ServiceAccount deluno -ServiceAccountPassword <pw> `
    -LibraryPath '\nas\share' -NasUser <u> -NasPassword <p>
```

Stages are separable (`-Stage folders`), because provisioning a machine is a
long sequence of things that each fail on their own and re-running all of it to
retry the last step wastes the afternoon.

**It stops before Deluno's first run, deliberately.** Phase 0.5 of the
end-to-end plan is "a clean install asks to create an account, not to sign in",
and phases 1 to 7 are the first-run experience, the libraries, the profiles and
the connections. Provisioning those would destroy the first thing the plan
tests, so this leaves Deluno installed, running and untouched.

It does not restate the service shape either - `ensure-rig-services.ps1` owns
that, and provisioning calls it.

Two things it gets right that the hand-built rig got wrong:

- The **NAS credential is stored in the service account's own vault**, through a
  one-shot task running as that account. `cmdkey` only ever writes to the vault
  of whoever runs it, so storing it as the administrator would leave the service
  refused - the same shape as the bug the service account exists to avoid.
- The **share is probed as the service account**, not from the provisioning
  session. A WinRM session has no delegatable network credentials, so testing
  the share from here fails whether or not the service could reach it, and sends
  you to debug the wrong machine.

## `rig-software.json`

The exact software the rig runs, pinned by SHA-256 - captured from the
simulation VM before it was retired, so a new rig starts from a set the
end-to-end plan has actually been walked against rather than from whatever is
current that day. Installers are staged on the developer machine and copied
over, so the rig needs no internet and cannot quietly get a different build.

## `assert-media-boundary.ps1`

The owner's real library shares a NAS with the test run, and the NAS account
Deluno uses can write to the whole share. Nothing on the NAS refuses a write,
so this refuses instead. **Run it before any step that can import, rename,
move, clean, or turn on automation, and stop if it fails.**

```powershell
./scripts/lab/assert-media-boundary.ps1
```

It reads, and never changes, every path Deluno or qBittorrent could write to:
library roots, downloads and processor folders, derived recycle bins, global
root settings, destination rules, and qBittorrent's save paths. It exits
non-zero if any NAS path is outside `rig.json`'s `mediaBoundary.isolatedRoot`,
if any path is inside a protected root, or if any path uses a drive letter
mapped to the share. The NAS is judged the same by name or by address.

Its first draft had a bug that would have let a production path through.
PowerShell 7 returns a JSON array as one object, so both libraries were judged
as one space-joined string, and the isolated Movies root at the front passed
whatever followed it. Test the guard against a rig file that must fail
(`-RigPath`), not only against one that passes.

## `ensure-rig-services.ps1`

Holds every service on the VM to one rule: it starts without a person, and it
comes back after a reboot. Reports drift, repairs only what has it.

```powershell
./scripts/lab/ensure-rig-services.ps1 -ReportOnly
./scripts/lab/ensure-rig-services.ps1
```

Deluno, qBittorrent and Weir are SYSTEM scheduled tasks with a boot trigger.
SABnzbd is a real Windows service, because it will not run as anything else: it
checks its own session id at startup, before parsing any argument, and every
process launched over WinRM or by a SYSTEM task is in session 0, so it always
decides it is a service. Its options live in the `CommandLine` value under its
own service key, which is where its `get_serv_parms` reads them from.

That one difference is why the end-to-end plan spent two runs recording "SABnzbd
needs an interactive session" as a fact about the rig.

## `provision-usenet.ps1`

Brings SABnzbd's own settings to the shape Deluno expects and syncs the API key Deluno holds for it: absolute complete and incomplete folders, the `tv` category, and the API key read from SABnzbd rather than typed, so the two cannot drift apart again. It does not add a news server: SABnzbd's news server is a real provider account, which you enter in SABnzbd yourself.

```powershell
./scripts/lab/provision-usenet.ps1
```

Idempotent; every part is skipped when it is already right.

## `watch-pipeline.ps1`

One call that prints where an acquisition has got to: the telemetry summary, each queue item's status, the processor hand-offs with their output paths and import job ids, the job queue, and the dispatch/processing/import activity. It is the fastest way to answer "is it stuck, and where".

```powershell
.\scripts\lab\watch-pipeline.ps1
```

Credentials: the rig sign-in password comes from `DELUNO_LAB_APP_PASSWORD`; it is never stored in the repository.
