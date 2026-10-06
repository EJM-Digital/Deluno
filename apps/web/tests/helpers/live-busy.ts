import type { Page } from "@playwright/test";

/**
 * A busy Live board (#1096): titles in every lane, several downloads that have
 * stopped, a processor hand-off that failed, raw release names that only the
 * library's title makes readable (a film, a show's episode, a season pack, and
 * one title that no longer exists), the speed over six hours, a day of activity
 * with plenty of routine housekeeping that must not show, and countdowns. The dev
 * copy has nothing in flight, so this is how the whole flow is seen. Every list
 * is the stored list the real screen reads; nothing reaches a client.
 */
export async function serveLiveBusy(page: Page) {
  const now = Date.now();
  const at = (ms: number) => new Date(now + ms).toISOString();
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  const host = " on MEDIA-SERVER";
  const count = { total: 184, monitored: 180, unmonitored: 4, missing: 12, upgradable: 9, qualityMet: 130, upcoming: 3, downloading: 4, airing: 0, fileBytes: 1e12 };
  const clients = [
    { id: "c1", name: `Deluge${host}`, protocol: "torrent", reachable: true, paused: false, version: "2.1", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 3, queued: 2 },
    { id: "c2", name: `SABnzbd${host}`, protocol: "usenet", reachable: true, paused: false, version: "4.3", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 0, queued: 0 },
    { id: "c3", name: `qBittorrent${host}`, protocol: "torrent", reachable: false, paused: null, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "unreachable", active: 0, queued: 0 }
  ];
  // Every client with its switch (the summary lists only the enabled ones): one more that is switched off.
  const clientList = [
    ...clients.map((client, n) => ({ id: client.id, name: client.name, protocol: client.protocol, isEnabled: true, priority: n, healthStatus: client.healthStatus })),
    { id: "c4", name: `Transmission${host}`, protocol: "torrent", isEnabled: false, priority: 3, healthStatus: "unknown" }
  ];

  // The library: what each id is. "m99" was deleted, so it is never answered.
  const movieTitles: Record<string, { title: string; year: number }> = {
    m1: { title: "Dune Part Two", year: 2024 }, m2: { title: "Oppenheimer", year: 2023 }, m4: { title: "Barbie", year: 2023 },
    m5: { title: "Metropolis", year: 1927 }, m6: { title: "Nosferatu", year: 1922 }, m7: { title: "Sintel", year: 2010 },
    m8: { title: "Big Buck Bunny", year: 2008 }, m9: { title: "Tears of Steel", year: 2012 },
    m30: { title: "The Wild Robot", year: 2024 }, m31: { title: "Sita Sings the Blues", year: 2008 }, m32: { title: "The Kid", year: 1921 },
    m20: { title: "Coherence", year: 2013 }, m21: { title: "Primer", year: 2004 }, m22: { title: "Moon", year: 2009 },
    m23: { title: "Her", year: 2013 }, m24: { title: "Ex Machina", year: 2014 }, m25: { title: "Arrival", year: 2016 },
    m26: { title: "Past Lives", year: 2023 }, m27: { title: "Aftersun", year: 2022 }, m28: { title: "Anatomy of a Fall", year: 2023 }, m29: { title: "Perfect Days", year: 2023 }
  };
  const showTitles: Record<string, { title: string; year: number }> = {
    s1: { title: "Say Yes to the Dress", year: 2007 }, s2: { title: "Lioness", year: 2023 }, s3: { title: "Severance", year: 2022 }, s4: { title: "The Bear", year: 2022 }, s5: { title: "Shogun", year: 2024 }
  };
  const episodes: Record<string, { seriesId: string; season: number; episode: number }> = {
    e1: { seriesId: "s1", season: 4, episode: 2 }, e2: { seriesId: "s2", season: 2, episode: 5 }, e3: { seriesId: "s3", season: 2, episode: 3 }
  };

  const poster = (label: string, n: number) =>
    "data:image/svg+xml;utf8," +
    encodeURIComponent(`<svg xmlns='http://www.w3.org/2000/svg' width='80' height='120'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='hsl(${(n * 47) % 360} 55% 45%)'/><stop offset='1' stop-color='hsl(${(n * 47 + 60) % 360} 60% 22%)'/></linearGradient></defs><rect width='80' height='120' fill='url(#g)'/><text x='40' y='72' font-size='40' font-family='Georgia,serif' fill='white' fill-opacity='0.85' text-anchor='middle'>${label.slice(0, 1)}</text></svg>`);
  const numberOf = (id: string) => Number(id.replace(/\D/g, "")) || 1;

  const summary = {
    asOfUtc: at(0), buildMilliseconds: 5, unavailableSections: [],
    library: { asOfUtc: at(0), movies: count, shows: { ...count, total: 42, missing: 3, upgradable: 2, qualityMet: 31 }, totalBytes: 2e12, libraries: [], libraryCounts: [{ libraryId: "hd", libraryName: "Movies HD", mediaType: "movies", met: 31, upgradable: 4, downloading: 1, missing: 2, arrivedToday: 6 }, { libraryId: "kids", libraryName: "Kids", mediaType: "movies", met: 12, upgradable: 1, downloading: 0, missing: 0, arrivedToday: 2 }, { libraryId: "tvl", libraryName: "TV", mediaType: "tv", met: 31, upgradable: 2, downloading: 0, missing: 3, arrivedToday: 2 }], mix: { movieQuality: [], movieCodec: [], episodeQuality: [], showCodec: [] }, collections: { followed: 0, monitored: 0, members: 0, inLibrary: 0, syncFailing: 0, lastSyncedUtc: null } },
    downloads: { asOfUtc: at(0), hasReading: true, readingUtc: at(-20_000), active: 3, queued: 2, stalled: 7, processing: 1, importReady: 2, waitingForProcessor: 1, downloadMbps: 14.2, uploadMbps: 1.3, clients, sharingHolds: 0, sharingExtraBytes: 0, leftovers: { scanned: true, count: 0, bytes: 0, scannedUtc: at(-60_000) }, failedDownloads: 0 },
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
    retention: { asOfUtc: at(0), leavingSoon: 3, nextLeaving: [], readyToRemove: 1, removedLast7Days: 2, removedBytesLast7Days: 3e10 },
    subtitles: null,
    requests: { asOfUtc: at(0), seerrServers: 1, seerrHealthy: 1, seerrFailing: 0, acquisitionsLast7Days: { added: 3 }, importLists: 0, importListsByLastSync: {}, listTitlesAddedLast7Days: 0 },
    blocklist: { asOfUtc: at(0), count: 4 },
    health: { asOfUtc: at(0), problems: 0, unknown: 0, suggestions: 0, lastRunUtc: at(-3 * 60_000), topProblems: [] },
    storage: {
      asOfUtc: at(0), readingUtc: at(-60_000),
      volumes: [
        { id: "v1", label: "Films (D:)", roles: ["library"], paths: ["D:/Films"], available: true, totalBytes: 8e12, freeBytes: 1.4e12 },
        { id: "v2", label: "Shows (E:)", roles: ["library"], paths: ["E:/Shows"], available: true, totalBytes: 12e12, freeBytes: 6.5e12 },
        { id: "v3", label: "Downloads (F:)", roles: ["downloads"], paths: ["F:/Downloads"], available: true, totalBytes: 2e12, freeBytes: 0.3e12 }
      ]
    },
    system: { asOfUtc: at(0), version: "1.0.0-rc.23", updateAvailable: false, latestVersion: null, uptimeSeconds: 7200, machine: null, jobs: { running: 1, queued: 0, failed: 0 }, lanes: [], lastBackupUtc: null, nextBackupUtc: null, backupsEnabled: false, internetOutage: false, outageSinceUtc: null },
    activity: { asOfUtc: at(0), windowHours: 24, searchesRun: 30, downloadsStarted: 9, importsCompleted: 6, cleanupRemovals: 2, retentionRemovals: 1, recent: [] }
  };

  // The download queue: what is downloading, waiting, stuck, with the processor and ready to import. A release name is what the indexer called it.
  const q = (id: string, release: string, status: string, progress: number, speed: number, eta: number, extra: Record<string, unknown> = {}) => ({
    id, clientId: "c1", clientName: `Deluge${host}`, protocol: "torrent", mediaType: "movie", title: release,
    releaseName: release, category: "movies", status, progress, speedMbps: speed, etaSeconds: eta,
    sizeBytes: 4.2e9, downloadedBytes: 1e9, peers: 12, indexerName: "NZBgeek", errorMessage: null, addedUtc: at(-20 * 60_000), sourcePath: null, libraryId: "l1", healthFindings: null, ...extra
  });
  const queue = [
    q("q1", "Dune.Part.Two.2024.1080p.WEB-DL", "downloading", 61, 6.4, 540), q("q2", "Oppenheimer.2023.1080p.WEB-DL", "downloading", 88, 5.1, 120),
    q("q4", "Barbie.2023.1080p.WEB-DL", "downloading", 12, 2.7, 1800),
    q("q5", "Metropolis.1927.1080p.WEB-DL", "queued", 0, 0, 0), q("q6", "Nosferatu.1922.1080p.WEB-DL", "queued", 0, 0, 0),
    q("q7", "Sintel.2010.1080p.WEB-DL", "waitingForProcessor", 100, 0, 0), q("q8", "Big.Buck.Bunny.2008.1080p.WEB-DL", "importReady", 100, 0, 0), q("q9", "Tears.of.Steel.2012.1080p.WEB-DL", "importReady", 100, 0, 0),
    // Stuck: stopped at 100 per cent, or a client warning.
    q("s1", "www Torrenting com The Wild Robot 2024 1080p WEB-DL", "stalled", 100, 0, 0, { errorMessage: "missing or invalid torrent data" }),
    q("s2", "Say.Yes.to.the.Dress.2007.S04E02.720p.HDTV", "stalled", 100, 0, 0),
    q("s3", "Sita.Sings.the.Blues.2008.1080p.BluRay", "stalled", 100, 0, 0, { errorMessage: "missing or invalid torrent data" }),
    q("s4", "The.Kid.1921.REMASTERED.1080p", "stalled", 34, 0, 0, { errorMessage: "No one is sharing it" }),
    q("s5", "Lioness.S02E05.1080p.WEB", "stalled", 100, 0, 0), q("s6", "Severance.S02E03.1080p.WEB", "stalled", 100, 0, 0, { errorMessage: "missing or invalid torrent data" }),
    q("s7", "The.Bear.S03.COMPLETE.1080p", "stalled", 100, 0, 0)
  ];

  // The sent downloads: which title each one is for. The deleted title (m99) is linked but no longer exists.
  const dispatch = (id: string, release: string, entityType: string, entityId: string, queueId: string | null, done: boolean, n: number, indexerName = "NZBgeek") => ({
    id, libraryId: "l1", mediaType: entityType === "movie" ? "movies" : "tv", entityType, entityId, releaseName: release,
    indexerName, downloadClientId: "c1", downloadClientName: `Deluge${host}`, status: "done", notesJson: null, createdUtc: at(-(n + 1) * 3_600_000),
    decisionQuality: "WEB 1080p", torrentHashOrItemId: queueId, importCompletedUtc: done ? at(-(n + 1) * 20 * 60_000) : null
  });
  const dispatches = [
    dispatch("d1", queue[0].releaseName, "movie", "m1", "q1", false, 1), dispatch("d2", queue[1].releaseName, "movie", "m2", "q2", false, 2), dispatch("d3", queue[2].releaseName, "movie", "m4", "q4", false, 3),
    dispatch("d4", queue[3].releaseName, "movie", "m5", "q5", false, 4, "DrunkenSlug"), dispatch("d5", queue[4].releaseName, "movie", "m6", "q6", false, 5),
    dispatch("d6", queue[5].releaseName, "movie", "m7", "q7", false, 6), dispatch("d7", queue[6].releaseName, "movie", "m8", "q8", false, 7), dispatch("d8", queue[7].releaseName, "movie", "m9", "q9", false, 8),
    dispatch("d10", queue[8].releaseName, "movie", "m30", "s1", false, 9, "1337x"), dispatch("d11", queue[9].releaseName, "episode", "e1", "s2", false, 10, "EZTV"),
    dispatch("d12", queue[10].releaseName, "movie", "m31", "s3", false, 11, "1337x"), dispatch("d13", queue[11].releaseName, "movie", "m32", "s4", false, 12),
    dispatch("d14", queue[12].releaseName, "episode", "e2", "s5", false, 13, "EZTV"), dispatch("d15", queue[13].releaseName, "episode", "e3", "s6", false, 14, "EZTV"),
    dispatch("d16", queue[14].releaseName, "series", "s4", "s7", false, 15, "EZTV"),
    ...[["m20", "Coherence.2013.1080p"], ["m21", "Primer.2004.1080p"], ["m22", "Moon.2009.1080p"], ["m23", "Her.2013.1080p"], ["m24", "Ex.Machina.2014.1080p"], ["m25", "Arrival.2016.1080p"]].map(([entity, release], n) => dispatch(`d2${n}`, release, "movie", entity, null, true, 20 + n)),
    dispatch("d99", "Deleted.Title.2001.1080p", "movie", "m99", null, true, 30)
  ];
  const handoff = (id: string, queueItemId: string, release: string, status: string, failure: string | null) => ({
    id, libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId, releaseName: release, sourcePath: "/d", processorName: `Cleaner${host}`,
    status, outputPath: null, importJobId: null, failureMessage: failure, createdUtc: at(-3_600_000), updatedUtc: at(-120_000)
  });
  const handoffs = [handoff("h1", "q7", "Sintel.2010.1080p.WEB-DL", "started", null), handoff("h2", "gone", "Metropolis.1927.REMASTERED", "failed", "The processor timed out.")];
  const indexers = ["NZBgeek", "DrunkenSlug", "Nyaa.si", "NZBFinder"].map((name, n) => ({
    id: `i${n}`, name, protocol: "usenet", isEnabled: true, priority: n, healthStatus: name === "Nyaa.si" ? "unreachable" : "healthy",
    lastHealthMessage: name === "Nyaa.si" ? "HTTP 504" : null, lastHealthLatencyMs: 180 + n * 90, lastHealthTestUtc: at(-2 * 86_400_000), consecutiveFailures: 0, rateLimitedUntilUtc: null,
    ...(name === "NZBgeek" ? { queryLimit: 100, limitsUnit: "day", queryLimitStatus: { allowed: true, limit: 100, count: 46, resetsAtUtc: at(8 * 3_600_000), unit: "day" } } : {})
  }));
  // One more that is switched off: its tile is grey and last.
  indexers.push({ id: "i9", name: "OldIndexer", protocol: "usenet", isEnabled: false, priority: 9, healthStatus: "healthy", lastHealthMessage: null, lastHealthLatencyMs: 300, lastHealthTestUtc: at(-3 * 86_400_000), consecutiveFailures: 0, rateLimitedUntilUtc: null });
  // What each indexer did today, from the stored record of its queries.
  const scoreboard = {
    windowDays: 1,
    indexers: indexers.slice(0, 4).map((indexer, n) => ({ id: indexer.id, name: indexer.name, isEnabled: true, healthStatus: indexer.healthStatus, totalQueries: [96, 58, 12, 46][n], failedQueries: [0, 3, 12, 0][n], successfulGrabs: [5, 3, 0, 1][n], lastQueryUtc: at(-[2, 7, 41, 12][n] * 60_000) }))
  };
  const samples = Array.from({ length: 48 }, (_, n) => ({ capturedUtc: at(-(48 - n) * 7.5 * 60_000), speedMbps: Math.max(0, 8 + 7 * Math.sin(n / 4) + (n % 5)), activeCount: 2, uploadMbps: Math.max(0, 1.5 + Math.sin(n / 3)) }));

  // Activity: what matters, and a lot of routine housekeeping that must be left out.
  const mattering: Array<[string, string, string, string]> = [
    ["movies.release.grabbed", "Grabbed Dune Part Two (2024) from NZBgeek.", "Release grabbed", "info"],
    ["filesystem.import.completed", "Imported Sintel (2010) into Movies.", "Imported into the library", "success"],
    ["wanted.search.completed", "Searched Movies: 14 checked, 2 grabbed.", "Search finished", "info"],
    ["download.orphan.removed", "Removed a leftover download folder.", "Leftover removed", "info"],
    ["processing.failed", "The processor could not clean Metropolis (1927): it timed out.", "Processing failed", "error"],
    ["retention.removed", "Removed Old Show S01 after its retention time.", "Removed by retention", "info"],
    ["subtitle.downloaded", "Found English subtitles for Metropolis (1927).", "Subtitles found", "info"],
    ["intake.list.synced", "Checked your Trakt list: 2 new titles.", "List checked", "info"]
  ];
  const routine: Array<[string, string, string, string]> = [
    ["job.started", "Started reading what your files actually are (TV).", "Task started", "info"],
    ["job.completed", "Every file Deluno holds has been read and evaluated under its current preference plan. (TV)", "Task finished", "info"],
    ["metadata.artwork.cleanup", "Artwork cache cleanup found no unreferenced artwork ready to remove.", "Cleaned up artwork", "info"],
    ["job.queued", "Queued a metadata refresh.", "Task queued", "info"]
  ];
  const row = (n: number, [category, message, label, severity]: [string, string, string, string], minutes: number) => ({
    id: `a${n}`, category, label, message, severity, detailsJson: null, relatedJobId: null, relatedEntityType: null, relatedEntityId: null, createdUtc: at(-minutes * 60_000)
  });
  const events = Array.from({ length: 60 }, (_, n) => row(n, n % 3 === 0 ? mattering[(n / 3) % mattering.length] : routine[n % routine.length], 2 + n * 3));
  const categories = ["wanted.search.completed", "movies.release.grabbed", "filesystem.import.completed", "download.orphan.removed", "subtitle.downloaded"];
  // The day's real count by the hour: far more than the 200 a page of events holds, most of it routine.
  const byHour = Array.from({ length: 11 }, (_, n) => [...categories.map((category, k) => ({ hour: n + 1, category, count: 3 + ((n * 7 + k * 5) % 9) })), { hour: n + 1, category: "job.completed", count: 40 }]).flat();

  // Today's arrivals (two of them upgrades), what was refused, requests and what retention will give back.
  const importAt = (minutes: number) => at(-minutes * 60_000);
  const arrival = (entityType: string, entityId: string, release: string, quality: string, minutes: number, extra: Record<string, unknown> = {}) => ({ entityType, entityId, release, quality, previousQuality: null, upgraded: false, sizeBytes: 4e9, importedUtc: importAt(minutes), ...(entityType === "series" ? { libraryId: "tvl", libraryName: "TV" } : Number(entityId.slice(1)) % 3 === 0 ? { libraryId: "kids", libraryName: "Kids" } : { libraryId: "hd", libraryName: "Movies HD" }), ...extra });
  const arrivals = [
    arrival("movie", "m20", "Coherence.2013.1080p", "WEB 1080p", 4), arrival("movie", "m21", "Primer.2004.1080p", "Bluray 1080p", 19), arrival("series", "s2", "Lioness.S02E04.1080p.WEB", "WEB 1080p", 26, { sizeBytes: 1.6e9 }),
    arrival("movie", "m22", "Moon.2009.1080p.BluRay", "Bluray 1080p", 41, { previousQuality: "WEB 720p", upgraded: true, sizeBytes: 9e9 }), arrival("movie", "m23", "Her.2013.2160p.WEB", "WEB 2160p", 63, { previousQuality: "WEB 1080p", upgraded: true, sizeBytes: 15e9 }),
    arrival("movie", "m24", "Ex.Machina.2014.1080p", "WEB 1080p", 95), arrival("movie", "m25", "Arrival.2016.1080p", "WEB 1080p", 130), arrival("series", "s1", "Say.Yes.to.the.Dress.2007.S04E03.1080p", "WEB 1080p", 150, { sizeBytes: 1.2e9 }),
    arrival("movie", "m26", "Past.Lives.2023.1080p", "WEB 1080p", 170), arrival("movie", "m27", "Aftersun.2022.1080p", "Bluray 1080p", 190)
  ];
  const liveExtras = {
    asOfUtc: at(0), sinceUtc: at(-8 * 3_600_000), arrivals, importsToday: 14, importedBytesToday: 64e9, upgrades: arrivals.filter((item) => item.upgraded),
    protected: { refusedToday: 5, unsafeToday: 2, latest: [{ release: "Totally.Real.Film.2024.1080p.WEB", reasonCode: "noVideoStream", reason: "It has no video in it.", blockedUtc: importAt(33) }] },
    requests: [
      { mediaType: "movie", entityId: "m4", title: "Barbie", status: "added", requestedBy: "Sam", createdUtc: importAt(240), updatedUtc: importAt(240) },
      { mediaType: "movie", entityId: "m28", title: "Anatomy of a Fall", status: "added", requestedBy: "Alex", createdUtc: importAt(120), updatedUtc: importAt(120) },
      { mediaType: "series", entityId: "s5", title: "Shogun", status: "added", requestedBy: "Sam", createdUtc: importAt(75), updatedUtc: importAt(75) },
      { mediaType: "movie", entityId: "m29", title: "Perfect Days", status: "search-failed", requestedBy: "Alex", createdUtc: importAt(300), updatedUtc: importAt(300) },
      { mediaType: "movie", entityId: "m26", title: "Past Lives", status: "available", requestedBy: "Sam", createdUtc: importAt(500), updatedUtc: importAt(170) }
    ],
    retentionReclaimBytes: 38e9,
    cleanedUpToday: 3,
    // Every release looked at and not taken today, by plain reason, and the newest ones.
    turnedDown: {
      total: 43,
      reasons: [
        { group: "language", label: "Not in a language you want", count: 14 },
        { group: "notBetter", label: "No better than what you have", count: 11 },
        { group: "tooBig", label: "Bigger than your size limit", count: 9 },
        { group: "fewSeeders", label: "Too few people sharing it", count: 5 },
        { group: "fake", label: "Fake or mislabelled", count: 4 },
        { group: "other", label: "Other reasons", count: 0 }
      ],
      latest: [
        { release: "Dune.Part.Two.2024.iTA.1080p.WEB-DL-NTb", group: "language", label: "Not in a language you want", title: "Dune Part Two", entityType: "movie", entityId: "m1", atUtc: importAt(2) },
        { release: "Oppenheimer.2023.720p.WEB-EVO", group: "notBetter", label: "No better than what you have", title: "Oppenheimer", entityType: "movie", entityId: "m2", atUtc: importAt(5) },
        { release: "Barbie.2023.2160p.REMUX.HDR-FLUX", group: "tooBig", label: "Bigger than your size limit", title: "Barbie", entityType: "movie", entityId: "m4", atUtc: importAt(9) },
        { release: "Primer.2004.1080p.WEB-DL-FAKE", group: "fake", label: "Fake or mislabelled", title: "Primer", entityType: "movie", entityId: "m21", atUtc: importAt(14) },
        { release: "Moon.2009.1080p.BluRay-SPARKS", group: "fewSeeders", label: "Too few people sharing it", title: "Moon", entityType: "movie", entityId: "m22", atUtc: importAt(21) },
        { release: "Her.2013.FRENCH.1080p.WEB-playWEB", group: "language", label: "Not in a language you want", title: "Her", entityType: "movie", entityId: "m23", atUtc: importAt(30) },
        { release: "Arrival.2016.720p.WEB-EVO", group: "notBetter", label: "No better than what you have", title: "Arrival", entityType: "movie", entityId: "m25", atUtc: importAt(44) }
      ]
    },
    // Everything else Deluno is connected to.
    connections: [
      { kind: "processor", id: "p1", name: "Weir", enabled: true, healthy: true, countToday: 6, busy: 1 },
      { kind: "processor", id: "p2", name: "FileFlows", enabled: true, healthy: true, countToday: 0, busy: 0 },
      { kind: "mediaServer", id: "ms1", name: "Plex", enabled: true, healthy: true, countToday: 5, busy: 0 },
      { kind: "requests", id: "rq1", name: "Seerr", enabled: true, healthy: true, countToday: 3, busy: 0 },
      { kind: "subtitles", id: "sb1", name: "OpenSubtitles", enabled: true, healthy: true, countToday: 12, busy: 0 },
      { kind: "lists", id: "ls1", name: "Trakt", enabled: true, healthy: true, countToday: 2, busy: 0 }
    ]
  };
  const airs = (hours: number) => at(hours * 3_600_000);
  const upcoming = [
    { seriesId: "s2", title: "Lioness", startYear: 2023, posterUrl: poster("Lioness", 9), episodeId: "u1", seasonNumber: 2, episodeNumber: 6, episodeTitle: "The Long Way", airDateUtc: airs(1.4) },
    { seriesId: "s3", title: "Severance", startYear: 2022, posterUrl: poster("Severance", 10), episodeId: "u2", seasonNumber: 2, episodeNumber: 4, episodeTitle: "Woe's Hollow", airDateUtc: airs(5.2) },
    { seriesId: "s4", title: "The Bear", startYear: 2022, posterUrl: poster("The Bear", 11), episodeId: "u3", seasonNumber: 3, episodeNumber: 8, episodeTitle: "Ice Chips", airDateUtc: airs(27) },
    { seriesId: "s5", title: "Shogun", startYear: 2024, posterUrl: poster("Shogun", 12), episodeId: "u4", seasonNumber: 1, episodeNumber: 9, episodeTitle: "Crimson Sky", airDateUtc: airs(51) },
    { seriesId: "s1", title: "Say Yes to the Dress", startYear: 2007, posterUrl: poster("Say Yes", 13), episodeId: "u5", seasonNumber: 4, episodeNumber: 4, episodeTitle: null, airDateUtc: airs(75) },
    { seriesId: "s2", title: "Lioness", startYear: 2023, posterUrl: poster("Lioness", 9), episodeId: "u6", seasonNumber: 2, episodeNumber: 7, episodeTitle: "Standoff", airDateUtc: airs(122) }
  ];

  await page.route(/\/api\/dashboard\/summary/, (route) => route.fulfill(json(summary)));
  await page.route(/\/api\/dashboard\/live-extras/, (route) => route.fulfill(json(liveExtras)));
  await page.route(/\/api\/series\/upcoming/, (route) => route.fulfill(json(upcoming)));
  await page.route(/\/api\/download-clients(\?|$)/, (route) => route.fulfill(json(clientList)));
  await page.route(/\/api\/download-clients\/telemetry/, (route) =>
    route.fulfill(json({ summary: { activeCount: 3, queuedCount: 2, stalledCount: 7, importReadyCount: 2, waitingForProcessorCount: 1 }, clients: [{ clientId: "c1", clientName: `Deluge${host}`, queue, summary: { seedingCount: 14 } }, { clientId: "c2", clientName: `SABnzbd${host}`, queue: [], summary: { seedingCount: 0 } }], capturedUtc: at(-30_000) }))
  );
  await page.route(/\/api\/download-dispatches/, (route) => route.fulfill(json({ items: dispatches, hasMore: false, nextPageToken: null })));
  await page.route(/\/api\/integrations\/processors\/handoffs/, (route) => route.fulfill(json(handoffs)));
  await page.route(/\/api\/indexers\/scoreboard/, (route) => route.fulfill(json(scoreboard)));
  await page.route(/\/api\/indexers(\?|$)/, (route) => route.fulfill(json(indexers)));
  await page.route(/\/api\/download-clients\/throughput/, (route) => route.fulfill(json({ hours: 6, samples })));
  await page.route(/\/api\/activity(\?|$)/, (route) => route.fulfill(json({ items: events, hasMore: false, nextPageToken: null })));
  await page.route(/\/api\/activity\/by-hour/, (route) => route.fulfill(json({ sinceUtc: at(-86_400_000), total: byHour.reduce((sum, r) => sum + r.count, 0), buckets: byHour })));
  // One request names every title and gives its poster; a title that no longer exists is simply absent.
  await page.route(/\/api\/movies\/titles/, (route) => {
    const ids = (new URL(route.request().url()).searchParams.get("ids") ?? "").split(",").filter(Boolean);
    return route.fulfill(json(Object.fromEntries(ids.filter((id) => movieTitles[id]).map((id) => [id, { ...movieTitles[id], posterUrl: poster(movieTitles[id].title, numberOf(id)) }]))));
  });
  await page.route(/\/api\/series\/titles/, (route) => {
    const params = new URL(route.request().url()).searchParams;
    const showIds = new Set((params.get("ids") ?? "").split(",").filter(Boolean));
    const episodeRows = Object.fromEntries((params.get("episodeIds") ?? "").split(",").filter((id) => episodes[id]).map((id) => [id, episodes[id]]));
    for (const episode of Object.values(episodeRows)) showIds.add(episode.seriesId);
    return route.fulfill(json({ series: Object.fromEntries([...showIds].filter((id) => showTitles[id]).map((id) => [id, { ...showTitles[id], posterUrl: poster(showTitles[id].title, numberOf(id) + 7) }])), episodes: episodeRows }));
  });
  return { liveExtras, upcoming, clientList, summary, queue, dispatches, handoffs, indexers, scoreboard, samples, events, movieTitles, showTitles, episodes, poster, numberOf, at, json, host, clients, byHour };
}
