/**
 * Two Dashboard summaries for the Downloads and System tab tests (#1082): a busy
 * install with something wrong in most places, and a brand-new empty one.
 */
import type { DashboardSummary } from "../../lib/api/types/operations";

const NOW = "2026-09-30T10:00:00Z";
const counts = { total: 0, monitored: 0, unmonitored: 0, missing: 0, upgradable: 0, qualityMet: 0, upcoming: 0, downloading: 0, airing: 0, fileBytes: 0 };

export const emptySummary: DashboardSummary = {
  asOfUtc: NOW,
  buildMilliseconds: 4,
  unavailableSections: [],
  library: { asOfUtc: NOW, movies: counts, shows: counts, totalBytes: 0, libraries: [], mix: { movieQuality: [], movieCodec: [], episodeQuality: [], showCodec: [] }, collections: { followed: 0, monitored: 0, members: 0, inLibrary: 0, syncFailing: 0, lastSyncedUtc: null } },
  downloads: { asOfUtc: NOW, hasReading: false, readingUtc: null, active: 0, queued: 0, stalled: 0, processing: 0, importReady: 0, waitingForProcessor: 0, downloadMbps: 0, uploadMbps: 0, clients: [], sharingHolds: 0, sharingExtraBytes: 0, leftovers: { scanned: false, count: 0, bytes: 0, scannedUtc: null }, failedDownloads: 0 },
  pipeline: { asOfUtc: NOW, today: { grabbed: 0, detected: 0, imported: 0, failed: 0 }, totals: { grabbed: 0, detected: 0, imported: 0, failedGrabs: 0, failedImports: 0, activeDispatches: 0 }, averageGrabToDetectionSeconds: null, averageDetectionToImportSeconds: null },
  imports: { asOfUtc: NOW, recoveryOpen: 0, recoveryByKind: {}, failedImportJobs: 0, failedProcessorHandoffs: 0 },
  automation: { asOfUtc: NOW, autoStartJobs: false, queuePause: { enabled: false, threshold: 0, moviesPaused: false, tvPaused: false, moviesWaiting: 0, tvWaiting: 0, unreadable: false, unreadableReason: null }, libraries: [], lastSearchCycle: null, tasks: [] },
  indexers: { asOfUtc: NOW, total: 0, healthy: 0, failing: 0, rateLimited: 0, disabled: 0, queriesToday: 0, failedQueriesToday: 0, grabsToday: 0, topFailing: [] },
  processors: { asOfUtc: NOW, total: 0, healthy: 0, failing: 0, handoffsToday: 0, waiting: 0 },
  retention: null,
  subtitles: null,
  requests: null,
  blocklist: { asOfUtc: NOW, count: 0 },
  health: { asOfUtc: NOW, problems: 0, unknown: 0, suggestions: 0, lastRunUtc: null, topProblems: [] },
  storage: { asOfUtc: NOW, readingUtc: null, volumes: [] },
  system: { asOfUtc: NOW, version: "1.0.0-rc.23", updateAvailable: false, latestVersion: null, uptimeSeconds: 30, machine: null, jobs: { running: 0, queued: 0, failed: 0 }, lanes: [], lastBackupUtc: null, nextBackupUtc: null, backupsEnabled: false, internetOutage: false, outageSinceUtc: null },
  activity: { asOfUtc: NOW, windowHours: 24, searchesRun: 0, downloadsStarted: 0, importsCompleted: 0, cleanupRemovals: 0, retentionRemovals: 0, recent: [] }
};

