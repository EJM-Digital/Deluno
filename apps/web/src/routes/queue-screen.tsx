/**
 * Transfers — the live hand-off view, on the shared grammar.
 *
 *   PageToolbar  (Refresh · Manual import)
 *   SummaryStrip (downloading · speed · ready to import · needs action · clients)
 *   ListCard     downloads        → drawer: progress · health · import · actions
 *   ListCard     needs attention  → only when something does (recovery, failed jobs)
 *   ListCard     recent activity  → one merged timeline, filtered by kind
 *
 * This replaced a page carrying a six-stat hero, a path banner, seven metric
 * tiles, a "next up" panel and nine separate panels — thirteen numbers and a
 * screen and a half of chrome before the first download. The client capability
 * matrix moved to Connections → Download clients, where the clients are set up.
 *
 * Contracts: GET /api/download-clients/telemetry, /api/download-dispatches,
 * /api/{movies,series}/import-recovery, /api/jobs, /api/download-health,
 * /api/integrations/processors/handoffs; POST …/queue/actions, …/jobs/retry-failed,
 * …/handoffs/{id}/retry, /api/filesystem/import/{preview,jobs,execute}.
 */
import { statusPresentation, statusTone } from "../lib/status-tones";
import { useMemo, useRef, useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, Pause, Play, RefreshCw, RotateCw, Trash2, Upload } from "lucide-react";
import {
  ApiRequestError,
  fetchJson, fetchPageItems,
  type DownloadCleanupPreview,
  type DownloadDispatchDetail,
  type DownloadHealthRecord,
  type DownloadDispatchItem,
  type DownloadQueueItem,
  type DownloadTelemetryOverview,
  type ActivityEventItem,
  type ImportJobResponse,
  type ImportPreviewRequest,
  type ImportPreviewResponse,
  type JobQueueItem,
  type LibraryItem,
  type MovieImportRecoverySummary,
  type PlatformSettingsSnapshot,
  type ProcessorConnectionItem,
  type ProcessorHandoffItem,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { formatBytes } from "../lib/utils";
import { resolveImportSourcePath } from "../lib/import-source";
import { JOB_STATUS, isJobActive, isJobDeadLettered, isJobFailed, type JobStatus } from "../lib/job-status-constants";
import { downloadQueueStatuses, isImportReadyStatus, isProcessingStatus, queueStatusLabel } from "../lib/download-telemetry";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { toast } from "../components/shell/toaster";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";

type QueueAction = "pause" | "resume" | "delete" | "recheck";

interface ManualImportForm {
  sourcePath: string;
  fileName: string;
  mediaType: string;
  title: string;
  year: string;
  genres: string;
  tags: string;
  transferMode: string;
}

interface QueueLoaderData {
  telemetry: DownloadTelemetryOverview;
  dispatches: DownloadDispatchItem[];
  movieRecovery: MovieImportRecoverySummary;
  seriesRecovery: SeriesImportRecoverySummary;
  libraries: LibraryItem[];
  settings: PlatformSettingsSnapshot;
  jobs: JobQueueItem[];
  healthRecords: DownloadHealthRecord[];
  processorConnections: ProcessorConnectionItem[];
  processorHandoffs: ProcessorHandoffItem[];
  activityEvents: ActivityEventItem[];
}

export async function queueLoader(): Promise<QueueLoaderData> {
  const [telemetry, dispatches, movieRecovery, seriesRecovery, libraries, settings, jobs, healthRecords, processorConnections, processorHandoffs, activityEvents] = await Promise.all([
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry"),
    fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=60"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchPageItems<JobQueueItem>("/api/jobs?pageSize=80"),
    fetchPageItems<DownloadHealthRecord>("/api/download-health?pageSize=30"),
    fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections").catch((error) => {
      if (error instanceof ApiRequestError && error.status === 404) return [];
      throw error;
    }),
    fetchJson<ProcessorHandoffItem[]>("/api/integrations/processors/handoffs?take=30").catch((error) => {
      // Permit a newly deployed web build to remain usable during a rolling upgrade
      // while an older local API is still running. Other failures remain visible.
      if (error instanceof ApiRequestError && error.status === 404) return [];
      throw error;
    }),
    fetchPageItems<ActivityEventItem>("/api/activity?pageSize=80&relatedEntityType=download_dispatch")
  ]);
  return { telemetry, dispatches, movieRecovery, seriesRecovery, libraries, settings, jobs, healthRecords, processorConnections, processorHandoffs, activityEvents };
}

/** One row shape for every kind of thing that has already happened. */
interface ActivityEntry {
  id: string;
  kind: "import" | "processor" | "sent" | "client" | "health";
  name: string;
  sub: string;
  detail: string;
  extra?: string;
  tone: NonNullable<ChipProps["tone"]>;
  status: string;
  whenUtc: string;
}

type DrawerMode = { kind: "queue"; id: string } | { kind: "manual" } | { kind: "activity"; id: string } | { kind: "dispatch"; id: string } | null;

