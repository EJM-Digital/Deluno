# Recovery module

Recovery owns download-health evaluation, dispatch recovery, and
import-recovery retention. It observes external-client state and records
explainable recovery work; it does not remove payload files automatically.

Nothing here retries on a clock. A grab that failed is not sent again, and its
title is searched on its library's schedule (#620, #621). The three dispatch
timeouts below become recovery cases, and Try again on a case is what acts.

## Failure-kind map

| Failure kind | Owner | Retry/remediation policy |
| --- | --- | --- |
| `grab-timeout` | Dispatch polling and recovery handlers | Recovery case; Try again ends a download the client never showed and searches for the title again, or hands one it did show to the processor again. |
| `detection-timeout` | Dispatch polling and recovery handlers | Recovery case; as `grab-timeout`. |
| `import-failed` | Dispatch polling and recovery handlers | Recovery case; Try again runs the import again. |
| `client-stalled` | Download-health evaluator | Health evidence and manual client review; a safe retry may be offered. |
| `post-processing-failed` | Download-health evaluator | Health evidence and manual review before retrying. |
| `missing-import-path` | Download-health evaluator | Repair path mapping and refresh the queue. |
| `no-throughput` | Download-health evaluator | Inspect peers/client connectivity; a safe retry may be offered. |
| `excessive-eta` | Download-health evaluator | Review availability before replacing or retrying. |
| `suspicious-payload-name` | Download-health evaluator | Verify contents manually; no automatic removal. |
| `orphanFile` | Filesystem recovery | Remains owned by the filesystem mechanism. |

The cleanup switch values (`max-retries-exceeded`, `notFound`, `paused`,
`planned`, and `circuitOpen`) intentionally remain as-is in this extraction;
they are not reconciled with the failure-kind table here. Import recovery
retention continues to sweep resolved cases after 24 hours and applies the
configured 30-day fallback when no positive retention value is configured.

Health findings are surfaced in Activity and the download queue, while
dispatch recovery cases remain available through the movie and series recovery
surfaces.
