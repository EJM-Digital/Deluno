import { useEffect, useState } from "react";
import { useLoaderData, useLocation, useRevalidator } from "react-router-dom";
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
  Upload,
  Wifi,
  WifiOff
} from "lucide-react";
import { JOB_STATUS, type JobStatus, isJobActive } from "../lib/job-status-constants";
import { SystemShell } from "../components/app/settings-shell";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { KpiCard } from "../components/app/kpi-card";
import { AuditTimeline, type TimelineEvent } from "../components/shell/audit-timeline";
import { WsStatusBadge } from "../components/shell/ws-status-badge";
import { useSignalREvent } from "../lib/use-signalr";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type ActivityEventItem,
  type BackupItem,
  type BackupSettingsSnapshot,
  type DownloadClientItem,
  type IndexerItem,
  type JobQueueItem,
  type LibraryAutomationStateItem,
  type MonitoringDashboardSnapshot,
  type PlatformSettingsSnapshot,
  type RestorePreviewResponse,
  type SearchCycleRunItem,
  type SearchRetryWindowItem,
  type UpdateActionResponse,
  type UpdatePreferencesResponse,
  type UpdateStatusResponse
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { densityDisplayName } from "../lib/use-density";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Input } from "../components/ui/input";
import { PathInput } from "../components/ui/path-input";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SystemLoaderData {
  activity: ActivityEventItem[];
  downloadClients: DownloadClientItem[];
  indexers: IndexerItem[];
  jobs: JobQueueItem[];
  settings: PlatformSettingsSnapshot;
  backups: BackupItem[];
  backupSettings: BackupSettingsSnapshot;
  updateStatus: UpdateStatusResponse;
  automation: LibraryAutomationStateItem[];
  searchCycles: SearchCycleRunItem[];
  retryWindows: SearchRetryWindowItem[];
  monitoring: MonitoringDashboardSnapshot;
}

export async function systemLoader(): Promise<SystemLoaderData> {
  const [settings, jobs, activity, indexers, downloadClients, backups, backupSettings, updateStatus, automation, searchCycles, retryWindows, monitoring] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<JobQueueItem[]>("/api/jobs"),
    fetchJson<ActivityEventItem[]>("/api/activity?take=200"),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    fetchJson<BackupItem[]>("/api/backups"),
    fetchJson<BackupSettingsSnapshot>("/api/backups/settings"),
    fetchJson<UpdateStatusResponse>("/api/updates/status"),
    fetchJson<LibraryAutomationStateItem[]>("/api/library-automation"),
    fetchJson<SearchCycleRunItem[]>("/api/search-cycles?take=12"),
    fetchJson<SearchRetryWindowItem[]>("/api/search-retry-windows?take=12"),
    fetchJson<MonitoringDashboardSnapshot>("/api/monitoring/dashboard")
  ]);

  return { activity, automation, backupSettings, backups, downloadClients, indexers, jobs, monitoring, retryWindows, searchCycles, settings, updateStatus };
}

