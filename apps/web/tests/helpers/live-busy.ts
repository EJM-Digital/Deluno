import type { Page } from "@playwright/test";

/**
 * A busy Live board (#1096): titles in every lane, a processor hand-off that
 * failed, the speed over six hours, a day of activity and countdowns. The dev
 * copy has nothing in flight, so this is how the whole flow is seen. Every
 * list is the stored list the real screen reads; nothing reaches a client.
 */
export async function serveLiveBusy(page: Page) {
  const now = Date.now();
  const at = (ms: number) => new Date(now + ms).toISOString();
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  const count = { total: 184, monitored: 180, unmonitored: 4, missing: 12, upgradable: 9, qualityMet: 150, upcoming: 3, downloading: 4, airing: 0, fileBytes: 1e12 };
  const clients = [
    { id: "c1", name: "Deluge", protocol: "torrent", reachable: true, paused: false, version: "2.1", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 3, queued: 2 },
    { id: "c2", name: "SABnzbd", protocol: "usenet", reachable: true, paused: false, version: "4.3", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 0, queued: 0 }
  ];
  const summary = {
    asOfUtc: at(0), buildMilliseconds: 5, unavailableSections: [],
    library: { asOfUtc: at(0), movies: count, shows: { ...count, total: 42, missing: 3, upgradable: 2 }, totalBytes: 2e12, libraries: [], mix: { movieQuality: [], movieCodec: [], episodeQuality: [], showCodec: [] }, collections: { followed: 0, monitored: 0, members: 0, inLibrary: 0, syncFailing: 0, lastSyncedUtc: null } },
    downloads: { asOfUtc: at(0), hasReading: true, readingUtc: at(-20_000), active: 3, queued: 2, stalled: 1, processing: 1, importReady: 2, waitingForProcessor: 1, downloadMbps: 14.2, uploadMbps: 1.3, clients, sharingHolds: 0, sharingExtraBytes: 0, leftovers: { scanned: true, count: 0, bytes: 0, scannedUtc: at(-60_000) }, failedDownloads: 0 },
    pipeline: { asOfUtc: at(0), today: { grabbed: 9, detected: 8, imported: 6, failed: 1 }, totals: { grabbed: 400, detected: 390, imported: 380, failedGrabs: 4, failedImports: 6, activeDispatches: 7 }, averageGrabToDetectionSeconds: 40, averageDetectionToImportSeconds: 600 },
    imports: { asOfUtc: at(0), recoveryOpen: 0, recoveryByKind: {}, failedImportJobs: 0, failedProcessorHandoffs: 1 },
    automation: {
      asOfUtc: at(0), autoStartJobs: true,
      queuePause: { enabled: false, threshold: 0, moviesPaused: false, tvPaused: false, moviesWaiting: 0, tvWaiting: 0, unreadable: false, unreadableReason: null },
      libraries: [
        { libraryId: "l1", name: "Movies", mediaType: "movies", status: "active", nextSearchUtc: at(12 * 60_000 + 4_000), nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: at(-48 * 60_000), lastError: null },
        { libraryId: "l2", name: "TV shows", mediaType: "tv", status: "running", nextSearchUtc: at(50 * 60_000), nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: at(-10 * 60_000), lastError: null }
      ],
      lastSearchCycle: { libraryId: "l1", libraryName: "Movies", mediaType: "movies", status: "completed", planned: 14, queued: 2, skipped: 12, startedUtc: at(-49 * 60_000), completedUtc: at(-48 * 60_000) },
      tasks: [
        { key: "health.checks", name: "Check health", lastResult: "completed", lastCompletedUtc: at(-3 * 60_000), nextRunUtc: at(27 * 60_000) },
        { key: "intake.automation", name: "Check lists", lastResult: "completed", lastCompletedUtc: at(-20 * 60_000), nextRunUtc: at(40 * 60_000) },
        { key: "rss.sync", name: "Watch indexers for new releases", lastResult: "completed", lastCompletedUtc: at(-60_000), nextRunUtc: at(9 * 60_000) },
        ...Array.from({ length: 8 }, (_, n) => ({ key: `t${n}`, name: `Scheduled task number ${n + 1}`, lastResult: "completed", lastCompletedUtc: at(-3_600_000), nextRunUtc: at((n + 2) * 3_600_000) }))
      ]
    },
    indexers: { asOfUtc: at(0), total: 4, healthy: 3, failing: 1, rateLimited: 0, disabled: 0, queriesToday: 212, failedQueriesToday: 9, grabsToday: 9, topFailing: [] },
    processors: { asOfUtc: at(0), total: 1, healthy: 1, failing: 0, handoffsToday: 6, waiting: 1 },
    retention: null, subtitles: null, requests: null,
    blocklist: { asOfUtc: at(0), count: 4 },
    health: { asOfUtc: at(0), problems: 0, unknown: 0, suggestions: 0, lastRunUtc: at(-3 * 60_000), topProblems: [] },
    storage: { asOfUtc: at(0), readingUtc: at(-60_000), volumes: [] },
    system: { asOfUtc: at(0), version: "1.0.0-rc.23", updateAvailable: false, latestVersion: null, uptimeSeconds: 7200, machine: null, jobs: { running: 1, queued: 0, failed: 0 }, lanes: [], lastBackupUtc: null, nextBackupUtc: null, backupsEnabled: false, internetOutage: false, outageSinceUtc: null, notificationsUnread: 0 },
    activity: {
      asOfUtc: at(0), windowHours: 24, searchesRun: 30, downloadsStarted: 9, importsCompleted: 6, cleanupRemovals: 2, retentionRemovals: 1,
      recent: [
        ["movies.release.grabbed", "Grabbed Dune Part Two (2024) from NZBgeek.", "Release grabbed"],
        ["filesystem.import.completed", "Imported Sintel (2010) into Movies.", "Imported into the library"],
        ["wanted.search.completed", "Searched Movies: 14 checked, 2 grabbed.", "Search finished"],
        ["download.orphan.removed", "Removed a leftover download folder.", "Leftover removed"],
        ["job.failed", "An import could not finish: the disk was full.", "Task failed"],
        ["retention.removed", "Removed Old Show S01 after its retention time.", "Removed by retention"],
        ["subtitle.downloaded", "Found English subtitles for Metropolis (1927).", "Subtitles found"],
        ["intake.list.synced", "Checked your Trakt list: 2 new titles.", "List checked"]
      ].map(([category, message, label], n) => ({ id: `a${n}`, category, label, message, relatedEntityType: null, relatedEntityId: null, createdUtc: at(-(n + 1) * 7 * 60_000) }))
    }
  };
  const q = (id: string, title: string, status: string, progress: number, speed: number, eta: number, extra: Record<string, unknown> = {}) => ({
    id, clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title,
    releaseName: title.replace(/ \((\d{4})\)/, ".$1.1080p.WEB-DL"), category: "movies", status, progress, speedMbps: speed, etaSeconds: eta,
    sizeBytes: 4.2e9, downloadedBytes: 1e9, peers: 12, indexerName: "NZBgeek", errorMessage: null, addedUtc: at(-20 * 60_000), sourcePath: null, libraryId: "l1", healthFindings: null, ...extra
  });
  const queue = [
    q("q1", "Dune Part Two (2024)", "downloading", 61, 6.4, 540), q("q2", "Oppenheimer (2023)", "downloading", 88, 5.1, 120),
    q("q3", "The Kid (1921)", "stalled", 34, 0, 0, { errorMessage: "No one is sharing it" }), q("q4", "Barbie (2023)", "downloading", 12, 2.7, 1800),
    q("q5", "Metropolis (1927)", "queued", 0, 0, 0), q("q6", "Nosferatu (1922)", "queued", 0, 0, 0),
    q("q7", "Sintel (2010)", "waitingForProcessor", 100, 0, 0), q("q8", "Big Buck Bunny (2008)", "importReady", 100, 0, 0), q("q9", "Tears of Steel (2012)", "importReady", 100, 0, 0)
  ];
  const dispatch = (n: number, title: string, done: boolean) => ({
    id: `d${n}`, libraryId: "l1", mediaType: "movies", entityType: "movie", entityId: `m${n}`,
    releaseName: title.replace(/ \((\d{4})\)/, ".$1.1080p.WEB-DL"), indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "Deluge",
    status: "done", notesJson: null, createdUtc: at(-(n + 1) * 3_600_000), decisionQuality: "WEB 1080p", importCompletedUtc: done ? at(-(n - 19) * 20 * 60_000) : null
  });
  const dispatches = [
    ...queue.map((item, n) => dispatch(n + 1, item.title, false)),
    ...["Coherence (2013)", "Primer (2004)", "Moon (2009)", "Her (2013)", "Ex Machina (2014)", "Arrival (2016)"].map((title, n) => dispatch(20 + n, title, true))
  ];
  const handoff = (id: string, queueItemId: string, release: string, status: string, failure: string | null) => ({
    id, libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId, releaseName: release, sourcePath: "/d", processorName: "Cleaner",
    status, outputPath: null, importJobId: null, failureMessage: failure, createdUtc: at(-3_600_000), updatedUtc: at(-120_000)
  });
  const handoffs = [handoff("h1", "q7", "Sintel.2010.1080p.WEB-DL", "started", null), handoff("h2", "gone", "Metropolis.1927.REMASTERED", "failed", "The processor timed out.")];
  const indexers = ["NZBgeek", "DrunkenSlug", "Nyaa.si", "NZBFinder"].map((name, n) => ({
    id: `i${n}`, name, protocol: "usenet", isEnabled: true, priority: n, healthStatus: name === "Nyaa.si" ? "unreachable" : "healthy",
    lastHealthMessage: name === "Nyaa.si" ? "HTTP 504" : null, lastHealthLatencyMs: 180 + n * 90, lastHealthTestUtc: at(-4 * 60_000), consecutiveFailures: 0, rateLimitedUntilUtc: null
  }));
  const samples = Array.from({ length: 48 }, (_, n) => ({ capturedUtc: at(-(48 - n) * 7.5 * 60_000), speedMbps: Math.max(0, 8 + 7 * Math.sin(n / 4) + (n % 5)), activeCount: 2, uploadMbps: Math.max(0, 1.5 + Math.sin(n / 3)) }));
  const categories = ["wanted.search.completed", "movies.release.grabbed", "filesystem.import.completed", "download.orphan.removed", "subtitle.downloaded"];
  const events = Array.from({ length: 40 }, (_, n) => ({
    id: `e${n}`, category: categories[n % 5], message: "Something Deluno did.", severity: "info", detailsJson: null, relatedJobId: null,
    relatedEntityType: null, relatedEntityId: null, createdUtc: new Date(new Date(now).setHours(0, 0, 0, 0) + ((Math.floor(n / 2) % 10) + 1) * 3_600_000 + n * 97_000).toISOString()
  }));
  const poster = (n: number) =>
    "data:image/svg+xml;utf8," +
    encodeURIComponent(`<svg xmlns='http://www.w3.org/2000/svg' width='80' height='120'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='hsl(${(n * 47) % 360} 55% 45%)'/><stop offset='1' stop-color='hsl(${(n * 47 + 60) % 360} 60% 22%)'/></linearGradient></defs><rect width='80' height='120' fill='url(#g)'/></svg>`);

  await page.route(/\/api\/dashboard\/summary/, (route) => route.fulfill(json(summary)));
  await page.route(/\/api\/download-clients\/telemetry/, (route) =>
    route.fulfill(json({ summary: { activeCount: 3, queuedCount: 2, stalledCount: 1, importReadyCount: 2, waitingForProcessorCount: 1 }, clients: [{ clientId: "c1", clientName: "Deluge", queue }], capturedUtc: at(-30_000) }))
  );
  await page.route(/\/api\/download-dispatches/, (route) => route.fulfill(json({ items: dispatches, hasMore: false, nextPageToken: null })));
  await page.route(/\/api\/integrations\/processors\/handoffs/, (route) => route.fulfill(json(handoffs)));
  await page.route(/\/api\/indexers(\?|$)/, (route) => route.fulfill(json(indexers)));
  await page.route(/\/api\/download-clients\/throughput/, (route) => route.fulfill(json({ hours: 6, samples })));
  await page.route(/\/api\/activity(\?|$)/, (route) => route.fulfill(json({ items: events, hasMore: false, nextPageToken: null })));
  // The day's real count by the hour: far more than the 200 a page of events holds.
  const byHour = Array.from({ length: 11 }, (_, n) => categories.map((category, k) => ({ hour: n + 1, category, count: 3 + ((n * 7 + k * 5) % 9) }))).flat();
  await page.route(/\/api\/activity\/by-hour/, (route) => route.fulfill(json({ sinceUtc: at(-86_400_000), total: byHour.reduce((sum, row) => sum + row.count, 0), buckets: byHour })));
  // One request answers every poster; a title that no longer exists is simply absent.
  await page.route(/\/api\/movies\/posters/, (route) => {
    const ids = (new URL(route.request().url()).searchParams.get("ids") ?? "").split(",").filter(Boolean);
    return route.fulfill(json(Object.fromEntries(ids.map((id) => [id, poster(Number(id.replace(/\D/g, "")) || 1)]))));
  });
  await page.route(/\/api\/series\/posters/, (route) => route.fulfill(json({})));
  await page.route(/\/api\/movies\/m[0-9]+$/, (route) => route.fulfill(json({ id: "m", title: "x", posterUrl: poster(Number(route.request().url().match(/m([0-9]+)$/)?.[1] ?? 1)) })));
}
