/**
 * What on the Dashboard needs a person, counted once (#1082).
 *
 * The tab badges, the Overview's Needs you list and its count all come from
 * this one function, and it follows the rules the sidebar's "Needs a look"
 * already keeps (`lib/needs-action.ts`, `lib/use-attention.ts`): a job Deluno
 * will retry by itself is not a problem, a download a recovery case is about is
 * the case and not also a stalled download, and a hand-off a case is about is
 * the hand-off.
 *
 * Every item is one or more rows with a button, on the tab where that button is:
 *  - `count` is how many rows of that tab the item stands for (a group such as
 *    "3 jobs gave up" is one row with one Retry button);
 *  - a tab's badge is the sum of `count` for the items on it;
 *  - the Overview lists every item, so its number is the sum of all of them:
 *    the other tabs' badges plus what only the Overview holds (setup steps, an
 *    internet outage, downloads Transfers deals with).
 */
import {
  fetchJson,
  type DashboardClientRow,
  type DashboardSummary,
  type DownloadDispatchItem,
  type DownloadQueueItem,
  type DownloadTelemetryOverview,
  type HealthCheckSummary,
  type IndexerItem,
  type JobQueueItem,
  type MovieImportRecoverySummary,
  type OrphanedDownloadItem,
  type ProcessorConnectionItem,
  type ProcessorHandoffItem,
  type SeriesImportRecoverySummary
} from "../../lib/api";
import { isJobRetrying } from "../../lib/job-status-constants";
import { needsAction, type RecoveryCaseRow } from "../../lib/needs-action";
import { defineSources } from "../../lib/route-cache";
import { sharedSources } from "../../lib/shared-sources";
import { plural } from "./format";

export type DashboardTab = "overview" | "library" | "downloads" | "system";

/** The stored lists the count reads. Each is one the sidebar already keeps warm. */
export const attentionSources = defineSources({
  telemetry: sharedSources.telemetry,
  movieRecovery: sharedSources.movieRecovery,
  seriesRecovery: sharedSources.seriesRecovery,
  failedJobs: sharedSources.failedJobs,
  handoffs: sharedSources.handoffs,
  dispatches: sharedSources.dispatches,
  indexers: sharedSources.indexers,
  processors: {
    key: ["processor-connections"],
    label: "Processors",
    fetch: () => fetchJson<ProcessorConnectionItem[]>("/api/integrations/processors/connections")
  },
  // The two the sidebar also reads, so the counts never depend on which tab was opened first.
  orphans: sharedSources.orphans,
  healthSummary: sharedSources.healthSummary
});

export interface AttentionLists {
  telemetry?: DownloadTelemetryOverview | null;
  movieRecovery?: MovieImportRecoverySummary | null;
  seriesRecovery?: SeriesImportRecoverySummary | null;
  failedJobs?: JobQueueItem[];
  handoffs?: ProcessorHandoffItem[];
  dispatches?: DownloadDispatchItem[];
  indexers?: IndexerItem[];
  processors?: ProcessorConnectionItem[];
  orphans?: OrphanedDownloadItem[];
  healthSummary?: HealthCheckSummary | null;
}

export interface SetupNeed {
  id: string;
  title: string;
  text: string;
  to: string;
  action: string;
}

export type AttentionTone = "warn" | "bad";

export interface AttentionItem {
  id: string;
  /** The tab whose screen holds the fix button. */
  tab: DashboardTab;
  tone: AttentionTone;
  title: string;
  text: string;
  /** Where the Overview's fix button goes. */
  to: string;
  action: string;
  /** How many rows with a button this item is on its tab. */
  count: number;
  /** True when the sidebar's "Needs a look" card counts this too (same source, same rule). */
  sidebar: boolean;
}

