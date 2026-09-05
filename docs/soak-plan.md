# Deluno 14-day production-like soak plan

Status: ready to start after the clean-Windows release-candidate gate in [#81](https://github.com/jampat000/Deluno/issues/81).

This plan is the evidence contract for [#82](https://github.com/jampat000/Deluno/issues/82). It describes a real, non-critical Deluno library, not a simulated clock or a disposable unit-test fixture. Every destructive option remains explicitly opt-in.

## Target and ownership

- **Machine:** record the hostname, Windows version, storage volume, and timezone in the Day 0 evidence.
- **Deluno version:** record the release-candidate tag and commit under test. Do not change the build during the run.
- **Library:** use a real but non-critical personal library with representative movies and TV, at least one enabled indexer, one enabled download client, and an active Media Plan. Record counts and filesystem roots without including secrets.
- **Owner:** record the person responsible for the daily check and the backup location.
- **Safety:** use a non-critical library, keep deletion and unmonitor actions opt-in, and take the Day 0 backup before the clock starts.

## Day 0 baseline

Capture the following before Day 1. Store the snapshot output under `artifacts/soak/<run-id>/` and attach the redacted summary to the issue.

- Library movie, series, season, and episode counts, plus the filesystem roots and free-space percentage.
- Enabled indexers and download clients, their health state, and the active Media Plans.
- Queue and activity counts, metadata readiness, open alerts, and the current application version.
- A manual backup created through `POST /api/backups`; record its timestamp and verification result.
- The current set of monitored titles and a filesystem listing for the test library. Do not commit credentials, API keys, or media metadata containing personal information.

## Workload profile

Run the same representative workload each day and record the outcome, IDs, and timestamps:

1. Let scheduled discovery run and confirm that a normal search cycle completes.
2. Perform at least one grab → transfer → import → rename round trip using the external indexer and download client.
3. Confirm the processor handoff when it is enabled, including the final path and monitored state.
4. Exercise the retry/cleanup path only with an intentionally disposable test item; never use a destructive action against the non-critical library without an explicit operator decision.
5. Record the day. The collector takes the reading, decides six of the seven checks against the thresholds below, and writes the result:

   ```powershell
   npm run soak:snapshot -- -RunId <run-id> -BaseUrl <url> -ApiKeyFile <path> -WorkflowNote "<what you saw>"
   ```

   Prefer `-ApiKeyFile` to `-ApiKey`: the key is then never in a shell history, a scheduled-task definition, or this repository, and it is never written to the evidence files either way.

## Daily checklist and thresholds

The collector records the endpoint-backed values. The operator records the filesystem and workflow observations in the final column.

| Check | Source | Pass threshold | Operator evidence |
| --- | --- | --- | --- |
| Readiness | `/api/health/ready`, `deluno_monitoring_readiness_ready` | `1` for the day | No startup or dependency regression |
| Critical alerts | `/api/monitoring/alerts`, `alerts_open` | `0` critical alerts | Review every warning and its decision |
| Failed jobs | Prometheus `deluno_monitoring_jobs_failed` | No upward trend for three consecutive days | Explain any retry or cleanup |
| API errors | Prometheus `deluno_monitoring_api_error_rate_percent` | `< 5%` | Note any endpoint or external-service error |
| Free storage | Prometheus `deluno_monitoring_storage_free_percent` | `> 12%` | Confirm no unexpected growth |
| Services | Prometheus healthy/total indexers and clients | All enabled services healthy | Name any disabled or intentionally unavailable service |
| Workflow | Jobs, activity, and filesystem review | Discovery, grab, transfer, import, and rename all accounted for | Record IDs, paths, and no unexpected deletes/unmonitors |

The seven checks are a daily decision, not a suggestion. A missing endpoint response or missing metric fails the day and must be recorded as such.

**Six of them are arithmetic, so the collector decides them** and writes `PASS`, `ATTENTION` or `FAIL` into `daily.md` with the reason. The seventh is the operator's eyes on the filesystem and cannot be read off an endpoint: supply it with `-WorkflowNote`. A day without one is `ATTENTION`, not `PASS` - an unmade decision is not a passing one.

The collector also tells a failing product apart from a broken collector. A day where no request even reached Deluno is a *missing* day: it warns, exits non-zero, and says so in the ledger, rather than recording fourteen red days that look like evidence. That distinction exists because the collector spent its whole life unable to take a reading on the machine that had to take it ([#461](https://github.com/jampat000/Deluno/issues/461)).

## Starting and stopping the run

Fourteen consecutive days is the point of the gate, and fourteen chances to remember is not a plan. Schedule it:

```powershell
npm run soak:snapshot -- -InstallDailyTask -RunId <run-id> -BaseUrl <url> -ApiKeyFile <path> -DailyTaskAt 09:00
```

Each collected day lands as `ATTENTION` until its workflow check is recorded, so the ledger shows what is still owed. Close a day by running the same command with `-WorkflowNote`.

A P0 or P1 stops the clock. Remove the task, fix and verify, then start again with a **new** run id so the new ledger cannot be mistaken for a continuation of the old one:

```powershell
npm run soak:snapshot -- -RemoveDailyTask -RunId <run-id>
```

**Do not start the clock while the build is still moving.** The rule below says a P0 or P1 restarts the run at Day 1, and an end-to-end pass that has not been walked yet will find some. Finish that first, then start.

## Defect and restart rule

File each finding as its own issue and link it from #82. A P0 or P1 finding stops the clock: fix it, verify the fix, and restart at Day 1 with a new run ID. Log lower-severity findings, link them to the run, and carry them to the release decision. An unexpected deletion, unmonitor, data loss, or inability to recover a failed transfer is P0.

Do not shorten the run by simulating time. Fourteen real consecutive days are required because memory leaks, retry loops, and unbounded growth are the failures this gate is designed to expose.

## Closure evidence template

Paste a redacted version of this table into #82 after Day 14:

| Day | Date | Ready | Critical alerts | Jobs failed | API error % | Free storage % | Workflow/filesystem notes | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| 0 | YYYY-MM-DD | 1/0 | 0 | n | n | n | Baseline and backup ID | PASS/FAIL |
| 1 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 2 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 3 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 4 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 5 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 6 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 7 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 8 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 9 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 10 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 11 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 12 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 13 | YYYY-MM-DD | 1/0 | 0 | n | n | n | | PASS/FAIL |
| 14 | YYYY-MM-DD | 1/0 | 0 | n | n | n | Final review and backup check | PASS/FAIL |

### Defects

| Issue | Severity | Resolution | Evidence |
| --- | --- | --- | --- |
| # | P0/P1/P2/P3 | Open/fixed/carried | Link |

**Recommendation:** GO / NO-GO  
**Blocking issues:** list every unresolved P0/P1, or write `None`.  
**Run ID:**  
**Candidate commit:**  
**Reviewer:**
