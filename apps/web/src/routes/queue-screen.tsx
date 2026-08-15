import { useMemo, useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import {
  AlertTriangle,
  ArrowDownToLine,
  CheckCircle2,
  Download,
  FileSearch,
  GitBranch,
  HardDriveDownload,
  Loader2,
  Pause,
  Play,
  RefreshCw,
  RotateCw,
  Trash2,
  Wand2
} from "lucide-react";
import {
  ApiRequestError,
  fetchJson,
  type DownloadClientHistoryItem,
  type DownloadCleanupPreview,
  type DownloadHealthRecord,
  type DownloadClientTelemetrySnapshot,
  type DownloadDispatchItem,
  type DownloadQueueItem,
  type DownloadTelemetryOverview,
  type ImportExecuteResponse,
  type ImportExecuteRequest,
  type ImportJobResponse,
  type ImportPreviewRequest,
  type ImportPreviewResponse,
  type JobQueueItem,
  type MovieImportRecoveryCase,
  type MovieImportRecoverySummary,
  type PlatformSettingsSnapshot,
  type ProcessorHandoffItem,
  type SeriesImportRecoveryCase,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { JOB_STATUS, isJobActive } from "../lib/job-status-constants";
import { downloadQueueStatuses, isImportReadyStatus, isProcessingStatus, queueStatusLabel, telemetryCapabilityChips } from "../lib/download-telemetry";
import { cn } from "../lib/utils";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { PathInput } from "../components/ui/path-input";
import { OperationPathBanner } from "../components/app/operations-guide";
import { EmptyState } from "../components/shell/empty-state";
import { GlassTile, PageHero } from "../components/shell/page-hero";
import { Stagger, StaggerItem } from "../components/shell/motion";
import { RouteSkeleton } from "../components/shell/skeleton";
import { toast } from "../components/shell/toaster";
import { ConfirmDialog } from "../components/ui/confirm-dialog";

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
  settings: PlatformSettingsSnapshot;
  jobs: JobQueueItem[];
  healthRecords: DownloadHealthRecord[];
  processorHandoffs: ProcessorHandoffItem[];
}

export async function queueLoader(): Promise<QueueLoaderData> {
  const [telemetry, dispatches, movieRecovery, seriesRecovery, settings, jobs, healthRecords, processorHandoffs] = await Promise.all([
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry"),
    fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?take=60"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<JobQueueItem[]>("/api/jobs?take=80"),
    fetchJson<DownloadHealthRecord[]>("/api/download-health?take=30"),
    fetchJson<ProcessorHandoffItem[]>("/api/integrations/processors/handoffs?take=30").catch((error) => {
      // Permit a newly deployed web build to remain usable during a rolling upgrade
      // while an older local API is still running. Other failures remain visible.
      if (error instanceof ApiRequestError && error.status === 404) return [];
      throw error;
    })
  ]);

  return { telemetry, dispatches, movieRecovery, seriesRecovery, settings, jobs, healthRecords, processorHandoffs };
}

export function QueuePage() {
  const loaderData = useLoaderData() as QueueLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const revalidator = useRevalidator();
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [importPreviews, setImportPreviews] = useState<Record<string, ImportPreviewResponse>>({});
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
  const [pendingRemoval, setPendingRemoval] = useState<DownloadQueueItem | null>(null);

  const telemetry = loaderData.telemetry;
  const dispatches = loaderData.dispatches;
  const movieRecovery = loaderData.movieRecovery;
  const seriesRecovery = loaderData.seriesRecovery;
  const settings = loaderData.settings;
  const jobs = loaderData.jobs;
  const healthRecords = loaderData.healthRecords;
  const processorHandoffs = loaderData.processorHandoffs;
  const [cleanupPreviews, setCleanupPreviews] = useState<Record<string, DownloadCleanupPreview>>({});

  const allQueue = useMemo(
    () => telemetry.clients.flatMap((client) => client.queue.map((item) => ({ ...item, clientProtocol: client.protocol }))),
    [telemetry.clients]
  );
  const clientHistory = useMemo(
    () =>
      telemetry.clients
        .flatMap((client) => client.history.map((item) => ({ ...item, clientHealth: client.healthStatus })))
        .sort((a, b) => new Date(b.completedUtc).getTime() - new Date(a.completedUtc).getTime()),
    [telemetry.clients]
  );
  const importJobs = useMemo(() => jobs.filter((job) => job.jobType === "filesystem.import.execute"), [jobs]);
  const importReady = allQueue.filter((item) => isImportReadyStatus(item.status));
  const processing = allQueue.filter((item) => isProcessingStatus(item.status));
  const queueAttention = allQueue.filter((item) =>
    item.status === downloadQueueStatuses.stalled || Boolean(item.errorMessage) || Boolean(item.healthFindings?.length)
  );
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;
  const activeImportJobs = importJobs.filter((job) => isJobActive(job.status as any)).length;
  const failedImportJobs = importJobs.filter((job) => job.status === JOB_STATUS.FAILED).length;
  const activeClients = telemetry.clients.filter((client) => isHealthyClient(client.healthStatus)).length;
  const activeProcessorHandoffs = processorHandoffs.filter((handoff) => ["waiting", "accepted", "started"].includes(handoff.status));

  async function handleQueueAction(clientId: string, item: DownloadQueueItem, action: QueueAction) {
    const key = `queue:${clientId}:${item.id}:${action}`;
    setBusyKey(key);
    try {
      const res = await authedFetch(`/api/download-clients/${clientId}/queue/actions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action, queueItemId: item.id })
      });
      if (!res.ok) {
        const message = await res.text().catch(() => "");
        throw new Error(message || "Download action failed.");
      }
      toast.success(`${actionLabel(action)} sent to ${item.clientName}`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Download action failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function confirmRemoval() {
    if (!pendingRemoval) return;
    const item = pendingRemoval;
    await handleQueueAction(item.clientId, item, "delete");
    setPendingRemoval(null);
  }

  async function ignoreHealthFinding(item: DownloadQueueItem, kind: string) {
    const key = `health-ignore:${item.clientId}:${item.id}:${kind}`;
    setBusyKey(key);
    try {
      const response = await authedFetch(`/api/download-clients/${item.clientId}/queue/${item.id}/health/${encodeURIComponent(kind)}/ignore`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ durationDays: 7 })
      });
      if (!response.ok) throw new Error("Could not pause this health finding.");
      toast.success("Health finding ignored for seven days. No data was removed.");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not pause this health finding.");
    } finally {
      setBusyKey(null);
    }
  }

  async function previewCleanup(item: DownloadQueueItem) {
    const key = `cleanup-preview:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const response = await authedFetch(`/api/download-clients/${item.clientId}/queue/${item.id}/cleanup-preview`);
      if (!response.ok) throw new Error("Could not prepare a cleanup preview for this queue item.");
      const preview = (await response.json()) as DownloadCleanupPreview;
      setCleanupPreviews((current) => ({ ...current, [`${item.clientId}:${item.id}`]: preview }));
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not prepare a cleanup preview.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handlePreviewImport(item: DownloadQueueItem) {
    const key = `import-preview:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const request = buildImportRequest(item, settings?.downloadsPath ?? null);
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
      setImportPreviews((current) => ({ ...current, [item.id]: preview }));
      toast.success("Import destination resolved");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Import preview failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleImportNow(item: DownloadQueueItem) {
    const key = `import-now:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const request = buildImportRequest(item, settings?.downloadsPath ?? null);
      const result = await fetchJson<ImportExecuteResponse>("/api/filesystem/import/execute", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          preview: request,
          transferMode: "auto",
          overwrite: false,
          allowCopyFallback: true,
          forceReplacement: false
        })
      });
      setImportPreviews((current) => ({ ...current, [item.id]: result.preview }));
      toast.success(result.message);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Import failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleQueueImport(item: DownloadQueueItem) {
    const key = `import-queue:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const request = buildImportRequest(item, settings?.downloadsPath ?? null);
      const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          preview: request,
          transferMode: "auto",
          overwrite: false,
          allowCopyFallback: true,
          forceReplacement: false
        })
      });
      setImportPreviews((current) => ({ ...current, [item.id]: result.preview }));
      toast.success(`Import queued as job ${result.jobId.slice(0, 8)}`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Import job could not be queued.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDismissRecovery(mediaType: "movie" | "series", id: string) {
    const key = `recovery:${mediaType}:${id}`;
    setBusyKey(key);
    try {
      const path = mediaType === "movie" ? `/api/movies/import-recovery/${id}` : `/api/series/import-recovery/${id}`;
      const res = await authedFetch(path, { method: "DELETE" });
      if (!res.ok) throw new Error("Recovery case could not be dismissed.");
      toast.success("Recovery case dismissed");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Recovery action failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleRetryRecovery(mediaType: "movie" | "series", item: MovieImportRecoveryCase | SeriesImportRecoveryCase) {
    const key = `recovery-retry:${mediaType}:${item.id}`;
    setBusyKey(key);
    try {
      const retryRequest = parseRecoveryRetryRequest(item.detailsJson);
      if (!retryRequest) {
        throw new Error("This recovery case was created before retry details were stored. Queue a fresh import from the download row.");
      }

      const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(retryRequest)
      });
      toast.success(`Recovery retry queued as job ${result.jobId.slice(0, 8)}`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Recovery retry could not be queued.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleRetryFailedJobs() {
    setBusyKey("jobs:retry-failed");
    try {
      const res = await authedFetch("/api/jobs/retry-failed", { method: "POST" });
      if (!res.ok) {
        throw new Error("Failed jobs could not be requeued.");
      }
      const result = (await res.json().catch(() => ({ retried: 0 }))) as { retried: number };
      toast.success(`${result.retried} failed job${result.retried === 1 ? "" : "s"} requeued`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Failed jobs could not be requeued.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleRetryProcessorHandoff(handoff: ProcessorHandoffItem) {
    const key = `processor-handoff-retry:${handoff.id}`;
    setBusyKey(key);
    try {
      const res = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/retry`, { method: "POST" });
      if (!res.ok) {
        const body = await res.json().catch(() => null) as { message?: string } | null;
        throw new Error(body?.message || "The processor hand-off could not be tried again.");
      }
      toast.success("Processor hand-off queued to try again. Deluno will use the same hand-off ID and still wait for a confirmed output.");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "The processor hand-off could not be tried again.");
    } finally {
      setBusyKey(null);
    }
  }

  function buildManualImportRequest(): ImportPreviewRequest {
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

  async function handleManualPreview() {
    if (!manualImport.sourcePath.trim()) {
      toast.info("Choose a source file or folder first.");
      return;
    }

    setBusyKey("manual-import:preview");
    try {
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildManualImportRequest())
      });
      setManualPreview(preview);
      toast.success("Manual import preview generated");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Manual import preview failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleManualQueue() {
    if (!manualImport.sourcePath.trim()) {
      toast.info("Choose a source file or folder first.");
      return;
    }

    setBusyKey("manual-import:queue");
    try {
      const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          preview: buildManualImportRequest(),
          transferMode: manualImport.transferMode,
          overwrite: false,
          allowCopyFallback: true,
          forceReplacement: false
        })
      });
      setManualPreview(result.preview);
      toast.success(`Manual import queued as job ${result.jobId.slice(0, 8)}`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Manual import could not be queued.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <PageHero
        eyebrow="Downloads & imports"
        eyebrowIcon={<HardDriveDownload className="h-3 w-3 text-primary" />}
        title={
          <>
            Your downloads, ready for your library.
          </>
        }
        subtitle={
          <>
            This is the hand-off view: external client download, optional processing, then safe import. Step in only when a transfer needs attention.
          </>
        }
        stats={[
          { label: "Active", value: telemetry.summary.activeCount.toString(), tone: "primary" },
          { label: "Processing", value: (telemetry.summary.processingCount ?? processing.length).toString(), tone: processing.length ? "primary" : "neutral" },
          { label: "Import ready", value: telemetry.summary.importReadyCount.toString(), tone: "success" },
          { label: "Import jobs", value: activeImportJobs.toString(), tone: activeImportJobs ? "primary" : "neutral" },
          { label: "External processing", value: activeProcessorHandoffs.length.toString(), tone: activeProcessorHandoffs.length ? "primary" : "neutral" },
          { label: "Recovery", value: openRecovery.toString(), tone: openRecovery ? "danger" : "neutral" }
        ]}
        actions={
          <>
            <Button size="lg" className="gap-2" onClick={() => revalidator.revalidate()}>
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
            <Button asChild size="lg" variant="secondary" className="gap-2">
              <Link to="/indexers">
                <Wand2 className="h-4 w-4" />
                Configure downloads
              </Link>
            </Button>
          </>
        }
      />

      <OperationPathBanner
        pathId="queue"
        actionTo="/indexers"
        actionLabel="Configure downloads"
      />

      <Stagger className="fluid-kpi-grid">
        <StaggerItem>
          <MetricTile icon={Download} label="Download apps" value={`${activeClients}/${telemetry.clients.length}`} sub="connected and ready" tone="primary" />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={ArrowDownToLine} label="Total speed" value={`${telemetry.summary.totalSpeedMbps.toFixed(1)}`} unit="MB/s" sub="combined speed" tone="success" />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={FileSearch} label="Import ready" value={importReady.length} sub="safe to preview" tone="success" />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={Wand2} label="Processing" value={processing.length} sub="being checked" tone={processing.length ? "primary" : "neutral"} />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={GitBranch} label="Import jobs" value={activeImportJobs} sub="queued or running" tone={activeImportJobs ? "primary" : "neutral"} />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={Wand2} label="External processing" value={activeProcessorHandoffs.length} sub="waiting on your processor" tone={activeProcessorHandoffs.length ? "primary" : "neutral"} />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={AlertTriangle} label="Needs action" value={openRecovery + queueAttention.length} sub="download health or recovery" tone={openRecovery + queueAttention.length ? "warn" : "neutral"} />
        </StaggerItem>
      </Stagger>

      <GlassTile className="p-[var(--tile-pad)]">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">Next up</p>
            <h2 className="mt-1 text-[17px] font-semibold text-foreground">What to do next</h2>
          </div>
          {openRecovery + queueAttention.length + failedImportJobs > 0 ? (
            <Badge variant="warning">{openRecovery + queueAttention.length + failedImportJobs} need attention</Badge>
          ) : importReady.length > 0 ? (
            <Badge variant="success">{importReady.length} ready to review</Badge>
          ) : (
            <Badge variant="success">All clear</Badge>
          )}
        </div>
        <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
          {queueAttention.length > 0 ? (
            <NextStep href="#download-queue" icon={AlertTriangle} tone="warning" title="Review download health" description={`${queueAttention.length} download${queueAttention.length === 1 ? " needs" : "s need"} a safe next action.`} />
          ) : null}
          {failedImportJobs > 0 ? (
            <NextStep href="#import-jobs" icon={RotateCw} tone="warning" title="Retry failed imports" description={`${failedImportJobs} import job${failedImportJobs === 1 ? " is" : "s are"} ready to retry.`} />
          ) : null}
          {openRecovery > 0 ? (
            <NextStep href="#recovery" icon={FileSearch} tone="warning" title="Resolve import issues" description={`${openRecovery} file handoff${openRecovery === 1 ? " needs" : "s need"} your decision.`} />
          ) : null}
          {importReady.length > 0 ? (
            <NextStep href="#download-queue" icon={CheckCircle2} tone="success" title="Add completed downloads" description={`${importReady.length} completed download${importReady.length === 1 ? " is" : "s are"} ready to review.`} />
          ) : null}
          {openRecovery + queueAttention.length + failedImportJobs + importReady.length === 0 ? (
            <NextStep icon={CheckCircle2} tone="success" title="Nothing for you to do" description="Deluno is tracking the active downloads and imports." />
          ) : null}
        </div>
      </GlassTile>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.45fr)_minmax(360px,0.8fr)]">
        <div className="space-y-[var(--page-gap)]">
          <GlassTile>
            <PanelHeader
              title="Download progress"
              subtitle="Everything currently being downloaded or prepared for import."
              meta={`${allQueue.length} queue items`}
            />
            {allQueue.length ? (
              <div className="divide-y divide-hairline">
                {allQueue.map((item) => (
                  <QueueRow
                    key={`${item.clientId}:${item.id}`}
                    item={item}
                    busyKey={busyKey}
                    preview={importPreviews[item.id]}
                    cleanupPreview={cleanupPreviews[`${item.clientId}:${item.id}`]}
                    allowExternalClientRemoval={settings.removeCompletedDownloads}
                    onAction={handleQueueAction}
                    onIgnoreHealthFinding={ignoreHealthFinding}
                    onPreviewCleanup={previewCleanup}
                    onRequestRemove={setPendingRemoval}
                    onPreview={handlePreviewImport}
                    onImport={handleImportNow}
                    onQueueImport={handleQueueImport}
                  />
                ))}
              </div>
            ) : (
              <EmptyState
                size="sm"
                variant="custom"
                title="No active queue items"
                description="Downloads dispatched from search will appear here with progress, speed, ETA, and import status."
              />
            )}
          </GlassTile>

          <GlassTile id="download-health-history">
            <PanelHeader
              title="Download health history"
              subtitle="Recent evidence is retained after an item leaves the live queue. Persisted import paths are redacted."
              meta={`${healthRecords.length} recent`}
            />
            {healthRecords.length ? (
              <div className="divide-y divide-hairline">
                {healthRecords.slice(0, 12).map((record) => <DownloadHealthHistoryRow key={`${record.clientId}:${record.queueItemId}:${record.kind}`} record={record} />)}
              </div>
            ) : (
              <EmptyState size="sm" variant="custom" title="No health history yet" description="Deluno will retain explainable health evidence when a download needs attention." />
            )}
          </GlassTile>

          <GlassTile id="external-processing">
            <PanelHeader
              title="External processing handoffs"
              subtitle="Items Deluno has handed to FileFlows, Weir, or another configured processor before safe import."
              meta={activeProcessorHandoffs.length ? `${activeProcessorHandoffs.length} active` : `${processorHandoffs.length} recent`}
            />
            {processorHandoffs.length ? (
              <div className="divide-y divide-hairline">
                {processorHandoffs.slice(0, 12).map((handoff) => (
                  <ProcessorHandoffRow
                    key={handoff.id}
                    handoff={handoff}
                    isRetrying={busyKey === `processor-handoff-retry:${handoff.id}`}
                    onRetry={() => void handleRetryProcessorHandoff(handoff)}
                  />
                ))}
              </div>
            ) : (
              <EmptyState size="sm" variant="custom" title="No external handoffs yet" description="When a library is configured to refine completed downloads first, the safe handoff and its import result will appear here." />
            )}
          </GlassTile>

          <GlassTile id="download-queue">
            <PanelHeader
              title="Imports in progress"
              subtitle="Files Deluno is moving, linking, and adding to your library."
              meta={failedImportJobs ? `${failedImportJobs} failed` : `${importJobs.length} recent`}
            />
            {failedImportJobs ? (
              <div className="border-b border-hairline px-[calc(var(--tile-pad)*0.85)] py-3">
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  className="gap-2"
                  disabled={busyKey !== null}
                  onClick={() => void handleRetryFailedJobs()}
                >
                  {busyKey === "jobs:retry-failed" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RotateCw className="h-3.5 w-3.5" />}
                  Retry failed jobs
                </Button>
              </div>
            ) : null}
            {importJobs.length ? (
              <div className="divide-y divide-hairline">
                {importJobs.slice(0, 8).map((job) => (
                  <ImportJobRow key={job.id} job={job} />
                ))}
              </div>
            ) : (
              <EmptyState
                size="sm"
                variant="custom"
                title="No import jobs yet"
                description="Use Queue import on an import-ready download to hand it to the background pipeline."
              />
            )}
          </GlassTile>

          <GlassTile id="import-jobs">
            <PanelHeader
              title="Download app history"
              subtitle="Completed, failed, and ready-to-import items reported by your download apps."
              meta={`${clientHistory.length} normalized`}
            />
            {clientHistory.length ? (
              <div className="divide-y divide-hairline">
                {clientHistory.slice(0, 16).map((item) => (
                  <ClientHistoryRow key={`${item.clientId}:${item.id}`} item={item} />
                ))}
              </div>
            ) : (
              <EmptyState size="sm" variant="custom" title="No client history yet" description="Completed downloads and failed client-side jobs will appear here once external clients report them." />
            )}
          </GlassTile>

          <GlassTile id="recovery">
            <PanelHeader
              title="What Deluno sent"
              subtitle="The releases Deluno approved and sent to a download app."
              meta={`${dispatches.length} recent`}
            />
            {dispatches.length ? (
              <div className="divide-y divide-hairline">
                {dispatches.slice(0, 12).map((dispatch) => (
                  <div key={dispatch.id} className="grid gap-3 px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.65)] md:grid-cols-[minmax(0,1fr)_auto] md:items-center">
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <p className="truncate font-medium text-foreground">{dispatch.releaseName}</p>
                        <Badge variant={dispatch.status === "sent" ? "success" : dispatch.status === "failed" ? "destructive" : "default"} className="text-[9.5px]">
                          {dispatch.status}
                        </Badge>
                        <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
                          {dispatch.mediaType}
                        </span>
                      </div>
                      <p className="mt-1 truncate font-mono text-[11px] text-muted-foreground">
                        {dispatch.indexerName || "unknown indexer"} to {dispatch.downloadClientName || "unassigned client"}
                      </p>
                    </div>
                    <span className="font-mono text-[11px] text-muted-foreground">{formatDateTime(dispatch.createdUtc)}</span>
                  </div>
                ))}
              </div>
            ) : (
              <EmptyState size="sm" variant="custom" title="No dispatches yet" description="Manual grabs and scheduled searches will populate this history." />
            )}
          </GlassTile>
        </div>

        <aside className="space-y-[var(--page-gap)]">
          <ManualImportPanel
            form={manualImport}
            preview={manualPreview}
            busyKey={busyKey}
            onChange={setManualImport}
            onPreview={handleManualPreview}
            onQueue={handleManualQueue}
          />

          <GlassTile>
            <PanelHeader title="Needs attention" subtitle="Imports that could not be completed automatically." meta={`${openRecovery} open`} />
            <div className="space-y-3 p-[calc(var(--tile-pad)*0.85)]">
              <RecoveryGroup
                title="Movies"
                mediaType="movie"
                cases={movieRecovery.recentCases}
                busyKey={busyKey}
                onDismiss={handleDismissRecovery}
                onRetry={handleRetryRecovery}
              />
              <RecoveryGroup
                title="TV"
                mediaType="series"
                cases={seriesRecovery.recentCases}
                busyKey={busyKey}
                onDismiss={handleDismissRecovery}
                onRetry={handleRetryRecovery}
              />
              {!movieRecovery.recentCases.length && !seriesRecovery.recentCases.length ? (
                <div className="rounded-xl border border-success/20 bg-success/5 p-4">
                  <div className="flex items-center gap-2 text-success">
                    <CheckCircle2 className="h-4 w-4" />
                    <p className="font-semibold">No recovery cases</p>
                  </div>
                  <p className="mt-1 text-[12px] text-muted-foreground">
                    Imports that fail because of missing sources, permission issues, or destination conflicts will appear here.
                  </p>
                </div>
              ) : null}
            </div>
          </GlassTile>

          <GlassTile>
            <PanelHeader title="Client capability matrix" subtitle="What Deluno can safely do through each protocol." />
            <div className="space-y-2 p-[calc(var(--tile-pad)*0.85)]">
              {telemetry.clients.length ? telemetry.clients.map((client) => (
                <CapabilityCard key={client.clientId} client={client} />
              )) : (
                <EmptyState size="sm" variant="custom" title="No clients configured" description="Add qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, or uTorrent in Library setup → Connections → Download clients." />
              )}
            </div>
          </GlassTile>
        </aside>
      </div>

      <ConfirmDialog
        open={pendingRemoval !== null}
        onOpenChange={(open) => {
          if (!open && busyKey === null) setPendingRemoval(null);
        }}
        title="Remove this client queue entry?"
        description={pendingRemoval
          ? `Deluno will ask ${pendingRemoval.clientName} to remove “${pendingRemoval.title}” from its queue. The client controls its own payload behaviour; Deluno never removes media-library files from this action.`
          : ""}
        confirmLabel="Remove queue entry"
        busy={pendingRemoval !== null && busyKey === `queue:${pendingRemoval.clientId}:${pendingRemoval.id}:delete`}
        onConfirm={() => void confirmRemoval()}
      />
    </div>
  );
}

