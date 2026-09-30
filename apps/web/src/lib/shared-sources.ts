import {
  ApiRequestError,
  fetchJson,
  fetchPageItems,
  type DownloadClientItem,
  type DownloadDispatchItem,
  type DownloadTelemetryOverview,
  type HealthCheckSummary,
  type IndexerItem,
  type JobQueueItem,
  type LibraryItem,
  type MovieImportRecoverySummary,
  type MovieWantedSummary,
  type OrphanedDownloadItem,
  type PlatformSettingsSnapshot,
  type ProcessorHandoffItem,
  type SeriesImportRecoverySummary,
  type SeriesWantedSummary
} from "./api";
import { fetchFailedJobs } from "./failed-jobs";
import type { RouteSource } from "./route-cache";

/**
 * Sources that more than one screen reads (#1081).
 *
 * The sidebar counts, Transfers, Activity and Find & Download all ask for the
 * same lists. Each one used to fetch its own copy; here they are named once,
 * under one cache key each, so whichever asks first pays and the rest read the
 * answer (and the request that is already on its way).
 */
function shared<T>(key: string, label: string, fetch: () => Promise<T>, extra: Partial<RouteSource<T>> = {}): RouteSource<T> {
  return { key: ["src", key], label, fetch, ...extra };
}

export const emptyRecovery = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] };

export const sharedSources = {
  telemetry: shared<DownloadTelemetryOverview>("telemetry", "Download activity", () => fetchJson("/api/download-clients/telemetry")),
  dispatches: shared<DownloadDispatchItem[]>("dispatches-60", "Sent downloads", () => fetchPageItems<DownloadDispatchItem>("/api/download-dispatches?pageSize=60")),
  movieRecovery: shared<MovieImportRecoverySummary>("movie-recovery", "Movie imports that need a look", () => fetchJson("/api/movies/import-recovery?limit=500")),
  seriesRecovery: shared<SeriesImportRecoverySummary>("series-recovery", "TV imports that need a look", () => fetchJson("/api/series/import-recovery?limit=500")),
  libraries: shared<LibraryItem[]>("libraries", "Libraries", () => fetchJson("/api/libraries")),
  settings: shared<PlatformSettingsSnapshot>("settings", "Settings", () => fetchJson("/api/settings")),
  failedJobs: shared<JobQueueItem[]>("failed-jobs", "Failed jobs", () => fetchFailedJobs()),
  indexers: shared<IndexerItem[]>("indexers", "Indexers", () => fetchJson("/api/indexers")),
  downloadClients: shared<DownloadClientItem[]>("download-clients", "Download clients", () => fetchJson("/api/download-clients")),
  // A newly deployed web build stays usable during a rolling upgrade while an
  // older local API is still running: no endpoint, no hand-offs. Other failures stay visible.
  handoffs: shared<ProcessorHandoffItem[]>("handoffs-30", "Processor hand-offs", () =>
    fetchJson<ProcessorHandoffItem[]>("/api/integrations/processors/handoffs?take=30").catch((error) => {
      if (error instanceof ApiRequestError && error.status === 404) return [];
      throw error;
    })),
  // Orphaned download folders (#588). An older API without the endpoint shows none.
  orphans: shared<OrphanedDownloadItem[]>("orphans", "Leftover download folders", () => fetchJson<OrphanedDownloadItem[]>("/api/download-clients/orphans").catch(() => [])),
  healthSummary: shared<HealthCheckSummary>("health-summary", "Health summary", () => fetchJson("/api/system/health/summary")),
  movieWanted: shared<MovieWantedSummary>("movie-wanted", "Wanted movies", () => fetchJson("/api/movies/wanted")),
  seriesWanted: shared<SeriesWantedSummary>("series-wanted", "Wanted shows", () => fetchJson("/api/series/wanted"))
} as const;

