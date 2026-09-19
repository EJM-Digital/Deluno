import { useCallback, useEffect, useState } from "react";
import {
  fetchJson,
  type IndexerItem,
  type MovieWantedSummary,
  type SeriesWantedSummary
} from "./api";
import { useVisibleInterval } from "../hooks/use-visible-interval";
import { fetchFailedJobs } from "./failed-jobs";
import { isJobDeadLettered, type JobStatus } from "./job-status-constants";
import { RealtimeGroups, useSignalREvent } from "./use-signalr";

/** The activity categories that move the failed-job count. */
const FAILED_JOB_CATEGORIES = new Set(["job.failed", "job.dead-letter", "job.retry", "job.revived", "job.dismissed"]);

export interface AttentionSnapshot {
  failedJobs: number;
  deadLetteredJobs: number;
  indexerAlerts: number;
  movieWanted: number;
  tvWanted: number;
  loading: boolean;
}

const empty: AttentionSnapshot = {
  failedJobs: 0,
  deadLetteredJobs: 0,
  indexerAlerts: 0,
  movieWanted: 0,
  tvWanted: 0,
  loading: true
};

/**
 * Everything that genuinely needs a person, in one number. The sidebar pill
 * used to read only failed jobs, so it said "All good" beside a dead-lettered
 * import and an unhealthy search source (#250).
 */
export function attentionTotal(snapshot: AttentionSnapshot): number {
  return snapshot.failedJobs + snapshot.indexerAlerts;
}

export function useAttention(pollMs = 45000) {
  const [snapshot, setSnapshot] = useState<AttentionSnapshot>(empty);

  const load = useCallback(async () => {
    setSnapshot((s) => ({ ...s, loading: true }));
    try {
      const [failed, indexers, movieWanted, seriesWanted] = await Promise.all([
        fetchFailedJobs().catch(() => []),
        fetchJson<IndexerItem[]>("/api/indexers").catch(() => []),
        fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => null),
        fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => null)
      ]);

      const failedJobs = failed.length;
      const deadLetteredJobs = failed.filter((job) => isJobDeadLettered(job.status as JobStatus)).length;
      const indexerAlerts = indexers.filter((i) => i.isEnabled && i.healthStatus !== "healthy").length;

      setSnapshot({
        failedJobs,
        deadLetteredJobs,
        indexerAlerts,
        movieWanted: movieWanted?.totalWanted ?? 0,
        tvWanted: seriesWanted?.totalWanted ?? 0,
        loading: false
      });
    } catch {
      setSnapshot((s) => ({ ...s, loading: false }));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  useVisibleInterval(() => void load(), pollMs);

  // A job failing, being retried or being dismissed changes the count now, not
  // at the next poll: dismissing the last failure beside a pill still saying
  // "1 failed job" reads as the dismissal not having worked.
  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    if (FAILED_JOB_CATEGORIES.has(event.category)) void load();
  });

  return { ...snapshot, refresh: load };
}
