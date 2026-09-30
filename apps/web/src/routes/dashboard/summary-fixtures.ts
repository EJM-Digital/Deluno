/** Two whole summaries for the Dashboard's tests: a busy install and a brand-new one. */
import type { DashboardMediaCounts, DashboardSummary } from "../../lib/api";

const NOW = "2026-09-30T10:00:00.000Z";

function counts(overrides: Partial<DashboardMediaCounts> = {}): DashboardMediaCounts {
  return { total: 0, monitored: 0, unmonitored: 0, missing: 0, upgradable: 0, qualityMet: 0, upcoming: 0, downloading: 0, airing: 0, fileBytes: 0, ...overrides };
}

/** A new install: every section answers, and every number is zero. */
export function emptySummary(): DashboardSummary {
  return {
    asOfUtc: NOW,
    buildMilliseconds: 12,
    unavailableSections: [],
    library: {
      asOfUtc: NOW,
      movies: counts(),
      shows: counts(),
      totalBytes: 0,
      libraries: [],
      mix: { movieQuality: [], movieCodec: [], episodeQuality: [], showCodec: [] },
      collections: { followed: 0, monitored: 0, members: 0, inLibrary: 0, syncFailing: 0, lastSyncedUtc: null }
    },
    downloads: {
      asOfUtc: NOW, hasReading: false, readingUtc: null, active: 0, queued: 0, stalled: 0, processing: 0, importReady: 0, waitingForProcessor: 0,
      downloadMbps: 0, uploadMbps: 0, clients: [], sharingHolds: 0, sharingExtraBytes: 0,
      leftovers: { scanned: false, count: 0, bytes: 0, scannedUtc: null }, failedDownloads: 0
    },
    pipeline: {
      asOfUtc: NOW,
      today: { grabbed: 0, detected: 0, imported: 0, failed: 0 },
      totals: { grabbed: 0, detected: 0, imported: 0, failedGrabs: 0, failedImports: 0, activeDispatches: 0 },
      averageGrabToDetectionSeconds: null,
      averageDetectionToImportSeconds: null
    },
    imports: { asOfUtc: NOW, recoveryOpen: 0, recoveryByKind: {}, failedImportJobs: 0, failedProcessorHandoffs: 0 },
    automation: {
      asOfUtc: NOW, autoStartJobs: false,
      queuePause: { enabled: false, threshold: 0, moviesPaused: false, tvPaused: false, moviesWaiting: 0, tvWaiting: 0, unreadable: false, unreadableReason: null },
      libraries: [], lastSearchCycle: null, tasks: []
    },
    indexers: { asOfUtc: NOW, total: 0, healthy: 0, failing: 0, rateLimited: 0, disabled: 0, queriesToday: 0, failedQueriesToday: 0, grabsToday: 0, topFailing: [] },
    processors: { asOfUtc: NOW, total: 0, healthy: 0, failing: 0, handoffsToday: 0, waiting: 0 },
    retention: { asOfUtc: NOW, leavingSoon: 0, nextLeaving: [], readyToRemove: 0, removedLast7Days: 0, removedBytesLast7Days: 0 },
    subtitles: { asOfUtc: NOW, wanted: 0, held: 0 },
    requests: { asOfUtc: NOW, seerrServers: 0, seerrHealthy: 0, seerrFailing: 0, acquisitionsLast7Days: {}, importLists: 0, importListsByLastSync: {}, listTitlesAddedLast7Days: 0 },
    blocklist: { asOfUtc: NOW, count: 0 },
    health: { asOfUtc: NOW, problems: 0, unknown: 0, suggestions: 0, lastRunUtc: null, topProblems: [] },
    storage: { asOfUtc: NOW, readingUtc: null, volumes: [] },
    system: {
      asOfUtc: NOW, version: "1.0.0-rc.23", updateAvailable: false, latestVersion: null, uptimeSeconds: 120, machine: null,
      jobs: { running: 0, queued: 0, failed: 0 }, lanes: [], lastBackupUtc: null, nextBackupUtc: null, backupsEnabled: false,
      internetOutage: false, outageSinceUtc: null, notificationsUnread: 0
    },
    activity: { asOfUtc: NOW, windowHours: 24, searchesRun: 0, downloadsStarted: 0, importsCompleted: 0, cleanupRemovals: 0, retentionRemovals: 0, recent: [] }
  };
}