function DownloadHealthHistoryRow({ record }: { record: DownloadHealthRecord }) {
  const ignored = record.ignoredUntilUtc && new Date(record.ignoredUntilUtc).getTime() > Date.now();
  return (
    <div className="px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.65)]">
      <div className="flex flex-wrap items-center gap-2">
        <p className="truncate font-medium text-foreground">{record.releaseName}</p>
        <Badge variant={record.severity === "critical" ? "destructive" : "warning"} className="text-[9.5px]">{record.kind.replaceAll("-", " ")}</Badge>
        <span className="text-[10.5px] text-muted-foreground">{record.strikeCount} {record.strikeCount === 1 ? "strike" : "strikes"}</span>
        {ignored ? <span className="text-[10.5px] text-muted-foreground">temporarily ignored</span> : null}
      </div>
      <p className="mt-1 text-[11px] text-muted-foreground">{record.evidence}</p>
      <p className="mt-1 text-[10.5px] text-muted-foreground">Last observed {new Date(record.lastObservedUtc).toLocaleString()}.</p>
    </div>
  );
}

function ProcessorHandoffRow({
  handoff,
  isRetrying,
  onRetry
}: {
  handoff: ProcessorHandoffItem;
  isRetrying: boolean;
  onRetry: () => void;
}) {
  const statusVariant = handoff.status === "completed"
    ? "success"
    : handoff.status === "failed" || handoff.status === "timed-out"
      ? "destructive"
      : handoff.status === "accepted" || handoff.status === "started"
        ? "default"
        : "info";
  const statusLabel = handoff.status === "waiting"
    ? "Waiting for processor"
    : handoff.status === "accepted"
      ? "Accepted by processor"
      : handoff.status === "started"
        ? "Processing"
    : handoff.status === "timed-out"
      ? "Processor timed out"
      : handoff.status.replace(/([a-z])([A-Z])/g, "$1 $2");

  return (
    <div className="px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.65)]">
      <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_auto] md:items-start">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="truncate font-medium text-foreground">{handoff.releaseName}</p>
            <Badge variant={statusVariant} className="text-[9.5px]">{statusLabel}</Badge>
            <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
              {handoff.mediaType || "media"}
            </span>
            {handoff.processorName ? (
              <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold text-muted-foreground">
                {handoff.processorName}
              </span>
            ) : null}
          </div>
          <p className="mt-1 truncate font-mono text-[10.5px] text-muted-foreground">from {handoff.sourcePath}</p>
          {handoff.outputPath ? <p className="mt-1 truncate font-mono text-[10.5px] text-muted-foreground">output {handoff.outputPath}</p> : null}
          {handoff.failureMessage ? (
            <p className="mt-2 rounded-lg border border-destructive/20 bg-destructive/5 px-2.5 py-1.5 text-[12px] text-destructive">
              {handoff.failureMessage}
            </p>
          ) : null}
        </div>
        <div className="grid gap-1 text-left md:text-right">
          <span className="font-mono text-[11px] text-muted-foreground">updated {formatDateTime(handoff.updatedUtc)}</span>
          {handoff.importJobId ? <span className="font-mono text-[11px] text-muted-foreground">import {handoff.importJobId.slice(0, 8)}</span> : null}
          {handoff.status === "failed" || handoff.status === "timed-out" ? (
            <Button type="button" variant="outline" size="sm" className="mt-1.5" disabled={isRetrying} onClick={onRetry}>
              {isRetrying ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RotateCw className="h-3.5 w-3.5" />}
              Try processor again
            </Button>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function ClientHistoryRow({ item }: { item: DownloadClientHistoryItem & { clientHealth?: string } }) {
  const outcomeVariant =
    item.outcome === "completed" || item.outcome === "success"
      ? "success"
      : item.outcome === "failed"
        ? "destructive"
        : item.outcome === "importReady"
          ? "info"
          : "default";

  return (
    <div className="grid gap-3 px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.65)] md:grid-cols-[minmax(0,1fr)_auto] md:items-center">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <p className="truncate font-medium text-foreground">{item.title}</p>
          <Badge variant={outcomeVariant} className="text-[9.5px]">
            {item.outcome}
          </Badge>
          <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
            {item.protocol}
          </span>
          <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
            {item.mediaType || "media"}
          </span>
        </div>
        <p className="mt-1 truncate font-mono text-[11px] text-muted-foreground">
          {item.releaseName} · {item.clientName} · {item.indexerName || "unknown source"}
        </p>
        {item.sourcePath ? (
          <p className="mt-1 truncate font-mono text-[10.5px] text-muted-foreground">source {item.sourcePath}</p>
        ) : null}
        {item.errorMessage ? (
          <p className="mt-2 rounded-lg border border-destructive/20 bg-destructive/5 px-2.5 py-1.5 text-[12px] text-destructive">
            {item.errorMessage}
          </p>
        ) : null}
      </div>
      <div className="grid gap-1 text-left md:text-right">
        <span className="font-mono text-[11px] text-muted-foreground">{formatDateTime(item.completedUtc)}</span>
        <span className="font-mono text-[11px] text-muted-foreground">{formatBytes(item.sizeBytes)}</span>
      </div>
    </div>
  );
}

function QueueRow({
  item,
  busyKey,
  preview,
  cleanupPreview,
  allowExternalClientRemoval,
  onAction,
  onIgnoreHealthFinding,
  onPreviewCleanup,
  onRequestRemove,
  onPreview,
  onImport,
  onQueueImport
}: {
  item: DownloadQueueItem;
  busyKey: string | null;
  preview?: ImportPreviewResponse;
  cleanupPreview?: DownloadCleanupPreview;
  allowExternalClientRemoval: boolean;
  onAction: (clientId: string, item: DownloadQueueItem, action: QueueAction) => Promise<void>;
  onIgnoreHealthFinding: (item: DownloadQueueItem, kind: string) => Promise<void>;
  onPreviewCleanup: (item: DownloadQueueItem) => Promise<void>;
  onRequestRemove: (item: DownloadQueueItem) => void;
  onPreview: (item: DownloadQueueItem) => Promise<void>;
  onImport: (item: DownloadQueueItem) => Promise<void>;
  onQueueImport: (item: DownloadQueueItem) => Promise<void>;
}) {
  const isReady = isImportReadyStatus(item.status);
  const isProcessing = isProcessingStatus(item.status) && item.status !== downloadQueueStatuses.importQueued;
  const isQueuedImport = item.status === downloadQueueStatuses.importQueued;
  const isImported = item.status === downloadQueueStatuses.imported;
  const isImportFailed = item.status === downloadQueueStatuses.importFailed;
  const isBusy = busyKey !== null;
  const healthFindings = item.healthFindings ?? [];
  const statusTone = item.status === downloadQueueStatuses.stalled || item.errorMessage || isImportFailed
    ? "destructive"
    : isReady || isImported
      ? "success"
      : item.status === downloadQueueStatuses.waitingForProcessor
        ? "warning"
        : item.status === downloadQueueStatuses.downloading || isProcessing || isQueuedImport
          ? "info"
          : "default";

  return (
    <div className="px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.75)]">
      <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="truncate font-medium text-foreground">{item.title}</p>
            <Badge variant={statusTone} className="text-[9.5px]">{queueStatusLabel(item.status)}</Badge>
            <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
              {item.protocol}
            </span>
            <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
              {item.mediaType || "media"}
            </span>
          </div>
          <p className="mt-1 truncate font-mono text-[11px] text-muted-foreground">
            {item.releaseName}
          </p>
          {item.sourcePath ? (
            <p className="mt-1 truncate font-mono text-[10.5px] text-muted-foreground">
              source {item.sourcePath}
            </p>
          ) : null}
          <div className="mt-3">
            <div className="h-2 overflow-hidden rounded-full bg-muted/60">
              <div
                className="h-full rounded-full bg-gradient-to-r from-primary to-[hsl(var(--primary-2))]"
                style={{ width: `${Math.max(0, Math.min(100, item.progress))}%` }}
              />
            </div>
            <div className="mt-1.5 flex flex-wrap gap-x-4 gap-y-1 font-mono text-[11px] text-muted-foreground">
              <span>{item.progress.toFixed(1)}%</span>
              <span>{item.speedMbps.toFixed(1)} MB/s</span>
              <span>{formatEta(item.etaSeconds)}</span>
              <span>{formatBytes(item.downloadedBytes)} / {formatBytes(item.sizeBytes)}</span>
              <span>{item.peers} peers</span>
              <span>{item.category || "uncategorised"}</span>
            </div>
          </div>
          {item.errorMessage ? (
            <p className="mt-2 rounded-lg border border-destructive/20 bg-destructive/5 px-2.5 py-1.5 text-[12px] text-destructive">
              {item.errorMessage}
            </p>
          ) : null}
          {healthFindings.map((finding) => (
            <div
              key={finding.kind}
              className={cn(
                "mt-2 rounded-xl border px-3 py-2.5",
                finding.severity === "critical" ? "border-destructive/20 bg-destructive/5" : "border-warning/25 bg-warning/5"
              )}
            >
              <p className={cn("text-[10px] font-semibold uppercase tracking-[0.14em]", finding.severity === "critical" ? "text-destructive" : "text-warning")}>
                Download health · {finding.kind.replaceAll("-", " ")}
              </p>
              <p className="mt-1 text-[12px] font-medium text-foreground">{finding.summary}</p>
              <p className="mt-1 text-[11px] text-muted-foreground">{finding.evidence} {finding.recommendedAction}</p>
              {finding.strikeCount > 0 ? (
                <p className="mt-1 text-[10.5px] text-muted-foreground">
                  {finding.strikeCount} health {finding.strikeCount === 1 ? "strike" : "strikes"}{finding.candidateBlocked ? "; this exact release is blocked from new grabs." : "."}
                </p>
              ) : null}
              {finding.ignoredUntilUtc ? (
                <p className="mt-1 text-[10.5px] text-muted-foreground">Ignored until {new Date(finding.ignoredUntilUtc).toLocaleString()}.</p>
              ) : (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="mt-1 h-7 px-2 text-[10.5px]"
                  disabled={isBusy}
                  onClick={() => void onIgnoreHealthFinding(item, finding.kind)}
                >
                  Ignore for 7 days
                </Button>
              )}
              <Button
                type="button"
                variant="ghost"
                size="sm"
                className="mt-1 h-7 px-2 text-[10.5px]"
                disabled={isBusy}
                onClick={() => void onPreviewCleanup(item)}
              >
                {busyKey === `cleanup-preview:${item.clientId}:${item.id}` ? "Preparing preview…" : "Preview safe cleanup"}
              </Button>
              {cleanupPreview ? (
                <div className="mt-2 rounded-lg border border-hairline bg-surface-1 px-2.5 py-2 text-[10.5px] text-muted-foreground">
                  <p className="font-medium text-foreground">{cleanupPreview.matchedPolicy}</p>
                  <p className="mt-1">{cleanupPreview.reason}</p>
                  <p className="mt-1">{cleanupPreview.proposedAction}</p>
                  <p className="mt-1">{cleanupPreview.affectedFiles}</p>
                  <p className="mt-1">This preview does not take action. It shows what the Automation & recovery policy will be allowed to do after its ownership checks.</p>
                </div>
              ) : null}
              <p className="mt-1.5 text-[10.5px] text-muted-foreground">
                Configure the three-strike policy in Automation & recovery; Deluno keeps a durable record of every finding and action.
              </p>
            </div>
          ))}
          {preview ? <ImportPreviewPanel preview={preview} /> : null}
          {isProcessing ? (
            <div className="mt-3 rounded-xl border border-primary/20 bg-primary/5 px-3 py-2.5">
              <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-primary">Refine before import</p>
              <p className="mt-1 text-[11px] text-muted-foreground">
                This item is waiting for the configured processor to produce a cleaned output file. Deluno will import that output when it becomes ready.
              </p>
            </div>
          ) : null}
          {isQueuedImport ? (
            <div className="mt-3 rounded-xl border border-primary/20 bg-primary/5 px-3 py-2.5">
              <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-primary">Import queued</p>
              <p className="mt-1 text-[11px] text-muted-foreground">
                Deluno has handed this completed download to the background import pipeline. The job monitor below will show move, hardlink, and catalog results.
              </p>
            </div>
          ) : null}
          {isImportFailed ? (
            <div className="mt-3 rounded-xl border border-destructive/20 bg-destructive/5 px-3 py-2.5">
              <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-destructive">Import failed</p>
              <p className="mt-1 text-[11px] text-muted-foreground">
                Deluno blocked or failed this import. Check the recovery panel or failed import job for the exact reason before forcing a retry.
              </p>
            </div>
          ) : null}
        </div>

        <div className="flex flex-wrap gap-1.5 lg:max-w-[360px] lg:justify-end">
          {isReady ? (
            <>
              <ActionButton
                icon={FileSearch}
                label="Preview import"
                busy={busyKey === `import-preview:${item.clientId}:${item.id}`}
                disabled={isBusy}
                onClick={() => void onPreview(item)}
              />
              <ActionButton
                icon={ArrowDownToLine}
                label="Import now"
                busy={busyKey === `import-now:${item.clientId}:${item.id}`}
                disabled={isBusy}
                onClick={() => void onImport(item)}
                primary
              />
              <ActionButton
                icon={HardDriveDownload}
                label="Queue import"
                busy={busyKey === `import-queue:${item.clientId}:${item.id}`}
                disabled={isBusy}
                onClick={() => void onQueueImport(item)}
              />
            </>
          ) : null}
          {isImportFailed && item.sourcePath ? (
            <>
              <ActionButton
                icon={FileSearch}
                label="Preview retry"
                busy={busyKey === `import-preview:${item.clientId}:${item.id}`}
                disabled={isBusy}
                onClick={() => void onPreview(item)}
              />
              <ActionButton
                icon={ArrowDownToLine}
                label="Retry import"
                busy={busyKey === `import-now:${item.clientId}:${item.id}`}
                disabled={isBusy}
                onClick={() => void onImport(item)}
                primary
              />
            </>
          ) : null}
          <ActionButton icon={Pause} label="Pause" busy={busyKey === `queue:${item.clientId}:${item.id}:pause`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "pause")} />
          <ActionButton icon={Play} label="Resume" busy={busyKey === `queue:${item.clientId}:${item.id}:resume`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "resume")} />
          {["qbittorrent", "transmission", "deluge", "utorrent"].includes(item.protocol) ? (
            <ActionButton icon={RotateCw} label="Recheck" busy={busyKey === `queue:${item.clientId}:${item.id}:recheck`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "recheck")} />
          ) : null}
          {allowExternalClientRemoval ? (
            <ActionButton icon={Trash2} label="Remove" busy={busyKey === `queue:${item.clientId}:${item.id}:delete`} disabled={isBusy} onClick={() => onRequestRemove(item)} destructive />
          ) : (
            <p className="basis-full text-[10.5px] text-muted-foreground">External-client queue removal is off. Enable it in Library setup → Media management if you want this confirmed control here.</p>
          )}
        </div>
      </div>
    </div>
  );
}

