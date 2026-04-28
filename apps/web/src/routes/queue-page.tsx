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
  fetchJson,
  type DownloadClientHistoryItem,
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
  type SeriesImportRecoveryCase,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { PathInput } from "../components/ui/path-input";
import { EmptyState } from "../components/shell/empty-state";
import { GlassTile, PageHero } from "../components/shell/page-hero";
import { Stagger, StaggerItem } from "../components/shell/motion";
import { RouteSkeleton } from "../components/shell/skeleton";
import { toast } from "../components/shell/toaster";

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
}

export async function queueLoader(): Promise<QueueLoaderData> {
  const [telemetry, dispatches, movieRecovery, seriesRecovery, settings, jobs] = await Promise.all([
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry"),
    fetchJson<DownloadDispatchItem[]>("/api/download-dispatches?take=60"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<JobQueueItem[]>("/api/jobs?take=80")
  ]);

  return { telemetry, dispatches, movieRecovery, seriesRecovery, settings, jobs };
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

  const telemetry = loaderData.telemetry;
  const dispatches = loaderData.dispatches;
  const movieRecovery = loaderData.movieRecovery;
  const seriesRecovery = loaderData.seriesRecovery;
  const settings = loaderData.settings;
  const jobs = loaderData.jobs;

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
  const importReady = allQueue.filter((item) => item.status === "importReady" || item.status === "completed");
  const processing = allQueue.filter((item) => ["processing", "processed", "processingFailed", "waitingForProcessor", "importQueued"].includes(item.status));
  const stalled = allQueue.filter((item) => item.status === "stalled" || item.errorMessage);
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;
  const activeImportJobs = importJobs.filter((job) => job.status === "queued" || job.status === "running").length;
  const failedImportJobs = importJobs.filter((job) => job.status === "failed").length;
  const activeClients = telemetry.clients.filter((client) => isHealthyClient(client.healthStatus)).length;

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
        eyebrow="Queue and imports"
        eyebrowIcon={<HardDriveDownload className="h-3 w-3 text-primary" />}
        title={
          <>
            Downloads, imports, and recovery in one operational view.
          </>
        }
        subtitle={
          <>
            Normalized telemetry from external clients, destination-rule previews before import, and recovery
            cases when Deluno cannot safely move media.
          </>
        }
        stats={[
          { label: "Active", value: telemetry.summary.activeCount.toString(), tone: "primary" },
          { label: "Processing", value: (telemetry.summary.processingCount ?? processing.length).toString(), tone: processing.length ? "primary" : "neutral" },
          { label: "Import ready", value: telemetry.summary.importReadyCount.toString(), tone: "success" },
          { label: "Import jobs", value: activeImportJobs.toString(), tone: activeImportJobs ? "primary" : "neutral" },
          { label: "Recovery", value: openRecovery.toString(), tone: openRecovery ? "danger" : "neutral" }
        ]}
        actions={
          <>
            <Button size="lg" className="gap-2" onClick={() => revalidator.revalidate()}>
              <RefreshCw className="h-4 w-4" />
              Refresh telemetry
            </Button>
            <Button asChild size="lg" variant="secondary" className="gap-2">
              <Link to="/indexers">
                <Wand2 className="h-4 w-4" />
                Configure clients
              </Link>
            </Button>
          </>
        }
      />

      <Stagger className="fluid-kpi-grid">
        <StaggerItem>
          <MetricTile icon={Download} label="Connected clients" value={`${activeClients}/${telemetry.clients.length}`} sub="ready endpoints" tone="primary" />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={ArrowDownToLine} label="Total speed" value={`${telemetry.summary.totalSpeedMbps.toFixed(1)}`} unit="MB/s" sub="normalized throughput" tone="success" />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={FileSearch} label="Import ready" value={importReady.length} sub="safe to preview" tone="success" />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={Wand2} label="Processing" value={processing.length} sub="refine-before-import lane" tone={processing.length ? "primary" : "neutral"} />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={GitBranch} label="Import jobs" value={activeImportJobs} sub="queued or running" tone={activeImportJobs ? "primary" : "neutral"} />
        </StaggerItem>
        <StaggerItem>
          <MetricTile icon={AlertTriangle} label="Needs action" value={openRecovery + stalled.length} sub="stalled or recovery" tone={openRecovery + stalled.length ? "warn" : "neutral"} />
        </StaggerItem>
      </Stagger>

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.45fr)_minmax(360px,0.8fr)]">
        <div className="space-y-[var(--page-gap)]">
          <GlassTile>
            <PanelHeader
              title="Unified client queue"
              subtitle="Every external downloader normalized into one model."
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
                    onAction={handleQueueAction}
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

          <GlassTile>
            <PanelHeader
              title="Import job monitor"
              subtitle="Queued import work from Deluno's mover, hardlink, and catalog update pipeline."
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

          <GlassTile>
            <PanelHeader
              title="Client history"
              subtitle="Completed, failed, and import-ready items reported by external clients and Deluno dispatches."
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

          <GlassTile>
            <PanelHeader
              title="Dispatch history"
              subtitle="What Deluno sent, where it was sent, and which client received it."
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
            <PanelHeader title="Recovery center" subtitle="Failed imports and unresolved media handoffs." meta={`${openRecovery} open`} />
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
                <EmptyState size="sm" variant="custom" title="No clients configured" description="Add qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, or uTorrent from Indexers." />
              )}
            </div>
          </GlassTile>
        </aside>
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
  onAction,
  onPreview,
  onImport,
  onQueueImport
}: {
  item: DownloadQueueItem;
  busyKey: string | null;
  preview?: ImportPreviewResponse;
  onAction: (clientId: string, item: DownloadQueueItem, action: QueueAction) => Promise<void>;
  onPreview: (item: DownloadQueueItem) => Promise<void>;
  onImport: (item: DownloadQueueItem) => Promise<void>;
  onQueueImport: (item: DownloadQueueItem) => Promise<void>;
}) {
  const isReady = item.status === "importReady" || item.status === "completed";
  const isProcessing = item.status === "processing" || item.status === "processed" || item.status === "waitingForProcessor";
  const isQueuedImport = item.status === "importQueued";
  const isImported = item.status === "imported";
  const isImportFailed = item.status === "importFailed";
  const isBusy = busyKey !== null;
  const statusTone = item.status === "stalled" || item.errorMessage || isImportFailed
    ? "destructive"
    : isReady || isImported
      ? "success"
    : item.status === "downloading" || isProcessing || isQueuedImport
        ? "default"
        : "info";

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
          <ActionButton icon={Pause} label="Pause" busy={busyKey === `queue:${item.clientId}:${item.id}:pause`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "pause")} />
          <ActionButton icon={Play} label="Resume" busy={busyKey === `queue:${item.clientId}:${item.id}:resume`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "resume")} />
          {["qbittorrent", "transmission", "deluge", "utorrent"].includes(item.protocol) ? (
            <ActionButton icon={RotateCw} label="Recheck" busy={busyKey === `queue:${item.clientId}:${item.id}:recheck`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "recheck")} />
          ) : null}
          <ActionButton icon={Trash2} label="Remove" busy={busyKey === `queue:${item.clientId}:${item.id}:delete`} disabled={isBusy} onClick={() => void onAction(item.clientId, item, "delete")} destructive />
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
  const statusVariant = job.status === "completed"
    ? "success"
    : job.status === "failed"
      ? "destructive"
      : job.status === "running"
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
  const capabilities = protocolCapabilities(client.protocol);
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
              capability.supported
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
    delete: "Remove",
    recheck: "Recheck"
  }[action];
}

function queueStatusLabel(status: string) {
  return {
    downloading: "Downloading",
    queued: "Queued",
    completed: "Import ready",
    importReady: "Import ready",
    waitingForProcessor: "Waiting for processor",
    processing: "Processing",
    processed: "Processed",
    processingFailed: "Processing failed",
    importQueued: "Import queued",
    importFailed: "Import failed",
    imported: "Imported",
    stalled: "Stalled"
  }[status] ?? status;
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

function protocolCapabilities(protocol: string) {
  const key = protocol.toLowerCase();
  const torrent = ["qbittorrent", "transmission", "deluge", "utorrent"].includes(key);
  const usenet = ["sabnzbd", "nzbget"].includes(key);
  return [
    { label: "Queue", supported: true },
    { label: "History", supported: true },
    { label: "Pause/resume", supported: true },
    { label: "Delete", supported: true },
    { label: "Recheck", supported: torrent },
    { label: "Import ready", supported: true },
    { label: "Category routing", supported: true },
    { label: "Repair/unpack", supported: usenet }
  ];
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
