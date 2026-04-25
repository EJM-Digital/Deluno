import { useCallback, useEffect, useState } from "react";
import {
  fetchJson,
  type IndexerItem,
  type JobQueueItem,
  type MovieWantedSummary,
  type SeriesWantedSummary
} from "./api";

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
        fetchJson<JobQueueItem[]>("/api/jobs?take=80").catch(() => []),
        fetchJson<IndexerItem[]>("/api/indexers").catch(() => []),
        fetchJson<MovieWantedSummary>("/api/movies/wanted").catch(() => null),
        fetchJson<SeriesWantedSummary>("/api/series/wanted").catch(() => null)
      ]);

      const failedJobs = jobs.filter((j) => j.status === "failed").length;
      const indexerAlerts = indexers.filter((i) => i.healthStatus !== "ready").length;

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
    const id = window.setInterval(() => void load(), pollMs);
    return () => window.clearInterval(id);
  }, [load, pollMs]);

  return { ...snapshot, refresh: load };
}
