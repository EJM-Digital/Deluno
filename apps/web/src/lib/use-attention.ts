import { useCallback, useEffect, useState } from "react";
import {
  fetchAllPages, fetchJson,
  type IndexerItem,
  type JobQueueItem,
  type MovieWantedSummary,
  type SeriesWantedSummary
} from "./api";
import { useVisibleInterval } from "../hooks/use-visible-interval";
import { isJobFailed, type JobStatus } from "./job-status-constants";

export interface AttentionSnapshot {
  failedJobs: number;
  indexerAlerts: number;
  movieWanted: number;
  tvWanted: number;
  loading: boolean;
}

const empty: AttentionSnapshot = {
  failedJobs: 0,
  indexerAlerts: 0,
  movieWanted: 0,
  tvWanted: 0,
  loading: true
};

export function useAttention(pollMs = 45000) {
  const [snapshot, setSnapshot] = useState<AttentionSnapshot>(empty);

  const load = useCallback(async () => {
    setSnapshot((s) => ({ ...s, loading: true }));
    try {
      const [jobs, indexers, movieWanted, seriesWanted] = await Promise.all([
        fetchAllPages<JobQueueItem>("/api/jobs?pageSize=80").catch(() => []),
        fetchJson<IndexerItem[]>("/api/indexers").catch(() => []),
        fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => null),
        fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => null)
      ]);

      const failedJobs = jobs.filter((job) => isJobFailed(job.status as JobStatus)).length;
      const indexerAlerts = indexers.filter((i) => i.isEnabled && i.healthStatus !== "healthy").length;

      setSnapshot({
        failedJobs,
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

  return { ...snapshot, refresh: load };
}