function ImportPreviewPanel({ preview }: { preview: ImportPreviewResponse }) {
  const hasWarnings = preview.warnings.length > 0;
  const risk = getImportPreviewRisk(preview);
  const probeSummary = formatProbeSummary(preview.mediaProbe);
  return (
    <div
      className={cn(
        "mt-3 rounded-xl border px-3 py-2.5",
        risk.tone === "blocked"
          ? "border-destructive/30 bg-destructive/5"
          : risk.tone === "warning"
            ? "border-warning/25 bg-warning/5"
            : "border-primary/20 bg-primary/5"
      )}
    >
      <div className="flex flex-wrap items-center gap-2">
        <p
          className={cn(
            "text-[10px] font-semibold uppercase tracking-[0.14em]",
            risk.tone === "blocked" ? "text-destructive" : risk.tone === "warning" ? "text-warning" : "text-primary"
          )}
        >
          Destination rule - {preview.preferredTransferMode}
        </p>
        <Badge variant={risk.badgeVariant} className="text-[9px]">
          {risk.label}
        </Badge>
        <Badge variant={preview.sourceExists ? "success" : "destructive"} className="text-[9px]">
          source {preview.sourceExists ? "visible" : "missing"}
        </Badge>
        <Badge variant={preview.destinationExists ? "warning" : "success"} className="text-[9px]">
          destination {preview.destinationExists ? "exists" : "clear"}
        </Badge>
      </div>
      <p className="mt-1 break-all font-mono text-[10.5px] text-muted-foreground">{preview.destinationPath}</p>
      <p className="mt-1 text-[11px] text-muted-foreground">{preview.explanation} {preview.transferExplanation}</p>
      <div className="mt-2 grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
        <PreviewFact label="Rule" value={preview.matchedRuleName || "Default resolver"} tone={preview.matchedRuleName ? "primary" : "neutral"} />
        <PreviewFact label="Transfer" value={preview.hardlinkAvailable ? "Hardlink ready" : preview.preferredTransferMode} tone={preview.hardlinkAvailable ? "success" : "warning"} />
        <PreviewFact label="Replacement" value={preview.destinationExists ? "Existing file" : "No conflict"} tone={preview.destinationExists ? "warning" : "success"} />
        <PreviewFact label="Source" value={preview.sourceExists ? formatBytes(preview.sourceSizeBytes) : "Not visible"} tone={preview.sourceExists ? "success" : "danger"} />
      </div>
      {probeSummary ? (
        <p className="mt-1 font-mono text-[10.5px] text-muted-foreground">
          {probeSummary}
        </p>
      ) : null}
      {preview.decisionSteps.length ? (
        <div className="mt-2 rounded-lg border border-hairline bg-background/40 p-2">
          <p className="text-[9.5px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Decision path</p>
          <ol className="mt-1 space-y-1">
            {preview.decisionSteps.map((step, index) => (
              <li key={`${index}-${step}`} className="grid grid-cols-[18px_minmax(0,1fr)] gap-2 text-[11px] text-muted-foreground">
                <span className="font-mono text-primary">{index + 1}</span>
                <span>{step}</span>
              </li>
            ))}
          </ol>
        </div>
      ) : null}
      {hasWarnings ? (
        <div className="mt-2 space-y-1">
          {preview.warnings.map((warning) => (
            <p key={warning} className="flex gap-1.5 text-[11px] text-warning">
              <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />
              <span>{warning}</span>
            </p>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function getImportPreviewRisk(preview: ImportPreviewResponse) {
  const warnings = preview.warnings.map((warning) => warning.toLowerCase());
  const isBlocked =
    !preview.sourceExists ||
    !preview.isSupportedMediaFile ||
    warnings.some((warning) => warning.includes("same file") || warning.includes("same path"));
  const isWarning = preview.destinationExists || warnings.length > 0;
  if (isBlocked) return { label: "Blocked", tone: "blocked" as const, badgeVariant: "destructive" as const };
  if (isWarning) return { label: "Review", tone: "warning" as const, badgeVariant: "warning" as const };
  return { label: "Ready", tone: "ready" as const, badgeVariant: "success" as const };
}

function formatProbeSummary(probe: ImportPreviewResponse["mediaProbe"]) {
  if (!probe) return "";
  const parts = [`Probe: ${probe.status}`];
  if (probe.durationSeconds) parts.push(formatDuration(probe.durationSeconds));
  const video = probe.videoStreams[0];
  if (video) parts.push(`${video.codec ?? "video"} ${video.width ?? "?"}x${video.height ?? "?"}`);
  parts.push(`${probe.audioStreams.length} audio`);
  parts.push(`${probe.subtitleStreams.length} subs`);
  return parts.join(" - ");
}

function formatDuration(seconds: number) {
  const rounded = Math.max(0, Math.round(seconds));
  const h = Math.floor(rounded / 3600).toString().padStart(2, "0");
  const m = Math.floor((rounded % 3600) / 60).toString().padStart(2, "0");
  const s = (rounded % 60).toString().padStart(2, "0");
  return `${h}:${m}:${s}`;
}

function PreviewFact({
  label,
  value,
  tone
}: {
  label: string;
  value: string;
  tone: "primary" | "success" | "warning" | "danger" | "neutral";
}) {
  const toneClass = {
    primary: "border-primary/20 bg-primary/5 text-primary",
    success: "border-success/20 bg-success/5 text-success",
    warning: "border-warning/20 bg-warning/5 text-warning",
    danger: "border-destructive/20 bg-destructive/5 text-destructive",
    neutral: "border-hairline bg-background/30 text-muted-foreground"
  }[tone];
  return (
    <div className={cn("rounded-lg border px-2.5 py-2", toneClass)}>
      <p className="text-[9.5px] font-bold uppercase tracking-[0.14em] opacity-75">{label}</p>
      <p className="mt-1 truncate text-[11px] font-semibold">{value}</p>
    </div>
  );
}

function ImportJobRow({ job }: { job: JobQueueItem }) {
  const payload = parseImportJobPayload(job.payloadJson);
  const statusVariant = job.status === JOB_STATUS.COMPLETED
    ? "success"
    : job.status === JOB_STATUS.FAILED
      ? "destructive"
      : job.status === JOB_STATUS.RUNNING
        ? "default"
        : "info";

  return (
    <div className="px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.7)]">
      <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium text-foreground">{payload?.title || "Background import"}</p>
            <Badge variant={statusVariant} className="text-[9.5px]">{job.status}</Badge>
            <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
              {payload?.mediaType || job.relatedEntityType || "media"}
            </span>
            <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
              {payload?.transferMode || "auto"}
            </span>
          </div>
          <div className="mt-2 grid gap-1 font-mono text-[10.5px] text-muted-foreground">
            <p className="truncate">from {payload?.sourcePath || "unknown source"}</p>
            <p className="truncate">as {payload?.fileName || "resolved by destination rules"}</p>
          </div>
          {job.lastError ? (
            <p className="mt-2 rounded-lg border border-destructive/20 bg-destructive/5 px-2.5 py-1.5 text-[12px] text-destructive">
              {job.lastError}
            </p>
          ) : null}
        </div>
        <div className="grid gap-1 text-left lg:text-right">
          <span className="font-mono text-[11px] text-muted-foreground">attempt {job.attempts}</span>
          <span className="font-mono text-[11px] text-muted-foreground">queued {formatDateTime(job.createdUtc)}</span>
          {job.startedUtc ? <span className="font-mono text-[11px] text-muted-foreground">started {formatDateTime(job.startedUtc)}</span> : null}
          {job.completedUtc ? <span className="font-mono text-[11px] text-muted-foreground">done {formatDateTime(job.completedUtc)}</span> : null}
        </div>
      </div>
    </div>
  );
}

function RecoveryGroup({
  title,
  mediaType,
  cases,
  busyKey,
  onDismiss,
  onRetry
}: {
  title: string;
  mediaType: "movie" | "series";
  cases: Array<MovieImportRecoveryCase | SeriesImportRecoveryCase>;
  busyKey: string | null;
  onDismiss: (mediaType: "movie" | "series", id: string) => Promise<void>;
  onRetry: (mediaType: "movie" | "series", item: MovieImportRecoveryCase | SeriesImportRecoveryCase) => Promise<void>;
}) {
  if (!cases.length) return null;
  return (
    <div className="space-y-2">
      <p className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">{title}</p>
      {cases.map((item) => {
        const key = `recovery:${mediaType}:${item.id}`;
        const retryKey = `recovery-retry:${mediaType}:${item.id}`;
        const canRetry = parseRecoveryRetryRequest(item.detailsJson) !== null;
        return (
          <div key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-3">
            <div className="flex items-start gap-3">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-warning" />
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <p className="font-medium text-foreground">{item.title}</p>
                  <Badge variant={item.failureKind === "quality" ? "warning" : "destructive"} className="text-[9px]">
                    {item.failureKind}
                  </Badge>
                </div>
                <p className="mt-1 text-[12px] text-muted-foreground">{item.summary}</p>
                <p className="mt-1 text-[12px] text-foreground">{item.recommendedAction}</p>
                <p className="mt-2 font-mono text-[10.5px] text-muted-foreground">{formatDateTime(item.detectedUtc)}</p>
              </div>
            </div>
            <div className="mt-3 grid gap-2 sm:grid-cols-2">
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="gap-2"
                disabled={busyKey !== null || !canRetry}
                onClick={() => void onRetry(mediaType, item)}
              >
                {busyKey === retryKey ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RotateCw className="h-3.5 w-3.5" />}
                Retry import
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="gap-2"
                disabled={busyKey !== null}
                onClick={() => void onDismiss(mediaType, item.id)}
              >
                {busyKey === key ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                Dismiss
              </Button>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function CapabilityCard({ client }: { client: DownloadClientTelemetrySnapshot }) {
  const capabilities = telemetryCapabilityChips(client);
  return (
    <div className="rounded-xl border border-hairline bg-surface-1 p-3">
      <div className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate font-semibold text-foreground">{client.clientName}</p>
          <p className="font-mono text-[10.5px] uppercase text-muted-foreground">{client.protocol}</p>
        </div>
        <Badge variant={isHealthyClient(client.healthStatus) ? "success" : "warning"} className="text-[9px]">
          {client.healthStatus}
        </Badge>
      </div>
      <div className="mt-3 grid grid-cols-2 gap-1.5">
        {capabilities.map((capability) => (
          <div
            key={capability.label}
            className={cn(
              "rounded-lg border px-2 py-1.5 text-[10.5px] font-semibold",
              capability.enabled
                ? "border-success/20 bg-success/5 text-success"
                : "border-hairline bg-muted/20 text-muted-foreground"
            )}
          >
            {capability.label}
          </div>
        ))}
      </div>
      {client.lastHealthMessage ? (
        <p className="mt-2 text-[11px] text-muted-foreground">{client.lastHealthMessage}</p>
      ) : null}
    </div>
  );
}

function ManualImportPanel({
  form,
  preview,
  busyKey,
  onChange,
  onPreview,
  onQueue
}: {
  form: ManualImportForm;
  preview: ImportPreviewResponse | null;
  busyKey: string | null;
  onChange: (form: ManualImportForm) => void;
  onPreview: () => Promise<void>;
  onQueue: () => Promise<void>;
}) {
  function update(patch: Partial<ManualImportForm>) {
    onChange({ ...form, ...patch });
  }

  return (
    <GlassTile>
      <PanelHeader
        title="Manual import"
        subtitle="Preview and queue an import from any server-visible path."
        meta="safe path first"
      />
      <div className="space-y-3 p-[calc(var(--tile-pad)*0.85)]">
        <div className="space-y-1.5">
          <label className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Source path</label>
          <PathInput
            value={form.sourcePath}
            onChange={(sourcePath) => update({ sourcePath })}
            placeholder="Completed file or folder path"
            browseTitle="Choose manual import source"
          />
        </div>

        <div className="grid gap-2 sm:grid-cols-2">
          <div className="space-y-1.5">
            <label className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Media</label>
            <select
              value={form.mediaType}
              onChange={(event) => update({ mediaType: event.target.value })}
              className="density-control-text h-[var(--control-height-sm)] w-full rounded-xl border border-hairline bg-surface-2 px-3 text-foreground outline-none"
            >
              <option value="movies">Movie</option>
              <option value="tv">TV show</option>
            </select>
          </div>
          <div className="space-y-1.5">
            <label className="text-[10px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Transfer</label>
            <select
              value={form.transferMode}
              onChange={(event) => update({ transferMode: event.target.value })}
              className="density-control-text h-[var(--control-height-sm)] w-full rounded-xl border border-hairline bg-surface-2 px-3 text-foreground outline-none"
            >
              <option value="auto">Auto</option>
              <option value="hardlink">Hardlink</option>
              <option value="copy">Copy</option>
              <option value="move">Move</option>
            </select>
          </div>
        </div>

        <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_96px]">
          <Input value={form.title} onChange={(event) => update({ title: event.target.value })} placeholder="Title, e.g. Dune Part Two" />
          <Input value={form.year} onChange={(event) => update({ year: event.target.value })} placeholder="2024" inputMode="numeric" />
        </div>
        <Input value={form.fileName} onChange={(event) => update({ fileName: event.target.value })} placeholder="Optional filename override" />
        <div className="grid gap-2 sm:grid-cols-2">
          <Input value={form.genres} onChange={(event) => update({ genres: event.target.value })} placeholder="Genres, comma separated" />
          <Input value={form.tags} onChange={(event) => update({ tags: event.target.value })} placeholder="Tags, comma separated" />
        </div>

        <div className="grid gap-2 sm:grid-cols-2">
          <Button
            type="button"
            variant="outline"
            className="gap-2"
            disabled={busyKey !== null}
            onClick={() => void onPreview()}
          >
            {busyKey === "manual-import:preview" ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileSearch className="h-4 w-4" />}
            Preview
          </Button>
          <Button
            type="button"
            className="gap-2"
            disabled={busyKey !== null}
            onClick={() => void onQueue()}
          >
            {busyKey === "manual-import:queue" ? <Loader2 className="h-4 w-4 animate-spin" /> : <HardDriveDownload className="h-4 w-4" />}
            Queue import
          </Button>
        </div>

        {preview ? <ImportPreviewPanel preview={preview} /> : null}
      </div>
    </GlassTile>
  );
}

function NextStep({
  href,
  icon: Icon,
  title,
  description,
  tone
}: {
  href?: string;
  icon: typeof Download;
  title: string;
  description: string;
  tone: "success" | "warning";
}) {
  const className = cn(
    "group rounded-xl border p-3 transition-colors",
    href && "hover:bg-muted/30",
    tone === "warning" ? "border-warning/25 bg-warning/5" : "border-success/25 bg-success/5"
  );
  const content = (
    <>
      <div className={cn("flex h-8 w-8 items-center justify-center rounded-lg", tone === "warning" ? "bg-warning/15 text-warning" : "bg-success/15 text-success")}>
        <Icon className="h-4 w-4" />
      </div>
      <p className="mt-3 text-[13px] font-semibold text-foreground">{title}</p>
      <p className="mt-1 text-[12px] leading-relaxed text-muted-foreground">{description}</p>
    </>
  );

  return href ? <Link to={href} className={className}>{content}</Link> : <div className={className}>{content}</div>;
}

function PanelHeader({ title, subtitle, meta }: { title: string; subtitle: string; meta?: string }) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-hairline px-[calc(var(--tile-pad)*0.85)] py-[calc(var(--tile-pad)*0.7)]">
      <div>
        <h2 className="font-display text-base font-semibold tracking-display text-foreground">{title}</h2>
        <p className="mt-0.5 text-[12px] text-muted-foreground">{subtitle}</p>
      </div>
      {meta ? <span className="font-mono text-[11px] text-muted-foreground">{meta}</span> : null}
    </div>
  );
}

function MetricTile({
  icon: Icon,
  label,
  value,
  unit,
  sub,
  tone
}: {
  icon: typeof Download;
  label: string;
  value: string | number;
  unit?: string;
  sub: string;
  tone: "primary" | "success" | "warn" | "neutral";
}) {
  const toneClass = {
    primary: "text-primary bg-primary/10 border-primary/20",
    success: "text-success bg-success/10 border-success/20",
    warn: "text-warning bg-warning/10 border-warning/20",
    neutral: "text-muted-foreground bg-muted/30 border-hairline"
  }[tone];
  return (
    <div className="h-full min-w-0 rounded-2xl border border-hairline bg-card p-[calc(var(--tile-pad)*0.75)] shadow-card">
      <div className={cn("flex h-[calc(var(--control-height-icon)*0.82)] w-[calc(var(--control-height-icon)*0.82)] items-center justify-center rounded-xl border", toneClass)}>
        <Icon className="h-4 w-4" />
      </div>
      <p className="density-nowrap mt-4 text-[length:var(--metric-label-size)] font-bold uppercase tracking-[0.16em] text-muted-foreground">{label}</p>
      <p className="density-nowrap mt-1 tabular font-display text-[length:var(--type-title-lg)] font-semibold tracking-display text-foreground">
        {value}
        {unit ? <span className="ml-1 text-sm font-semibold text-muted-foreground">{unit}</span> : null}
      </p>
      <p className="density-nowrap mt-1 text-[length:var(--metric-meta-size)] text-muted-foreground">{sub}</p>
    </div>
  );
}

function ActionButton({
  icon: Icon,
  label,
  busy,
  disabled,
  onClick,
  primary,
  destructive
}: {
  icon: typeof Download;
  label: string;
  busy: boolean;
  disabled: boolean;
  onClick: () => void;
  primary?: boolean;
  destructive?: boolean;
}) {
  return (
    <Button
      type="button"
      size="sm"
      variant={primary ? "default" : destructive ? "ghost" : "outline"}
      disabled={disabled}
      onClick={onClick}
      className={cn("gap-1.5", destructive && "text-destructive hover:text-destructive")}
    >
      {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Icon className="h-3.5 w-3.5" />}
      {label}
    </Button>
  );
}

function buildImportRequest(item: DownloadQueueItem, downloadsPath: string | null): ImportPreviewRequest {
  const fileName = inferImportFileName(item);
  const sourceBase = downloadsPath?.trim() || "D:\\Downloads";
  const sourcePath = item.sourcePath?.trim() || (
    sourceBase.endsWith("\\") || sourceBase.endsWith("/")
      ? `${sourceBase}${fileName}`
      : `${sourceBase}\\${fileName}`
  );

  return {
    sourcePath,
    fileName,
    mediaType: item.mediaType,
    title: item.title,
    year: inferYear(item.releaseName),
    genres: [],
    tags: [item.category].filter(Boolean),
    studio: null,
    originalLanguage: null
  };
}

function inferImportFileName(item: DownloadQueueItem) {
  const normalized = item.releaseName
    .replace(/[<>:"/\\|?*]+/g, ".")
    .replace(/\s+/g, ".")
    .replace(/\.+/g, ".")
    .replace(/^\.+|\.+$/g, "");
  return /\.(mkv|mp4|avi|mov|m4v)$/i.test(normalized) ? normalized : `${normalized || item.id}.mkv`;
}

function inferYear(value: string) {
  const match = value.match(/\b(19|20)\d{2}\b/);
  return match ? Number(match[0]) : null;
}

function splitCsv(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function actionLabel(action: QueueAction) {
  return {
    pause: "Pause",
    resume: "Resume",
    delete: "Cancel",
    recheck: "Recheck"
  }[action];
}

function formatEta(seconds: number) {
  if (!Number.isFinite(seconds) || seconds <= 0) return "ETA unknown";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const remaining = minutes % 60;
  return `${hours}h ${remaining}m`;
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function formatDateTime(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "unknown";
  return date.toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  });
}

interface ParsedImportJobPayload {
  sourcePath: string | null;
  fileName: string | null;
  mediaType: string | null;
  title: string | null;
  transferMode: string | null;
}

function parseImportJobPayload(payloadJson: string | null): ParsedImportJobPayload | null {
  if (!payloadJson) return null;
  try {
    const value = JSON.parse(payloadJson) as {
      preview?: {
        sourcePath?: string | null;
        fileName?: string | null;
        mediaType?: string | null;
        title?: string | null;
      } | null;
      Preview?: {
        SourcePath?: string | null;
        FileName?: string | null;
        MediaType?: string | null;
        Title?: string | null;
      } | null;
      transferMode?: string | null;
      TransferMode?: string | null;
    };
    const preview = value.preview ?? (value.Preview ? {
      sourcePath: value.Preview.SourcePath,
      fileName: value.Preview.FileName,
      mediaType: value.Preview.MediaType,
      title: value.Preview.Title
    } : null);
    return {
      sourcePath: preview?.sourcePath ?? null,
      fileName: preview?.fileName ?? null,
      mediaType: preview?.mediaType ?? null,
      title: preview?.title ?? null,
      transferMode: value.transferMode ?? value.TransferMode ?? null
    };
  } catch {
    return null;
  }
}

function parseRecoveryRetryRequest(detailsJson: string | null): ImportExecuteRequest | null {
  if (!detailsJson) return null;
  try {
    const value = JSON.parse(detailsJson) as Record<string, unknown>;
    const retry = (value.retryRequest ?? value.RetryRequest) as Record<string, unknown> | undefined;
    if (!retry) return null;

    const preview = (retry.preview ?? retry.Preview) as Record<string, unknown> | undefined;
    if (!preview) return null;

    const sourcePath = stringValue(preview.sourcePath ?? preview.SourcePath);
    if (!sourcePath) return null;

    return {
      preview: {
        sourcePath,
        fileName: stringValue(preview.fileName ?? preview.FileName),
        mediaType: stringValue(preview.mediaType ?? preview.MediaType),
        title: stringValue(preview.title ?? preview.Title),
        year: numberValue(preview.year ?? preview.Year),
        genres: stringArrayValue(preview.genres ?? preview.Genres),
        tags: stringArrayValue(preview.tags ?? preview.Tags),
        studio: stringValue(preview.studio ?? preview.Studio),
        originalLanguage: stringValue(preview.originalLanguage ?? preview.OriginalLanguage)
      },
      transferMode: stringValue(retry.transferMode ?? retry.TransferMode) ?? "auto",
      overwrite: booleanValue(retry.overwrite ?? retry.Overwrite),
      allowCopyFallback: booleanValue(retry.allowCopyFallback ?? retry.AllowCopyFallback, true),
      forceReplacement: booleanValue(retry.forceReplacement ?? retry.ForceReplacement, false)
    };
  } catch {
    return null;
  }
}

function stringValue(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function numberValue(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function booleanValue(value: unknown, fallback = false): boolean {
  return typeof value === "boolean" ? value : fallback;
}

function stringArrayValue(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null;
  const items = value.filter((item): item is string => typeof item === "string" && item.trim().length > 0);
  return items.length ? items : null;
}

function isHealthyClient(status: string) {
  return status === "ready" || status === "healthy";
}

function emptyTelemetry(): DownloadTelemetryOverview {
  return {
    capturedUtc: new Date(0).toISOString(),
    clients: [],
    summary: {
      activeCount: 0,
      queuedCount: 0,
      completedCount: 0,
      stalledCount: 0,
      importReadyCount: 0,
      processingCount: 0,
      totalSpeedMbps: 0
    }
  };
}

function emptyMovieRecovery(): MovieImportRecoverySummary {
  return {
    openCount: 0,
    qualityCount: 0,
    unmatchedCount: 0,
    corruptCount: 0,
    downloadFailedCount: 0,
    importFailedCount: 0,
    recentCases: []
  };
}

function emptySeriesRecovery(): SeriesImportRecoverySummary {
  return {
    openCount: 0,
    qualityCount: 0,
    unmatchedCount: 0,
    corruptCount: 0,
    downloadFailedCount: 0,
    importFailedCount: 0,
    recentCases: []
  };
}
