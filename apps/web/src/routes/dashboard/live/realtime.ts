/**
 * Live is fully live (#1097): each change the server pushes is applied to the
 * lists the board reads, at once, with no request after it. A hand-off, a
 * sent download, an import case, a queue item, an indexer, a client, a drive and
 * the speed are each a small patch of the cached list they live in (the board
 * then moves the card, because it is drawn from those lists). Nothing here asks
 * the server for anything, and a patch for something the cache does not hold yet
 * is dropped: the first load, or the reconnect, fills it.
 *
 * The patches are plain functions of a cache, so each can be proved by itself.
 */
import type { QueryClient } from "@tanstack/react-query";
import { comparablePath } from "../../../lib/library-paths";
import type {
  ClientStateChangedEvent,
  DashboardLiveExtras,
  DashboardSummary,
  DownloadDispatchItem,
  DownloadSpeedSampleEvent,
  DownloadTelemetryOverview,
  DownloadThroughputWindow,
  DriveSpaceChangedEvent,
  IndexerQueryRecordedEvent,
  JourneyStageChangedEvent,
  MovieImportRecoverySummary,
  ProcessorHandoffChangedEvent,
  ProcessorHandoffItem,
  RecoveryCaseChangedEvent,
  SeriesImportRecoverySummary
} from "../../../lib/api";
import { DASHBOARD_SUMMARY_KEY } from "../../../lib/realtime-cache";
import { sharedSources } from "../../../lib/shared-sources";
import { liveSources } from "../sources";
import type { IndexerScoreboardToday } from "./indexers";

type Cache = Pick<QueryClient, "getQueryData" | "setQueryData">;

function patch<T>(client: Cache, key: readonly unknown[], change: (current: T) => T | undefined): boolean {
  const current = client.getQueryData<T>(key);
  if (current === undefined || current === null) return false;
  const next = change(current);
  if (next === undefined || next === current) return false;
  client.setQueryData(key, next);
  return true;
}

/* ------------------------------------------------------- hand-offs */

/** The processor's own states, in the words the hand-off list uses. */
export function handoffStatusOf(state: string): string {
  const said = state.toLowerCase();
  if (said === "queued" || said === "scheduled" || said === "working") return "started";
  if (said === "passed-through") return "completed";
  return said;
}

/** A hand-off was created or moved on: the card is in its new lane. */
export function applyHandoffChanged(client: Cache, event: ProcessorHandoffChangedEvent): void {
  const status = handoffStatusOf(event.state);
  patch<ProcessorHandoffItem[]>(client, sharedSources.handoffs.key, (list) => {
    const known = list.find((item) => item.id === event.handoffId);
    const failed = ["failed", "rejected", "timed-out", "timeout", "stalled", "lost", "cancelled", "source-gone"].includes(status);
    if (known) {
      return list.map((item) => (item.id === event.handoffId
        ? { ...item, status, updatedUtc: event.at, failureMessage: failed ? event.message ?? item.failureMessage : null, processorName: event.processorName ?? item.processorName }
        : item));
    }
    const added: ProcessorHandoffItem = {
      id: event.handoffId, libraryId: event.libraryId ?? "", mediaType: "", clientId: "", queueItemId: event.queueItemId ?? "", releaseName: event.releaseName ?? "",
      sourcePath: "", processorName: event.processorName, status, outputPath: null, importJobId: null, failureMessage: failed ? event.message : null, createdUtc: event.at, updatedUtc: event.at
    };
    return [added, ...list];
  });
}

/* ------------------------------------------------- sent downloads */

const SENT_KEYS = () => [sharedSources.dispatches.key, liveSources.boardDispatches.key] as const;

