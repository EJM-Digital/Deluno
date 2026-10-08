import type {
  DownloadDispatchItem,
  DownloadQueueItem,
  DownloadTelemetryOverview,
  JobQueueItem,
  MovieImportRecoverySummary,
  OrphanedDownloadItem,
  ProcessorHandoffItem,
  SeriesImportRecoverySummary
} from "./api";
import { isChosenOnSearch } from "./dispatch-target";
import { downloadQueueStatuses } from "./download-telemetry";
import { isJobRetrying } from "./job-status-constants";

/**
 * What needs a person about downloads, defined once (#556, #557).
 *
 * Transfers and the sidebar each worked this out, and differently. On the rig
 * the sidebar said "Nothing needs you" beside twenty rows on Transfers asking
 * for Try again or Dismiss, because it read failed jobs and search sources
 * only. And Transfers listed records rather than problems: a download that
 * timed out appeared as its recovery case and again as the failed download,
 * and a release refused on every search cycle added a row each time.
 */

/**
 * Mirrors `ProcessorHandoffStatuses.IsWithProcessor`. "Active" used to mean
 * "not completed", which counted every failed, rejected and lost hand-off as
 * work in progress for ever, as well as listing it under Needs attention.
 */
export function isWithProcessor(status: string) {
  return ["waiting", "submitted", "accepted", "started"].includes(status.toLowerCase());
}

export function isProcessorFailure(status: string) {
  return ["failed", "rejected", "timed-out", "timeout", "stalled", "lost", "cancelled"].includes(status.toLowerCase());
}

/**
 * A hand-off a person has dismissed is history: it keeps its reason and needs
 * nobody.
 *
 * `ProcessorHandoffStatuses.NeedsPerson` is this rule on the server, and
 * dismissal is defined as it — so anything counted here can be cleared (#570).
 */
export function handoffNeedsPerson(handoff: ProcessorHandoffItem) {
  if (handoff.status.toLowerCase() === "dismissed") return false;
  return isProcessorFailure(handoff.status) || Boolean(handoff.failureMessage);
}

/** Deluno's own stall rules blocked, removed and replaced this download (#1167): Activity's story, not a failed import. */
export function isHandledByDeluno(dispatch: Pick<DownloadDispatchItem, "importFailureCode">) {
  return dispatch.importFailureCode?.toLowerCase() === "health-remediation-applied";
}

export function isDispatchFailure(dispatch: DownloadDispatchItem) {
  if (isHandledByDeluno(dispatch)) return false;
  return [dispatch.status, dispatch.grabStatus, dispatch.importStatus, dispatch.grabFailureCode, dispatch.importFailureCode]
    .filter((value): value is string => Boolean(value))
    .some((value) => ["failed", "blocked", "rejected", "unresolved", "error", "not_found", "circuit_open", "paused"].includes(value.toLowerCase()))
    || dispatch.failure !== null && dispatch.failure !== undefined;
}

export function queueItemNeedsAttention(item: DownloadQueueItem) {
  // A download that is in the library has nothing left to ask of anyone, whatever its client said on the way.
  if (item.status === downloadQueueStatuses.imported) return false;
  return item.status === downloadQueueStatuses.stalled || Boolean(item.errorMessage) || Boolean(item.healthFindings?.length);
}

type RecoveryCase = MovieImportRecoverySummary["recentCases"][number];

export type RecoveryCaseRow = RecoveryCase & { mediaType: "movie" | "series" };

export interface FailedDownloadRow {
  /** The latest record of this title and release. */
  dispatch: DownloadDispatchItem;
  /** How many records it stands for: a release refused on every cycle is one problem, however often it was tried. */
  attempts: number;
}

