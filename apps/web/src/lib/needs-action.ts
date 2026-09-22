import type {
  DownloadDispatchItem,
  DownloadQueueItem,
  DownloadTelemetryOverview,
  JobQueueItem,
  MovieImportRecoverySummary,
  ProcessorHandoffItem,
  SeriesImportRecoverySummary
} from "./api";
import { downloadQueueStatuses } from "./download-telemetry";

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

/** A hand-off a person has dismissed is history: it keeps its reason and needs nobody. */
export function handoffNeedsPerson(handoff: ProcessorHandoffItem) {
  if (handoff.status.toLowerCase() === "dismissed") return false;
  return isProcessorFailure(handoff.status) || Boolean(handoff.failureMessage);
}

export function isDispatchFailure(dispatch: DownloadDispatchItem) {
  return [dispatch.status, dispatch.grabStatus, dispatch.importStatus, dispatch.grabFailureCode, dispatch.importFailureCode]
    .filter((value): value is string => Boolean(value))
    .some((value) => ["failed", "blocked", "rejected", "unresolved", "error", "not_found", "circuit_open", "paused"].includes(value.toLowerCase()))
    || dispatch.failure !== null && dispatch.failure !== undefined;
}

export function queueItemNeedsAttention(item: DownloadQueueItem) {
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
}

export function needsAction(sources: NeedsActionSources): NeedsAction {
  const recoveryCases: RecoveryCaseRow[] = [
    ...(sources.movieRecovery?.recentCases ?? []).map((item) => ({ ...item, mediaType: "movie" as const })),
    ...(sources.seriesRecovery?.recentCases ?? []).map((item) => ({ ...item, mediaType: "series" as const }))
  ];
  const queueAttention = (sources.telemetry?.clients ?? []).flatMap((client) => client.queue).filter(queueItemNeedsAttention);
  const failedImportJobs = (sources.failedJobs ?? []).filter((job) => job.jobType === "filesystem.import.execute");
  const failedHandoffs = (sources.handoffs ?? []).filter(handoffNeedsPerson);

  // A download a recovery case is about is shown once, as that case, which is
  // the row that carries Try again and Dismiss.
  const coveredByCase = new Set(recoveryCases.map((item) => dispatchIdIn(item.detailsJson)).filter((id): id is string => Boolean(id)));
  const byTitleAndRelease = new Map<string, FailedDownloadRow>();
  for (const dispatch of (sources.dispatches ?? []).filter(isDispatchFailure)) {
    if (coveredByCase.has(dispatch.id)) continue;
    const key = `${dispatch.entityType}:${dispatch.entityId}:${dispatch.releaseName.toLowerCase()}`;
    const row = byTitleAndRelease.get(key);
    if (!row) {
      byTitleAndRelease.set(key, { dispatch, attempts: 1 });
    } else {
      row.attempts += 1;
      if (dispatch.createdUtc > row.dispatch.createdUtc) row.dispatch = dispatch;
    }
  }
  const failedDownloads = [...byTitleAndRelease.values()];

  return {
    recoveryCases,
    queueAttention,
    failedImportJobs,
    failedHandoffs,
    failedDownloads,
    total: recoveryCases.length + queueAttention.length + failedImportJobs.length + failedHandoffs.length + failedDownloads.length
  };
}

/** The download a recovery case is about, from the details the poller wrote with it. */
function dispatchIdIn(detailsJson: string | null | undefined): string | null {
  if (!detailsJson) return null;
  try {
    const details = JSON.parse(detailsJson) as Record<string, unknown>;
    const id = details.DispatchId ?? details.dispatchId;
    return typeof id === "string" && id.length > 0 ? id : null;
  } catch {
    return null;
  }
}
