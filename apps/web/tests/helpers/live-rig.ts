import type { Page } from "@playwright/test";
import { serveLiveBusy } from "./live-busy";

/**
 * A Live board shaped like the real rig on 1 Oct (#1096): nothing downloading or
 * waiting, seventeen downloads stopped at 100 per cent with "Missing or invalid
 * torrent data!" (Deluge, some qBittorrent) and three more with a warning, four
 * items with the processor (two refused with long technical messages, one
 * waiting), about thirty import cases, some for the very downloads that are
 * cards, one film that is in the library with a poster, one that was never added,
 * and an activity list dominated by "episode search executed ... skipped".
 * The sent-download list the cards are matched against holds only the newest 60,
 * as the real one does; the older ones need the archived list.
 */
export async function serveLiveRig(page: Page) {
  const parts = await serveLiveBusy(page);
  const { at, json, host, poster, numberOf } = parts;
  const emptyList = <T,>(list: T[]) => list.splice(0, list.length);

  // ---- The library
  Object.assign(parts.movieTitles, {
    m32: { title: "The Kid", year: 1921 }, m31: { title: "Sita Sings the Blues", year: 2008 }, m40: { title: "The Little Shop of Horrors", year: 1960 },
    m41: { title: "Night of the Living Dead", year: 1968 }, m42: { title: "Plan 9 from Outer Space", year: 1959 }
  });
  delete (parts.movieTitles as Record<string, unknown>).m30; // The Wild Robot was never added.
  Object.assign(parts.showTitles, { s1: { title: "Say Yes to the Dress", year: 2007 }, s2: { title: "Lioness", year: 2023 }, s5: { title: "Say Yes To The Dress Atlanta", year: 2010 } });
  for (let n = 0; n < 12; n += 1) (parts.episodes as Record<string, unknown>)[`ls${n}`] = { seriesId: "s2", season: 3, episode: 8 - (n % 8) };
  Object.assign(parts.episodes, { e7: { seriesId: "s1", season: 4, episode: 7 }, e18: { seriesId: "s1", season: 5, episode: 18 }, e91: { seriesId: "s1", season: 9, episode: 1 } });

  // ---- The queue
  const q = (id: string, clientId: string, release: string, status: string, error: string | null, extra: Record<string, unknown> = {}) => ({
    id, clientId, clientName: clientId === "c1" ? `Deluge${host}` : `qBittorrent${host}`, protocol: "torrent", mediaType: "movie", title: release,
    releaseName: release, category: "movies", status, progress: status === "stalled" ? 100 : 100, speedMbps: 0, etaSeconds: 0,
    sizeBytes: 5.5e9, downloadedBytes: 5.5e9, peers: 0, indexerName: clientId === "c1" ? `Deluge${host}` : `qBittorrent${host}`, errorMessage: error, addedUtc: at(-5 * 3_600_000), sourcePath: null, libraryId: "l1", healthFindings: null, ...extra
  });
  const invalid = "Missing or invalid torrent data!";
  const stuckNames: Array<[string, string, string | null]> = [
    ["www Torrenting com The Wild Robot 2024 1080p WEB-DL x264", "c1", invalid],
    ["Sita.Sings.The.Blues.2008.1080p.BluRay.x264", "c1", invalid],
    ["The Kid 1921 CC 1080p BluRay x264 nikt0", "c3", "qBittorrent no longer lists this download."],
    ["Say.Yes.to.the.Dress.2007.S04E07.The.Blowout.Sale.1080p.WEB-DL.AAC2.0.H.264", "c1", invalid],
    ["Say.Yes.to.the.Dress.2007.S05E18.In.the.Name.of.Love.1080p.WEB-DL", "c1", invalid],
    ["Say.Yes.to.the.Dress.2007.S09E01.Big.Bad.Bride.1080p.WEB-DL", "c1", invalid],
    ["Night.of.the.Living.Dead.1968.1080p.BluRay.x264", "c3", invalid],
    ["Plan.9.from.Outer.Space.1959.1080p.BluRay.x264", "c1", invalid],
    ["Lioness.S03E08.1080p.WEB.h264-ETHEL", "c1", invalid],
    ["Lioness.S03E07.1080p.WEB.h264-ETHEL", "c1", invalid],
    ["Say.Yes.to.the.Dress.Atlanta.S07E03.1080p.WEB-DL", "c3", invalid],
    ["Some.Obscure.Film.1973.1080p.BluRay.x264", "c1", invalid],
    ["Another.Obscure.Film.1981.720p.BluRay.x264", "c1", invalid],
    ["Third.Obscure.Film.1990.1080p.BluRay.x264", "c3", invalid],
    ["Fourth.Obscure.Film.1999.1080p.BluRay.x264", "c1", invalid],
    ["Fifth.Obscure.Film.2002.1080p.BluRay.x264", "c1", invalid],
    ["Sixth.Obscure.Film.2005.1080p.BluRay.x264", "c1", invalid]
  ];
  const queue: Array<ReturnType<typeof q>> = stuckNames.map(([name, client, error], n) => q(`s${n + 1}`, client, name, "stalled", error));
  // Three more that downloaded and carry a warning from their client.
  for (const n of [1, 2, 3]) queue.push(q(`w${n}`, "c1", `Warned.Film.${n}.2011.1080p.WEB-DL`, "downloading", null, { speedMbps: 0, healthFindings: [{ id: `f${n}`, severity: "warning", summary: "The download client reported a problem with this download.", code: "client" }] }));
  // With the processor.
  queue.push(q("p1", "c1", "The Little Shop of Horrors 1960 1080p BluRay x264-PSYCHD", "processingFailed", null));
  queue.push(q("p2", "c1", "Say.Yes.to.the.Dress.2007.S04E07.The.Blowout.Sale.1080p.WEB-DL.AAC2.0.H.264", "processingFailed", null));
  queue.push(q("p3", "c1", "Night.of.the.Living.Dead.1968.REMASTERED.1080p", "waitingForProcessor", null));
  queue.push(q("p4", "c1", "Plan.9.from.Outer.Space.1959.REMASTERED.720p", "processing", null));
  emptyList(parts.queue);
  parts.queue.push(...(queue as never[]));

  // ---- Sent downloads: the newest 60 know the processor items; the stuck ones are older and only in the archived list.
  const dispatch = (id: string, release: string, entityType: string, entityId: string, queueId: string | null, n: number, indexerName = "1337x", clientId = "c1") => ({
    id, libraryId: "l1", mediaType: entityType === "movie" ? "movies" : "tv", entityType, entityId, releaseName: release,
    indexerName, downloadClientId: clientId, downloadClientName: clientId === "c1" ? `Deluge${host}` : `qBittorrent${host}`, status: "done", notesJson: null, createdUtc: at(-(n + 1) * 3_600_000),
    decisionQuality: "Bluray 1080p", torrentHashOrItemId: queueId, importCompletedUtc: null as string | null
  });
  const recentDispatches = [
    dispatch("d-p1", queue.find((item) => item.id === "p1")!.releaseName, "movie", "m40", "p1", 1),
    dispatch("d-p2", queue.find((item) => item.id === "p2")!.releaseName, "episode", "e7", "p2", 2, "EZTV"),
    dispatch("d-p3", queue.find((item) => item.id === "p3")!.releaseName, "movie", "m41", "p3", 3),
    dispatch("d-p4", queue.find((item) => item.id === "p4")!.releaseName, "movie", "m42", "p4", 4)
  ];
  const olderDispatches = [
    dispatch("d-s1", queue[0].releaseName, "movie", "m30", "s1", 40),
    dispatch("d-s2", queue[1].releaseName, "movie", "m31", "s2", 41),
    dispatch("d-s3", queue[2].releaseName, "movie", "m32", "s3", 42, "TorrentLeech", "c3"),
    dispatch("d-s4", queue[3].releaseName, "episode", "e7", "s4", 43, "EZTV"),
    dispatch("d-s5", queue[4].releaseName, "episode", "e18", "s5", 44, "EZTV"),
    dispatch("d-s6", queue[5].releaseName, "episode", "e91", "s6", 45, "EZTV"),
    dispatch("d-s7", queue[6].releaseName, "movie", "m41", "s7", 46, "1337x", "c3"),
    dispatch("d-s8", queue[7].releaseName, "movie", "m42", "s8", 47),
    dispatch("d-s9", queue[8].releaseName, "episode", "ls0", "s9", 48, "EZTV"),
    dispatch("d-s10", queue[9].releaseName, "episode", "ls1", "s10", 49, "EZTV")
  ];
  emptyList(parts.dispatches);
  parts.dispatches.push(...(recentDispatches as never[]));
  await page.route(/\/api\/download-dispatches/, (route) => {
    const archived = new URL(route.request().url()).searchParams.get("includeArchived") === "true";
    return route.fulfill(json({ items: archived ? [...recentDispatches, ...olderDispatches] : recentDispatches, hasMore: false, nextPageToken: null }));
  });

  // ---- The processor
  const refused = (n: string) => `The processor refused the hand-off with HTTP 400: The hand-off names '${n}' which does not exist under the folder the processor watches, so it cannot be cleaned or imported.`;
  const handoff = (id: string, queueItemId: string, release: string, status: string, failure: string | null) => ({
    id, libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId, releaseName: release, sourcePath: "/d", processorName: `Processor${host}`,
    status, outputPath: null, importJobId: null, failureMessage: failure, createdUtc: at(-3_600_000), updatedUtc: at(-120_000)
  });
  emptyList(parts.handoffs);
  parts.handoffs.push(
    handoff("h1", "p1", queue.find((item) => item.id === "p1")!.releaseName, "failed", refused("The Little Shop of Horrors 1960 1080p BluRay x264-PSYCHD")) as never,
    handoff("h2", "p2", queue.find((item) => item.id === "p2")!.releaseName, "failed", refused("Say.Yes.to.the.Dress.2007.S04E07.The.Blowout.Sale.1080p")) as never,
    handoff("h3", "p3", queue.find((item) => item.id === "p3")!.releaseName, "waiting", null) as never,
    handoff("h4", "p4", queue.find((item) => item.id === "p4")!.releaseName, "started", null) as never
  );

  // ---- Telemetry, summary
  await page.route(/\/api\/download-clients\/telemetry/, (route) =>
    route.fulfill(json({
      summary: { activeCount: 0, queuedCount: 0, stalledCount: 17, importReadyCount: 0, waitingForProcessorCount: 1 },
      clients: [
        { clientId: "c1", clientName: `Deluge${host}`, queue: queue.filter((item) => item.clientId === "c1") },
        { clientId: "c3", clientName: `qBittorrent${host}`, queue: queue.filter((item) => item.clientId === "c3") }
      ],
      capturedUtc: at(-30_000)
    }))
  );
  const summary = parts.summary as Record<string, any>;
  Object.assign(summary.downloads, { active: 0, queued: 0, stalled: 17, processing: 1, importReady: 0, waitingForProcessor: 1, downloadMbps: 0, uploadMbps: 0.1 });
  Object.assign(summary.imports, { recoveryOpen: 30, failedProcessorHandoffs: 2 });
  summary.pipeline.today = { grabbed: 0, detected: 0, imported: 0, failed: 0 };
  summary.automation.lastSearchCycle = { libraryId: "l2", libraryName: "TV", mediaType: "tv", status: "completed", planned: 2, queued: 0, skipped: 0, startedUtc: at(-23 * 60_000), completedUtc: at(-22 * 60_000) };
  summary.automation.libraries = summary.automation.libraries.map((library: Record<string, unknown>) => ({ ...library, status: "active" }));

  // ---- Import cases: thirty, some for downloads that are cards.
  const cases = (media: "movie" | "series") => {
    const rows: Array<{ id: string; title: string; summary: string; dispatchId: string | null }> = [];
    if (media === "movie") {
      rows.push({ id: "r-kid", title: queue[2].releaseName, summary: "qBittorrent no longer lists this download.", dispatchId: "d-s3" });
      rows.push({ id: "r-shop", title: queue.find((item) => item.id === "p1")!.releaseName, summary: "Import failed: release refused; replacement blocked.", dispatchId: "d-p1" });
      for (let n = 0; n < 8; n += 1) rows.push({ id: `r-m${n}`, title: `Older.Film.${n}.19${70 + n}.1080p.BluRay`, summary: "Import failed: release refused; replacement blocked.", dispatchId: null });
    } else {
      for (const [id, release, dispatchId] of [["r-y1", queue[3].releaseName, "d-s4"], ["r-y2", queue[4].releaseName, "d-s5"], ["r-y3", queue[5].releaseName, "d-s6"]] as const) {
        rows.push({ id, title: release, summary: "Import failed: release refused; replacement blocked.", dispatchId });
      }
      for (let n = 0; n < 17; n += 1) rows.push({ id: `r-t${n}`, title: `Lioness.S03E${String((n % 8) + 1).padStart(2, "0")}.1080p.WEB.h264-ETHEL`, summary: "Import failed: release refused; replacement blocked.", dispatchId: null });
    }
    return rows.map((row) => ({ id: row.id, title: row.title, failureKind: "importFailed", summary: row.summary, recommendedAction: "review", detailsJson: row.dispatchId ? JSON.stringify({ DispatchId: row.dispatchId }) : null, detectedUtc: at(-3_600_000) }));
  };
  const summaryOf = (rows: ReturnType<typeof cases>) => ({ openCount: rows.length, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: rows.length, recentCases: rows });
  await page.route(/\/api\/movies\/import-recovery/, (route) => route.fulfill(json(summaryOf(cases("movie")))));
  await page.route(/\/api\/series\/import-recovery/, (route) => route.fulfill(json(summaryOf(cases("series")))));

  // ---- Speed: nothing down, a little up.
  emptyList(parts.samples);
  parts.samples.push(...Array.from({ length: 48 }, (_, n) => ({ capturedUtc: at(-(48 - n) * 7.5 * 60_000), speedMbps: n % 11 === 0 ? 2 : 0, activeCount: 0, uploadMbps: n % 9 === 0 ? 4.2 : Math.max(0, 0.4 * Math.sin(n / 3) + 0.2) })));

  // ---- Activity: mostly skipped episode searches.
  const row = (n: number, category: string, message: string, label: string, entityType: string | null, entityId: string | null, minutes: number) => ({
    id: `r${n}`, category, label, message, severity: "info", detailsJson: null, relatedJobId: null, relatedEntityType: entityType, relatedEntityId: entityId, createdUtc: at(-minutes * 60_000)
  });
  emptyList(parts.events);
  for (let n = 0; n < 18; n += 1) {
    const episode = `ls${n % 12}`;
    const info = (parts.episodes as Record<string, { season: number; episode: number }>)[episode];
    parts.events.push(row(n, "episode.search.executed", `Episode search executed: S${String(info.season).padStart(2, "0")}E${String(info.episode).padStart(2, "0")} - skipped`, "Episode search finished", "episode", episode, 22 + n * 2) as never);
  }
  for (let n = 0; n < 62; n += 1) parts.events.push(row(100 + n, n % 2 ? "job.completed" : "metadata.artwork.cleanup", "Every file Deluno holds has been read. (TV)", "Task finished", null, null, 3 + n * 4) as never);
  parts.events.sort((a, b) => Date.parse((b as { createdUtc: string }).createdUtc) - Date.parse((a as { createdUtc: string }).createdUtc));
  void numberOf;
  void poster;
}