export const fullSummary: DashboardSummary = {
  ...emptySummary,
  downloads: {
    asOfUtc: NOW, hasReading: true, readingUtc: NOW, active: 4, queued: 1, stalled: 2, processing: 1, importReady: 1, waitingForProcessor: 1,
    downloadMbps: 3.9, uploadMbps: 0.6,
    clients: [
      { id: "c1", name: "Deluge", protocol: "torrent", reachable: true, paused: false, version: "v2.1.1", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 3, queued: 1 },
      { id: "c2", name: "SABnzbd", protocol: "usenet", reachable: false, paused: null, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "unreachable", active: 0, queued: 0 },
      { id: "c3", name: "NZBGet", protocol: "nzbget", reachable: true, paused: true, version: "v4.0.6", warnings: 2, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0 }
    ],
    sharingHolds: 5, sharingExtraBytes: 3e10, leftovers: { scanned: true, count: 2, bytes: 4e9, scannedUtc: NOW }, failedDownloads: 1
  },
  pipeline: { ...emptySummary.pipeline!, today: { grabbed: 6, detected: 5, imported: 4, failed: 1 } },
  imports: { asOfUtc: NOW, recoveryOpen: 1, recoveryByKind: { importFailed: 1 }, failedImportJobs: 1, failedProcessorHandoffs: 1 },
  automation: {
    asOfUtc: NOW, autoStartJobs: true,
    queuePause: { enabled: true, threshold: 20, moviesPaused: true, tvPaused: false, moviesWaiting: 3, tvWaiting: 0, unreadable: false, unreadableReason: null },
    libraries: [{ libraryId: "l1", name: "Movies", mediaType: "movies", status: "active", nextSearchUtc: "2026-09-30T11:00:00Z", nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: "2026-09-30T09:00:00Z", lastError: null }],
    lastSearchCycle: { libraryId: "l1", libraryName: "Movies", mediaType: "movies", status: "completed", planned: 2, queued: 0, skipped: 2, startedUtc: "2026-09-30T09:00:00Z", completedUtc: "2026-09-30T09:01:00Z" },
    tasks: [
      { key: "import", name: "Import finished downloads", lastResult: "completed", lastCompletedUtc: "2026-09-30T09:59:00Z", nextRunUtc: "2026-09-30T10:00:15Z" },
      { key: "lists", name: "Check lists", lastResult: "failed", lastCompletedUtc: "2026-09-30T09:50:00Z", nextRunUtc: "2026-09-30T10:05:00Z" },
      { key: "new", name: "Refresh metadata", lastResult: "not-run", lastCompletedUtc: null, nextRunUtc: null }
    ]
  },
  indexers: { asOfUtc: NOW, total: 3, healthy: 2, failing: 1, rateLimited: 0, disabled: 0, queriesToday: 40, failedQueriesToday: 3, grabsToday: 6, topFailing: [{ id: "i2", name: "Nyaa.si", reason: "HTTP 504" }] },
  processors: { asOfUtc: NOW, total: 1, healthy: 0, failing: 1, handoffsToday: 6, waiting: 1 },
  blocklist: { asOfUtc: NOW, count: 48 },
  health: {
    asOfUtc: NOW, problems: 3, unknown: 1, suggestions: 1, lastRunUtc: "2026-09-30T09:45:00Z",
    topProblems: [
      { id: "indexer:i2", title: "Nyaa.si isn\u2019t answering", category: "indexers", message: "HTTP 504 since 27 Sep.", sinceUtc: null, link: "/indexers/indexers", snoozed: false },
      { id: "system:background-problem:startup:service-restart-on-failure", title: "Restart after a crash isn\u2019t set", category: "system", message: "Windows won\u2019t restart Deluno by itself.", sinceUtc: null, link: "/system", snoozed: false }
    ]
  },
  storage: { asOfUtc: NOW, readingUtc: "2026-09-30T09:59:00Z", volumes: [
    { id: "v1", label: "Data (C:)", roles: ["data"], paths: ["C:\\"], available: true, totalBytes: 2e12, freeBytes: 1.5e12 },
    { id: "v2", label: "Library (NAS)", roles: ["library", "downloads"], paths: ["\\nas"], available: false, totalBytes: null, freeBytes: null }
  ] },
  system: {
    ...emptySummary.system!, uptimeSeconds: 27 * 60, updateAvailable: true, latestVersion: "1.0.0-rc.24",
    machine: { capturedUtc: NOW, cpuPercent: 7, memoryBytes: 3.9e9, memoryPercent: 12 },
    jobs: { running: 1, queued: 2, failed: 2 },
    lanes: [{ name: "search-worker", state: "failing", activeJobs: 0, lastError: "Out of memory" }],
    lastBackupUtc: "2026-09-25T09:00:00Z", backupsEnabled: false
  },
  activity: { asOfUtc: NOW, windowHours: 24, searchesRun: 12, downloadsStarted: 3, importsCompleted: 4, cleanupRemovals: 0, retentionRemovals: 0, recent: [
    { id: "a1", category: "filesystem.import.completed", label: "Imported into the library", message: "Imported Sintel (2010).", relatedEntityType: null, relatedEntityId: null, createdUtc: "2026-09-30T09:58:00Z" }
  ] }
};