/** A download moved on its journey: its sent-download row says so, and the board draws the card where that puts it. */
export function applyJourneyStage(client: Cache, event: JourneyStageChangedEvent): void {
  const stage = event.stage.toLowerCase();
  for (const key of SENT_KEYS()) {
    patch<DownloadDispatchItem[]>(client, key, (list) => {
      const known = list.find((item) => item.id === event.dispatchId);
      if (stage === "archived") return known ? list.filter((item) => item.id !== event.dispatchId) : undefined;
      const change = (item: DownloadDispatchItem): DownloadDispatchItem => {
        if (stage === "import_succeeded") return { ...item, importStatus: "imported", importCompletedUtc: event.at };
        if (stage === "import_failed" || stage.startsWith("webhook-failed")) return { ...item, importStatus: "failed", importCompletedUtc: event.at, importFailureMessage: event.detail ?? item.importFailureMessage };
        if (stage === "grab_failed") return { ...item, grabStatus: "failed", grabMessage: event.detail ?? item.grabMessage };
        if (stage === "grab_succeeded") return { ...item, grabStatus: "succeeded", grabAttemptedUtc: event.at };
        if (stage === "detection_succeeded") return { ...item, detectedUtc: event.at };
        if (stage === "client_completed" || stage === "webhook-completed") return { ...item, importDetectedUtc: event.at };
        return item;
      };
      if (known) {
        const next = change(known);
        return next === known ? undefined : list.map((item) => (item.id === event.dispatchId ? next : item));
      }
      // A grab or an import for a download the list does not hold yet: it is a new row, from what the event says.
      if (stage !== "grab_succeeded" && stage !== "import_succeeded") return undefined;
      const row: DownloadDispatchItem = change({
        id: event.dispatchId, libraryId: event.libraryId ?? "", mediaType: event.mediaType ?? "", entityType: event.entityType ?? "", entityId: event.entityId ?? "", releaseName: event.releaseName ?? "",
        indexerName: "", downloadClientId: "", downloadClientName: "", status: "sent", notesJson: null, createdUtc: event.at
      });
      return [row, ...list];
    });
  }
  // A title that has just landed is on the wall now; its quality and size come with the next read of the day.
  if (stage === "import_succeeded") {
    patch<DashboardLiveExtras | null>(client, liveSources.liveExtras.key, (extras) => {
      if (!extras || !Array.isArray(extras.arrivals)) return undefined;
      const entityType = event.entityType ?? "movie";
      // The library comes with the event; its name from the library counts the summary already holds.
      const libraryId = event.libraryId ?? null;
      const libraryName = libraryId ? client.getQueryData<DashboardSummary>(DASHBOARD_SUMMARY_KEY)?.library?.libraryCounts?.find((library) => library.libraryId === libraryId)?.libraryName ?? null : null;
      const row = { entityType, entityId: event.entityId, release: event.releaseName, quality: null, previousQuality: null, upgraded: false, sizeBytes: null, importedUtc: event.at, libraryId, libraryName };
      if (extras.arrivals.some((item) => item.importedUtc === event.at && item.entityId === event.entityId)) return undefined;
      return { ...extras, arrivals: [row, ...extras.arrivals], importsToday: extras.importsToday + 1 };
    });
  }
}

/* ------------------------------------------------- import cases */

/** An import case opened or closed: it is a card in Needs you, or it is gone. */
export function applyRecoveryCase(client: Cache, event: RecoveryCaseChangedEvent): void {
  const source = event.entityType === "series" ? sharedSources.seriesRecovery : sharedSources.movieRecovery;
  patch<MovieImportRecoverySummary | SeriesImportRecoverySummary>(client, source.key, (summary) => {
    const known = summary.recentCases.some((entry) => entry.id === event.caseId);
    if (event.state === "closed") {
      if (!known) return { ...summary, openCount: Math.max(0, summary.openCount - 1) };
      return { ...summary, openCount: Math.max(0, summary.openCount - 1), recentCases: summary.recentCases.filter((entry) => entry.id !== event.caseId) };
    }
    if (known) return undefined;
    const row = { id: event.caseId, title: event.title ?? "", failureKind: event.kind ?? "importFailed", summary: "", recommendedAction: "review", detailsJson: null, detectedUtc: event.at };
    return { ...summary, openCount: summary.openCount + 1, recentCases: [row, ...summary.recentCases] };
  });
}

/* ------------------------------------------------------ queue */

/** A queue item changed status (or its error), or went away: the card moves, or leaves. */
export function applyQueueStatus(client: Cache, event: { id: string; status: string; errorMessage: string | null }): void {
  patch<DownloadTelemetryOverview>(client, sharedSources.telemetry.key, (overview) => {
    let changed = false;
    const clients = overview.clients.map((entry) => {
      if (!entry.queue.some((item) => item.id === event.id)) return entry;
      changed = true;
      return { ...entry, queue: entry.queue.map((item) => (item.id === event.id ? { ...item, status: event.status, errorMessage: event.errorMessage } : item)) };
    });
    return changed ? { ...overview, clients } : undefined;
  });
}