export function QueuePage() {
  const { telemetry, dispatches, movieRecovery, seriesRecovery, libraries, settings, jobs, healthRecords, processorConnections, processorHandoffs, activityEvents } =
    useLoaderData() as QueueLoaderData;
  const revalidator = useRevalidator();
  const lastDispatchRefresh = useRef(0);

  const scheduleDispatchRefresh = () => {
    const now = Date.now();
    if (revalidator.state !== "idle" || now - lastDispatchRefresh.current < 5_000) return;
    lastDispatchRefresh.current = now;
    revalidator.revalidate();
  };

  useSignalREvent("DispatchGrabCompleted", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchDetected", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchImportStarted", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchGrabAttempt", RealtimeGroups.Queue, (event) => {
    toast.info(`Sent "${event.releaseName}" to ${event.clientName}`);
  });

  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [drawer, setDrawer] = useState<DrawerMode>(null);
  const [dispatchDetails, setDispatchDetails] = useState<Record<string, DownloadDispatchDetail>>({});
  const [importPreviews, setImportPreviews] = useState<Record<string, ImportPreviewResponse>>({});
  const [cleanupPreviews, setCleanupPreviews] = useState<Record<string, DownloadCleanupPreview>>({});
  const [pendingRemoval, setPendingRemoval] = useState<DownloadQueueItem | null>(null);
  const [activityKind, setActivityKind] = useState<"all" | ActivityEntry["kind"]>("all");
  const [manualImport, setManualImport] = useState<ManualImportForm>(() => ({
    sourcePath: "",
    fileName: "",
    mediaType: "movies",
    title: "",
    year: "",
    genres: "",
    tags: "",
    transferMode: "auto"
  }));
  const [manualPreview, setManualPreview] = useState<ImportPreviewResponse | null>(null);
  const [manualMessage, setManualMessage] = useState<string | null>(null);
  // DrawerFooter only renders a message for a non-clean state, so the outcome of a
  // preview or a queue has to move the state, not just the text.
  const [manualState, setManualState] = useState<"clean" | "saving" | "saved" | "error">("clean");

  /* ------------------------------------------------------------- derived */
  const queue = useMemo(
    () => telemetry.clients.flatMap((client) => client.queue.map((item) => ({ ...item, clientProtocol: client.protocol }))),
    [telemetry.clients]
  );
  const importJobs = useMemo(() => jobs.filter((job) => job.jobType === "filesystem.import.execute"), [jobs]);
  const importReady = queue.filter((item) => isImportReadyStatus(item.status));
  const processing = queue.filter((item) => isProcessingStatus(item.status));
  const queueAttention = queue.filter(
    (item) => item.status === downloadQueueStatuses.stalled || Boolean(item.errorMessage) || Boolean(item.healthFindings?.length)
  );
  // Dead-lettered jobs are failed and out of retries: they are exactly what
  // "Retry N failed" exists for, and were previously excluded (#249).
  const failedImportJobs = importJobs.filter((job) => isJobFailed(job.status as JobStatus));
  const activeImportJobs = importJobs.filter((job) => isJobActive(job.status as never)).length;
  const activeProcessorHandoffs = processorHandoffs.filter((handoff) => !isProcessorTerminal(handoff.status));
  const failedProcessorHandoffs = processorHandoffs.filter((handoff) => isProcessorFailure(handoff.status) || Boolean(handoff.failureMessage));
  const failedDispatches = dispatches.filter(isDispatchFailure);
  const blockedQueueItems = queue.filter((item) => item.healthFindings?.some((finding) => finding.candidateBlocked));
  const healthyClients = telemetry.clients.filter((client) => isHealthyClient(client.healthStatus)).length;
  const recoveryCases = useMemo(
    () => [
      ...movieRecovery.recentCases.map((item) => ({ ...item, mediaType: "movie" as const })),
      ...seriesRecovery.recentCases.map((item) => ({ ...item, mediaType: "series" as const }))
    ],
    [movieRecovery.recentCases, seriesRecovery.recentCases]
  );
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;
  const needsAction = openRecovery + queueAttention.length + failedImportJobs.length + failedProcessorHandoffs.length + failedDispatches.length;

  const activity = useMemo(
    () => buildActivity({ importJobs, processorHandoffs, dispatches, telemetry, healthRecords, activityEvents }),
    [importJobs, processorHandoffs, dispatches, telemetry, healthRecords, activityEvents]
  );
  const visibleActivity = activityKind === "all" ? activity : activity.filter((entry) => entry.kind === activityKind);
  const openQueueItem = drawer?.kind === "queue" ? queue.find((item) => item.id === drawer.id) ?? null : null;
  const openActivity = drawer?.kind === "activity" ? activity.find((entry) => entry.id === drawer.id) ?? null : null;
  const openDispatchSummary = drawer?.kind === "dispatch" ? dispatches.find((item) => item.id === drawer.id) ?? null : null;
  const openDispatchDetail = drawer?.kind === "dispatch" ? dispatchDetails[drawer.id] ?? null : null;

  /* ------------------------------------------------------------- actions */
  async function run(key: string, action: () => Promise<unknown>, success?: string) {
    setBusyKey(key);
    try {
      await action();
      if (success) toast.success(success);
      revalidator.revalidate();
      return true;
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That action could not be completed.");
      return false;
    } finally {
      setBusyKey(null);
    }
  }

  async function showDispatch(dispatch: DownloadDispatchItem) {
    setDrawer({ kind: "dispatch", id: dispatch.id });
    if (dispatchDetails[dispatch.id]) return;
    setBusyKey(`dispatch:${dispatch.id}`);
    try {
      const detail = await fetchJson<DownloadDispatchDetail>(`/api/v1/download-dispatches/${encodeURIComponent(dispatch.id)}`);
      setDispatchDetails((current) => ({ ...current, [dispatch.id]: detail }));
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The transfer details could not be loaded.");
    } finally {
      setBusyKey(null);
    }
  }

  async function queueAction(item: DownloadQueueItem, action: QueueAction) {
    return run(
      `queue:${item.clientId}:${item.id}:${action}`,
      async () => {
        const response = await authedFetch(`/api/download-clients/${item.clientId}/queue/actions`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ action, queueItemId: item.id })
        });
        if (!response.ok) throw new Error((await response.text().catch(() => "")) || "The download action failed.");
      },
      `${actionLabel(action)} sent to ${item.clientName}`
    );
  }

  async function previewImport(item: DownloadQueueItem) {
    await run(`preview:${item.id}`, async () => {
      const sourcePath = resolveImportSourcePath(item, libraries);
      if (!sourcePath) throw new Error("The download client has not reported a completed file location, and this library has no folder override.");
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildImportRequest(item, libraries))
      });
      setImportPreviews((current) => ({ ...current, [item.id]: preview }));
    });
  }

  async function queueImport(item: DownloadQueueItem) {
    await run(
      `import:${item.id}`,
      async () => {
        const sourcePath = resolveImportSourcePath(item, libraries);
        if (!sourcePath) throw new Error("The download client has not reported a completed file location, and this library has no folder override.");
        const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            preview: buildImportRequest(item, libraries),
            transferMode: "auto",
            overwrite: false,
            allowCopyFallback: true,
            forceReplacement: false
          })
        });
        setImportPreviews((current) => ({ ...current, [item.id]: result.preview }));
      },
      `${item.title} queued for import`
    );
  }

  async function previewCleanup(item: DownloadQueueItem) {
    await run(`cleanup:${item.id}`, async () => {
      const preview = await fetchJson<DownloadCleanupPreview>(`/api/download-clients/${item.clientId}/queue/${item.id}/cleanup-preview`);
      setCleanupPreviews((current) => ({ ...current, [`${item.clientId}:${item.id}`]: preview }));
    });
  }

  async function retryFailedJobs() {
    await run(
      "jobs:retry-failed",
      async () => {
        const response = await authedFetch("/api/jobs/retry-failed", { method: "POST" });
        if (!response.ok) throw new Error("The failed jobs could not be requeued.");
      },
      `${failedImportJobs.length} failed ${failedImportJobs.length === 1 ? "job" : "jobs"} requeued`
    );
  }

  async function retryRecovery(item: (typeof recoveryCases)[number]) {
    const collection = item.mediaType === "movie" ? "movies" : "series";
    await run(
      `recovery:retry:${item.id}`,
      async () => {
        const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/retry`, { method: "POST" });
        if (!response.ok) throw new Error("That import could not be tried again.");
      },
      `${item.title} queued to try again`
    );
  }

  async function dismissRecovery(item: (typeof recoveryCases)[number]) {
    const collection = item.mediaType === "movie" ? "movies" : "series";
    await run(
      `recovery:dismiss:${item.id}`,
      async () => {
        const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/dismiss`, { method: "POST" });
        if (!response.ok) throw new Error("That case could not be dismissed.");
      },
      `${item.title} dismissed`
    );
  }

  async function retryHandoff(handoff: ProcessorHandoffItem) {
    await run(
      `handoff:${handoff.id}`,
      async () => {
        const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/retry`, { method: "POST" });
        if (!response.ok) {
          const body = (await response.json().catch(() => null)) as { message?: string } | null;
          throw new Error(body?.message || "The processor hand-off could not be tried again.");
        }
      },
      "Hand-off queued to try again with the same ID"
    );
  }

  function manualRequest(): ImportPreviewRequest {
    return {
      sourcePath: manualImport.sourcePath.trim(),
      fileName: manualImport.fileName.trim() || null,
      mediaType: manualImport.mediaType,
      title: manualImport.title.trim() || null,
      year: manualImport.year ? Number(manualImport.year) : null,
      genres: splitCsv(manualImport.genres),
      tags: splitCsv(manualImport.tags),
      studio: null,
      originalLanguage: null
    };
  }

  /** Preview and queue both report into the drawer that asked, not a toast. */
  async function manualRun(mode: "preview" | "queue") {
    if (!manualImport.sourcePath.trim()) {
      setManualState("error");
      setManualMessage("Choose a source file or folder first.");
      return;
    }
    setBusyKey(`manual:${mode}`);
    setManualState("saving");
    setManualMessage(null);
    try {
      if (mode === "preview") {
        setManualPreview(await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(manualRequest())
        }));
        setManualState("saved");
        setManualMessage("Preview ready — check the destination before queueing.");
      } else {
        const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ preview: manualRequest(), transferMode: manualImport.transferMode, overwrite: false, allowCopyFallback: true, forceReplacement: false })
        });
        setManualPreview(result.preview);
        setManualState("saved");
        setManualMessage(`Queued as job ${result.jobId.slice(0, 8)}.`);
        revalidator.revalidate();
      }
    } catch (error) {
      setManualState("error");
      setManualMessage(error instanceof Error ? error.message : "That could not be completed.");
    } finally {
      setBusyKey(null);
    }
  }

  /* -------------------------------------------------------------- render */
  return (
    <div className="flex flex-col gap-[var(--page-gap)]">
      <PageToolbar
        actions={
          <>
            <Button type="button" variant="outline" onClick={() => revalidator.revalidate()} disabled={revalidator.state !== "idle"}>
              {revalidator.state !== "idle" ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
              Refresh
            </Button>
            <Button type="button" onClick={() => { setManualMessage(null); setManualState("clean"); setDrawer({ kind: "manual" }); }}>
              <Upload className="h-4 w-4" />
              Manual import
            </Button>
          </>
        }
      />

      <SummaryStrip
        cells={[
          { label: "Downloading", value: String(telemetry.summary.activeCount), help: `${telemetry.summary.totalSpeedMbps.toFixed(1)} MB/s combined` },
          { label: "Processing", value: String(processing.length + activeProcessorHandoffs.length), help: activeProcessorHandoffs.length ? `${activeProcessorHandoffs.length} with the processor` : processorConnections.length ? `${processorConnections.length} connector${processorConnections.length === 1 ? "" : "s"} configured` : "no processor connected" },
          { label: "Importing", value: String(activeImportJobs), help: activeImportJobs ? "writing into your library" : "nothing being written" },
          // Automation picks these up on its next pass; "waiting on you" asked
          // for something the user does not need to do (#263).
          { label: "Ready to import", value: String(importReady.length), help: importReady.length ? "Deluno imports these next" : "nothing waiting" },
          { label: "Needs action", value: String(needsAction), help: blockedQueueItems.length ? `${blockedQueueItems.length} release${blockedQueueItems.length === 1 ? "" : "s"} blocked` : needsAction ? "see below" : "all clear", tone: needsAction ? "warning" : undefined },
          { label: "Clients", value: `${healthyClients}/${telemetry.clients.length}`, help: healthyClients === telemetry.clients.length ? "all responding" : "one is not responding", tone: healthyClients < telemetry.clients.length ? "danger" : undefined }
        ]}
      />

      <ListCard
        title="Media pipeline"
        count={queue.length ? `${queue.length} in flight · ${activeImportJobs} importing` : undefined}
      >
        {queue.length === 0 ? (
          <ListEmpty
            title="Nothing in motion"
            description="Media appears here from the moment Deluno sends it to a download client through processing, naming, and import."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Media" },
              { label: "Stage", width: "minmax(0,1.25fr)" },
              { label: "Progress", width: "minmax(0,1.2fr)" },
              { label: "Speed / left", mobile: true },
              { label: "Client" },
              { label: "Status", width: LIST_TRACK.status, mobile: true }
            ]}
          >
            {queue.map((item) => {
              const chip = queueChip(item);
              // Colour follows what needs a person, not what finished. A
              // completed transfer used to be the brightest thing on the page —
              // a full saturated bar at 100% and 0.0 MB/s — while a failure sat
              // in plain text below it (#263).
              const transferring = item.speedMbps > 0 || (item.progress > 0 && item.progress < 100);
              const needsAttention = chip.tone === "bad" || chip.tone === "warn";
              return (
                <ListRow key={`${item.clientId}:${item.id}`} onClick={() => setDrawer({ kind: "queue", id: item.id })} selected={openQueueItem?.id === item.id}>
                  <ListNameCell
                    name={item.title || item.releaseName}
                    sub={<span className="font-mono">{item.releaseName}</span>}
                  />
                  <ListCell primary={pipelineStage(item)} secondary={pipelineDetail(item)} />
                  <ListCell>
                    {transferring ? (
                      <>
                        <ProgressBar value={item.progress} />
                        <span className="mt-1 block truncate text-[length:var(--type-caption)] tabular-nums text-muted-foreground">
                          {Math.round(item.progress)}% of {formatBytes(item.sizeBytes)}
                        </span>
                      </>
                    ) : (
                      // Finished, queued or stalled: a bar would say "in
                      // progress" about something that is not.
                      <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
                        {formatBytes(item.sizeBytes)}
                      </span>
                    )}
                  </ListCell>
                  <ListCell
                    numeric
                    primary={transferring ? `${item.speedMbps.toFixed(1)} MB/s` : "—"}
                    secondary={transferring && item.etaSeconds > 0 ? formatEta(item.etaSeconds) : undefined}
                  />
                  <ListCell primary={item.clientName} secondary={item.indexerName || undefined} />
                  <ListCell mobile>
                    <Chip tone={needsAttention ? chip.tone : transferring ? "info" : "idle"}>{chip.label}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      {processorHandoffs.length || processorConnections.length ? (
        <ListCard
          title="Processing connector"
          count={activeProcessorHandoffs.length ? `${activeProcessorHandoffs.length} active · ${processorConnections.length} configured` : `${processorConnections.length} configured · latest ${processorHandoffs.length}`}
        >
          {processorConnections.length ? (
            <div className="flex flex-wrap gap-2 border-b border-hairline px-[var(--card-pad-x)] py-3">
              {processorConnections.map((connection) => (
                <Chip key={connection.id} tone={processorConnectionTone(connection)}>
                  {connection.name} · {processorConnectionStatus(connection)}
                </Chip>
              ))}
            </div>
          ) : null}
          {processorHandoffs.length ? (
            <ListTable columns={[{ label: "Media" }, { label: "Connector" }, { label: "Stage" }, { label: "Source / output", width: "minmax(0,1.8fr)" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
              {processorHandoffs.map((handoff) => (
                <ListRow key={handoff.id} onClick={() => setDrawer({ kind: "activity", id: `processor:${handoff.id}` })} selected={openActivity?.id === `processor:${handoff.id}`}>
                  <ListNameCell name={handoff.releaseName} sub={handoff.mediaType === "tv" ? "TV" : "Movies"} />
                  <ListCell primary={handoff.processorName ?? "Configured processor"} secondary={handoff.outputPath ? "Output received" : "Waiting for output"} />
                  <ListCell primary={processorStageLabel(handoff.status)} secondary={handoff.failureMessage ?? undefined} />
                  <ListCell primary={handoff.outputPath ?? handoff.sourcePath} secondary={handoff.outputPath ? `From ${handoff.sourcePath}` : "Source hand-off"} />
                  <ListCell mobile><Chip tone={processorTone(handoff)}>{processorStatusLabel(handoff.status)}</Chip></ListCell>
                </ListRow>
              ))}
            </ListTable>
          ) : (
            <ListEmpty title="No media is being processed" description="The connector is configured. When Deluno hands media to it, its status and returned output will appear here." />
          )}
        </ListCard>
      ) : null}

      {importJobs.length ? (
        <ListCard
          title="Import & naming"
          count={`${activeImportJobs} active · latest ${importJobs.length}`}
        >
          <ListTable columns={[{ label: "Media" }, { label: "Source / destination", width: "minmax(0,2fr)" }, { label: "Stage" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
            {importJobs.map((job) => {
              const info = parseImportJobPayload(job.payloadJson);
              return (
                <ListRow key={job.id} onClick={() => setDrawer({ kind: "activity", id: `import:${job.id}` })} selected={openActivity?.id === `import:${job.id}`}>
                  <ListNameCell name={info?.title || info?.fileName || jobTitle(job)} sub={info?.fileName || "Filesystem import"} />
                  <ListCell primary={info?.destinationPath || "Destination not resolved yet"} secondary={info?.sourcePath ? `From ${info.sourcePath}` : "Source path not recorded"} />
                  <ListCell primary={importJobStage(job)} secondary={job.attempts > 1 ? `Attempt ${job.attempts}` : undefined} />
                  <ListCell mobile><Chip tone={importJobTone(job)}>{job.status}</Chip></ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        </ListCard>
      ) : null}

      {needsAction > 0 ? (
        <ListCard
          title="Needs attention"
          count={`${needsAction} ${needsAction === 1 ? "thing" : "things"} Deluno could not finish on its own`}
          actions={
            failedImportJobs.length ? (
              <Button type="button" variant="outline" size="sm" onClick={() => void retryFailedJobs()} disabled={busyKey !== null}>
                {busyKey === "jobs:retry-failed" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RotateCw className="h-3.5 w-3.5" />}
                Retry {failedImportJobs.length} failed
              </Button>
            ) : undefined
          }
        >
          <ListTable
            columns={[{ label: "Item" }, { label: "What happened", width: "minmax(0,1.8fr)" }, { label: "Fix", width: "180px", mobile: true, srOnly: true }]}
            chevron={false}
          >
            {recoveryCases.map((item) => (
              <ListRow key={`${item.mediaType}:${item.id}`}>
                <ListNameCell name={item.title} sub={item.mediaType === "movie" ? "Movies" : "TV"} />
                <ListCell primary={item.summary} secondary={item.recommendedAction} />
                <ListCell mobile align="end">
                  <span className="flex justify-end gap-2">
                    <Button type="button" variant="outline" size="sm" onClick={() => void retryRecovery(item)} disabled={busyKey !== null}>
                      {busyKey === `recovery:retry:${item.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                      Try again
                    </Button>
                    <Button type="button" variant="outline" size="sm" onClick={() => void dismissRecovery(item)} disabled={busyKey !== null}>
                      Dismiss
                    </Button>
                  </span>
                </ListCell>
              </ListRow>
            ))}
            {queueAttention.map((item) => (
              <ListRow key={`attention:${item.clientId}:${item.id}`} onClick={() => setDrawer({ kind: "queue", id: item.id })}>
                <ListNameCell name={item.title || item.releaseName} sub={item.clientName} />
                <ListCell
                  primary={attentionReason(item)}
                  secondary={attentionEvidence(item)}
                />
                <ListCell mobile align="end">
                  <span className="text-[length:var(--type-caption)] text-muted-foreground">Open to fix</span>
                </ListCell>
              </ListRow>
            ))}
            {failedImportJobs.map((job) => (
              <ListRow key={`job:${job.id}`}>
                <ListNameCell name={jobTitle(job)} sub="Import job" />
                <ListCell primary={job.lastError ?? "The import failed."} secondary={`Attempt ${job.attempts}`} />
                <ListCell mobile align="end">
                  <span className="text-[length:var(--type-caption)] text-muted-foreground">Use Retry above</span>
                </ListCell>
                </ListRow>
            ))}
            {failedProcessorHandoffs.map((handoff) => (
              <ListRow key={`processor-failure:${handoff.id}`} onClick={() => setDrawer({ kind: "activity", id: `processor:${handoff.id}` })}>
                <ListNameCell name={handoff.releaseName} sub={handoff.processorName ?? "Processing connector"} />
                <ListCell primary={handoff.failureMessage ?? `Processor reported ${processorStatusLabel(handoff.status)}.`} secondary={handoff.sourcePath} />
                <ListCell mobile align="end"><span className="text-[length:var(--type-caption)] text-muted-foreground">Open to retry</span></ListCell>
              </ListRow>
            ))}
            {failedDispatches.map((dispatch) => (
              <ListRow key={`dispatch-failure:${dispatch.id}`} onClick={() => void showDispatch(dispatch)} selected={openDispatchSummary?.id === dispatch.id}>
                <ListNameCell name={dispatch.releaseName} sub={dispatch.mediaType === "tv" ? "TV" : "Movies"} />
                <ListCell primary={dispatchActivityDetail(dispatch)} secondary={dispatchFailureDetail(dispatch)} />
                <ListCell mobile align="end"><span className="text-[length:var(--type-caption)] text-muted-foreground">Open transfer details</span></ListCell>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      <ListCard
        title="Cleanup guardrails"
        count={cleanupPolicyLabel(settings)}
      >
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <p className="max-w-3xl text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">
            Transfers records why media failed, was blocked, or was removed. Deluno will not delete a client entry or payload just because a health check failed unless the matching safeguards are enabled.
          </p>
          <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
            <PolicyFact label="Block release" enabled={settings.cleanupBlockReleaseAfterThreshold} />
            <PolicyFact label="Queue replacement" enabled={settings.cleanupQueueReplacementAfterThreshold} />
            <PolicyFact label="Remove client entry" enabled={settings.cleanupRemoveClientEntryAfterThreshold} />
            <PolicyFact label="Purge payload" enabled={settings.cleanupPurgePayloadAfterThreshold} />
          </div>
          <div>
            <Button asChild type="button" variant="outline" size="sm">
              <Link to="/search-cycles">Review Automation &amp; Recovery</Link>
            </Button>
          </div>
        </div>
      </ListCard>

      <ListCard
        title="Recent activity"
        count={activity.length ? `latest ${Math.min(visibleActivity.length, 25)} of ${activity.length}` : undefined}
        actions={
          activity.length ? (
            <SegmentedControl<"all" | ActivityEntry["kind"]>
              aria-label="Filter activity"
              className="w-auto"
              value={activityKind}
              onValueChange={setActivityKind}
              options={[
                { value: "all", label: "All" },
                { value: "import", label: "Imports" },
                { value: "processor", label: "Processing" },
                { value: "sent", label: "Sent" },
                { value: "client", label: "Clients" },
                { value: "health", label: "Issues" }
              ]}
            />
          ) : undefined
        }
      >
        {visibleActivity.length === 0 ? (
          <ListEmpty title="Nothing has happened yet" description="Imports, processor hand-offs, releases Deluno sent to a client, and what those clients reported back all land here." />
        ) : (
          <ListTable
            columns={[{ label: "Item" }, { label: "What happened", width: "minmax(0,1.6fr)" }, { label: "When", width: "150px" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}
          >
            {visibleActivity.slice(0, 25).map((entry) => (
              <ListRow
                key={entry.id}
                onClick={() => {
                  if (entry.kind === "sent") {
                    const dispatch = dispatches.find((item) => `sent:${item.id}` === entry.id);
                    if (dispatch) {
                      void showDispatch(dispatch);
                      return;
                    }
                  }
                  setDrawer({ kind: "activity", id: entry.id });
                }}
                selected={openActivity?.id === entry.id || (entry.kind === "sent" && openDispatchSummary?.id === entry.id.slice("sent:".length))}
              >
                <ListNameCell name={entry.name} sub={entry.sub} />
                <ListCell primary={entry.detail} secondary={entry.extra} />
                <ListCell numeric primary={formatAgo(entry.whenUtc)} secondary={formatDateTime(entry.whenUtc)} />
                <ListCell mobile>
                  <Chip tone={entry.tone}>{entry.status}</Chip>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      {/* -------------------------------------------------- download drawer */}
      <Drawer
        open={drawer?.kind === "queue"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openQueueItem?.title || openQueueItem?.releaseName || "Download"}
        description={openQueueItem ? `${openQueueItem.clientName} · ${queueStatusLabel(openQueueItem.status)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setDrawer(null)} />}
      >
        {openQueueItem ? (
          <>
            <DrawerSection title="Progress">
              <ProgressBar value={openQueueItem.progress} />
              <div className="grid gap-1.5">
                <Fact label="Done" value={`${Math.round(openQueueItem.progress)}% — ${formatBytes(openQueueItem.downloadedBytes)} of ${formatBytes(openQueueItem.sizeBytes)}`} />
                <Fact label="Speed" value={openQueueItem.speedMbps > 0 ? `${openQueueItem.speedMbps.toFixed(1)} MB/s` : "Not moving"} />
                <Fact label="Time left" value={openQueueItem.etaSeconds > 0 ? formatEta(openQueueItem.etaSeconds) : "Unknown"} />
                <Fact label="Peers" value={String(openQueueItem.peers)} />
                <Fact label="From" value={openQueueItem.indexerName || "Unknown source"} />
                <Fact label="Release" value={openQueueItem.releaseName} mono />
              </div>
            </DrawerSection>

            {openQueueItem.errorMessage || openQueueItem.healthFindings?.length ? (
              <DrawerSection title="Health">
                {openQueueItem.errorMessage ? <p className="text-[length:var(--type-body-sm)] text-destructive">{openQueueItem.errorMessage}</p> : null}
                {(openQueueItem.healthFindings ?? []).map((finding, index) => (
                  <div key={index} className="grid gap-0.5 border-b border-hairline py-2 last:border-b-0">
                    <span className="text-[length:var(--type-body-sm)] text-foreground">{finding.summary}</span>
                    {finding.recommendedAction ? (
                      <span className="text-[length:var(--type-caption)] text-muted-foreground">{finding.recommendedAction}</span>
                    ) : null}
                  </div>
                ))}
              </DrawerSection>
            ) : null}

            <DrawerSection
              title="Import"
              aside={isImportReadyStatus(openQueueItem.status) ? "ready" : queueStatusLabel(openQueueItem.status)}
            >
              {importPreviews[openQueueItem.id] ? (
                <ImportPreviewFacts preview={importPreviews[openQueueItem.id]!} />
              ) : (
                <p className="text-[length:var(--type-caption)] text-muted-foreground">
                  Preview shows exactly where the file would land and what it would be renamed to, before anything moves.
                </p>
              )}
              <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" size="sm" onClick={() => void previewImport(openQueueItem)} disabled={busyKey !== null}>
                  {busyKey === `preview:${openQueueItem.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Preview import
                </Button>
                <Button type="button" size="sm" onClick={() => void queueImport(openQueueItem)} disabled={busyKey !== null || !isImportReadyStatus(openQueueItem.status)}>
                  {busyKey === `import:${openQueueItem.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Queue import
                </Button>
              </div>
            </DrawerSection>

            <DrawerSection title="In the client">
              <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" size="sm" onClick={() => void queueAction(openQueueItem, "pause")} disabled={busyKey !== null}>
                  <Pause className="h-3.5 w-3.5" />
                  Pause
                </Button>
                <Button type="button" variant="outline" size="sm" onClick={() => void queueAction(openQueueItem, "resume")} disabled={busyKey !== null}>
                  <Play className="h-3.5 w-3.5" />
                  Resume
                </Button>
                <Button type="button" variant="outline" size="sm" onClick={() => void queueAction(openQueueItem, "recheck")} disabled={busyKey !== null}>
                  <RotateCw className="h-3.5 w-3.5" />
                  Re-check
                </Button>
                <Button type="button" variant="outline" size="sm" onClick={() => void previewCleanup(openQueueItem)} disabled={busyKey !== null}>
                  What would removing do?
                </Button>
                <Button type="button" variant="destructive" size="sm" onClick={() => setPendingRemoval(openQueueItem)} disabled={busyKey !== null}>
                  <Trash2 className="h-3.5 w-3.5" />
                  Remove from client
                </Button>
              </div>
              {cleanupPreviews[`${openQueueItem.clientId}:${openQueueItem.id}`] ? (
                <div className="grid gap-1.5">
                  <Fact label="Would do" value={cleanupPreviews[`${openQueueItem.clientId}:${openQueueItem.id}`]!.proposedAction} />
                  <Fact label="Because" value={cleanupPreviews[`${openQueueItem.clientId}:${openQueueItem.id}`]!.reason} />
                  <Fact label="Files affected" value={cleanupPreviews[`${openQueueItem.clientId}:${openQueueItem.id}`]!.affectedFiles} mono />
                  <Fact label="Allowed" value={cleanupPreviews[`${openQueueItem.clientId}:${openQueueItem.id}`]!.removalAllowed ? "Yes" : "Needs review first"} />
                </div>
              ) : null}
              <p className="text-[length:var(--type-caption)] text-muted-foreground">
                Deluno asks the client to act. The client owns its own payload — nothing in your media library is touched from here.
              </p>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      {/* ---------------------------------------------------- manual import */}
      <Drawer
        open={drawer?.kind === "manual"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title="Manual import"
        description="Bring in a file Deluno did not download itself"
        footer={
          <DrawerFooter
            state={manualState}
            message={manualMessage}
            saveType="button"
            saveLabel="Queue import"
            saveEnabled={Boolean(manualImport.sourcePath.trim()) && busyKey === null}
            onSave={() => void manualRun("queue")}
            onCancel={() => setDrawer(null)}
          />
        }
      >
        <DrawerSection title="Source">
          <Field label="File or folder" help="Anything Deluno can read — it is copied or linked, never moved out from under you without a preview.">
            <PathInput value={manualImport.sourcePath} onChange={(value) => setManualImport((current) => ({ ...current, sourcePath: value }))} placeholder="D:/Downloads/Some.Release.2024" />
          </Field>
          <FieldRow>
            <Field label="Media type">
              <SegmentedControl<string>
                aria-label="Media type"
                value={manualImport.mediaType}
                onValueChange={(value) => setManualImport((current) => ({ ...current, mediaType: value }))}
                options={[
                  { value: "movies", label: "Movies" },
                  { value: "tv", label: "TV" }
                ]}
              />
            </Field>
            <Field label="Transfer method" help="Automatic uses a single-copy link (hardlink) when possible, so the download can keep seeding without a duplicate. If that is not possible, Deluno copies or moves the file.">
              <Select
                value={manualImport.transferMode}
                onChange={(event) => setManualImport((current) => ({ ...current, transferMode: event.target.value }))}
                options={[
                  { value: "auto", label: "Automatic" },
                  { value: "hardlink", label: "Single-copy link (hardlink)" },
                  { value: "copy", label: "Copy the file" },
                  { value: "move", label: "Move the file" }
                ]}
              />
            </Field>
          </FieldRow>
        </DrawerSection>

        <DrawerSection title="Help Deluno match it" aside="optional">
          <FieldRow>
            <Field label="Title" optional help="Only needed when the file name does not say.">
              <Input value={manualImport.title} onChange={(event) => setManualImport((current) => ({ ...current, title: event.target.value }))} />
            </Field>
            <Field label="Year" optional>
              <Input type="number" value={manualImport.year} onChange={(event) => setManualImport((current) => ({ ...current, year: event.target.value }))} />
            </Field>
          </FieldRow>
          <Field label="File name" optional help="Override the file inside the folder to import.">
            <Input value={manualImport.fileName} onChange={(event) => setManualImport((current) => ({ ...current, fileName: event.target.value }))} className="font-mono" />
          </Field>
        </DrawerSection>

        <DrawerSection title="Where it would land" aside={manualPreview ? undefined : "run a preview"}>
          {manualPreview ? (
            <ImportPreviewFacts preview={manualPreview} />
          ) : (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">Preview first — it shows the destination path and the new name without writing anything.</p>
          )}
          <div>
            <Button type="button" variant="outline" size="sm" onClick={() => void manualRun("preview")} disabled={busyKey !== null || !manualImport.sourcePath.trim()}>
              {busyKey === "manual:preview" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
              Preview
            </Button>
          </div>
        </DrawerSection>
      </Drawer>

      {/* ----------------------------------------------- dispatch details */}
      <Drawer
        open={drawer?.kind === "dispatch"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openDispatchDetail?.dispatch.releaseName ?? openDispatchSummary?.releaseName ?? "Transfer details"}
        description={openDispatchDetail ? `${openDispatchDetail.dispatch.downloadClientName || "Download client"} · ${dispatchStageLabel(openDispatchDetail.dispatch)}` : "Loading the recorded transfer journey"}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setDrawer(null)} />}
      >
        {openDispatchDetail ? (
          <>
            <DrawerSection title="Transfer journey" aside={dispatchStageLabel(openDispatchDetail.dispatch)}>
              <div className="grid gap-1.5">
                <Fact label="Release" value={openDispatchDetail.dispatch.releaseName} mono />
                <Fact label="Indexer" value={openDispatchDetail.dispatch.indexerName || "Unknown source"} />
                <Fact label="Client" value={openDispatchDetail.dispatch.downloadClientName || "Unassigned client"} />
                <Fact label="Grab" value={dispatchOutcome(openDispatchDetail.dispatch.grabStatus, openDispatchDetail.dispatch.grabMessage)} />
                <Fact label="Detected" value={openDispatchDetail.dispatch.detectedUtc ? formatDateTime(openDispatchDetail.dispatch.detectedUtc) : "Not detected by the client"} />
                <Fact label="Import" value={dispatchOutcome(openDispatchDetail.dispatch.importStatus, openDispatchDetail.dispatch.importFailureMessage)} />
                <Fact label="Attempts" value={String(openDispatchDetail.dispatch.attemptCount ?? 0)} />
              </div>
            </DrawerSection>

            {openDispatchDetail.dispatch.importedFilePath || openDispatchDetail.dispatch.importFailureMessage || openDispatchDetail.dispatch.grabFailureCode ? (
              <DrawerSection title="File and outcome">
                <div className="grid gap-1.5">
                  {openDispatchDetail.dispatch.importedFilePath ? <Fact label="Imported file" value={openDispatchDetail.dispatch.importedFilePath} mono /> : null}
                  {openDispatchDetail.dispatch.downloadedBytes ? <Fact label="Downloaded" value={formatBytes(openDispatchDetail.dispatch.downloadedBytes)} /> : null}
                  {openDispatchDetail.dispatch.grabFailureCode ? <Fact label="Grab issue" value={openDispatchDetail.dispatch.grabFailureCode} /> : null}
                  {openDispatchDetail.dispatch.importFailureMessage ? <Fact label="Import issue" value={openDispatchDetail.dispatch.importFailureMessage} /> : null}
                  {openDispatchDetail.dispatch.nextRetryEligibleUtc ? <Fact label="Next retry" value={formatDateTime(openDispatchDetail.dispatch.nextRetryEligibleUtc)} /> : null}
                </div>
              </DrawerSection>
            ) : null}

            <DrawerSection title="Recorded timeline" aside={openDispatchDetail.timeline.length ? `${openDispatchDetail.timeline.length} events` : "No events recorded"}>
              {openDispatchDetail.timeline.length ? (
                <div className="grid gap-0">
                  {openDispatchDetail.timeline.map((event) => (
                    <div key={event.id} className="grid gap-0.5 border-b border-hairline py-2 last:border-b-0">
                      <div className="flex items-baseline justify-between gap-3">
                        <span className="text-[length:var(--type-body-sm)] font-medium text-foreground">{timelineEventLabel(event.eventType)}</span>
                        <span className="shrink-0 text-[length:var(--type-caption)] text-muted-foreground">{formatAgo(event.timestamp)}</span>
                      </div>
                      {timelineEventDetail(event) ? <span className="text-[length:var(--type-caption)] text-muted-foreground">{timelineEventDetail(event)}</span> : null}
                    </div>
                  ))}
                </div>
              ) : (
                <p className="text-[length:var(--type-caption)] text-muted-foreground">The client has not reported any additional transfer events yet.</p>
              )}
            </DrawerSection>
          </>
        ) : (
          <ListEmpty
            title={busyKey?.startsWith("dispatch:") ? "Loading transfer history" : "Transfer details unavailable"}
            description="The transfer row is still visible, but its full server-side timeline could not be loaded. Refresh and try again."
          />
        )}
      </Drawer>

      {/* ------------------------------------------------- activity drawer */}
      <Drawer
        open={drawer?.kind === "activity"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openActivity?.name ?? "Activity"}
        description={openActivity ? `${openActivity.sub} · ${openActivity.status}` : undefined}
        footer={
          openActivity?.kind === "processor" ? (
            <DrawerFooter
              state="clean"
              saveType="button"
              saveLabel="Try again"
              saveEnabled={busyKey === null}
              onSave={() => {
                const handoff = processorHandoffs.find((entry) => `processor:${entry.id}` === openActivity?.id);
                if (handoff) void retryHandoff(handoff);
              }}
              onCancel={() => setDrawer(null)}
            />
          ) : (
            <DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setDrawer(null)} />
          )
        }
      >
        {openActivity ? (
          <DrawerSection title="What happened">
            <div className="grid gap-1.5">
              <Fact label="Outcome" value={openActivity.detail} />
              {openActivity.extra ? <Fact label="Detail" value={openActivity.extra} mono /> : null}
              <Fact label="When" value={formatDateTime(openActivity.whenUtc)} />
              <Fact label="Kind" value={activityKindLabel(openActivity.kind)} />
            </div>
          </DrawerSection>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={pendingRemoval !== null}
        onOpenChange={(open) => {
          if (!open && busyKey === null) setPendingRemoval(null);
        }}
        title="Remove this from the client's queue?"
        description={
          pendingRemoval
            ? `Deluno will ask ${pendingRemoval.clientName} to drop “${pendingRemoval.title}”. The client decides what happens to the files it downloaded; Deluno never removes anything from your media library through this.`
            : ""
        }
        confirmLabel="Remove from client"
        busy={pendingRemoval !== null && busyKey === `queue:${pendingRemoval.clientId}:${pendingRemoval.id}:delete`}
        onConfirm={async () => {
          if (!pendingRemoval) return;
          await queueAction(pendingRemoval, "delete");
          setPendingRemoval(null);
          setDrawer(null);
        }}
      />
    </div>
  );
}

/* ---------------------------------------------------------------- bits */

function ProgressBar({ value }: { value: number }) {
  const clamped = Math.min(100, Math.max(0, value));
  return (
    <span aria-hidden className="block h-1.5 w-full overflow-hidden rounded-full bg-surface-3">
      <span className="block h-full rounded-full bg-primary transition-[width] duration-300" style={{ width: `${clamped}%` }} />
    </span>
  );
}

function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
      <span className="shrink-0 text-[length:var(--type-caption)] text-muted-foreground">{label}</span>
      <span className={`min-w-0 truncate text-right text-[length:var(--type-caption)] text-foreground ${mono ? "font-mono" : ""}`} title={value}>
        {value}
      </span>
    </div>
  );
}

function ImportPreviewFacts({ preview }: { preview: ImportPreviewResponse }) {
  return (
    <div className="grid gap-1.5">
      <Fact label="Would land at" value={preview.destinationPath || "Not resolved"} mono />
      <Fact label="In folder" value={preview.destinationFolder || "Not resolved"} mono />
      <Fact label="Matched rule" value={preview.matchedRuleName ?? "No destination rule matched"} />
      <Fact label="How" value={preview.transferExplanation || preview.preferredTransferMode} />
      <Fact label="Source" value={preview.sourceExists ? formatBytes(preview.sourceSizeBytes) : "Not found on disk"} />
      {preview.destinationExists ? <Fact label="Careful" value="Something already exists at the destination" /> : null}
      {preview.warnings.length ? <Fact label="Warnings" value={preview.warnings.join(" · ")} /> : null}
    </div>
  );
}

function queueChip(item: DownloadQueueItem): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (item.errorMessage || item.status === downloadQueueStatuses.stalled) return { tone: statusTone("transfer.stalled"), label: queueStatusLabel(item.status) };
  if (item.healthFindings?.length) return statusPresentation("transfer.needsALook");
  // Was green here and grey in the pipeline strip. Green is the colour for
  // done, and a release waiting to be imported is mid-pipeline.
  if (isImportReadyStatus(item.status)) return statusPresentation("transfer.importReady");
  if (isProcessingStatus(item.status)) return { tone: "info", label: queueStatusLabel(item.status) };
  return { tone: "info", label: queueStatusLabel(item.status) };
}

function importJobStage(job: JobQueueItem) {
  if (isJobDeadLettered(job.status as JobStatus)) return "Import gave up";
  if (isJobFailed(job.status as JobStatus)) return "Import failed";
  if (job.status === JOB_STATUS.COMPLETED) return "Imported and named";
  if (job.status === JOB_STATUS.RUNNING) return "Writing to library";
  return "Waiting to import";
}

function importJobTone(job: JobQueueItem): NonNullable<ChipProps["tone"]> {
  if (isJobFailed(job.status as JobStatus)) return "bad";
  if (isJobActive(job.status as never)) return "info";
  return "ok";
}

function pipelineStage(item: DownloadQueueItem) {
  if (item.status === downloadQueueStatuses.waitingForProcessor) return "Processing connector";
  if (item.status === downloadQueueStatuses.processing || item.status === downloadQueueStatuses.processed) return "Checking media";
  if (item.status === downloadQueueStatuses.importQueued) return "Importing & naming";
  if (item.status === downloadQueueStatuses.imported) return "In library";
  if (item.status === downloadQueueStatuses.importFailed || item.status === downloadQueueStatuses.processingFailed) return "Needs attention";
  if (isImportReadyStatus(item.status)) return "Ready for import";
  return item.status === downloadQueueStatuses.queued ? "Waiting for client" : "Downloading";
}

function pipelineDetail(item: DownloadQueueItem) {
  if (item.errorMessage) return item.errorMessage;
  if (item.status === downloadQueueStatuses.waitingForProcessor) return "Waiting for the cleaned output before import.";
  if (item.status === downloadQueueStatuses.processing || item.status === downloadQueueStatuses.processed) return "Media quality and processor output are being checked.";
  if (item.status === downloadQueueStatuses.importQueued) return "The destination and final name are being applied.";
  if (item.status === downloadQueueStatuses.imported) return "The file has been placed in the library.";
  if (isImportReadyStatus(item.status)) return "Preview the destination and final name before importing.";
  return item.sourcePath ? `Source: ${item.sourcePath}` : "External client owns the download.";
}

function attentionReason(item: DownloadQueueItem) {
  const blocked = item.healthFindings?.find((finding) => finding.candidateBlocked);
  if (blocked) return "Release blocked after repeated health failures";
  return item.errorMessage ?? item.healthFindings?.[0]?.summary ?? "This download has stalled.";
}

function attentionEvidence(item: DownloadQueueItem) {
  const blocked = item.healthFindings?.find((finding) => finding.candidateBlocked);
  if (blocked) return `${blocked.strikeCount} strikes · ${blocked.evidence}`;
  return item.healthFindings?.length ? `${item.healthFindings.length} health ${item.healthFindings.length === 1 ? "finding" : "findings"}` : undefined;
}

function isProcessorTerminal(status: string) {
  return status.toLowerCase() === "completed";
}

function isProcessorFailure(status: string) {
  return ["failed", "timed-out", "timeout"].includes(status.toLowerCase());
}

function processorStageLabel(status: string) {
  switch (status.toLowerCase()) {
    case "waiting":
      return "Waiting to submit";
    case "submitted":
      return "Submitted to connector";
    case "accepted":
      return "Accepted by connector";
    case "started":
      return "Processing media";
    case "completed":
      return "Output received";
    case "timed-out":
    case "timeout":
      return "Timed out";
    case "failed":
      return "Processing failed";
    default:
      return status || "Waiting";
  }

}

function processorStatusLabel(status: string) {
  return processorStageLabel(status);
}

function processorTone(handoff: ProcessorHandoffItem): NonNullable<ChipProps["tone"]> {
  if (isProcessorFailure(handoff.status) || handoff.failureMessage) return "bad";
  if (isProcessorTerminal(handoff.status)) return "ok";
  return "info";
}

function processorConnectionStatus(connection: ProcessorConnectionItem) {
  if (!connection.isEnabled) return "disabled";
  return connection.healthStatus || "unknown";
}

function processorConnectionTone(connection: ProcessorConnectionItem): NonNullable<ChipProps["tone"]> {
  if (!connection.isEnabled || connection.healthStatus === "unreachable") return "bad";
  if (connection.healthStatus === "degraded") return "warn";
  if (connection.healthStatus === "healthy") return "ok";
  return "idle";
}

function isDispatchFailure(dispatch: DownloadDispatchItem) {
  return [dispatch.status, dispatch.grabStatus, dispatch.importStatus, dispatch.grabFailureCode, dispatch.importFailureCode]
    .filter((value): value is string => Boolean(value))
    .some((value) => ["failed", "blocked", "rejected", "unresolved", "error"].includes(value.toLowerCase()));
}

function dispatchStageLabel(dispatch: DownloadDispatchItem) {
  if (isDispatchFailure(dispatch)) return "Needs attention";
  if (dispatch.importStatus === "completed" || dispatch.importCompletedUtc) return "Imported";
  if (dispatch.importStatus) return `Import ${dispatch.importStatus}`;
  if (dispatch.detectedUtc || dispatch.grabStatus === "detected") return "Detected by client";
  if (dispatch.grabStatus) return `Grab ${dispatch.grabStatus}`;
  return dispatch.status || "Sent to client";
}

function dispatchActivityDetail(dispatch: DownloadDispatchItem) {
  if (dispatch.importedFilePath) return `Imported as ${fileNameFromPath(dispatch.importedFilePath)}`;
  if (dispatch.importFailureMessage) return `Import could not complete: ${dispatch.importFailureMessage}`;
  if (dispatch.grabMessage) return dispatch.grabMessage;
  return `${dispatch.indexerName || "Unknown source"} → ${dispatch.downloadClientName || "Unassigned client"}`;
}

function dispatchFailureDetail(dispatch: DownloadDispatchItem) {
  if (dispatch.importFailureMessage) return dispatch.importFailureMessage;
  if (dispatch.grabMessage) return dispatch.grabMessage;
  if (dispatch.grabFailureCode) return `Grab failed with ${dispatch.grabFailureCode}.`;
  if (!dispatch.notesJson) return "Review the transfer timeline for the recorded reason.";
  try {
    const parsed = JSON.parse(dispatch.notesJson) as { message?: string; reason?: string; error?: string; failureMessage?: string };
    return parsed.failureMessage ?? parsed.reason ?? parsed.message ?? parsed.error ?? dispatch.notesJson;
  } catch {
    return dispatch.notesJson;
  }
}

function dispatchOutcome(status: string | null | undefined, message: string | null | undefined) {
  if (message) return message;
  if (status) return status;
  return "Not reported yet";
}

function fileNameFromPath(path: string) {
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path;
}

function timelineEventLabel(eventType: string) {
  return eventType
    .replace(/[_-]+/g, " ")
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function timelineEventDetail(event: { detailsJson: string | null }) {
  if (!event.detailsJson) return null;
  try {
    const parsed = JSON.parse(event.detailsJson) as Record<string, unknown>;
    const values = Object.entries(parsed)
      .filter(([, value]) => typeof value === "string" || typeof value === "number" || typeof value === "boolean")
      .map(([key, value]) => `${timelineEventLabel(key)}: ${String(value)}`);
    return values.length ? values.join(" · ") : null;
  } catch {
    return event.detailsJson;
  }
}

function cleanupPolicyLabel(settings: PlatformSettingsSnapshot) {
  if (settings.cleanupRemoveClientEntryAfterThreshold || settings.cleanupPurgePayloadAfterThreshold) return "automatic removal enabled";
  if (settings.cleanupBlockReleaseAfterThreshold || settings.cleanupQueueReplacementAfterThreshold) return "automatic safeguards enabled";
  return "manual review before removal";
}

function PolicyFact({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <div className="rounded-lg border border-hairline bg-surface-1 px-3 py-2">
      <span className="block text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">{label}</span>
      <span className={enabled ? "text-success" : "text-muted-foreground"}>{enabled ? "Enabled" : "Off"}</span>
    </div>
  );
}

/** Every finished thing, in one row shape, newest first. */
function buildActivity({
  importJobs,
  processorHandoffs,
  dispatches,
  telemetry,
  healthRecords,
  activityEvents
}: {
  importJobs: JobQueueItem[];
  processorHandoffs: ProcessorHandoffItem[];
  dispatches: DownloadDispatchItem[];
  telemetry: DownloadTelemetryOverview;
  healthRecords: DownloadHealthRecord[];
  activityEvents: ActivityEventItem[];
}): ActivityEntry[] {
  const entries: ActivityEntry[] = [];

  for (const job of importJobs) {
    const info = parseImportJobPayload(job.payloadJson);
    const importedName = info?.fileName ? ` as ${info.fileName}` : "";
    entries.push({
      id: `import:${job.id}`,
      kind: "import",
      name: info?.title || jobTitle(job),
      sub: "Import",
      detail: isJobFailed(job.status as JobStatus)
        ? job.lastError ?? "The import failed."
        : job.status === JOB_STATUS.COMPLETED
          ? `Imported${importedName}`
          : job.status === JOB_STATUS.RUNNING
            ? `Importing${importedName}`
            : `Import ${job.status}`,
      extra: info?.destinationPath ?? (job.attempts > 1 ? `Attempt ${job.attempts}` : undefined),
      tone: isJobFailed(job.status as JobStatus) ? "bad" : isJobActive(job.status as never) ? "info" : "ok",
      status: job.status,
      whenUtc: job.completedUtc ?? job.startedUtc ?? job.createdUtc
    });
  }

  for (const handoff of processorHandoffs) {
    entries.push({
      id: `processor:${handoff.id}`,
      kind: "processor",
      name: handoff.releaseName,
      sub: handoff.processorName ?? "Processor",
      detail: handoff.failureMessage ?? (handoff.outputPath ? "Processed output confirmed" : `Hand-off ${handoff.status}`),
      extra: handoff.outputPath ?? handoff.sourcePath,
      tone: handoff.failureMessage ? "bad" : handoff.outputPath ? "ok" : "info",
      status: handoff.status,
      whenUtc: handoff.updatedUtc
    });
  }

  for (const dispatch of dispatches) {
    entries.push({
      id: `sent:${dispatch.id}`,
      kind: "sent",
      name: dispatch.releaseName,
      sub: dispatch.mediaType === "tv" ? "TV" : "Movies",
      detail: dispatchActivityDetail(dispatch),
      extra: dispatch.importedFilePath ?? (isDispatchFailure(dispatch) ? dispatchFailureDetail(dispatch) : undefined),
      tone: isDispatchFailure(dispatch) ? "bad" : dispatch.importedFilePath ? "ok" : "idle",
      status: dispatchStageLabel(dispatch),
      whenUtc: dispatch.importCompletedUtc ?? dispatch.detectedUtc ?? dispatch.grabAttemptedUtc ?? dispatch.createdUtc
    });
  }

  for (const client of telemetry.clients) {
    for (const item of client.history) {
      entries.push({
        id: `client:${client.clientId}:${item.id}`,
        kind: "client",
        name: item.title || item.releaseName,
        sub: client.clientName,
        detail: item.errorMessage ?? `Client reported ${item.outcome}`,
        extra: item.sizeBytes ? formatBytes(item.sizeBytes) : undefined,
        tone: item.errorMessage ? "bad" : "ok",
        status: item.outcome,
        whenUtc: item.completedUtc
      });
    }
  }

  for (const record of healthRecords) {
    entries.push({
      id: `health:${record.clientId}:${record.queueItemId}:${record.kind}`,
      kind: "health",
      name: record.releaseName,
      sub: "Health check",
      detail: record.evidence,
      extra: record.strikeCount > 1 ? `${record.strikeCount} strikes` : undefined,
      tone: record.severity === "critical" ? "bad" : "warn",
      status: record.kind,
      whenUtc: record.lastObservedUtc
    });
  }

  for (const event of activityEvents) {
    entries.push({
      id: `event:${event.id}`,
      kind: "health",
      name: activityEventTitle(event),
      sub: "Issue / cleanup",
      detail: event.message,
      extra: activityEventDetail(event),
      tone: event.severity === "error" ? "bad" : event.severity === "warning" ? "warn" : "info",
      status: activityEventLabel(event.category),
      whenUtc: event.createdUtc
    });
  }

  return entries.sort((a, b) => new Date(b.whenUtc).getTime() - new Date(a.whenUtc).getTime());
}

function activityKindLabel(kind: ActivityEntry["kind"]) {
  switch (kind) {
    case "import":
      return "Import job";
    case "processor":
      return "Processor hand-off";
    case "sent":
      return "Sent to a client";
    case "client":
      return "Reported by a client";
    default:
      return "Health finding";
  }
}

function jobTitle(job: JobQueueItem) {
  const parsed = parseImportJobPayload(job.payloadJson);
  return parsed?.title || parsed?.fileName || `Job ${job.id.slice(0, 8)}`;
}

interface ImportJobInfo {
  title: string | null;
  fileName: string | null;
  sourcePath: string | null;
  destinationPath: string | null;
}

function parseImportJobPayload(payloadJson: string | null): ImportJobInfo | null {
  if (!payloadJson) return null;
  try {
    const parsed = JSON.parse(payloadJson) as {
      preview?: { title?: string; fileName?: string; sourcePath?: string; destinationPath?: string };
      title?: string;
      fileName?: string;
      sourcePath?: string;
      destinationPath?: string;
    };
    const preview = parsed.preview;
    return {
      title: preview?.title ?? parsed.title ?? null,
      fileName: preview?.fileName ?? parsed.fileName ?? null,
      sourcePath: preview?.sourcePath ?? parsed.sourcePath ?? null,
      destinationPath: preview?.destinationPath ?? parsed.destinationPath ?? null
    };
  } catch {
    return null;
  }
}

function activityEventTitle(event: ActivityEventItem) {
  const details = parseActivityDetails(event.detailsJson);
  return typeof details?.releaseName === "string" ? details.releaseName : event.message;
}

function activityEventDetail(event: ActivityEventItem) {
  const details = parseActivityDetails(event.detailsJson);
  const actions = Array.isArray(details?.actions) ? details.actions.filter((item): item is string => typeof item === "string") : [];
  if (actions.length) return actions.join(" · ");
  if (typeof details?.reason === "string") return details.reason;
  return event.detail ?? undefined;
}

function activityEventLabel(category: string) {
  return category
    .replace(/^download\./, "")
    .replace(/[._-]+/g, " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());
}

function parseActivityDetails(detailsJson: string | null): Record<string, unknown> | null {
  if (!detailsJson) return null;
  try {
    const parsed: unknown = JSON.parse(detailsJson);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

function buildImportRequest(item: DownloadQueueItem, libraries: LibraryItem[]): ImportPreviewRequest {
  return {
    sourcePath: resolveImportSourcePath(item, libraries) || "",
    fileName: inferImportFileName(item),
    mediaType: item.mediaType === "tv" ? "tv" : "movies",
    title: item.title || null,
    year: inferYear(item.releaseName),
    genres: null,
    tags: null,
    studio: null,
    originalLanguage: null
  };
}

function inferImportFileName(item: DownloadQueueItem) {
  if (!item.sourcePath) return null;
  const parts = item.sourcePath.split(/[\\/]/).filter(Boolean);
  const last = parts[parts.length - 1];
  return last && last.includes(".") ? last : null;
}

function inferYear(value: string) {
  const match = value.match(/\b(19|20)\d{2}\b/);
  return match ? Number(match[0]) : null;
}

function splitCsv(value: string) {
  const parts = value.split(",").map((item) => item.trim()).filter(Boolean);
  return parts.length ? parts : null;
}

function actionLabel(action: QueueAction) {
  switch (action) {
    case "pause":
      return "Pause";
    case "resume":
      return "Resume";
    case "recheck":
      return "Re-check";
    default:
      return "Remove";
  }
}

function isHealthyClient(status: string) {
  return status === "healthy" || status === "ok";
}

function formatEta(seconds: number) {
  if (seconds <= 0) return "—";
  if (seconds < 60) return `${Math.round(seconds)}s left`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m left`;
  return `${(seconds / 3600).toFixed(1)}h left`;
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value));
}

function formatAgo(value: string) {
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 60 * 48) return `${Math.round(minutes / 60)} h ago`;
  return `${Math.round(minutes / 1440)} d ago`;
}