export interface NeedsAction {
  recoveryCases: RecoveryCaseRow[];
  queueAttention: DownloadQueueItem[];
  failedImportJobs: JobQueueItem[];
  failedHandoffs: ProcessorHandoffItem[];
  failedDownloads: FailedDownloadRow[];
  /** Folders in a completed-download folder that nothing claims (#588). */
  orphanedDownloads: OrphanedDownloadItem[];
  /** One per row above, so the number and the list under it cannot disagree. */
  total: number;
}

export interface NeedsActionSources {
  movieRecovery?: MovieImportRecoverySummary | null;
  seriesRecovery?: SeriesImportRecoverySummary | null;
  telemetry?: DownloadTelemetryOverview | null;
  failedJobs?: JobQueueItem[];
  handoffs?: ProcessorHandoffItem[];
  dispatches?: DownloadDispatchItem[];
  orphans?: OrphanedDownloadItem[];
}

/**
 * An alarm resolves itself when what it was about resolved (#1167): a download that has since been imported
 * has nothing left to ask of anyone, whatever it reported on the way.
 */
function importedDownloads(dispatches: DownloadDispatchItem[]) {
  const byClientItem = new Set<string>();
  const byDispatchId = new Set<string>();
  for (const dispatch of dispatches) {
    if (dispatch.importStatus?.toLowerCase() !== "imported") continue;
    byDispatchId.add(dispatch.id);
    if (dispatch.torrentHashOrItemId) byClientItem.add(`${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase());
  }
  return { byClientItem, byDispatchId };
}

export function needsAction(sources: NeedsActionSources): NeedsAction {
  const imported = importedDownloads(sources.dispatches ?? []);
  const unfilteredQueueAttention = (sources.telemetry?.clients ?? [])
    .flatMap((client) => client.queue)
    .filter(queueItemNeedsAttention)
    .filter((item) => !imported.byClientItem.has(`${item.clientId}:${item.id}`.toLowerCase()));
  // A job with another attempt coming is retrying, not waiting on a person (#562).
  const unfilteredFailedImportJobs = (sources.failedJobs ?? [])
    .filter((job) => job.jobType === "filesystem.import.execute" && !isJobRetrying(job));
  const failedHandoffs = (sources.handoffs ?? [])
    .filter(handoffNeedsPerson)
    .filter((handoff) => !imported.byClientItem.has(`${handoff.clientId}:${handoff.queueItemId}`.toLowerCase()));

  // A hand-off a case is about is shown once, as the hand-off row: it, not
  // the case, carries Import original, Retry, Stop waiting and Dismiss
  // (#914, #929). Without this, a stalled or lost hand-off appeared twice --
  // once as its case, offering only Try again (which processor cases cannot
  // do) and Dismiss, and again as the hand-off, with everything that can
  // actually be done about it.
  // One failure is one row (#1167): a failed hand-off names its download by client and item id, and the
  // dispatch, the dead-lettered import job and the recovery case behind that same download are the same
  // problem. The hand-off carries the most specific cause and the fixes, so it is the row that stays.
  const handoffClientItems = new Set(failedHandoffs.map((handoff) => `${handoff.clientId}:${handoff.queueItemId}`.toLowerCase()));
  const handoffDispatchIds = new Set(
    (sources.dispatches ?? [])
      .filter((dispatch) => dispatch.torrentHashOrItemId && handoffClientItems.has(`${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase()))
      .map((dispatch) => dispatch.id)
  );
  const handoffJobIds = new Set(failedHandoffs.map((handoff) => handoff.importJobId).filter((id): id is string => Boolean(id)));
  const handoffIdsWithTheirOwnRow = new Set((sources.handoffs ?? []).map((handoff) => handoff.id));
  const recoveryCases: RecoveryCaseRow[] = [
    ...(sources.movieRecovery?.recentCases ?? []).map((item) => ({ ...item, mediaType: "movie" as const })),
    ...(sources.seriesRecovery?.recentCases ?? []).map((item) => ({ ...item, mediaType: "series" as const }))
  ].filter((item) => {
    const dispatchId = dispatchIdIn(item.detailsJson);
    if (dispatchId !== null && imported.byDispatchId.has(dispatchId)) return false;
    if (dispatchId !== null && handoffDispatchIds.has(dispatchId)) return false;
    // A case that names the client's entry for its download is the same problem as the failed hand-off for that
    // download, whichever hand-off id it carries (#1167).
    const clientItem = clientItemIn(item.detailsJson);
    if (clientItem !== null && handoffClientItems.has(clientItem)) return false;
    const handoffId = handoffIdIn(item.detailsJson);
    return handoffId === null || !handoffIdsWithTheirOwnRow.has(handoffId);
  });

  // A download a recovery case is about is shown once, as that case, which is
  // the row that carries Try again and Dismiss.
  const coveredByCase = new Set(recoveryCases.map((item) => dispatchIdIn(item.detailsJson)).filter((id): id is string => Boolean(id)));
  // The client's copy of a download a case is about is the same problem too
  // (#603): a refused sample was listed as its case, its failed job and its
  // queue item. The case is the row that can act on it.
  const caseClientItems = new Set(recoveryCases.map((item) => clientItemIn(item.detailsJson)).filter((key): key is string => key !== null));
  const coveredClientItems = new Set(
    (sources.dispatches ?? [])
      .filter((dispatch) => coveredByCase.has(dispatch.id) && dispatch.torrentHashOrItemId)
      .map((dispatch) => `${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase())
  );
  for (const key of caseClientItems) coveredClientItems.add(key);
  // The download of a failed hand-off is that hand-off's row, which carries the fixes: it is not also listed as the client's item.
  for (const handoff of failedHandoffs) coveredClientItems.add(`${handoff.clientId}:${handoff.queueItemId}`.toLowerCase());
  const queueAttention = unfilteredQueueAttention.filter((item) => !coveredClientItems.has(`${item.clientId}:${item.id}`.toLowerCase()));
  const byTitleAndRelease = new Map<string, FailedDownloadRow>();
  for (const dispatch of (sources.dispatches ?? []).filter(isDispatchFailure)) {
    if (coveredByCase.has(dispatch.id) || handoffDispatchIds.has(dispatch.id)) continue;
    // The same download a case names by its client entry is that case's problem (#1167).
    if (dispatch.torrentHashOrItemId && caseClientItems.has(`${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase())) continue;
    // A release chosen on Search has no title: it is one problem per release name.
    const target = isChosenOnSearch(dispatch.entityType) ? "chosen-on-search" : `${dispatch.entityType}:${dispatch.entityId}`;
    const key = `${target}:${dispatch.releaseName.toLowerCase()}`;
    const row = byTitleAndRelease.get(key);
    if (!row) {
      byTitleAndRelease.set(key, { dispatch, attempts: 1 });
    } else {
      row.attempts += 1;
      if (dispatch.createdUtc > row.dispatch.createdUtc) row.dispatch = dispatch;
    }
  }
  const failedDownloads = [...byTitleAndRelease.values()];
  // The same import failing is one problem (#1167): the case or the failed download names the real cause and
  // carries the actions, so the dead-lettered job behind it is not listed a second time.
  const failedDispatchIds = new Set((sources.dispatches ?? []).filter(isDispatchFailure).map((dispatch) => dispatch.id));
  const failedImportJobs = unfilteredFailedImportJobs.filter((job) => {
    if (handoffJobIds.has(job.id)) return false;
    const dispatchId = dispatchIdIn(job.payloadJson);
    return dispatchId === null || !(coveredByCase.has(dispatchId) || failedDispatchIds.has(dispatchId) || handoffDispatchIds.has(dispatchId));
  });
  const orphanedDownloads = sources.orphans ?? [];

  return {
    recoveryCases,
    queueAttention,
    failedImportJobs,
    failedHandoffs,
    failedDownloads,
    orphanedDownloads,
    total: recoveryCases.length + queueAttention.length + failedImportJobs.length + failedHandoffs.length + failedDownloads.length + orphanedDownloads.length
  };
}

