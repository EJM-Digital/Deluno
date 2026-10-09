import { useCallback, useEffect, useRef, useState } from "react";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { queryClient, type RouteSource } from "./route-cache";
import { sharedSources } from "./shared-sources";
import { isJobDeadLettered, isJobRetrying, type JobStatus } from "./job-status-constants";
import { needsAction } from "./needs-action";
import { isBackgroundJob } from "./background-jobs";
import { attentionSummarySource, emptyAttentionSummary, type AttentionSummary } from "./attention-summary";
import { RealtimeGroups, useSignalREvent } from "./use-signalr";

/** The activity categories that move the failed-job count. */
const FAILED_JOB_CATEGORIES = new Set(["job.failed", "job.dead-letter", "job.retry", "job.revived", "job.dismissed"]);

export interface AttentionSnapshot {
  failedJobs: number;
  /** Of the failed jobs, the ones about a title: Activity clears them (#1114). */
  failedTitleJobs: number;
  /** Of the failed jobs, the ones not about a title: System's Background jobs clears them (#1114). */
  failedBackgroundJobs: number;
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
   * Everything that needs a person, in one number: the server's own count (`GET /api/attention/summary`, #1185),
   * the same function the tray reads. This file adds nothing up.
   */
  total: number;
  /** The rows behind the number, for the bell's panel to draw (#1087, #1185). */
  summary: AttentionSummary;
  loading: boolean;
}

const empty: AttentionSnapshot = {
  failedJobs: 0,
  failedTitleJobs: 0,
  failedBackgroundJobs: 0,
  deadLetteredJobs: 0,
  indexerAlerts: 0,
  downloadsNeedingAction: 0,
  failedImportJobs: 0,
  movieWanted: 0,
  tvWanted: 0,
  healthProblems: 0,
  total: 0,
  summary: emptyAttentionSummary,
  loading: true
};

/**
 * Everything that genuinely needs a person, in one number: the bell's badge.
 * It used to read only failed jobs, so it said nothing beside a dead-lettered
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

function readCached<T>(source: RouteSource<T>): T | undefined {
  return queryClient.getQueryData<T>(source.key);
}

async function readFresh<T>(source: RouteSource<T>, force: boolean, supersede = false): Promise<T> {
  // A push-named read (supersede) means "this changed just now". A read already in flight began before the change, and
  // fetchQuery would hand back that older answer as if it were new, with no later push to correct it (#1191: the bell kept
  // counting a title that had just been dismissed). Drop the old request and ask again.
  if (force && supersede && queryClient.isFetching({ queryKey: source.key }) > 0) await queryClient.cancelQueries({ queryKey: source.key });
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

  // The one count: the server's. Nothing here adds it up.
  const read = readCached(attentionSummarySource);
  const summary = read && typeof read.count === "number" && Array.isArray(read.rows) ? read : emptyAttentionSummary;

  return {
    failedJobs: stopped.length,
    failedTitleJobs: stopped.filter((job) => !isBackgroundJob(job)).length,
    failedBackgroundJobs: stopped.filter(isBackgroundJob).length,
    deadLetteredJobs: stopped.filter((job) => isJobDeadLettered(job.status as JobStatus)).length,
    indexerAlerts: (indexers ?? []).filter((i) => i.isEnabled && i.healthStatus !== "healthy").length,
    downloadsNeedingAction: needsAction({ movieRecovery, seriesRecovery, telemetry, handoffs, dispatches, orphans }).total,
    failedImportJobs: stopped.filter((job) => job.jobType === "filesystem.import.execute").length,
    movieWanted: readCached(sharedSources.movieWanted)?.totalWanted ?? 0,
    tvWanted: readCached(sharedSources.seriesWanted)?.totalWanted ?? 0,
    healthProblems: health?.unsnoozedProblemCount ?? 0,
    total: summary.count,
    summary,
    loading: false
  };
}

/**
 * The sources a push can have moved, by the words the server names (#1179), or null when everything should be read
 * again (a word this list does not know, or one that can move several lists: a client or a download's journey).
 * Held releases are listed on Transfers, not in the bell, so they read nothing here.
 */