export interface DashboardAttention {
  /** Every problem, worst first. The Overview's Needs you list. */
  items: AttentionItem[];
  counts: Record<DashboardTab, number>;
  /** How many of the Overview's problems only the Dashboard checks; the sidebar card does not count them. */
  dashboardOnly: number;
  /** The rows the tabs draw with buttons, already free of duplicates. */
  downloads: {
    stalled: DownloadQueueItem[];
    clients: DashboardClientRow[];
    indexers: IndexerItem[];
    processors: ProcessorConnectionItem[];
    recoveryCases: RecoveryCaseRow[];
    failedImportJobs: JobQueueItem[];
    handoffs: ProcessorHandoffItem[];
  };
  /** Jobs that gave up, not counting failed imports (those are on Downloads). */
  jobsGaveUp: number;
  failingLanes: number;
}

/** Which screen a web path belongs to, in the words the sidebar uses. */
export function screenName(path: string): string {
  if (path.startsWith("/system")) return "System";
  if (path.startsWith("/indexers")) return "Find & Download";
  if (path.startsWith("/queue")) return "Transfers";
  if (path.startsWith("/activity")) return "Activity";
  if (path.startsWith("/movies")) return "Movies";
  if (path.startsWith("/tv")) return "TV Shows";
  if (path.startsWith("/search-cycles") || path.startsWith("/settings/automation")) return "Automation & Recovery";
  if (path.startsWith("/settings")) return "Settings";
  return "Deluno";
}

/** A client row has a button when it is not answering, is paused, or has warnings to clear. */
export function clientNeedsButton(client: DashboardClientRow): boolean {
  return !client.reachable || client.paused === true || client.warnings > 0;
}

/** An enabled indexer that is not known to be healthy gets Test again (the sidebar's rule). */
export function indexerNeedsButton(indexer: Pick<IndexerItem, "isEnabled" | "healthStatus">): boolean {
  return indexer.isEnabled && indexer.healthStatus !== "healthy";
}

export function processorUnhealthy(processor: Pick<ProcessorConnectionItem, "isEnabled" | "healthStatus">): boolean {
  return processor.isEnabled && (processor.healthStatus === "unreachable" || processor.healthStatus === "degraded");
}

const isStalled = (item: DownloadQueueItem) => item.status === "stalled";

