/**
 * Activity — the permanent record of what happened and why.
 *
 * Four lists over one poll: the job queue, what went to download clients, the
 * event trail, and imports that could not be filed. Each row opens the detail
 * rather than printing it inline, because an error string or a payload is worth
 * reading in full and worth nothing squeezed into a cell.
 *
 * A failed job is listed however old it is. It used to be found only among the
 * latest 24, and recurring work buries one within hours: the sidebar went on
 * counting it while this screen said "none failing" and offered nothing to press.
 *
 * Contracts: GET /api/jobs, /api/jobs?failed=true, /api/activity,
 * /api/download-dispatches, /api/movies/import-recovery,
 * /api/series/import-recovery; POST /api/jobs/retry-failed,
 * /api/jobs/{id}/dismiss.
 */
import type { Tone } from "../lib/status-tones";
import { useMemo, useRef, useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { RefreshCw } from "lucide-react";
import {
  fetchJson, fetchPageItems,
  type ActivityEventItem,
  type DownloadDispatchItem,
  type JobQueueItem,
  type MovieImportRecoverySummary,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { getDispatchStatusLabel as formatDispatchStatus, getJobStatusLabel, getJobStatusTone, JOB_STATUS, isJobActive, isJobFailed, isJobInProgress, isJobRetrying, isJobSuccessful, type JobStatus } from "../lib/job-status-constants";
import { dismissFailedJob, fetchFailedJobs } from "../lib/failed-jobs";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ColumnPicker } from "../components/ui/column-picker";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerDanger, DrawerFacts, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import {
  LIST_TRACK,
  ListCard,
  ListCell,
  ListEmpty,
  ListNameCell,
  ListRow,
  ListTable
} from "../components/ui/list-card";
import { MenuSelect } from "../components/ui/menu-select";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { SummaryStrip } from "../components/ui/summary-strip";
import { toast } from "../components/shell/toaster";
import { DownloadDispatchDrawer } from "../components/app/download-dispatch-drawer";
import { useLiveInterval } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { formatDateTime as formatPreferenceDateTime, useDisplayPreferences } from "../lib/display-preferences";
import {
  ACTIVITY_EVENT_TYPE_LABELS,
  activityEventType,
  categoryLabel,
  foldCrossMediaJobRuns,
  foldRoutineJobEvents,
  type ActivityEventType
} from "../lib/activity-format";
import { reasonWords } from "../lib/failure-reasons";
import { isChosenOnSearch, relatedEntityLabel } from "../lib/dispatch-target";
import { useColumnVisibility, type ColumnDefinition } from "../lib/use-column-visibility";
import {
  fileNameFromPath,
  parseDeletedLeftoverPaths,
  parseSeasonPackSkippedFiles,
  SEASON_PACK_COMPLETED_CATEGORY,
  SEASON_PACK_LEFTOVERS_DELETED_CATEGORY
} from "../lib/season-pack-leftovers";

interface ActivityLoaderData {
  activity: ActivityEventItem[];
  dispatches: DownloadDispatchItem[];
  failedJobs: JobQueueItem[];
  jobs: JobQueueItem[];
  movieRecovery: MovieImportRecoverySummary;
  seriesRecovery: SeriesImportRecoverySummary;
}

type Section = "jobs" | "events" | "imports";

/** Persisted column choice for the Events table (#798). Category and When are
 * always useful; Type and Related are the ones worth hiding on a narrow list. */
const EVENT_OPTIONAL_COLUMNS: readonly ColumnDefinition[] = [
  { key: "category", label: "Category", defaultVisible: true },
  { key: "type", label: "Type", defaultVisible: true },
  { key: "related", label: "Related", defaultVisible: false },
  { key: "when", label: "When", defaultVisible: true }
];

export async function activityLoader(): Promise<ActivityLoaderData> {
  const [jobs, failedJobs, activity, dispatches, movieRecovery, seriesRecovery] = await Promise.all([
    fetchPageItems<JobQueueItem>("/api/jobs?pageSize=24"),
    fetchFailedJobs(),
    fetchPageItems<ActivityEventItem>("/api/activity?pageSize=40"),
    fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=20&includeArchived=true"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery")
  ]);

  return { activity, dispatches, failedJobs, jobs, movieRecovery, seriesRecovery };
}

