/**
 * Transfers — the live hand-off view, on the shared grammar.
 *
 *   PageToolbar  (Manual import)
 *   SummaryStrip (downloading · speed · ready to import · needs action · clients)
 *   ListCard     downloads        → drawer: progress · health · import · actions
 *   ListCard     needs attention  → only when something does (recovery, failed jobs)
 *   link         See everything in Activity →
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
import { statusPresentation, statusTone, toneSortValue } from "../lib/status-tones";
import { eventLabel } from "../lib/activity-format";
import { handoffCanBeSentAgain } from "../lib/alert-actions";
import { approveHeldRelease, dismissHeldRelease, findAnotherForHeldRelease, heldReleasesSource, heldReleaseTitle, type HeldReleaseRow } from "../lib/held-releases";
import { plainSearchResult } from "../lib/release-candidate-status";
import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { defineSources, queryClient, sourceLoader, useRouteSources, type RouteSourceData, type RouteSourceState } from "../lib/route-cache";
import { emptyRecovery, sharedSources } from "../lib/shared-sources";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { ArrowDownToLine, ArrowUpToLine, ChevronDown, ChevronUp, Loader2, Pause, Play, RotateCw, Trash2, Upload, Zap } from "lucide-react";
import {
  ApiRequestError,
  emptyPlatformSettingsSnapshot,
  fetchJson, fetchPageItems,
  type DownloadCleanupPreview,
  type DownloadDispatchDetail,
  type DownloadHealthFinding,
  type DownloadHealthRecord,
  type DownloadDispatchItem,
  type DownloadClientTelemetrySnapshot,
  type DownloadQueueItem,
  type DownloadTelemetryOverview,
  type IntegrationFailure,
  type ActivityEventItem,
  type ImportBatchExecuteResponse,
  type ImportExecuteRequest,
  type ImportFolderScanItem,
  type ImportFolderScanResponse,
  type ImportJobResponse,
  type ImportPreviewRequest,
  type ImportPreviewResponse,
  type JobQueueItem,
  type LibraryItem,
  type MediaProbeInfo,
  type MovieImportRecoverySummary,
  type OrphanedDownloadItem,
  type PlatformSettingsSnapshot,
  type ProcessorConnectionItem,
  type ProcessorHandoffItem,
  type ProcessorOutcomeRecord,
  type SeriesImportRecoverySummary, readErrorText } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { formatBytes } from "../lib/utils";
import { resolveImportSourcePath } from "../lib/import-source";
import { fileProvenanceFromTimeline } from "../lib/dispatch-provenance";
import { dismissFailedJob } from "../lib/failed-jobs";
import { JOB_STATUS, isJobActive, isJobDeadLettered, isJobFailed, isJobRetrying, type JobStatus } from "../lib/job-status-constants";
import { clientHistoryOutcomeLabel, downloadQueueStatuses, isImportReadyStatus, isProcessingStatus, queueStatusLabel } from "../lib/download-telemetry";
import { parseImportJobPayload } from "../lib/import-job-payload";
import { placeDownloadsOnce } from "../lib/transfer-stages";
import { dispatchWasImported } from "../lib/dispatch-final-state";
import { SOURCE_GONE, canTryAgain, handoffNeedsPerson, handoffStatusLabel, isDispatchFailure, isProcessorFailure, isProcessorFault, isVanished, isWithProcessor, needsAction, plainFailure } from "../lib/needs-action";
import { processorOutcomeSummary } from "../lib/processor-outcome";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { Chip, type ChipProps } from "../components/ui/chip";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { RemovalReasonDialog, type RemovalReason } from "../components/ui/removal-reason-dialog";
import { submitQueueRemoval } from "../lib/remove-download";
import { BlockReleaseAction } from "../components/app/block-release-action";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PathInput } from "../components/ui/path-input";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { Switch } from "../components/ui/switch";
import { ColumnPicker } from "../components/ui/column-picker";
import { realtimeToast, toast } from "../components/shell/toaster";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { applyImportOutcome, TRANSFERS_JOBS_KEY, useCoalescedRefresh, useTransfersLiveEvents } from "./queue-live";
import { formatDateTime as formatPreferenceDateTime, useDisplayPreferences, type DisplayPreferences } from "../lib/display-preferences";
import { useColumnVisibility, type ColumnDefinition } from "../lib/use-column-visibility";

/**
 * The queue table's optional columns (#796): quality, custom-format score,
 * language and a dedicated timeleft, beside the always-shown Media / Stage /
 * Progress / Client / Status. All default on — Radarr shows its equivalents
 * by default too — but the picker lets a narrow screen or a person who does
 * not care about custom-format scores turn them back off, remembered for
 * next time the same way Activity's own picker is (#798).
 */
const QUEUE_OPTIONAL_COLUMNS: readonly ColumnDefinition[] = [
  { key: "quality", label: "Quality", defaultVisible: true },
  { key: "customFormat", label: "Custom format score", defaultVisible: true },
  { key: "language", label: "Language", defaultVisible: true },
  { key: "size", label: "Size", defaultVisible: true },
  { key: "timeleft", label: "Timeleft", defaultVisible: true }
];

type QueueAction = "pause" | "resume" | "recheck" | "move-to-top" | "move-up" | "move-down" | "move-to-bottom" | "force-start";

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

/**
 * One row of a folder scan (#783), editable before import. Deluno's own
 * guess seeds `title`/`year`; a person can correct either and re-check the
 * row before it is included in the batch.
 */
interface FolderScanRow {
  sourcePath: string;
  fileName: string;
  title: string;
  year: string;
  matched: boolean;
  preview: ImportPreviewResponse;
  /** Off by default for an unmatched row — a guess with nothing in it is not something to import silently. */
  include: boolean;
  checking: boolean;
}

/**
 * Transfers opens on the answers it already has and asks again behind them
 * (#1081). Only the downloads and what was sent are waited for; everything else
 * fills in as it arrives, and a source that fails says so on the screen with a
 * Retry rather than taking the whole page to the error screen.
 */
export const queueSources = defineSources({
  telemetry: sharedSources.telemetry,
  dispatches: sharedSources.dispatches,
  movieRecovery: { ...sharedSources.movieRecovery, fallback: emptyRecovery as MovieImportRecoverySummary },
  seriesRecovery: { ...sharedSources.seriesRecovery, fallback: emptyRecovery as SeriesImportRecoverySummary },
  libraries: { ...sharedSources.libraries, fallback: [] as LibraryItem[] },
  settings: { ...sharedSources.settings, fallback: emptyPlatformSettingsSnapshot },
  jobs: { key: [...TRANSFERS_JOBS_KEY], label: "Jobs", fetch: () => fetchPageItems<JobQueueItem>("/api/jobs?pageSize=80&includePayload=true"), fallback: [] as JobQueueItem[] },
  failedJobs: { ...sharedSources.failedJobs, fallback: [] as JobQueueItem[] },
  healthRecords: { key: ["src", "download-health-30"], label: "Download health", fetch: () => fetchPageItems<DownloadHealthRecord>("/api/download-health?pageSize=30"), fallback: [] as DownloadHealthRecord[] },
  processorConnections: {
    key: ["src", "processor-connections"],
    label: "Processors",
    fetch: () => fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections").catch((error) => {
      if (error instanceof ApiRequestError && error.status === 404) return [];
      throw error;
    }),
    fallback: [] as ProcessorConnectionItem[]
  },
  processorHandoffs: { ...sharedSources.handoffs, fallback: [] as ProcessorHandoffItem[] },
  activityEvents: { key: ["src", "activity-dispatch-80"], label: "What happened to each download", fetch: () => fetchPageItems<ActivityEventItem>("/api/activity?pageSize=80&relatedEntityType=download_dispatch"), fallback: [] as ActivityEventItem[] },
  // Orphaned download folders (#588). An older API without the endpoint shows none.
  orphans: { ...sharedSources.orphans, fallback: [] as OrphanedDownloadItem[] },
  // Releases a search held for a person's yes (#1159). An older API without the endpoint lists none.
  heldReleases: {
    ...heldReleasesSource,
    fetch: () => heldReleasesSource.fetch().catch((error) => {
      if (error instanceof ApiRequestError && error.status === 404) return [] as HeldReleaseRow[];
      throw error;
    })
  }
});

export const queueLoader = sourceLoader(queueSources);

/** One row shape for the finished things a Transfers drawer can open. */
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

type DrawerMode = { kind: "queue"; id: string } | { kind: "manual" } | { kind: "activity"; id: string } | { kind: "dispatch"; id: string } | { kind: "attention"; id: string } | null;

/** What a Needs-attention row says in full, for the rows that have no drawer of their own (a phone cannot hover to read it). */
interface AttentionDetail {
  title: string;
  sub: string;
  facts: { label: string; value: string; mono?: boolean }[];
}

/** Rows of Needs attention that stack their name, their reason and their buttons on a phone, so the buttons never squeeze the name to nothing. */
const ATTENTION_ROW_PHONE = "max-md:grid-cols-1 max-md:gap-y-1.5 max-md:py-2.5";
/** The buttons' cell on a phone: left-aligned under the words. */
const ATTENTION_ACTIONS_PHONE = "max-md:text-left";

export function QueuePage() {
  const state = useRouteSources(queueSources);
  return (
    <RouteSourceGate state={state}>
      {() => <QueuePageBody data={state.data} revalidator={state.revalidator} />}
    </RouteSourceGate>
  );
}

