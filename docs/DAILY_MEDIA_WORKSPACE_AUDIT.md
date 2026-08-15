# Daily media workspace audit

Updated: 2026-08-14

This is the working audit for the everyday Deluno experience: Dashboard,
Movies, TV Shows, Schedule, Transfers, Automation, Activity, and title detail.
It records only confirmed gaps. Product/design questions that require a decision
belong in their own issue rather than being silently treated as defects.

## Product contract

Daily media work is a workstation, not another setup area. A user must be able
to add, inspect, monitor, correct, search, upgrade, remove, and recover their
movies and TV shows without finding hidden configuration routes or translating
internal automation jargon.

## Confirmed findings

| ID | Surface | Confirmed gap | User impact | Status / tracking |
| --- | --- | --- | --- | --- |
| DMW-01 | Movies, TV Shows, title details | No visible remove-from-Deluno action exists for one or many titles. The API already has an unreachable bulk record-removal operation, while a misleading legacy `DELETE /bulk` operation only unmonitors. | A normal library correction is impossible; users cannot tell record removal apart from deleting files or download-client work. | Record-only removal is now implemented and verified on desktop/mobile; the optional safe file-delete preview, client-work separation/audit, and legacy route cleanup remain in [#102](https://github.com/jampat000/Deluno/issues/102). |
| DMW-02 | Movies and TV workspaces | Wanted and Upgrades both render upgrade-eligible records. | The same work appears in two places with potentially conflicting counts and actions. | Open: [#103](https://github.com/jampat000/Deluno/issues/103) |

## Audit queue

| Area | Check being applied |
| --- | --- |
| Dashboard | At-a-glance information only; no fake activity, duplicate control room, or missing route to the next useful action. |
| Movies and TV | Status definitions, bulk/title parity, action placement, library/wanted/upgrades/import ownership, and clear empty states. |
| Schedule | Calendar and release/retry visibility without becoming a second automation configuration page. |
| Transfers | One understandable lifecycle from client dispatch through processing, import, and recovery; no competing recovery screens. |
| Automation | Live operational controls only, with an explicit connection back to its permanent Library setup policy. |
| Activity | Durable history and rationale, not live controls or a duplicate Transfers view. |
| Detail screens | Consistent title status, decision explanations, recovery, management actions, and safe removal. |

## Rule for creating follow-up issues

Before creating a new issue, search existing open issues for an equivalent
outcome. A follow-up must name the affected user task, show repository evidence,
define the expected experience, and include movie/TV parity and browser plus
integration acceptance coverage where relevant.