export function attentionSourcesForReasons(reasons: readonly string[]): RouteSource<unknown>[] | null {
  const wanted = new Map<string, RouteSource<unknown>>();
  const add = (...sources: RouteSource<unknown>[]) => sources.forEach((source) => wanted.set(source.key.join("/"), source));
  for (const reason of reasons) {
    switch (reason) {
      case "noRelease":
        add(attentionSummarySource as RouteSource<unknown>);
        break;
      case "recovery":
        add(sharedSources.movieRecovery as RouteSource<unknown>, sharedSources.seriesRecovery as RouteSource<unknown>, sharedSources.failedJobs as RouteSource<unknown>, attentionSummarySource as RouteSource<unknown>);
        break;
      case "handoff":
        add(sharedSources.handoffs as RouteSource<unknown>, sharedSources.failedJobs as RouteSource<unknown>, attentionSummarySource as RouteSource<unknown>);
        break;
      case "jobs":
        add(sharedSources.failedJobs as RouteSource<unknown>, attentionSummarySource as RouteSource<unknown>);
        break;
      case "orphans":
        add(sharedSources.orphans as RouteSource<unknown>, attentionSummarySource as RouteSource<unknown>);
        break;
      case "update":
        add(attentionSummarySource as RouteSource<unknown>);
        break;
      case "health":
        add(sharedSources.healthSummary as RouteSource<unknown>, attentionSummarySource as RouteSource<unknown>);
        break;
      case "heldReleases":
        break;
      default:
        return null;
    }
  }
  return [...wanted.values()];
}

export function useAttention() {
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
  // A push that lands while a batch is in flight is not lost (#1179): one more batch runs when this one ends.
  const againRef = useRef(false);

  /**
   * `force` asks the server again whatever the cache holds (an event said
   * something changed). The leftover-folders list is the one source the poll
   * never asks for: it walks folders on the server, and Transfers, which shows
   * it, refreshes it while it is open. It is read here only if nothing has ever
   * read it.
   */
  const load = useCallback(async (force = false) => {
    if (loadingRef.current) {
      if (force) againRef.current = true;
      return;
    }
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
        // What needs a person, worked out once on the server (#1185): the bell's rows and its count.
        readFresh(attentionSummarySource, force).catch(() => undefined)
      ]);

      // A job Deluno will try again on its own is not one the pill should
      // count: every processed import failed once, then imported (#562).
      setSnapshot(snapshotFromCache() ?? { ...empty, loading: false });
    } catch {
      setSnapshot((s) => ({ ...s, loading: false }));
    } finally {
      loadingRef.current = false;
      if (againRef.current) {
        againRef.current = false;
        void load(true);
      }
    }
  }, []);

  /** Reads only these sources again, for a change that can move nothing else (a title or an indexer changing). */
  const loadSome = useCallback(async (...sources: RouteSource<unknown>[]) => {
    await Promise.all(sources.map((source) => readFresh(source, true, true).catch(() => undefined)));
    const next = snapshotFromCache();
    if (next) setSnapshot(next);
  }, []);

  // The first read waits a beat so it never competes with the screen being
  // opened: a screen change is never held up by the sidebar's counts (#1081).
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 400);
    return () => window.clearTimeout(timer);
  }, [load]);

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
  // #1179: nothing here is read on a timer. Everything the count reads is pushed: a recovery case, a hand-off to a
  // processor, a download's journey, a client going away or coming back, a leftover folder or a title with no release
  // (all AttentionChanged, which the server sends on the dashboard subject so the bell needs no screen open).
  // The push names what moved (a burst comes as one push, its words joined by commas), so only those lists are read
  // again; a word that is not known reads everything.
  const pendingReasonsRef = useRef(new Set<string>());
  const reloadAfterAttention = useCoalescedRevalidate(() => {
    const reasons = [...pendingReasonsRef.current];
    pendingReasonsRef.current.clear();
    const sources = attentionSourcesForReasons(reasons);
    if (sources === null) void load(true);
    else if (sources.length > 0) void loadSome(...sources);
  }, 400);
  useSignalREvent("AttentionChanged", RealtimeGroups.Dashboard, (event) => {
    const reasons = (event?.reason ?? "").split(",").map((word) => word.trim()).filter(Boolean);
    for (const reason of reasons.length > 0 ? reasons : ["all"]) pendingReasonsRef.current.add(reason);
    reloadAfterAttention();
  });
  // A title or an indexer changing moves only the wanted counts, or the indexers and the Dashboard summary.
  const reloadWanted = useCoalescedRevalidate(() => void loadSome(sharedSources.movieWanted, sharedSources.seriesWanted), 2_500);
  useSignalREvent("MovieChanged", RealtimeGroups.Dashboard, () => reloadWanted());
  useSignalREvent("SeriesChanged", RealtimeGroups.Dashboard, () => reloadWanted());
  const reloadIndexers = useCoalescedRevalidate(() => void loadSome(sharedSources.indexers, attentionSummarySource as RouteSource<unknown>), 1_500);
  useSignalREvent("IndexerChanged", RealtimeGroups.Dashboard, () => reloadIndexers());
  // #1098: a real test of a connection finished. A connection that stopped (or started) answering changes what needs a person; a burst of quiet results is folded into one read.
  const reloadAfterCheck = useCoalescedRevalidate(() => void load(true), 1_500);
  useSignalREvent("ConnectionChecked", RealtimeGroups.Dashboard, () => reloadAfterCheck());

  const refresh = useCallback(() => load(true), [load]);
  return { ...snapshot, refresh };
}
