import { useCallback, useEffect, useRef, useState } from "react";
import { useLiveInterval } from "../hooks/use-live-interval";
import { queryClient, type RouteSource } from "./route-cache";
import { sharedSources } from "./shared-sources";
import { isJobDeadLettered, isJobRetrying, type JobStatus } from "./job-status-constants";
import { needsAction } from "./needs-action";
import type { DashboardSummary } from "./api";
import { attentionSources, attentionSummaryLine, computeAttention, type AttentionLists } from "../routes/dashboard/attention";
import { SUMMARY_KEY, summaryQuery, setupNeedsFromCache, setupSources } from "../routes/dashboard/sources";
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
  /**
   * Everything that needs a person, in one number. It is the Dashboard's own
   * count (`computeAttention`, over the same stored sources), so the sidebar
   * and the Dashboard's Needs you never disagree (#1082).
   */
  total: number;
  /** The same rows in a short sentence for the sidebar card. */
  summary: string;
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
  total: 0,
  summary: "",
  loading: true
};

/**
 * Everything that genuinely needs a person, in one number. The sidebar pill
 * used to read only failed jobs, so it said "All good" beside a dead-lettered
 * import and an unhealthy search source (#250), and then beside twenty
 * downloads on Transfers asking for Try again or Dismiss (#556).
 */
export function attentionTotal(snapshot: Pick<AttentionSnapshot, "total">): number {
  return snapshot.total;
}

/**
 * Every source the count reads, all through the shared cache (#1081), so the
 * sidebar, Transfers, Activity and Find & Download read one answer instead of
 * each asking. A read is a cache read whenever the answer is recent enough.
 */
const ATTENTION_FRESH_MS = 30_000;

/** The Dashboard summary, read through the cache the Dashboard itself uses. */
const summarySource: RouteSource<DashboardSummary> = { key: [...SUMMARY_KEY], label: "Dashboard summary", fetch: summaryQuery.queryFn };

function readCached<T>(source: RouteSource<T>): T | undefined {
  return queryClient.getQueryData<T>(source.key);
}

function readFresh<T>(source: RouteSource<T>, force: boolean): Promise<T> {
  return queryClient.fetchQuery({ queryKey: source.key, queryFn: source.fetch, staleTime: force ? 0 : ATTENTION_FRESH_MS, retry: false });
}

/** The count, worked out from whatever the cache holds right now. Nothing is fetched. */
function snapshotFromCache(): AttentionSnapshot | null {
  const failed = readCached(sharedSources.failedJobs);
  const indexers = readCached(sharedSources.indexers);
  // Nothing has been read yet: the pill keeps saying it is still loading.
  if (failed === undefined && indexers === undefined) return null;

  const stopped = (failed ?? []).filter((job) => !isJobRetrying(job));
  const movieRecovery = readCached(sharedSources.movieRecovery) ?? null;
  const seriesRecovery = readCached(sharedSources.seriesRecovery) ?? null;
  const telemetry = readCached(sharedSources.telemetry) ?? null;
  const handoffs = readCached(sharedSources.handoffs) ?? [];
  const dispatches = readCached(sharedSources.dispatches) ?? [];
  const orphans = readCached(sharedSources.orphans) ?? [];
  const health = readCached(sharedSources.healthSummary) ?? null;

  // The one count: the Dashboard's, over the same summary and the same stored lists.
  const lists = Object.fromEntries(Object.entries(attentionSources).map(([name, source]) => [name, readCached(source as RouteSource<unknown>)])) as AttentionLists;
  const attention = computeAttention(readCached(summarySource), lists, setupNeedsFromCache());

  return {
    failedJobs: stopped.length,
    deadLetteredJobs: stopped.filter((job) => isJobDeadLettered(job.status as JobStatus)).length,
    indexerAlerts: (indexers ?? []).filter((i) => i.isEnabled && i.healthStatus !== "healthy").length,
    downloadsNeedingAction: needsAction({ movieRecovery, seriesRecovery, telemetry, handoffs, dispatches, orphans }).total,
    failedImportJobs: stopped.filter((job) => job.jobType === "filesystem.import.execute").length,
    movieWanted: readCached(sharedSources.movieWanted)?.totalWanted ?? 0,
    tvWanted: readCached(sharedSources.seriesWanted)?.totalWanted ?? 0,
    healthProblems: health?.unsnoozedProblemCount ?? 0,
    total: attention.counts.overview,
    summary: attentionSummaryLine(attention.items),
    loading: false
  };
}