export function ActivityPage() {
  const loaderData = useLoaderData() as ActivityLoaderData;
  const revalidator = useRevalidator();
  const lastDispatchRefresh = useRef(0);
  const [section, setSection] = useState<Section>("jobs");
  const [openJobId, setOpenJobId] = useState<string | null>(null);
  const [openDispatchId, setOpenDispatchId] = useState<string | null>(null);
  const [openEventId, setOpenEventId] = useState<string | null>(null);
  const [openImportCaseId, setOpenImportCaseId] = useState<string | null>(null);
  const [eventTypeFilter, setEventTypeFilter] = useState<"all" | ActivityEventType>("all");
  const [busy, setBusy] = useState(false);
  const [confirmingLeftoverDelete, setConfirmingLeftoverDelete] = useState(false);
  const [deletingLeftovers, setDeletingLeftovers] = useState(false);
  const { preferences } = useDisplayPreferences();
  const eventColumns = useColumnVisibility("activity-page.events-columns", EVENT_OPTIONAL_COLUMNS);

  // Active work needs a short refresh, and it keeps running while the tab is
  // hidden so the screen is already current when it is looked at (#641).
  useLiveInterval(() => revalidator.revalidate(), 10_000);

  const scheduleDispatchRefresh = () => {
    const now = Date.now();
    if (revalidator.state !== "idle" || now - lastDispatchRefresh.current < 5_000) return;
    lastDispatchRefresh.current = now;
    revalidator.revalidate();
  };

  useSignalREvent("DispatchDetected", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, scheduleDispatchRefresh);

  const { activity, dispatches, failedJobs: failed, movieRecovery, seriesRecovery } = loaderData;
  // A failed job has its own card, so it is not listed again among the latest.
  // A job with another attempt coming belongs with the work in flight, not
  // with the ones that stopped and need a person (#562).
  const jobs = useMemo(
    () => loaderData.jobs.filter((job) => !isJobFailed(job.status as JobStatus) || isJobRetrying(job)),
    [loaderData.jobs]
  );
  const stopped = useMemo(() => failed.filter((job) => !isJobRetrying(job)), [failed]);

  const runningJobs = jobs.filter((job) => isJobInProgress(job.status as JobStatus)).length;
  const queuedJobs = jobs.filter((job) => isJobActive(job.status as JobStatus) || isJobRetrying(job)).length - runningJobs;
  const completedJobs = jobs.filter((job) => isJobSuccessful(job.status as JobStatus)).length;
  const failedJobs = stopped.length;
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;

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
  const foldedActivity = useMemo(() => foldCrossMediaJobRuns(foldRoutineJobEvents(activity)), [activity]);
  const openEventEntry = foldedActivity.find((folded) => folded.event.id === openEventId);
  const openEvent = openEventEntry?.event ?? activity.find((event) => event.id === openEventId) ?? null;
  const openEventFoldedFrom = openEventEntry?.foldedFrom ?? [];

  // #889: a season-pack import names, in the same event, every file that did
  // not go in - an extra the catalogue never listed, or one that matched an
  // episode but had a problem of its own. A path already confirmed deleted
  // (recorded on its own event) drops off the list rather than staying
  // there for the life of the browser tab.
  const deletedLeftoverPaths = useMemo(() => {
    const paths = new Set<string>();
    for (const event of activity) {
      if (event.category !== SEASON_PACK_LEFTOVERS_DELETED_CATEGORY) continue;
      for (const path of parseDeletedLeftoverPaths(event.detailsJson)) paths.add(path);
    }
    return paths;
  }, [activity]);
  const openEventLeftovers = useMemo(() => {
    if (!openEvent || openEvent.category !== SEASON_PACK_COMPLETED_CATEGORY) return [];
    return parseSeasonPackSkippedFiles(openEvent.detailsJson).filter((file) => !deletedLeftoverPaths.has(file.sourcePath));
  }, [openEvent, deletedLeftoverPaths]);

  // Only the types actually present are offered (#798): a dropdown of eight
  // options where six are always empty is not a filter, it is a maze.
  const presentEventTypes = useMemo(
    () => [...new Set(foldedActivity.map((folded) => activityEventType(folded.event)))],
    [foldedActivity]
  );
  const filteredActivity = useMemo(
    () =>
      eventTypeFilter === "all"
        ? foldedActivity
        : foldedActivity.filter((folded) => activityEventType(folded.event) === eventTypeFilter),
    [foldedActivity, eventTypeFilter]
  );

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

  async function handleDismissJob(job: JobQueueItem) {
    setBusy(true);
    try {
      await dismissFailedJob(job.id);
      toast.success(`${formatJobType(job.jobType)} dismissed`);
      setOpenJobId(null);
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That job could not be dismissed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        left={
          <SegmentedControl<Section>
            aria-label="Section"
            className="w-auto"
            value={section}
            onValueChange={setSection}
            options={[
              { value: "jobs", label: "Jobs & downloads" },
              { value: "events", label: "Events" },
              { value: "imports", label: "Import issues" }
            ]}
          />
        }
        actions={
          <>
            <Button type="button" variant="outline" onClick={() => revalidator.revalidate()} disabled={revalidator.state !== "idle"}>
              <RefreshCw className="h-4 w-4" />
              Refresh
            </Button>
            <Button type="button" onClick={() => void handleRetryFailedJobs()} disabled={busy || failedJobs === 0}>
              Retry {failedJobs || "failed"} {failedJobs === 1 ? "job" : "jobs"}
            </Button>
          </>
        }
      />

      <SummaryStrip
        cells={[
          { label: "Running", value: runningJobs, help: runningJobs ? "in flight now" : "nothing working" },
          { label: "Queued", value: queuedJobs, help: "waiting for a worker" },
          { label: "Finished", value: completedJobs, help: "in the last 24 jobs" },
          { label: "Failed", value: failedJobs, tone: failedJobs > 0 ? "danger" : undefined, help: failedJobs ? "need attention" : "none failing" },
          { label: "Import issues", value: openRecovery, tone: openRecovery > 0 ? "warning" : undefined, help: openRecovery ? "need a decision" : "all imports clear" }
        ]}
      />

      {section === "jobs" ? (
        <>
          {stopped.length > 0 ? (
            <ListCard title="Failed jobs" count={`${stopped.length} stopped`}>
              <ListTable
                columns={[
                  { label: "Job" },
                  { label: "Source", mobile: true },
                  { label: "Attempts", align: "end" },
                  { label: "When" },
                  { label: "Status", width: LIST_TRACK.status }
                ]}
              >
                {stopped.map((job) => (
                  <ListRow key={job.id} onClick={() => setOpenJobId(job.id)} selected={openJobId === job.id}>
                    <ListNameCell name={formatJobType(job.jobType)} sub={job.lastError ?? job.workerId ?? "—"} />
                    <ListCell primary={job.source} mobile />
                    <ListCell primary={job.attempts} align="end" numeric />
                    <ListCell primary={formatPreferenceDateTime(job.completedUtc ?? job.startedUtc ?? job.scheduledUtc, preferences)} />
                    <ListCell>
                      <Chip tone={jobTone(job.status)}>{getJobStatusLabel(job.status as JobStatus)}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            </ListCard>
          ) : null}

          <ListCard title="Job queue" count={jobs.length ? `Latest ${jobs.length}` : undefined}>
            {jobs.length === 0 ? (
              <ListEmpty title="Nothing queued" description="Scheduled searches, metadata refreshes and imports appear here as they run." />
            ) : (
              <ListTable
                columns={[
                  { label: "Job" },
                  { label: "Source", mobile: true },
                  { label: "Attempts", align: "end" },
                  { label: "When" },
                  { label: "Status", width: LIST_TRACK.status }
                ]}
              >
                {jobs.map((job) => (
                  <ListRow key={job.id} onClick={() => setOpenJobId(job.id)} selected={openJobId === job.id}>
                    <ListNameCell name={formatJobType(job.jobType)} sub={job.lastError ?? job.workerId ?? "—"} />
                    <ListCell primary={job.source} mobile />
                    <ListCell primary={job.attempts} align="end" numeric />
                    <ListCell primary={formatPreferenceDateTime(job.completedUtc ?? job.startedUtc ?? job.scheduledUtc, preferences)} />
                    <ListCell>
                      <Chip tone={isJobRetrying(job) ? "info" : jobTone(job.status)}>{isJobRetrying(job) ? "Retrying" : getJobStatusLabel(job.status as JobStatus)}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>

          <ListCard title="Sent to downloads" count={dispatches.length ? `Latest ${dispatches.length}` : undefined}>
            {dispatches.length === 0 ? (
              <ListEmpty title="Nothing sent yet" description="Releases Deluno hands to a download client are listed here, with what the client said back." />
            ) : (
              <ListTable
                columns={[
                  { label: "Release" },
                  { label: "Client", mobile: true },
                  { label: "Media" },
                  { label: "When" },
                  { label: "Status", width: LIST_TRACK.status }
                ]}
              >
                {dispatches.map((dispatch) => (
                  <ListRow
                    key={dispatch.id}
                    onClick={() => setOpenDispatchId(dispatch.id)}
                    selected={openDispatchId === dispatch.id}
                  >
                    <ListNameCell name={dispatch.releaseName} sub={dispatch.indexerName} />
                    <ListCell primary={dispatch.downloadClientName} mobile />
                    <ListCell primary={dispatch.mediaType === "tv" ? "TV" : "Movies"} />
                    <ListCell primary={formatPreferenceDateTime(dispatch.createdUtc, preferences)} />
                    <ListCell>
                      <Chip tone={dispatchTone(dispatch.status)}>{formatDispatchStatus(dispatch.status)}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </ListTable>
            )}
          </ListCard>
        </>
      ) : null}

      {section === "events" ? (
        <ListCard
          title="Events"
          count={
            filteredActivity.length !== foldedActivity.length
              ? `${filteredActivity.length} of ${foldedActivity.length}`
              : foldedActivity.length
                ? `Latest ${foldedActivity.length}`
                : undefined
          }
          actions={
            <>
              {presentEventTypes.length > 1 ? (
                <MenuSelect
                  label="Filter by event type"
                  value={eventTypeFilter}
                  onChange={(value) => setEventTypeFilter(value as "all" | ActivityEventType)}
                  options={[
                    { value: "all", label: "All events" },
                    ...presentEventTypes.map((type) => ({ value: type, label: ACTIVITY_EVENT_TYPE_LABELS[type] }))
                  ]}
                  triggerClassName="h-[var(--control-height-sm)] rounded-[8px] border border-hairline bg-surface-1 px-2.5 text-[length:var(--type-caption)] font-semibold text-muted-foreground hover:text-foreground"
                />
              ) : null}
              <ColumnPicker columns={EVENT_OPTIONAL_COLUMNS} isVisible={eventColumns.isVisible} onToggle={eventColumns.toggle} />
            </>
          }
        >
          {activity.length === 0 ? (
            <ListEmpty title="Nothing has happened yet" description="Every consequential thing Deluno does is recorded here, with the reason behind it." />
          ) : filteredActivity.length === 0 ? (
            <ListEmpty title="Nothing of this type" description="Choose a different event type, or All events, to see the rest of the list." />
          ) : (
            <ListTable
              columns={[
                { label: "Event" },
                ...(eventColumns.isVisible("category") ? [{ label: "Category", mobile: true as const }] : []),
                ...(eventColumns.isVisible("type") ? [{ label: "Type" }] : []),
                ...(eventColumns.isVisible("related") ? [{ label: "Related" }] : []),
                ...(eventColumns.isVisible("when") ? [{ label: "When" }] : [])
              ]}
            >
              {filteredActivity.map(({ event, foldedFrom }) => (
                <ListRow key={event.id} onClick={() => setOpenEventId(event.id)} selected={openEventId === event.id}>
                  <ListNameCell
                    name={event.message}
                    sub={
                      foldedFrom.length
                        ? [event.detail, `+${foldedFrom.length} more step${foldedFrom.length === 1 ? "" : "s"} for this run — open to see them`]
                            .filter(Boolean)
                            .join(" · ")
                        : event.detail
                    }
                  />
                  {eventColumns.isVisible("category") ? <ListCell primary={categoryLabel(event.category)} mobile /> : null}
                  {eventColumns.isVisible("type") ? <ListCell primary={ACTIVITY_EVENT_TYPE_LABELS[activityEventType(event)]} /> : null}
                  {eventColumns.isVisible("related") ? (
                    <ListCell {...relatedEntityLabel(event.relatedEntityType, event.relatedEntityId)} />
                  ) : null}
                  {eventColumns.isVisible("when") ? <ListCell primary={formatPreferenceDateTime(event.createdUtc, preferences)} /> : null}
                </ListRow>
              ))}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      {section === "imports" ? (
        <ListCard
          title="Import issues"
          count={openRecovery ? `${openRecovery} open` : undefined}
        >
          {importCases.length === 0 ? (
            <ListEmpty title="Nothing stuck" description="Downloads that finish but cannot be filed — wrong quality, no match, corrupt — land here for a decision." />
          ) : (
            <ListTable
              columns={[
                { label: "Issue" },
                { label: "Media", mobile: true },
                { label: "What to do", width: "minmax(0,1.4fr)" },
                { label: "Found" }
              ]}
            >
              {importCases.map((item) => {
                const id = `${item.mediaLabel}-${item.id}`;
                return (
                  <ListRow key={id} onClick={() => setOpenImportCaseId(id)} selected={openImportCaseId === id}>
                    <ListNameCell name={`${reasonWords(item.failureKind)} · ${item.title}`} sub={item.summary} />
                    <ListCell primary={item.mediaLabel} mobile />
                    <ListCell primary={item.recommendedAction} />
                    <ListCell primary={formatPreferenceDateTime(item.detectedUtc, preferences)} />
                  </ListRow>
                );
              })}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      <Drawer
        open={openJob !== null}
        onOpenChange={(next) => {
          if (!next) setOpenJobId(null);
        }}
        title={openJob ? formatJobType(openJob.jobType) : "Job"}
        description={openJob ? `${openJob.source} · ${getJobStatusLabel(openJob.status as JobStatus)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenJobId(null)} />}
      >
        {openJob ? (
          <>
            <DrawerSection title="Run" aside={getJobStatusLabel(openJob.status as JobStatus)}>
              <DrawerFacts
                items={[
                  { label: "Attempts", value: String(openJob.attempts) },
                  { label: "Scheduled", value: formatPreferenceDateTime(openJob.scheduledUtc, preferences) },
                  { label: "Started", value: openJob.startedUtc ? formatPreferenceDateTime(openJob.startedUtc, preferences) : "Not started" },
                  { label: "Finished", value: openJob.completedUtc ? formatPreferenceDateTime(openJob.completedUtc, preferences) : "Not finished" },
                  { label: "Worker", value: openJob.workerId ?? "—", mono: true }
                ]}
              />
            </DrawerSection>

            {openJob.lastError ? (
              <DrawerSection title="Why it failed">
                <p className="whitespace-pre-wrap text-[length:var(--type-body-sm)] leading-relaxed text-destructive">{openJob.lastError}</p>
              </DrawerSection>
            ) : null}

            {openJob.relatedEntityId ? (
              <DrawerSection title="What it was for">
                <DrawerFacts
                  items={[
                    { label: "Type", value: relatedEntityLabel(openJob.relatedEntityType, openJob.relatedEntityId).primary },
                    ...(isChosenOnSearch(openJob.relatedEntityType) ? [] : [{ label: "Id", value: openJob.relatedEntityId, mono: true }])
                  ]}
                />
              </DrawerSection>
            ) : null}

            {openJob.payloadJson ? (
              <DrawerSection title="Payload">
                <pre className="overflow-x-auto rounded-[10px] border border-hairline bg-surface-1 p-3 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                  {prettyJson(openJob.payloadJson)}
                </pre>
              </DrawerSection>
            ) : null}

            {isJobFailed(openJob.status as JobStatus) ? (
              <DrawerSection title="If it does not need doing">
                <DrawerDanger
                  title="Dismiss this job"
                  description="Deluno stops counting it as failed. What it was and why it failed stays in Events."
                  action={
                    <Button type="button" variant="outline" size="sm" onClick={() => void handleDismissJob(openJob)} disabled={busy}>
                      Dismiss
                    </Button>
                  }
                />
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

      <DownloadDispatchDrawer
        dispatch={openDispatch}
        onClose={() => setOpenDispatchId(null)}
        onBlocked={() => revalidator.revalidate()}
      />

      <Drawer
        open={openEvent !== null}
        onOpenChange={(next) => {
          if (!next) {
            setOpenEventId(null);
            setConfirmingLeftoverDelete(false);
          }
        }}
        title={openEvent?.message ?? "Event"}
        description={openEvent ? `${openEvent.category} · ${formatPreferenceDateTime(openEvent.createdUtc, preferences)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenEventId(null)} />}
      >
        {openEvent ? (
          <>
            <DrawerSection title="What happened">
              <p className="text-[length:var(--type-body-sm)] leading-relaxed text-foreground">{openEvent.message}</p>
              {openEvent.detail ? (
                <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{openEvent.detail}</p>
              ) : null}
            </DrawerSection>

            {openEvent.relatedEntityId ? (
              <DrawerSection title="What it was about">
                <DrawerFacts
                  items={[
                    { label: "Type", value: relatedEntityLabel(openEvent.relatedEntityType, openEvent.relatedEntityId).primary },
                    ...(isChosenOnSearch(openEvent.relatedEntityType) ? [] : [{ label: "Id", value: openEvent.relatedEntityId, mono: true }])
                  ]}
                />
              </DrawerSection>
            ) : null}

            {openEventLeftovers.length > 0 ? (
              <DrawerSection title="What didn't match" aside={`${openEventLeftovers.length} file${openEventLeftovers.length === 1 ? "" : "s"}`}>
                <ul className="flex flex-col gap-2">
                  {openEventLeftovers.map((file) => (
                    <li key={file.sourcePath} className="text-[length:var(--type-body-sm)] leading-relaxed text-foreground">
                      <span className="font-medium">{fileNameFromPath(file.sourcePath)}</span>
                      <span className="block text-[length:var(--type-caption)] text-muted-foreground">{file.reason}</span>
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

            {openEvent.detailsJson ? (
              <DrawerSection title="Detail">
                <pre className="overflow-x-auto rounded-[10px] border border-hairline bg-surface-1 p-3 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                  {prettyJson(openEvent.detailsJson)}
                </pre>
              </DrawerSection>
            ) : null}

            {openEventFoldedFrom.length ? (
              <DrawerSection title="The rest of this run">
                <ul className="flex flex-col gap-2">
                  {openEventFoldedFrom.map((step) => (
                    <li key={step.id} className="text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">
                      <span className="text-foreground">{step.message}</span>
                      <span className="ml-1.5 text-[length:var(--type-caption)]">
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
        title={openImportCase ? `${reasonWords(openImportCase.failureKind)} · ${openImportCase.title}` : "Import issue"}
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
              <p className="whitespace-pre-wrap text-[length:var(--type-body-sm)] leading-relaxed text-foreground">{openImportCase.summary}</p>
            </DrawerSection>

            <DrawerSection title="What Deluno recommends">
              <p className="text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">{openImportCase.recommendedAction}</p>
            </DrawerSection>

            {openImportCase.detailsJson ? (
              <DrawerSection title="Evidence">
                <pre className="overflow-x-auto rounded-[10px] border border-hairline bg-surface-1 p-3 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                  {prettyJson(openImportCase.detailsJson)}
                </pre>
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>

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

function formatJobType(value: string) {
  const [area, ...rest] = value.split(".");
  const action = rest.join(" ").replace(/[-_]/g, " ");
  const label = `${area} ${action}`.trim();
  return label.charAt(0).toUpperCase() + label.slice(1);
}

/**
 * Queued was grey here, which is the colour for nothing happening — a queued job
 * is motion that has not started. The table decides now, and this screen no
 * longer holds an opinion of its own.
 */
function jobTone(status: string): Tone {
  return getJobStatusTone(status as JobStatus);
}

function dispatchTone(status: string): "ok" | "warn" | "bad" | "info" {
  switch (status) {
    case "sent":
    case "imported":
      return "ok";
    case "failed":
    case "importFailed":
      return "bad";
    case "planned":
      return "warn";
    default:
      return "info";
  }
}

function prettyJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}
