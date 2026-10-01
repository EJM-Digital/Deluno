/**
 * What on the Dashboard needs a person, counted once (#1087).
 *
 * The two tab badges, the Needs you list and the sidebar's "Needs a look" card
 * all come from this one function (the sidebar reads it in
 * `lib/use-attention.ts`), so they can never disagree.
 *
 * One rule: only a REAL problem is counted, meaning something Deluno cannot
 * resolve without a person: a client, indexer or processor that is not
 * answering, an open import case, an import or processor hand-off that failed,
 * a download that stalled, a job that gave up, an open health problem that is
 * not snoozed, a background worker that keeps failing. NOT counted: leftover
 * folders, health suggestions, a failed download Deluno is already searching
 * again for, and anything snoozed. Leftovers and suggestions stay reachable as
 * one quiet line (`alsoWorthALook`) under Needs you.
 *
 * Each problem is counted once, on the tab where its button is:
 *  - Live: downloads, imports and processor hand-offs;
 *  - System: connections, folders, storage, system and jobs.
 * `count` is how many rows an item stands for (a group such as "3 jobs gave up"
 * is one row with one Retry button). A tab's badge is the sum of its items'
 * counts, and the sidebar's total is the two badges added together.
 */
import {
  fetchJson,
  type DashboardClientRow,
  type DashboardHealthProblem,
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

/** Two tabs (#1087): Live is what is moving, System is the plumbing. */
export type DashboardTab = "live" | "system";

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
  /** Where the fix button goes. */
  to: string;
  action: string;
  /** How many rows with a button this item is on its tab. */
  count: number;
}

