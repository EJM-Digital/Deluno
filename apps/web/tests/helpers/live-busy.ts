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
  const host = " on WIN-7K3";
  const count = { total: 184, monitored: 180, unmonitored: 4, missing: 12, upgradable: 9, qualityMet: 150, upcoming: 3, downloading: 4, airing: 0, fileBytes: 1e12 };
  const clients = [
    { id: "c1", name: `Deluge${host}`, protocol: "torrent", reachable: true, paused: false, version: "2.1", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 3, queued: 2 },
    { id: "c2", name: `SABnzbd${host}`, protocol: "usenet", reachable: true, paused: false, version: "4.3", warnings: 0, freeSpaceBytes: 2e12, healthStatus: "healthy", active: 0, queued: 0 }
  ];

  // The library: what each id is. "m99" was deleted, so it is never answered.
  const movieTitles: Record<string, { title: string; year: number }> = {
    m1: { title: "Dune Part Two", year: 2024 }, m2: { title: "Oppenheimer", year: 2023 }, m4: { title: "Barbie", year: 2023 },
    m5: { title: "Metropolis", year: 1927 }, m6: { title: "Nosferatu", year: 1922 }, m7: { title: "Sintel", year: 2010 },
    m8: { title: "Big Buck Bunny", year: 2008 }, m9: { title: "Tears of Steel", year: 2012 },
    m30: { title: "The Wild Robot", year: 2024 }, m31: { title: "Sita Sings the Blues", year: 2008 }, m32: { title: "The Kid", year: 1921 },
    m20: { title: "Coherence", year: 2013 }, m21: { title: "Primer", year: 2004 }, m22: { title: "Moon", year: 2009 },
    m23: { title: "Her", year: 2013 }, m24: { title: "Ex Machina", year: 2014 }, m25: { title: "Arrival", year: 2016 }
  };
  const showTitles: Record<string, { title: string; year: number }> = {
    s1: { title: "Say Yes to the Dress", year: 2007 }, s2: { title: "Lioness", year: 2023 }, s3: { title: "Severance", year: 2022 }, s4: { title: "The Bear", year: 2022 }
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
    library: { asOfUtc: at(0), movies: count, shows: { ...count, total: 42, missing: 3, upgradable: 2 }, totalBytes: 2e12, libraries: [], mix: { movieQuality: [], movieCodec: [], episodeQuality: [], showCodec: [] }, collections: { followed: 0, monitored: 0, members: 0, inLibrary: 0, syncFailing: 0, lastSyncedUtc: null } },
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
    retention: null, subtitles: null, requests: null,
    blocklist: { asOfUtc: at(0), count: 4 },
    health: { asOfUtc: at(0), problems: 0, unknown: 0, suggestions: 0, lastRunUtc: at(-3 * 60_000), topProblems: [] },
    storage: { asOfUtc: at(0), readingUtc: at(-60_000), volumes: [] },
    system: { asOfUtc: at(0), version: "1.0.0-rc.23", updateAvailable: false, latestVersion: null, uptimeSeconds: 7200, machine: null, jobs: { running: 1, queued: 0, failed: 0 }, lanes: [], lastBackupUtc: null, nextBackupUtc: null, backupsEnabled: false, internetOutage: false, outageSinceUtc: null, notificationsUnread: 0 },
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
    lastHealthMessage: name === "Nyaa.si" ? "HTTP 504" : null, lastHealthLatencyMs: 180 + n * 90, lastHealthTestUtc: at(-2 * 86_400_000), consecutiveFailures: 0, rateLimitedUntilUtc: null
  }));
  // What each indexer did today, from the stored record of its queries.
  const scoreboard = {
    windowDays: 1,
    indexers: indexers.map((indexer, n) => ({ id: indexer.id, name: indexer.name, isEnabled: true, healthStatus: indexer.healthStatus, totalQueries: [96, 58, 12, 46][n], failedQueries: [0, 3, 12, 0][n], successfulGrabs: [5, 3, 0, 1][n], lastQueryUtc: at(-[2, 7, 41, 12][n] * 60_000) }))
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

  await page.route(/\/api\/dashboard\/summary/, (route) => route.fulfill(json(summary)));
  await page.route(/\/api\/download-clients\/telemetry/, (route) =>
    route.fulfill(json({ summary: { activeCount: 3, queuedCount: 2, stalledCount: 7, importReadyCount: 2, waitingForProcessorCount: 1 }, clients: [{ clientId: "c1", clientName: `Deluge${host}`, queue }], capturedUtc: at(-30_000) }))
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
  return { summary, queue, dispatches, handoffs, indexers, scoreboard, samples, events, movieTitles, showTitles, episodes, poster, numberOf, at, json, host, clients, byHour };
}