/** A working install with things to look at in every area. */
export function fullSummary(): DashboardSummary {
  const summary = emptySummary();
  summary.library = {
    asOfUtc: NOW,
    movies: counts({ total: 20, monitored: 19, unmonitored: 1, missing: 2, upgradable: 2, qualityMet: 15, fileBytes: 900e9 }),
    shows: counts({ total: 2, monitored: 2, missing: 1, upgradable: 0, qualityMet: 1, fileBytes: 120e9 }),
    totalBytes: 1020e9,
    libraries: [],
    mix: {
      movieQuality: [{ label: "WEB 1080p", count: 8 }, { label: "Blu-ray 1080p", count: 9 }],
      movieCodec: [{ label: "x264", count: 12 }],
      episodeQuality: [{ label: "WEB 720p", count: 40 }],
      showCodec: [{ label: "x265", count: 2 }]
    },
    collections: { followed: 3, monitored: 3, members: 12, inLibrary: 9, syncFailing: 1, lastSyncedUtc: "2026-09-30T09:00:00.000Z" }
  };
  summary.downloads = {
    ...summary.downloads!, hasReading: true, readingUtc: NOW, active: 4, queued: 1, stalled: 2, downloadMbps: 3.9, uploadMbps: 0.6,
    clients: [
      { id: "c1", name: "qBittorrent", protocol: "torrent", reachable: true, paused: false, version: "5", warnings: 0, freeSpaceBytes: 1e12, healthStatus: "healthy", active: 3, queued: 1 },
      { id: "c2", name: "SABnzbd", protocol: "usenet", reachable: false, paused: null, version: null, warnings: 1, freeSpaceBytes: null, healthStatus: "failing", active: 0, queued: 0 }
    ],
    failedDownloads: 1
  };
  summary.imports = { asOfUtc: NOW, recoveryOpen: 3, recoveryByKind: { unmatched: 2, corrupt: 1 }, failedImportJobs: 1, failedProcessorHandoffs: 1 };
  summary.automation = {
    ...summary.automation!, autoStartJobs: true,
    libraries: [{ libraryId: "l1", name: "Movies", mediaType: "movies", status: "idle", nextSearchUtc: "2026-09-30T11:00:00.000Z", nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: NOW, lastError: null }],
    lastSearchCycle: { libraryId: "l1", libraryName: "Movies", mediaType: "movies", status: "completed", planned: 5, queued: 2, skipped: 3, startedUtc: "2026-09-30T09:30:00.000Z", completedUtc: "2026-09-30T09:32:00.000Z" }
  };
  summary.indexers = { ...summary.indexers!, total: 14, healthy: 13, failing: 1, topFailing: [{ id: "i1", name: "Nyaa.si", reason: "no answer" }] };
  summary.processors = { asOfUtc: NOW, total: 1, healthy: 1, failing: 0, handoffsToday: 5, waiting: 0 };
  summary.retention = {
    asOfUtc: NOW, leavingSoon: 4,
    nextLeaving: [{ entityId: "m1", title: "Big Buck Bunny", mediaType: "movie", removeAfterUtc: "2026-10-02T10:00:00.000Z" }],
    readyToRemove: 0, removedLast7Days: 3, removedBytesLast7Days: 5e9
  };
  summary.subtitles = { asOfUtc: NOW, wanted: 6, held: 14 };
  summary.requests = { asOfUtc: NOW, seerrServers: 1, seerrHealthy: 1, seerrFailing: 0, acquisitionsLast7Days: { filled: 4 }, importLists: 3, importListsByLastSync: { ok: 3 }, listTitlesAddedLast7Days: 5 };
  summary.health = {
    asOfUtc: NOW, problems: 7, unknown: 0, suggestions: 0, lastRunUtc: NOW,
    topProblems: [
      { id: "h1", title: "Nyaa.si isn't answering", category: "indexer", message: "It stopped answering 14 minutes ago.", sinceUtc: NOW, link: "/indexers/indexers", snoozed: false },
      { id: "h2", title: "A drive is nearly full", category: "storage", message: "Less than 5% is free.", sinceUtc: NOW, link: "/system", snoozed: false }
    ]
  };
  summary.storage = {
    asOfUtc: NOW, readingUtc: NOW,
    volumes: [{ id: "v1", label: "Library (NAS)", roles: ["library"], paths: ["/media"], available: true, totalBytes: 8e12, freeBytes: 4.6e12 }]
  };
  summary.system = {
    ...summary.system!, uptimeSeconds: 27 * 60, updateAvailable: true, latestVersion: "1.0.0-rc.24",
    jobs: { running: 1, queued: 0, failed: 2 }, lanes: [{ name: "Searching", state: "failing", activeJobs: 0, lastError: "boom" }]
  };
  summary.activity = {
    ...summary.activity!, searchesRun: 363,
    recent: [
      { id: "a1", category: "filesystem.import.completed", label: "Imported into the library", message: "Imported The Little Shop of Horrors (1960).", relatedEntityType: "movie", relatedEntityId: "m9", createdUtc: "2026-09-30T09:58:00.000Z" }
    ]
  };
  return summary;
}
