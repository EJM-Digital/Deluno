/**
 * Whether the acquisition pipeline currently holds work.
 *
 * The dashboard polls download-client telemetry on this rather than on a fixed
 * clock. Every moving part of the pipeline card — the stage counts, the flow
 * motes between stages, the progress bar on each transfer — is rendered from
 * that one response, and no realtime event carries progress: `DownloadProgress`
 * is published once per grab with progress and speed both zero, so it announces
 * that a transfer exists rather than how far along it is.
 *
 * Getting this wrong is expensive in both directions, which is why it is a
 * named function with tests rather than an inline condition: false while work
 * is in flight freezes the board on the 60s heartbeat, and true while idle
 * polls Deluno every few seconds forever for numbers that cannot change.
 */
import type { DownloadTelemetryOverview } from "./api";

/**
 * Fast enough that a progress bar climbs rather than jumps, slow enough to stay
 * a poll against Deluno's own cache of the download clients rather than a
 * hammer on the clients themselves.
 */
export const ACTIVE_PIPELINE_REFRESH_MS = 3_000;

/** Anything between grabbed and imported means the board has something to move. */
export function isPipelineMoving(telemetry: DownloadTelemetryOverview | undefined) {
  const summary = telemetry?.summary;
  if (!summary) return false;

  // completedCount is deliberately not here: finished work has left the
  // pipeline, and counting it would pin the board to the fast poll forever.
  return summary.activeCount > 0
    || summary.queuedCount > 0
    || summary.stalledCount > 0
    || summary.processingCount > 0
    || summary.importReadyCount > 0;
}