export function SystemPage() {
  const location = useLocation();
  const revalidator = useRevalidator();
  const loaderData = useLoaderData() as SystemLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { activity, automation, backupSettings, backups, downloadClients, indexers, jobs, monitoring, retryWindows, searchCycles, settings, updateStatus } = loaderData;
  const activeJobs = jobs.filter((job) => isJobActive(job.status as JobStatus)).length;
  const healthyIndexers = indexers.filter((item) => item.healthStatus === "healthy").length;
  const healthyClients = downloadClients.filter((item) => item.healthStatus === "healthy").length;

  /* Live events prepended from WebSocket */
  const [liveEvents, setLiveEvents] = useState<TimelineEvent[]>([]);

  useSignalREvent("ActivityEventAdded", (event) => {
    setLiveEvents((prev) => [
      {
        id: event.id,
        message: event.message,
        category: event.category,
        severity: event.severity,
        createdUtc: event.createdUtc
      },
      ...prev.slice(0, 49)
    ]);
  });

  /* Live job count update */
  const [liveActiveJobs, setLiveActiveJobs] = useState(activeJobs);
  useSignalREvent("QueueItemAdded", () => setLiveActiveJobs((n) => n + 1));
  useSignalREvent("QueueItemRemoved", () => setLiveActiveJobs((n) => Math.max(0, n - 1)));

  const auditCard = (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between gap-3">
          <span>Audit timeline</span>
          <WsStatusBadge />
        </CardTitle>
        <CardDescription>
          Full searchable log of every event Deluno has recorded. Live events stream in from WebSocket.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <AuditTimeline events={activity} liveEvents={liveEvents} maxVisible={500} />
      </CardContent>
    </Card>
  );

  const runtimeCard = (
    <Card>
      <CardHeader>
        <CardTitle>System status</CardTitle>
        <CardDescription>Host settings and defaults for this instance.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <HealthRow label="Bind address" status={`${settings.hostBindAddress}:${settings.hostPort}`} />
        <HealthRow label="URL base" status={settings.urlBase || "/"} />
        <HealthRow label="Authentication" status="Required" />
        <HealthRow label="UI defaults" status={`${settings.uiTheme} / ${densityDisplayName(settings.uiDensity)}`} />
      </CardContent>
    </Card>
  );

  const providerCard = (
    <Card>
      <CardHeader>
        <CardTitle>Provider health</CardTitle>
        <CardDescription>Current health of your search providers and download clients.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {indexers.slice(0, 6).map((indexer) => (
          <HealthRow key={indexer.id} label={indexer.name} status={indexer.healthStatus} />
        ))}
        {downloadClients.slice(0, 4).map((client) => (
          <HealthRow key={client.id} label={client.name} status={client.healthStatus} />
        ))}
      </CardContent>
    </Card>
  );

  const automationCard = (
    <AutomationCard automation={automation} cycles={searchCycles} retryWindows={retryWindows} onRefresh={() => revalidator.revalidate()} />
  );

  const jobsCard = (
    <Card>
      <CardHeader>
        <CardTitle>Recent jobs</CardTitle>
        <CardDescription>Latest background work executing in Deluno.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {jobs.slice(0, 8).map((job) => (
          <div key={job.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
            <div className="flex items-center justify-between gap-3">
              <p className="text-[13px] font-medium leading-snug text-foreground">{job.jobType}</p>
              <JobStatusBadge status={job.status} />
            </div>
            <p className="mt-1 text-[11px] text-muted-foreground">
              {job.source} / {formatWhen(job.createdUtc)}
            </p>
          </div>
        ))}
        {jobs.length === 0 ? (
          <p className="py-6 text-center text-sm text-muted-foreground">No recent jobs.</p>
        ) : null}
      </CardContent>
    </Card>
  );

  if (location.pathname.startsWith("/system/backups")) {
    return (
      <SystemShell title="Backups" description="Manual backups, automatic schedule, restore preview, and backup downloads.">
        <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.35fr)_minmax(340px,0.65fr)]">
          <BackupCard initialBackups={backups} initialSettings={backupSettings} />
          <div className="space-y-[var(--page-gap)]">
            <OperationsFlowCard />
            {runtimeCard}
          </div>
        </div>
      </SystemShell>
    );
  }

  if (location.pathname.startsWith("/system/updates")) {
    return (
      <SystemShell title="Updates" description="Version status, update preferences, and restart workflow.">
        <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,0.95fr)_minmax(0,1.05fr)]">
          <UpgradeCard status={updateStatus} />
          <div className="space-y-[var(--page-gap)]">
            <OperationsFlowCard />
            {runtimeCard}
          </div>
        </div>
      </SystemShell>
    );
  }

  if (location.pathname.startsWith("/system/audit")) {
    return (
      <SystemShell title="Audit Timeline" description="Searchable live system activity.">
        <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.55fr)_minmax(320px,0.45fr)]">
          {auditCard}
          <div className="space-y-[var(--page-gap)]">
            {jobsCard}
            {providerCard}
          </div>
        </div>
      </SystemShell>
    );
  }

  return (
    <SystemShell
      title="System"
      description="Runtime health, background jobs, diagnostics, and the full audit timeline for this Deluno instance."
    >
      {/* KPI row */}
      <div className="fluid-kpi-grid">
        <KpiCard
          label="Active jobs"
          value={String(liveActiveJobs)}
          icon={Activity}
          meta="Queued and running background work."
          sparkline={[1, 2, 2, 3, 2, 4, 3, 4, 3, 4, 5, 4, 4, 5, 4]}
        />
        <KpiCard
          label="Healthy indexers"
          value={`${healthyIndexers}/${indexers.length}`}
          icon={Server}
          meta="Providers currently reporting healthy state."
          sparkline={[3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6]}
        />
        <KpiCard
          label="Healthy clients"
          value={`${healthyClients}/${downloadClients.length}`}
          icon={BellRing}
          meta="Download clients currently reporting healthy state."
          sparkline={[1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3]}
        />
        <KpiCard
          label="Storage roots"
          value={String([settings.movieRootPath, settings.seriesRootPath].filter(Boolean).length)}
          icon={HardDrive}
          meta="Configured media storage roots."
          sparkline={[1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2]}
        />
        <KpiCard
          label="Open alerts"
          value={String(monitoring.alerts.length)}
          icon={BellRing}
          meta={monitoring.alerts.length === 0 ? "No active monitoring alerts." : "Monitoring rules detected conditions that need action."}
          sparkline={[1, 1, 0, 1, 2, 1, 1, 2, 3, 2, 2, 3, 2, 1, monitoring.alerts.length]}
        />
      </div>

      {/* Main grid */}
      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.6fr)_minmax(320px,0.4fr)] 2xl:grid-cols-[minmax(0,1.8fr)_minmax(380px,0.32fr)]">
        {/* Audit timeline — the star of the show */}
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center justify-between gap-3">
              <span>Audit timeline</span>
              <WsStatusBadge />
            </CardTitle>
            <CardDescription>
              Full searchable log of every event Deluno has recorded. Live events stream in from WebSocket.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <AuditTimeline events={activity} liveEvents={liveEvents} maxVisible={500} />
          </CardContent>
        </Card>

        {/* Right column */}
        <div className="space-y-[var(--page-gap)]">
          <OperationsFlowCard />
          <BackupCard initialBackups={backups} initialSettings={backupSettings} />
          <UpgradeCard status={updateStatus} />
          {automationCard}
          <MonitoringCard monitoring={monitoring} />
          {/* Runtime posture */}
          <Card>
            <CardHeader>
              <CardTitle>System status</CardTitle>
              <CardDescription>Host settings and defaults for this instance.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <HealthRow label="Bind address" status={`${settings.hostBindAddress}:${settings.hostPort}`} />
              <HealthRow label="URL base" status={settings.urlBase || "/"} />
              <HealthRow label="Authentication" status="Required" />
              <HealthRow label="UI defaults" status={`${settings.uiTheme} · ${densityDisplayName(settings.uiDensity)}`} />
            </CardContent>
          </Card>

          {/* Provider health */}
          <Card>
            <CardHeader>
              <CardTitle>Provider health</CardTitle>
              <CardDescription>Current health of your search providers and download clients.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {indexers.slice(0, 6).map((indexer) => (
                <HealthRow key={indexer.id} label={indexer.name} status={indexer.healthStatus} />
              ))}
              {downloadClients.slice(0, 4).map((client) => (
                <HealthRow key={client.id} label={client.name} status={client.healthStatus} />
              ))}
            </CardContent>
          </Card>

          {/* Recent jobs */}
          <Card>
            <CardHeader>
              <CardTitle>Recent jobs</CardTitle>
              <CardDescription>Latest background work executing in Deluno.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {jobs.slice(0, 8).map((job) => (
                <div key={job.id} className="rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.7)]">
                  <div className="flex items-center justify-between gap-3">
                    <p className="text-[13px] font-medium text-foreground leading-snug">{job.jobType}</p>
                    <JobStatusBadge status={job.status} />
                  </div>
                  <p className="mt-1 text-[11px] text-muted-foreground">
                    {job.source} · {formatWhen(job.createdUtc)}
                  </p>
                </div>
              ))}
              {jobs.length === 0 ? (
                <p className="py-6 text-center text-sm text-muted-foreground">No recent jobs.</p>
              ) : null}
            </CardContent>
          </Card>
        </div>
      </div>
    </SystemShell>
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
  const [busyLibraryId, setBusyLibraryId] = useState<string | null>(null);
  const running = automation.filter((item) => isJobActive(item.status as JobStatus) || item.searchRequested).length;
  const latest = cycles[0] ?? null;
  const waiting = retryWindows.filter((item) => new Date(item.nextEligibleUtc).getTime() > Date.now()).length;

  async function runNow(libraryId: string) {
    setBusyLibraryId(libraryId);
    try {
      const response = await authedFetch(`/api/libraries/${libraryId}/search-now`, { method: "POST" });
      if (!response.ok) {
        throw new Error(await response.text().catch(() => "Search could not be requested."));
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
      <CardContent className="space-y-4">
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
                  {latest.triggerKind} / {formatWhen(latest.startedUtc)}
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
            <p className="text-[11px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Searches held back until</p>
            {retryWindows.slice(0, 4).map((item) => (
              <div key={`${item.entityType}:${item.entityId}:${item.actionKind}`} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">{item.mediaType} / {item.actionKind}</p>
                  <p className="text-[11px] text-muted-foreground">{item.lastResult || "Last search recorded"}</p>
                </div>
                <span className="font-mono text-[11px] text-muted-foreground">{formatWhen(item.nextEligibleUtc)}</span>
              </div>
            ))}
          </div>
        ) : null}

        {automation.length ? (
          <div className="space-y-2">
            <p className="text-[11px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Libraries</p>
            {automation.slice(0, 5).map((item) => (
              <div key={item.libraryId} className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-background/40 px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">{item.libraryName}</p>
                  <p className="text-[11px] text-muted-foreground">
                    {item.status}{item.nextSearchUtc ? ` / next ${formatWhen(item.nextSearchUtc)}` : ""}
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
      <p className="text-[10px] font-bold uppercase tracking-[0.14em] text-muted-foreground">{label}</p>
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
  initialSettings
}: {
  initialBackups: BackupItem[];
  initialSettings: BackupSettingsSnapshot;
}) {
  const [backups, setBackups] = useState(initialBackups);
  const [settings, setSettings] = useState(initialSettings);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [restorePreview, setRestorePreview] = useState<RestorePreviewResponse | null>(null);
  const [restoreFile, setRestoreFile] = useState<File | null>(null);
  const [showRestoreConfirm, setShowRestoreConfirm] = useState(false);

  async function reload() {
    const [nextBackups, nextSettings] = await Promise.all([
      fetchJson<BackupItem[]>("/api/backups"),
      fetchJson<BackupSettingsSnapshot>("/api/backups/settings")
    ]);
    setBackups(nextBackups);
    setSettings(nextSettings);
  }

  async function createBackup() {
    setBusy("create");
    setMessage(null);
    try {
      await fetchJson("/api/backups", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason: "manual" })
      });
      await reload();
      setMessage("Backup created.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Backup failed.");
    } finally {
      setBusy(null);
    }
  }

  async function saveSchedule() {
    setBusy("schedule");
    setMessage(null);
    try {
      const next = await fetchJson<BackupSettingsSnapshot>("/api/backups/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(settings)
      });
      setSettings(next);
      setMessage("Backup schedule saved.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Schedule could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function previewRestore(file: File) {
    setBusy("preview");
    setMessage(null);
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
    } catch (error) {
      setRestorePreview(null);
      setMessage(error instanceof Error ? error.message : "Restore preview failed.");
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
    setMessage(null);
    const formData = new FormData();
    formData.append("file", restoreFile);
    try {
      const response = await authedFetch("/api/backups/restore", {
        method: "POST",
        body: formData
      });
      if (!response.ok) throw new Error(await response.text());
      const result = await response.json() as { message?: string };
      setMessage(result.message ?? "Restore completed. Restart Deluno before continuing.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Restore failed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck className="h-4 w-4 text-primary" />
          Backup and restore
        </CardTitle>
        <CardDescription>Protect the app database, settings, queues, and integration cache before changes.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-[calc(var(--field-group-pad)*0.9)]">
        {message ? <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">{message}</p> : null}
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
            <label className="flex items-center gap-2 text-sm text-foreground">
              <input
                type="checkbox"
                checked={settings.enabled}
                onChange={(event) => setSettings((current) => ({ ...current, enabled: event.target.checked }))}
              />
              Enable scheduled backups
            </label>
            <select
              value={settings.frequency}
              onChange={(event) => setSettings((current) => ({ ...current, frequency: event.target.value }))}
              className="density-control-text h-[var(--control-height-sm)] rounded-[10px] border border-hairline bg-surface-2 px-3 text-foreground outline-none"
            >
              <option value="daily">Daily</option>
              <option value="weekly">Weekly</option>
              <option value="monthly">Monthly</option>
            </select>
            <Input
              type="time"
              value={settings.timeOfDay}
              onChange={(event) => setSettings((current) => ({ ...current, timeOfDay: event.target.value }))}
            />
            <Input
              type="number"
              min={1}
              max={100}
              value={settings.retentionCount}
              onChange={(event) => setSettings((current) => ({ ...current, retentionCount: Number(event.target.value || 7) }))}
            />
          </div>
          <div className="mt-2">
            <PathInput
              value={settings.backupFolder}
              onChange={(value) => setSettings((current) => ({ ...current, backupFolder: value }))}
              placeholder="Backup folder"
              browseTitle="Choose backup folder"
            />
          </div>
          <div className="mt-3 flex items-center justify-between gap-3">
            <p className="text-xs text-muted-foreground">
              Next run: {settings.nextRunUtc ? formatWhen(settings.nextRunUtc) : "Not scheduled"} · Retains latest {settings.retentionCount} backup{settings.retentionCount === 1 ? "" : "s"}
            </p>
            <Button type="button" size="sm" variant="outline" onClick={() => void saveSchedule()} disabled={busy === "schedule"}>
              {busy === "schedule" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
              Save
            </Button>
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
          <Input
            className="mt-3"
            type="file"
            accept=".zip,application/zip"
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (file) void previewRestore(file);
            }}
          />
          {restorePreview ? (
            <div className="mt-3 rounded-xl border border-hairline bg-background/40 p-3 text-sm">
              <p className={restorePreview.valid ? "text-success" : "text-destructive"}>{restorePreview.message}</p>
              {restorePreview.manifest ? (
                <p className="mt-1 text-xs text-muted-foreground">
                  {restorePreview.manifest.files.length} files · {formatWhen(restorePreview.manifest.createdUtc)}
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
                <p className="text-xs text-muted-foreground">{formatBytes(backup.sizeBytes)} · {formatWhen(backup.createdUtc)}</p>
              </div>
              <Button type="button" variant="ghost" size="sm" asChild>
                <a href={`/api/backups/${backup.id}/download`}>Download</a>
              </Button>
            </div>
          ))}
          {backups.length === 0 ? <p className="text-sm text-muted-foreground">No backups yet.</p> : null}
        </div>
      </CardContent>

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
    </Card>
  );
}

function UpgradeCard({ status }: { status: UpdateStatusResponse }) {
  const [current, setCurrent] = useState(status);
  const [preferences, setPreferences] = useState<UpdatePreferencesResponse | null>(null);
  const [busyAction, setBusyAction] = useState<"check" | "download" | "prepare" | "restart" | "prefs" | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  async function refreshStatus() {
    const next = await fetchJson<UpdateStatusResponse>("/api/updates/status");
    setCurrent(next);
  }

  async function loadPreferences() {
    const next = await fetchJson<UpdatePreferencesResponse>("/api/updates/preferences");
    setPreferences(next);
  }

  async function runAction(action: "check" | "download" | "prepare" | "restart") {
    setBusyAction(action);
    setMessage(null);
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
      setMessage(result.message);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Update action failed.");
      await refreshStatus();
    } finally {
      setBusyAction(null);
    }
  }

  async function savePreferences(next: UpdatePreferencesResponse) {
    setBusyAction("prefs");
    setMessage(null);
    try {
      const saved = await fetchJson<UpdatePreferencesResponse>("/api/updates/preferences", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(savedPreferencePayload(next))
      });
      setPreferences(saved);
      setMessage("Update preferences saved.");
      await refreshStatus();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Could not save preferences.");
    } finally {
      setBusyAction(null);
    }
  }

  useEffect(() => {
    if (preferences === null) {
      void loadPreferences();
    }
  }, [preferences]);

  const isDocker = current.installKind === "docker";
  const canControl = current.isInstalled && !isDocker;
  const controlsDisabledReason = !current.isInstalled
    ? "In-app update controls require the packaged Windows install."
    : isDocker
      ? "Docker updates are applied by pulling a new image and recreating the container."
      : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <RotateCcw className="h-4 w-4 text-primary" />
          Updates
        </CardTitle>
        <CardDescription>Version status, channel, behavior mode, and restart flow.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {message ? (
          <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">{message}</p>
        ) : null}
        <HealthRow label="Current version" status={current.currentVersion} />
        <HealthRow label="Latest version" status={current.latestVersion ?? "No update available"} />
        <HealthRow label="Channel" status={current.channel} />
        <HealthRow label="Install kind" status={installKindLabel(current.installKind)} />
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
            <p className="text-sm font-semibold text-foreground">Update behavior</p>
            <select
              value={preferences.mode}
              onChange={(event) => setPreferences((currentValue) => currentValue ? { ...currentValue, mode: event.target.value } : currentValue)}
              className="density-control-text h-[var(--control-height-sm)] w-full rounded-[10px] border border-hairline bg-surface-2 px-3 text-foreground outline-none"
              disabled={busyAction === "prefs"}
            >
              <option value="notify-only">Notify only</option>
              <option value="download-background">Download in background</option>
              <option value="download-apply-on-restart">Download and apply on next restart</option>
            </select>
            <p className="text-xs text-muted-foreground">
              {updateModeDescription(preferences.mode)}
            </p>
            <label className="flex items-center gap-2 text-sm text-foreground">
              <input
                type="checkbox"
                checked={preferences.autoCheck}
                onChange={(event) => setPreferences((currentValue) => currentValue ? { ...currentValue, autoCheck: event.target.checked } : currentValue)}
                disabled={busyAction === "prefs"}
              />
              Check for updates automatically
            </label>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={busyAction === "prefs"}
              onClick={() => void savePreferences(preferences)}
            >
              {busyAction === "prefs" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}
              Save preferences
            </Button>
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
      </CardContent>
    </Card>
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
      <CardContent className="space-y-4">
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
      <p className="text-[13px] text-foreground">{label}</p>
      <span
        className={[
          "flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-medium",
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
  const isDone = status === "completed" || status === "succeeded";
  const isFailed = status === "failed" || status === "error";

  return (
    <span
      className={[
        "rounded-full border px-2.5 py-0.5 text-[10.5px] font-medium uppercase tracking-wide",
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

function formatWhen(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
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