function QueuePageBody({ data, revalidator }: { data: RouteSourceData<typeof queueSources>; revalidator: RouteSourceState<typeof queueSources>["revalidator"] }) {
  const { telemetry, dispatches, movieRecovery, seriesRecovery, libraries, settings, jobs, failedJobs, healthRecords, processorConnections, processorHandoffs, activityEvents, orphans, heldReleases } = data;
  const { preferences } = useDisplayPreferences();
  const queueColumns = useColumnVisibility("queue-screen.columns", QUEUE_OPTIONAL_COLUMNS);

  // Transfers is live: pushes patch the lists this screen reads (queue-live.ts). Only the lists with no
  // patch of their own are read again, once, after a burst; there is no timer and no Refresh.
  const refreshDispatches = useCoalescedRefresh([queueSources.dispatches.key, queueSources.telemetry.key, queueSources.activityEvents.key]);
  const refreshActivity = useCoalescedRefresh([queueSources.activityEvents.key]);
  const refreshJobs = useCoalescedRefresh([queueSources.jobs.key]);
  const refreshTelemetry = useCoalescedRefresh([queueSources.telemetry.key]);
  useTransfersLiveEvents(refreshDispatches, refreshActivity, refreshJobs, refreshTelemetry);
  useSignalREvent("DispatchGrabAttempt", RealtimeGroups.Queue, (event) => {
    realtimeToast.info(`Sent "${event.releaseName}" to ${event.clientName}`);
  });

  const [busyKey, setBusyKey] = useState<string | null>(null);
  /** Hand-offs sent again from here: shown as in progress until the processor's answer arrives by push (#1193). */
  const [resentHandoffIds, setResentHandoffIds] = useState<Set<string>>(new Set());
  const [drawer, setDrawer] = useState<DrawerMode>(null);
  const [dispatchDetails, setDispatchDetails] = useState<Record<string, DownloadDispatchDetail>>({});
  const [importPreviews, setImportPreviews] = useState<Record<string, ImportPreviewResponse>>({});
  const [cleanupPreviews, setCleanupPreviews] = useState<Record<string, DownloadCleanupPreview>>({});
  const [pendingRemoval, setPendingRemoval] = useState<DownloadQueueItem | null>(null);
  const [removalReasonBusy, setRemovalReasonBusy] = useState<RemovalReason | null>(null);
  // #932: select-all on the leftovers list, alongside per-item Keep/Remove.
  const [selectedOrphanPaths, setSelectedOrphanPaths] = useState<Set<string>>(new Set());
  /** Rows picked for Dismiss in Needs attention (#1167): a key per row, see `dismissibleRows`. */
  const [selectedDismissKeys, setSelectedDismissKeys] = useState<Set<string>>(new Set());
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
  // What was last queued from the drawer, so the same file cannot be queued twice (#1171).
  const [manualQueued, setManualQueued] = useState<{ key: string; title: string; destination: string } | null>(null);
  const [manualMessage, setManualMessage] = useState<string | null>(null);
  // DrawerFooter only renders a message for a non-clean state, so the outcome of a
  // preview or a queue has to move the state, not just the text.
  const [manualState, setManualState] = useState<"clean" | "saving" | "saved" | "error">("clean");
  const [searchParams, setSearchParams] = useSearchParams();

  // System -> Health's Import button for a stray item in a processor output
  // folder (#921) lands here with the path already known, rather than
  // asking a person to retype what Health already named.
  useEffect(() => {
    const path = searchParams.get("manualImportPath");
    if (!path) return;
    const mediaType = searchParams.get("manualImportMediaType") === "tv" ? "tv" : "movies";
    setManualImport((current) => ({ ...current, sourcePath: path, mediaType }));
    setManualMessage(null);
    setManualState("clean");
    setDrawer({ kind: "manual" });
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.delete("manualImportPath");
      next.delete("manualImportMediaType");
      return next;
    }, { replace: true });
    // Only ever consumed once, when Health hands a path over -- not every
    // time some other state on this screen changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  // #783: a second mode on the same drawer — one file, typed once (above), or
  // a whole folder, scanned and reviewed row by row.
  const [manualImportMode, setManualImportMode] = useState<"file" | "folder">("file");
  const [folderScanPath, setFolderScanPath] = useState("");
  const [folderScanMediaType, setFolderScanMediaType] = useState("movies");
  const [folderScanRows, setFolderScanRows] = useState<FolderScanRow[]>([]);
  const [folderScanIssues, setFolderScanIssues] = useState<string[]>([]);
  const [folderScanBusy, setFolderScanBusy] = useState(false);
  const [folderScanMessage, setFolderScanMessage] = useState<string | null>(null);

  /* ------------------------------------------------------------- derived */
  const queue = useMemo(
    () => telemetry.clients.flatMap((client) => client.queue.map((item) => ({
      ...item,
      clientProtocol: client.protocol,
      // Until the hand-off exists the processor is not yet being waited for: the download has finished and is
      // being moved or handed over, and the row says that.
      status: item.status === downloadQueueStatuses.waitingForProcessor
        && !processorHandoffs.some((handoff) => handoffDownloadKey(handoff.clientId, handoff.queueItemId) === handoffDownloadKey(item.clientId, item.id))
        ? downloadQueueStatuses.handingOver
        : item.status
    }))),
    [telemetry.clients, processorHandoffs]
  );
  const importJobs = useMemo(() => jobs.filter((job) => job.jobType === "filesystem.import.execute"), [jobs]);
  const importReady = queue.filter((item) => isImportReadyStatus(item.status));
  const processing = queue.filter((item) => isProcessingStatus(item.status));
  // Dead-lettered jobs are failed and out of retries: they are exactly what
  // "Retry N failed" exists for, and were previously excluded (#249). They are
  // asked for by name rather than looked for among the latest jobs, which a
  // failed import drops out of within hours.
  //
  // What needs a person is worked out once, with the sidebar (#556), one row
  // per problem rather than one per record (#557).
  const attention = useMemo(
    () => needsAction({ movieRecovery, seriesRecovery, telemetry, failedJobs, handoffs: processorHandoffs, dispatches, orphans }),
    [movieRecovery, seriesRecovery, telemetry, failedJobs, processorHandoffs, dispatches, orphans]
  );
  const { recoveryCases, queueAttention, failedImportJobs, failedHandoffs: failedProcessorHandoffs, failedDownloads, orphanedDownloads } = attention;
  const selectedRemovableOrphans = orphanedDownloads.filter((orphan) => selectedOrphanPaths.has(orphan.path));
  const activeImportJobs = importJobs.filter((job) => isJobActive(job.status as never)).length;
  const activeProcessorHandoffs = processorHandoffs.filter((handoff) => isWithProcessor(handoff.status));
  // A download that is still in the list above shows its hand-off in its own
  // row (the Stage column), so the Processing connector table keeps only the
  // hand-offs that have no row there: one download, one line (#1162).
  const newestHandoffByDownload = useMemo(() => {
    const map = new Map<string, ProcessorHandoffItem>();
    for (const handoff of processorHandoffs) {
      const key = handoffDownloadKey(handoff.clientId, handoff.queueItemId);
      const known = map.get(key);
      if (!known || handoff.updatedUtc.localeCompare(known.updatedUtc) > 0) map.set(key, handoff);
    }
    return map;
  }, [processorHandoffs]);
  const queueDownloadKeys = useMemo(() => new Set(queue.map((item) => handoffDownloadKey(item.clientId, item.id))), [queue]);
  // ...and a download whose import has started, or that failed, is shown only in that table (#1168).
  const placed = useMemo(
    () => placeDownloadsOnce({
      handoffs: processorHandoffs.filter((handoff) => !queueDownloadKeys.has(handoffDownloadKey(handoff.clientId, handoff.queueItemId))),
      importJobs,
      failedImportJobs,
      failedHandoffs: failedProcessorHandoffs,
      failedDownloads,
      queueAttention
    }),
    [processorHandoffs, queueDownloadKeys, importJobs, failedImportJobs, failedProcessorHandoffs, failedDownloads, queueAttention]
  );
  const handoffsWithoutARow = placed.handoffs;
  const importJobsHere = placed.importJobs;
  const blockedQueueItems = queue.filter((item) => item.healthFindings?.some((finding) => finding.candidateBlocked));
  const healthyClients = telemetry.clients.filter((client) => isHealthyClient(client.healthStatus)).length;
  const needsActionCount = attention.total + heldReleases.length;

  const activity = useMemo(
    () => buildActivity({ importJobs, processorHandoffs, dispatches, telemetry, healthRecords, activityEvents }),
    [importJobs, processorHandoffs, dispatches, telemetry, healthRecords, activityEvents]
  );
  const openQueueItem = drawer?.kind === "queue" ? queue.find((item) => item.id === drawer.id) ?? null : null;
  // #810: whether this item's own client can reorder its queue or force-start
  // a transfer, read straight from the client's own reported capabilities
  // rather than guessed at per protocol, so a control never shows for a
  // client that would just refuse it.
  const openQueueClient = openQueueItem ? telemetry.clients.find((client) => client.clientId === openQueueItem.clientId) ?? null : null;
  const { canReorder, canForceStart } = reorderControlsFor(openQueueClient);
  // The newest hand-off of this download, so its drawer can say what the processor said (#616).
  const openQueueHandoff = openQueueItem
    ? processorHandoffs
      .filter((handoff) => handoff.clientId.toLowerCase() === openQueueItem.clientId.toLowerCase() && handoff.queueItemId.toLowerCase() === openQueueItem.id.toLowerCase())
      .sort((left, right) => right.updatedUtc.localeCompare(left.updatedUtc))[0] ?? null
    : null;
  /** The processor's own reason is said once, in the Import box with its fixes, not again as the client's health (#1151). */
  const processorOwnsFailure = Boolean(openQueueHandoff) && openQueueItem?.status === downloadQueueStatuses.processingFailed;
  /** One rule for the whole drawer: while a hand-off is being sent again, no section shows the old failure (#1204). */
  const resendingHandoff = openQueueItem
    ? isResendingHandoff(openQueueHandoff, resentHandoffIds, openQueueItem.status, openQueueItem.clientId, openQueueItem.id)
    : false;
  const showItemFailure = itemFailureVisible(processorOwnsFailure, resendingHandoff);
  const drawerFindings = openQueueItem ? visibleFindings(openQueueItem, openQueueHandoff, resendingHandoff) : [];
  const openActivity = drawer?.kind === "activity" ? activity.find((entry) => entry.id === drawer.id) ?? null : null;
  const openImportJob = openActivity?.id.startsWith("import:") ? jobs.find((job) => `import:${job.id}` === openActivity.id) ?? null : null;
  const openImportInfo = openImportJob ? parseImportJobPayload(openImportJob.payloadJson) : null;
  const openHandoff = openActivity?.kind === "processor" ? processorHandoffs.find((entry) => `processor:${entry.id}` === openActivity.id) ?? null : null;
  const attentionDetails = useMemo(() => {
    const map = new Map<string, AttentionDetail>();
    for (const row of heldReleases) {
      map.set(`held:${row.historyId}`, {
        title: heldReleaseTitle(row),
        sub: `Waiting for your OK · ${row.mediaType === "movie" ? "Movies" : "TV"}`,
        facts: [
          { label: "Release", value: row.releaseName, mono: true },
          ...(row.quality ? [{ label: "Quality", value: row.quality }] : []),
          ...(row.sizeBytes ? [{ label: "Size", value: formatBytes(row.sizeBytes) }] : []),
          ...(row.indexerName ? [{ label: "From", value: row.indexerName }] : []),
          ...(row.reason ? [{ label: "Why it is held", value: plainSearchResult(row.reason) }] : [])
        ]
      });
    }
    for (const item of recoveryCases) {
      map.set(`recovery:${item.mediaType}:${item.id}`, {
        title: item.title,
        sub: item.mediaType === "movie" ? "Movies" : "TV",
        facts: [
          { label: "What happened", value: item.summary },
          ...(item.recommendedAction ? [{ label: "What to do", value: item.recommendedAction }] : [])
        ]
      });
    }
    for (const job of failedImportJobs) {
      map.set(`job:${job.id}`, {
        title: jobTitle(job),
        sub: "Import job",
        facts: [
          { label: "What happened", value: job.lastError ? plainFailure(job.lastError, "The import") : "The import failed." },
          { label: "Attempts", value: String(job.attempts) }
        ]
      });
    }
    for (const handoff of failedProcessorHandoffs) {
      map.set(`handoff:${handoff.id}`, {
        title: handoff.releaseName,
        sub: handoff.processorName ?? "Processing connector",
        facts: [
          { label: "What happened", value: handoff.failureMessage ? plainFailure(handoff.failureMessage, `Sending it to ${handoff.processorName ?? "the processor"}`) : `Processor reported ${processorStatusLabel(handoff.status)}.` },
          { label: "From", value: handoff.sourcePath, mono: true },
          ...(handoff.outputPath ? [{ label: "Output", value: handoff.outputPath, mono: true }] : [])
        ]
      });
    }
    for (const orphan of orphanedDownloads) {
      map.set(`orphan:${orphan.path}`, {
        title: orphan.name,
        sub: `${orphan.libraryName} download folder`,
        facts: [
          { label: "What happened", value: "Left in the download folder. No client, import or hand-off is using it." },
          { label: "Size", value: formatBytes(orphan.sizeBytes) },
          { label: "Where", value: orphan.path, mono: true }
        ]
      });
    }
    return map;
  }, [heldReleases, recoveryCases, failedImportJobs, failedProcessorHandoffs, orphanedDownloads]);
  const openAttention = drawer?.kind === "attention" ? attentionDetails.get(drawer.id) ?? null : null;
  const openDispatchSummary = drawer?.kind === "dispatch" ? dispatches.find((item) => item.id === drawer.id) ?? null : null;
  const openDispatchDetail = drawer?.kind === "dispatch" ? dispatchDetails[drawer.id] ?? null : null;
  const openFileProvenance = fileProvenanceFromTimeline(openDispatchDetail?.timeline ?? []);

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
        if (!response.ok) throw new Error(await readErrorText(response, "The download action failed."));
      },
      `${actionLabel(action)} sent to ${item.clientName}`
    );
  }

  // Cleanup sheet J1/J2 (#924): removing a download asks why, in plain
  // words, and each choice does exactly what it says. The client action, the
  // blocklist entry, the replacement search and — for "I don't want it" —
  // the unmonitor all happen on the server in this one call now, so there is
  // nothing left to follow up here; the returned message already says so.
  async function removeWithReason(item: DownloadQueueItem, reason: RemovalReason) {
    setRemovalReasonBusy(reason);
    try {
      const result = await submitQueueRemoval(item.clientId, item.id, reason);

      toast.success(result.message);
      setPendingRemoval(null);
      setDrawer(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That removal could not be completed.");
    } finally {
      setRemovalReasonBusy(null);
    }
  }

  // #880/#881: a setup-problem finding (a full disk, permissions, a missing
  // file — never the release's fault) stays on the item until it clears on
  // its own, but a person can dismiss it from here rather than staring at it.
  // Dismissing does not touch the download or the release; it only quiets
  // this one finding for a week, the same server-side rule every other
  // ignored finding already uses.
  async function dismissHealthFinding(item: DownloadQueueItem, kind: string) {
    // #813: a SABnzbd queue-level warning is the client's own state, not a
    // per-item problem Deluno is tracking — dismissing it here has to clear
    // it at SABnzbd (the same call the Connections screen's client row
    // makes), not add this one item to the per-item ignore list, which would
    // leave the warning reappearing on every other item from this client on
    // the next poll.
    if (kind === "sabnzbd-client-warning") {
      return run(
        `warnings:clear:${item.clientId}`,
        async () => {
          const response = await authedFetch(`/api/download-clients/${item.clientId}/warnings/clear`, { method: "POST" });
          if (!response.ok) throw new Error(await readErrorText(response, "That warning could not be cleared."));
        },
        "Cleared this client's warnings"
      );
    }
    return run(
      `dismiss:${item.clientId}:${item.id}:${kind}`,
      async () => {
        const response = await authedFetch(
          `/api/download-clients/${item.clientId}/queue/${item.id}/health/${encodeURIComponent(kind)}/ignore`,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ durationDays: 7 })
          }
        );
        if (!response.ok) throw new Error(await readErrorText(response, "That finding could not be dismissed."));
      },
      "Dismissed for a week, or until it happens again"
    );
  }

  // #817: exempts this one download from every download-health rule, for as
  // long as it is in the client - by its hash (its queue item id, which for
  // a torrent client is the info hash), reviewable and removable from
  // Settings -> Download health. Different from Dismiss above: a dismissed
  // finding still comes back if the same problem happens again; an ignored
  // download never strikes on anything, ever, until this entry is removed.
  async function ignoreDownloadHealth(item: DownloadQueueItem) {
    return run(
      `ignore-download:${item.id}`,
      async () => {
        const response = await authedFetch("/api/download-health/ignore-list", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ hash: item.id, note: item.title || item.releaseName })
        });
        if (!response.ok) throw new Error(await readErrorText(response, "That download could not be added to the ignore list."));
      },
      "Added to the ignore list — exempt from every health rule until removed"
    );
  }

  async function previewImport(item: DownloadQueueItem) {
    await run(`preview:${item.id}`, async () => {
      const sourcePath = resolveImportSourcePath(item, libraries);
      if (!sourcePath) throw new Error("The download client has not reported a completed file location, and this library has no folder override.");
      const dispatch = findDispatchForQueueItem(item, dispatches);
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildImportRequest(item, libraries, dispatch))
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
        const dispatch = findDispatchForQueueItem(item, dispatches);
        const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            preview: buildImportRequest(item, libraries, dispatch),
            transferMode: "auto",
            overwrite: false,
            allowCopyFallback: true,
            forceReplacement: false,
            dispatchId: dispatch?.id ?? null
          })
        });
        setImportPreviews((current) => ({ ...current, [item.id]: result.preview }));
        // The row shows the import's outcome as soon as the click returns, with no wait for the next read (#1209).
        applyImportOutcome(queryClient, item.id, result.job);
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
    await run(`recovery:retry:${item.id}`, async () => {
      const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/retry`, { method: "POST" });
      // The server says what it did for this kind of case, or why there is
      // nothing to do yet (#558), and that is what the person needs to read.
      if (!response.ok) throw new Error(await readErrorText(response, "That could not be tried again."));
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      toast.success(body?.message ?? `${item.title} is being tried again.`);
    });
  }

  /** Stop wanting it (#927): offered beside Search for another on a vanished download's own case. */
  async function stopWantingRecovery(item: (typeof recoveryCases)[number]) {
    const collection = item.mediaType === "movie" ? "movies" : "series";
    await run(`recovery:stop-wanting:${item.id}`, async () => {
      const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/stop-wanting`, { method: "POST" });
      if (!response.ok) throw new Error(await readErrorText(response, "That could not be stopped."));
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      toast.success(body?.message ?? `Deluno stopped wanting ${item.title}.`);
    });
  }

  /** Import the original, unprocessed download (#919): offered beside Run the processor again and Search for another on a processor-fault case. */
  async function importOriginalRecovery(item: (typeof recoveryCases)[number]) {
    const collection = item.mediaType === "movie" ? "movies" : "series";
    await run(`recovery:import-original:${item.id}`, async () => {
      const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/import-original`, { method: "POST" });
      if (!response.ok) throw new Error(await readErrorText(response, "The original could not be imported."));
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      toast.success(body?.message ?? `${item.title} will be imported unprocessed.`);
    });
  }

  /** Search for another release (#919): never blocklists -- Checkpoint A already showed this release is good, only the processor's copy of it is at fault. */
  async function searchAgainRecovery(item: (typeof recoveryCases)[number]) {
    const collection = item.mediaType === "movie" ? "movies" : "series";
    await run(`recovery:search-again:${item.id}`, async () => {
      const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/search-again`, { method: "POST" });
      if (!response.ok) throw new Error(await readErrorText(response, "Deluno could not search for another release."));
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      toast.success(body?.message ?? `Searching for another release for ${item.title}.`);
    });
  }

  /** Keep or remove one orphaned download folder (#588). Removing is only offered for what Deluno downloaded. */
  async function actOnOrphan(orphan: OrphanedDownloadItem, action: "keep" | "remove") {
    await run(`orphan:${action}:${orphan.path}`, async () => {
      const response = await authedFetch(`/api/download-clients/orphans/${action}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ path: orphan.path })
      });
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error(body?.message || "That could not be done.");
      toast.success(body?.message ?? (action === "keep" ? `Kept ${orphan.name}.` : `Removed ${orphan.name}.`));
    });
  }

  function toggleOrphanSelected(path: string, checked: boolean) {
    setSelectedOrphanPaths((current) => {
      const next = new Set(current);
      if (checked) next.add(path);
      else next.delete(path);
      return next;
    });
  }

  /** #932: select-all on the leftovers list. Only what Deluno can prove it downloaded is ever selectable. */
  function selectAllRemovableOrphans(removable: OrphanedDownloadItem[]) {
    setSelectedOrphanPaths(new Set(removable.map((orphan) => orphan.path)));
  }

  function clearOrphanSelection() {
    setSelectedOrphanPaths(new Set());
  }

  /** #932: delete every selected leftover in one action, the same per-item rule (Deluno's own grabs only), one at a time so one failure does not stop the rest. */
  async function removeSelectedOrphans(selected: OrphanedDownloadItem[]) {
    await run("orphan:remove-selected", async () => {
      let removed = 0;
      const failures: string[] = [];
      for (const orphan of selected) {
        const response = await authedFetch("/api/download-clients/orphans/remove", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ path: orphan.path })
        });
        if (response.ok) {
          removed += 1;
        } else {
          const body = (await response.json().catch(() => null)) as { message?: string } | null;
          failures.push(body?.message ?? `${orphan.name} could not be removed.`);
        }
      }
      setSelectedOrphanPaths(new Set());
      if (failures.length > 0) {
        throw new Error(
          removed > 0
            ? `Removed ${removed} of ${selected.length}. ${failures[0]}`
            : failures[0]
        );
      }
      toast.success(`Removed ${removed} leftover download${removed === 1 ? "" : "s"}.`);
    });
  }

  async function dismissFailedImport(job: JobQueueItem) {
    await run(`job:dismiss:${job.id}`, () => dismissFailedJob(job.id), `${jobTitle(job)} dismissed`);
  }

  async function dismissRecoveryRequest(item: (typeof recoveryCases)[number]) {
    const collection = item.mediaType === "movie" ? "movies" : "series";
    const response = await authedFetch(`/api/${collection}/import-recovery/${item.id}/dismiss`, { method: "POST" });
    if (!response.ok) throw new Error("That case could not be dismissed.");
  }

  async function dismissRecovery(item: (typeof recoveryCases)[number]) {
    await run(`recovery:dismiss:${item.id}`, () => dismissRecoveryRequest(item), `${item.title} dismissed`);
  }

  async function importOriginal(handoff: ProcessorHandoffItem) {
    await run(
      `original:${handoff.id}`,
      async () => {
        const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/import-original`, { method: "POST" });
        if (!response.ok) {
          const body = (await response.json().catch(() => null)) as { message?: string } | null;
          throw new Error(body?.message || "The original could not be queued for import.");
        }
      },
      `${handoff.releaseName} will be imported unprocessed`
    );
  }

  async function dismissDispatchRequest(dispatch: DownloadDispatchItem) {
    const response = await authedFetch(`/api/v1/download-dispatches/${encodeURIComponent(dispatch.id)}?reason=dismissed`, { method: "DELETE" });
    if (!response.ok && response.status !== 404) throw new Error("That download could not be dismissed.");
  }

  async function dismissDispatch(dispatch: DownloadDispatchItem) {
    const done = await run(`dispatch:dismiss:${dispatch.id}`, () => dismissDispatchRequest(dispatch), `${dispatch.releaseName} dismissed`);
    if (done) setDrawer(null);
  }

  async function dismissHandoffRequest(handoff: ProcessorHandoffItem) {
    const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/dismiss`, { method: "POST" });
    if (!response.ok) {
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      throw new Error(body?.message || "That hand-off could not be dismissed.");
    }
  }

  async function approveHeld(row: HeldReleaseRow) {
    await run(`held:approve:${row.historyId}`, () => approveHeldRelease(row), `Sent ${row.releaseName} to your download client.`);
  }

  async function findAnotherHeld(row: HeldReleaseRow) {
    await run(`held:another:${row.historyId}`, () => findAnotherForHeldRelease(row), `${row.releaseName} will not be used again. Searching for another.`);
  }

  async function dismissHeld(row: HeldReleaseRow) {
    await run(`held:dismiss:${row.historyId}`, () => dismissHeldRelease(row), `${heldReleaseTitle(row)} dismissed`);
  }

  async function dismissHandoff(handoff: ProcessorHandoffItem) {
    const done = await run(`handoff:dismiss:${handoff.id}`, () => dismissHandoffRequest(handoff), `${handoff.releaseName} dismissed`);
    if (done) setDrawer(null);
  }

  /**
   * What can be done about a hand-off the processor did not finish: one place, used by the download's own drawer and
   * by the hand-off drawer, each action with a line saying what it does (#1151).
   */
  function handoffFixes(handoff: ProcessorHandoffItem) {
    const processor = handoff.processorName ?? "the processor";
    const busy = busyKey !== null;
    const spin = (key: string) => (busyKey === key ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null);
    const line = (button: React.ReactNode, caption: string) => (
      <div className="grid gap-1">
        <div>{button}</div>
        <p className="text-(length:--type-caption) text-muted-foreground">{caption}</p>
      </div>
    );
    return (
      <div className="grid gap-3">
        {handoff.status.toLowerCase() === SOURCE_GONE
          ? (
            <>
              <p className="text-(length:--type-caption) text-muted-foreground">
                The download was gone before {processor} could clean it, so there is nothing to send again or import.
              </p>
              {line(
                <Button type="button" variant="outline" size="sm" onClick={() => void searchAgainForHandoff(handoff, "copy")} disabled={busy}>
                  {spin(`handoff:search-again:${handoff.id}`)}
                  Search for another copy
                </Button>,
                "Looks for a different copy of this title right now. Nothing is blocklisted."
              )}
            </>
          )
          : null}
        {canImportOriginal(handoff.status)
          ? (
            <>
              {handoffCanBeSentAgain(handoff)
                ? line(
                  <Button type="button" size="sm" onClick={() => void retryHandoff(handoff)} disabled={busy}>
                    {spin(`handoff:${handoff.id}`)}
                    Send to {processor} again
                  </Button>,
                  `Sends the finished download to ${processor} again, the same way as the first time.`
                )
                : (
                  <p className="text-(length:--type-caption) text-muted-foreground">
                    {processor} already made the processed file for {handoff.releaseName}, so there is nothing to send again.
                  </p>
                )}
              {line(
                <Button type="button" variant="outline" size="sm" onClick={() => void importOriginal(handoff)} disabled={busy}>
                  {spin(`original:${handoff.id}`)}
                  Import original
                </Button>,
                "Brings the download in as it is, without processing."
              )}
              {line(
                <Button type="button" variant="outline" size="sm" onClick={() => void searchAgainForHandoff(handoff)} disabled={busy}>
                  {spin(`handoff:search-again:${handoff.id}`)}
                  Search for another release
                </Button>,
                "Gives up on this release, takes its download out of your client with its files, never picks it again, and searches for a different one right now."
              )}
              {line(
                <Button type="button" variant="outline" size="sm" onClick={() => void keepWaiting(handoff)} disabled={busy}>
                  {spin(`handoff:keep-waiting:${handoff.id}`)}
                  Keep waiting
                </Button>,
                `Goes on watching ${processor} for a while longer without sending anything again.`
              )}
            </>
          )
          : null}
        {handoffNeedsPerson(handoff)
          ? line(
            <Button type="button" variant="outline" size="sm" aria-label="Dismiss this hand-off" onClick={() => void dismissHandoff(handoff)} disabled={busy}>
              {spin(`handoff:dismiss:${handoff.id}`)}
              Dismiss
            </Button>,
            "Stops counting this as needing you. What happened stays here and in Activity."
          )
          : null}
      </div>
    );
  }

  /** Every row of Needs attention that Dismiss clears, with the request that clears it (#1167). */
  const dismissibleRows: { key: string; title: string; dismiss: () => Promise<void> }[] = [
    ...heldReleases.map((row) => ({ key: `held:${row.historyId}`, title: heldReleaseTitle(row), dismiss: () => dismissHeldRelease(row) })),
    ...recoveryCases.map((item) => ({ key: `recovery:${item.mediaType}:${item.id}`, title: item.title, dismiss: () => dismissRecoveryRequest(item) })),
    ...failedImportJobs.map((job) => ({ key: `job:${job.id}`, title: jobTitle(job), dismiss: () => dismissFailedJob(job.id).then(() => undefined) })),
    ...failedProcessorHandoffs.map((handoff) => ({ key: `handoff:${handoff.id}`, title: handoff.releaseName, dismiss: () => dismissHandoffRequest(handoff) })),
    ...failedDownloads.map(({ dispatch }) => ({ key: `dispatch:${dispatch.id}`, title: dispatch.releaseName, dismiss: () => dismissDispatchRequest(dispatch) }))
  ];
  const selectedDismissRows = dismissibleRows.filter((row) => selectedDismissKeys.has(row.key));
  /** Two rows can name the same release, and a button's name must be unique on screen. */
  const selectLabels = (() => {
    const seen = new Map<string, number>();
    const totals = new Map<string, number>();
    for (const row of dismissibleRows) totals.set(row.title, (totals.get(row.title) ?? 0) + 1);
    return new Map(dismissibleRows.map((row) => {
      const n = (seen.get(row.title) ?? 0) + 1;
      seen.set(row.title, n);
      return [row.key, (totals.get(row.title) ?? 0) > 1 ? `Select ${row.title} (${n})` : `Select ${row.title}`] as const;
    }));
  })();

  /** The header counts every row; Select all covers the ones Dismiss clears, and the rest say how they are cleared from their own row. */
  const otherRowsNote = (() => {
    const parts: string[] = [];
    if (orphanedDownloads.length) parts.push(`${orphanedDownloads.length} leftover ${orphanedDownloads.length === 1 ? "folder" : "folders"} (Remove or Keep)`);
    if (queueAttention.length) parts.push(`${queueAttention.length} live ${queueAttention.length === 1 ? "download" : "downloads"} (open one to fix)`);
    return parts.length ? `. ${dismissibleRows.length} can be dismissed together; ${parts.join(" and ")} are cleared from their own rows` : "";
  })();

  function toggleDismissSelected(key: string, checked: boolean) {
    setSelectedDismissKeys((current) => {
      const next = new Set(current);
      if (checked) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  /** Select all, then Dismiss (#1167): one at a time, so one failure does not stop the rest. */
  async function dismissSelected(rows: typeof dismissibleRows) {
    await run("needs:dismiss-selected", async () => {
      let dismissed = 0;
      let firstFailure: string | null = null;
      for (const row of rows) {
        try {
          await row.dismiss();
          dismissed += 1;
        } catch (error) {
          firstFailure ??= error instanceof Error ? error.message : `${row.title} could not be dismissed.`;
        }
      }
      setSelectedDismissKeys(new Set());
      if (firstFailure) throw new Error(dismissed > 0 ? `Dismissed ${dismissed} of ${rows.length}. ${firstFailure}` : firstFailure);
      toast.success(`Dismissed ${dismissed} ${dismissed === 1 ? "thing" : "things"}.`);
    });
  }

  async function retryHandoff(handoff: ProcessorHandoffItem) {
    await run(
      `handoff:${handoff.id}`,
      async () => {
        const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/retry`, { method: "POST" });
        if (!response.ok) {
          const body = (await response.json().catch(() => null)) as { message?: string } | null;
          // 409: the server has closed this hand-off already (its processed file is there). Say so rather than "failed".
          throw new Error(body?.message || (response.status === 409
            ? `${handoff.releaseName} cannot be sent again: it is finished or already has its processed file.`
            : "The processor hand-off could not be tried again."));
        }
      },
      `Sent to ${handoff.processorName ?? "the processor"} again.`
    ).then((sent) => {
      if (sent) setResentHandoffIds((current) => new Set(current).add(handoff.id).add(resentItemKey(handoff.clientId, handoff.queueItemId)));
    });
  }

  /** Say again what became of the file, from the start (#580). */
  async function sendOutcomeAgain(handoff: ProcessorHandoffItem) {
    await run(`handoff:outcome:${handoff.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/outcome/send-again`, { method: "POST" });
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error(body?.message || "Deluno could not say it again.");
      toast.success(body?.message ?? `Deluno told ${handoff.processorName ?? "the processor"} again.`);
    });
  }

  /**
   * Stop waiting for a processor that is never going to finish this one (#566).
   * The server asks it to drop the file and ends the hand-off, so it can then be
   * dismissed or the original imported like any other ending.
   */
  async function stopWaiting(handoff: ProcessorHandoffItem) {
    await run(`handoff:stop:${handoff.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/stop-waiting`, { method: "POST" });
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error(body?.message || "Deluno could not stop waiting for this hand-off.");
      toast.success(body?.message ?? `Deluno stopped waiting for ${handoff.releaseName}.`);
    });
  }

  /**
   * Give up on this release entirely (#914, #929): blocklists it, ends the
   * hand-off, and starts a search for a replacement, instead of waiting on
   * the same processor or the same download any longer.
   */
  async function searchAgainForHandoff(handoff: ProcessorHandoffItem, what = "release") {
    const done = await run(`handoff:search-again:${handoff.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/search-again`, { method: "POST" });
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error(body?.message || `Deluno could not search for another ${what}.`);
      toast.success(body?.message ?? `Deluno is searching for a replacement for ${handoff.releaseName}.`);
    });
    if (done) setDrawer(null);
  }

  /**
   * Keep waiting for a hand-off Deluno already gave up on, with a fresh
   * limit, instead of importing the original or resubmitting to the
   * processor (#914/#929).
   */
  async function keepWaiting(handoff: ProcessorHandoffItem) {
    await run(`handoff:keep-waiting:${handoff.id}`, async () => {
      const response = await authedFetch(`/api/integrations/processors/handoffs/${handoff.id}/keep-waiting`, { method: "POST" });
      const body = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error(body?.message || "Deluno could not keep waiting for this hand-off.");
      toast.success(body?.message ?? `Deluno will keep waiting for ${handoff.releaseName}.`);
    });
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

  const manualKey = [manualImport.sourcePath.trim(), manualImport.fileName.trim(), manualImport.mediaType, manualImport.title.trim(), manualImport.year].join("|");

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
        const landing = result.preview.destinationPath || result.preview.destinationFolder;
        setManualQueued({ key: manualKey, title: result.preview.matchedTitle ?? (manualImport.title.trim() || "Your file"), destination: landing });
        setManualMessage("Queued. Deluno is importing it now.");
        revalidator.revalidate();
      }
    } catch (error) {
      setManualState("error");
      setManualMessage(error instanceof Error ? error.message : "That could not be completed.");
    } finally {
      setBusyKey(null);
    }
  }

  function scanRowToState(item: ImportFolderScanItem): FolderScanRow {
    return {
      sourcePath: item.sourcePath,
      fileName: item.fileName,
      title: item.guessedTitle ?? "",
      year: item.guessedYear ? String(item.guessedYear) : "",
      matched: item.matched,
      preview: item.preview,
      include: item.matched,
      checking: false
    };
  }

  /** Scans a folder for every video file inside it and previews each one's best guess (#783). */
  async function scanFolder() {
    if (!folderScanPath.trim()) {
      setFolderScanMessage("Choose a folder first.");
      return;
    }
    setFolderScanBusy(true);
    setFolderScanMessage(null);
    try {
      const response = await fetchJson<ImportFolderScanResponse>("/api/filesystem/import/scan-folder", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ folderPath: folderScanPath.trim(), mediaType: folderScanMediaType })
      });
      setFolderScanRows(response.items.map(scanRowToState));
      setFolderScanIssues(response.issues);
      setFolderScanMessage(
        response.items.length
          ? `Found ${response.items.length} file${response.items.length === 1 ? "" : "s"}. Review each match before importing.`
          : "No supported video files were found in this folder."
      );
    } catch (error) {
      setFolderScanRows([]);
      setFolderScanMessage(error instanceof Error ? error.message : "That folder could not be scanned.");
    } finally {
      setFolderScanBusy(false);
    }
  }

  /** Re-runs a single row's preview after its title/year was corrected by hand. */
  async function recheckFolderScanRow(index: number) {
    const row = folderScanRows[index];
    if (!row) return;
    setFolderScanRows((current) => current.map((item, i) => (i === index ? { ...item, checking: true } : item)));
    try {
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          sourcePath: row.sourcePath,
          fileName: null,
          mediaType: folderScanMediaType,
          title: row.title.trim() || null,
          year: row.year ? Number(row.year) : null,
          genres: null,
          tags: null,
          studio: null,
          originalLanguage: null
        } satisfies ImportPreviewRequest)
      });
      setFolderScanRows((current) =>
        current.map((item, i) =>
          i === index
            ? { ...item, preview, matched: Boolean(row.title.trim()), include: Boolean(row.title.trim()), checking: false }
            : item
        )
      );
    } catch (error) {
      setFolderScanRows((current) => current.map((item, i) => (i === index ? { ...item, checking: false } : item)));
      toast.error(error instanceof Error ? error.message : "That row could not be re-checked.");
    }
  }

  /** Imports every included, reviewed row in one action rather than one file at a time (#783). */
  async function importReviewedFolderRows() {
    const included = folderScanRows.filter((row) => row.include);
    if (included.length === 0) {
      setFolderScanMessage("Nothing is selected to import.");
      return;
    }
    setFolderScanBusy(true);
    setFolderScanMessage(null);
    try {
      const items: ImportExecuteRequest[] = included.map((row) => ({
        preview: {
          sourcePath: row.sourcePath,
          fileName: null,
          mediaType: folderScanMediaType,
          title: row.title.trim() || null,
          year: row.year ? Number(row.year) : null,
          genres: null,
          tags: null,
          studio: null,
          originalLanguage: null
        },
        transferMode: manualImport.transferMode,
        overwrite: false,
        allowCopyFallback: true,
        forceReplacement: false
      }));

      const result = await fetchJson<ImportBatchExecuteResponse>("/api/filesystem/import/execute-batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ items })
      });

      const failed = result.results.filter((item) => !item.succeeded);
      setFolderScanMessage(
        failed.length === 0
          ? `Imported ${result.importedCount} of ${result.requestedCount} file${result.requestedCount === 1 ? "" : "s"}.`
          : `Imported ${result.importedCount} of ${result.requestedCount}. ${failed.length} failed: ${failed[0]!.message}`
      );
      // Drop the rows that succeeded; a person reviewing a failure should not
      // have to re-find it among rows that are already done.
      setFolderScanRows((current) =>
        current.filter((row) => !result.results.some((item) => item.sourcePath === row.sourcePath && item.succeeded))
      );
      revalidator.revalidate();
    } catch (error) {
      setFolderScanMessage(error instanceof Error ? error.message : "The batch could not be imported.");
    } finally {
      setFolderScanBusy(false);
    }
  }

  /* -------------------------------------------------------------- render */
  return (
    <div className="flex flex-col gap-(--page-gap)">
      <PageToolbar
        actions={
          <>
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
          { label: "Needs action", value: String(needsActionCount), help: blockedQueueItems.length ? `${blockedQueueItems.length} release${blockedQueueItems.length === 1 ? "" : "s"} blocked` : needsActionCount ? "see below" : "all clear", tone: needsActionCount ? "warning" : undefined },
          { label: "Clients", value: `${healthyClients}/${telemetry.clients.length}`, help: healthyClients === telemetry.clients.length ? "all responding" : "one is not responding", tone: healthyClients < telemetry.clients.length ? "danger" : undefined }
        ]}
      />

      <ListCard
        title="Media pipeline"
        count={queue.length ? `${queue.length} in flight · ${activeImportJobs} importing` : undefined}
        actions={
          <ColumnPicker
            label="Show columns"
            columns={QUEUE_OPTIONAL_COLUMNS}
            isVisible={queueColumns.isVisible}
            onToggle={queueColumns.toggle}
          />
        }
      >
        <p className="border-b border-hairline px-(--card-pad-x) py-2 text-(length:--type-caption) text-muted-foreground">
          Deluno only shows downloads it sent.
        </p>
        {queue.length === 0 ? (
          <ListEmpty
            title="Nothing in motion"
            description="Media appears here from the moment Deluno sends it to a download client through processing, naming, and import."
          />
        ) : (
          <ListTable
            listId="transfers-queue"
            items={queue}
            columns={[
              { id: "media", label: "Media", sort: (item) => (item.title || item.releaseName).toLowerCase() },
              { id: "stage", label: "Stage", width: "minmax(0,1.25fr)", sort: (item) => pipelineStage(item) },
              { id: "progress", label: "Progress", width: "minmax(0,1.2fr)", sort: (item) => item.progress },
              { id: "speed", label: "Speed / left", mobile: true, sort: (item) => item.speedMbps },
              { id: "client", label: "Client", sort: (item) => item.clientName.toLowerCase() },
              ...(queueColumns.isVisible("quality") ? [{ id: "quality", label: "Quality", sort: (item: DownloadQueueItem) => queueRowExtras(item, findDispatchForQueueItem(item, dispatches), false).quality }] : []),
              ...(queueColumns.isVisible("customFormat") ? [{ id: "customFormat", label: "Custom format", sort: (item: DownloadQueueItem) => queueRowExtras(item, findDispatchForQueueItem(item, dispatches), false).customFormat }] : []),
              ...(queueColumns.isVisible("language") ? [{ id: "language", label: "Language", sort: (item: DownloadQueueItem) => queueRowExtras(item, findDispatchForQueueItem(item, dispatches), false).language }] : []),
              ...(queueColumns.isVisible("size") ? [{ id: "size", label: "Size", align: "end" as const, sort: (item: DownloadQueueItem) => item.sizeBytes }] : []),
              ...(queueColumns.isVisible("timeleft") ? [{ id: "timeleft", label: "Timeleft", sort: (item: DownloadQueueItem) => (item.etaSeconds > 0 ? item.etaSeconds : null) }] : []),
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (item) => { const chip = queueChip(item); return toneSortValue(chip.tone, chip.label); } }
            ]}
          >
            {(rows) => rows.map((item) => {
              const chip = queueChip(item);
              // Colour follows what needs a person, not what finished. A
              // completed transfer used to be the brightest thing on the page —
              // a full saturated bar at 100% and 0.0 MB/s — while a failure sat
              // in plain text below it (#263).
              const transferring = item.speedMbps > 0 || (item.progress > 0 && item.progress < 100);
              const needsAttention = chip.tone === "bad" || chip.tone === "warn" || Boolean(item.failure);
              const rowDispatch = findDispatchForQueueItem(item, dispatches);
              const extras = queueRowExtras(item, rowDispatch, transferring);
              const rowHandoff = item.failure || item.errorMessage ? undefined : newestHandoffByDownload.get(handoffDownloadKey(item.clientId, item.id));
              return (
                <ListRow key={`${item.clientId}:${item.id}`} onClick={() => setDrawer({ kind: "queue", id: item.id })} selected={openQueueItem?.id === item.id}>
                  <ListNameCell
                    name={item.title || item.releaseName}
                    sub={<span className="font-mono">{item.releaseName}</span>}
                  />
                  <ListCell
                    primary={pipelineStage(item)}
                    secondary={rowHandoff ? `${rowHandoff.processorName ?? "Processor"}: ${processorStageLabel(rowHandoff.status)}${rowHandoff.failureMessage ? ` - ${rowHandoff.failureMessage}` : ""}` : pipelineDetail(item)}
                  />
                  <ListCell>
                    {transferring ? (
                      <>
                        <ProgressBar value={item.progress} />
                        <span className="mt-1 block truncate text-(length:--type-caption) tabular-nums text-muted-foreground">
                          {Math.round(item.progress)}% of {formatBytes(item.sizeBytes)}
                        </span>
                      </>
                    ) : (
                      // Finished, queued or stalled: a bar would say "in
                      // progress" about something that is not.
                      <span className="block truncate text-(length:--type-caption) text-muted-foreground">
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
                  {queueColumns.isVisible("quality") ? (
                    <ListCell primary={extras.quality} />
                  ) : null}
                  {queueColumns.isVisible("customFormat") ? (
                    <ListCell numeric primary={extras.customFormat} />
                  ) : null}
                  {queueColumns.isVisible("language") ? (
                    <ListCell primary={extras.language} />
                  ) : null}
                  {queueColumns.isVisible("size") ? (
                    <ListCell numeric primary={extras.size} />
                  ) : null}
                  {queueColumns.isVisible("timeleft") ? (
                    <ListCell primary={extras.timeleft} />
                  ) : null}
                  <ListCell mobile>
                    <Chip tone={needsAttention ? chip.tone : transferring ? "info" : "idle"}>{chip.label}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        )}
      </ListCard>

      {handoffsWithoutARow.length || processorConnections.length ? (
        <ListCard
          title="Processing connector"
          count={activeProcessorHandoffs.length ? `${activeProcessorHandoffs.length} active · ${processorConnections.length} configured` : `${processorConnections.length} configured · latest ${processorHandoffs.length}`}
        >
          {processorConnections.length ? (
            <div className="flex flex-wrap gap-2 border-b border-hairline px-(--card-pad-x) py-3">
              {processorConnections.map((connection) => (
                <Chip key={connection.id} tone={processorConnectionTone(connection)}>
                  {connection.name} · {processorConnectionStatus(connection)}
                </Chip>
              ))}
            </div>
          ) : null}
          {handoffsWithoutARow.length ? (
            <ListTable
              listId="transfers-processing"
              items={handoffsWithoutARow}
              columns={[
                { id: "media", label: "Media", sort: (handoff) => handoff.releaseName.toLowerCase() },
                { id: "connector", label: "Connector", sort: (handoff) => (handoff.processorName ?? "Configured processor").toLowerCase() },
                { id: "source", label: "Source / output", width: "minmax(0,1.8fr)", sort: (handoff) => (handoff.outputPath ?? handoff.sourcePath).toLowerCase() },
                { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (handoff) => toneSortValue(processorTone(handoff), processorStatusLabel(handoff.status)) }
              ]}
            >
              {(rows) => rows.map((handoff) => (
                <ListRow key={handoff.id} onClick={() => setDrawer({ kind: "activity", id: `processor:${handoff.id}` })} selected={openActivity?.id === `processor:${handoff.id}`}>
                  <ListNameCell name={handoff.releaseName} sub={handoff.mediaType === "tv" ? "TV" : "Movies"} />
                  {/* The stage and the Status chip said the same words in every row, so the chip alone carries them; a failure's reason goes under the connector. */}
                  <ListCell primary={handoff.processorName ?? "Configured processor"} secondary={handoff.failureMessage ?? connectorSubLine(handoff, processorSays(handoff, (utc) => formatPreferenceDateTime(utc, preferences)))} />
                  <ListCell primary={handoff.outputPath ?? handoff.sourcePath} secondary={handoff.outputPath ? `From ${handoff.sourcePath}` : "Source hand-off"} />
                  <ListCell mobile><Chip tone={processorTone(handoff)}>{processorStatusLabel(handoff.status)}</Chip></ListCell>
                </ListRow>
              ))}
            </ListTable>
          ) : (
            processorHandoffs.length ? (
            <ListEmpty title="Everything with the processor is listed elsewhere on this page" description="A download shows what the processor is doing in its own row, or sits with its import or its problem." />
          ) : (
            <ListEmpty title="No media is being processed" description="The connector is configured. When Deluno hands media to it, its status and returned output will appear here." />
          )
          )}
        </ListCard>
      ) : null}

      {importJobsHere.length ? (
        <ListCard
          title="Import & naming"
          count={`${activeImportJobs} active · latest ${importJobsHere.length}`}
        >
          <ListTable
            listId="transfers-imports"
            items={importJobsHere}
            columns={[
              { id: "media", label: "Media", sort: (job) => (parseImportJobPayload(job.payloadJson)?.title || parseImportJobPayload(job.payloadJson)?.fileName || jobTitle(job)).toLowerCase() },
              { id: "destination", label: "Source / destination", width: "minmax(0,2fr)", sort: (job) => (parseImportJobPayload(job.payloadJson)?.destinationPath ?? "").toLowerCase() },
              { id: "stage", label: "Stage", sort: (job) => importJobStage(job) },
              { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (job) => toneSortValue(importJobTone(job), isJobRetrying(job) ? "retrying" : job.status) }
            ]}
          >
            {(rows) => rows.map((job) => {
              const info = parseImportJobPayload(job.payloadJson);
              // The destination is not in the payload: naming decides it as the
              // file moves, and the download it belongs to then knows where it
              // landed (#564).
              const landed = info?.dispatchId
                ? dispatches.find((dispatch) => dispatch.id === info.dispatchId)?.importedFilePath ?? null
                : null;
              return (
                <ListRow key={job.id} onClick={() => setDrawer({ kind: "activity", id: `import:${job.id}` })} selected={openActivity?.id === `import:${job.id}`}>
                  <ListNameCell name={info?.title || info?.fileName || jobTitle(job)} sub={info?.fileName || "Filesystem import"} />
                  <ListCell primary={info?.destinationPath || landed || "Named as it imports"} secondary={info?.sourcePath ? `From ${info.sourcePath}` : "Source path not recorded"} />
                  <ListCell primary={importJobStage(job)} secondary={job.attempts > 1 ? `Attempt ${job.attempts}` : undefined} />
                  <ListCell mobile><Chip tone={importJobTone(job)}>{isJobRetrying(job) ? "retrying" : job.status}</Chip></ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        </ListCard>
      ) : null}

      {needsActionCount > 0 ? (
        <ListCard
          title="Needs attention"
          count={`${needsActionCount} ${needsActionCount === 1 ? "thing" : "things"} Deluno could not finish on its own${otherRowsNote}`}
          actions={
            <>
              {dismissibleRows.length ? (
                selectedDismissRows.length > 0 ? (
                  <>
                    <Button type="button" variant="outline" size="sm" onClick={() => setSelectedDismissKeys(new Set())} disabled={busyKey !== null}>
                      Clear dismiss selection
                    </Button>
                    <Button type="button" variant="outline" size="sm" onClick={() => void dismissSelected(selectedDismissRows)} disabled={busyKey !== null}>
                      {busyKey === "needs:dismiss-selected" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                      Dismiss {selectedDismissRows.length} selected
                    </Button>
                  </>
                ) : (
                  <Button type="button" variant="outline" size="sm" onClick={() => setSelectedDismissKeys(new Set(dismissibleRows.map((row) => row.key)))} disabled={busyKey !== null}>
                    Select all {dismissibleRows.length} to dismiss
                  </Button>
                )
              ) : null}
              {failedImportJobs.length ? (
                <Button type="button" variant="outline" size="sm" onClick={() => void retryFailedJobs()} disabled={busyKey !== null}>
                  {busyKey === "jobs:retry-failed" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RotateCw className="h-3.5 w-3.5" />}
                  Retry {failedImportJobs.length} failed
                </Button>
              ) : null}
              {orphanedDownloads.length ? (
                selectedRemovableOrphans.length > 0 ? (
                  <>
                    <Button type="button" variant="outline" size="sm" onClick={clearOrphanSelection} disabled={busyKey !== null}>
                      Clear selection
                    </Button>
                    <Button type="button" variant="outline" size="sm" onClick={() => void removeSelectedOrphans(selectedRemovableOrphans)} disabled={busyKey !== null}>
                      {busyKey === "orphan:remove-selected" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                      Remove {selectedRemovableOrphans.length} selected
                    </Button>
                  </>
                ) : (
                  <Button type="button" variant="outline" size="sm" onClick={() => selectAllRemovableOrphans(orphanedDownloads)} disabled={busyKey !== null}>
                    Select all leftovers
                  </Button>
                )
              ) : null}
            </>
          }
        >
          <ListTable
            listId="transfers-needs-attention"
            columns={[
              { id: "item", label: "Item", sortable: false },
              { id: "what", label: "What happened", width: "minmax(0,1.8fr)", sortable: false },
              { id: "fix", label: "Fix", width: "300px", mobile: true, srOnly: true }
            ]}
            chevron={false}
          >
            {heldReleases.map((row) => (
              <ListRow key={`held:${row.historyId}`} className={ATTENTION_ROW_PHONE} onClick={() => setDrawer({ kind: "attention", id: `held:${row.historyId}` })}>
                <ListNameCell name={heldReleaseTitle(row)} sub={row.mediaType === "movie" ? "Movies" : "TV"} />
                <ListCell
                  mobile
                  primary={`Waiting for your OK: ${row.releaseName}`}
                  secondary={[
                    row.quality,
                    row.sizeBytes ? formatBytes(row.sizeBytes) : null,
                    row.indexerName ? `from ${row.indexerName}` : null,
                    row.reason ? plainSearchResult(row.reason) : null
                  ].filter(Boolean).join(" · ")}
                />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="flex flex-wrap items-center justify-end gap-2 max-md:justify-start" onClick={(event) => event.stopPropagation()}>
                    <Switch size="sm" aria-label={selectLabels.get(`held:${row.historyId}`)} checked={selectedDismissKeys.has(`held:${row.historyId}`)} onCheckedChange={(checked) => toggleDismissSelected(`held:${row.historyId}`, checked)} disabled={busyKey !== null} />
                    <Button type="button" size="sm" aria-label={`Approve ${row.releaseName}`} onClick={() => void approveHeld(row)} disabled={busyKey !== null}>
                      {busyKey === `held:approve:${row.historyId}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                      Approve
                    </Button>
                    <Button type="button" variant="outline" size="sm" aria-label={`Find another for ${heldReleaseTitle(row)}`} onClick={() => void findAnotherHeld(row)} disabled={busyKey !== null}>
                      {busyKey === `held:another:${row.historyId}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                      Find another
                    </Button>
                    <Button type="button" variant="outline" size="sm" aria-label={`Dismiss ${row.releaseName}`} onClick={() => void dismissHeld(row)} disabled={busyKey !== null}>
                      Dismiss
                    </Button>
                  </span>
                </ListCell>
              </ListRow>
            ))}
            {recoveryCases.map((item) => (
              <ListRow key={`${item.mediaType}:${item.id}`} className={ATTENTION_ROW_PHONE} onClick={() => setDrawer({ kind: "attention", id: `recovery:${item.mediaType}:${item.id}` })}>
                <ListNameCell name={item.title} sub={item.mediaType === "movie" ? "Movies" : "TV"} />
                <ListCell mobile primary={item.summary} secondary={item.recommendedAction} />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="flex flex-wrap items-center justify-end gap-2 max-md:justify-start" onClick={(event) => event.stopPropagation()}>
                    <span onClick={(event) => event.stopPropagation()}>
                      <Switch size="sm" aria-label={selectLabels.get(`recovery:${item.mediaType}:${item.id}`)} checked={selectedDismissKeys.has(`recovery:${item.mediaType}:${item.id}`)} onCheckedChange={(checked) => toggleDismissSelected(`recovery:${item.mediaType}:${item.id}`, checked)} disabled={busyKey !== null} />
                    </span>
                    {/* Offered where Deluno can act (#558): a failed download or
                        import, named by the download it is about. A quality,
                        unmatched or corrupt file needs a decision instead. */}
                    {canTryAgain(item) ? (
                      <Button type="button" variant="outline" size="sm" onClick={() => void retryRecovery(item)} disabled={busyKey !== null}>
                        {busyKey === `recovery:retry:${item.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                        {isVanished(item) ? "Search for another" : isProcessorFault(item) ? "Run the processor again" : "Try again"}
                      </Button>
                    ) : null}
                    {isVanished(item) ? (
                      <Button type="button" variant="outline" size="sm" onClick={() => void stopWantingRecovery(item)} disabled={busyKey !== null}>
                        {busyKey === `recovery:stop-wanting:${item.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                        Stop wanting it
                      </Button>
                    ) : null}
                    {/* #919: Checkpoint B held a processed file back because the
                        processor lost something the original had -- three real
                        actions, never a blocklist since the release itself is
                        already known good. */}
                    {isProcessorFault(item) ? (
                      <>
                        <Button type="button" variant="outline" size="sm" onClick={() => void importOriginalRecovery(item)} disabled={busyKey !== null}>
                          {busyKey === `recovery:import-original:${item.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                          Import original
                        </Button>
                        <Button type="button" variant="outline" size="sm" onClick={() => void searchAgainRecovery(item)} disabled={busyKey !== null}>
                          {busyKey === `recovery:search-again:${item.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                          Search for another release
                        </Button>
                      </>
                    ) : null}
                    <Button type="button" variant="outline" size="sm" onClick={() => void dismissRecovery(item)} disabled={busyKey !== null}>
                      Dismiss
                    </Button>
                  </span>
                </ListCell>
              </ListRow>
            ))}
            {queueAttention.map((item) => (
              <ListRow key={`attention:${item.clientId}:${item.id}`} className={ATTENTION_ROW_PHONE} onClick={() => setDrawer({ kind: "queue", id: item.id })}>
                <ListNameCell name={item.title || item.releaseName} sub={item.clientName} />
                <ListCell
                  mobile
                  primary={attentionReason(item)}
                  secondary={attentionEvidence(item)}
                />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="text-(length:--type-caption) text-muted-foreground">Open to fix</span>
                </ListCell>
              </ListRow>
            ))}
            {failedImportJobs.map((job) => (
              <ListRow key={`job:${job.id}`} className={ATTENTION_ROW_PHONE} onClick={() => setDrawer({ kind: "attention", id: `job:${job.id}` })}>
                <ListNameCell name={jobTitle(job)} sub="Import job" />
                <ListCell mobile primary={job.lastError ? plainFailure(job.lastError, "The import") : "The import failed."} secondary={`Attempt ${job.attempts}`} />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="flex flex-wrap items-center justify-end gap-2 max-md:justify-start" onClick={(event) => event.stopPropagation()}>
                    <span onClick={(event) => event.stopPropagation()}>
                      <Switch size="sm" aria-label={selectLabels.get(`job:${job.id}`)} checked={selectedDismissKeys.has(`job:${job.id}`)} onCheckedChange={(checked) => toggleDismissSelected(`job:${job.id}`, checked)} disabled={busyKey !== null} />
                    </span>
                    <Button type="button" variant="outline" size="sm" onClick={() => void dismissFailedImport(job)} disabled={busyKey !== null}>
                      Dismiss
                    </Button>
                  </span>
                </ListCell>
              </ListRow>
            ))}
            {failedProcessorHandoffs.map((handoff) => (
              <ListRow
                key={`processor-failure:${handoff.id}`}
                className={ATTENTION_ROW_PHONE}
                onClick={() => setDrawer(
                  // The download's own drawer holds the fixes beside the download; the hand-off drawer is for one whose download has left the client.
                  queue.some((item) => item.id === handoff.queueItemId && item.clientId === handoff.clientId)
                    ? { kind: "queue", id: handoff.queueItemId }
                    : activity.some((entry) => entry.id === `processor:${handoff.id}`)
                      ? { kind: "activity", id: `processor:${handoff.id}` }
                      : { kind: "attention", id: `handoff:${handoff.id}` }
                )}
              >
                <ListNameCell name={handoff.releaseName} sub={handoff.processorName ?? "Processing connector"} />
                <ListCell mobile primary={handoff.failureMessage ? plainFailure(handoff.failureMessage, `Sending it to ${handoff.processorName ?? "the processor"}`) : `Processor reported ${processorStatusLabel(handoff.status)}.`} secondary={handoff.sourcePath} />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="flex flex-wrap items-center justify-end gap-2 max-md:justify-start">
                    <span onClick={(event) => event.stopPropagation()}>
                      <Switch size="sm" aria-label={selectLabels.get(`handoff:${handoff.id}`)} checked={selectedDismissKeys.has(`handoff:${handoff.id}`)} onCheckedChange={(checked) => toggleDismissSelected(`handoff:${handoff.id}`, checked)} disabled={busyKey !== null} />
                    </span>
                    <span className="text-(length:--type-caption) text-muted-foreground">Open to fix</span>
                  </span>
                </ListCell>
              </ListRow>
            ))}
            {failedDownloads.map(({ dispatch, attempts }) => (
              <ListRow key={`dispatch-failure:${dispatch.id}`} className={ATTENTION_ROW_PHONE} onClick={() => void showDispatch(dispatch)} selected={openDispatchSummary?.id === dispatch.id}>
                <ListNameCell name={dispatch.releaseName} sub={dispatch.mediaType === "tv" ? "TV" : "Movies"} />
                <ListCell
                  mobile
                  primary={dispatchActivityDetail(dispatch)}
                  secondary={attempts > 1 ? `${attempts} attempts · ${dispatchFailureDetail(dispatch)}` : dispatchFailureDetail(dispatch)}
                />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="flex flex-wrap items-center justify-end gap-2 max-md:justify-start">
                    <span onClick={(event) => event.stopPropagation()}>
                      <Switch size="sm" aria-label={selectLabels.get(`dispatch:${dispatch.id}`)} checked={selectedDismissKeys.has(`dispatch:${dispatch.id}`)} onCheckedChange={(checked) => toggleDismissSelected(`dispatch:${dispatch.id}`, checked)} disabled={busyKey !== null} />
                    </span>
                    <span className="text-(length:--type-caption) text-muted-foreground">Open transfer details</span>
                  </span>
                </ListCell>
              </ListRow>
            ))}
            {orphanedDownloads.map((orphan) => (
              <ListRow key={`orphan:${orphan.path}`} className={ATTENTION_ROW_PHONE} onClick={() => setDrawer({ kind: "attention", id: `orphan:${orphan.path}` })}>
                <ListNameCell name={orphan.name} sub={`${orphan.libraryName} download folder`} />
                <ListCell
                  mobile
                  primary="Left in the download folder. No client, import or hand-off is using it."
                  secondary={`${formatBytes(orphan.sizeBytes)} · ${orphan.path}`}
                />
                <ListCell mobile align="end" className={ATTENTION_ACTIONS_PHONE}>
                  <span className="flex flex-wrap items-center justify-end gap-2 max-md:justify-start" onClick={(event) => event.stopPropagation()}>
                    <Switch
                      size="sm"
                      aria-label={`Select ${orphan.name}`}
                      checked={selectedOrphanPaths.has(orphan.path)}
                      onCheckedChange={(checked) => toggleOrphanSelected(orphan.path, checked)}
                      disabled={busyKey !== null}
                    />
                    <Button type="button" variant="outline" size="sm" aria-label={`Remove ${orphan.name}`} onClick={() => void actOnOrphan(orphan, "remove")} disabled={busyKey !== null}>
                      Remove
                    </Button>
                    <Button type="button" variant="outline" size="sm" aria-label={`Keep ${orphan.name}`} onClick={() => void actOnOrphan(orphan, "keep")} disabled={busyKey !== null}>
                      Keep
                    </Button>
                  </span>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      <ListCard
        title="Cleanup guardrails"
        count={cleanupPolicyLabel(settings)}
      >
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <p className="max-w-3xl text-(length:--type-body-sm) leading-relaxed text-muted-foreground">
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

      <p className="px-1 text-sm">
        <Link to="/activity" className="font-medium text-primary hover:underline">
          See everything in Activity →
        </Link>
      </p>

      {/* -------------------------------------------------- download drawer */}
      <Drawer
        open={drawer?.kind === "queue"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openQueueItem?.title || openQueueItem?.releaseName || "Download"}
        description={openQueueItem ? `${openQueueItem.clientName} · ${drawerStatusLabel(openQueueItem.status, resendingHandoff, openQueueHandoff?.processorName)}` : undefined}
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
                {openQueueItem.availability != null ? (
                  <Fact label="Availability" value={`${openQueueItem.availability.toFixed(2)} (1.0 is one full copy across all peers)`} />
                ) : null}
                {openQueueItem.queuePosition != null ? (
                  <Fact label="Place in queue" value={`${openQueueItem.queuePosition} (1 is next to start)`} />
                ) : null}
                {openQueueItem.seedLimit ? (
                  <Fact label="Seed limit" value={openQueueItem.seedLimit} />
                ) : null}
                {openQueueItem.lastActivityUtc ? (
                  <Fact label="Last activity" value={formatAgo(openQueueItem.lastActivityUtc, preferences)} />
                ) : null}
                {openQueueItem.completionUtc ? (
                  <Fact label="Completed" value={formatAgo(openQueueItem.completionUtc, preferences)} />
                ) : null}
                <Fact label="From" value={openQueueItem.indexerName || "Unknown source"} />
                <Fact label="Tracker privacy" value={privacyLabel(openQueueItem.isPrivate)} />
                <Fact label="Release" value={openQueueItem.releaseName} mono />
              </div>
              <div className="flex justify-end">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => void ignoreDownloadHealth(openQueueItem)}
                  disabled={busyKey !== null}
                  title="Exempt this download from every health rule until removed from Automation & Recovery, Download Health"
                >
                  {busyKey === `ignore-download:${openQueueItem.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Ignore this download
                </Button>
              </div>
            </DrawerSection>

            {(showItemFailure && (openQueueItem.errorMessage || openQueueItem.failure)) || drawerFindings.length ? (
              <DrawerSection title="Health">
                {showItemFailure && openQueueItem.failure ? <IntegrationFailureDetails failure={openQueueItem.failure} preferences={preferences} /> : null}
                {showItemFailure && openQueueItem.errorMessage ? <p className="text-(length:--type-body-sm) text-destructive">{openQueueItem.errorMessage}</p> : null}
                {drawerFindings.map((finding, index) => (
                  <div key={index} className="grid gap-0.5 border-b border-hairline py-2 last:border-b-0">
                    <div className="flex items-start justify-between gap-2">
                      <span className="text-(length:--type-body-sm) text-foreground">{finding.summary}</span>
                      {/* #981: unlinked-from-library is a live fact mirrored
                          from the sharing pass, not a per-item strike record -
                          there is nothing here to ignore for a week. A real one
                          offers the fix instead: take the leftover copy out. */}
                      {finding.kind === "hardlink-lost" ? (
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => void removeWithReason(openQueueItem, "reclaim")}
                          disabled={busyKey !== null || removalReasonBusy !== null}
                          title="Take this copy out of the client and delete its files. The library file is not touched."
                        >
                          {removalReasonBusy === "reclaim" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                          Remove this copy
                        </Button>
                      ) : (
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => void dismissHealthFinding(openQueueItem, finding.kind)}
                          disabled={busyKey !== null}
                        >
                          Dismiss
                        </Button>
                      )}
                    </div>
                    {/* #905: which rule is tripping, its strikes so far, and
                        when the next strike or action is due - a finding
                        outside the rule engine (client-stalled, and so on)
                        has no ruleId and shows only its strike count. */}
                    <span className="text-(length:--type-caption) text-muted-foreground">
                      {healthFindingRuleSummary(finding, preferences)}
                    </span>
                    {finding.recommendedAction ? (
                      <span className="text-(length:--type-caption) text-muted-foreground">{finding.recommendedAction}</span>
                    ) : null}
                  </div>
                ))}
              </DrawerSection>
            ) : null}

            <DrawerSection
              title="Import"
              aside={resendingHandoff ? drawerStatusLabel(openQueueItem.status, true, openQueueHandoff?.processorName) : isImportReadyStatus(openQueueItem.status) ? "ready" : queueStatusLabel(openQueueItem.status)}
            >
              {/* After Try again the answer is still to come: say so, never "failed" (#1193). */}
              {openQueueHandoff && resendingHandoff ? (
                <p className="flex items-center gap-2 text-(length:--type-body-sm) text-foreground" role="status">
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                  Sending it to {openQueueHandoff.processorName ?? "the processor"} again. The result shows here when it answers.
                </p>
              ) : null}
              {/* The processor's own reason and the way out, where the failure
                  is shown (#616): the hand-off drawer had both, this one had
                  neither, and Queue import is closed to a failed hand-off. */}
              {/* While "Sending it again" shows, the old failure is hidden, not shown beside it (#1198). */}
              {openQueueHandoff && openQueueItem.status === downloadQueueStatuses.processingFailed && !resendingHandoff ? (
                <div className="grid gap-3 rounded-[10px] border border-destructive/30 bg-destructive/5 p-3">
                  <p className="text-(length:--type-body-sm) text-foreground">
                    {plainFailure(openQueueHandoff.failureMessage, `Sending it to ${openQueueHandoff.processorName ?? "the processor"}`)}
                  </p>
                  {handoffFixes(openQueueHandoff)}
                </div>
              ) : null}
              {!queueItemHasFiles(openQueueItem) ? (
                <p className="text-(length:--type-caption) text-muted-foreground">
                  This download has no files yet (a magnet link is still fetching its details), so there is nothing to import. Import opens once it has files.
                </p>
              ) : importPreviews[openQueueItem.id] ? (
                <ImportPreviewFacts preview={importPreviews[openQueueItem.id]!} />
              ) : (
                <p className="text-(length:--type-caption) text-muted-foreground">
                  Preview shows exactly where the file would land and what it would be renamed to, before anything moves.
                </p>
              )}
              {queueItemHasFiles(openQueueItem) ? (
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
              ) : null}
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
                {canReorder ? (
                  <>
                    <Button type="button" variant="outline" size="sm" title="Move to top" aria-label="Move to top of queue" onClick={() => void queueAction(openQueueItem, "move-to-top")} disabled={busyKey !== null}>
                      <ArrowUpToLine className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="outline" size="sm" title="Move up" aria-label="Move up in queue" onClick={() => void queueAction(openQueueItem, "move-up")} disabled={busyKey !== null}>
                      <ChevronUp className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="outline" size="sm" title="Move down" aria-label="Move down in queue" onClick={() => void queueAction(openQueueItem, "move-down")} disabled={busyKey !== null}>
                      <ChevronDown className="h-3.5 w-3.5" />
                    </Button>
                    <Button type="button" variant="outline" size="sm" title="Move to bottom" aria-label="Move to bottom of queue" onClick={() => void queueAction(openQueueItem, "move-to-bottom")} disabled={busyKey !== null}>
                      <ArrowDownToLine className="h-3.5 w-3.5" />
                    </Button>
                  </>
                ) : (
                  // #810: uTorrent's queue-reorder API could not be
                  // independently confirmed this session (closed-source, no
                  // public source repo), so the control is hidden rather than
                  // guessed at — with a plain reason rather than nothing.
                  <p className="text-(length:--type-caption) text-muted-foreground basis-full">
                    Reordering the queue is not available for {openQueueItem.clientName}.
                  </p>
                )}
                {canForceStart ? (
                  <Button type="button" variant="outline" size="sm" onClick={() => void queueAction(openQueueItem, "force-start")} disabled={busyKey !== null}>
                    <Zap className="h-3.5 w-3.5" />
                    Force start
                  </Button>
                ) : null}
                <Button type="button" variant="outline" size="sm" onClick={() => void previewCleanup(openQueueItem)} disabled={busyKey !== null}>
                  What would removing do?
                </Button>
                {/* Standalone from #924's "It's bad" removal reason (#925):
                    blocks the release without also removing, deleting or
                    re-searching. Matched against this same queue item by its
                    id (torrent hash / NZB item id), so it opens straight into
                    the "found downloading" stop-too option. */}
                <BlockReleaseAction
                  releaseName={openQueueItem.releaseName}
                  indexerName={openQueueItem.indexerName}
                  hash={openQueueItem.id}
                  mediaType={openQueueItem.mediaType}
                  entityId={findDispatchForQueueItem(openQueueItem, dispatches)?.entityId ?? null}
                  title={openQueueItem.title}
                  onBlocked={() => revalidator.revalidate()}
                />
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
              <p className="text-(length:--type-caption) text-muted-foreground">
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
        description="Bring in files Deluno did not download itself"
        footer={
          manualImportMode === "file" ? (
            <DrawerFooter
              state={manualState}
              message={manualMessage}
              saveType="button"
              saveLabel="Queue import"
              saveEnabled={Boolean(manualImport.sourcePath.trim()) && busyKey === null && manualQueued?.key !== manualKey && !(manualPreview?.needsTitle && !manualImport.title.trim())}
              onSave={() => void manualRun("queue")}
              onCancel={() => setDrawer(null)}
            />
          ) : (
            <DrawerFooter readOnly state="clean" saveLabel="Close" onCancel={() => setDrawer(null)} />
          )
        }
      >
        <DrawerSection title="Bring in">
          <SegmentedControl<"file" | "folder">
            aria-label="Manual import mode"
            value={manualImportMode}
            onValueChange={setManualImportMode}
            options={[
              { value: "file", label: "One file" },
              { value: "folder", label: "Scan a folder" }
            ]}
          />
        </DrawerSection>

        {manualImportMode === "file" ? (
          <>
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

            {manualQueued?.key === manualKey ? (
              <DrawerSection title="Queued">
                <p className="text-(length:--type-caption) text-muted-foreground" role="status">
                  {manualQueued.title} will be filed at <span className="font-mono">{manualQueued.destination}</span>.{" "}
                  <Link to="/activity" className="font-medium text-primary hover:underline">Follow it in Activity</Link>
                </p>
              </DrawerSection>
            ) : null}

            <DrawerSection title="Where it would land" aside={manualPreview ? undefined : "run a preview"}>
              {manualPreview?.needsTitle && !manualImport.title.trim() ? (
                <p className="text-(length:--type-caption) text-warning" role="alert">
                  Deluno could not tell which show in your library this is. Enter the show&apos;s title above, then preview again.
                </p>
              ) : null}
              {manualPreview?.clientHoldsSource ? (
                <p className="text-(length:--type-caption) text-muted-foreground">
                  A download client is still sharing this file. Deluno copies it and leaves the original where the client expects it.
                  {manualImport.transferMode === "move" ? " Moving it takes the file away from that client, which will lose it and stop sharing." : ""}
                </p>
              ) : null}
              {manualPreview ? (
                <ImportPreviewFacts preview={manualPreview} />
              ) : (
                <p className="text-(length:--type-caption) text-muted-foreground">Preview first — it shows the destination path and the new name without writing anything.</p>
              )}
              <div>
                <Button type="button" variant="outline" size="sm" onClick={() => void manualRun("preview")} disabled={busyKey !== null || !manualImport.sourcePath.trim()}>
                  {busyKey === "manual:preview" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Preview
                </Button>
              </div>
            </DrawerSection>
          </>
        ) : (
          <>
            <DrawerSection title="Folder">
              <Field label="Folder" help="Deluno lists every video file inside it — including subfolders, for a season pack — and guesses each one's title and year from its own name.">
                <PathInput value={folderScanPath} onChange={setFolderScanPath} placeholder="D:/Downloads/Some.Release.2024" />
              </Field>
              <FieldRow>
                <Field label="Media type">
                  <SegmentedControl<string>
                    aria-label="Folder media type"
                    value={folderScanMediaType}
                    onValueChange={setFolderScanMediaType}
                    options={[
                      { value: "movies", label: "Movies" },
                      { value: "tv", label: "TV" }
                    ]}
                  />
                </Field>
                <Field label="Transfer method">
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
              <div>
                <Button type="button" variant="outline" size="sm" onClick={() => void scanFolder()} disabled={folderScanBusy || !folderScanPath.trim()}>
                  {folderScanBusy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Scan folder
                </Button>
              </div>
              {folderScanMessage ? (
                <p className="text-(length:--type-caption) text-muted-foreground">{folderScanMessage}</p>
              ) : null}
              {folderScanIssues.length > 0 ? (
                <ul className="list-inside list-disc text-(length:--type-caption) text-warning">
                  {folderScanIssues.map((issue) => <li key={issue}>{issue}</li>)}
                </ul>
              ) : null}
            </DrawerSection>

            {folderScanRows.length > 0 ? (
              <DrawerSection
                title="Review every match"
                aside={`${folderScanRows.filter((row) => row.include).length} of ${folderScanRows.length} selected`}
              >
                <div className="flex flex-col gap-2">
                  {folderScanRows.map((row, index) => (
                    <div key={row.sourcePath} className="rounded-[10px] border border-hairline p-3">
                      <div className="flex items-start gap-2">
                        <Checkbox
                          checked={row.include}
                          aria-label={`Include ${row.fileName}`}
                          onCheckedChange={(checked) =>
                            setFolderScanRows((current) =>
                              current.map((item, i) => (i === index ? { ...item, include: checked } : item))
                            )
                          }
                        />
                        <div className="min-w-0 flex-1">
                          <p className="truncate font-mono text-(length:--type-caption) text-muted-foreground" title={row.fileName}>
                            {row.fileName}
                          </p>
                          {!row.matched ? (
                            <p className="mt-0.5 text-(length:--type-caption) font-medium text-warning">
                              Deluno could not guess a title — fill it in and re-check before including this row.
                            </p>
                          ) : null}
                          <FieldRow className="mt-1.5">
                            <Field label="Title">
                              <Input
                                value={row.title}
                                onChange={(event) =>
                                  setFolderScanRows((current) =>
                                    current.map((item, i) => (i === index ? { ...item, title: event.target.value } : item))
                                  )
                                }
                              />
                            </Field>
                            <Field label="Year" optional>
                              <Input
                                type="number"
                                value={row.year}
                                onChange={(event) =>
                                  setFolderScanRows((current) =>
                                    current.map((item, i) => (i === index ? { ...item, year: event.target.value } : item))
                                  )
                                }
                              />
                            </Field>
                          </FieldRow>
                          <div className="mt-1.5 flex items-center gap-2">
                            <Button type="button" variant="outline" size="sm" onClick={() => void recheckFolderScanRow(index)} disabled={row.checking}>
                              {row.checking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                              Re-check
                            </Button>
                            <span className="truncate text-(length:--type-caption) text-muted-foreground" title={row.preview.destinationPath}>
                              {row.preview.destinationPath}
                            </span>
                          </div>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
                <div>
                  <Button
                    type="button"
                    onClick={() => void importReviewedFolderRows()}
                    disabled={folderScanBusy || folderScanRows.every((row) => !row.include)}
                  >
                    {folderScanBusy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Upload className="h-3.5 w-3.5" />}
                    Import selected
                  </Button>
                </div>
              </DrawerSection>
            ) : null}
          </>
        )}
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
                <Fact label="Detected" value={openDispatchDetail.dispatch.detectedUtc ? formatPreferenceDateTime(openDispatchDetail.dispatch.detectedUtc, preferences) : "Not detected by the client"} />
                <Fact label="Import" value={dispatchOutcome(openDispatchDetail.dispatch.importStatus, openDispatchDetail.dispatch.importFailureMessage)} />
                <Fact label="Attempts" value={String(openDispatchDetail.dispatch.attemptCount ?? 0)} />
              </div>
            </DrawerSection>

            {openDispatchDetail.dispatch.failure ? (
              <DrawerSection title="Integration finding">
                <IntegrationFailureDetails failure={openDispatchDetail.dispatch.failure} preferences={preferences} />
              </DrawerSection>
            ) : null}

            {openDispatchDetail.dispatch.importedFilePath || openDispatchDetail.dispatch.grabFailureCode ? (
              <DrawerSection title="File and outcome">
                <div className="grid gap-1.5">
                  {openDispatchDetail.dispatch.importedFilePath ? <Fact label="Imported file" value={openDispatchDetail.dispatch.importedFilePath} mono /> : null}
                  {openDispatchDetail.dispatch.downloadedBytes ? <Fact label="Downloaded" value={formatBytes(openDispatchDetail.dispatch.downloadedBytes)} /> : null}
                  {openDispatchDetail.dispatch.grabFailureCode ? <Fact label="Grab issue" value={openDispatchDetail.dispatch.grabFailureCode} /> : null}
                </div>
              </DrawerSection>
            ) : null}

            {isDispatchFailure(openDispatchDetail.dispatch) ? (
              <DrawerSection title="If nothing more needs doing">
                <DrawerDanger
                  title="Dismiss this download"
                  description="Deluno stops counting it as needing you. Its journey stays on record, and the title is still searched for as usual."
                  action={
                    <Button type="button" variant="outline" size="sm" onClick={() => void dismissDispatch(openDispatchDetail.dispatch)} disabled={busyKey !== null}>
                      {busyKey === `dispatch:dismiss:${openDispatchDetail.dispatch.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                      Dismiss
                    </Button>
                  }
                />
              </DrawerSection>
            ) : null}

            {openFileProvenance ? (
              <DrawerSection title="File provenance" aside={openFileProvenance.preferenceStatus ?? "Recorded at import"}>
                <div className="grid gap-1.5">
                  <Fact label="Original release" value={openFileProvenance.releaseNameEvidence ?? "Not recorded"} mono />
                  <Fact label="Source file" value={openFileProvenance.sourcePath ?? "Not recorded"} mono />
                  <Fact label="Destination file" value={openFileProvenance.destinationPath ?? "Not recorded"} mono />
                  <Fact label="Rename" value={openFileProvenance.rename ?? "Not recorded"} mono />
                  <Fact label="Matched rules" value={openFileProvenance.matchedRuleNames || "None"} />
                  <Fact label="Processor" value={openFileProvenance.processorName ?? openFileProvenance.importWorkflow ?? "Direct import"} />
                  <Fact
                    label="Transfer"
                    value={transferProvenanceLabel(
                      openFileProvenance.preferredTransferMode,
                      openFileProvenance.transferModeUsed,
                      openFileProvenance.usedFallback
                    )}
                  />
                  {openFileProvenance.preferencePlanId ? (
                    <Fact label="Preference plan" value={`${openFileProvenance.preferencePlanId}${openFileProvenance.preferencePlanVersion ? ` · ${openFileProvenance.preferencePlanVersion}` : ""}`} mono />
                  ) : null}
                  {openFileProvenance.preferencePlanHash ? <Fact label="Plan proof" value={openFileProvenance.preferencePlanHash} mono /> : null}
                  {openFileProvenance.workflowCleanup ? <Fact label="Cleanup" value={openFileProvenance.workflowCleanup} /> : null}
                </div>
              </DrawerSection>
            ) : null}

            <DrawerSection title="Recorded timeline" aside={openDispatchDetail.timeline.length ? `${openDispatchDetail.timeline.length} events` : "No events recorded"}>
              {openDispatchDetail.timeline.length ? (
                <div className="grid gap-0">
                  {openDispatchDetail.timeline.map((event) => (
                    <div key={event.id} className="grid gap-0.5 border-b border-hairline py-2 last:border-b-0">
                      <div className="flex items-baseline justify-between gap-3">
                        <span className="text-(length:--type-body-sm) font-medium text-foreground">{timelineEventLabel(event.eventType)}</span>
                        <span className="shrink-0 text-(length:--type-caption) text-muted-foreground">{formatAgo(event.timestamp, preferences)}</span>
                      </div>
                      {timelineEventDetail(event, openDispatchDetail.dispatch.importFailureMessage) ? <span className="text-(length:--type-caption) text-muted-foreground">{timelineEventDetail(event, openDispatchDetail.dispatch.importFailureMessage)}</span> : null}
                    </div>
                  ))}
                </div>
              ) : (
                <p className="text-(length:--type-caption) text-muted-foreground">The client has not reported any additional transfer events yet.</p>
              )}
            </DrawerSection>
          </>
        ) : (
          <ListEmpty
            title={busyKey?.startsWith("dispatch:") ? "Loading transfer history" : "Transfer details unavailable"}
            description="The transfer row is still visible, but its full server-side timeline could not be loaded. Close it and open it again to try again."
          />
        )}
      </Drawer>

      {/* ------------------------------------------ needs-attention drawer */}
      <Drawer
        open={drawer?.kind === "attention"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openAttention?.title ?? "Needs attention"}
        description={openAttention?.sub}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setDrawer(null)} />}
      >
        {openAttention ? (
          <DrawerSection title="What happened">
            <div className="grid gap-1.5">
              {openAttention.facts.map((fact) => <Fact key={fact.label} label={fact.label} value={fact.value} mono={fact.mono} />)}
            </div>
            <p className="text-(length:--type-caption) text-muted-foreground">The buttons for this row are on the row itself.</p>
          </DrawerSection>
        ) : null}
      </Drawer>

      {/* ------------------------------------------------- activity drawer */}
      <Drawer
        open={drawer?.kind === "activity"}
        onOpenChange={(open) => {
          if (!open) setDrawer(null);
        }}
        title={openActivity?.name ?? "Activity"}
        description={openActivity ? `${openActivity.sub} · ${openActivity.status}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setDrawer(null)} />}
      >
        {openActivity ? (
          <DrawerSection title="What happened">
            <div className="grid gap-1.5">
              <Fact label="Outcome" value={openActivity.detail} />
              {/* The hand-off's and the import's own paths are printed below under their names; Detail would say the same path a second time. */}
              {openActivity.extra && ![openHandoff?.sourcePath, openHandoff?.outputPath, openImportInfo?.sourcePath, openImportInfo?.destinationPath].includes(openActivity.extra) ? <Fact label="Detail" value={openActivity.extra} mono /> : null}
              <Fact label="When" value={formatPreferenceDateTime(openActivity.whenUtc, preferences)} />
              <Fact label="Kind" value={activityKindLabel(openActivity.kind)} />
              {openHandoff ? <Fact label="From" value={openHandoff.sourcePath} mono /> : null}
              {openHandoff?.outputPath ? <Fact label="Output" value={openHandoff.outputPath} mono /> : null}
              {openImportInfo?.sourcePath ? <Fact label="From" value={openImportInfo.sourcePath} mono /> : null}
              {openImportInfo?.destinationPath ? <Fact label="Destination" value={openImportInfo.destinationPath} mono /> : null}
            </div>
          </DrawerSection>
        ) : null}
        {/* Whether the processor heard what became of its file, so it can let go
            of its copy — and a way to say it again (#580). */}
        {openHandoff?.outcome ? (
          <ProcessorOutcomeSection
            handoff={openHandoff}
            outcome={openHandoff.outcome}
            formatWhen={(utc) => formatPreferenceDateTime(utc, preferences)}
            busy={busyKey !== null}
            sending={busyKey === `handoff:outcome:${openHandoff.id}`}
            onSendAgain={() => void sendOutcomeAgain(openHandoff)}
          />
        ) : null}
        {/* Waiting is the processor's to end, until a person says otherwise (#566). */}
        {openHandoff && isWithProcessor(openHandoff.status) ? (
          <DrawerSection title="Still with the processor" aside={processorStageLabel(openHandoff.status)}>
            <DrawerDanger
              title={`Stop waiting for ${openHandoff.processorName ?? "the processor"}`}
              description="Deluno asks it to drop this file and stops waiting. The download stays where it is, and this hand-off can then be dismissed or its original imported."
              action={
                <Button type="button" variant="outline" size="sm" onClick={() => void stopWaiting(openHandoff)} disabled={busyKey !== null}>
                  {busyKey === `handoff:stop:${openHandoff.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Stop waiting
                </Button>
              }
            />
          </DrawerSection>
        ) : null}
        {openHandoff && (canImportOriginal(openHandoff.status) || handoffNeedsPerson(openHandoff)) ? (
          <DrawerSection title="What you can do" aside={processorStageLabel(openHandoff.status)}>
            {handoffFixes(openHandoff)}
          </DrawerSection>
        ) : null}
      </Drawer>

      <RemovalReasonDialog
        open={pendingRemoval !== null}
        onOpenChange={(open) => {
          if (!open) setPendingRemoval(null);
        }}
        title={pendingRemoval?.title ?? ""}
        busyReason={removalReasonBusy}
        onChoose={(reason) => {
          if (!pendingRemoval) return;
          void removeWithReason(pendingRemoval, reason);
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

function ProcessorOutcomeSection({
  handoff,
  outcome,
  formatWhen,
  busy,
  sending,
  onSendAgain
}: {
  handoff: ProcessorHandoffItem;
  outcome: ProcessorOutcomeRecord;
  formatWhen: (utc: string) => string;
  busy: boolean;
  sending: boolean;
  onSendAgain: () => void;
}) {
  const processor = handoff.processorName ?? outcome.processorName;
  const summary = processorOutcomeSummary(outcome, formatWhen);
  return (
    <DrawerSection title={`What ${processor} was told`}>
      <div className="grid gap-1.5">
        <Fact label="Deluno said" value={summary.said} />
        <Fact label="Heard" value={summary.heard} />
      </div>
      {summary.detail ? <p className="text-(length:--type-caption) text-muted-foreground">{summary.detail}</p> : null}
      {summary.canSendAgain ? (
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onSendAgain} disabled={busy}>
            {sending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
            Tell {processor} again
          </Button>
        </div>
      ) : null}
    </DrawerSection>
  );
}

function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex min-w-0 items-baseline justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
      <span className="shrink-0 text-(length:--type-caption) text-muted-foreground">{label}</span>
      <span className={`min-w-0 text-right text-(length:--type-caption) text-foreground [overflow-wrap:anywhere] ${mono ? "font-mono" : ""}`} title={value}>
        {value}
      </span>
    </div>
  );
}

function IntegrationFailureDetails({
  failure,
  preferences
}: {
  failure: IntegrationFailure;
  preferences: ReturnType<typeof useDisplayPreferences>["preferences"];
}) {
  const retryLabel = integrationRetryLabel(failure.retryState);
  const technicalFacts = [
    failure.serviceName ? { label: "Service", value: failure.serviceName } : null,
    failure.operation ? { label: "Operation", value: failure.operation } : null,
    failure.code ? { label: "Code", value: failure.code, mono: true } : null,
    failure.httpStatus != null ? { label: "HTTP status", value: String(failure.httpStatus) } : null,
    failure.externalId ? { label: "External ID", value: failure.externalId, mono: true } : null,
    failure.attempts > 0 ? { label: "Attempts", value: String(failure.attempts) } : null,
    failure.retryAfterUtc ? { label: "Next eligible", value: formatPreferenceDateTime(failure.retryAfterUtc, preferences) } : null
  ].filter((fact): fact is { label: string; value: string; mono?: boolean } => fact !== null);

  return (
    <div className="grid gap-2 rounded-sm border border-border bg-muted/30 p-3">
      <div className="flex flex-wrap items-center gap-2">
        <Chip tone={integrationFailureTone(failure)}>{integrationFailureKindLabel(failure.kind)}</Chip>
        <span className="text-(length:--type-caption) text-muted-foreground">{retryLabel}</span>
      </div>
      <div className="grid gap-0.5">
        <p className="text-(length:--type-body-sm) font-medium text-foreground">{failure.summary || failure.message}</p>
        {failure.message && failure.message !== failure.summary ? <p className="text-(length:--type-caption) text-muted-foreground">{failure.message}</p> : null}
        <p className="text-(length:--type-caption) text-muted-foreground">{failure.nextAction}</p>
      </div>
      {technicalFacts.length ? (
        <div className="grid gap-0.5 border-t border-border/70 pt-1.5">
          {technicalFacts.map((fact) => <Fact key={fact.label} {...fact} />)}
        </div>
      ) : null}
    </div>
  );
}

export function ImportPreviewFacts({ preview }: { preview: ImportPreviewResponse }) {
  return (
    <div className="grid gap-1.5">
      <Fact label={preview.pack ? "Pack destination" : "Would land at"} value={preview.destinationPath || "Not resolved"} mono />
      <Fact label="In folder" value={preview.destinationFolder || "Not resolved"} mono />
      {preview.matchedTitle ? <Fact label="Matched show" value={preview.matchedTitle} /> : null}
      <Fact label="Matched rule" value={preview.matchedRuleName ?? "No destination rule matched"} />
      <Fact label="How" value={preview.transferExplanation || preview.preferredTransferMode} />
      <Fact label="Source" value={preview.sourceExists ? formatBytes(preview.sourceSizeBytes) : "Not found on disk"} />
      {preview.destinationExists ? <Fact label="Careful" value="Something already exists at the destination" /> : null}
      {preview.pack ? (
        <div className="grid gap-1 rounded-sm border border-border/70 bg-muted/20 p-2" role="region" aria-label="Season pack import preview">
          <Fact label="Season pack" value={`${preview.pack.sourceFileCount} files · ${preview.pack.episodeCount} episodes`} />
          <Fact label="Readiness" value={preview.pack.alreadyCommitted ? "Already imported as this exact pack" : preview.pack.canExecute ? "Every file and episode is resolved" : "Needs recovery review"} />
          {preview.pack.files.map((file) => (
            <div key={file.sourcePath} className="grid gap-0.5 border-t border-border/60 pt-1.5">
              <Fact label={file.episodeKeys.join(", ") || "Unmatched"} value={file.destinationPath} mono />
            </div>
          ))}
          {preview.pack.blockReasons.length ? <Fact label="Blocked because" value={preview.pack.blockReasons.join(" · ")} /> : null}
        </div>
      ) : null}
      {preview.warnings.length ? <Fact label="Warnings" value={preview.warnings.join(" · ")} /> : null}
      {preview.mediaProbe ? <MediaInfoDetail probe={preview.mediaProbe} /> : null}
    </div>
  );
}

/**
 * The full MediaInfo detail set for a file's video and audio streams (#794):
 * pixel format, frame rate, sample rate and channel layout, straight off the
 * probe the import preview already ran - no new prober work. A field ffprobe
 * did not report is "Unavailable" rather than left blank or dropped, so a
 * partly-probed file still reads as partly known rather than silently empty.
 */
export function MediaInfoDetail({ probe }: { probe: MediaProbeInfo }) {
  if (probe.status !== "succeeded") {
    return (
      <div className="grid gap-0.5 rounded-sm border border-border/70 bg-muted/20 p-2" role="region" aria-label="MediaInfo">
        <Fact label="MediaInfo" value={probe.message ?? "Not probed"} />
      </div>
    );
  }

  const unavailable = "Unavailable";
  return (
    <div className="grid gap-1.5 rounded-sm border border-border/70 bg-muted/20 p-2" role="region" aria-label="MediaInfo">
      {probe.videoStreams.map((stream) => (
        <div key={`video-${stream.index}`} className="grid gap-0.5">
          <Fact label="Video" value={[stream.codec, stream.width && stream.height ? `${stream.width}x${stream.height}` : null].filter(Boolean).join(" · ") || unavailable} />
          <Fact label="Pixel format" value={stream.pixelFormat ?? unavailable} mono />
          <Fact label="Frame rate" value={stream.frameRate != null ? `${stream.frameRate.toFixed(2)} fps` : unavailable} />
        </div>
      ))}
      {probe.audioStreams.map((stream) => (
        <div key={`audio-${stream.index}`} className="grid gap-0.5 border-t border-border/60 pt-1.5 first:border-t-0 first:pt-0">
          <Fact label="Audio" value={[stream.codec, stream.language].filter(Boolean).join(" · ") || unavailable} />
          <Fact label="Channel layout" value={stream.channelLayout ?? (stream.channels != null ? `${stream.channels}ch` : unavailable)} />
          <Fact label="Sample rate" value={stream.sampleRate != null ? `${stream.sampleRate.toLocaleString()} Hz` : unavailable} />
        </div>
      ))}
      {probe.videoStreams.length === 0 && probe.audioStreams.length === 0 ? <Fact label="MediaInfo" value="No video or audio streams probed" /> : null}
    </div>
  );
}

/**
 * Findings still worth showing (#880/#881): a dismissed one keeps counting
 * server-side (it can come back if the same problem happens again once the
 * week is up) but drops out of the drawer the moment it is dismissed, rather
 * than sitting there after a person has already acted on it.
 */
export function visibleFindings(item: DownloadQueueItem, handoff?: Pick<ProcessorHandoffItem, "status"> | null, resending = false): DownloadHealthFinding[] {
  const now = Date.now();
  // A download the processor found gone has its one plain row ("The download was gone before ... could clean it");
  // the generic "post-processing step failed, review it before retrying" would say it a second time, with a Dismiss
  // of its own and advice to retry something that cannot be sent again (#1186).
  const goneAlready = handoff?.status === "source-gone";
  return (item.healthFindings ?? []).filter((finding) =>
    (!finding.ignoredUntilUtc || new Date(finding.ignoredUntilUtc).getTime() <= now)
    && !(goneAlready && finding.kind === "post-processing-failed")
    // While the hand-off is being sent again the old failure is not shown anywhere in the drawer (#1204).
    && !(resending && finding.kind === "post-processing-failed"));
}

/**
 * The status the drawer's header and Import box read. While a hand-off is being sent again it is the in-progress
 * state, never the old "Processing failed" (#1204): the same rule as the failure card and the Health section.
 */
export function drawerStatusLabel(status: string, resending: boolean, processorName?: string | null) {
  return resending ? `Sending to ${processorName ?? "the processor"} again` : queueStatusLabel(status);
}

/**
 * Whether the client's own failure details and error message may show in the Health section: not when the processor
 * owns the failure (it is said once, in Import), and not while the hand-off is being sent again (#1204).
 */
export function itemFailureVisible(processorOwnsFailure: boolean, resending: boolean) {
  return !processorOwnsFailure && !resending;
}

/** The key a download goes under in the "sent again" set, so the state outlives the hand-off row vanishing for a refresh. */
export function resentItemKey(clientId: string, queueItemId: string) {
  return `item:${clientId.toLowerCase()}:${queueItemId.toLowerCase()}`;
}

/** Hand-off answers that are a real failure (the old one being shown, or a new one), as opposed to still working or done. */
function isHandoffFailure(status: string) {
  return ["failed", "rejected", "timed-out", "stalled", "lost", "cancelled", "source-gone"].includes(status.toLowerCase());
}

/**
 * Whether a hand-off the person has just sent again is still waiting for its new answer (#1193, #1198, #1229). While it
 * is, the drawer says "Sending it again" and hides the old failure, so the two never sit side by side. It is held until
 * the new status's own data has arrived: the hand-off with the processor, a hand-off row briefly missing from a
 * refresh, or the download itself still reading "Processing failed" while the hand-off has already moved on. Only a
 * hand-off that answers with a failure again, or a download that has left "Processing failed", ends it.
 */
export function isResendingHandoff(
  handoff: Pick<ProcessorHandoffItem, "id" | "status"> | null,
  resentHandoffIds: ReadonlySet<string>,
  itemStatus?: string,
  clientId?: string,
  queueItemId?: string
) {
  const marked = (handoff ? resentHandoffIds.has(handoff.id) : false)
    || (clientId && queueItemId ? resentHandoffIds.has(resentItemKey(clientId, queueItemId)) : false);
  if (!marked) return false;
  if (!handoff) return itemStatus === downloadQueueStatuses.processingFailed || itemStatus === downloadQueueStatuses.waitingForProcessor;
  if (isWithProcessor(handoff.status)) return true;
  if (isHandoffFailure(handoff.status)) return false;
  return itemStatus === downloadQueueStatuses.processingFailed;
}

/**
 * #810: whether a queue item's own client can reorder its queue or
 * force-start a transfer, read from the client's own reported capabilities
 * rather than guessed at per protocol — a client whose queue API this
 * session could not confirm (uTorrent) reports neither, and the control is
 * hidden with a plain reason instead of failing when clicked.
 */
export function reorderControlsFor(client: Pick<DownloadClientTelemetrySnapshot, "capabilities"> | null): { canReorder: boolean; canForceStart: boolean } {
  return {
    canReorder: client?.capabilities.supportsQueueReorder ?? false,
    canForceStart: client?.capabilities.supportsForceStart ?? false
  };
}

/** Almost done and only closing its last blocks, moving or checking (#1193): the plain word "Finishing", never Stalled. */
function isFinishing(item: DownloadQueueItem) {
  if (item.progress < 99.9) return false;
  return (item.status === downloadQueueStatuses.downloading && item.progress < 100) || item.status === downloadQueueStatuses.notTransferring;
}

function queueChip(item: DownloadQueueItem): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (item.failure) return { tone: integrationFailureTone(item.failure), label: integrationFailureShortLabel(item.failure) };
  if (item.errorMessage || item.status === downloadQueueStatuses.stalled) return { tone: statusTone("transfer.stalled"), label: queueStatusLabel(item.status) };
  if (visibleFindings(item).length) return statusPresentation("transfer.needsALook");
  // Was green here and grey in the pipeline strip. Green is the colour for
  // done, and a release waiting to be imported is mid-pipeline.
  if (isImportReadyStatus(item.status)) return statusPresentation("transfer.importReady");
  if (item.status === downloadQueueStatuses.paused) return statusPresentation("transfer.paused");
  if (isFinishing(item)) return { tone: "info", label: "Finishing" };
  if (item.status === downloadQueueStatuses.notTransferring) return statusPresentation("transfer.notTransferring");
  if (isProcessingStatus(item.status)) return { tone: "info", label: queueStatusLabel(item.status) };
  return { tone: "info", label: queueStatusLabel(item.status) };
}

function importJobStage(job: JobQueueItem) {
  if (isJobDeadLettered(job.status as JobStatus)) return "Import gave up";
  // An attempt that failed with another one coming is not a failed import: the
  // file was still being written, and the next attempt imports it (#562).
  if (isJobRetrying(job)) return "Waiting for the file to settle";
  if (isJobFailed(job.status as JobStatus)) return "Import failed";
  if (job.status === JOB_STATUS.COMPLETED) return "Imported and named";
  if (job.status === JOB_STATUS.RUNNING) return "Writing to library";
  return "Waiting to import";
}

function importJobTone(job: JobQueueItem): NonNullable<ChipProps["tone"]> {
  if (isJobRetrying(job)) return "info";
  if (isJobFailed(job.status as JobStatus)) return "bad";
  if (isJobActive(job.status as never)) return "info";
  return "ok";
}

function handoffDownloadKey(clientId: string, queueItemId: string) {
  return `${clientId}:${queueItemId}`.toLowerCase();
}

function pipelineStage(item: DownloadQueueItem) {
  if (item.status === downloadQueueStatuses.handingOver) return "Handing over";
  if (item.status === downloadQueueStatuses.handedOverElsewhere) return "Another copy was handed over";
  if (item.status === downloadQueueStatuses.notHandedOver) return "Needs attention";
  if (item.status === downloadQueueStatuses.waitingForProcessor) return "Processing connector";
  if (item.status === downloadQueueStatuses.processing || item.status === downloadQueueStatuses.processed) return "Checking media";
  if (item.status === downloadQueueStatuses.importQueued) return "Importing & naming";
  if (item.status === downloadQueueStatuses.imported) return "In library";
  if (item.status === downloadQueueStatuses.importedFileLeft) return "Imported earlier";
  if (item.status === downloadQueueStatuses.filesGone) return "Files are gone";
  if (item.status === downloadQueueStatuses.importFailed || item.status === downloadQueueStatuses.processingFailed) return "Needs attention";
  if (isImportReadyStatus(item.status)) return "Ready for import";
  if (item.status === downloadQueueStatuses.paused) return "Paused";
  if (isFinishing(item)) return "Finishing";
  if (item.status === downloadQueueStatuses.notTransferring) return "Not transferring";
  return item.status === downloadQueueStatuses.queued ? "Waiting for client" : "Downloading";
}

function pipelineDetail(item: DownloadQueueItem) {
  if (item.failure) return item.failure.summary || item.failure.message || item.failure.nextAction;
  if (item.errorMessage) return item.errorMessage;
  if (item.status === downloadQueueStatuses.handingOver) return "The download has finished and is being moved or handed over.";
  if (item.status === downloadQueueStatuses.handedOverElsewhere) return "Another download of this same release has already been handed to the processor, so nothing is owed to this copy.";
  if (item.status === downloadQueueStatuses.notHandedOver) return "This download finished a while ago but has not been handed to the processor. Check the processor connection in System, then try again.";
  if (item.status === downloadQueueStatuses.waitingForProcessor) return "Waiting for the cleaned output before import.";
  if (item.status === downloadQueueStatuses.processing || item.status === downloadQueueStatuses.processed) return "Media quality and processor output are being checked.";
  if (item.status === downloadQueueStatuses.importQueued) return "The destination and final name are being applied.";
  if (item.status === downloadQueueStatuses.imported) return "The file has been placed in the library.";
  if (item.status === downloadQueueStatuses.importedFileLeft) return "Imported earlier, but the file has since left the library. The title shows as missing and Deluno searches for it.";
  if (item.status === downloadQueueStatuses.filesGone) return "The files of this download are gone, so there is nothing to import. Search for another copy, or remove it from the client.";
  if (isImportReadyStatus(item.status)) return "Preview the destination and final name before importing.";
  return item.sourcePath ? `Source: ${item.sourcePath}` : "External client owns the download.";
}

function attentionReason(item: DownloadQueueItem) {
  const blocked = item.healthFindings?.find((finding) => finding.candidateBlocked);
  if (blocked) return "Release refused: it stopped downloading";
  return item.failure?.summary ?? item.errorMessage ?? item.healthFindings?.[0]?.summary ?? "This download has stalled.";
}

function attentionEvidence(item: DownloadQueueItem) {
  const blocked = item.healthFindings?.find((finding) => finding.candidateBlocked);
  if (blocked) return `${blocked.strikeCount} strikes · ${blocked.evidence}`;
  if (item.failure) return [integrationFailureShortLabel(item.failure), item.failure.attempts > 0 ? `${item.failure.attempts} attempts` : undefined].filter(Boolean).join(" · ");
  return item.healthFindings?.length ? `${item.healthFindings.length} health ${item.healthFindings.length === 1 ? "finding" : "findings"}` : undefined;
}

function integrationFailureTone(failure: IntegrationFailure): NonNullable<ChipProps["tone"]> {
  return failure.retryState === "Retrying" || failure.retryState === "RetryScheduled" || failure.retryState === "CircuitOpen" ? "warn" : "bad";
}

function integrationFailureKindLabel(kind: string) {
  return kind.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function integrationRetryLabel(retryState: string) {
  switch (retryState) {
    case "Retrying": return "Retrying automatically";
    case "RetryScheduled": return "Retry scheduled";
    case "CircuitOpen": return "Paused after repeated failures";
    case "ManualAction": return "Needs your action";
    case "NotRetryable": return "Not retryable automatically";
    default: return retryState || "Retry status unknown";
  }
}

function integrationFailureShortLabel(failure: IntegrationFailure) {
  if (failure.retryState === "ManualAction") return "Needs action";
  if (failure.retryState === "CircuitOpen") return "Paused after failures";
  if (failure.retryState === "Retrying" || failure.retryState === "RetryScheduled") return "Retry scheduled";
  return "Failed";
}

function integrationFailureActivityDetail(failure: IntegrationFailure) {
  return [
    failure.summary || failure.message,
    integrationRetryLabel(failure.retryState),
    failure.retryAfterUtc ? `next eligible ${failureRetryAfterLabel(failure.retryAfterUtc)}` : undefined,
    failure.nextAction
  ].filter(Boolean).join(" · ");
}

function failureRetryAfterLabel(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

function isProcessorTerminal(status: string) {
  return ["completed", "released"].includes(status.toLowerCase());
}

/** Mirrors `ProcessorHandoffStatuses.CanReleaseOriginal`: the processor stopped without delivering. */
/** A magnet still fetching its details is 0 B of 0 B: nothing to preview or import yet (#1138). */
function queueItemHasFiles(item: { sizeBytes: number; downloadedBytes: number }): boolean {
  return item.sizeBytes > 0 || item.downloadedBytes > 0;
}

function canImportOriginal(status: string) {
  return ["failed", "timed-out", "stalled", "lost", "cancelled"].includes(status.toLowerCase());
}

function ordinal(position: number) {
  const tens = position % 100;
  if (tens >= 11 && tens <= 13) return `${position}th`;
  return `${position}${({ 1: "st", 2: "nd", 3: "rd" } as Record<number, string>)[position % 10] ?? "th"}`;
}

/**
 * What the processor itself says about a file it holds, in a few words (#511):
 * "Weir: 3rd in queue", "Weir: runs at 01:00". Null when it cannot be
 * asked, so the row falls back to Deluno's own view.
 */
function processorSays(handoff: ProcessorHandoffItem, formatWhen: (utc: string) => string) {
  const seen = handoff.processor;
  const name = handoff.processorName ?? "Processor";
  if (!seen || isProcessorTerminal(handoff.status) || isProcessorFailure(handoff.status)) return null;
  if (seen.unreachableSinceUtc) return `${name} isn't answering · still waiting`;
  switch (seen.state) {
    case "queued":
      return seen.queuePosition ? `${name}: ${ordinal(seen.queuePosition)} in queue` : `${name}: queued`;
    case "scheduled":
      return seen.scheduledForUtc ? `${name}: runs at ${formatWhen(seen.scheduledForUtc)}` : `${name}: ${seen.message ?? "waiting for its schedule"}`;
    case "working":
      return `${name}: working on it`;
    case "completed":
    case "passed-through":
      return `${name}: finished · importing`;
    default:
      return seen.message ? `${name}: ${seen.message}` : null;
  }
}

const processorStageLabel = handoffStatusLabel;

/** What the Connector cell adds under its name: never the words the Stage cell and the Status chip already say (#1168). */
export function connectorSubLine(handoff: Pick<ProcessorHandoffItem, "status" | "outputPath">, says: string | null) {
  const line = says ?? (handoff.outputPath ? "Output received" : "Waiting for output");
  return line === processorStageLabel(handoff.status) ? undefined : line;
}

function processorStatusLabel(status: string) {
  return processorStageLabel(status);
}

function processorTone(handoff: ProcessorHandoffItem): NonNullable<ChipProps["tone"]> {
  if (handoffNeedsPerson(handoff)) return "bad";
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

export function dispatchStageLabel(dispatch: DownloadDispatchItem) {
  if (isDispatchFailure(dispatch)) return "Needs attention";
  if (dispatchWasImported(dispatch)) return "Imported";
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
  if (dispatch.failure) return integrationFailureActivityDetail(dispatch.failure);
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

function timelineEventDetail(event: { eventType: string; detailsJson: string | null }, alreadyShown?: string | null) {
  if (!event.detailsJson) return null;
  // The import failure is said once, in the Import line above (#1168).
  if (alreadyShown && event.detailsJson.includes(alreadyShown.slice(0, 40))) return null;
  if (event.eventType.toLowerCase() === "file_provenance_recorded") {
    return "Recorded the original release, rename, destination, matched rules and immutable preference-plan proof.";
  }
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

function transferProvenanceLabel(preferred: string | null, used: string | null, fallback: boolean | null) {
  if (!preferred && !used) return "Not recorded";
  const result = preferred && used && preferred !== used
    ? `${preferred} requested · ${used} used`
    : `${used ?? preferred} used`;
  return fallback ? `${result} · fallback required` : result;
}

function cleanupPolicyLabel(settings: PlatformSettingsSnapshot) {
  if (settings.cleanupRemoveClientEntryAfterThreshold || settings.cleanupPurgePayloadAfterThreshold) return "automatic removal enabled";
  if (settings.cleanupBlockReleaseAfterThreshold || settings.cleanupQueueReplacementAfterThreshold) return "automatic safeguards enabled";
  return "manual review before removal";
}

function PolicyFact({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <div className="rounded-lg border border-hairline bg-surface-1 px-3 py-2">
      <span className="block text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground">{label}</span>
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
      tone: handoffNeedsPerson(handoff) ? "bad" : handoff.outputPath ? "ok" : "info",
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
        detail: item.errorMessage
          ?? (item.failure
            ? integrationFailureActivityDetail(item.failure)
            : `Client reported: ${clientHistoryOutcomeLabel(item.outcome)}`),
        extra: [
          historySourceLabel(item.historySource),
          item.failure ? integrationFailureShortLabel(item.failure) : undefined,
          item.externalId ? `External ID ${item.externalId}` : undefined,
          item.sizeBytes ? formatBytes(item.sizeBytes) : undefined
        ].filter(Boolean).join(" · ") || undefined,
        tone: item.errorMessage || item.failure ? "bad" : "ok",
        status: clientHistoryOutcomeLabel(item.outcome),
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
      status: eventLabel(event),
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

function historySourceLabel(source: string | undefined) {
  switch (source) {
    case "native":
      return "Native client history";
    case "dispatch-derived":
      return "Deluno dispatch history";
    case "inferred":
      return "Inferred history";
    case "queue-derived":
    default:
      return "Queue-derived history";
  }
}

function jobTitle(job: JobQueueItem) {
  const parsed = parseImportJobPayload(job.payloadJson);
  return parsed?.title || parsed?.fileName || "A file import (no name recorded)";
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

function parseActivityDetails(detailsJson: string | null): Record<string, unknown> | null {
  if (!detailsJson) return null;
  try {
    const parsed: unknown = JSON.parse(detailsJson);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

export function buildImportRequest(
  item: DownloadQueueItem,
  libraries: LibraryItem[],
  dispatch: DownloadDispatchItem | null = null
): ImportPreviewRequest {
  return {
    sourcePath: resolveImportSourcePath(item, libraries) || "",
    fileName: inferImportFileName(item),
    mediaType: item.mediaType === "tv" ? "tv" : "movies",
    // A TV row sends no title or year: the queue title is a release name, and
    // sent as a hint it is taken for a title the person typed and a show folder
    // is built from it. The server matches the library's show from the files'
    // own names instead (#1171). A grab (dispatchId / seriesId) names it exactly.
    title: item.mediaType === "tv" ? null : item.title || null,
    year: item.mediaType === "tv" ? null : inferYear(item.releaseName),
    genres: null,
    tags: null,
    studio: null,
    originalLanguage: null,
    // So the preview names it as the import will: as the title grabbed (#617).
    dispatchId: dispatch?.id ?? null,
    seriesId: item.mediaType === "tv" && dispatch?.entityType.toLowerCase() === "series"
      ? dispatch.entityId
      : null
  };
}

export function findDispatchForQueueItem(
  item: DownloadQueueItem,
  dispatches: DownloadDispatchItem[]
): DownloadDispatchItem | null {
  const clientDispatches = dispatches.filter((dispatch) =>
    dispatch.downloadClientId.toLowerCase() === item.clientId.toLowerCase()
  );
  return clientDispatches.find((dispatch) =>
    dispatch.torrentHashOrItemId?.toLowerCase() === item.id.toLowerCase()
  ) ?? clientDispatches.find((dispatch) =>
    comparableReleaseName(dispatch.releaseName) === comparableReleaseName(item.releaseName)
  ) ?? null;
}

/**
 * The five optional queue columns (#796), read from the matched dispatch's
 * decision telemetry, each falling back to "—" rather than a blank cell when
 * Deluno has no answer (an older dispatch recorded before a column existed,
 * or a release name that names no language). Language comes from the
 * backend's own release parser (Deluno.Quality's
 * ReleasePreferenceFactFactory.DetectLanguageFromReleaseName, read back as
 * decisionLanguage), recorded once at grab time — never a second copy of the
 * token list in the web app.
 */
export interface QueueRowExtras {
  quality: string;
  customFormat: string;
  language: string;
  size: string;
  timeleft: string;
}

export function queueRowExtras(
  item: DownloadQueueItem,
  dispatch: DownloadDispatchItem | null,
  transferring: boolean
): QueueRowExtras {
  return {
    quality: dispatch?.decisionQuality ?? "—",
    customFormat: dispatch?.decisionCustomFormatScore != null ? String(dispatch.decisionCustomFormatScore) : "—",
    language: dispatch?.decisionLanguage ?? "—",
    size: formatBytes(item.sizeBytes),
    timeleft: transferring && item.etaSeconds > 0 ? formatEta(item.etaSeconds) : "—"
  };
}

/**
 * A release name with its case and punctuation gone (#617). A client reports
 * the torrent's own name ("house.on.-haunted.-hill.-1959…") where the grab kept
 * the indexer's, and an exact match imported the film under the release name.
 */
export function comparableReleaseName(name: string) {
  return name.toLowerCase().replace(/[^\p{L}\p{N}]/gu, "");
}

function inferImportFileName(item: DownloadQueueItem) {
  if (!item.sourcePath) return null;
  const parts = item.sourcePath.split(/[\\/]/).filter(Boolean);
  const last = parts[parts.length - 1];
  // Only a real media file name: a folder such as "www.UIndex.org - Show S01E04"
  // has dots too, and sent as the file it hides the season and episode (#1171).
  return last && /\.(mkv|mp4|m4v|avi|mov|wmv|ts|m2ts|mpg|mpeg|webm|flv|iso)$/i.test(last) ? last : null;
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
    case "move-to-top":
      return "Move to top";
    case "move-up":
      return "Move up";
    case "move-down":
      return "Move down";
    case "move-to-bottom":
      return "Move to bottom";
    case "force-start":
      return "Force start";
    default:
      return "Remove";
  }
}

function isHealthyClient(status: string) {
  return status === "healthy" || status === "ok";
}

/** #815: the torrent's own reported tracker privacy, or "Unknown" when the client did not say. */
function privacyLabel(isPrivate: boolean | null | undefined) {
  return isPrivate === true ? "Private" : isPrivate === false ? "Public" : "Unknown";
}

/** Matches Settings -> Download health's own copy for each rule kind (settings-download-health-page.tsx). */
const HEALTH_RULE_KIND_LABELS: Record<string, string> = {
  "no-progress": "No progress",
  "too-slow": "Too slow",
  "low-peers": "Not enough peers",
  "dead-torrent": "Dead torrent"
};

/** Matches Settings -> Download health's own copy for each action preset (settings-download-health-page.tsx). */
const HEALTH_RULE_ACTION_LABELS: Record<string, string> = {
  "just-tell-me": "Just tell me",
  "retry-elsewhere": "Retry elsewhere, keep files",
  replace: "Replace it"
};

/**
 * #905: "which rule it is tripping, its strikes so far and when the next
 * strike or action is due". A finding outside the rule engine (the client
 * itself reporting stalled, for example) has no ruleId and no per-rule
 * action, so it only ever shows its own strike count.
 */
export function healthFindingRuleSummary(finding: DownloadHealthFinding, preferences: DisplayPreferences) {
  const strikes = `${finding.strikeCount} strike${finding.strikeCount === 1 ? "" : "s"} so far`;
  if (!finding.ruleId) return strikes;

  const rule = HEALTH_RULE_KIND_LABELS[finding.kind] ?? finding.kind;
  const action = finding.ruleActionPreset ? HEALTH_RULE_ACTION_LABELS[finding.ruleActionPreset] ?? finding.ruleActionPreset : null;
  const limit = finding.ruleStrikeLimit ? ` of ${finding.ruleStrikeLimit}` : "";
  const due = finding.nextStrikeUtc ? `next strike due ${formatPreferenceDateTime(finding.nextStrikeUtc, preferences)}` : null;

  return [
    `${rule} rule`,
    `${strikes}${limit}`,
    due,
    action ? `set to ${action.toLowerCase()}` : null
  ].filter(Boolean).join(" · ");
}

function formatEta(seconds: number) {
  if (seconds <= 0) return "—";
  if (seconds < 60) return `${Math.round(seconds)}s left`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m left`;
  return `${(seconds / 3600).toFixed(1)}h left`;
}

function formatAgo(value: string, preferences: ReturnType<typeof useDisplayPreferences>["preferences"]) {
  if (!preferences.showRelativeDates) return formatPreferenceDateTime(value, preferences);
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 60 * 48) return `${Math.round(minutes / 60)} h ago`;
  return `${Math.round(minutes / 1440)} d ago`;
}