export function computeAttention(summary: DashboardSummary | undefined, lists: AttentionLists = {}, setup: SetupNeed[] = []): DashboardAttention {
  const items: AttentionItem[] = [];
  const add = (item: Omit<AttentionItem, "count" | "sidebar"> & { count?: number; sidebar?: boolean }) => {
    const count = item.count ?? 1;
    if (count > 0) items.push({ ...item, count, sidebar: item.sidebar ?? false });
  };

  const na = needsAction({
    movieRecovery: lists.movieRecovery,
    seriesRecovery: lists.seriesRecovery,
    telemetry: lists.telemetry,
    failedJobs: lists.failedJobs,
    handoffs: lists.handoffs,
    dispatches: lists.dispatches,
    orphans: lists.orphans
  });
  const downloads = summary?.downloads;
  const imports = summary?.imports;
  const system = summary?.system;

  // Where a list has not arrived yet, the summary's own count stands in for it.
  const recoveryKnown = lists.movieRecovery !== undefined || lists.seriesRecovery !== undefined;
  const recoveryCount = recoveryKnown ? na.recoveryCases.length : imports?.recoveryOpen ?? 0;
  const importJobsCount = lists.failedJobs !== undefined ? na.failedImportJobs.length : imports?.failedImportJobs ?? 0;
  const handoffCount = lists.handoffs !== undefined ? na.failedHandoffs.length : imports?.failedProcessorHandoffs ?? 0;
  const stalled = na.queueAttention.filter(isStalled);
  const stalledCount = lists.telemetry !== undefined ? stalled.length : downloads?.stalled ?? 0;
  const otherQueue = na.queueAttention.filter((item) => !isStalled(item));
  const failedDownloadCount = lists.dispatches !== undefined ? na.failedDownloads.length : downloads?.failedDownloads ?? 0;

  const clients = (downloads?.clients ?? []).filter(clientNeedsButton);
  const indexers = (lists.indexers ?? []).filter(indexerNeedsButton);
  const indexerCount = lists.indexers !== undefined ? indexers.length : summary?.indexers?.failing ?? 0;
  const processors = (lists.processors ?? []).filter(processorUnhealthy);
  const processorCount = lists.processors !== undefined ? processors.length : summary?.processors?.failing ?? 0;

  // A job that gave up is one Retry row. A failed import is on Downloads instead,
  // and a job Deluno will try again by itself is not a problem.
  const jobsGaveUp = lists.failedJobs !== undefined
    ? lists.failedJobs.filter((job) => job.jobType !== "filesystem.import.execute" && !isJobRetrying(job)).length
    : Math.max(0, (system?.jobs.failed ?? 0) - (imports?.failedImportJobs ?? 0));
  const failingLanes = system?.lanes.filter((lane) => lane.state === "failing").length ?? 0;
  const collectionsFailing = summary?.library?.collections.syncFailing ?? 0;
  const requestServersFailing = summary?.requests?.seerrFailing ?? 0;

  // ------------------------------------------------------------- overview only
  for (const item of setup) {
    add({ id: `setup:${item.id}`, tab: "overview", tone: "warn", title: item.title, text: item.text, to: item.to, action: item.action });
  }
  if (system?.internetOutage) {
    add({
      id: "outage",
      tab: "overview",
      tone: "bad",
      title: "Deluno can't reach the internet",
      text: "Searching and cleanup are paused until it is back. Nothing was marked as a bad release.",
      to: "/system",
      action: "See System"
    });
  }
  const gate = summary?.automation?.queuePause;
  if (gate?.enabled && gate.unreadable) {
    add({
      id: "queue-gate",
      tab: "overview",
      tone: "warn",
      title: "Deluno can't read the download queue",
      text: "Pausing searches when the queue is long is not working until it can.",
      to: "/settings/automation",
      action: "Fix it in Automation & Recovery"
    });
  }
  add({
    id: "failed-downloads",
    sidebar: true,
    tab: "overview",
    tone: "bad",
    title: `${plural(failedDownloadCount, "download")} failed`,
    text: "Deluno will look for another release when the title is still wanted.",
    to: "/queue",
    action: "Look in Transfers",
    count: failedDownloadCount
  });
  add({
    id: "queue-warnings",
    sidebar: true,
    tab: "overview",
    tone: "warn",
    title: `${plural(otherQueue.length, "download has", "downloads have")} a warning`,
    text: "The download client reported a problem with them.",
    to: "/queue",
    action: "Look in Transfers",
    count: otherQueue.length
  });
  add({
    id: "leftovers",
    sidebar: true,
    tab: "overview",
    tone: "warn",
    title: `${plural(na.orphanedDownloads.length, "leftover folder")} in the download folders`,
    text: "Nothing claims these folders.",
    to: "/queue",
    action: "Look in Transfers",
    count: na.orphanedDownloads.length
  });

  // -------------------------------------------------------------------- library
  add({
    id: "collections",
    tab: "library",
    tone: "warn",
    title: `${plural(collectionsFailing, "collection")} could not update`,
    text: "The list a collection follows could not be read.",
    to: "/collections",
    action: "Look in Collections",
    count: collectionsFailing > 0 ? 1 : 0
  });
  add({
    id: "requests",
    tab: "library",
    tone: "warn",
    title: `${plural(requestServersFailing, "request server")} not answering`,
    text: "New requests can't arrive until it answers.",
    to: "/?tab=library",
    action: "Look in Library",
    count: requestServersFailing > 0 ? 1 : 0
  });

  // ------------------------------------------------------------------ downloads
  add({
    id: "stalled",
    sidebar: true,
    tab: "downloads",
    tone: "warn",
    title: `${plural(stalledCount, "download is", "downloads are")} stalled`,
    text: "They have stopped moving in the download client.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: stalledCount
  });
  add({
    id: "clients",
    tab: "downloads",
    tone: "warn",
    title: `${plural(clients.length, "download client")} needs a look`,
    text: "It is not answering, is paused, or has warnings to clear.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: clients.length
  });
  add({
    id: "indexers",
    sidebar: true,
    tab: "downloads",
    tone: "warn",
    title: `${plural(indexerCount, "indexer")} not answering`,
    text: "Deluno can't search a site until it answers.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: indexerCount
  });
  add({
    id: "recovery",
    sidebar: true,
    tab: "downloads",
    tone: "warn",
    title: `${plural(recoveryCount, "import needs", "imports need")} a look`,
    text: "Deluno could not finish these on its own. Retry them or give up on them.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: recoveryCount
  });
  add({
    id: "failed-imports",
    sidebar: true,
    tab: "downloads",
    tone: "bad",
    title: `${plural(importJobsCount, "import")} didn't finish`,
    text: "They stopped and won't be tried again on their own.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: importJobsCount
  });
  add({
    id: "handoffs",
    sidebar: true,
    tab: "downloads",
    tone: "warn",
    title: `${plural(handoffCount, "hand-off")} to the processor failed`,
    text: "The finished download was not handed over for cleaning.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: handoffCount
  });
  add({
    id: "processors",
    tab: "downloads",
    tone: "warn",
    title: `${plural(processorCount, "processor")} not answering`,
    text: "Finished downloads can't be cleaned until it answers.",
    to: "/?tab=downloads",
    action: "Fix it in Downloads",
    count: processorCount
  });

  // ---------------------------------------------------------------------- system
  const health = summary?.health;
  // A problem that is snoozed is not one the sidebar counts either.
  const shownProblems = (health?.topProblems ?? []).filter((problem) => !problem.snoozed);
  for (const problem of shownProblems) {
    add({
      id: `health:${problem.id}`,
      sidebar: true,
      tab: "system",
      tone: "warn",
      title: problem.title,
      text: problem.message,
      to: problem.link || "/system",
      action: `Fix it in ${screenName(problem.link || "/system")}`
    });
  }
  const shown = shownProblems.length;
  const unsnoozed = lists.healthSummary?.unsnoozedProblemCount;
  const healthTotal = typeof unsnoozed === "number" ? unsnoozed : health?.problems ?? 0;
  if (health && healthTotal > shown) {
    add({
      id: "health:more",
      sidebar: true,
      tab: "system",
      tone: "warn",
      title: `${plural(healthTotal - shown, "more problem")} to look at`,
      text: "The health checks found more than fit here.",
      to: "/system",
      action: "See them in System",
      count: healthTotal - shown
    });
  }
  add({
    id: "failed-jobs",
    sidebar: true,
    tab: "system",
    tone: "bad",
    title: `${plural(jobsGaveUp, "background job")} gave up`,
    text: "Nothing else waits on them, but the work was not done.",
    to: "/?tab=system",
    action: "Fix it in System",
    count: jobsGaveUp > 0 ? 1 : 0
  });
  add({
    id: "lanes",
    tab: "system",
    tone: "bad",
    title: `${plural(failingLanes, "background worker")} keeps failing`,
    text: "Deluno keeps trying, but this part is not getting its work done.",
    to: "/?tab=system",
    action: "Fix it in System",
    count: failingLanes
  });

  const counts: Record<DashboardTab, number> = { overview: 0, library: 0, downloads: 0, system: 0 };
  for (const item of items) {
    counts[item.tab] += item.count;
    counts.overview += item.count;
  }
  // The Overview's own items were counted twice above.
  counts.overview -= items.filter((item) => item.tab === "overview").reduce((sum, item) => sum + item.count, 0);
  // The Overview's number is the sidebar card's number plus what only the Dashboard checks.
  const dashboardOnly = items.filter((item) => !item.sidebar).reduce((sum, item) => sum + item.count, 0);

  return {
    items: [...items].sort((a, b) => Number(b.tone === "bad") - Number(a.tone === "bad")),
    counts,
    dashboardOnly,
    downloads: { stalled, clients, indexers, processors, recoveryCases: na.recoveryCases, failedImportJobs: na.failedImportJobs, handoffs: na.failedHandoffs },
    jobsGaveUp,
    failingLanes
  };
}