/**
 * Whether Try again can do anything for this case (#558). The server acts on a
 * failed download or a failed import, through the download the case names. A
 * case raised about a processor failure names a hand-off, not a download, so
 * the button could only say "dismiss it". It is not offered there.
 */
export function canTryAgain(item: Pick<RecoveryCase, "failureKind" | "detailsJson">): boolean {
  return (
    (item.failureKind === "downloadFailed" || item.failureKind === "importFailed" || item.failureKind === "vanished-downloading" || item.failureKind === "processedFileIsLess") &&
    dispatchIdIn(item.detailsJson) !== null
  );
}

/** A download the client no longer lists at all (#927): Search for another (Try again, relabelled) and Stop wanting it are both offered, beside Dismiss. */
export function isVanished(item: Pick<RecoveryCase, "failureKind">): boolean {
  return item.failureKind === "vanished-downloading";
}

/**
 * A processed file Checkpoint B held back because the processor lost
 * something the original had (#919). Offered three real actions: Import the
 * original, Run the processor again (canTryAgain's "Try again"), and Search
 * for another release -- never a blocklist, since Checkpoint A already
 * proved the release itself is good.
 */
export function isProcessorFault(item: Pick<RecoveryCase, "failureKind">): boolean {
  return item.failureKind === "processedFileIsLess";
}

