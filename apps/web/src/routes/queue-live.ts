/**
 * Transfers is live: what the server pushes is applied to the lists the screen
 * already reads, with no request after it. The patches that already exist for the
 * Live board (`dashboard/live/realtime.ts`) are shared; the two Transfers needs on
 * top of them, a download's own reading and a client's light, are here. A source
 * with no patch of its own is read again, once, after a burst of events
 * (`useCoalescedRefresh`), never on a timer.
 */
import type { QueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef } from "react";
import type { ClientStateChangedEvent, DownloadTelemetryOverview, JobQueueItem } from "../lib/api";
import { downloadQueueStatuses } from "../lib/download-telemetry";
import { isJobFailed, isJobRetrying, isJobSuccessful, type JobStatus } from "../lib/job-status-constants";
import { queryClient } from "../lib/route-cache";
import { sharedSources } from "../lib/shared-sources";
import { useLiveEvent, useLiveWatching, type DownloadProgressEvent } from "../lib/use-signalr";
import {
  applyHandoffChanged,
  applyJourneyStage,
  applyQueueRemoved,
  applyQueueStatus,
  applyRecoveryCase
} from "./dashboard/live/realtime";

type Cache = Pick<QueryClient, "getQueryData" | "setQueryData">;

/** The client's own "time left" as the server words it ("01:02:03"), in seconds; 0 when it gave none. */
export function etaSecondsOf(eta: string | null | undefined): number {
  if (!eta) return 0;
  const parts = eta.split(":").map(Number);
  if (parts.length !== 3 || parts.some((part) => !Number.isFinite(part))) return 0;
  return parts[0] * 3600 + parts[1] * 60 + parts[2];
}

const LIVE_STATUSES = new Set(["downloading", "stalled", "queued"]);

/** A download's own reading: its bar, speed and time left move, and the totals on top follow. */
export function applyDownloadProgress(client: Cache, event: DownloadProgressEvent): boolean {
  const overview = client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key);
  if (!overview) return false;
  let changed = false;
  const clients = overview.clients.map((entry) => {
    if (!entry.queue.some((item) => item.id === event.id)) return entry;
    changed = true;
    return {
      ...entry,
      queue: entry.queue.map((item) => (item.id === event.id
        ? {
            ...item,
            progress: event.progress,
            speedMbps: event.speedMbps,
            etaSeconds: etaSecondsOf(event.eta),
            status: LIVE_STATUSES.has(event.status) ? event.status : item.status
          }
        : item))
    };
  });
  if (!changed) return false;
  const all = clients.flatMap((entry) => entry.queue);
  client.setQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key, {
    ...overview,
    clients,
    summary: {
      ...overview.summary,
      activeCount: all.filter((item) => item.status === "downloading").length,
      totalSpeedMbps: all.reduce((sum, item) => sum + (item.status === "downloading" ? item.speedMbps : 0), 0)
    }
  });
  return true;
}

/** A client became unreachable or reachable, or was paused or resumed: its row in Clients says so. */
export function applyClientStateToTelemetry(client: Cache, event: ClientStateChangedEvent): boolean {
  const overview = client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key);
  const known = overview?.clients.find((entry) => entry.clientId === event.clientId);
  if (!overview || !known) return false;
  const healthStatus = event.reachable ? (known.healthStatus === "healthy" || known.healthStatus === "ok" ? known.healthStatus : "healthy") : "unreachable";
  const clientPaused = event.paused ?? known.clientPaused ?? null;
  if (healthStatus === known.healthStatus && clientPaused === (known.clientPaused ?? null)) return false;
  client.setQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key, {
    ...overview,
    clients: overview.clients.map((entry) => (entry.clientId === event.clientId ? { ...entry, healthStatus, clientPaused, lastHealthMessage: event.reachable ? null : event.message ?? entry.lastHealthMessage } : entry))
  });
  return true;
}

/**
 * What a download's row says once its import job has answered: Imported, Import failed, or Import queued while the
 * job is waiting, running or has another attempt coming (#1209). Judged from the job the click returned, so the row
 * leaves "Ready to import" at once instead of at the next read.
 */
export function importOutcomeStatus(job: Pick<JobQueueItem, "status" | "attempts" | "maxAttempts" | "nextAttemptUtc">): string {
  if (isJobSuccessful(job.status as JobStatus)) return downloadQueueStatuses.imported;
  if (isJobFailed(job.status as JobStatus) && !isJobRetrying(job)) return downloadQueueStatuses.importFailed;
  return downloadQueueStatuses.importQueued;
}

