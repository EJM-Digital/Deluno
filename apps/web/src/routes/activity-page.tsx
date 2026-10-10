/**
 * Activity — one list of what happened to each title (#1114).
 *
 * Each row is one step for one title: when, title and step, detail, status. Status chips across the top
 * choose what the list holds, and the history filters narrow it together with the chip:
 *   - In progress: jobs for a title that are queued or running, and sends still with a client.
 *   - Done: the history, paged.
 *   - Failed: jobs for a title that stopped, and sends that were refused or failed.
 *   - Needs a decision: imports that could not be filed.
 *   - All: the open items first, then the history.
 * Jobs that are not about a title (library-wide refreshes, backups, scheduled passes) are on System.
 * Each row opens the detail rather than printing it inline, because an error string or a payload is
 * worth reading in full and worth nothing squeezed into a cell.
 *
 * A failed job is listed however old it is. It used to be found only among the latest 24, and
 * recurring work buries one within hours: the sidebar went on counting it while this screen said
 * "none failing" and offered nothing to press.
 *
 * Contracts: GET /api/jobs, /api/jobs?failed=true, /api/activity, /api/download-dispatches,
 * /api/movies/import-recovery, /api/series/import-recovery; POST /api/jobs/retry-failed,
 * /api/jobs/{id}/dismiss.
 */
