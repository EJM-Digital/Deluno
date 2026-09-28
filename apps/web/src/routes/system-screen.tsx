import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import {
  Activity,
  BellRing,
  Download,
  LoaderCircle,
  HardDrive,
  RotateCcw,
  Server,
  ShieldCheck,
  TimerReset,
  Trash2,
  Upload,
  Wifi,
  WifiOff
} from "lucide-react";
import { JOB_STATUS, type JobStatus, isJobActive, isJobFailed, isJobSuccessful } from "../lib/job-status-constants";
import { SystemShell } from "../components/app/settings-shell";
import { LanAccessNotice, type LanAccessStatus } from "../components/app/lan-access-notice";
import { LogFilesCard } from "../components/app/log-files-card";
import { MetadataMaintenanceCard } from "../components/app/metadata-maintenance-card";
import { ProviderHealthDrawer, type ProviderHealthSelection } from "../components/app/provider-health-drawer";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { AuditTimeline, type TimelineEvent } from "../components/shell/audit-timeline";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { WsStatusBadge } from "../components/shell/ws-status-badge";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { categoryLabel } from "../lib/activity-format";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type BackupItem,
  type BackupSettingsSnapshot,
  type DownloadClientItem,
  type ProcessorConnectionItem,
  type HealthCheckListResponse,
  type HealthCheckResultItem,
  type IndexerItem,
  type JobQueueItem,
  type LibraryItem,
  type LibraryAutomationStateItem,
  type MonitoringDashboardSnapshot,
  type PlatformSettingsSnapshot,
  type RestorePreviewResponse,
  type RecycleBinItem,
  type RecycleBinSettings,
  type SearchCycleRunItem,
  type SearchRetryWindowItem,
  type SystemTaskItem,
  type UpdateActionResponse,
  type UpdatePreferencesResponse,
  type UpdateStatusResponse, readErrorText } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { describeCleanup, type RecycleBinCleanupPreview } from "../lib/recycle-bin-words";
import type { DrawerSaveState } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { PageFooter } from "../components/ui/page-footer";
import { PathInput } from "../components/ui/path-input";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { SummaryStrip } from "../components/ui/summary-strip";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { formatDateTime, type DisplayPreferences, useDisplayPreferences } from "../lib/display-preferences";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

interface SystemLoaderData {
  activity: ActivityEventItem[];
  downloadClients: DownloadClientItem[];
  processors: ProcessorConnectionItem[];
  indexers: IndexerItem[];
  jobs: JobQueueItem[];
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  backups: BackupItem[];
  backupSettings: BackupSettingsSnapshot;
  updateStatus: UpdateStatusResponse;
  automation: LibraryAutomationStateItem[];
  searchCycles: SearchCycleRunItem[];
  retryWindows: SearchRetryWindowItem[];
  monitoring: MonitoringDashboardSnapshot;
  systemTasks: SystemTaskItem[];
  recycleBin: RecycleBinItem[];
  recycleBinSettings: RecycleBinSettings;
  lanAccess: LanAccessStatus | null;
  healthChecks: HealthCheckListResponse;
  systemInfo: SystemInstallationInfo | null;
  activityDisplaySettings: { pageSize: number };
}

/** GET /api/system/info (#869): uptime, runtime, and app-data details for the "This installation" card. */
interface SystemInstallationInfo {
  processStartUtc: string;
  uptimeSeconds: number;
  osDescription: string;
  runtimeVersion: string;
  databaseType: string;
  databaseLocation: string;
  appDataPath: string;
}

export async function systemLoader(): Promise<SystemLoaderData> {
  // #871: how many rows the timeline fetches and shows is a user setting now,
  // not a fixed 500-row cap.
  const activityDisplaySettings = await fetchJson<{ pageSize: number }>("/api/activity/display-settings").catch(() => ({ pageSize: 500 }));
  const [settings, libraries, jobs, activity, indexers, downloadClients, processors, backups, backupSettings, updateStatus, automation, searchCycles, retryWindows, monitoring, systemTasks, recycleBin, recycleBinSettings, lanAccess, healthChecks, systemInfo] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchPageItems<JobQueueItem>("/api/jobs?pageSize=25"),
    fetchPageItems<ActivityEventItem>(`/api/activity?pageSize=${activityDisplaySettings.pageSize}`),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    // A processor holds every file mid-import, and its health used to show on
    // one settings chip and nowhere else (#577).
    fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections"),
    fetchJson<BackupItem[]>("/api/backups"),
    fetchJson<BackupSettingsSnapshot>("/api/backups/settings"),
    fetchJson<UpdateStatusResponse>("/api/updates/status"),
    fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50"),
    fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=12"),
    fetchPageItems<SearchRetryWindowItem>("/api/search-retry-windows?pageSize=12"),
    fetchJson<MonitoringDashboardSnapshot>("/api/monitoring/dashboard"),
    fetchJson<SystemTaskItem[]>("/api/system/tasks"),
    fetchJson<RecycleBinItem[]>("/api/recycle-bin"),
    fetchJson<RecycleBinSettings>("/api/recycle-bin/settings"),
    // "Listening on" below used to echo the stored host.bindAddress/host.port
    // as if they were live; Kestrel actually binds from Server:Port /
    // Server:AllowLan configuration, so this reads the same effective values
    // LanAccessNotice does (#736).
    fetchJson<LanAccessStatus>("/api/system/lan-access").catch(() => null),
    // #909: every health check's latest result, read by System -> Health, the
    // Dashboard tile, the bell and outbound notifications alike.
    fetchJson<HealthCheckListResponse>("/api/system/health"),
    // #869: uptime, process start, OS/runtime version, and app-data path,
    // read live rather than hardcoded at build time.
    fetchJson<SystemInstallationInfo>("/api/system/info").catch(() => null)
  ]);

  return { activity, activityDisplaySettings, automation, backupSettings, backups, downloadClients, healthChecks, indexers, jobs, lanAccess, libraries, monitoring, processors, recycleBin, recycleBinSettings, retryWindows, searchCycles, settings, systemInfo, systemTasks, updateStatus };
}

