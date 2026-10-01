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
import { Link, useSearchParams } from "react-router-dom";
import { defineSources, sourceLoader, useRouteSources, type RouteSourceData, type RouteSourceState } from "../lib/route-cache";
import { emptyRecovery, sharedSources } from "../lib/shared-sources";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { RefreshCw } from "lucide-react";
import {
  fetchPageItems,
  type ActivityEventItem,
  type DownloadDispatchItem,
  type JobQueueItem,
  type MovieImportRecoverySummary,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { getDispatchStatusLabel as formatDispatchStatus, getJobStatusLabel, getJobStatusTone, JOB_STATUS, isJobActive, isJobFailed, isJobInProgress, isJobRetrying, isJobSuccessful, type JobStatus } from "../lib/job-status-constants";
import { dismissFailedJob } from "../lib/failed-jobs";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
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
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { SummaryStrip } from "../components/ui/summary-strip";
import { toast } from "../components/shell/toaster";
import { DownloadDispatchDrawer } from "../components/app/download-dispatch-drawer";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { formatDateTime as formatPreferenceDateTime, useDisplayPreferences } from "../lib/display-preferences";
import { eventLabel, foldCrossMediaJobRuns, foldRoutineJobEvents } from "../lib/activity-format";
import { ActivityHistory, useActivityFilterChoices } from "../components/app/activity-history";
import { activityLinkForFile, activityLinkForTitle } from "../lib/activity-history";
import { reasonWords } from "../lib/failure-reasons";
import { isChosenOnSearch, relatedEntityLabel } from "../lib/dispatch-target";
import {
  fileNameFromPath,
  parseDeletedLeftoverPaths,
  parseSeasonPackSkippedFiles,
  SEASON_PACK_COMPLETED_CATEGORY,
  SEASON_PACK_LEFTOVERS_DELETED_CATEGORY
} from "../lib/season-pack-leftovers";

type Section = "jobs" | "events" | "imports";

/**
 * Active work needs a short refresh, so only the lists that move (jobs,
 * failures, events, sends) are asked again every ten seconds, and only while
 * the tab is on screen (#1081). Everything else is read when the screen opens
 * and when something it shows changes. The screen opens on what it last showed.
 */
const ACTIVE_REFRESH = { refetchInterval: 10_000, pauseWhenHidden: true } as const;

export const activitySources = defineSources({
  jobs: { key: ["src", "jobs-24"], label: "Jobs", fetch: () => fetchPageItems<JobQueueItem>("/api/jobs?pageSize=24&includePayload=true"), ...ACTIVE_REFRESH },
  failedJobs: { ...sharedSources.failedJobs, fallback: [] as JobQueueItem[], ...ACTIVE_REFRESH },
  dispatches: { key: ["src", "dispatches-20-archived"], label: "Sent downloads", fetch: () => fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=20&includeArchived=true"), fallback: [] as DownloadDispatchItem[], ...ACTIVE_REFRESH },
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

function ActivityPageBody({ data: loaderData, revalidator }: { data: RouteSourceData<typeof activitySources>; revalidator: RouteSourceState<typeof activitySources>["revalidator"] }) {
  const lastDispatchRefresh = useRef(0);
  // The section is in the address too, so a link can land on the history (#1100). With nothing asked for, the
  // page opens on the history: it is what Live's "All activity" and a title's link mean.
  const [searchParams, setSearchParams] = useSearchParams();
  const filterChoices = useActivityFilterChoices();
  const requestedSection = searchParams.get("section");
  const section: Section = requestedSection === "jobs" || requestedSection === "imports" || requestedSection === "events"
    ? requestedSection
    : "events";
  const setSection = (next: Section) =>
    setSearchParams((current) => {
      const params = new URLSearchParams(current);
      params.set("section", next);
      return params;
    }, { replace: true });
  const [openJobId, setOpenJobId] = useState<string | null>(null);
  const [openDispatchId, setOpenDispatchId] = useState<string | null>(null);
  const [openEvent, setOpenEvent] = useState<ActivityEventItem | null>(null);
  const [historyEvents, setHistoryEvents] = useState<ActivityEventItem[]>([]);
  const [openImportCaseId, setOpenImportCaseId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmingLeftoverDelete, setConfirmingLeftoverDelete] = useState(false);
  const [deletingLeftovers, setDeletingLeftovers] = useState(false);
  const { preferences } = useDisplayPreferences();

  const scheduleDispatchRefresh = () => {
    const now = Date.now();
    if (revalidator.state !== "idle" || now - lastDispatchRefresh.current < 5_000) return;
    lastDispatchRefresh.current = now;
    revalidator.revalidate();
  };

  useSignalREvent("DispatchDetected", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, scheduleDispatchRefresh);

  const { dispatches, failedJobs: failed, movieRecovery, seriesRecovery } = loaderData;
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
  // minutes of each other fold into one line too (the owner, 27 Sep 2026) — the
  // drawer is where either original run's own wording still shows.
  const foldedActivity = useMemo(() => foldCrossMediaJobRuns(foldRoutineJobEvents(historyEvents)), [historyEvents]);
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
              { value: "events", label: "History" },
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
                    <ListNameCell name={job.label} sub={job.lastError ?? job.workerId ?? "—"} />
                    <ListCell primary={job.sourceLabel} mobile />
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
                    <ListNameCell name={job.label} sub={job.lastError ?? job.workerId ?? "—"} />
                    <ListCell primary={job.sourceLabel} mobile />
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
        <ActivityHistory openEventId={openEvent?.id ?? null} onOpenEvent={setOpenEvent} onLoaded={setHistoryEvents} />
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
        title={openJob ? openJob.label : "Job"}
        description={openJob ? `${openJob.sourceLabel} · ${getJobStatusLabel(openJob.status as JobStatus)}` : undefined}
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
              <p className="text-[length:var(--type-body-sm)] leading-relaxed text-foreground">{openEvent.message}</p>
              {openEvent.detail ? (
                <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{openEvent.detail}</p>
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
                    className="mt-2 inline-block text-[length:var(--type-caption)] font-semibold text-primary hover:underline"
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