/** The jobs list Transfers reads (its own source in queue-screen). */
export const TRANSFERS_JOBS_KEY = ["src", "jobs-80"] as const;

/** The import job a click just made joins the jobs list the screen reads, so "Importing" counts it with no wait. */
export function applyImportJob(client: Cache, job: JobQueueItem): boolean {
  const jobs = client.getQueryData<JobQueueItem[]>(TRANSFERS_JOBS_KEY);
  if (!jobs) return false;
  client.setQueryData<JobQueueItem[]>(TRANSFERS_JOBS_KEY, jobs.some((entry) => entry.id === job.id)
    ? jobs.map((entry) => (entry.id === job.id ? job : entry))
    : [job, ...jobs]);
  return true;
}

/** Shows the outcome of "Queue import" on its row the moment the click returns (#1209). */
export function applyImportOutcome(client: Cache, queueItemId: string, job: JobQueueItem): void {
  applyImportJob(client, job);
  applyQueueStatus(client, { id: queueItemId, status: importOutcomeStatus(job), errorMessage: isJobFailed(job.status as JobStatus) ? job.lastError ?? null : null });
}

/**
 * Reads the given sources again, once, after the events stop arriving for a
 * moment: a burst of pushes is one read, never one per push.
 */
export function useCoalescedRefresh(keys: ReadonlyArray<readonly unknown[]>, waitMs = 1000): () => void {
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const latest = useRef(keys);
  latest.current = keys;
  useEffect(() => () => { if (timer.current !== null) clearTimeout(timer.current); }, []);
  return useCallback(() => {
    if (timer.current !== null) return;
    timer.current = setTimeout(() => {
      timer.current = null;
      for (const key of latest.current) void queryClient.invalidateQueries({ queryKey: key, exact: true });
    }, waitMs);
  }, [waitMs]);
}

/** How long a burst of download readings is held before it is shown: a few times a second follows a download. */
const PROGRESS_FLUSH_MS = 250;

/**
 * Mounts every push Transfers shows. Joins the subjects only while Transfers is
 * open, and tells the server this screen is watching so it reads the clients every second.
 * `refresh` is called for the sources that have no patch of their own.
 */
export function useTransfersLiveEvents(refreshDispatches: () => void, refreshActivity: () => void, refreshJobs: () => void, refreshTelemetry: () => void): void {
  useLiveWatching();
  const buffer = useRef(new Map<string, DownloadProgressEvent>());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current !== null) clearTimeout(timer.current); }, []);

  useLiveEvent("DownloadProgress", (event) => {
    buffer.current.set(event.id, event);
    if (timer.current !== null) return;
    timer.current = setTimeout(() => {
      timer.current = null;
      const readings = [...buffer.current.values()];
      buffer.current.clear();
      for (const reading of readings) applyDownloadProgress(queryClient, reading);
    }, PROGRESS_FLUSH_MS);
  });
  useLiveEvent("QueueItemStatusChanged", (event) => applyQueueStatus(queryClient, event));
  useLiveEvent("QueueItemRemoved", (event) => applyQueueRemoved(queryClient, event.id));
  // A new queue item has its client, size and route only in the reading itself: that one list is read again.
  useLiveEvent("QueueItemAdded", refreshTelemetry);
  useLiveEvent("JourneyStageChanged", (event) => { applyJourneyStage(queryClient, event); refreshActivity(); });
  useLiveEvent("ProcessorHandoffChanged", (event) => { applyHandoffChanged(queryClient, event); refreshActivity(); });
  useLiveEvent("RecoveryCaseChanged", (event) => applyRecoveryCase(queryClient, event));
  useLiveEvent("ClientStateChanged", (event) => { applyClientStateToTelemetry(queryClient, event); });
  useLiveEvent("JobChanged", (event) => {
    refreshJobs();
    if (event.state === "failed") void queryClient.invalidateQueries({ queryKey: sharedSources.failedJobs.key, exact: true });
  });
  // A manual import has no dispatch, so its row only moves to done when the job itself says it finished (#1171).
  useLiveEvent("ImportStateChanged", () => { refreshJobs(); refreshDispatches(); refreshTelemetry(); });
  useLiveEvent("DispatchGrabCompleted", refreshDispatches);
  useLiveEvent("DispatchDetected", refreshDispatches);
  useLiveEvent("DispatchImportStarted", refreshDispatches);
  useLiveEvent("DispatchImportCompleted", refreshDispatches);
}