export interface DashboardAttention {
  /** Every problem, worst first. The Needs you list. */
  items: AttentionItem[];
  /** Real problems per tab: the two badges. Each problem is on one tab only. */
  counts: Record<DashboardTab, number>;
  /** The two badges added together: the sidebar's number. */
  total: number;
  /** Uncounted: leftover folders and health suggestions. */
  alsoWorthALook: { leftovers: number; suggestions: number };
  /** The health problems the System tab lists: not snoozed, and not one a row above already stands for. */
  healthListed: DashboardHealthProblem[];
  /** The rows the tabs draw with buttons, already free of duplicates. */
  downloads: {
    stalled: DownloadQueueItem[];
    /** Every download that needs a look: the stalled ones and those the client reported a problem with. */
    queueAttention: DownloadQueueItem[];
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
  const add = (item: Omit<AttentionItem, "count"> & { count?: number }) => {
    const count = item.count ?? 1;
    if (count > 0) items.push({ ...item, count });
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

  const clients = (downloads?.clients ?? []).filter(clientNeedsButton);
  const indexers = (lists.indexers ?? []).filter(indexerNeedsButton);
  const indexerCount = lists.indexers !== undefined ? indexers.length : summary?.indexers?.failing ?? 0;
  const processors = (lists.processors ?? []).filter(processorUnhealthy);
  const processorCount = lists.processors !== undefined ? processors.length : summary?.processors?.failing ?? 0;

  // The health board raises its own problem for a client, indexer or processor that is not answering
  // (id `indexer:<id>`, `download-client:<id>`, `processor:<id>`). One that already has its own row
  // here is the same problem, so it is that row and not a second one.
  const covered = new Set<string>([
    ...indexers.map((item) => `indexer:${item.id}`),
    ...clients.map((item) => `download-client:${item.id}`),
    ...processors.map((item) => `processor:${item.id}`)
  ]);
  /** With exactly one connection down, the row says which one and why, the way the health problem did. */
  const only = <T extends { name: string }>(list: T[], why: (item: T) => string | null | undefined, fallback: string) =>
    list.length === 1
      ? { title: `${list[0].name} isn't answering`, text: why(list[0]) || fallback }
      : null;
  const indexerOnly = lists.indexers !== undefined ? only(indexers, (item) => item.lastHealthMessage, "Deluno can't search this site until it answers.") : null;
  const processorOnly = lists.processors !== undefined ? only(processors, (item) => item.lastHealthMessage, "Finished downloads can't be cleaned until it answers.") : null;

  // A job that gave up is one Retry row. A failed import is on Downloads instead,
  // and a job Deluno will try again by itself is not a problem.
  const jobsGaveUp = lists.failedJobs !== undefined
    ? lists.failedJobs.filter((job) => job.jobType !== "filesystem.import.execute" && !isJobRetrying(job)).length
    : Math.max(0, (system?.jobs.failed ?? 0) - (imports?.failedImportJobs ?? 0));
  const failingLanes = system?.lanes.filter((lane) => lane.state === "failing").length ?? 0;
  const collectionsFailing = summary?.library?.collections.syncFailing ?? 0;
  const requestServersFailing = summary?.requests?.seerrFailing ?? 0;

  // ---------------------------------------------------------------- setup health
  for (const item of setup) {
    add({ id: `setup:${item.id}`, tab: "system", tone: "warn", title: item.title, text: item.text, to: item.to, action: item.action });
  }
  if (system?.internetOutage) {
    add({
      id: "outage",
      tab: "system",
      tone: "bad",
      title: "Deluno can't reach the internet",
      text: "Searching and cleanup are paused until it is back. Nothing was marked as a bad release.",
      to: "/system",
      action: "Open System"
    });
  }
  const gate = summary?.automation?.queuePause;
  if (gate?.enabled && gate.unreadable) {
    add({
      id: "queue-gate",
      tab: "system",
      tone: "warn",
      title: "Deluno can't read the download queue",
      text: "Pausing searches when the queue is long is not working until it can.",
      to: "/settings/automation",
      action: "Open Automation"
    });
  }
  add({
    id: "queue-warnings",
    tab: "live",
    tone: "warn",
    title: `${plural(otherQueue.length, "download has", "downloads have")} a warning`,
    text: "The download client reported a problem with them.",
    to: "/queue",
    action: "Open Transfers",
    count: otherQueue.length
  });

  // -------------------------------------------------------------------- library
  add({
    id: "collections",
    tab: "system",
    tone: "warn",
    title: `${plural(collectionsFailing, "collection")} could not update`,
    text: "The list a collection follows could not be read.",
    to: "/collections",
    action: "Collections",
    count: collectionsFailing > 0 ? 1 : 0
  });
  add({
    id: "requests",
    tab: "system",
    tone: "warn",
    title: `${plural(requestServersFailing, "request server")} not answering`,
    text: "New requests can't arrive until it answers.",
    to: "/indexers/media-servers",
    action: "Find & Download",
    count: requestServersFailing > 0 ? 1 : 0
  });

  // ------------------------------------------------------------------ downloads
  add({
    id: "stalled",
    tab: "live",
    tone: "warn",
    title: `${plural(stalledCount, "download is", "downloads are")} stalled`,
    text: "They have stopped moving in the download client.",
    to: "/queue",
    action: "Open Transfers",
    count: stalledCount
  });
  add({
    id: "clients",
    tab: "system",
    tone: "warn",
    title: `${plural(clients.length, "download client")} needs a look`,
    text: "It is not answering, is paused, or has warnings to clear.",
    to: "/?tab=system",
    action: "Open System",
    count: clients.length
  });
  add({
    id: "indexers",
    tab: "system",
    tone: "warn",
    title: indexerOnly?.title ?? `${plural(indexerCount, "indexer")} not answering`,
    text: indexerOnly?.text ?? "Deluno can't search a site until it answers.",
    to: "/?tab=system",
    action: "Open System",
    count: indexerCount
  });
  add({
    id: "recovery",
    tab: "live",
    tone: "warn",
    title: `${plural(recoveryCount, "import needs", "imports need")} a look`,
    text: "Deluno could not finish these on its own. Retry them or give up on them.",
    to: "/",
    action: "Open Dashboard",
    count: recoveryCount
  });
  add({
    id: "failed-imports",
    tab: "live",
    tone: "bad",
    title: `${plural(importJobsCount, "import")} didn't finish`,
    text: "They stopped and won't be tried again on their own.",
    to: "/",
    action: "Open Dashboard",
    count: importJobsCount
  });
  add({
    id: "handoffs",
    tab: "live",
    tone: "warn",
    title: `${plural(handoffCount, "hand-off")} to the processor failed`,
    text: "The finished download was not handed over for cleaning.",
    to: "/",
    action: "Open Dashboard",
    count: handoffCount
  });
  add({
    id: "processors",
    tab: "system",
    tone: "warn",
    title: processorOnly?.title ?? `${plural(processorCount, "processor")} not answering`,
    text: processorOnly?.text ?? "Finished downloads can't be cleaned until it answers.",
    to: "/?tab=system",
    action: "Open System",
    count: processorCount
  });

  // ---------------------------------------------------------------------- system
  const health = summary?.health;
  // A snoozed problem is not counted, and a problem a row above already stands for is not a second one.
  const unsnoozedProblems = (health?.topProblems ?? []).filter((problem) => !problem.snoozed);
  const healthListed = unsnoozedProblems.filter((problem) => !covered.has(problem.id));
  for (const problem of healthListed) {
    add({
      id: `health:${problem.id}`,
      tab: "system",
      tone: "warn",
      title: problem.title,
      text: problem.message,
      to: problem.link || "/system",
      action: `Fix it in ${screenName(problem.link || "/system")}`
    });
  }
  // Every unsnoozed problem the summary carried is accounted for, listed or merged into a row above;
  // only the ones it did not carry are "more".
  const shown = unsnoozedProblems.length;
  const unsnoozed = lists.healthSummary?.unsnoozedProblemCount;
  const healthTotal = typeof unsnoozed === "number" ? unsnoozed : health?.problems ?? 0;
  if (health && healthTotal > shown) {
    add({
      id: "health:more",
      tab: "system",
      tone: "warn",
      title: `${plural(healthTotal - shown, "more problem")} to look at`,
      text: "The health checks found more than fit here.",
      to: "/system",
      action: "Open System",
      count: healthTotal - shown
    });
  }
  add({
    id: "failed-jobs",
    tab: "system",
    tone: "bad",
    title: `${plural(jobsGaveUp, "background job")} gave up`,
    text: "Nothing else waits on them, but the work was not done.",
    to: "/?tab=system",
    action: "Open System",
    count: jobsGaveUp > 0 ? 1 : 0
  });
  add({
    id: "lanes",
    tab: "system",
    tone: "bad",
    title: `${plural(failingLanes, "background worker")} keeps failing`,
    text: "Deluno keeps trying, but this part is not getting its work done.",
    to: "/?tab=system",
    action: "Open System",
    count: failingLanes
  });

  const counts: Record<DashboardTab, number> = { live: 0, system: 0 };
  for (const item of items) counts[item.tab] += item.count;

  // What is worth a look but is not a problem: never counted, one quiet line under Needs you.
  const suggestions = lists.healthSummary?.suggestionCount ?? health?.suggestions ?? 0;
  const alsoWorthALook = { leftovers: na.orphanedDownloads.length, suggestions };

  return {
    items: [...items].sort((a, b) => Number(b.tone === "bad") - Number(a.tone === "bad")),
    counts,
    total: counts.live + counts.system,
    alsoWorthALook,
    healthListed,
    downloads: { stalled, queueAttention: na.queueAttention, clients, indexers, processors, recoveryCases: na.recoveryCases, failedImportJobs: na.failedImportJobs, handoffs: na.failedHandoffs },
    jobsGaveUp,
    failingLanes
  };
}
