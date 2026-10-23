# Background services

Every `BackgroundService` and `IHostedService` in Deluno (`src` and `apps`), and what each does when something goes wrong (#1080). Keep this list in step with the `AddHostedService` calls: a new service goes here in the same change.

## The rules

1. **A loop ends only when the host is stopping.** A cancellation that is not a shutdown (an `HttpClient` timeout surfaces as `TaskCanceledException`) is an ordinary failure. Loops catch with `when (!stoppingToken.IsCancellationRequested)`, never with `is not OperationCanceledException`, and never `break` out of a loop on a cancellation unless the stopping token is the one that was cancelled.
2. **A loop logs the failure and tries again** at its next pass (after its normal interval, or a growing pause in the job lanes). It never throws out of `ExecuteAsync` for a failed pass.
3. **Waiting for the databases never throws.** Use `IDelunoStartupGate.WaitUntilReadyAsync`, which keeps waiting and logs every two minutes. The plain `WaitAsync(timeout)` throws `TimeoutException`, which used to end three loops for good on a slow start.
4. **A safety net sits under all of them.** Deluno sets `BackgroundServiceExceptionBehavior.Ignore`, so one service throwing does not stop the host. `BackgroundServiceFailureMonitor` watches every service's task; an error that escapes is logged and shown on Health in plain words.
5. **Two services are critical**: `DelunoHeartbeatWorker` (runs every job lane) and `HealthCheckHostedService` (the only thing that can put a problem on Health). If one ends, the monitor restarts it in-process with a growing pause, up to five times in a row. If it cannot be kept running, Deluno gives up, and what happens next depends on how it is hosted:
   - **Docker or console host**: the host stops with exit code 1 and Docker restarts it (`compose.yaml`).
   - **Installed Windows service**: `WindowsServiceLifetime` (a `ServiceBase`) reports the host stopping to the service manager with `ServiceBase.ExitCode`, default 0, which looks like a clean stop. So on a give-up Deluno first sets that exit code to 1067 (ERROR_PROCESS_ABORTED), then stops the host gracefully; with `failureflag 1` the service manager counts that as a failure and Windows restarts the service after one minute. `Environment.Exit(1)` remains only as a fallback if the host has not stopped in time. A normal stop never sets the code. Every start of the service also sets `sc failure ... restart/60000` and `failureflag 1`, so services installed by earlier builds get them.
   - **Tray-hosted (the default on Windows)**: the tray icon and the web host are separate things in one process. Every start of the host goes through `HostSupervisor`, not only a stop nobody asked for: the first start (a normal start, the first run after Setup, the first start after an update, a pending update applied at start), Restart, Change port, and `DelunoServer.StoppedUnexpectedly`. See "Supervised starts" below.
6. **Start-up steps that prepare the databases** (`StartAsync` of the schema initializers and the storage bootstrap) wait up to two minutes, with a log line, for a database that another process holds locked (`SqliteLockRetry`). A lock that never clears still fails start-up with the original error: Deluno must not run on a database it could not prepare. This is the one deliberate case where an exception at start-up stops the host.

## Supervised starts (#1256)

A start that failed or hung used to be final: the supervisor covered only a stop after a good start, and a start that blocked kept the icon blinking "Starting" for ever. The model follows systemd `Restart`/`StartLimit`, Windows service recovery and Kubernetes startup probes, and matches Weir's start-up wording.

- **Phases.** `StartupPhase`: starting, backup, opening the databases, migrations, web host, ready. Before each step that can block, `IStartupProgress.Enter` is called (each database, each migration, each backup file), which writes `start-progress.json` beside the other start-up logs (pid, try, phase, step, time; replaced whole, never torn) and a line in `tray-startup.log`. A marker another process left, with a phase before ready, is reported as the previous run at the next start and never counts as progress.
- **Who reads the marker.** Nothing that judges a live start. The tray's supervisor and the service's guard run in the process that is starting and watch its in-memory progress, so a marker that cannot be written (a scanner holds it, the disk is slow) never counts as "no progress". The file is for outside readers: the next start, a person, the golden-path scenario. A read that meets a busy file waits about 250 ms and answers "busy" (never "unreadable", which means contents that cannot be parsed); a write keeps trying for about 2 seconds, then logs once that it cannot and tries once per step until it lands.
- **Hung start.** No step reported for the phase's window: starting 2 min, backup 12 min (the backup cancels itself at 10 and is abandoned 30 s later), opening the databases 4 min (one locked database is waited for up to 2 min), migrations 15 min (the longest single step), web host 3 min. A start that keeps reporting steps is never cut off.
- **What happens.** The try is cancelled and given 20 s; the host is stopped and disposed (30 s) so its databases and port are free; the phase, step and thread list are logged. If the start still has not ended, the program starts itself again (`--after-restart <pid>`; at most 2 relaunches in 30 min, kept in `restart-guard.json`), else it gives up in plain words.
- **Retries.** Capped exponential backoff with jitter (15 s doubling to 5 min, each wait between half and all of that), 5 failures inside 15 minutes before giving up, the count cleared after 10 minutes of steady running by the clock. Each try is logged with its number, reason and next wait; a start that needed a retry is a warning; giving up and recovering are logged.
- **Not on the UI thread.** The supervisor runs each try on the thread pool. The tray shows what the start is doing ("Starting - opening the databases..."), and the icon that covers the backup at start is up before the backup begins.
- **Giving up.** The balloon's click is Restart. In service mode `ServiceStartGuard` watches the same progress and ends a hung start with exit code 3 (a failed one with 1) so the service manager's recovery acts; the restart-on-failure settings are set before the host is built, so a service installed by an older build has them even when its first start hangs.

## Every service

"Start-up" is what an exception before the loop's first pass does. "Loop" is an exception inside a pass. "Timeout" is an `HttpClient` timeout (`TaskCanceledException`) inside a pass.

### Run once at start (`IHostedService`)

| Service | Start-up | Notes |
|---|---|---|
| `DelunoStorageBootstrapService` | Waits for a locked database (rule 6), otherwise a failure stops the host (intended). | Opens every database, sets pragmas. |
| `PlatformSchemaInitializer`, `MoviesSchemaInitializer`, `SeriesSchemaInitializer`, `JobsSchemaInitializer`, `CacheSchemaInitializer` | Same: retry for a locked database, then fail start-up and mark the database failed on the start-up gate. | Migrations commit one at a time, so a retry carries on where it stopped. |
| `IndexerIdentityBackfillService` | Caught and logged; history stays filed by name. | |
| `CollectionExclusionMigrationHostedService`, `MovieRungRepairReportService` | Caught, logged, shown on Health (a `startup:...` problem key), run again next start. | |
| `BackgroundServiceFailureMonitor` | Nothing to fail. | The safety net itself. |
| `IndexerNamesUpgradeService` | A busy database is waited out; any other failure is caught and logged, and indexers keep their names. | Gives indexers their automatic name from their address; runs again each start and changes nothing the second time. |
| `NotificationSecretsUpgradeService` | Caught and logged (not a shutdown); the addresses and tokens stay as they were and the next start tries again. | Protects the notification addresses and tokens an upgraded install kept in clear (#1364); changes nothing the second time. |
| `AppliedUpdateStartupCheck` | Waits for the databases (rule 3), because it writes an Activity line; a failure is caught and logged. | Looks for a newly applied update at start so its history and Activity line exist whether or not anyone opens the Updates card (#1376). |
| `EpisodeFileLinkRepairService` | An exception is caught and logged; links stay as they are. | Once per start, not gated by the automatic-jobs switch: clears an episode's wrong file link, keeps the episode the file name gives, relinks the cleared episode's own file from the show folder, writes Activity; safe to repeat. |

### Loops (`BackgroundService`)

| Service | Critical | Start-up | Loop | Timeout |
|---|---|---|---|---|
| `DelunoHeartbeatWorker` | yes | Releasing old leases is caught and logged; import-restart recovery is caught and logged; a handler with no lane is a programming error and goes through the critical path. | Each lane runs under `LaneSupervisor` and `LaneRunner`: a failed pass is logged and retried with a pause, a lane that keeps failing shows on Health, a lane task that ends is restarted. | A failed pass. |
| `HealthCheckHostedService` | yes | Nothing before the loop. | Catches everything except a shutdown; recording the outcome is inside the same guard. | A failed pass, logged; the next tick tries again. |
| `DownloadDispatchPollingHostedService` | no | None. | Caught per pass and around scheduling. | A failed pass. |
| `DownloadThroughputSampler`, `MachineTelemetrySampler`, `DownloadProgressPublisher` | no | None. | A failed sample is a gap in a chart; the first failure is a warning, the rest debug. | A failed pass. |
| `HistoryRetentionWorker` | no | None. | Caught and logged; next pass after the interval. | A failed pass. |
| `QbittorrentBlocklistSyncService`, `ContentBlocklistRemoteRefreshService` | no | None. | Caught and logged; hourly. | A failed pass. |
| `DelunoBackupService` | no | None. | Caught and logged each minute. | A failed pass. |
| `ImportRecoveryRetentionService` (the tray runs this one too; its own copy was removed, #1324) | no | None. | Caught and logged. | A failed pass. |
| `RetentionScheduleHostedService` | no | None. | Caught per rule and per pass. | A failed pass. |
| `EpisodeGroupRefreshHostedService`, `SceneMappingRefreshHostedService` | no | None. | Caught per pass and around scheduling. | A failed pass. |
| `RankingModelTrainingHostedService` | no | Waits for the databases (rule 3); a failure reading its status is logged and it carries on. | Every pass, and the failure record inside it, is guarded. | A failed pass. |
| `TrackerDefinitionAutoUpdateHostedService`, `GuideUpdateCheckHostedService` | no | Waits for the databases (rule 3). | Caught per pass. | A failed pass. |
| `GuideCatalogueUpgradeHostedService` | no | Waits for the databases (rule 3); one pass, the whole pass guarded (store calls included). | Runs once. | A failed pass. |
| `TrackerSiteSwitchHostedService` | no | Waits for the databases (rule 3); one pass, guarded. | Runs once. | A failed pass. |
| `DownloadFolderChangeNoticeService` | no | Waits 20 s, then one guarded pass; no marker is written on a failure, so the next start tries again. | Runs once. | A failed pass. |
| `CloudflareHelperSessionCleanupService` | no | None. | A cancellation that is not a shutdown no longer ends the loop (fixed in a99593a2). | A failed pass. |
| `InstalledComponentWiringService` | no | None. | Same fix (a99593a2). | A failed pass. |
| `SignalRRealtimeEventPublisher` | no | None. | A failed delivery is logged at debug; the queue read ends only when stopping. | Not applicable. |
| `TrayUpdateBackgroundService` (tray host) | no | None. | Caught and logged every six hours. | A failed check; no longer ends the loop. |
| `RegularConnectionCheckService` | no | Waits 45 s. | Every 30 s, claims `SystemTasks.ConnectionsCheck` once a minute and tests the connections nothing has used for 15 minutes; a failed or cancelled pass is recorded failed or cancelled and logged, and the loop goes on. Off with the setting Check connections regularly. | A failed pass. |
| `ProcessorHealthWatcher` | no | None. | Own loop, 2 s tick: a healthy processor is looked at every 30 s, one that is not every 15 s, on a fixed schedule, in parallel, each probe cut off at 12 s so a hanging processor never holds up another, one probe out per processor. A throwing repository or probe is logged and retried. Not tied to the automatic-jobs switch. | A probe that hangs is cut off at its 12 s limit. |
| `DashboardVolumeRefresher` | no | Waits 2 s. | Every 60 s measures the library, download and data folders so a request never touches a disk; 4 s per folder, so a share that never answers costs one budget and shows as unavailable; a refresh that throws keeps the last reading. | A folder past its 4 s budget. |
| `SystemOverviewSampler` | no | None. | Every 2 s while the Dashboard or Live is open (pushes the overview), every 10 s otherwise (a history-only reading). A failed reading is a gap in a chart and is logged at debug. | A failed reading. |
| `SystemOverviewRecorder` | no | Records the start, waits 30 s. | Hourly records each drive's free space for the day and prunes history older than 14 days. An hour that cannot be read or written is logged and skipped; the next hour tries again, so the drive-full forecast does not stop. | A failed hour. |
| `SystemChangeWatcher`, `SystemLogPushService`, `OutboundThrottleWatcher` | no | None. | Push to a watching Dashboard only when the picture changed (every 15 s, each new log line, every 2 s). A failing tick is skipped with a debug line and the loop continues. | A failed tick. |
| `DatabaseIntegrityService` | no | Waits 2 min. | Hourly tick, claims `SystemTasks.DatabaseIntegrity` daily and runs `PRAGMA quick_check` on every database; a corrupt database is named in the note and the pass is recorded failed, and an exception is recorded failed with a plain reason. | A failed pass. |
| `TitleChangeNotifier` | no | None. | Coalesces writes to the Movies and Series databases for 250 ms, then announces the changed titles one by one (up to 200 titles and 25 episodes, else one bulk event). A flush that throws is logged at debug and the loop continues. | A failed flush. |
| `DownloadTelemetryRefreshService` | no | None. | Refreshes the shared download-client reading every 20 s, every 2 s while a screen watches and every 1 s while downloads are moving; every client is read fresh each tick and a failing client warns once, then debug. Concurrent requests share one in-flight read. | A failed client read. |
| `OrphanedDownloadRefreshService` | no | Waits 6 s. | Every 60 s finds download-folder leftovers nothing owns, so the Queue list is current; read-only, not gated; a scan error is logged at debug and the last list stays. | A failed scan. |
| `MislabelledEpisodeFileRepairService` | no | Runs at once. | Then every 6 h (a real timer); does nothing while automatic jobs are off (#1321). An exception in a pass is caught and logged and the timer keeps going. | A failed pass. |
| `KeepAwakeService` (Windows only) | no | Returns at once elsewhere. | Every 30 s asks Windows to stay awake when Keep computer awake is on (off by default). A settings read that fails is logged at debug. | Not applicable. |

## What switches a service off

Nothing automatic may change a person's files, downloads or library records unless they switched automatic jobs on (27 Sep, #1321). So every service is one of:

- **Changes media, asks the switch.** `EpisodeFileLinkRepairService` and `MislabelledEpisodeFileRepairService` carry `[ChangesMediaOnlyWhenAutomaticJobsAreOn]` and ask `AutomaticJobs.IsOnAsync`; with the switch off they change nothing and say so once in Activity. `DelunoHeartbeatWorker` runs the job lanes, which stop when the switch is off.
- **Does not change media.** It prepares or upgrades Deluno's own databases, prunes Deluno's own history, or only reads, samples, checks and pushes to open screens. `HostedServicesObeyTheAutomaticJobsSwitchTests` names each one with the reason, and fails when a new service is in neither group.
- **Has its own off-by-default switch.** Scheduled backups (`DelunoBackupService`), the content-blocklist push to qBittorrent (`QbittorrentBlocklistSyncService`), the remote content blocklist refresh (`ContentBlocklistRemoteRefreshService`) and Keep computer awake (`KeepAwakeService`) do nothing until the person turns them on. Checking connections regularly (`RegularConnectionCheckService`) only reads and is on for every install.

## Tests

- `BackgroundServiceFailureTests`: one failing service does not stop the host; a critical service is restarted; giving up stops the host, or ends the process when `ExitProcess` is set.
- `HealthCheckHostedServiceTimeoutTests`: a `TaskCanceledException` that is not a shutdown does not end the service.
- `StartupGateWaitTests`: waiting past the limit does not throw.
- `MigrationLockRetryTests`: a busy or locked answer is retried, a lock that never clears still fails, and a real held write lock is waited out.
- `HostSupervisorTests`, `StartProgressTests`, `StartGuardTests` (tray, on a fake clock): see "Supervised starts".
- `StartupProgressHookTests` (persistence): the migrator, the storage bootstrap and the start-up gate report each step.
- `ServiceFailureActionsTests`: the `sc failure` and `failureflag` commands.
- `EveryBackgroundServiceIsInTheServiceListTests`: every hosted service the server builds is named in this list.
- `HostedServicesObeyTheAutomaticJobsSwitchTests`: every hosted service says whether it changes media, and those that do ask the switch.

## What only the rig can prove

- `sc qfailure Deluno` shows restart after 60000 ms three times and the failure flag set, on a fresh install and on a service installed by an earlier build after it has started once.
- Make the job worker fail permanently (a test build that throws from `DelunoHeartbeatWorker`): the service goes to Stopped and is Running again about a minute later, with a second start in Deluno's log.
- Tray-hosted: the same test build; the web host stops, the tray icon says Deluno stopped and is starting again, and the web page answers again within the first backoff.
- A write lock on a database (`BEGIN IMMEDIATE`) held for 60 s while Deluno starts: start-up logs that it is waiting for a locked database and completes once the lock is released.