export function SystemPage() {
  const location = useLocation();
  const revalidator = useRevalidator();
  const loaderData = useLoaderData() as SystemLoaderData;
  const { activity, activityDisplaySettings, automation, backupSettings, backups, downloadClients, healthChecks, indexers, jobs, lanAccess, libraries, monitoring, processors, recycleBin, recycleBinSettings, retryWindows, searchCycles, settings, systemInfo, systemTasks, updateStatus } = loaderData;
  // The folders Deluno writes to are its libraries' folders. This used to count
  // the two paths the setup guide asks for, which a library made in Settings
  // never fills in: two libraries on a NAS read "0".
  const mediaRoots = new Set(libraries.map((library) => library.rootPath?.trim().toLowerCase()).filter(Boolean)).size;
  const { preferences } = useDisplayPreferences();

  const activeJobs = jobs.filter((job) => isJobActive(job.status as JobStatus)).length;
  const healthyIndexers = indexers.filter((item) => item.healthStatus === "healthy").length;
  const healthyClients = downloadClients.filter((item) => item.healthStatus === "healthy").length;
  const enabledProcessors = processors.filter((item) => item.isEnabled);
  const healthyProcessors = enabledProcessors.filter((item) => item.healthStatus === "healthy").length;
  const [selectedProvider, setSelectedProvider] = useState<ProviderHealthSelection | null>(null);

  // #871: the Activity timeline's page-size control and Clear action.
  const [activityPageSize, setActivityPageSize] = useState(activityDisplaySettings.pageSize);
  const [activityBusy, setActivityBusy] = useState<"pageSize" | "clear" | null>(null);
  const [confirmClearActivity, setConfirmClearActivity] = useState(false);
  const activityStatus = useSaveStatus();

  async function changeActivityPageSize(nextPageSize: number) {
    setActivityBusy("pageSize");
    try {
      await authedFetch("/api/activity/display-settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ pageSize: nextPageSize })
      });
      setActivityPageSize(nextPageSize);
      activityStatus.markSaved("Page size saved");
      revalidator.revalidate();
    } catch (error) {
      activityStatus.markError(error instanceof Error ? error.message : "Could not save page size");
    } finally {
      setActivityBusy(null);
    }
  }

  async function clearActivityHistory() {
    setActivityBusy("clear");
    try {
      const response = await authedFetch("/api/activity/clear", { method: "POST" });
      if (!response.ok) throw new Error("Activity could not be cleared.");
      activityStatus.markSaved("Activity cleared");
      setConfirmClearActivity(false);
      revalidator.revalidate();
    } catch (error) {
      activityStatus.markError(error instanceof Error ? error.message : "Activity could not be cleared.");
    } finally {
      setActivityBusy(null);
    }
  }

  /* Live events prepended from WebSocket */
  const [liveEvents, setLiveEvents] = useState<TimelineEvent[]>([]);
  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    setLiveEvents((prev) => [
      { id: event.id, message: event.message, category: event.category, severity: event.severity, createdUtc: event.createdUtc },
      ...prev.slice(0, 49)
    ]);
  });

  const [liveActiveJobs, setLiveActiveJobs] = useState(activeJobs);
  useSignalREvent("QueueItemAdded", RealtimeGroups.Queue, () => setLiveActiveJobs((n) => n + 1));
  useSignalREvent("QueueItemRemoved", RealtimeGroups.Queue, () => setLiveActiveJobs((n) => Math.max(0, n - 1)));
  // #909: a health check starting or clearing revalidates the whole loader,
  // the same coarse refresh SettingsChanged and friends already use here --
  // health rows are read once per page load, not worth a live socket of
  // their own.
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, () => revalidator.revalidate());

  // The timeline has its own tab; repeating all 500 events here made Health
  // three times taller than everything on it that actually answers "is it well?".
  const auditCard = (
    <>
      <ListCard
        title="Activity timeline"
        count="Every event Deluno has recorded, live as it happens"
        actions={
          <div className="flex items-center gap-2">
            <Field label="Rows shown" hideLabel>
              <Select
                value={String(activityPageSize)}
                disabled={activityBusy !== null}
                onChange={(event) => void changeActivityPageSize(Number(event.target.value))}
                options={[
                  { value: "50", label: "50 rows" },
                  { value: "100", label: "100 rows" },
                  { value: "250", label: "250 rows" },
                  { value: "500", label: "500 rows" },
                  { value: "1000", label: "1000 rows" }
                ]}
              />
            </Field>
            <Button type="button" variant="outline" size="sm" disabled={activityBusy !== null} onClick={() => setConfirmClearActivity(true)}>
              Clear
            </Button>
            <WsStatusBadge />
          </div>
        }
      >
        <div className="space-y-2 p-[var(--card-pad-x)]">
          <SaveStatus state={activityStatus.state} message={activityStatus.message} />
          <AuditTimeline events={activity} liveEvents={liveEvents} maxVisible={activityPageSize} />
        </div>
      </ListCard>
      <ConfirmDialog
        open={confirmClearActivity}
        onOpenChange={setConfirmClearActivity}
        title="Clear the Activity timeline?"
        description="This removes every stored Activity row, including the history behind automation decision explanations elsewhere in Deluno — there is no separate copy. This cannot be undone."
        confirmLabel="Clear activity"
        busy={activityBusy === "clear"}
        onConfirm={() => void clearActivityHistory()}
      />
    </>
  );

  const providerCard = (
    <ListCard title="Providers" count={`${healthyIndexers + healthyClients} of ${indexers.length + downloadClients.length} responding`}>
      {indexers.length + downloadClients.length + processors.length === 0 ? (
        <ListEmpty title="Nothing connected yet" description="Add a search source or a download client under Find & Download and its health shows up here." />
      ) : (
        <ListTable columns={[{ label: "Provider" }, { label: "Kind", width: "minmax(0,1fr)" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
          {indexers.map((indexer) => (
            <ListRow key={`indexer:${indexer.id}`} onClick={() => setSelectedProvider({ kind: "indexer", provider: indexer })}>
              <ListNameCell name={indexer.name} sub="Search source" />
              <ListCell primary={indexer.protocol} />
              <ListCell mobile>
                <Chip tone={healthTone(indexer.healthStatus)}>{indexer.healthStatus}</Chip>
              </ListCell>
            </ListRow>
          ))}
          {downloadClients.map((client) => (
            <ListRow key={`client:${client.id}`} onClick={() => setSelectedProvider({ kind: "download-client", provider: client })}>
              <ListNameCell name={client.name} sub="Download client" />
              <ListCell primary={client.protocol} />
              <ListCell mobile>
                <Chip tone={healthTone(client.healthStatus)}>{client.healthStatus}</Chip>
              </ListCell>
            </ListRow>
          ))}
          {processors.map((processor) => (
            <ListRow key={`processor:${processor.id}`}>
              <ListNameCell name={processor.name} sub="Processor" />
              <ListCell primary={processor.provider} />
              <ListCell mobile>
                <Chip tone={processor.isEnabled ? healthTone(processor.healthStatus) : "idle"}>
                  {processor.isEnabled ? processor.healthStatus : "off"}
                </Chip>
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      )}
      <ProviderHealthDrawer selection={selectedProvider} onClose={() => setSelectedProvider(null)} />
    </ListCard>
  );

  const healthChecksCard = <HealthChecksCard checks={healthChecks.checks} onChanged={() => revalidator.revalidate()} />;

  const jobsCard = (
    <ListCard title="Background jobs" count={jobs.length ? `latest ${Math.min(jobs.length, 12)} of ${jobs.length}` : undefined}>
      {jobs.length === 0 ? (
        <ListEmpty title="No jobs have run yet" description="Searches, imports and metadata refreshes queue here as Deluno picks them up." />
      ) : (
        <ListTable columns={[{ label: "Job" }, { label: "Started by", width: "minmax(0,1fr)" }, { label: "When", width: "150px" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
          {jobs.slice(0, 12).map((job) => (
            <ListRow key={job.id}>
              <ListNameCell name={jobTypeLabel(job.jobType)} sub={job.jobType} />
              <ListCell primary={job.source} secondary={job.lastError ?? undefined} />
              <ListCell numeric primary={formatWhen(job.createdUtc, preferences)} />
              <ListCell mobile>
                <Chip tone={jobTone(job.status)}>{job.status}</Chip>
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      )}
    </ListCard>
  );

  const runtimeCard = (
    <ListCard title="This installation" count="Host settings and defaults">
      <ListTable columns={[{ label: "Setting" }, { label: "Value", width: "minmax(0,1.4fr)", align: "end" }]} chevron={false}>
        {[
          {
            label: "Port",
            value: lanAccess?.port ? String(lanAccess.port) : "Unknown"
          },
          {
            label: "Other devices on your network",
            value: lanAccess?.state === "open" ? "Can connect" : lanAccess?.state === "blocked" ? "Cannot connect" : "Unknown from here"
          },
          { label: "Sign-in", value: "Required" },
          { label: "Interface", value: settings.uiTheme },
          { label: "Uptime", value: systemInfo ? formatUptime(systemInfo.uptimeSeconds) : "Unknown" },
          { label: "Started", value: systemInfo ? relativeTime(systemInfo.processStartUtc) : "Unknown" },
          { label: "OS", value: systemInfo?.osDescription ?? "Unknown" },
          { label: ".NET runtime", value: systemInfo?.runtimeVersion ?? "Unknown" },
          { label: "Database", value: systemInfo ? `${systemInfo.databaseType} · ${systemInfo.databaseLocation}` : "Unknown" },
          { label: "App-data folder", value: systemInfo?.appDataPath ?? "Unknown" }
        ].map((row) => (
          <ListRow key={row.label}>
            <ListNameCell name={row.label} />
            <ListCell numeric align="end" primary={row.value} />
          </ListRow>
        ))}
      </ListTable>
    </ListCard>
  );

  if (location.pathname.startsWith("/system/backups")) {
    return (
      <SystemShell>
        <BackupCard initialBackups={backups} initialSettings={backupSettings}>{runtimeCard}</BackupCard>
      </SystemShell>
    );
  }

  if (location.pathname.startsWith("/system/recycle-bin")) {
    return (
      <SystemShell>
        <RecycleBinCard initialItems={recycleBin} initialSettings={recycleBinSettings}>{runtimeCard}</RecycleBinCard>
      </SystemShell>
    );
  }

  if (location.pathname.startsWith("/system/updates")) {
    return (
      <SystemShell>
        <UpgradeCard status={updateStatus}>{runtimeCard}</UpgradeCard>
      </SystemShell>
    );
  }

  if (location.pathname.startsWith("/system/audit")) {
    return (
      <SystemShell>
        {auditCard}
        {jobsCard}
      </SystemShell>
    );
  }

  return (
    <SystemShell>
      <SummaryStrip
        cells={[
          { label: "Running jobs", value: String(liveActiveJobs), help: liveActiveJobs ? "queued or working" : "nothing queued" },
          { label: "Search sources", value: `${healthyIndexers}/${indexers.length}`, help: healthyIndexers === indexers.length ? "all responding" : "one is not responding", tone: indexers.length && healthyIndexers < indexers.length ? "warning" : undefined },
          { label: "Download clients", value: `${healthyClients}/${downloadClients.length}`, help: healthyClients === downloadClients.length ? "all responding" : "one is not responding", tone: downloadClients.length && healthyClients < downloadClients.length ? "warning" : undefined },
          ...(enabledProcessors.length
            ? [{ label: "Processors", value: `${healthyProcessors}/${enabledProcessors.length}`, help: healthyProcessors === enabledProcessors.length ? "all answering" : "one is not answering", tone: healthyProcessors < enabledProcessors.length ? "warning" as const : undefined }]
            : []),
          { label: "Alerts", value: String(monitoring.alerts.length), help: monitoring.alerts.length ? "need a look" : "nothing raised", tone: monitoring.alerts.length ? "danger" : undefined },
          { label: "Health", value: String(healthChecks.summary.problemCount), help: healthChecks.summary.problemCount ? "checks need attention" : "all checks pass", tone: healthChecks.summary.unsnoozedProblemCount ? "danger" : undefined },
          { label: "Media roots", value: String(mediaRoots), help: "folders Deluno writes to" }
        ]}
      />

      <LanAccessNotice />
      {healthChecksCard}
      {monitoring.alerts.length ? <MonitoringCard monitoring={monitoring} /> : null}
      {providerCard}
      <SystemTasksCard tasks={systemTasks} />
      <MetadataMaintenanceCard onRefresh={() => revalidator.revalidate()} />
      <AutomationCard automation={automation} cycles={searchCycles} retryWindows={retryWindows} onRefresh={() => revalidator.revalidate()} />
      {jobsCard}
      {runtimeCard}
      <LogFilesCard />
    </SystemShell>
  );
}

/* ---------------------------------------------------------------- bits */

function healthTone(status: string): NonNullable<ChipProps["tone"]> {
  if (status === "healthy" || status === "ok") return "ok";
  if (status === "degraded" || status === "warning" || status === "rate-limited") return "warn";
  if (status === "unhealthy" || status === "failed" || status === "unreachable") return "bad";
  return "idle";
}

function jobTone(status: string): NonNullable<ChipProps["tone"]> {
  if (status === JOB_STATUS.FAILED) return "bad";
  if (status === JOB_STATUS.COMPLETED) return "ok";
  if (isJobActive(status as JobStatus)) return "info";
  return "idle";
}

/** "filesystem.import.execute" reads as machinery; say what it does. */
function jobTypeLabel(jobType: string) {
  const known: Record<string, string> = {
    "filesystem.import.execute": "Import a file",
    "metadata.refresh": "Refresh details",
    "search.cycle": "Search cycle",
    "notification.dispatch": "Send a notification",
    "backup.create": "Create a backup"
  };
  if (known[jobType]) return known[jobType];
  const tail = jobType.split(".").pop() ?? jobType;
  return tail.charAt(0).toUpperCase() + tail.slice(1).replace(/([A-Z])/g, " $1");
}

/**
 * The passes you can also start by hand, and what to call it.
 *
 * <p>DESIGN-007: "everything we have decided as far as a lot of the behaviour
 * can have manual overrides like if a file is missing and the schedule hasn't
 * run, a user can manually trigger a refresh of the library and it should come
 * up as missing and then the user can manually trigger a search". A person
 * should never have to wait for a timer to find out what Deluno thinks.</p>
 *
 * <p>Keyed rather than blanket, because most passes have no manual route and a
 * button that quietly does nothing is worse than no button. The endpoint here
 * runs the same service the schedule runs — there is one implementation, so
 * the answer cannot depend on which one started it.</p>
 */
const RUN_NOW: Record<string, { path: string; label: string; said: (result: LibraryFileCheckResult | LeftoverCleanupRunResult) => string }> = {
  "library.file.check": {
    path: "/api/filesystem/file-check",
    // Not "Check now": the metadata maintenance card on this same screen
    // already has one, and it checks something else entirely. Two buttons with
    // one name on one screen is a coin toss for the reader, and Playwright
    // found it before a person did.
    label: "Check files now",
    said: (raw) => {
      const result = raw as LibraryFileCheckResult;
      return result.markedMissing === 0 && result.unreachableRoots === 0
        ? "Every file Deluno is holding is still on disk."
        : [
            result.markedMissing === 1
              ? "1 title is now missing — search for it when you want it back."
              : result.markedMissing > 1
                ? `${result.markedMissing} titles are now missing — search for them when you want them back.`
                : null,
            result.unreachableRoots > 0
              ? `${result.unreachableRoots} library ${result.unreachableRoots === 1 ? "root is" : "roots are"} unreachable, and nothing in ${result.unreachableRoots === 1 ? "it was" : "them was"} touched.`
              : null
          ]
            .filter(Boolean)
            .join(" ");
    }
  },
  // #958: the same service the "Clean up leftover downloads" schedule runs
  // (POST /api/download-clients/leftovers/cleanup), so a person never has to
  // wait for the timer to prove a leftover is really gone.
  "download.leftover.cleanup": {
    path: "/api/download-clients/leftovers/cleanup",
    label: "Run now",
    said: (raw) => {
      const result = raw as LeftoverCleanupRunResult;
      if (result.removed === 0 && result.held === 0) {
        return "No leftovers to clean up right now.";
      }
      return [
        result.removed > 0
          ? `${result.removed} leftover${result.removed === 1 ? "" : "s"} removed.`
          : null,
        result.held > 0
          ? `${result.held} held back — still in use, too young, or unproven.`
          : null
      ]
        .filter(Boolean)
        .join(" ");
    }
  }
};

interface LibraryFileCheckResult {
  markedMissing: number;
  unreachableRoots: number;
}

/** POST /api/download-clients/leftovers/cleanup response (#932, #958). */
interface LeftoverCleanupRunResult {
  removed: number;
  held: number;
}

function SystemTasksCard({ tasks }: { tasks: SystemTaskItem[] }) {
  const { preferences } = useDisplayPreferences();
  const revalidator = useRevalidator();
  const [running, setRunning] = useState<string | null>(null);
  const [said, setSaid] = useState<{ key: string; message: string } | null>(null);

  async function runNow(task: SystemTaskItem) {
    const trigger = RUN_NOW[task.key];
    if (!trigger) return;

    setRunning(task.key);
    setSaid(null);
    try {
      const result = await fetchJson<LibraryFileCheckResult | LeftoverCleanupRunResult>(trigger.path, { method: "POST" });
      setSaid({ key: task.key, message: trigger.said(result) });
      revalidator.revalidate();
    } catch {
      setSaid({ key: task.key, message: "That did not finish. Nothing was changed." });
    } finally {
      setRunning(null);
    }
  }

  return (
    <ListCard title="System tasks" count={`${tasks.length} recurring passes · durable across restarts`}>
      <ListTable columns={[{ label: "Task" }, { label: "Cadence", width: "140px" }, { label: "Last execution", width: "180px" }, { label: "Next execution", width: "180px" }, { label: "Result", width: LIST_TRACK.status, mobile: true }, { label: "", width: LIST_TRACK.action, align: "end", mobile: true }]} chevron={false}>
        {tasks.map((task) => (
          <ListRow key={task.key}>
            <ListNameCell
              name={task.name}
              sub={said?.key === task.key ? said.message : task.description}
            />
            <ListCell primary={`${formatInterval(task.intervalSeconds)}${task.isConfigurable ? " · configured" : ""}`} />
            <ListCell numeric primary={task.lastCompletedUtc ? formatWhen(task.lastCompletedUtc, preferences) : "Not run yet"} secondary={task.lastDurationMs === null ? undefined : `${task.lastDurationMs}ms`} />
            <ListCell numeric primary={task.nextRunUtc ? formatWhen(task.nextRunUtc, preferences) : "Not scheduled"} />
            <ListCell mobile>
              <Chip tone={jobTone(task.lastResult)}>{task.lastResult}</Chip>
            </ListCell>
            <ListCell mobile align="end">
              {RUN_NOW[task.key] ? (
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={running !== null}
                  onClick={() => void runNow(task)}
                >
                  <TimerReset aria-hidden className="h-3.5 w-3.5" />
                  {running === task.key ? "Checking…" : RUN_NOW[task.key]!.label}
                </Button>
              ) : null}
            </ListCell>
          </ListRow>
        ))}
      </ListTable>
    </ListCard>
  );
}

function formatInterval(seconds: number) {
  if (seconds % 86400 === 0) return `${seconds / 86400}d`;
  if (seconds % 3600 === 0) return `${seconds / 3600}h`;
  if (seconds % 60 === 0) return `${seconds / 60}m`;
  return `${seconds}s`;
}

function RecycleBinCard({
  initialItems,
  initialSettings,
  children
}: {
  initialItems: RecycleBinItem[];
  initialSettings: RecycleBinSettings;
  children: ReactNode;
}) {
  const { preferences } = useDisplayPreferences();
  const [items, setItems] = useState(initialItems);
  const [savedSettings, setSavedSettings] = useState(initialSettings);
  const [settings, setSettings] = useState(initialSettings);
  const [busy, setBusy] = useState<string | null>(null);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);
  const [deleteItem, setDeleteItem] = useState<RecycleBinItem | null>(null);
  const [cleanupPreview, setCleanupPreview] = useState<RecycleBinCleanupPreview | null>(null);
  const dirty = useMemo(() => JSON.stringify(settings) !== JSON.stringify(savedSettings), [settings, savedSettings]);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);

  async function reload() {
    const [nextItems, nextSettings] = await Promise.all([
      fetchJson<RecycleBinItem[]>("/api/recycle-bin"),
      fetchJson<RecycleBinSettings>("/api/recycle-bin/settings")
    ]);
    setItems(nextItems);
    if (!dirty) {
      setSavedSettings(nextSettings);
      setSettings(nextSettings);
    }
  }

  async function save() {
    if (!dirty || saveState === "saving") return;
    setSaveState("saving");
    setMessage(null);
    try {
      const next = await fetchJson<RecycleBinSettings>("/api/recycle-bin/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(settings)
      });
      setSavedSettings(next);
      setSettings(next);
      setSaveState("saved");
      setMessage("Recycle-bin settings saved.");
      await reload();
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Recycle-bin settings could not be saved.");
    }
  }

  async function restore(item: RecycleBinItem) {
    setBusy(`restore:${item.id}`);
    setMessage(null);
    try {
      const result = await fetchJson<{ message: string }>(`/api/recycle-bin/${item.id}/restore`, { method: "POST" });
      setMessage(result.message);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "The item could not be restored.");
    } finally {
      setBusy(null);
    }
  }

  async function permanentlyDelete(item: RecycleBinItem) {
    setDeleteItem(null);
    setBusy(`delete:${item.id}`);
    setMessage(null);
    try {
      const result = await fetchJson<{ message: string }>(`/api/recycle-bin/${item.id}`, { method: "DELETE" });
      setMessage(result.message);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "The item could not be deleted.");
    } finally {
      setBusy(null);
    }
  }

  /**
   * Asks what an empty would take, then shows it and waits.
   *
   * <p>DESIGN-007 decision 15: "Enforce retention automatically, and show what
   * a manual empty takes". This used to delete first and count afterwards,
   * which is a report rather than a choice — and permanent deletion is the one
   * place a report after the fact is worth nothing.</p>
   *
   * <p>The preview comes from the server, which decides what to take with the
   * same code that then takes it. Working it out here from the item list would
   * be a second implementation of the retention rule, which is how a dialog
   * saying "3 items" ends up removing five.</p>
   */
  async function previewCleanup() {
    setBusy("cleanup");
    setMessage(null);
    try {
      setCleanupPreview(await fetchJson<RecycleBinCleanupPreview>("/api/recycle-bin/cleanup/preview"));
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Deluno could not work out what an empty would take.");
    } finally {
      setBusy(null);
    }
  }

  async function cleanup() {
    setBusy("cleanup");
    setMessage(null);
    try {
      const result = await fetchJson<{ removed: number }>("/api/recycle-bin/cleanup", { method: "POST" });
      setMessage(result.removed ? `Removed ${result.removed} expired recycle-bin item${result.removed === 1 ? "" : "s"}.` : "No recycle-bin items were ready to expire.");
      setCleanupPreview(null);
      await reload();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Recycle-bin cleanup failed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Trash2 className="h-4 w-4 text-primary" />
            Recycle bin
          </CardTitle>
          <CardDescription>Library removals and replaced files stay recoverable until their retention window expires.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-[var(--page-gap)]">
          {message ? <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">{message}</p> : null}
          <div className="grid gap-2 sm:grid-cols-2">
            <Field label="Retention days">
              <Input
                type="number"
                min={1}
                max={365}
                value={settings.retentionDays}
                onChange={(event) => setSettings((current) => ({ ...current, retentionDays: Number(event.target.value || 30) }))}
              />
            </Field>
            <Field label="Maximum size (MB)">
              <Input
                type="number"
                min={1}
                value={settings.maxSizeMb}
                onChange={(event) => setSettings((current) => ({ ...current, maxSizeMb: Number(event.target.value || 102400) }))}
              />
            </Field>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="outline" disabled={busy !== null} onClick={() => void previewCleanup()}>
              {busy === "cleanup" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
              Clean expired items
            </Button>
            <span className="text-xs text-muted-foreground">{items.length} stored item{items.length === 1 ? "" : "s"}</span>
          </div>
          {items.length ? (
            <div className="space-y-2">
              {items.map((item) => (
                <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-3">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium text-foreground">{item.libraryName} · {item.mediaType}{item.isDirectory ? " · folder" : " · file"}</p>
                      <p className="mt-1 break-all text-xs text-muted-foreground">{item.originalPath}</p>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {formatBytes(item.sizeBytes)} · removed {formatWhen(item.createdUtc, preferences)} · expires {formatWhen(item.expiresUtc, preferences)}
                      </p>
                    </div>
                    <div className="flex shrink-0 gap-2">
                      <Button type="button" size="sm" variant="outline" disabled={busy !== null} onClick={() => void restore(item)}>
                        {busy === `restore:${item.id}` ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                        Restore
                      </Button>
                      <Button type="button" size="sm" variant="ghost" disabled={busy !== null} onClick={() => setDeleteItem(item)}>
                        Delete permanently
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <ListEmpty title="Recycle bin is empty" description="Files removed from a library will appear here when the recycle-bin policy is enabled." />
          )}
        </CardContent>
      </Card>
      {children}
      <PageFooter
        state={footerState}
        message={message}
        saveLabel="Save recycle-bin settings"
        saveType="button"
        disabled={!dirty || busy !== null}
        onSave={() => void save()}
      />
      <ConfirmDialog
        open={deleteItem !== null}
        onOpenChange={(open) => { if (!open) setDeleteItem(null); }}
        title="Delete this item permanently?"
        description="The stored library file will be removed and cannot be restored by Deluno."
        confirmLabel="Delete permanently"
        busy={busy?.startsWith("delete:") ?? false}
        onConfirm={() => { if (deleteItem) void permanentlyDelete(deleteItem); }}
      />
      <ConfirmDialog
        open={cleanupPreview !== null}
        onOpenChange={(open) => { if (!open) setCleanupPreview(null); }}
        title={cleanupPreview?.items.length ? "Empty these from the recycle bin?" : "Nothing is ready to be emptied"}
        description={cleanupPreview ? describeCleanup(cleanupPreview, formatBytes) : ""}
        confirmLabel={cleanupPreview?.items.length ? "Empty them" : "Close"}
        confirmVariant={cleanupPreview?.items.length ? "destructive" : "default"}
        busy={busy === "cleanup"}
        onConfirm={() => {
          if (cleanupPreview?.items.length) void cleanup();
          else setCleanupPreview(null);
        }}
      />
    </>
  );
}

function AutomationCard({
  automation,
  cycles,
  retryWindows,
  onRefresh
}: {
  automation: LibraryAutomationStateItem[];
  cycles: SearchCycleRunItem[];
  retryWindows: SearchRetryWindowItem[];
  onRefresh: () => void;
}) {
  const { preferences } = useDisplayPreferences();
  const [busyLibraryId, setBusyLibraryId] = useState<string | null>(null);
  const running = automation.filter((item) => isJobActive(item.status as JobStatus) || item.searchRequested).length;
  const latest = cycles[0] ?? null;
  const waiting = retryWindows.filter((item) => new Date(item.nextEligibleUtc).getTime() > Date.now()).length;

  async function runNow(libraryId: string) {
    setBusyLibraryId(libraryId);
    try {
      const response = await authedFetch(`/api/libraries/${libraryId}/search-now`, { method: "POST" });
      if (!response.ok) {
        throw new Error(await readErrorText(response, "Search could not be requested."));
      }
      onRefresh();
    } finally {
      setBusyLibraryId(null);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <TimerReset className="h-4 w-4 text-primary" />
          Search automation
        </CardTitle>
        <CardDescription>
          Scheduled searches for missing and upgradeable titles.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-[var(--page-gap)]">
        <div className="grid grid-cols-3 gap-2">
          <AutomationMetric label="Active" value={running} />
          <AutomationMetric label="Runs" value={cycles.length} />
          <AutomationMetric label="Waiting" value={waiting} />
        </div>

        {latest ? (
          <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
            <div className="flex items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="truncate text-sm font-semibold text-foreground">{latest.libraryName}</p>
                <p className="mt-1 text-xs text-muted-foreground">
                  {latest.triggerKind} / {formatWhen(latest.startedUtc, preferences)}
                </p>
              </div>
              <JobStatusBadge status={latest.status} />
            </div>
            <div className="mt-3 grid grid-cols-3 gap-2 text-center">
              <AutomationMetric label="Checked" value={latest.plannedCount} compact />
              <AutomationMetric label="Sent" value={latest.queuedCount} compact />
              <AutomationMetric label="Retry" value={latest.skippedCount} compact />
            </div>
          </div>
        ) : (
          <p className="rounded-xl border border-hairline bg-surface-1 p-4 text-sm text-muted-foreground">
            No search cycles have run yet. Once libraries are enabled, Deluno will show every scheduled and manual pass here.
          </p>
        )}

        {retryWindows.length ? (
          <div className="space-y-2">
            <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.16em] text-muted-foreground">Searches held back until</p>
            {retryWindows.slice(0, 4).map((item) => (
              <div key={`${item.entityType}:${item.entityId}:${item.actionKind}`} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">{item.mediaType} / {item.actionKind}</p>
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">{item.lastResult || "Last search recorded"}</p>
                </div>
                <span className="text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{formatWhen(item.nextEligibleUtc, preferences)}</span>
              </div>
            ))}
          </div>
        ) : null}

        {automation.length ? (
          <div className="space-y-2">
            <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.16em] text-muted-foreground">Libraries</p>
            {automation.slice(0, 5).map((item) => (
              <div key={item.libraryId} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">{item.libraryName}</p>
                  <p className="text-[length:var(--type-caption)] text-muted-foreground">
                    {item.status}{item.nextSearchUtc ? ` / next ${formatWhen(item.nextSearchUtc, preferences)}` : ""}
                  </p>
                </div>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  className="h-8 shrink-0"
                  disabled={busyLibraryId !== null}
                  onClick={() => void runNow(item.libraryId)}
                >
                  {busyLibraryId === item.libraryId ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                  Run now
                </Button>
              </div>
            ))}
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

function AutomationMetric({ label, value, compact = false }: { label: string; value: number | string; compact?: boolean }) {
  return (
    <div className="rounded-lg border border-hairline bg-background/35 px-3 py-2">
      <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.14em] text-muted-foreground">{label}</p>
      <p className={`${compact ? "text-lg" : "text-xl"} tabular font-display font-semibold text-foreground`}>{value}</p>
    </div>
  );
}

function OperationsFlowCard() {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Operational flow</CardTitle>
        <CardDescription>Follow this order when changing the install.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-2">
        {[
          ["A", "Backup", "Create a fresh app backup before risky changes."],
          ["B", "Restore", "Use restore only when rolling back or migrating."],
          ["C", "Upgrade", "Check updates, download in-app, and restart when ready after backup."]
        ].map(([step, title, copy]) => (
          <div key={step} className="flex gap-3 rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
            <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg border border-primary/20 bg-primary/10 text-xs font-bold text-primary">
              {step}
            </span>
            <div>
              <p className="text-sm font-semibold text-foreground">{title}</p>
              <p className="text-xs leading-relaxed text-muted-foreground">{copy}</p>
            </div>
          </div>
        ))}
      </CardContent>
    </Card>
  );
}

function BackupCard({
  initialBackups,
  initialSettings,
  children
}: {
  initialBackups: BackupItem[];
  initialSettings: BackupSettingsSnapshot;
  children: ReactNode;
}) {
  const { preferences } = useDisplayPreferences();
  const [backups, setBackups] = useState(initialBackups);
  const [savedSettings, setSavedSettings] = useState(initialSettings);
  const [settings, setSettings] = useState(initialSettings);
  const [busy, setBusy] = useState<string | null>(null);
  const actionStatus = useSaveStatus();
  const [scheduleSaveState, setScheduleSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [scheduleMessage, setScheduleMessage] = useState<string | null>(null);
  const [restorePreview, setRestorePreview] = useState<RestorePreviewResponse | null>(null);
  const [restoreFile, setRestoreFile] = useState<File | null>(null);
  const [showRestoreConfirm, setShowRestoreConfirm] = useState(false);
  const dirty = useMemo(() => JSON.stringify(settings) !== JSON.stringify(savedSettings), [settings, savedSettings]);
  const scheduleState: DrawerSaveState = resolveDrawerSaveState(scheduleSaveState, dirty);
  useUnsavedChanges(dirty, "Your backup schedule edits have not been saved.", saveSchedule);

  useEffect(() => {
    if (shouldClearSaveState(scheduleSaveState, dirty)) setScheduleSaveState(undefined);
  }, [dirty, scheduleSaveState]);

  async function reload() {
    const [nextBackups, nextSettings] = await Promise.all([
      fetchJson<BackupItem[]>("/api/backups"),
      fetchJson<BackupSettingsSnapshot>("/api/backups/settings")
    ]);
    setBackups(nextBackups);
    if (!dirty) {
      setSavedSettings(nextSettings);
      setSettings(nextSettings);
    }
  }

  async function createBackup() {
    setBusy("create");
    actionStatus.markSyncing("Creating backup…");
    try {
      await fetchJson("/api/backups", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason: "manual" })
      });
      await reload();
      actionStatus.markSaved("Backup created");
    } catch (error) {
      actionStatus.markError(error instanceof Error ? error.message : "Backup failed");
    } finally {
      setBusy(null);
    }
  }

  async function saveSchedule() {
    if (scheduleState === "saving") return;
    setBusy("schedule");
    setScheduleSaveState("saving");
    setScheduleMessage(null);
    try {
      const next = await fetchJson<BackupSettingsSnapshot>("/api/backups/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(settings)
      });
      setSavedSettings(next);
      setSettings(next);
      setScheduleSaveState("saved");
    } catch (error) {
      setScheduleSaveState("error");
      setScheduleMessage(error instanceof Error ? error.message : "Schedule could not be saved");
    } finally {
      setBusy(null);
    }
  }

  async function previewRestore(file: File) {
    setBusy("preview");
    actionStatus.markSyncing("Checking backup…");
    setRestoreFile(file);
    const formData = new FormData();
    formData.append("file", file);
    try {
      const response = await authedFetch("/api/backups/restore/preview", {
        method: "POST",
        body: formData
      });
      if (!response.ok) throw new Error(await response.text());
      setRestorePreview((await response.json()) as RestorePreviewResponse);
      actionStatus.reset();
    } catch (error) {
      setRestorePreview(null);
      actionStatus.markError(error instanceof Error ? error.message : "Restore preview failed");
    } finally {
      setBusy(null);
    }
  }

  async function restoreBackup() {
    if (!restoreFile) return;
    setShowRestoreConfirm(true);
  }

  async function confirmRestore() {
    setShowRestoreConfirm(false);
    if (!restoreFile) return;
    setBusy("restore");
    actionStatus.markSyncing("Restoring backup…");
    const formData = new FormData();
    formData.append("file", restoreFile);
    try {
      const response = await authedFetch("/api/backups/restore", {
        method: "POST",
        body: formData
      });
      if (!response.ok) throw new Error(await response.text());
      const result = await response.json() as { message?: string };
      actionStatus.markSaved(result.message ?? "Restore completed. Restart Deluno before continuing.");
    } catch (error) {
      actionStatus.markError(error instanceof Error ? error.message : "Restore failed");
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck className="h-4 w-4 text-primary" />
          Backup and restore
        </CardTitle>
        <CardDescription>Protect the app database, settings, queues, and integration cache before changes.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
        <SaveStatus state={actionStatus.state} message={actionStatus.message} />
        <div className="flex flex-wrap gap-2">
          <Button type="button" onClick={() => void createBackup()} disabled={busy === "create"}>
            {busy === "create" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
            Create backup
          </Button>
          {backups[0] ? (
            <Button type="button" variant="outline" asChild>
              <a href={`/api/backups/${backups[0].id}/download`}>
                <Download className="h-4 w-4" />
                Download latest
              </a>
            </Button>
          ) : null}
        </div>

        <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
          <p className="text-sm font-semibold text-foreground">Automatic schedule</p>
          <div className="mt-3 grid gap-2 sm:grid-cols-2">
            <SwitchRow
              label="Enable scheduled backups"
              checked={settings.enabled}
              onCheckedChange={(enabled) => setSettings((current) => ({ ...current, enabled }))}
            />
            <Field label="Backup frequency" hideLabel>
              <Select
                value={settings.frequency}
                onChange={(event) => setSettings((current) => ({ ...current, frequency: event.target.value }))}
                options={[
                  { value: "daily", label: "Daily" },
                  { value: "weekly", label: "Weekly" },
                  { value: "monthly", label: "Monthly" }
                ]}
              />
            </Field>
            <Field label="Backup time" hideLabel>
              <Input
                type="time"
                value={settings.timeOfDay}
                onChange={(event) => setSettings((current) => ({ ...current, timeOfDay: event.target.value }))}
              />
            </Field>
            <Field label="Backup retention count" hideLabel>
              <Input
                type="number"
                min={1}
                max={100}
                value={settings.retentionCount}
                onChange={(event) => setSettings((current) => ({ ...current, retentionCount: Number(event.target.value || 7) }))}
              />
            </Field>
          </div>
          <Field label="Backup folder" hideLabel className="mt-2">
            <PathInput
              value={settings.backupFolder}
              onChange={(value) => setSettings((current) => ({ ...current, backupFolder: value }))}
              placeholder="Backup folder"
              browseTitle="Choose backup folder"
            />
          </Field>
          <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
            <p className="text-xs text-muted-foreground">
              Next run: {settings.nextRunUtc ? formatWhen(settings.nextRunUtc, preferences) : "Not scheduled"} · Retains latest {settings.retentionCount} backup{settings.retentionCount === 1 ? "" : "s"}
            </p>
          </div>
        </div>

        <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
          <p className="text-sm font-semibold text-foreground">Restore from backup</p>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
            Restore replaces Deluno data files. Create a backup first and restart Deluno after restore.
          </p>
          <p className="mt-2 rounded-lg border border-warning/35 bg-warning/10 px-3 py-2 text-xs leading-relaxed text-warning">
            Restore is intentionally a two-step flow: preview first, then restore only after Deluno confirms the archive contains a valid manifest.
          </p>
          <Field label="Backup archive" hideLabel className="mt-3">
            <Input
              type="file"
              accept=".zip,application/zip"
              onChange={(event) => {
                const file = event.target.files?.[0];
                if (file) void previewRestore(file);
              }}
            />
          </Field>
          {restorePreview ? (
            <div className="mt-3 rounded-xl border border-hairline bg-background/40 p-3 text-sm">
              <p className={restorePreview.valid ? "text-success" : "text-destructive"}>{restorePreview.message}</p>
              {restorePreview.manifest ? (
                <p className="mt-1 text-xs text-muted-foreground">
                  {restorePreview.manifest.files.length} files · {formatWhen(restorePreview.manifest.createdUtc, preferences)}
                </p>
              ) : null}
              <Button
                type="button"
                className="mt-3"
                variant="outline"
                disabled={!restorePreview.valid || busy === "restore"}
                onClick={() => void restoreBackup()}
              >
                {busy === "restore" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
                Restore backup
              </Button>
            </div>
          ) : null}
        </div>

        <div className="space-y-2">
          {backups.slice(0, 4).map((backup) => (
            <div key={backup.id} className="flex items-center justify-between gap-3 rounded-xl border border-hairline bg-surface-1 p-3">
              <div className="min-w-0">
                <p className="truncate text-sm font-medium text-foreground">{backup.fileName}</p>
                <p className="text-xs text-muted-foreground">{formatBytes(backup.sizeBytes)} · {formatWhen(backup.createdUtc, preferences)}</p>
              </div>
              <Button type="button" variant="ghost" size="sm" asChild>
                <a href={`/api/backups/${backup.id}/download`}>Download</a>
              </Button>
            </div>
          ))}
          {backups.length === 0 ? <p className="text-sm text-muted-foreground">No backups yet.</p> : null}
        </div>
        </CardContent>
      </Card>
      {children}

      <PageFooter
        state={scheduleState}
        message={scheduleMessage}
        saveLabel="Save backup schedule"
        saveType="button"
        onSave={() => void saveSchedule()}
      />

      <ConfirmDialog
        open={showRestoreConfirm}
        onOpenChange={setShowRestoreConfirm}
        title="Restore this backup?"
        description="Deluno's data files will be replaced with this backup. Restart Deluno immediately after the restore completes."
        confirmLabel="Restore now"
        confirmVariant="destructive"
        busy={busy === "restore"}
        onConfirm={() => void confirmRestore()}
      />
    </>
  );
}

/**
 * Renders a release-notes body as plain text with the lightest possible
 * markdown: a line starting with "- " becomes a bullet, "**bold**" becomes
 * bold text. Never HTML -- the text comes from RELEASE_NOTES.md in the build,
 * but it is still rendered as data, not injected as markup (#870).
 */
function ReleaseNotesBody({ text }: { text: string }) {
  const lines = text.split("\n").filter((line) => line.trim().length > 0);
  const renderInline = (line: string) =>
    line.split(/(\*\*[^*]+\*\*)/g).map((part, index) =>
      part.startsWith("**") && part.endsWith("**") ? (
        <strong key={index} className="font-semibold text-foreground">{part.slice(2, -2)}</strong>
      ) : (
        <span key={index}>{part}</span>
      )
    );

  return (
    <ul className="space-y-1">
      {lines.map((line, index) => {
        const bullet = line.trim().replace(/^-\s+/, "");
        return (
          <li key={index} className="list-disc pl-4">
            {renderInline(bullet)}
          </li>
        );
      })}
    </ul>
  );
}

function UpgradeCard({ status, children }: { status: UpdateStatusResponse; children: ReactNode }) {
  const [current, setCurrent] = useState(status);
  const [savedPreferences, setSavedPreferences] = useState<UpdatePreferencesResponse | null>(null);
  const [preferences, setPreferences] = useState<UpdatePreferencesResponse | null>(null);
  const [busyAction, setBusyAction] = useState<"check" | "download" | "prepare" | "restart" | "prefs" | null>(null);
  const actionStatus = useSaveStatus();
  const [preferencesSaveState, setPreferencesSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [preferencesMessage, setPreferencesMessage] = useState<string | null>(null);
  const preferencesDirty = useMemo(
    () => preferences !== null && savedPreferences !== null && JSON.stringify(preferences) !== JSON.stringify(savedPreferences),
    [preferences, savedPreferences]
  );
  const preferencesState: DrawerSaveState = resolveDrawerSaveState(preferencesSaveState, preferencesDirty);
  useUnsavedChanges(preferencesDirty, "Your update preference edits have not been saved.", () => {
    if (preferences) return savePreferences(preferences);
  });

  async function refreshStatus() {
    const next = await fetchJson<UpdateStatusResponse>("/api/updates/status");
    setCurrent(next);
  }

  async function loadPreferences() {
    const next = await fetchJson<UpdatePreferencesResponse>("/api/updates/preferences");
    setSavedPreferences(next);
    setPreferences(next);
  }

  async function runAction(action: "check" | "download" | "prepare" | "restart") {
    setBusyAction(action);
    actionStatus.markSyncing();
    try {
      const path =
        action === "check"
          ? "/api/updates/check"
          : action === "download"
            ? "/api/updates/download"
            : action === "prepare"
              ? "/api/updates/apply-on-restart"
              : "/api/updates/restart-now";
      const result = await fetchJson<UpdateActionResponse>(path, { method: "POST" });
      setCurrent(result.status);
      actionStatus.markSaved(result.message);
    } catch (error) {
      actionStatus.markError(error instanceof Error ? error.message : "Update action failed");
      await refreshStatus();
    } finally {
      setBusyAction(null);
    }
  }

  async function savePreferences(next: UpdatePreferencesResponse) {
    if (preferencesState === "saving") return;
    setBusyAction("prefs");
    setPreferencesSaveState("saving");
    setPreferencesMessage(null);
    try {
      const saved = await fetchJson<UpdatePreferencesResponse>("/api/updates/preferences", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(savedPreferencePayload(next))
      });
      setSavedPreferences(saved);
      setPreferences(saved);
      setPreferencesSaveState("saved");
      await refreshStatus();
    } catch (error) {
      setPreferencesSaveState("error");
      setPreferencesMessage(error instanceof Error ? error.message : "Could not save preferences");
    } finally {
      setBusyAction(null);
    }
  }

  useEffect(() => {
    if (preferences === null) {
      void loadPreferences();
    }
  }, [preferences]);

  useEffect(() => {
    if (shouldClearSaveState(preferencesSaveState, preferencesDirty)) setPreferencesSaveState(undefined);
  }, [preferencesDirty, preferencesSaveState]);

  const isDocker = current.installKind === "docker";
  const canControl = current.isInstalled && !isDocker;
  const controlsDisabledReason = !current.isInstalled
    ? "In-app update controls require the packaged Windows install."
    : isDocker
      ? "Docker updates are applied by pulling a new image and recreating the container."
      : null;

  return (
    <>
      <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <RotateCcw className="h-4 w-4 text-primary" />
          Updates
        </CardTitle>
        <CardDescription>Version status, channel, behaviour mode, and restart flow.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <SaveStatus state={actionStatus.state} message={actionStatus.message} />
        <HealthRow label="Current version" status={current.currentVersion} />
        <HealthRow label="Latest version" status={current.latestVersion ?? "No update available"} />
        <HealthRow label="Channel" status={current.channel} />
        <HealthRow label="Install kind" status={installKindLabel(current.installKind)} />
        {isDocker && current.currentImageRef ? <HealthRow label="Image" status={current.currentImageRef} /> : null}
        {isDocker && current.currentImageDigest ? <HealthRow label="Image digest" status={current.currentImageDigest} /> : null}
        <HealthRow label="State" status={updateStateLabel(current.state)} />
        <p className="text-sm leading-relaxed text-muted-foreground">{current.message}</p>
        {current.progressPercent !== null ? (
          <p className="text-xs text-muted-foreground">Download progress: {current.progressPercent}%</p>
        ) : null}
        {controlsDisabledReason ? (
          <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-xs leading-relaxed text-muted-foreground">
            {controlsDisabledReason}
          </p>
        ) : null}

        {preferences ? (
          <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)] space-y-3">
            <p className="text-sm font-semibold text-foreground">Update behaviour</p>
            <Field label="Update behaviour" hideLabel>
              <Select
                value={preferences.mode}
                onChange={(event) => setPreferences((currentValue) => currentValue ? { ...currentValue, mode: event.target.value } : currentValue)}
                disabled={busyAction === "prefs"}
                options={[
                  { value: "notify-only", label: "Notify only" },
                  { value: "download-background", label: "Download in background" },
                  { value: "download-apply-on-restart", label: "Download and apply on next restart" }
                ]}
              />
            </Field>
            <p className="text-xs text-muted-foreground">
              {updateModeDescription(preferences.mode)}
            </p>
            <SwitchRow
              label="Check for updates automatically"
              checked={preferences.autoCheck}
              onCheckedChange={(autoCheck) => setPreferences((currentValue) => currentValue ? { ...currentValue, autoCheck } : currentValue)}
              disabled={busyAction === "prefs"}
            />
          </div>
        ) : null}

        {canControl ? (
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" disabled={busyAction !== null || !current.canCheck} onClick={() => void runAction("check")}>
              {busyAction === "check" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
              Check now
            </Button>
            <Button type="button" variant="outline" disabled={busyAction !== null || !current.canDownload} onClick={() => void runAction("download")}>
              {busyAction === "download" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
              Download update
            </Button>
            <Button type="button" variant="outline" disabled={busyAction !== null || !current.canApply} onClick={() => void runAction("prepare")}>
              {busyAction === "prepare" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <TimerReset className="h-4 w-4" />}
              Apply on restart
            </Button>
            <Button type="button" disabled={busyAction !== null || !current.canApply || !current.restartRequired} onClick={() => void runAction("restart")}>
              {busyAction === "restart" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
              Restart and apply
            </Button>
          </div>
        ) : null}

        {current.notes.map((note) => (
          <p key={note} className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-xs leading-relaxed text-muted-foreground">
            {note}
          </p>
        ))}

        {current.updateAvailable && current.changelog ? (
          <div className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)] space-y-1">
            <p className="text-sm font-semibold text-foreground">What's new in {current.latestVersion}</p>
            <p className="whitespace-pre-line text-xs leading-relaxed text-muted-foreground">{current.changelog}</p>
          </div>
        ) : null}

        {current.appliedUpdates && current.appliedUpdates.length > 0 ? (
          <div className="space-y-1">
            <p className="text-sm font-semibold text-foreground">Update history</p>
            <ul className="space-y-1">
              {current.appliedUpdates.map((entry) => (
                <li key={`${entry.version}-${entry.appliedAtUtc}`} className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-xs">
                  <div className="flex items-center justify-between">
                    <span className="font-medium text-foreground">{entry.version}</span>
                    <span className="text-muted-foreground">{new Date(entry.appliedAtUtc).toLocaleString()}</span>
                  </div>
                  {entry.notes ? (
                    <details className="mt-1">
                      <summary className="cursor-pointer text-muted-foreground">What changed</summary>
                      <div className="mt-1 text-muted-foreground">
                        <ReleaseNotesBody text={entry.notes} />
                      </div>
                    </details>
                  ) : (
                    <p className="mt-1 text-muted-foreground">No notes for this version.</p>
                  )}
                </li>
              ))}
            </ul>
          </div>
        ) : null}
        </CardContent>
      </Card>
      {children}

      <PageFooter
        state={preferencesState}
        message={preferencesMessage}
        saveLabel="Save update preferences"
        saveType="button"
        disabled={preferences === null}
        onSave={() => preferences && void savePreferences(preferences)}
      />
    </>
  );
}

/**
 * Every health check Deluno runs, in one list (#909): last result, when it
 * last ran, and -- for a problem -- the cause and fix in plain words. Stays
 * on screen until the same check reports ok again; a snooze hides it from
 * asking for attention without pretending it passed.
 */
/** What a processor-output safety-net row's detailsJson carries (#921). */
interface ProcessorOutputItemDetails {
  libraryId: string;
  path: string;
  kind: "file" | "folder" | "scraps";
  isMedia: boolean;
}

function processorOutputDetails(check: HealthCheckResultItem): ProcessorOutputItemDetails | null {
  if (!check.id.startsWith("processor-output:") || !check.detailsJson) return null;
  try {
    return JSON.parse(check.detailsJson) as ProcessorOutputItemDetails;
  } catch {
    return null;
  }
}

function HealthChecksCard({ checks, onChanged }: { checks: HealthCheckResultItem[]; onChanged: () => void }) {
  const navigate = useNavigate();
  const [busyId, setBusyId] = useState<string | null>(null);
  const problems = checks.filter((check) => check.status !== "ok");
  const sorted = [...checks].sort((a, b) => {
    const rank = (status: string) => (status === "problem" ? 0 : status === "unknown" ? 1 : 2);
    return rank(a.status) - rank(b.status);
  });

  function importProcessorOutputItem(details: ProcessorOutputItemDetails) {
    const params = new URLSearchParams({ manualImportPath: details.path, manualImportMediaType: details.kind === "folder" ? "tv" : "movies" });
    navigate(`/queue?${params.toString()}`);
  }

  async function deleteProcessorOutputItem(check: HealthCheckResultItem, details: ProcessorOutputItemDetails) {
    setBusyId(check.id);
    try {
      const response = await authedFetch("/api/integrations/processors/output/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryId: details.libraryId, path: details.path })
      });
      if (response.ok) {
        onChanged();
      }
    } catch {
      // The row simply stays; nothing silently succeeded.
    } finally {
      setBusyId(null);
    }
  }

  async function snooze(check: HealthCheckResultItem) {
    setBusyId(check.id);
    try {
      await authedFetch(`/api/system/health/${encodeURIComponent(check.id)}/snooze`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ hours: 24 })
      });
      onChanged();
    } catch {
      // The list simply keeps showing the problem; nothing silently succeeded.
    } finally {
      setBusyId(null);
    }
  }

  async function unsnooze(check: HealthCheckResultItem) {
    setBusyId(check.id);
    try {
      await authedFetch(`/api/system/health/${encodeURIComponent(check.id)}/unsnooze`, { method: "POST" });
      onChanged();
    } catch {
      // As above.
    } finally {
      setBusyId(null);
    }
  }

  return (
    <ListCard
      title="Health"
      count={problems.length === 0 ? "everything checked out" : `${problems.length} need${problems.length === 1 ? "s" : ""} attention`}
    >
      {sorted.length === 0 ? (
        <ListEmpty title="No checks have run yet" description="Deluno checks download clients, indexers, libraries and connections every fifteen minutes." />
      ) : (
        <ListTable
          columns={[
            { label: "Check" },
            { label: "Result", width: "minmax(0,1.6fr)" },
            { label: "Status", width: LIST_TRACK.status, mobile: true }
          ]}
          chevron={false}
        >
          {sorted.map((check) => {
            const isSnoozed = check.snoozedUntilUtc !== null && new Date(check.snoozedUntilUtc).getTime() > Date.now();
            // A safety-net row for one unexpected item in a processor output
            // folder (#921) is acted on directly -- Import or Delete -- not
            // snoozed, because snoozing only hides a problem that stays true;
            // this one is meant to be dealt with and then gone.
            const outputDetails = processorOutputDetails(check);
            return (
              <ListRow key={check.id}>
                <ListNameCell name={check.name} sub={categoryLabel(check.category)} />
                <ListCell
                  primary={check.message}
                  secondary={check.status === "problem" && (check.cause || check.fix) ? [check.cause, check.fix].filter(Boolean).join(" ") : undefined}
                />
                <ListCell mobile>
                  <div className="flex flex-col items-end gap-1.5">
                    <Chip tone={check.status === "ok" ? "ok" : check.status === "problem" ? (isSnoozed ? "idle" : "bad") : "warn"}>
                      {check.status === "ok" ? "ok" : isSnoozed ? "snoozed" : check.status}
                    </Chip>
                    {check.id === "security:allowed-hosts" && check.status !== "ok" ? (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="h-6 px-2 text-[length:var(--type-caption)]"
                        onClick={(event) => {
                          event.stopPropagation();
                          navigate("/system/security");
                        }}
                      >
                        Fix
                      </Button>
                    ) : check.id === "download-health:dangerous-file" && check.status !== "ok" ? (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="h-6 px-2 text-[length:var(--type-caption)]"
                        onClick={(event) => {
                          event.stopPropagation();
                          navigate("/search-cycles/failed-downloads");
                        }}
                      >
                        Fix
                      </Button>
                    ) : outputDetails ? (
                      <div className="flex gap-1.5">
                        {outputDetails.isMedia ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="h-6 px-2 text-[length:var(--type-caption)]"
                            disabled={busyId === check.id}
                            onClick={(event) => {
                              event.stopPropagation();
                              importProcessorOutputItem(outputDetails);
                            }}
                          >
                            Import
                          </Button>
                        ) : null}
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-[length:var(--type-caption)] text-destructive"
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void deleteProcessorOutputItem(check, outputDetails);
                          }}
                        >
                          Delete
                        </Button>
                      </div>
                    ) : check.status !== "ok" ? (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="h-6 px-2 text-[length:var(--type-caption)]"
                        disabled={busyId === check.id}
                        onClick={(event) => {
                          event.stopPropagation();
                          void (isSnoozed ? unsnooze(check) : snooze(check));
                        }}
                      >
                        {isSnoozed ? "Unsnooze" : "Snooze 24h"}
                      </Button>
                    ) : null}
                  </div>
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
      )}
    </ListCard>
  );
}

function MonitoringCard({ monitoring }: { monitoring: MonitoringDashboardSnapshot }) {
  const { alerts, performance, readiness, services, storage } = monitoring;
  const apiLatency = performance.apiLatency;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Activity className="h-4 w-4 text-primary" />
          Monitoring dashboard
        </CardTitle>
        <CardDescription>
          Health, alert rules, performance trends, and export endpoints for external dashboards.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-[var(--page-gap)]">
        <div className="grid grid-cols-2 gap-2">
          <AutomationMetric label="Health checks" value={`${readiness.totalChecks - readiness.failedChecks}/${readiness.totalChecks}`} compact />
          <AutomationMetric label="Open alerts" value={alerts.length} compact />
          <AutomationMetric label="API p95" value={`${Math.round(apiLatency.p95Ms)}ms`} compact />
          <AutomationMetric label="Queued jobs" value={services.queuedJobs} compact />
        </div>

        <div className="space-y-2 rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)] text-xs text-muted-foreground">
          <p>Storage free: {storage.freePercent === null ? "n/a" : `${storage.freePercent.toFixed(1)}%`}</p>
          <p>Search cycle avg: {formatDuration(performance.averageSearchCycleSeconds, "s")}</p>
          <p>Grab to detect avg: {formatDuration(performance.averageGrabToDetectionSeconds, "s")}</p>
          <p>Detect to import avg: {formatDuration(performance.averageDetectionToImportSeconds, "s")}</p>
          <p>API error rate (15m): {apiLatency.errorRatePercent.toFixed(2)}%</p>
        </div>

        {alerts.length ? (
          <div className="space-y-2">
            {alerts.slice(0, 3).map((alert) => (
              <div key={alert.code} className="rounded-xl border border-warning/35 bg-warning/10 p-3">
                <p className="text-xs font-semibold uppercase tracking-[0.14em] text-warning">{alert.severity}</p>
                <p className="mt-1 text-sm font-semibold text-foreground">{alert.summary}</p>
                <p className="mt-1 text-xs text-muted-foreground">{alert.details}</p>
              </div>
            ))}
          </div>
        ) : (
          <p className="rounded-xl border border-hairline bg-surface-1 p-3 text-sm text-muted-foreground">
            No active monitoring alerts.
          </p>
        )}

        <div className="grid gap-2">
          <a
            className="rounded-lg border border-hairline bg-background/35 px-3 py-2 text-xs font-medium text-primary hover:bg-background/55"
            href="/api/monitoring/export/prometheus"
            target="_blank"
            rel="noreferrer"
          >
            Open Prometheus metrics export
          </a>
          <a
            className="rounded-lg border border-hairline bg-background/35 px-3 py-2 text-xs font-medium text-primary hover:bg-background/55"
            href="/api/monitoring/export/influx"
            target="_blank"
            rel="noreferrer"
          >
            Open Influx line-protocol export
          </a>
        </div>
      </CardContent>
    </Card>
  );
}

function savedPreferencePayload(preferences: UpdatePreferencesResponse) {
  return {
    mode: preferences.mode,
    channel: preferences.channel,
    autoCheck: preferences.autoCheck
  };
}

function updateModeDescription(mode: string) {
  if (mode === "notify-only") {
    return "Deluno checks for updates and notifies you, but does not download automatically.";
  }

  if (mode === "download-apply-on-restart") {
    return "Deluno downloads updates automatically and stages them for the next restart.";
  }

  return "Deluno checks and downloads updates in the background. You restart when ready.";
}

function installKindLabel(installKind: string) {
  if (installKind === "windows-packaged") {
    return "Windows packaged";
  }

  if (installKind === "docker") {
    return "Docker";
  }

  return "Manual";
}

function updateStateLabel(state: string) {
  switch (state) {
    case "idle":
      return "Idle";
    case "checking":
      return "Checking";
    case "update-available":
      return "Update available";
    case "downloading":
      return "Downloading";
    case "ready-to-restart":
      return "Ready to restart";
    case "up-to-date":
      return "Up to date";
    case "not-supported":
      return "Not supported";
    case "error":
      return "Error";
    default:
      return state;
  }
}

function HealthRow({ label, status }: { label: string; status: string }) {
  const isHealthy = status === "healthy" || status === "online";
  const isDegraded = status === "degraded" || status === "warning";
  const isOffline = status === "offline" || status === "unhealthy" || status === "error";

  return (
    <div className="flex items-center justify-between rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
      <p className="text-[length:var(--type-body-sm)] text-foreground">{label}</p>
      <span
        className={[
          "flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[length:var(--type-caption)] font-medium",
          isHealthy
            ? "border-success/20 bg-success/10 text-success"
            : isDegraded
              ? "border-warning/20 bg-warning/10 text-warning"
              : isOffline
                ? "border-destructive/20 bg-destructive/8 text-destructive"
                : "border-hairline text-muted-foreground"
        ].join(" ")}
      >
        {isHealthy ? <Wifi className="h-3 w-3" /> : isOffline ? <WifiOff className="h-3 w-3" /> : null}
        {status}
      </span>
    </div>
  );
}

function JobStatusBadge({ status }: { status: string }) {
  const isRunning = status === JOB_STATUS.RUNNING;
  const isQueued = status === JOB_STATUS.QUEUED;
  const isDone = isJobSuccessful(status as JobStatus) || status === "succeeded";
  const isFailed = isJobFailed(status as JobStatus) || status === "error";

  return (
    <span
      className={[
        "rounded-full border px-2.5 py-0.5 text-[length:var(--type-micro)] font-medium uppercase tracking-wide",
        isRunning
          ? "border-primary/20 bg-primary/10 text-primary"
          : isQueued
            ? "border-warning/20 bg-warning/10 text-warning"
            : isDone
              ? "border-success/20 bg-success/10 text-success"
              : isFailed
                ? "border-destructive/20 bg-destructive/8 text-destructive"
                : "border-hairline text-muted-foreground"
      ].join(" ")}
    >
      {isRunning ? "● " : ""}{status}
    </span>
  );
}

function formatWhen(value: string, preferences: DisplayPreferences) {
  return formatDateTime(value, preferences);
}

/** #869: uptime in whole days/hours/minutes, the way an "up 3d 4h" line reads. */
function formatUptime(totalSeconds: number): string {
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor((totalSeconds % 86_400) / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  const parts = [
    days ? `${days}d` : null,
    hours ? `${hours}h` : null,
    days === 0 ? `${minutes}m` : null
  ].filter((part): part is string => part !== null);
  return parts.length ? parts.join(" ") : "just started";
}

function relativeTime(iso: string): string {
  const minutes = Math.round(Math.abs(Date.now() - new Date(iso).getTime()) / 60000);
  return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`;
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`;
  return `${(value / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

function formatDuration(value: number | null, unit: "s" | "ms") {
  if (value === null || Number.isNaN(value)) {
    return "n/a";
  }

  if (unit === "ms") {
    return `${value.toFixed(1)}ms`;
  }

  return `${value.toFixed(1)}s`;
}
