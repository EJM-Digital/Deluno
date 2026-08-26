/**
 * Activity — the permanent record of what happened and why.
 *
 * Four lists over one poll: the job queue, what went to download clients, the
 * event trail, and imports that could not be filed. Each row opens the detail
 * rather than printing it inline, because an error string or a payload is worth
 * reading in full and worth nothing squeezed into a cell.
 *
 * Contracts: GET /api/jobs, /api/activity, /api/download-dispatches,
 * /api/movies/import-recovery, /api/series/import-recovery;
 * POST /api/jobs/retry-failed.
 */
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
import { JOB_STATUS, isJobActive, isJobFailed, isJobInProgress, isJobSuccessful, type JobStatus } from "../lib/job-status-constants";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../components/ui/drawer";
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
import { useVisibleInterval } from "../hooks/use-visible-interval";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";

interface ActivityLoaderData {
  activity: ActivityEventItem[];
  dispatches: DownloadDispatchItem[];
  jobs: JobQueueItem[];
  movieRecovery: MovieImportRecoverySummary;
  seriesRecovery: SeriesImportRecoverySummary;
}

type Section = "jobs" | "events" | "imports";

export async function activityLoader(): Promise<ActivityLoaderData> {
  const [jobs, activity, dispatches, movieRecovery, seriesRecovery] = await Promise.all([
    fetchPageItems<JobQueueItem>("/api/jobs?pageSize=24"),
    fetchPageItems<ActivityEventItem>("/api/activity?pageSize=40"),
    fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=20"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery")
  ]);

  return { activity, dispatches, jobs, movieRecovery, seriesRecovery };
}

