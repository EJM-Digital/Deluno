import { comparablePath } from "../lib/library-paths";
import { toneSortValue } from "../lib/status-tones";
import { latestVersionLabel, updateHeadline } from "../lib/update-headline";
import { localizeResetTimes } from "../lib/reset-times";
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { defineSources, queryClient, refetchSources, sourceLoader, useRouteSources, type RouteSource, type RouteSourceData, type RouteSourceState } from "../lib/route-cache";
import { sharedSources } from "../lib/shared-sources";
import { RouteSourceGate } from "../components/shell/route-source-gate";
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
import { JOB_STATUS, type JobStatus, getJobStatusLabel, isJobActive, isJobFailed, isJobRetrying, isJobSuccessful } from "../lib/job-status-constants";
import { dismissFailedJob } from "../lib/failed-jobs";
import { isBackgroundJob, splitStoppedJobs } from "../lib/background-jobs";
import { JobDrawer } from "../components/app/job-drawer";
import { SystemShell } from "../components/app/settings-shell";
import { LanAccessNotice, type LanAccessStatus } from "../components/app/lan-access-notice";
import { ComponentRecoveryNotices } from "../components/app/component-recovery-notices";
import { HostNameTip } from "../components/app/host-name-tip";
import { LogFilesCard } from "../components/app/log-files-card";
import { MetadataMaintenanceCard } from "../components/app/metadata-maintenance-card";
import { ProviderHealthDrawer, type ProviderHealthSelection } from "../components/app/provider-health-drawer";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { AuditTimeline, AuditTimelineFilters, useAuditTimeline, type TimelineEvent } from "../components/shell/audit-timeline";
import { SaveStatus, useSaveStatus } from "../components/shell/save-status";
import { WsStatusBadge } from "../components/shell/ws-status-badge";
import { useCoalescedSourceRefresh } from "../lib/live-refresh";
import { RealtimeGroups, useLiveEvent, useSignalREvent } from "../lib/use-signalr";
import { categoryLabel } from "../lib/activity-format";
import {
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
import { toast } from "../components/shell/toaster";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { describeCleanup, type RecycleBinCleanupPreview } from "../lib/recycle-bin-words";
import type { DrawerSaveState } from "../components/ui/drawer";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { PageFooter } from "../components/ui/page-footer";
import { PathInput } from "../components/ui/path-input";
import { FILTER_MENU_TRIGGER } from "../components/ui/filter-chips";
import { MenuSelect } from "../components/ui/menu-select";
import { Select } from "../components/ui/select";
import { SwitchRow } from "../components/ui/switch";
import { Chip, type ChipProps } from "../components/ui/chip";
import { TmdbAttribution } from "../components/app/tmdb-attribution";
import { TvdbAttribution } from "../components/app/tvdb-attribution";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { SummaryStrip } from "../components/ui/summary-strip";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { formatDateTime, type DisplayPreferences, useDisplayPreferences } from "../lib/display-preferences";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";
import { healthChecksNeedingAttention, isHealthCheckSnoozed } from "../lib/health-snooze";
import { formatProgressPercent } from "../lib/progress-format";

/** GET /api/system/info (#869): uptime, runtime, and app-data details for the "This installation" card. */
interface SystemInstallationInfo {
  processStartUtc: string;
  uptimeSeconds: number;
  osDescription: string;
  runtimeVersion: string;
  databaseType: string;
  databaseLocation: string;
  appDataPath: string;
  /** The computer this Deluno runs on (#1101). */
  hostName?: string;
  /** True in Docker when the name is the container's random id. */
  hostLooksLikeContainerId?: boolean;
}

/**
 * System opens on what it last showed and asks again behind it (#1081). The
 * lists and small answers fill in as they arrive; what the page cannot be
 * drawn without (settings, health, monitoring, backups, updates, the recycle
 * bin setting) is waited for, in one wave rather than three.
 */
const emptyActivityDisplaySettings = { pageSize: 500 };

const activityDisplaySettingsSource: RouteSource<{ pageSize: number }> = {
  key: ["src", "activity-display-settings"],
  label: "Timeline size",
  // #871: how many rows the timeline fetches and shows is a user setting now,
  // not a fixed 500-row cap.
  fetch: () => fetchJson<{ pageSize: number }>("/api/activity/display-settings").catch(() => emptyActivityDisplaySettings),
  fallback: emptyActivityDisplaySettings
};

export const systemSources = defineSources({
  settings: sharedSources.settings,
  healthChecks: {
    // #909: every health check's latest result, read by System -> Health, the
    // Dashboard tile, the bell and outbound notifications alike.
    key: ["src", "system-health"], label: "Health checks", fetch: () => fetchJson<HealthCheckListResponse>("/api/system/health")
  },
  monitoring: { key: ["src", "monitoring"], label: "Monitoring", fetch: () => fetchJson<MonitoringDashboardSnapshot>("/api/monitoring/dashboard") },
  backupSettings: { key: ["src", "backup-settings"], label: "Backup settings", fetch: () => fetchJson<BackupSettingsSnapshot>("/api/backups/settings") },
  updateStatus: { key: ["src", "update-status"], label: "Update status", fetch: () => fetchJson<UpdateStatusResponse>("/api/updates/status") },
  recycleBinSettings: { key: ["src", "recycle-bin-settings"], label: "Recycle bin settings", fetch: () => fetchJson<RecycleBinSettings>("/api/recycle-bin/settings") },
  libraries: { ...sharedSources.libraries, fallback: [] as LibraryItem[] },
  // Background jobs (#1114): the latest jobs, of which the ones not about a title are listed; failed ones are read by name so a stopped job is never pushed out of the window.
  jobs: { key: ["src", "jobs-200"], label: "Jobs", fetch: () => fetchPageItems<JobQueueItem>("/api/jobs?pageSize=200&includePayload=true"), fallback: [] as JobQueueItem[] },
  failedJobs: { ...sharedSources.failedJobs, fallback: [] as JobQueueItem[] },
  activityDisplaySettings: activityDisplaySettingsSource,
  activity: {
    key: ["src", "system-activity"],
    label: "Timeline",
    fetch: async () => {
      const { pageSize } = await queryClient.fetchQuery({ queryKey: activityDisplaySettingsSource.key, queryFn: activityDisplaySettingsSource.fetch, staleTime: 3_000 });
      return fetchPageItems<ActivityEventItem>(`/api/activity?pageSize=${pageSize}`);
    },
    fallback: [] as ActivityEventItem[]
  },
  indexers: { ...sharedSources.indexers, fallback: [] as IndexerItem[] },
  downloadClients: { ...sharedSources.downloadClients, fallback: [] as DownloadClientItem[] },
  // A processor holds every file mid-import, and its health used to show on
  // one settings chip and nowhere else (#577).
  processors: { key: ["src", "processor-connections-system"], label: "Processors", fetch: () => fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections"), fallback: [] as ProcessorConnectionItem[] },
  backups: { key: ["src", "backups"], label: "Backups", fetch: () => fetchJson<BackupItem[]>("/api/backups"), fallback: [] as BackupItem[] },
  automation: { key: ["src", "library-automation-50"], label: "Automation state", fetch: () => fetchPageItems<LibraryAutomationStateItem>("/api/library-automation?pageSize=50"), fallback: [] as LibraryAutomationStateItem[] },
  searchCycles: { key: ["src", "search-cycles-12"], label: "Search runs", fetch: () => fetchPageItems<SearchCycleRunItem>("/api/search-cycles?pageSize=12"), fallback: [] as SearchCycleRunItem[] },
  retryWindows: { key: ["src", "retry-windows-12"], label: "Retry windows", fetch: () => fetchPageItems<SearchRetryWindowItem>("/api/search-retry-windows?pageSize=12"), fallback: [] as SearchRetryWindowItem[] },
  systemTasks: { key: ["src", "system-tasks"], label: "Background tasks", fetch: () => fetchJson<SystemTaskItem[]>("/api/system/tasks"), fallback: [] as SystemTaskItem[] },
  recycleBin: { key: ["src", "recycle-bin"], label: "Recycle bin", fetch: () => fetchJson<RecycleBinItem[]>("/api/recycle-bin"), fallback: [] as RecycleBinItem[] },
  // "Listening on" below used to echo the stored host.bindAddress/host.port
  // as if they were live; Kestrel actually binds from Server:Port /
  // Server:AllowLan configuration, so this reads the same effective values
  // LanAccessNotice does (#736).
  lanAccess: { key: ["src", "lan-access"], label: "Network access", fetch: () => fetchJson<LanAccessStatus>("/api/system/lan-access").catch(() => null), fallback: null as LanAccessStatus | null },
  // #869: uptime, process start, OS/runtime version, and app-data path,
  // read live rather than hardcoded at build time.
  systemInfo: { key: ["src", "system-info"], label: "This installation", fetch: () => fetchJson<SystemInstallationInfo>("/api/system/info").catch(() => null), fallback: null as SystemInstallationInfo | null }
});

export const systemLoader = sourceLoader(systemSources);

type SystemLoaderData = RouteSourceData<typeof systemSources>;

export function SystemPage() {
  const state = useRouteSources(systemSources);
  return (
    <RouteSourceGate state={state}>
      {() => <SystemPageBody data={state.data} revalidator={state.revalidator} />}
    </RouteSourceGate>
  );
}

function SystemPageBody({ data: loaderData, revalidator }: { data: SystemLoaderData; revalidator: RouteSourceState<typeof systemSources>["revalidator"] }) {
  const location = useLocation();
  const { activity, activityDisplaySettings, automation, backupSettings, backups, downloadClients, failedJobs, healthChecks, indexers, jobs, lanAccess, libraries, monitoring, processors, recycleBin, recycleBinSettings, retryWindows, searchCycles, settings, systemInfo, systemTasks, updateStatus } = loaderData;
  // The folders Deluno writes to are its libraries' folders. This used to count
  // the two paths the setup guide asks for, which a library made in Settings
  // never fills in: two libraries on a NAS read "0".
  const mediaRoots = new Set(libraries.map((library) => comparablePath(library.rootPath)).filter(Boolean)).size;
  const { preferences } = useDisplayPreferences();

  const activeJobs = jobs.filter((job) => isBackgroundJob(job) && isJobActive(job.status as JobStatus)).length;
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
      { id: event.id, message: event.message, category: event.category, label: event.label, severity: event.severity, createdUtc: event.createdUtc },
      ...prev.slice(0, 49)
    ]);
  });

  // Every list on this screen follows the server (#1179): an event names the lists it moves and
  // only those are read again. No timer asks, and a reconnect reads the screen once.
  const refresh = useCoalescedSourceRefresh();
  const { healthChecks: healthSource, indexers: indexersSource, downloadClients: clientsSource, processors: processorsSource } = systemSources;
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, () => refresh(healthSource, indexersSource, clientsSource, processorsSource));
  useSignalREvent("MonitoringChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.monitoring));
  useSignalREvent("BackupChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.backups, systemSources.backupSettings));
  useSignalREvent("UpdateStatusChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.updateStatus));
  useSignalREvent("RecycleBinChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.recycleBin, systemSources.recycleBinSettings));
  useSignalREvent("SystemTaskChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.systemTasks));
  useSignalREvent("SettingsChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.settings, systemSources.lanAccess));
  useSignalREvent("LibraryChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.libraries));
  useSignalREvent("IndexerChanged", RealtimeGroups.Dashboard, () => refresh(indexersSource));
  useSignalREvent("DownloadClientChanged", RealtimeGroups.Dashboard, () => refresh(clientsSource));
  useSignalREvent("AutomationStateChanged", RealtimeGroups.Dashboard, () => refresh(systemSources.automation, systemSources.retryWindows));
  useSignalREvent("SearchRunCompleted", RealtimeGroups.Dashboard, () => refresh(systemSources.searchCycles, systemSources.retryWindows));
  useLiveEvent("JobChanged", () => refresh(systemSources.jobs, systemSources.failedJobs));
  useLiveEvent("SearchCycleProgress", () => refresh(systemSources.searchCycles, systemSources.automation));
  useLiveEvent("ClientStateChanged", () => refresh(clientsSource));

  // The timeline has its own tab; repeating all 500 events here made Health
  // three times taller than everything on it that actually answers "is it well?".
  const timeline = useAuditTimeline(activity, liveEvents, activityPageSize);
  const auditCard = (
    <>
      <ListCard
        title="Activity timeline"
        count={timeline.countLabel}
        filter={{ value: timeline.query, onChange: timeline.setQuery, placeholder: "Find an event" }}
        actions={
          <div className="flex min-w-0 flex-wrap items-center gap-2 lg:flex-nowrap">
            <AuditTimelineFilters timeline={timeline} />
            <MenuSelect
              label="Rows shown"
              value={String(activityPageSize)}
              onChange={(next) => { if (activityBusy === null) void changeActivityPageSize(Number(next)); }}
              align="end"
              options={[
                { value: "50", label: "50 rows" },
                { value: "100", label: "100 rows" },
                { value: "250", label: "250 rows" },
                { value: "500", label: "500 rows" },
                { value: "1000", label: "1000 rows" }
              ]}
              triggerClassName={FILTER_MENU_TRIGGER}
            />
            <Button type="button" variant="outline" size="sm" disabled={activityBusy !== null} onClick={() => setConfirmClearActivity(true)}>
              Clear
            </Button>
            <WsStatusBadge />
          </div>
        }
      >
        <div className="space-y-2 p-(--card-pad-x)">
          <SaveStatus state={activityStatus.state} message={activityStatus.message} />
          <AuditTimeline timeline={timeline} />
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
        <ListTable
          listId="system-providers"
          items={[
            ...indexers.map((provider) => ({ kind: "indexer" as const, provider })),
            ...downloadClients.map((provider) => ({ kind: "client" as const, provider })),
            ...processors.map((provider) => ({ kind: "processor" as const, provider }))
          ]}
          columns={[
            { id: "provider", label: "Provider", sort: (entry) => entry.provider.name.toLowerCase() },
            { id: "kind", label: "Kind", width: "minmax(0,1fr)", sort: (entry) => (entry.kind === "processor" ? entry.provider.provider : entry.provider.protocol).toLowerCase() },
            { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (entry) => (entry.kind === "processor" ? (entry.provider.isEnabled ? entry.provider.healthStatus : "off") : entry.provider.healthStatus) }
          ]}
        >
          {(rows) => rows.map((entry) =>
            entry.kind === "indexer" ? (
              <ListRow key={`indexer:${entry.provider.id}`} onClick={() => setSelectedProvider({ kind: "indexer", provider: entry.provider })}>
                <ListNameCell name={entry.provider.name} sub="Search source" />
                <ListCell primary={entry.provider.protocol} />
                <ListCell mobile>
                  <Chip tone={healthTone(entry.provider.healthStatus)}>{entry.provider.healthStatus}</Chip>
                </ListCell>
              </ListRow>
            ) : entry.kind === "client" ? (
              <ListRow key={`client:${entry.provider.id}`} onClick={() => setSelectedProvider({ kind: "download-client", provider: entry.provider })}>
                <ListNameCell name={entry.provider.name} sub="Download client" />
                <ListCell primary={entry.provider.protocol} />
                <ListCell mobile>
                  <Chip tone={healthTone(entry.provider.healthStatus)}>{entry.provider.healthStatus}</Chip>
                </ListCell>
              </ListRow>
            ) : (
              <ListRow key={`processor:${entry.provider.id}`}>
                <ListNameCell name={entry.provider.name} sub="Processor" />
                <ListCell primary={entry.provider.provider} />
                <ListCell mobile>
                  <Chip tone={entry.provider.isEnabled ? healthTone(entry.provider.healthStatus) : "idle"}>
                    {entry.provider.isEnabled ? entry.provider.healthStatus : "off"}
                  </Chip>
                </ListCell>
              </ListRow>
            )
          )}
        </ListTable>
      )}
      <ProviderHealthDrawer selection={selectedProvider} onClose={() => setSelectedProvider(null)} />
    </ListCard>
  );

  const healthCheckAttention = healthChecksNeedingAttention(healthChecks.checks).length;
  const healthChecksCard = <HealthChecksCard checks={healthChecks.checks} onChanged={() => revalidator.revalidate()} />;

  const jobsCard = <BackgroundJobsCard jobs={jobs} failedJobs={failedJobs} onChanged={() => revalidator.revalidate()} />;

  const runtimeCard = (
    <>
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
          { label: "Runs on", value: systemInfo?.hostName ?? "Unknown" },
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
      <div role="group" aria-label="Credits" className="flex flex-col gap-1.5 border-t border-hairline px-(--card-pad-x) py-(--card-pad-y)">
        <p className="text-(length:--type-micro) font-bold uppercase tracking-[0.18em] text-muted-foreground">Credits</p>
        <TmdbAttribution full />
        <TvdbAttribution />
      </div>
    </ListCard>
    <HostNameTip looksLikeContainerId={systemInfo?.hostLooksLikeContainerId} />
    </>
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
        <RecycleBinCard initialItems={recycleBin} initialSettings={recycleBinSettings} />
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
          { label: "Running jobs", value: String(activeJobs), help: activeJobs ? "queued or working" : "nothing queued" },
          { label: "Search sources", value: `${healthyIndexers}/${indexers.length}`, help: healthyIndexers === indexers.length ? "all responding" : "one is not responding", tone: indexers.length && healthyIndexers < indexers.length ? "warning" : undefined },
          { label: "Download clients", value: `${healthyClients}/${downloadClients.length}`, help: healthyClients === downloadClients.length ? "all responding" : "one is not responding", tone: downloadClients.length && healthyClients < downloadClients.length ? "warning" : undefined },
          ...(enabledProcessors.length
            ? [{ label: "Processors", value: `${healthyProcessors}/${enabledProcessors.length}`, help: healthyProcessors === enabledProcessors.length ? "all answering" : "one is not answering", tone: healthyProcessors < enabledProcessors.length ? "warning" as const : undefined }]
            : []),
          { label: "Monitoring alerts", value: String(monitoring.alerts.length), help: monitoring.alerts.length ? "raised by monitoring" : "nothing raised", tone: monitoring.alerts.length ? "danger" : undefined },
          // The same count the Health card's own title gives (#1138): every check that is not fine, unanswered ones included.
          { label: "Health checks", value: String(healthCheckAttention), help: healthCheckAttention ? "need attention" : "all checks pass", tone: healthChecks.summary.unsnoozedProblemCount ? "danger" : undefined },
          { label: "Media roots", value: String(mediaRoots), help: "folders Deluno writes to" }
        ]}
      />

      <LanAccessNotice />
      <ComponentRecoveryNotices />
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

/**
 * Background jobs (#1114): the jobs that are not about a title -- a library-wide metadata refresh, a backup,
 * a scheduled pass. The stopped ones that need a person (with Retry and Dismiss) come first, then queued and
 * running ones, then the recent ones. A job about a title is in Activity.
 */
export function BackgroundJobsCard({ jobs, failedJobs, onChanged }: { jobs: JobQueueItem[]; failedJobs: JobQueueItem[]; onChanged: () => void }) {
  const { preferences } = useDisplayPreferences();
  const [openJobId, setOpenJobId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const stopped = useMemo(() => splitStoppedJobs(failedJobs).background, [failedJobs]);
  const stoppedIds = useMemo(() => new Set(stopped.map((job) => job.id)), [stopped]);
  const background = useMemo(() => jobs.filter(isBackgroundJob), [jobs]);
  const working = background.filter((job) => !stoppedIds.has(job.id) && (isJobActive(job.status as JobStatus) || isJobRetrying(job)));
  const recent = background.filter((job) => !stoppedIds.has(job.id) && !working.includes(job) && !isJobFailed(job.status as JobStatus)).slice(0, 12);
  const listed = [...stopped, ...working, ...recent];
  const openJob = listed.find((job) => job.id === openJobId) ?? null;

  async function retryFailed() {
    setBusy(true);
    try {
      const response = await authedFetch("/api/jobs/retry-failed", { method: "POST" });
      if (!response.ok) throw new Error("Failed jobs could not be requeued.");
      const result = (await response.json()) as { retried?: number };
      toast.success(`${result.retried ?? 0} failed job${result.retried === 1 ? "" : "s"} requeued`);
      onChanged();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Retry failed.");
    } finally {
      setBusy(false);
    }
  }

  async function dismiss(job: JobQueueItem) {
    setBusy(true);
    try {
      await dismissFailedJob(job.id);
      toast.success(`${job.label} dismissed`);
      setOpenJobId(null);
      onChanged();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That job could not be dismissed.");
    } finally {
      setBusy(false);
    }
  }

  const count = stopped.length ? `${stopped.length} stopped` : listed.length ? `${working.length} working, ${recent.length} recent` : undefined;
  return (
    <ListCard
      title="Background jobs"
      count={count}
      actions={
        stopped.length ? (
          <Button type="button" size="sm" onClick={() => void retryFailed()} disabled={busy}>
            Retry failed jobs
          </Button>
        ) : undefined
      }
    >
      {listed.length === 0 ? (
        <ListEmpty title="No background jobs yet" description="Library-wide refreshes, backups and scheduled passes show here as Deluno runs them. Jobs about one title are in Activity." />
      ) : (
        <ListTable
          listId="system-background-jobs"
          items={listed}
          columns={[
            { id: "job", label: "Job", sort: (job) => job.label.toLowerCase() },
            { id: "startedBy", label: "Started by", width: "minmax(0,1fr)", sort: (job) => job.sourceLabel.toLowerCase() },
            { id: "when", label: "When", width: "150px", sort: (job) => new Date(job.completedUtc ?? job.startedUtc ?? job.createdUtc) },
            { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (job) => { const retrying = isJobRetrying(job); return toneSortValue(retrying ? "info" : jobTone(job.status), retrying ? "Retrying" : getJobStatusLabel(job.status as JobStatus)); } }
          ]}
        >
          {(rows) => rows.map((job) => (
            <ListRow key={job.id} onClick={() => setOpenJobId(job.id)} selected={openJobId === job.id}>
              <ListNameCell name={job.label} sub={job.attempts > 1 ? `Tried ${job.attempts} times` : "Tried once"} />
              <ListCell primary={job.sourceLabel} secondary={job.lastError ?? undefined} />
              <ListCell numeric primary={formatWhen(job.completedUtc ?? job.startedUtc ?? job.createdUtc, preferences)} />
              <ListCell mobile>
                <Chip tone={isJobRetrying(job) ? "info" : jobTone(job.status)}>{isJobRetrying(job) ? "Retrying" : getJobStatusLabel(job.status as JobStatus)}</Chip>
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      )}
      <JobDrawer job={openJob} busy={busy} onClose={() => setOpenJobId(null)} onDismiss={(job) => void dismiss(job)} />
    </ListCard>
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
  if (isJobFailed(status as JobStatus)) return "bad";
  if (status === JOB_STATUS.COMPLETED) return "ok";
  if (isJobActive(status as JobStatus)) return "info";
  return "idle";
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
  const revalidator = { revalidate: () => { void refetchSources(systemSources); } };
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
      <ListTable
        listId="system-tasks"
        items={tasks}
        columns={[
          { id: "task", label: "Task", sort: (task) => task.name.toLowerCase() },
          { id: "cadence", label: "Cadence", width: "140px", sort: (task) => task.intervalSeconds },
          { id: "last", label: "Last execution", width: "180px", sort: (task) => (task.lastCompletedUtc ? new Date(task.lastCompletedUtc) : null) },
          { id: "next", label: "Next execution", width: "180px", sort: (task) => (task.nextRunUtc ? new Date(task.nextRunUtc) : null) },
          { id: "result", label: "Result", width: LIST_TRACK.status, mobile: true, sort: (task) => toneSortValue(jobTone(task.lastResult), task.lastResult) },
          { id: "actions", label: "", width: LIST_TRACK.action, align: "end", mobile: true }
        ]}
        chevron={false}
      >
        {(rows) => rows.map((task) => (
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
  initialSettings
}: {
  initialItems: RecycleBinItem[];
  initialSettings: RecycleBinSettings;
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
  // Files going in or out of the bin, and the bin's own settings, arrive as a newer answer; typed-in edits are kept (#1179).
  const dirtyNow = useRef(dirty);
  dirtyNow.current = dirty;
  useEffect(() => { setItems(initialItems); }, [initialItems]);
  useEffect(() => {
    if (dirtyNow.current) return;
    setSavedSettings(initialSettings);
    setSettings(initialSettings);
  }, [initialSettings]);

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
            {items.length ? `${items.length} stored item${items.length === 1 ? "" : "s"}` : "Nothing stored"}
          </CardTitle>
          <CardDescription>Library removals and replaced files stay recoverable until their retention window expires.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-(--page-gap)">
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
                      {item.titleSnapshotId ? (
                        <p className="mt-1 text-xs text-muted-foreground">Restore puts the title back in Deluno with these files, its monitoring and its history.</p>
                      ) : null}
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
      <CardContent className="space-y-(--page-gap)">
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
            <p className="text-(length:--type-caption) font-bold uppercase tracking-[0.16em] text-muted-foreground">Searches held back until</p>
            {retryWindows.slice(0, 4).map((item) => (
              <div key={`${item.entityType}:${item.entityId}:${item.actionKind}`} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">{item.mediaType} / {item.actionKind}</p>
                  <p className="text-(length:--type-caption) text-muted-foreground">{item.lastResult || "Last search recorded"}</p>
                </div>
                <span className="text-(length:--type-caption) tabular-nums text-muted-foreground">{formatWhen(item.nextEligibleUtc, preferences)}</span>
              </div>
            ))}
          </div>
        ) : null}

        {automation.length ? (
          <div className="space-y-2">
            <p className="text-(length:--type-caption) font-bold uppercase tracking-[0.16em] text-muted-foreground">Libraries</p>
            {automation.slice(0, 5).map((item) => (
              <div key={item.libraryId} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">{item.libraryName}</p>
                  <p className="text-(length:--type-caption) text-muted-foreground">
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
      <p className="text-(length:--type-micro) font-bold uppercase tracking-[0.14em] text-muted-foreground">{label}</p>
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
  // A backup made, deleted or rescheduled anywhere (a scheduled one included) arrives as a newer list;
  // typed-in schedule edits are never overwritten by it (#1179).
  const dirtyNow = useRef(dirty);
  dirtyNow.current = dirty;
  useEffect(() => { setBackups(initialBackups); }, [initialBackups]);
  useEffect(() => {
    if (dirtyNow.current) return;
    setSavedSettings(initialSettings);
    setSettings(initialSettings);
  }, [initialSettings]);
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
          {backups.length ? `${backups.length} backup${backups.length === 1 ? "" : "s"}` : "No backups yet"}
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
          {savedSettings.problem ? (
            <div role="alert" className="mt-2 rounded-lg border border-warning/40 bg-warning/10 p-3 text-sm text-foreground">
              <p>{savedSettings.problem}</p>
              <p className="mt-1 font-medium">Save the backup settings again to repair them.</p>
              <Button type="button" className="mt-2" onClick={() => void saveSchedule()} disabled={busy === "schedule"}>
                Repair backup settings
              </Button>
            </div>
          ) : null}
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
  // A newer answer (a version applied, a check run elsewhere) replaces the one shown (#1179).
  useEffect(() => { setCurrent(status); }, [status]);
  const [savedPreferences, setSavedPreferences] = useState<UpdatePreferencesResponse | null>(null);
  const [preferences, setPreferences] = useState<UpdatePreferencesResponse | null>(null);
  const [busyAction, setBusyAction] = useState<"check" | "download" | "restart" | "prefs" | null>(null);
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

  // The update behaviour saved from another tab announces itself as a changed update status, and the status
  // itself may be exactly the same. The form reads it again then, unless it holds edits that are not saved
  // yet, which are never overwritten (#1179).
  const preferencesDirtyNow = useRef(preferencesDirty);
  preferencesDirtyNow.current = preferencesDirty;
  useSignalREvent("UpdateStatusChanged", RealtimeGroups.Dashboard, () => {
    if (!preferencesDirtyNow.current) void loadPreferences();
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

  async function runAction(action: "check" | "download" | "restart") {
    setBusyAction(action);
    actionStatus.markSyncing();
    try {
      const path =
        action === "check"
          ? "/api/updates/check"
          : action === "download"
            ? "/api/updates/download"
            : "/api/updates/restart-now";
      const result = await fetchJson<UpdateActionResponse>(path, { method: "POST" });
      setCurrent(result.status);
      // The server message can carry GitHub's reset as a UTC stamp; SaveStatus shows it in the viewer's own time (#1181).
      if (result.status.state === "error") actionStatus.markError(result.message);
      else actionStatus.markSaved(result.message);
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
          {updateHeadline(current)}
        </CardTitle>
        <CardDescription>Version status, channel, behaviour mode, and restart flow.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <SaveStatus state={actionStatus.state} message={actionStatus.message} />
        <HealthRow label="Current version" status={current.currentVersion} />
        <HealthRow label="Latest version" status={latestVersionLabel(current)} />
        <HealthRow label="Channel" status={current.channel} />
        <HealthRow label="Install kind" status={installKindLabel(current.installKind)} />
        {isDocker && current.currentImageRef ? <HealthRow label="Image" status={current.currentImageRef} /> : null}
        {isDocker && current.currentImageDigest ? <HealthRow label="Image digest" status={current.currentImageDigest} /> : null}
        <HealthRow label="State" status={updateStateLabel(current.state)} />
        <p className="text-sm leading-relaxed text-muted-foreground">{localizeResetTimes(current.message)}</p>
        {current.progressPercent !== null ? (
          <p className="text-xs text-muted-foreground">Download progress: {formatProgressPercent(current.progressPercent)}</p>
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
  /** The film the library already holds, when this is a second copy of it (#1138). */
  spareCopyOf?: string;
}

function processorOutputDetails(check: HealthCheckResultItem): ProcessorOutputItemDetails | null {
  if (!check.id.startsWith("processor-output:") || !check.detailsJson) return null;
  try {
    return JSON.parse(check.detailsJson) as ProcessorOutputItemDetails;
  } catch {
    return null;
  }
}

/** A one-time note (for example after an upgrade): cleared by hand, since no check ever re-creates it. */
export function isHealthNotice(check: Pick<HealthCheckResultItem, "id">): boolean {
  return check.id.startsWith("notice:");
}

/**
 * The import issue a "missing from / unmatched in a processor's output" row mirrors (#1228), or null for any other row.
 * The row is the same problem as that issue (and its hand-off): it is dismissed, not only snoozed, and the dismissal
 * is the issue's own, so Activity, Transfers and the bell stop counting it too.
 */
export function outputIssueOf(check: Pick<HealthCheckResultItem, "id" | "detailsJson">): { caseId: string; path: string } | null {
  if (!check.id.startsWith("processor-output-issue:") || !check.detailsJson) return null;
  try {
    const details = JSON.parse(check.detailsJson) as { recoveryCaseId?: unknown; recoveryMediaType?: unknown };
    if (typeof details.recoveryCaseId !== "string" || !details.recoveryCaseId) return null;
    const collection = details.recoveryMediaType === "series" ? "series" : "movies";
    return { caseId: details.recoveryCaseId, path: `/api/${collection}/import-recovery/${encodeURIComponent(details.recoveryCaseId)}/dismiss` };
  } catch {
    return null;
  }
}

/** A start-up step that did not finish (#1080): its problem key, or null for any other row. These can be run again or cleared, not only snoozed. */
export function startupProblemKey(check: Pick<HealthCheckResultItem, "id">): string | null {
  const prefix = "system:background-problem:";
  if (!check.id.startsWith(prefix)) return null;
  const key = check.id.slice(prefix.length);
  return key.startsWith("startup:") ? key : null;
}

/**
 * How many missing files an "unreachable" note counts (#1070). The note holds
 * only the library, a count and when; a note raised by an older build listed
 * every file instead, so that list's length still counts.
 */
function unreachableMissingCount(check: Pick<HealthCheckResultItem, "id" | "detailsJson">): number {
  if (!check.id.startsWith("notice:library-unreachable:") || !check.detailsJson) return 0;
  try {
    const details = JSON.parse(check.detailsJson) as { missing?: unknown; issueIds?: unknown };
    if (typeof details.missing === "number") return details.missing;
    return Array.isArray(details.issueIds) ? details.issueIds.length : 0;
  } catch {
    return 0;
  }
}

/** The library an "unreachable" note is about, when it counts files it found missing (#1070). */
export function unreachableLibraryId(check: Pick<HealthCheckResultItem, "id" | "detailsJson">): string | null {
  if (unreachableMissingCount(check) === 0) return null;
  try {
    const details = JSON.parse(check.detailsJson as string) as { libraryId?: unknown };
    return typeof details.libraryId === "string" ? details.libraryId : null;
  } catch {
    return null;
  }
}

/** Where a tag warning (#1043) points: the connection's page, or the titles it strands. */
function tagCheckLink(check: HealthCheckResultItem): string | null {
  if (!check.id.startsWith("tags:") || !check.detailsJson) return null;
  try {
    const link = (JSON.parse(check.detailsJson) as { link?: unknown }).link;
    return typeof link === "string" && link.startsWith("/") ? link : null;
  } catch {
    return null;
  }
}

/** Where a suggestion's button goes: the screen that turns the setting on (#1060). */
export const SUGGESTION_ACTIONS: Record<string, { label: string; to: string }> = {
  "security:allowed-hosts": { label: "Open Security", to: "/system/security" },
  "download-health:dangerous-file": { label: "Turn it on", to: "/search-cycles/failed-downloads" }
};

function HealthChecksCard({ checks, onChanged }: { checks: HealthCheckResultItem[]; onChanged: () => void }) {
  const navigate = useNavigate();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [confirmGone, setConfirmGone] = useState<HealthCheckResultItem | null>(null);
  const [runningChecks, setRunningChecks] = useState(false);
  // A suggestion (#1060) is a setting that starts off and is worth turning on.
  // It is not a problem: it never counts toward "need attention".
  const suggestions = checks.filter((check) => check.status === "suggestion");
  const problems = healthChecksNeedingAttention(checks);
  const sorted = checks.filter((check) => check.status !== "suggestion").sort((a, b) => {
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

  async function snooze(check: HealthCheckResultItem, hours = 24) {
    setBusyId(check.id);
    try {
      await authedFetch(`/api/system/health/${encodeURIComponent(check.id)}/snooze`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ hours })
      });
      onChanged();
    } catch {
      // The list simply keeps showing the problem; nothing silently succeeded.
    } finally {
      setBusyId(null);
    }
  }

  async function clearNotice(check: HealthCheckResultItem) {
    setBusyId(check.id);
    try {
      const response = await authedFetch(`/api/system/health/notices/${encodeURIComponent(check.id)}`, { method: "DELETE" });
      if (response.ok) {
        onChanged();
      }
    } catch {
      // The note simply stays; nothing silently succeeded.
    } finally {
      setBusyId(null);
    }
  }

  async function dismissOutputIssue(check: HealthCheckResultItem) {
    const issue = outputIssueOf(check);
    if (!issue) return;
    setBusyId(check.id);
    try {
      const response = await authedFetch(issue.path, { method: "POST" });
      // 404: the issue was already closed somewhere else; the row goes on the push all the same.
      if (response.ok || response.status === 404) {
        toast.success("Dismissed. Nothing was deleted.");
        onChanged();
      } else {
        toast.error("That could not be dismissed.");
      }
    } catch {
      toast.error("That could not be dismissed.");
    } finally {
      setBusyId(null);
    }
  }

  async function retryStartupProblem(check: HealthCheckResultItem) {
    const key = startupProblemKey(check);
    if (!key) return;
    setBusyId(check.id);
    try {
      const response = await authedFetch(`/api/system/health/background-problems/${encodeURIComponent(key)}/retry`, { method: "POST" });
      if (response.ok) {
        const result = (await response.json()) as { cleared: boolean; message: string };
        if (result.cleared) toast.success(result.message);
        else toast.error(result.message);
        onChanged();
      } else {
        toast.error("Deluno could not try that again. Check its logs.");
      }
    } catch {
      // The row simply stays; nothing silently succeeded.
    } finally {
      setBusyId(null);
    }
  }

  async function clearStartupProblem(check: HealthCheckResultItem) {
    const key = startupProblemKey(check);
    if (!key) return;
    setBusyId(check.id);
    try {
      const response = await authedFetch(`/api/system/health/background-problems/${encodeURIComponent(key)}`, { method: "DELETE" });
      if (response.ok) {
        onChanged();
      }
    } catch {
      // The row simply stays; nothing silently succeeded.
    } finally {
      setBusyId(null);
    }
  }

  async function filesReallyGone(check: HealthCheckResultItem) {
    const libraryId = unreachableLibraryId(check);
    if (!libraryId) return;
    setBusyId(check.id);
    try {
      // Deluno recounts the missing files and re-checks the drive before it changes anything.
      const response = await authedFetch(`/api/filesystem/libraries/${encodeURIComponent(libraryId)}/files-gone`, { method: "POST" });
      const result = (await response.json().catch(() => null)) as
        | { message?: string; libraryUnreachable?: boolean; markedGone?: number; inBackground?: boolean }
        | null;
      if (!response.ok) {
        toast.error(result?.message ?? "Deluno could not mark those files as gone. Nothing was changed.");
      } else if (result?.libraryUnreachable || (result?.markedGone === 0 && !result?.inBackground)) {
        // A refusal, not a success: still unreachable, or nothing left to act on.
        toast.error(result?.message ?? "Deluno changed nothing.");
      } else {
        toast.success(result?.message ?? "Those files are marked as gone.");
      }
      onChanged();
    } catch {
      toast.error("Deluno could not mark those files as gone. Nothing was changed.");
    } finally {
      setBusyId(null);
    }
  }

  // "Check now" for the health checks (#1217): the server runs every check again within seconds and pushes the
  // result, so the rows move on their own; nothing here reloads the screen.
  async function runChecksNow() {
    setRunningChecks(true);
    try {
      const response = await authedFetch("/api/system/health/run", { method: "POST" });
      if (response.ok) toast.success("Checking everything again. Rows that now pass will clear in a moment.");
      else toast.error("Deluno could not start the checks. Try again.");
    } catch {
      toast.error("Deluno could not start the checks. Try again.");
    } finally {
      setRunningChecks(false);
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
    <>
    <ListCard
      title={problems.length === 0 ? (sorted.length === 0 && suggestions.length === 0 ? "Everything checked out" : "All clear") : `${problems.length} need${problems.length === 1 ? "s" : ""} attention`}
      titleCase={false}
      actions={
        <Button type="button" variant="outline" size="sm" aria-label="Run all the health checks now" disabled={runningChecks} onClick={() => void runChecksNow()}>
          {runningChecks ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
          Check now
        </Button>
      }
    >
      {sorted.length === 0 && suggestions.length > 0 ? (
        <ListEmpty title="All clear" description="Nothing needs a look. The suggestions below are optional." />
      ) : sorted.length === 0 ? (
        <ListEmpty title="No checks have run yet" description="Deluno checks download clients, indexers, libraries and connections every fifteen minutes." />
      ) : (
        <ListTable
          listId="system-health-checks"
          items={sorted}
          columns={[
            { id: "check", label: "Check", sort: (check) => check.name.toLowerCase() },
            { id: "result", label: "Result", width: "minmax(0,1.6fr)", sort: (check) => check.message.toLowerCase() },
            { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (check) => ({ problem: 0, warning: 1, ok: 3 } as Record<string, number>)[check.status] ?? 2 }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((check) => {
            const isSnoozed = isHealthCheckSnoozed(check);
            // A safety-net row for one unexpected item in a processor output
            // folder (#921) is acted on directly -- Import or Delete -- not
            // snoozed, because snoozing only hides a problem that stays true;
            // this one is meant to be dealt with and then gone.
            const outputDetails = processorOutputDetails(check);
            const tagLink = tagCheckLink(check);
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
                    {outputDetails?.spareCopyOf ? (
                      <div className="flex gap-1.5">
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-(length:--type-caption)"
                          aria-label={`Keep the spare copy of ${outputDetails.spareCopyOf}`}
                          title="Leaves the spare copy where it is and stops listing it here for 30 days."
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void snooze(check, 24 * 30);
                          }}
                        >
                          Keep
                        </Button>
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-(length:--type-caption) text-destructive"
                          aria-label={`Remove the spare copy of ${outputDetails.spareCopyOf}`}
                          title="Deletes the spare copy. The film in your library is not touched."
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void deleteProcessorOutputItem(check, outputDetails);
                          }}
                        >
                          Remove
                        </Button>
                      </div>
                    ) : outputDetails ? (
                      <div className="flex gap-1.5">
                        {outputDetails.isMedia ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="h-6 px-2 text-(length:--type-caption)"
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
                          className="h-6 px-2 text-(length:--type-caption) text-destructive"
                          aria-label={`Clear ${outputDetails.path.split(/[\\/]/).filter(Boolean).pop() ?? "this item"}`}
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void deleteProcessorOutputItem(check, outputDetails);
                          }}
                        >
                          Clear
                        </Button>
                      </div>
                    ) : isHealthNotice(check) ? (
                      <div className="flex gap-1.5">
                        {unreachableLibraryId(check) ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="h-6 px-2 text-(length:--type-caption) text-destructive"
                            aria-label={`These files really are gone: ${check.name}`}
                            disabled={busyId === check.id}
                            onClick={(event) => {
                              event.stopPropagation();
                              setConfirmGone(check);
                            }}
                          >
                            These files really are gone
                          </Button>
                        ) : null}
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-(length:--type-caption)"
                          aria-label={`Clear: ${check.name}`}
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void clearNotice(check);
                          }}
                        >
                          Clear
                        </Button>
                      </div>
                    ) : startupProblemKey(check) ? (
                      <div className="flex gap-1.5">
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-(length:--type-caption)"
                          aria-label={`Try again: ${check.name}`}
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void retryStartupProblem(check);
                          }}
                        >
                          Try again
                        </Button>
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-(length:--type-caption)"
                          aria-label={`Clear: ${check.name}`}
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void clearStartupProblem(check);
                          }}
                        >
                          Clear
                        </Button>
                      </div>
                    ) : check.status !== "ok" ? (
                      <div className="flex gap-1.5">
                        {outputIssueOf(check) ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="h-6 px-2 text-(length:--type-caption)"
                            aria-label={`Dismiss: ${check.name}`}
                            title="Closes this problem everywhere it is counted. Nothing is deleted."
                            disabled={busyId === check.id}
                            onClick={(event) => {
                              event.stopPropagation();
                              void dismissOutputIssue(check);
                            }}
                          >
                            Dismiss
                          </Button>
                        ) : null}
                        {tagLink ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="h-6 px-2 text-(length:--type-caption)"
                            aria-label={`Show: ${check.name}`}
                            onClick={(event) => {
                              event.stopPropagation();
                              navigate(tagLink);
                            }}
                          >
                            Show
                          </Button>
                        ) : null}
                        <Button
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-(length:--type-caption)"
                          disabled={busyId === check.id}
                          onClick={(event) => {
                            event.stopPropagation();
                            void (isSnoozed ? unsnooze(check) : snooze(check));
                          }}
                        >
                          {isSnoozed ? "Unsnooze" : "Snooze 24h"}
                        </Button>
                      </div>
                    ) : null}
                  </div>
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
      )}
    </ListCard>
    {suggestions.length > 0 ? (
      <ListCard
        title="Suggestions"
        count={`${suggestions.length} optional`}
      >
        <ListTable
          listId="system-suggestions"
          items={suggestions}
          columns={[
            { id: "setting", label: "Setting", sort: (check) => check.name.toLowerCase() },
            { id: "why", label: "Why", width: "minmax(0,1.6fr)", sort: (check) => check.message.toLowerCase() },
            { id: "actions", label: "", width: LIST_TRACK.action, align: "end", mobile: true }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((check) => {
            const action = SUGGESTION_ACTIONS[check.id];
            return (
              <ListRow key={check.id}>
                <ListNameCell name={check.name} sub="Switched off" />
                <ListCell
                  primary={check.message}
                  secondary={[check.cause, check.fix].filter(Boolean).join(" ") || undefined}
                />
                <ListCell mobile>
                  {action ? (
                    <Button
                      variant="ghost"
                      size="sm"
                      className="h-6 px-2 text-(length:--type-caption)"
                      aria-label={`${action.label}: ${check.name}`}
                      onClick={(event) => {
                        event.stopPropagation();
                        navigate(action.to);
                      }}
                    >
                      {action.label}
                    </Button>
                  ) : null}
                </ListCell>
              </ListRow>
            );
          })}
        </ListTable>
      </ListCard>
    ) : null}
    <ConfirmDialog
      open={confirmGone !== null}
      onOpenChange={(open) => { if (!open) setConfirmGone(null); }}
      title="Mark these files as gone?"
      description={confirmGone ? describeFilesGone(unreachableMissingCount(confirmGone)) : ""}
      confirmLabel="Mark them as gone"
      confirmVariant="destructive"
      busy={confirmGone !== null && busyId === confirmGone.id}
      onConfirm={() => {
        const check = confirmGone;
        setConfirmGone(null);
        if (check) void filesReallyGone(check);
      }}
    />
    </>
  );
}

function describeFilesGone(count: number): string {
  const what = count === 1 ? "1 file" : `about ${count} files`;
  return `Deluno will check again and, for ${what} that really are missing, stop wanting the title so it is never searched for or downloaded again. Nothing is deleted from your disk. Anything that has come back is left alone.`;
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
      <CardContent className="space-y-(--page-gap)">
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
      return "Needs attention";
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
      <p className="text-(length:--type-body-sm) text-foreground">{label}</p>
      <span
        className={[
          "flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-(length:--type-caption) font-medium",
          isHealthy
            ? "border-success/20 bg-success/10 text-success"
            : isDegraded
              ? "border-warning/20 bg-warning/10 text-warning"
              : isOffline
                ? "border-destructive/20 text-destructive"
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
        "rounded-full border px-2.5 py-0.5 text-(length:--type-micro) font-medium uppercase tracking-wide",
        isRunning
          ? "border-primary/20 bg-primary/10 text-primary"
          : isQueued
            ? "border-warning/20 bg-warning/10 text-warning"
            : isDone
              ? "border-success/20 bg-success/10 text-success"
              : isFailed
                ? "border-destructive/20 text-destructive"
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