export function useAttention(pollMs = 45000) {
  const [snapshot, setSnapshot] = useState<AttentionSnapshot>(() => snapshotFromCache() ?? empty);
  // Guards against a second call landing while the batch below is still in
  // flight (#937). Nothing here waited its turn: the 45s ticker, every
  // ActivityEventAdded in FAILED_JOB_CATEGORIES, and every HealthChanged all
  // called the same `load`, so a slow batch -- a loaded dev server, a
  // throttled CI runner -- let the next trigger start an identical batch on
  // top of the one still running, and the one after that on top of both.
  // Each batch never really disappears, it just joins a pile that keeps
  // growing faster than any single batch can drain, which is what turns an
  // ordinary slow response into a page that never goes network-idle. One
  // in-flight batch at a time; a trigger that arrives mid-batch is dropped,
  // not queued -- the next poll or event finds the loop again.
  const loadingRef = useRef(false);

  /**
   * `force` asks the server again whatever the cache holds (an event said
   * something changed). The leftover-folders list is the one source the poll
   * never asks for: it walks folders on the server, and Transfers, which shows
   * it, refreshes it while it is open. It is read here only if nothing has ever
   * read it.
   */
  const load = useCallback(async (force = false) => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    try {
      const orphansKnown = readCached(sharedSources.orphans) !== undefined;
      await Promise.all([
        readFresh(sharedSources.failedJobs, force).catch(() => undefined),
        readFresh(sharedSources.indexers, force).catch(() => undefined),
        readFresh(sharedSources.movieWanted, force).catch(() => undefined),
        readFresh(sharedSources.seriesWanted, force).catch(() => undefined),
        // The sources Transfers reads for Needs attention, so the two agree.
        readFresh(sharedSources.movieRecovery, force).catch(() => undefined),
        readFresh(sharedSources.seriesRecovery, force).catch(() => undefined),
        readFresh(sharedSources.dispatches, force).catch(() => undefined),
        readFresh(sharedSources.handoffs, force).catch(() => undefined),
        readFresh(sharedSources.telemetry, force).catch(() => undefined),
        orphansKnown ? Promise.resolve(undefined) : readFresh(sharedSources.orphans, false).catch(() => undefined),
        // #909: the same summary System -> Health and the Dashboard tile read.
        readFresh(sharedSources.healthSummary, force).catch(() => undefined),
        // The Dashboard's own summary (memoised, stored numbers only) and the lists its count also reads.
        readFresh(summarySource, force).catch(() => undefined),
        readFresh(attentionSources.processors, force).catch(() => undefined),
        ...Object.values(setupSources).map((source) => readFresh(source as RouteSource<unknown>, force).catch(() => undefined))
      ]);

      // A job Deluno will try again on its own is not one the pill should
      // count: every processed import failed once, then imported (#562).
      setSnapshot(snapshotFromCache() ?? { ...empty, loading: false });
    } catch {
      setSnapshot((s) => ({ ...s, loading: false }));
    } finally {
      loadingRef.current = false;
    }
  }, []);

  // The first read waits a beat so it never competes with the screen being
  // opened: a screen change is never held up by the sidebar's counts (#1081).
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 400);
    return () => window.clearTimeout(timer);
  }, [load]);

  useLiveInterval(() => void load(), pollMs);

  // Whatever a screen reads and finds changed (Transfers finishing a leftover
  // folder scan, Activity dismissing a failure) moves the count now, without a
  // request of its own.
  useEffect(() => {
    return queryClient.getQueryCache().subscribe((event) => {
      if (event.type !== "updated" || event.action.type !== "success") return;
      if (event.query.queryKey[0] !== "src" && event.query.queryKey[0] !== "dashboard") return;
      const next = snapshotFromCache();
      if (next) setSnapshot(next);
    });
  }, []);

  // A job failing, being retried or being dismissed changes the count now, not
  // at the next poll: dismissing the last failure beside a pill still saying
  // "1 failed job" reads as the dismissal not having worked.
  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    if (FAILED_JOB_CATEGORIES.has(event.category)) void load(true);
  });
  // #909: a health check starting or clearing lights (or clears) the bell's
  // dot immediately, rather than waiting for the next poll.
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, () => void load(true));

  const refresh = useCallback(() => load(true), [load]);
  return { ...snapshot, refresh };
}
