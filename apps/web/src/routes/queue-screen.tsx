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
import { useMemo, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { Loader2, Pause, Play, RefreshCw, RotateCw, Trash2, Upload } from "lucide-react";
import {
  ApiRequestError,
  fetchJson, fetchPageItems,
  type DownloadCleanupPreview,
  type DownloadHealthRecord,
  type DownloadDispatchItem,
  type DownloadQueueItem,
  type DownloadTelemetryOverview,
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
    fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=60"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchPageItems<JobQueueItem>("/api/jobs?pageSize=80"),
    fetchPageItems<DownloadHealthRecord>("/api/download-health?pageSize=30"),
    fetchJson<ProcessorHandoffItem[]>("/api/integrations/processors/handoffs?take=30").catch((error) => {
      // Permit a newly deployed web build to remain usable during a rolling upgrade
      // while an older local API is still running. Other failures remain visible.
      if (error instanceof ApiRequestError && error.status === 404) return [];
      throw error;
    })
  ]);
  return { telemetry, dispatches, movieRecovery, seriesRecovery, settings, jobs, healthRecords, processorHandoffs };
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

type DrawerMode = { kind: "queue"; id: string } | { kind: "manual" } | { kind: "activity"; id: string } | null;

export function QueuePage() {
  const { telemetry, dispatches, movieRecovery, seriesRecovery, settings, jobs, healthRecords, processorHandoffs } =
    useLoaderData() as QueueLoaderData;
  const revalidator = useRevalidator();

  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [drawer, setDrawer] = useState<DrawerMode>(null);
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
  const failedImportJobs = importJobs.filter((job) => job.status === JOB_STATUS.FAILED);
  const activeImportJobs = importJobs.filter((job) => isJobActive(job.status as never)).length;
  const healthyClients = telemetry.clients.filter((client) => isHealthyClient(client.healthStatus)).length;
  const recoveryCases = useMemo(
    () => [
      ...movieRecovery.recentCases.map((item) => ({ ...item, mediaType: "movie" as const })),
      ...seriesRecovery.recentCases.map((item) => ({ ...item, mediaType: "series" as const }))
    ],
    [movieRecovery.recentCases, seriesRecovery.recentCases]
  );
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;
  const needsAction = openRecovery + queueAttention.length + failedImportJobs.length;

  const activity = useMemo(
    () => buildActivity({ importJobs, processorHandoffs, dispatches, telemetry, healthRecords }),
    [importJobs, processorHandoffs, dispatches, telemetry, healthRecords]
  );
  const visibleActivity = activityKind === "all" ? activity : activity.filter((entry) => entry.kind === activityKind);
  const openQueueItem = drawer?.kind === "queue" ? queue.find((item) => item.id === drawer.id) ?? null : null;
  const openActivity = drawer?.kind === "activity" ? activity.find((entry) => entry.id === drawer.id) ?? null : null;

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
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildImportRequest(item, settings.downloadsPath))
      });
      setImportPreviews((current) => ({ ...current, [item.id]: preview }));
    });
  }

  async function queueImport(item: DownloadQueueItem) {
    await run(
      `import:${item.id}`,
      async () => {
        const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            preview: buildImportRequest(item, settings.downloadsPath),
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
          { label: "Downloading", value: String(telemetry.summary.activeCount), help: processing.length ? `${processing.length} being processed` : "in your clients" },
          { label: "Speed", value: telemetry.summary.totalSpeedMbps.toFixed(1), help: "MB/s combined" },
          { label: "Ready to import", value: String(importReady.length), help: importReady.length ? "waiting on you" : "nothing waiting", tone: importReady.length ? "success" : undefined },
          { label: "Needs action", value: String(needsAction), help: needsAction ? "see below" : "all clear", tone: needsAction ? "warning" : undefined },
          { label: "Clients", value: `${healthyClients}/${telemetry.clients.length}`, help: healthyClients === telemetry.clients.length ? "all responding" : "one is not responding", tone: healthyClients < telemetry.clients.length ? "danger" : undefined }
        ]}
      />

      <ListCard
        title="Downloads"
        count={queue.length ? `${queue.length} in flight · ${activeImportJobs} importing` : undefined}
      >
        {queue.length === 0 ? (
          <ListEmpty
            title="Nothing downloading"
            description="Releases Deluno sends to a download client show up here with progress, speed and import status. You only need to step in when one needs attention."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Release" },
              { label: "Progress", width: "minmax(0,1.2fr)" },
              { label: "Speed / left" },
              { label: "Client" },
              { label: "Status", width: LIST_TRACK.status, mobile: true }
            ]}
          >
            {queue.map((item) => {
              const chip = queueChip(item);
              return (
                <ListRow key={`${item.clientId}:${item.id}`} onClick={() => setDrawer({ kind: "queue", id: item.id })} selected={openQueueItem?.id === item.id}>
                  <ListNameCell name={item.title || item.releaseName} sub={item.mediaType === "tv" ? "TV" : "Movies"} />
                  <ListCell>
                    <ProgressBar value={item.progress} />
                    <span className="mt-1 block truncate text-[length:var(--type-caption)] tabular-nums text-muted-foreground">
                      {Math.round(item.progress)}% of {formatBytes(item.sizeBytes)}
                    </span>
                  </ListCell>
                  <ListCell
                    numeric
                    primary={item.speedMbps > 0 ? `${item.speedMbps.toFixed(1)} MB/s` : "—"}
                    secondary={item.etaSeconds > 0 ? formatEta(item.etaSeconds) : undefined}
                  />
                  <ListCell primary={item.clientName} secondary={item.indexerName || undefined} />
                  <ListCell mobile>
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

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
                  primary={item.errorMessage ?? item.healthFindings?.[0]?.summary ?? "This download has stalled."}
                  secondary={item.healthFindings?.length ? `${item.healthFindings.length} health ${item.healthFindings.length === 1 ? "finding" : "findings"}` : undefined}
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
          </ListTable>
        </ListCard>
      ) : null}

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
                { value: "client", label: "Clients" }
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
              <ListRow key={entry.id} onClick={() => setDrawer({ kind: "activity", id: entry.id })} selected={openActivity?.id === entry.id}>
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
        footer={<DrawerFooter state="clean" saveType="button" saveLabel="Close" saveEnabled={false} onCancel={() => setDrawer(null)} />}
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
            <Field label="Transfer" help="Auto picks a hardlink when it can and a copy when it cannot.">
              <Select
                value={manualImport.transferMode}
                onChange={(event) => setManualImport((current) => ({ ...current, transferMode: event.target.value }))}
                options={[
                  { value: "auto", label: "Automatic" },
                  { value: "hardlink", label: "Hardlink" },
                  { value: "copy", label: "Copy" },
                  { value: "move", label: "Move" }
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

      {/* ------------------------------------------------- activity drawer */}
      <Drawer
        open={drawer?.kind === "activity"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openActivity?.name ?? "Activity"}
        description={openActivity ? `${openActivity.sub} · ${openActivity.status}` : undefined}
        footer={
          <DrawerFooter
            state="clean"
            saveType="button"
            saveLabel={openActivity?.kind === "processor" ? "Try again" : "Close"}
            saveEnabled={openActivity?.kind === "processor" && busyKey === null}
            onSave={() => {
              const handoff = processorHandoffs.find((entry) => `processor:${entry.id}` === openActivity?.id);
              if (handoff) void retryHandoff(handoff);
            }}
            onCancel={() => setDrawer(null)}
          />
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
  if (item.errorMessage || item.status === downloadQueueStatuses.stalled) return { tone: "bad", label: queueStatusLabel(item.status) };
  if (item.healthFindings?.length) return { tone: "warn", label: "Needs a look" };
  if (isImportReadyStatus(item.status)) return { tone: "ok", label: "Ready to import" };
  if (isProcessingStatus(item.status)) return { tone: "info", label: queueStatusLabel(item.status) };
  return { tone: "info", label: queueStatusLabel(item.status) };
}

/** Every finished thing, in one row shape, newest first. */
function buildActivity({
  importJobs,
  processorHandoffs,
  dispatches,
  telemetry,
  healthRecords
}: {
  importJobs: JobQueueItem[];
  processorHandoffs: ProcessorHandoffItem[];
  dispatches: DownloadDispatchItem[];
  telemetry: DownloadTelemetryOverview;
  healthRecords: DownloadHealthRecord[];
}): ActivityEntry[] {
  const entries: ActivityEntry[] = [];

  for (const job of importJobs) {
    entries.push({
      id: `import:${job.id}`,
      kind: "import",
      name: jobTitle(job),
      sub: "Import",
      detail: job.status === JOB_STATUS.FAILED ? job.lastError ?? "The import failed." : `Import ${job.status}`,
      extra: job.attempts > 1 ? `Attempt ${job.attempts}` : undefined,
      tone: job.status === JOB_STATUS.FAILED ? "bad" : isJobActive(job.status as never) ? "info" : "ok",
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
      detail: `${dispatch.indexerName || "unknown source"} → ${dispatch.downloadClientName || "unassigned client"}`,
      tone: dispatch.status === "sent" ? "ok" : dispatch.status === "failed" ? "bad" : "muted",
      status: dispatch.status,
      whenUtc: dispatch.createdUtc
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

function parseImportJobPayload(payloadJson: string | null): { title: string | null; fileName: string | null } | null {
  if (!payloadJson) return null;
  try {
    const parsed = JSON.parse(payloadJson) as { preview?: { title?: string; fileName?: string }; title?: string; fileName?: string };
    return {
      title: parsed.preview?.title ?? parsed.title ?? null,
      fileName: parsed.preview?.fileName ?? parsed.fileName ?? null
    };
  } catch {
    return null;
  }
}

function buildImportRequest(item: DownloadQueueItem, downloadsPath: string | null): ImportPreviewRequest {
  return {
    sourcePath: item.sourcePath || downloadsPath || "",
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

function formatBytes(value: number) {
  if (!value) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
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