export function ActivityPage() {
  const loaderData = useLoaderData() as ActivityLoaderData;
  const revalidator = useRevalidator();
  const lastDispatchRefresh = useRef(0);
  const [section, setSection] = useState<Section>("jobs");
  const [openJobId, setOpenJobId] = useState<string | null>(null);
  const [openEventId, setOpenEventId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Active work needs a short refresh, but hidden tabs must not contend with it.
  useVisibleInterval(() => revalidator.revalidate(), 10_000);

  const scheduleDispatchRefresh = () => {
    const now = Date.now();
    if (revalidator.state !== "idle" || now - lastDispatchRefresh.current < 5_000) return;
    lastDispatchRefresh.current = now;
    revalidator.revalidate();
  };

  useSignalREvent("DispatchDetected", RealtimeGroups.Queue, scheduleDispatchRefresh);
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, scheduleDispatchRefresh);

  const { activity, dispatches, jobs, movieRecovery, seriesRecovery } = loaderData;

  const runningJobs = jobs.filter((job) => isJobInProgress(job.status as JobStatus)).length;
  const queuedJobs = jobs.filter((job) => isJobActive(job.status as JobStatus)).length - runningJobs;
  const completedJobs = jobs.filter((job) => isJobSuccessful(job.status as JobStatus)).length;
  const failedJobs = jobs.filter((job) => isJobFailed(job.status as JobStatus)).length;
  const openRecovery = movieRecovery.openCount + seriesRecovery.openCount;

  const importCases = useMemo(
    () => [
      ...movieRecovery.recentCases.map((item) => ({ ...item, mediaLabel: "Movie" })),
      ...seriesRecovery.recentCases.map((item) => ({ ...item, mediaLabel: "TV" }))
    ],
    [movieRecovery.recentCases, seriesRecovery.recentCases]
  );

  const openJob = jobs.find((job) => job.id === openJobId) ?? null;
  const openEvent = activity.find((event) => event.id === openEventId) ?? null;

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
                    <ListCell primary={formatDateTime(job.completedUtc ?? job.startedUtc ?? job.scheduledUtc)} />
                    <ListCell>
                      <Chip tone={jobTone(job.status)}>{formatJobStatus(job.status)}</Chip>
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
                chevron={false}
                columns={[
                  { label: "Release" },
                  { label: "Client", mobile: true },
                  { label: "Media" },
                  { label: "When" },
                  { label: "Status", width: LIST_TRACK.status }
                ]}
              >
                {dispatches.map((dispatch) => (
                  <ListRow key={dispatch.id}>
                    <ListNameCell name={dispatch.releaseName} sub={dispatch.indexerName} />
                    <ListCell primary={dispatch.downloadClientName} mobile />
                    <ListCell primary={dispatch.mediaType === "tv" ? "TV" : "Movies"} />
                    <ListCell primary={formatDateTime(dispatch.createdUtc)} />
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
        <ListCard title="Events" count={activity.length ? `Latest ${activity.length}` : undefined}>
          {activity.length === 0 ? (
            <ListEmpty title="Nothing has happened yet" description="Every consequential thing Deluno does is recorded here, with the reason behind it." />
          ) : (
            <ListTable columns={[{ label: "Event" }, { label: "Category", mobile: true }, { label: "When" }]}>
              {activity.map((event) => (
                <ListRow key={event.id} onClick={() => setOpenEventId(event.id)} selected={openEventId === event.id}>
                  <ListNameCell name={event.message} sub={event.detail} />
                  <ListCell primary={event.category} mobile />
                  <ListCell primary={formatDateTime(event.createdUtc)} />
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
              chevron={false}
              columns={[
                { label: "Issue" },
                { label: "Media", mobile: true },
                { label: "What to do", width: "minmax(0,1.4fr)" },
                { label: "Found" }
              ]}
            >
              {importCases.map((item) => (
                <ListRow key={`${item.mediaLabel}-${item.id}`}>
                  <ListNameCell name={`${formatFailureKind(item.failureKind)} · ${item.title}`} sub={item.summary} />
                  <ListCell primary={item.mediaLabel} mobile />
                  <ListCell primary={item.recommendedAction} />
                  <ListCell primary={formatDateTime(item.detectedUtc)} />
                </ListRow>
              ))}
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
        description={openJob ? `${openJob.source} · ${formatJobStatus(openJob.status)}` : undefined}
        footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenJobId(null)} />}
      >
        {openJob ? (
          <>
            <DrawerSection title="Run" aside={formatJobStatus(openJob.status)}>
              <DrawerFacts
                items={[
                  { label: "Attempts", value: String(openJob.attempts) },
                  { label: "Scheduled", value: formatDateTime(openJob.scheduledUtc) },
                  { label: "Started", value: openJob.startedUtc ? formatDateTime(openJob.startedUtc) : "Not started" },
                  { label: "Finished", value: openJob.completedUtc ? formatDateTime(openJob.completedUtc) : "Not finished" },
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
                    { label: "Type", value: openJob.relatedEntityType ?? "—" },
                    { label: "Id", value: openJob.relatedEntityId, mono: true }
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
          </>
        ) : null}
      </Drawer>

      <Drawer
        open={openEvent !== null}
        onOpenChange={(next) => {
          if (!next) setOpenEventId(null);
        }}
        title={openEvent?.message ?? "Event"}
        description={openEvent ? `${openEvent.category} · ${formatDateTime(openEvent.createdUtc)}` : undefined}
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
                    { label: "Type", value: openEvent.relatedEntityType ?? "—" },
                    { label: "Id", value: openEvent.relatedEntityId, mono: true }
                  ]}
                />
              </DrawerSection>
            ) : null}

            {openEvent.detailsJson ? (
              <DrawerSection title="Detail">
                <pre className="overflow-x-auto rounded-[10px] border border-hairline bg-surface-1 p-3 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                  {prettyJson(openEvent.detailsJson)}
                </pre>
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>
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

function formatJobStatus(status: string) {
  const spaced = status.replace(/[-_]/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function jobTone(status: string): "ok" | "warn" | "bad" | "info" | "muted" {
  switch (status) {
    case JOB_STATUS.COMPLETED:
      return "ok";
    case JOB_STATUS.RUNNING:
      return "info";
    case JOB_STATUS.QUEUED:
      return "muted";
    case JOB_STATUS.FAILED:
      return "bad";
    default:
      return "warn";
  }
}

function dispatchTone(status: string): "ok" | "warn" | "bad" | "info" {
  switch (status) {
    case "sent":
      return "ok";
    case "failed":
      return "bad";
    case "planned":
      return "warn";
    default:
      return "info";
  }
}

function formatDispatchStatus(status: string) {
  switch (status) {
    case "sent":
      return "Sent";
    case "failed":
      return "Failed";
    case "planned":
      return "Needs URL";
    default:
      return status.charAt(0).toUpperCase() + status.slice(1);
  }
}

function formatFailureKind(value: string) {
  switch (value) {
    case "quality":
      return "Quality rejected";
    case "unmatched":
      return "Needs matching";
    case "corrupt":
      return "Corrupt";
    case "downloadFailed":
      return "Download failed";
    case "importFailed":
      return "Import failed";
    default:
      return "Needs review";
  }
}

function prettyJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}