import { FilterChips } from "../components/ui/filter-chips";
import { JobDrawer } from "../components/app/job-drawer";
import type { Tone } from "../lib/status-tones";
import { useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { defineSources, queryClient, sourceLoader, useRouteSources, type RouteSourceData, type RouteSourceState } from "../lib/route-cache";
import { emptyRecovery, sharedSources } from "../lib/shared-sources";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { useQuery } from "@tanstack/react-query";
import {
  ApiRequestError,
  fetchJson,
  fetchPageItems,
  type DownloadHealthRecord,
  type DownloadTelemetryOverview,
  type ProcessorHandoffItem,
  type ActivityEventItem,
  type DownloadDispatchItem,
  type JobQueueItem,
  type MovieImportRecoverySummary,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { getDispatchStatusLabel as formatDispatchStatus, getJobStatusLabel, isJobInProgress, isJobPending, isJobRetrying, isJobSuccessful, type JobStatus } from "../lib/job-status-constants";
import { dismissFailedJob, retryFailedJob } from "../lib/failed-jobs";
import { detailFields, fixLinkForActivity } from "../lib/detail-fields";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFacts, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import {
  ListCard,
  ListCell,
  ListEmpty,
  ListNameCell,
  ListRow,
  ListTable,
  type ListColumn
} from "../components/ui/list-card";
import { useListColumns } from "../lib/use-list-columns";
import { toast } from "../components/shell/toaster";
import { DownloadDispatchDrawer } from "../components/app/download-dispatch-drawer";
import { RealtimeGroups, useLiveEvent, useSignalREvent } from "../lib/use-signalr";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { formatDateTime as formatPreferenceDateTime, useDisplayPreferences } from "../lib/display-preferences";
import { eventLabel } from "../lib/activity-format";
import { recoveryDecisions, type AlertAction } from "../lib/alert-actions";
import { send } from "./dashboard/downloads-kit";
import { ActivityFilterTokens, ActivityFilters, useActivityFilterChoices, useActivityHistory } from "../components/app/activity-history";
import {
  ACTIVITY_DEFAULT_SORT,
  activityLinkForFile,
  activityLinkForTitle,
  activitySortFrom,
  activityStatusFromParams,
  paramsWithStatus,
  resolveSince,
  resolveUntil,
  sortActivityRows,
  type ActivityStatus
} from "../lib/activity-history";
import {
  downloadClientEntries,
  downloadHealthEntries,
  isLiveStep,
  processingEntries,
  type LiveStepEntry
} from "../lib/activity-live-steps";
import { isBackgroundJob } from "../lib/background-jobs";
import { dispatchStanding } from "../lib/dispatch-final-state";
import { describeEpisodeSearchGroup, groupEpisodeSearches, type EpisodeSearchGroup } from "../lib/episode-search-groups";
import { failureKindWords } from "../lib/failure-reasons";
import {
  fileNameFromPath,
  parseDeletedLeftoverPaths,
  parseSeasonPackSkippedFiles,
  SEASON_PACK_COMPLETED_CATEGORY,
  SEASON_PACK_LEFTOVERS_DELETED_CATEGORY
} from "../lib/season-pack-leftovers";

/**
 * Nothing here is read on a timer (#1179): the lists are read when the screen opens, and each one again, once,
 * when the server says what it shows changed (see the events in ActivityPageBody). The screen opens on what it last showed.
 */

export const activitySources = defineSources({
  jobs: { key: ["src", "jobs-100"], label: "Jobs", fetch: () => fetchPageItems<JobQueueItem>("/api/jobs?pageSize=100&includePayload=true") },
  failedJobs: { ...sharedSources.failedJobs, fallback: [] as JobQueueItem[] },
  dispatches: { key: ["src", "dispatches-20-archived"], label: "Sent downloads", fetch: () => fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=20&includeArchived=true"), fallback: [] as DownloadDispatchItem[] },
  movieRecovery: { ...sharedSources.movieRecovery, fallback: emptyRecovery as MovieImportRecoverySummary },
  seriesRecovery: { ...sharedSources.seriesRecovery, fallback: emptyRecovery as SeriesImportRecoverySummary }
});

export const activityLoader = sourceLoader(activitySources);

export function ActivityPage() {
  const state = useRouteSources(activitySources);
  return (
    <RouteSourceGate state={state}>
      {() => <ActivityPageBody data={state.data} revalidator={state.revalidator} />}
    </RouteSourceGate>
  );
}

type OpenStatus = Exclude<ActivityStatus, "all" | "done">;

/** One row of the list: one step for one title. */
interface ActivityRow {
  key: string;
  /** Where the row opens: a job, a send to a client, an import issue, or an event in the history. */
  kind: "job" | "group" | "dispatch" | "import" | "event" | "live";
  /** Set for the open items; history events are Done. */
  status: OpenStatus | "done";
  whenUtc: string;
  title: string;
  step?: string;
  detail?: string;
  /** A quieter second line under the detail. */
  detailSub?: string;
  pill: { tone: Tone; label: string };
  /** What a title or item filter compares against, for the open items. */
  entityId?: string | null;
  entityType?: string | null;
  open: () => void;
  selected: boolean;
}

/** The list pages a screenful at a time, so each heading sorts on the server: the whole history is in order, not only the rows on screen (#1119). */
const ACTIVITY_COLUMNS: Array<ListColumn<ActivityRow>> = [
  { id: "title", label: "Title", width: "minmax(0,1.2fr)", sort: "title" },
  { id: "what", label: "What happened", width: "minmax(0,2fr)", mobile: true, sort: "what" },
  { id: "when", label: "When", width: "120px", sort: "when" }
];

const STATUS_CHIPS: readonly { value: ActivityStatus; label: string }[] = [
  { value: "all", label: "All" },
  { value: "in-progress", label: "In progress" },
  { value: "done", label: "Done" },
  { value: "failed", label: "Failed" },
  { value: "needs-decision", label: "Needs a decision" }
];

const OPEN_PILL: Record<OpenStatus, { tone: Tone; label: string }> = {
  "in-progress": { tone: "info", label: "In progress" },
  failed: { tone: "bad", label: "Failed" },
  "needs-decision": { tone: "warn", label: "Needs a decision" }
};

/** What each chip would list, said plainly when it lists nothing. */
const EMPTY_STATUS: Record<ActivityStatus, { title: string; description: string }> = {
  all: { title: "Nothing has happened yet", description: "Every consequential thing Deluno does is recorded here, with the reason behind it." },
  done: { title: "Nothing has happened yet", description: "Every consequential thing Deluno does is recorded here, with the reason behind it." },
  "in-progress": { title: "Nothing in progress", description: "Searches, downloads and imports for your titles appear here while they run." },
  failed: { title: "Nothing has failed", description: "A search, download or import that stopped, and needs another go or a look, appears here." },
  "needs-decision": { title: "Nothing needs a decision", description: "Downloads that finish but cannot be filed — wrong quality, no match, corrupt — land here for you to decide." }
};

function ActivityPageBody({ data: loaderData, revalidator }: { data: RouteSourceData<typeof activitySources>; revalidator: RouteSourceState<typeof activitySources>["revalidator"] }) {
  // The chosen status is in the address too, so a link can land on it (#1114). No status means All; the links
  // from before the tabs became chips still work: section=jobs is In progress, imports is Needs a decision,
  // events is All.
  const [searchParams, setSearchParams] = useSearchParams();
  const filterChoices = useActivityFilterChoices();
  const status = activityStatusFromParams(searchParams);
  const setStatus = (next: ActivityStatus) => setSearchParams((current) => paramsWithStatus(next, current), { replace: true });
  const [openJobId, setOpenJobId] = useState<string | null>(null);
  // A show-wide search opened: the show it is for, so its episodes list inside it (#1180).
  const [openGroupId, setOpenGroupId] = useState<string | null>(null);
  const [openDispatchId, setOpenDispatchId] = useState<string | null>(null);
  const [openEvent, setOpenEvent] = useState<ActivityEventItem | null>(null);
  const [openImportCaseId, setOpenImportCaseId] = useState<string | null>(null);
  const [openLive, setOpenLive] = useState<LiveStepEntry | null>(null);
  const [busy, setBusy] = useState(false);
  const [decisionBusy, setDecisionBusy] = useState<string | null>(null);
  const [confirmingDecision, setConfirmingDecision] = useState<AlertAction | null>(null);
  const [confirmingLeftoverDelete, setConfirmingLeftoverDelete] = useState(false);
  const [deletingLeftovers, setDeletingLeftovers] = useState(false);
  const { preferences } = useDisplayPreferences();

  const historyOn = status === "all" || status === "done";
  // Newest first until a heading is chosen. The history is sorted by the server; the rows built here are sorted below.
  const columnsList = useListColumns<ActivityRow>("activity", ACTIVITY_COLUMNS, undefined, { serverSort: true });
  const sort = activitySortFrom(columnsList.sort);
  const list = { ...columnsList, sort: columnsList.sort ?? ACTIVITY_DEFAULT_SORT };
  const { filters, setFilters, clearFilters, history, events: historyEvents, folded: foldedActivity, filtered } = useActivityHistory({ enabled: historyOn, onOpenEvent: setOpenEvent, sort });

  // Each list is read again, once, a moment after the server says what it shows changed; a burst is one read (#1179).
  const readJobs = useCoalescedRevalidate(() => {
    void queryClient.invalidateQueries({ queryKey: activitySources.jobs.key });
    void queryClient.invalidateQueries({ queryKey: activitySources.failedJobs.key });
  }, 1_000);
  const readDispatches = useCoalescedRevalidate(() => void queryClient.invalidateQueries({ queryKey: activitySources.dispatches.key }), 1_000);
  const readRecovery = useCoalescedRevalidate(() => {
    void queryClient.invalidateQueries({ queryKey: activitySources.movieRecovery.key });
    void queryClient.invalidateQueries({ queryKey: activitySources.seriesRecovery.key });
  }, 1_000);
  const readProcessing = useCoalescedRevalidate(() => void queryClient.invalidateQueries({ queryKey: ["activity-live", "processing"] }), 1_000);
  const readDownloadHealth = useCoalescedRevalidate(() => void queryClient.invalidateQueries({ queryKey: ["activity-live", "download-health"] }), 1_000);
  const readClients = useCoalescedRevalidate(() => void queryClient.invalidateQueries({ queryKey: ["activity-live", "download-client"] }), 1_500);

  // Open while this screen is, so the server knows someone is watching and sends these (#1179).
  useLiveEvent("JobChanged", readJobs);
  useLiveEvent("JourneyStageChanged", readDispatches);
  useLiveEvent("RecoveryCaseChanged", readRecovery);
  useLiveEvent("ProcessorHandoffChanged", readProcessing);
  useLiveEvent("ClientStateChanged", readClients);
  useLiveEvent("DownloadProgress", readClients);
  useLiveEvent("QueueItemAdded", readClients);
  useLiveEvent("QueueItemRemoved", readClients);
  useLiveEvent("QueueItemStatusChanged", readClients);
  useSignalREvent("DownloadHealthChanged", RealtimeGroups.Dashboard, readDownloadHealth);
  useSignalREvent("DispatchGrabAttempt", RealtimeGroups.Queue, readDispatches);
  useSignalREvent("DispatchGrabCompleted", RealtimeGroups.Queue, readDispatches);
  useSignalREvent("DispatchDetected", RealtimeGroups.Queue, readDispatches);
  useSignalREvent("DispatchImportStarted", RealtimeGroups.Queue, readDispatches);
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, readDispatches);

  const { dispatches, failedJobs: failed, movieRecovery, seriesRecovery } = loaderData;
  // A job with another attempt coming belongs with the work in flight, not
  // with the ones that stopped and need a person (#562).
  const jobs = loaderData.jobs;
  const stopped = useMemo(() => failed.filter((job) => !isJobRetrying(job)), [failed]);

  const importCases = useMemo(
    () => [
      ...movieRecovery.recentCases.map((item) => ({ ...item, mediaLabel: "Movie" })),
      ...seriesRecovery.recentCases.map((item) => ({ ...item, mediaLabel: "TV" }))
    ],
    [movieRecovery.recentCases, seriesRecovery.recentCases]
  );

  const openJob = failed.find((job) => job.id === openJobId) ?? jobs.find((job) => job.id === openJobId) ?? null;
  const openDispatch = dispatches.find((dispatch) => dispatch.id === openDispatchId) ?? null;
  const openImportCase = importCases.find((item) => `${item.mediaLabel}-${item.id}` === openImportCaseId) ?? null;

  // #916: a routine background job's queued/started/completed rows fold into
  // the one that answers "what happened" (its completion, or its start while
  // still running), with the other two kept for the drawer. On top of that,
  // a Movies run and a TV run of the same job finishing within fifteen
  // minutes of each other fold into one line too (James, 27 Sep 2026) — the
  // drawer is where either original run's own wording still shows.
  const openEventFoldedFrom = foldedActivity.find((folded) => folded.event.id === openEvent?.id)?.foldedFrom ?? [];

  // #889: a season-pack import names, in the same event, every file that did
  // not go in - an extra the catalogue never listed, or one that matched an
  // episode but had a problem of its own. A path already confirmed deleted
  // (recorded on its own event) drops off the list rather than staying
  // there for the life of the browser tab.
  const deletedLeftoverPaths = useMemo(() => {
    const paths = new Set<string>();
    for (const event of historyEvents) {
      if (event.category !== SEASON_PACK_LEFTOVERS_DELETED_CATEGORY) continue;
      for (const path of parseDeletedLeftoverPaths(event.detailsJson)) paths.add(path);
    }
    return paths;
  }, [historyEvents]);
  const openEventLeftovers = useMemo(() => {
    if (!openEvent || openEvent.category !== SEASON_PACK_COMPLETED_CATEGORY) return [];
    return parseSeasonPackSkippedFiles(openEvent.detailsJson).filter((file) => !deletedLeftoverPaths.has(file.sourcePath));
  }, [openEvent, deletedLeftoverPaths]);

  // The open items: what is still going, what stopped, and what needs a decision. Jobs that are not about a
  // title are not listed here (System lists them); an import is about a title even when its job does not name one.
  const openRows = useMemo<ActivityRow[]>(() => {
    const rows: ActivityRow[] = [];
    const jobWhen = (job: JobQueueItem) => job.completedUtc ?? job.startedUtc ?? job.scheduledUtc;
    const activeJobs: JobQueueItem[] = [];
    for (const job of jobs) {
      if (isBackgroundJob(job)) continue;
      if (isJobInProgress(job.status as JobStatus) || isJobPending(job.status as JobStatus) || isJobRetrying(job)) activeJobs.push(job);
    }
    const { groups, rest } = groupEpisodeSearches(activeJobs, jobs.filter((job) => isJobSuccessful(job.status as JobStatus)));
    for (const group of groups) {
      rows.push({
        key: `group-${group.showId}`, kind: "group", status: "in-progress", whenUtc: group.whenUtc, title: group.showTitle,
        step: "Search for episodes · The TV library", detail: describeEpisodeSearchGroup(group), pill: OPEN_PILL["in-progress"],
        open: () => setOpenGroupId(group.showId), selected: openGroupId === group.showId
      });
    }
    for (const job of rest) {
      const retrying = isJobRetrying(job);
      const state = retrying ? `Retrying, attempt ${job.attempts} of ${job.maxAttempts}` : getJobStatusLabel(job.status as JobStatus);
      rows.push({
        key: `job-${job.id}`, kind: "job", status: "in-progress", whenUtc: jobWhen(job), title: job.target ?? job.label, step: job.target ? `${job.label} · ${job.sourceLabel}` : job.sourceLabel,
        detail: job.lastError ? `${state} · ${job.lastError}` : state, pill: OPEN_PILL["in-progress"],
        entityId: job.relatedEntityId, entityType: job.relatedEntityType,
        open: () => setOpenJobId(job.id), selected: openJobId === job.id
      });
    }
    for (const job of stopped) {
      if (isBackgroundJob(job)) continue;
      rows.push({
        key: `job-${job.id}`, kind: "job", status: "failed", whenUtc: jobWhen(job), title: job.target ?? job.label, step: job.target ? `${job.label} · ${job.sourceLabel}` : job.sourceLabel,
        detail: job.lastError ?? `${getJobStatusLabel(job.status as JobStatus)} after ${job.attempts} ${job.attempts === 1 ? "attempt" : "attempts"}`,
        pill: OPEN_PILL.failed, entityId: job.relatedEntityId, entityType: job.relatedEntityType,
        open: () => setOpenJobId(job.id), selected: openJobId === job.id
      });
    }
    for (const dispatch of dispatches) {
      const standing = dispatchStanding(dispatch, dispatches);
      if (!standing) continue;
      const dispatchStatus = standing.status;
      rows.push({
        key: `dispatch-${dispatch.id}`, kind: "dispatch", status: dispatchStatus, whenUtc: dispatch.createdUtc, title: dispatch.releaseName,
        step: `Sent to ${dispatch.downloadClientName}`,
        detail: standing.detail ?? (dispatchStatus === "in-progress"
          ? `${formatDispatchStatus(dispatch.status)} · ${dispatch.indexerName}`
          : dispatch.grabMessage ?? dispatch.importFailureMessage ?? `${formatDispatchStatus(dispatch.status)} · ${dispatch.indexerName}`),
        pill: standing.pill, entityId: dispatch.entityId, entityType: dispatch.entityType,
        open: () => setOpenDispatchId(dispatch.id), selected: openDispatchId === dispatch.id
      });
    }
    for (const item of importCases) {
      const id = `${item.mediaLabel}-${item.id}`;
      rows.push({
        key: `import-${id}`, kind: "import", status: "needs-decision", whenUtc: item.detectedUtc, title: item.title, step: failureKindWords(item.failureKind),
        detail: item.summary, pill: OPEN_PILL["needs-decision"],
        open: () => setOpenImportCaseId(id), selected: openImportCaseId === id
      });
    }
    return rows;
  }, [jobs, stopped, dispatches, importCases, openJobId, openGroupId, openDispatchId, openImportCaseId]);

  // The show-wide search opened, rebuilt from the same job reads so it follows each JobChanged push (#1180).
  const openGroup = useMemo<EpisodeSearchGroup | null>(() => {
    if (!openGroupId) return null;
    const active = jobs.filter((job) => !isBackgroundJob(job) && (isJobInProgress(job.status as JobStatus) || isJobPending(job.status as JobStatus) || isJobRetrying(job)));
    return groupEpisodeSearches(active, jobs.filter((job) => isJobSuccessful(job.status as JobStatus))).groups.find((group) => group.showId === openGroupId) ?? null;
  }, [jobs, openGroupId]);

  // Processing, Download health and Download client are steps of their own, built from what Transfers reads (#1115).
  // Each is read only while its step is chosen, so the page does not ask for them otherwise.
  const liveStep = isLiveStep(filters.type) ? filters.type : null;
  const handoffs = useQuery({
    queryKey: ["activity-live", "processing"],
    queryFn: () => fetchJson<ProcessorHandoffItem[]>("/api/integrations/processors/handoffs?take=60").catch((error) => {
      if (error instanceof ApiRequestError && error.status === 404) return [] as ProcessorHandoffItem[];
      throw error;
    }),
    enabled: liveStep === "processing"
  });
  const healthRecords = useQuery({
    queryKey: ["activity-live", "download-health"],
    queryFn: () => fetchPageItems<DownloadHealthRecord>("/api/download-health?pageSize=60"),
    enabled: liveStep === "download-health"
  });
  const clientTelemetry = useQuery({
    queryKey: ["activity-live", "download-client"],
    queryFn: () => fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry"),
    enabled: liveStep === "download-client"
  });
  const liveQuery = liveStep === "processing" ? handoffs : liveStep === "download-health" ? healthRecords : liveStep === "download-client" ? clientTelemetry : null;
  const liveEntries = useMemo<LiveStepEntry[]>(() => {
    if (liveStep === "processing") return processingEntries(handoffs.data ?? []);
    if (liveStep === "download-health") return downloadHealthEntries(healthRecords.data ?? []);
    if (liveStep === "download-client") return clientTelemetry.data ? downloadClientEntries(clientTelemetry.data) : [];
    return [];
  }, [liveStep, handoffs.data, healthRecords.data, clientTelemetry.data]);
  const liveRows = useMemo<ActivityRow[]>(() => {
    const since = filterDay(filters.since, "since");
    const until = filterDay(filters.until, "until");
    const needle = filters.title.trim().toLowerCase();
    return liveEntries
      .filter((entry) => {
        if (needle && !`${entry.title} ${entry.stepLabel}`.toLowerCase().includes(needle)) return false;
        const when = new Date(entry.whenUtc).getTime();
        if (since !== null && when < since) return false;
        if (until !== null && when >= until) return false;
        return true;
      })
      .map((entry) => ({
        key: entry.key, kind: "live" as const, status: entry.status, whenUtc: entry.whenUtc, title: entry.title, step: entry.stepLabel,
        detail: entry.detail, detailSub: entry.detailSub,
        pill: entry.status === "done" ? { tone: "ok" as Tone, label: "Done" } : OPEN_PILL[entry.status],
        open: () => setOpenLive(entry), selected: openLive?.key === entry.key
      }));
  }, [liveEntries, filters.since, filters.until, filters.title, openLive]);

  // The filters narrow the open items too, where they can: the title, the one title chosen, and the days. A filter
  // that is about the history alone (kind of event, file, source, outcome, reason) leaves no open item matching.
  const matchingOpen = useMemo(() => {
    const historyOnly = Boolean(filters.type || filters.file || filters.source || filters.outcome || filters.reason);
    if (historyOnly || liveStep) return [];
    const since = filterDay(filters.since, "since");
    const until = filterDay(filters.until, "until");
    const needle = filters.title.trim().toLowerCase();
    return openRows.filter((row) => {
      if (needle && !`${row.title} ${row.step ?? ""}`.toLowerCase().includes(needle)) return false;
      if (filters.item && (row.entityId !== filters.item || (filters.itemType && row.entityType && row.entityType !== filters.itemType))) return false;
      const when = new Date(row.whenUtc).getTime();
      if (since !== null && when < since) return false;
      if (until !== null && when >= until) return false;
      return true;
    });
  }, [openRows, filters]);

  const countable = liveStep ? liveRows : matchingOpen;
  const counts: Partial<Record<ActivityStatus, number>> = {
    "in-progress": countable.filter((row) => row.status === "in-progress").length,
    failed: countable.filter((row) => row.status === "failed").length,
    "needs-decision": countable.filter((row) => row.status === "needs-decision").length,
    ...(liveStep ? { done: countable.filter((row) => row.status === "done").length } : {})
  };

  const historyRows = useMemo<ActivityRow[]>(
    () => foldedActivity.map(({ event, foldedFrom }) => ({
      key: `event-${event.id}`, kind: "event", status: "done", whenUtc: event.createdUtc,
      title: event.title ?? eventLabel(event),
      step: event.title ? eventLabel(event) : event.sourceName ?? undefined,
      detail: event.message,
      detailSub: [event.detail, foldedFrom.length ? `+${foldedFrom.length} more step${foldedFrom.length === 1 ? "" : "s"} for this run — open to see them` : null].filter(Boolean).join(" · ") || undefined,
      pill: event.outcome === "problem" ? { tone: "bad" as Tone, label: "Went wrong" } : { tone: "ok" as Tone, label: "Done" },
      open: () => setOpenEvent(event), selected: openEvent?.id === event.id
    })),
    [foldedActivity, openEvent]
  );

  const sortedOpen = (rowsToSort: ActivityRow[]) => sortActivityRows(rowsToSort, sort);
  const rows: ActivityRow[] =
    liveStep ? sortedOpen(status === "all" ? liveRows : liveRows.filter((row) => row.status === status))
    : status === "all" ? [...sortedOpen(matchingOpen), ...historyRows]
    : status === "done" ? historyRows
    : sortedOpen(matchingOpen.filter((row) => row.status === status));

  const historyLoading = liveStep ? Boolean(liveQuery?.isPending) : historyOn && history.isPending;
  const historyBroken = liveStep ? Boolean(liveQuery?.isError) : historyOn && history.isError;
  const failedTitleJobs = counts.failed ?? 0;
  // The card says what it holds in words, so it never repeats the page's title: the chosen status and how many.
  const chosenCount = counts[status];
  const cardTitle = status === "all" ? "Everything" : `${STATUS_CHIPS.find((chip) => chip.value === status)?.label ?? ""}${chosenCount !== undefined ? ` · ${chosenCount}` : ""}`;

  async function handleRetryFailedJobs() {
    setBusy(true);
    try {
      const response = await authedFetch("/api/jobs/retry-failed", { method: "POST" });
      if (!response.ok) throw new Error("Failed jobs could not be requeued.");
      const result = (await response.json()) as { retried?: number };
      toast.success(`${result.retried ?? 0} failed job${result.retried === 1 ? "" : "s"} requeued`);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Retry failed.");
    } finally {
      setBusy(false);
    }
  }

  /**
   * Confirmed leftovers from a season pack (#889): deletes exactly the files
   * this event named as not matched, from wherever the import left them.
   * What already imported is never touched.
   */
  async function handleDeleteLeftovers() {
    if (openEventLeftovers.length === 0) return;
    setDeletingLeftovers(true);
    try {
      const response = await authedFetch("/api/filesystem/import/season-pack/leftovers/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ paths: openEventLeftovers.map((file) => file.sourcePath) })
      });
      const body = (await response.json().catch(() => null)) as { deleted?: number; refused?: string[] } | null;
      if (!response.ok) throw new Error("Those leftover files could not be deleted.");
      setConfirmingLeftoverDelete(false);
      const deletedCount = body?.deleted ?? 0;
      if (body?.refused?.length) {
        toast.error(`Deleted ${deletedCount} of ${openEventLeftovers.length}. ${body.refused.length} could not be removed.`);
      } else {
        toast.success(`Deleted ${deletedCount} leftover file${deletedCount === 1 ? "" : "s"}.`);
      }
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Those leftover files could not be deleted.");
    } finally {
      setDeletingLeftovers(false);
    }
  }

  /** Runs one decision on an import case through the same requests and words the bell uses, then asks the sources again. */
  async function runDecision(action: AlertAction) {
    const request = action.request;
    if (!request) return;
    setDecisionBusy(action.id);
    try {
      const result = await send(request.path, request.failure, { method: request.method, body: request.body });
      toast.success(result?.message ?? request.done);
      setConfirmingDecision(null);
      setOpenImportCaseId(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : request.failure);
    } finally {
      setDecisionBusy(null);
    }
  }

  async function handleRetryJob(job: JobQueueItem) {
    setBusy(true);
    try {
      await retryFailedJob(job.id);
      toast.success(`${job.label} is being tried again`);
      setOpenJobId(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That job could not be tried again.");
    } finally {
      setBusy(false);
    }
  }

  async function handleDismissJob(job: JobQueueItem) {
    setBusy(true);
    try {
      await dismissFailedJob(job.id);
      toast.success(`${job.label} dismissed`);
      setOpenJobId(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That job could not be dismissed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <ActivityFilters
        filters={filters}
        setFilters={setFilters}
        chips={
          <FilterChips<ActivityStatus>
            label="Show"
            value={status}
            onValueChange={setStatus}
            options={STATUS_CHIPS.map((chip) => ({ ...chip, count: counts[chip.value] }))}
          />
        }
      />

      <ListCard
        title={<span>{cardTitle}</span>}
        actions={
          status === "failed" ? (
            <Button type="button" size="sm" className="shrink-0" onClick={() => void handleRetryFailedJobs()} disabled={busy || failedTitleJobs === 0}>
              Retry failed jobs
            </Button>
          ) : null
        }
      >
        <ActivityFilterTokens filters={filters} setFilters={setFilters} clearFilters={clearFilters} />

        {rows.length === 0 && historyLoading ? (
          <ListEmpty title="Reading the history" description="This takes a moment." />
        ) : rows.length === 0 && historyBroken ? (
          <ListEmpty
            title="The history could not be read"
            description="Deluno could not load it just now."
            actions={
              <Button type="button" variant="outline" size="sm" onClick={() => void (liveQuery ?? history).refetch()}>
                Try again
              </Button>
            }
          />
        ) : rows.length === 0 ? (
          filtered ? (
            <ListEmpty
              title="Nothing matches these filters"
              description="Widen the dates, or clear a filter, to see more."
              actions={
                <Button type="button" variant="outline" size="sm" onClick={clearFilters}>
                  Clear filters
                </Button>
              }
            />
          ) : (
            <ListEmpty title={EMPTY_STATUS[status].title} description={EMPTY_STATUS[status].description} />
          )
        ) : (
          <>
            <ListTable list={list}>
              {rows.map((row) => (
                <ListRow key={row.key} onClick={row.open} selected={row.selected}>
                  <ListNameCell name={row.title} sub={row.step} />
                  <ListCell mobile className="max-md:max-w-38">
                    <div className="flex min-w-0 items-center gap-2">
                      <Chip tone={row.pill.tone}>{row.pill.label}</Chip>
                      <span className="min-w-0 truncate text-foreground">{row.detail ?? "—"}</span>
                    </div>
                    {row.detailSub ? <span className="mt-0.5 block truncate text-(length:--type-caption) text-muted-foreground">{row.detailSub}</span> : null}
                  </ListCell>
                  <ListCell primary={formatPreferenceDateTime(row.whenUtc, preferences)} />
                </ListRow>
              ))}
            </ListTable>
            {historyOn && !liveStep && historyBroken ? (
              <div className="flex items-center justify-center gap-3 border-t border-hairline p-3 text-(length:--type-caption) text-muted-foreground">
                The history could not be read just now.
                <Button type="button" variant="outline" size="sm" onClick={() => void (liveQuery ?? history).refetch()}>
                  Try again
                </Button>
              </div>
            ) : null}
            {historyOn && !liveStep && history.hasNextPage ? (
              <div className="flex justify-center border-t border-hairline p-3">
                <Button type="button" variant="outline" size="sm" onClick={() => void history.fetchNextPage()} disabled={history.isFetchingNextPage}>
                  {history.isFetchingNextPage ? "Loading" : "Show older events"}
                </Button>
              </div>
            ) : null}
          </>
        )}
      </ListCard>

      <Drawer
        open={openGroup !== null}
        onOpenChange={(next) => {
          if (!next) setOpenGroupId(null);
        }}
        title={openGroup ? openGroup.showTitle : "Search"}
        description={openGroup ? describeEpisodeSearchGroup(openGroup) : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenGroupId(null)} />}
      >
        {openGroup ? (
          <DrawerSection title="Episodes" aside={String(openGroup.total)}>
            <ul className="flex flex-col divide-y divide-hairline">
              {openGroup.jobs.map((job) => (
                <li key={job.id}>
                  <button
                    type="button"
                    className="flex w-full items-center justify-between gap-3 py-2 text-left text-(length:--type-body-sm) hover:text-foreground"
                    onClick={() => {
                      setOpenGroupId(null);
                      setOpenJobId(job.id);
                    }}
                  >
                    <span className="truncate">{job.target}</span>
                    <span className="shrink-0 text-muted-foreground">{getJobStatusLabel(job.status as JobStatus)}</span>
                  </button>
                </li>
              ))}
            </ul>
          </DrawerSection>
        ) : null}
      </Drawer>

      <JobDrawer job={openJob} busy={busy} onClose={() => setOpenJobId(null)} onDismiss={(job) => void handleDismissJob(job)} onRetry={(job) => void handleRetryJob(job)} />

      <DownloadDispatchDrawer
        dispatch={openDispatch}
        onClose={() => setOpenDispatchId(null)}
        onBlocked={() => revalidator.revalidate()}
      />

      <Drawer
        open={openLive !== null}
        onOpenChange={(next) => {
          if (!next) setOpenLive(null);
        }}
        title={openLive?.title ?? "Detail"}
        description={openLive ? `${openLive.stepLabel} · ${formatPreferenceDateTime(openLive.whenUtc, preferences)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenLive(null)} />}
      >
        {openLive ? (
          <>
            <DrawerSection title="What happened">
              <p className="text-(length:--type-body-sm) leading-relaxed text-foreground">{openLive.detail}</p>
            </DrawerSection>
            <DrawerSection title="Details">
              {/* The title above is cut to two lines on a phone; its full words are one tap away here. */}
              <DrawerFacts items={openLive.facts.some((fact) => fact.value === openLive.title) ? openLive.facts : [{ label: "Title", value: openLive.title }, ...openLive.facts]} />
              <Link to="/queue" onClick={() => setOpenLive(null)} className="mt-2 inline-block text-(length:--type-caption) font-semibold text-primary hover:underline">
                Open Transfers
              </Link>
            </DrawerSection>
          </>
        ) : null}
      </Drawer>

      <Drawer
        open={openEvent !== null}
        onOpenChange={(next) => {
          if (!next) {
            setOpenEvent(null);
            setConfirmingLeftoverDelete(false);
          }
        }}
        title={openEvent?.message ?? "Event"}
        description={openEvent ? `${eventLabel(openEvent)} · ${formatPreferenceDateTime(openEvent.createdUtc, preferences)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenEvent(null)} />}
      >
        {openEvent ? (
          <>
            <DrawerSection title="What happened">
              <p className="text-(length:--type-body-sm) leading-relaxed text-foreground">{openEvent.message}</p>
              {openEvent.detail ? (
                <p className="text-(length:--type-caption) leading-relaxed text-muted-foreground">{openEvent.detail}</p>
              ) : null}
              {fixLinkForActivity(openEvent.category, openEvent.outcome) ? (
                <Link
                  to={fixLinkForActivity(openEvent.category, openEvent.outcome)!.to}
                  onClick={() => setOpenEvent(null)}
                  className="mt-2 inline-block text-(length:--type-caption) font-semibold text-primary hover:underline"
                >
                  {fixLinkForActivity(openEvent.category, openEvent.outcome)!.label}
                </Link>
              ) : null}
            </DrawerSection>

            {openEvent.title || openEvent.filePath || openEvent.sourceName || openEvent.reasonGroup ? (
              <DrawerSection title="What it was about">
                <DrawerFacts
                  items={[
                    ...(openEvent.title ? [{ label: "Title", value: openEvent.title }] : []),
                    ...(openEvent.sourceName ? [{ label: "From", value: openEvent.sourceName }] : []),
                    ...(openEvent.filePath ? openEvent.filePath.split(/[\r\n;]+/).map((path, index) => ({
                      label: index === 0 ? "File" : "",
                      value: (
                        <Link to={activityLinkForFile(path)} onClick={() => setOpenEvent(null)} title="Everything that happened to this file" className="text-primary hover:underline">
                          {path}
                        </Link>
                      ),
                      mono: true
                    })) : []),
                    ...(openEvent.reasonGroup ? [{ label: "Reason", value: filterChoices.data?.reasons.find((reason) => reason.value === openEvent.reasonGroup)?.label ?? openEvent.reasonGroup }] : [])
                  ]}
                />
                {openEvent.title && openEvent.relatedEntityId && (openEvent.relatedEntityType === "movie" || openEvent.relatedEntityType === "series") ? (
                  <Link
                    to={activityLinkForTitle(openEvent.title, openEvent.relatedEntityType, openEvent.relatedEntityId)}
                    onClick={() => setOpenEvent(null)}
                    className="mt-2 inline-block text-(length:--type-caption) font-semibold text-primary hover:underline"
                  >
                    Everything about {openEvent.title}
                  </Link>
                ) : null}
              </DrawerSection>
            ) : null}

            {openEventLeftovers.length > 0 ? (
              <DrawerSection title="What didn't match" aside={`${openEventLeftovers.length} file${openEventLeftovers.length === 1 ? "" : "s"}`}>
                <ul className="flex flex-col gap-2">
                  {openEventLeftovers.map((file) => (
                    <li key={file.sourcePath} className="text-(length:--type-body-sm) leading-relaxed text-foreground">
                      <span className="font-medium">{fileNameFromPath(file.sourcePath)}</span>
                      <span className="block text-(length:--type-caption) text-muted-foreground">{file.reason}</span>
                    </li>
                  ))}
                </ul>
                <div className="mt-3">
                  <DrawerDanger
                    title="Delete leftover files"
                    description="Removes exactly these files from the download's folder. What already imported is untouched."
                    action={
                      <Button type="button" variant="outline" size="sm" onClick={() => setConfirmingLeftoverDelete(true)} disabled={deletingLeftovers}>
                        Delete leftover files
                      </Button>
                    }
                  />
                </div>
              </DrawerSection>
            ) : null}

            {detailFields(openEvent.detailsJson).length ? (
              <DrawerSection title="Detail">
                <DrawerFacts items={detailFields(openEvent.detailsJson).map((field) => ({ label: field.label, value: field.value, mono: field.mono }))} />
              </DrawerSection>
            ) : null}

            {openEventFoldedFrom.length ? (
              <DrawerSection title="The rest of this run">
                <ul className="flex flex-col gap-2">
                  {openEventFoldedFrom.map((step) => (
                    <li key={step.id} className="text-(length:--type-body-sm) leading-relaxed text-muted-foreground">
                      <span className="text-foreground">{step.message}</span>
                      <span className="ml-1.5 text-(length:--type-caption)">
                        {formatPreferenceDateTime(step.createdUtc, preferences)}
                      </span>
                    </li>
                  ))}
                </ul>
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

      <Drawer
        open={openImportCase !== null}
        onOpenChange={(next) => {
          if (!next) setOpenImportCaseId(null);
        }}
        title={openImportCase ? `${failureKindWords(openImportCase.failureKind)} · ${openImportCase.title}` : "Import issue"}
        description={openImportCase ? `${openImportCase.mediaLabel} · found ${formatPreferenceDateTime(openImportCase.detectedUtc, preferences)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenImportCaseId(null)} />}
      >
        {openImportCase ? (
          <>
            {/*
              #798: the row already names the failure code in plain words and
              shows its summary as the sub-line; this is where the summary is
              read in full rather than truncated, alongside what Deluno
              recommends and the raw evidence it kept, so "Quality rejected"
              never has to stand for the actual reason on its own.
            */}
            <DrawerSection title="Why this needs a decision">
              <p className="whitespace-pre-wrap text-(length:--type-body-sm) leading-relaxed text-foreground">{openImportCase.summary}</p>
            </DrawerSection>

            <DrawerSection title="What Deluno recommends">
              <p className="text-(length:--type-body-sm) leading-relaxed text-muted-foreground">{openImportCase.recommendedAction}</p>
            </DrawerSection>

            <DrawerSection title="Decide">
              <div className="flex flex-wrap gap-2">
                {recoveryDecisions({ ...openImportCase, mediaType: openImportCase.mediaLabel === "Movie" ? "movie" : "series" }).map((action) => (
                  action.to ? (
                    <Button key={action.id} asChild variant="outline" size="sm" title={action.hint}>
                      <Link to={action.to} onClick={() => setOpenImportCaseId(null)}>{action.label}</Link>
                    </Button>
                  ) : (
                  <Button
                    key={action.id}
                    type="button"
                    variant="outline"
                    size="sm"
                    title={action.hint}
                    aria-label={`${action.label}: ${openImportCase.title}`}
                    disabled={decisionBusy !== null}
                    onClick={() => (action.confirm ? setConfirmingDecision(action) : void runDecision(action))}
                  >
                    {action.label}
                  </Button>
                  )
                ))}
              </div>
            </DrawerSection>

            {detailFields(openImportCase.detailsJson).length ? (
              <DrawerSection title="Evidence">
                <DrawerFacts items={detailFields(openImportCase.detailsJson).map((field) => ({ label: field.label, value: field.value, mono: field.mono }))} />
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmingDecision !== null}
        onOpenChange={(next) => {
          if (!next) setConfirmingDecision(null);
        }}
        title={confirmingDecision?.confirm?.title ?? ""}
        description={confirmingDecision?.confirm?.description ?? ""}
        confirmLabel={confirmingDecision?.confirm?.confirmLabel ?? "Confirm"}
        presentation="default"
        busy={decisionBusy !== null}
        onConfirm={() => confirmingDecision && void runDecision(confirmingDecision)}
      />

      <ConfirmDialog
        open={confirmingLeftoverDelete}
        onOpenChange={setConfirmingLeftoverDelete}
        title="Delete leftover files?"
        description={
          openEventLeftovers.length > 0
            ? `This permanently deletes ${openEventLeftovers.length} file${openEventLeftovers.length === 1 ? "" : "s"} that never matched an episode: ${openEventLeftovers.map((file) => fileNameFromPath(file.sourcePath)).join(", ")}.`
            : "This permanently deletes the leftover files that never matched an episode."
        }
        confirmLabel="Delete"
        confirmVariant="destructive"
        presentation="default"
        busy={deletingLeftovers}
        onConfirm={() => void handleDeleteLeftovers()}
      />
    </div>
  );
}

/* -------------------------------------------------------------- helpers */

/** A filter day (a quick word or YYYY-MM-DD) as the moment it starts, or where the "until" day ends. Null when not set. */
function filterDay(token: string, edge: "since" | "until"): number | null {
  const moment = edge === "since" ? resolveSince(token) : resolveUntil(token);
  return moment ? moment.getTime() : null;
}