/** The download a recovery case is about, from the details the poller wrote with it. */
export function dispatchIdIn(detailsJson: string | null | undefined): string | null {
  if (!detailsJson) return null;
  try {
    const details = JSON.parse(detailsJson) as Record<string, unknown>;
    // At the top, or inside the retry an import case keeps (#602).
    const retry = (details.RetryRequest ?? details.retryRequest) as Record<string, unknown> | undefined;
    const id = details.DispatchId ?? details.dispatchId ?? retry?.DispatchId ?? retry?.dispatchId;
    return typeof id === "string" && id.length > 0 ? id : null;
  } catch {
    return null;
  }
}

/** The processor hand-off a recovery case is about, from the details ProcessorHandoffWatcher wrote with it (#914, #929). */
function handoffIdIn(detailsJson: string | null | undefined): string | null {
  if (!detailsJson) return null;
  try {
    const details = JSON.parse(detailsJson) as Record<string, unknown>;
    const id = details.HandoffId ?? details.handoffId;
    return typeof id === "string" && id.length > 0 ? id : null;
  } catch {
    return null;
  }
}

/** The client's entry for the download a case is about (`clientId:queueItemId`, lower case), from the details the processor watcher wrote (#1167). */
function clientItemIn(detailsJson: string | null | undefined): string | null {
  if (!detailsJson) return null;
  try {
    const details = JSON.parse(detailsJson) as Record<string, unknown>;
    const clientId = details.ClientId ?? details.clientId;
    const itemId = details.QueueItemId ?? details.queueItemId;
    return typeof clientId === "string" && clientId.length > 0 && typeof itemId === "string" && itemId.length > 0
      ? `${clientId}:${itemId}`.toLowerCase()
      : null;
  } catch {
    return null;
  }
}

/**
 * A failure says which step failed and what to do, never a bare "Error: timed out" (#1167). A message that is
 * only a generic word is dressed with the step; a message that already says something is left as it is.
 */
export function plainFailure(raw: string | null | undefined, step: string): string {
  const message = (raw ?? "").trim();
  const bare = message.replace(/^error:?\s*/i, "").replace(/[.!\s]+$/, "").toLowerCase();
  if (!message || ["timed out", "timeout", "time out", "failed", "unknown", "unknown error", "error"].includes(bare)) {
    const what = !message || bare === "failed" || bare === "error" || bare.startsWith("unknown") ? "failed" : "timed out";
    return `${step} ${what}. If the title is already where it belongs, dismiss this; otherwise try again.`;
  }
  return message;
}
