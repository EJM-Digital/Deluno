# PLAN: the end-to-end suite runs in CI, with no rig and no VM

**Decided by James, 16 Oct 2026:** "once we are happy and we are no longer in RC there will be no VM or Rig to test, we will need to do it properly how Senior development teams do it." The golden-path suite (scripts/golden-path/, docs/golden-path.md) runs on a private Hyper-V machine. It takes over two hours, one group after another, and it disappears with the rig. This plan replaces it with suites that run on GitHub's own runners, from a clean checkout, every time.

## Where each kind of proof runs

| Kind | Runs on | How |
| --- | --- | --- |
| Deluno with real download clients, indexers and Weir | `ubuntu-latest`, Docker Compose | Deluno's own image (built by the same CI run). Weir's published image, pinned by digest. qBittorrent, Transmission, Deluge, SABnzbd and NZBGet as their published images, pinned by digest. The suite's local Torznab/Newznab feed and NNTP server as small fixture containers. Scenarios drive Deluno's API and screens (Playwright against the real server, as `apps/web/tests/real/` does) and read each client's own API, never Deluno's word for it. |
| Windows-only behaviour | `windows-latest` | Silent install from the candidate installer, the Windows service, the component picker, the update from the previous release, the tray through UI Automation, NTFS hard links and cross-volume moves, uninstall. |
| Contracts with other apps | every push | Already in place: tests replay recorded real replies (`tests/fixtures/recorded/`). |
| Drift in the other apps | scheduled, weekly | A job runs the pinned and the newest image of each app, re-records, and fails on any difference from the committed recordings. A difference becomes an issue, not a silent update. |

## Speed comes from sharding, not from shorter clocks

- **One job per group, in a matrix.** Each job runs its own Compose stack, so groups cannot interfere. The slowest groups are split further, for example `resend` by client. Wall time is the slowest shard, aimed at 15 minutes.
- **The real default timings stay proven.** The stall check interval and Weir's report time are not shortened.

## When it runs

- **Every push:** today's unit, integration, recorded-reply and smoke tests. Also the E2E shards whose area the change touches, chosen by the same paths filter `changes` uses.
- **Before a tag:** the full E2E matrix and the Windows job on the tagged commit, or a pass carried from an ancestor whose shipped files are identical (James, 16 Oct: the carry-over rule).
- **Nightly**, once the repository is public (Actions minutes for standard runners are then free): the full matrix, repeated, to catch flakes.

## Getting there without a gap

1. **Prove the stack.** Bring up Compose with Deluno, one client (qBittorrent) and the fixture feed, and port group `films`. It must pass in CI.
2. **Port group by group.** Use the times and evidence in docs/golden-path.md. Each port keeps every check the VM scenario makes, and the parity table below records it.
3. **The Windows job** takes install, picker, update, tray and NTFS.
4. **The rig suite stays the gate** until every row below is ported and green in CI twice in a row. Then the release check switches to the CI suite, and the VM suite is retired.

## Parity table

One row per golden-path scenario id (77 on 16 Oct 2026), each with its CI home (`linux-e2e:<group>` or `windows`) and its state (to port, ported, or green twice). Filled in during step 2.
