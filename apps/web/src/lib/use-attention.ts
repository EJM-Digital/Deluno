import { useCallback, useEffect, useRef, useState } from "react";
import {
  fetchJson,
  fetchPageItems,
  type DownloadDispatchItem,
  type DownloadTelemetryOverview,
  type HealthCheckSummary,
  type IndexerItem,
  type MovieImportRecoverySummary,
  type MovieWantedSummary,
  type OrphanedDownloadItem,
  type ProcessorHandoffItem,
  type SeriesImportRecoverySummary,
  type SeriesWantedSummary
} from "./api";
import { useLiveInterval } from "../hooks/use-live-interval";
import { fetchFailedJobs } from "./failed-jobs";
import { isJobDeadLettered, isJobRetrying, type JobStatus } from "./job-status-constants";
import { needsAction } from "./needs-action";
import { RealtimeGroups, useSignalREvent } from "./use-signalr";

/** The activity categories that move the failed-job count. */
const FAILED_JOB_CATEGORIES = new Set(["job.failed", "job.dead-letter", "job.retry", "job.revived", "job.dismissed"]);

export interface AttentionSnapshot {
  failedJobs: number;
  deadLetteredJobs: number;
  indexerAlerts: number;
  /**
   * What Transfers lists under Needs attention, other than failed import jobs,
   * which are already among the failed jobs (#556).
   */
  downloadsNeedingAction: number;
  /** Failed import jobs, which Transfers lists beside the downloads. */
  failedImportJobs: number;
  movieWanted: number;
  tvWanted: number;
  /** Health checks currently reporting a problem that is not snoozed (#909). */
  healthProblems: number;
  loading: boolean;
}

const empty: AttentionSnapshot = {
  failedJobs: 0,
  deadLetteredJobs: 0,
  indexerAlerts: 0,
  downloadsNeedingAction: 0,
  failedImportJobs: 0,
  movieWanted: 0,
  tvWanted: 0,
  healthProblems: 0,
  loading: true
};

/**
 * Everything that genuinely needs a person, in one number. The sidebar pill
 * used to read only failed jobs, so it said "All good" beside a dead-lettered
 * import and an unhealthy search source (#250), and then beside twenty
 * downloads on Transfers asking for Try again or Dismiss (#556).
 */
export function attentionTotal(snapshot: AttentionSnapshot): number {
  return snapshot.failedJobs + snapshot.indexerAlerts + snapshot.downloadsNeedingAction + snapshot.healthProblems;
}

export function useAttention(pollMs = 45000) {
  const [snapshot, setSnapshot] = useState<AttentionSnapshot>(empty);
  // Guards against a second call landing while the eleven-fetch batch below
  // is still in flight (#937). Nothing here waited its turn: the 45s ticker,
  // every ActivityEventAdded in FAILED_JOB_CATEGORIES, and every HealthChanged
  // all called the same `load`, so a slow batch -- a loaded dev server, a
  // throttled CI runner -- let the next trigger start an identical batch on
  // top of the one still running, and the one after that on top of both.
  // Each batch never really disappears, it just joins a pile that keeps
  // growing faster than any single batch can drain, which is what turns an
  // ordinary slow response into a page that never goes network-idle. One
  // in-flight batch at a time; a trigger that arrives mid-batch is dropped,
  // not queued -- the next poll or event finds the loop again.
  const loadingRef = useRef(false);

  const load = useCallback(async () => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    setSnapshot((s) => ({ ...s, loading: true }));
    try {
      const [failed, indexers, movieWanted, seriesWanted, movieRecovery, seriesRecovery, dispatches, handoffs, telemetry, orphans, health] = await Promise.all([
        fetchFailedJobs().catch(() => []),
        fetchJson<IndexerItem[]>("/api/indexers").catch(() => []),
        fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => null),
        fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => null),
        // The sources Transfers reads for Needs attention, so the two agree.
        fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery?limit=500").catch(() => null),
        fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery?limit=500").catch(() => null),
        fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=60").catch(() => []),
        fetchJson<ProcessorHandoffItem[]>("/api/integrations/processors/handoffs?take=30").catch(() => []),
        fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => null),
        fetchJson<OrphanedDownloadItem[]>("/api/download-clients/orphans").catch(() => []),
        // #909: the same summary System -> Health and the Dashboard tile read.
        fetchJson<HealthCheckSummary>("/api/system/health/summary").catch(() => null)
      ]);

      // A job Deluno will try again on its own is not one the pill should
      // count: every processed import failed once, then imported (#562).
      const stopped = failed.filter((job) => !isJobRetrying(job));
      const failedJobs = stopped.length;
      const deadLetteredJobs = stopped.filter((job) => isJobDeadLettered(job.status as JobStatus)).length;
      const indexerAlerts = indexers.filter((i) => i.isEnabled && i.healthStatus !== "healthy").length;
      const downloadsNeedingAction = needsAction({ movieRecovery, seriesRecovery, telemetry, handoffs, dispatches, orphans }).total;

      setSnapshot({
        failedJobs,
        deadLetteredJobs,
        indexerAlerts,
        downloadsNeedingAction,
        failedImportJobs: stopped.filter((job) => job.jobType === "filesystem.import.execute").length,
        movieWanted: movieWanted?.totalWanted ?? 0,
        tvWanted: seriesWanted?.totalWanted ?? 0,
        healthProblems: health?.unsnoozedProblemCount ?? 0,
        loading: false
      });
    } catch {
      setSnapshot((s) => ({ ...s, loading: false }));
    } finally {
      loadingRef.current = false;
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  useLiveInterval(() => void load(), pollMs);

  // A job failing, being retried or being dismissed changes the count now, not
  // at the next poll: dismissing the last failure beside a pill still saying
  // "1 failed job" reads as the dismissal not having worked.
  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    if (FAILED_JOB_CATEGORIES.has(event.category)) void load();
  });
  // #909: a health check starting or clearing lights (or clears) the bell's
  // dot immediately, rather than waiting for the next poll.
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, () => void load());

  return { ...snapshot, refresh: load };
}