export function applyQueueRemoved(client: Cache, id: string): void {
  patch<DownloadTelemetryOverview>(client, sharedSources.telemetry.key, (overview) => {
    let changed = false;
    const clients = overview.clients.map((entry) => {
      if (!entry.queue.some((item) => item.id === id)) return entry;
      changed = true;
      return { ...entry, queue: entry.queue.filter((item) => item.id !== id) };
    });
    return changed ? { ...overview, clients } : undefined;
  });
}

/* ---------------------------------------------- clients, drives, speed */

/** A client turned unreachable or reachable, or was paused or resumed: its light changes. */
export function applyClientState(client: Cache, event: ClientStateChangedEvent): void {
  patch<DashboardSummary>(client, DASHBOARD_SUMMARY_KEY, (summary) => {
    const downloads = summary.downloads;
    if (!downloads || !downloads.clients.some((row) => row.id === event.clientId)) return undefined;
    return { ...summary, downloads: { ...downloads, clients: downloads.clients.map((row) => (row.id === event.clientId ? { ...row, reachable: event.reachable, paused: event.paused ?? row.paused } : row)) } };
  });
}

const slashes = comparablePath;

/** A file landed on a drive: its gauge ticks down to the new free space. */
export function applyDriveSpace(client: Cache, event: DriveSpaceChangedEvent): void {
  patch<DashboardSummary>(client, DASHBOARD_SUMMARY_KEY, (summary) => {
    const volumes = summary.storage?.volumes;
    if (!volumes) return undefined;
    const where = slashes(event.path);
    // The most specific drive folder that holds the path.
    let best = -1;
    let length = -1;
    volumes.forEach((volume, index) => {
      for (const path of volume.paths) {
        const root = slashes(path);
        if ((where === root || where.startsWith(`${root}/`)) && root.length > length) { best = index; length = root.length; }
      }
    });
    if (best < 0) return undefined;
    return { ...summary, storage: { ...summary.storage!, volumes: volumes.map((volume, index) => (index === best ? { ...volume, freeBytes: event.freeBytes, totalBytes: event.totalBytes ?? volume.totalBytes } : volume)) } };
  });
}

const SIX_HOURS_MS = 6 * 3_600_000;

/** A speed reading: the trace extends, and "now" is the new speed. */
export function applySpeedSample(client: Cache, event: DownloadSpeedSampleEvent): void {
  patch<DownloadThroughputWindow>(client, liveSources.throughput.key, (window) => {
    const at = Date.parse(event.at);
    if (!Number.isFinite(at)) return undefined;
    const last = window.samples[window.samples.length - 1];
    if (last && Date.parse(last.capturedUtc) >= at) return undefined;
    const kept = window.samples.filter((sample) => at - Date.parse(sample.capturedUtc) <= SIX_HOURS_MS);
    return { ...window, samples: [...kept, { capturedUtc: event.at, speedMbps: event.downloadMbps, activeCount: event.active, uploadMbps: event.uploadMbps }] };
  });
  patch<DashboardSummary>(client, DASHBOARD_SUMMARY_KEY, (summary) => {
    const downloads = summary.downloads;
    if (!downloads) return undefined;
    return { ...summary, downloads: { ...downloads, downloadMbps: event.downloadMbps, uploadMbps: event.uploadMbps, active: event.active, readingUtc: event.at } };
  });
}

/** An indexer answered: its count for today moves on, so the scoreboard is right without asking. */
export function applyIndexerAnswer(client: Cache, event: IndexerQueryRecordedEvent): void {
  if (event.phase !== "answered") return;
  patch<IndexerScoreboardToday | null>(client, liveSources.scoreboard.key, (board) => {
    if (!board || !Array.isArray(board.indexers)) return undefined;
    return { ...board, indexers: board.indexers.map((row) => (row.id === event.indexerId ? { ...row, totalQueries: row.totalQueries + 1, failedQueries: row.failedQueries + (event.failed ? 1 : 0), lastQueryUtc: event.at } : row)) };
  });
}
