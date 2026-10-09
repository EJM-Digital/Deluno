import { describe, expect, it } from "vitest";
import { handoffStatusLabel } from "../../../lib/needs-action";
import { countdown, formatCountdown, updatedAgo } from "./countdown";
import { buildJourney, IMPORTING_NOW, LANDED_MS, byStage, episodeFromRelease, finishAt, laneCounts, movedCards, routeOf, shortNamer, titleFromRelease, type Stage } from "./journey";
import { buildOperations } from "./band";
import { plainReason } from "./plain";
import { faceOf, NO_TITLES, wantedTitles } from "./titles";
import { activityKind, isRoutine } from "./kinds";
import { linePath, shapeSpeedSeries } from "./series";
import { startedByFromWord, startedByOfDispatch } from "./provenance";
import { fullSummary } from "../downloads-fixtures";

const NOW = Date.parse("2026-10-01T10:00:00Z");
const iso = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();

describe("countdown maths", () => {
  it("writes minutes and seconds under an hour, and the next search as 12:04", () => {
    expect(formatCountdown(12 * 60_000 + 4_000)).toBe("12:04");
    expect(formatCountdown(59_001)).toBe("1:00");
    expect(formatCountdown(3_600_000 + 5 * 60_000 + 30_000)).toBe("1:05:30");
    expect(formatCountdown(2 * 86_400_000 + 3 * 3_600_000)).toBe("2 days 3 h");
    expect(formatCountdown(0)).toBe("now");
  });

  it("says how far through the gap between two runs it is, for the ring-3", () => {
    const halfway = countdown(iso(30 * 60_000), iso(-30 * 60_000), NOW);
    expect(halfway).toMatchObject({ text: "30:00", due: false });
    expect(halfway?.fraction).toBeCloseTo(0.5, 5);
    expect(countdown(iso(60_000), null, NOW)?.fraction).toBeNull();
  });

  it("is due, never negative, and empty when nothing is scheduled", () => {
    expect(countdown(iso(-5_000), iso(-60_000), NOW)).toMatchObject({ due: true, remainingMs: 0, text: "now", fraction: 1 });
    expect(countdown(null, iso(-1), NOW)).toBeNull();
    expect(countdown("not a date", null, NOW)).toBeNull();
  });

  it("says when it was last updated, ticking up by the second", () => {
    expect(updatedAgo(NOW - 2_000, NOW)).toBe("updated just now");
    expect(updatedAgo(NOW - 47_000, NOW)).toBe("updated 47 s ago");
    expect(updatedAgo(NOW - 5 * 60_000, NOW)).toBe("updated 5 min ago");
    expect(updatedAgo(NOW + 5_000, NOW)).toBe("updated just now");
  });
});

describe("the speed chart's data", () => {
  const sample = (minutesAgo: number, down: number, up = 0) => ({ capturedUtc: iso(-minutesAgo * 60_000), speedMbps: down, activeCount: 1, uploadMbps: up });

  it("keeps the window, oldest first, and holds the last reading to now", () => {
    const series = shapeSpeedSeries([sample(10, 4), sample(400, 9), sample(30, 2, 1)], null, NOW, 6);
    expect(series.points.map((point) => point.down)).toEqual([2, 4, 4]);
    expect(series.points[series.points.length - 1].t).toBe(NOW);
    expect(series.peakDown).toBe(4);
    expect(series.peakUp).toBe(1);
    expect(series.averageDown).toBe(3);
  });

  it("extends with a newer live reading and ignores an older one", () => {
    const stored = [sample(10, 4)];
    const extended = shapeSpeedSeries(stored, { atMs: NOW - 60_000, down: 7.5, up: 0.5 }, NOW, 6);
    expect(extended.points.map((point) => point.down)).toEqual([4, 7.5, 7.5]);
    const older = shapeSpeedSeries(stored, { atMs: NOW - 20 * 60_000, down: 99, up: 0 }, NOW, 6);
    expect(older.points.map((point) => point.down)).toEqual([4, 4]);
  });

  it("always has a reading: no samples is a flat 0, and bad numbers read 0", () => {
    const empty = shapeSpeedSeries([], null, NOW, 6);
    expect(empty.points).toEqual([{ t: NOW - 6 * 3_600_000, down: 0, up: 0 }, { t: NOW, down: 0, up: 0 }]);
    const bad = shapeSpeedSeries([{ capturedUtc: iso(-1000), speedMbps: Number.NaN, activeCount: 0, uploadMbps: -3 }], null, NOW, 6);
    expect(bad.points[0]).toMatchObject({ down: 0, up: 0 });
  });

  it("draws a path inside its box, with a zero line at the bottom", () => {
    const series = shapeSpeedSeries([sample(180, 10), sample(0, 0)], null, NOW, 6);
    const path = linePath(series.points, "down", series.start, series.end, series.peakDown, 100, 50);
    expect(path.startsWith("M50.0 0.0")).toBe(true);
    expect(path).toContain("L100.0 50.0");
    expect(linePath(shapeSpeedSeries([], null, NOW, 6).points, "down", NOW - 6 * 3_600_000, NOW, 0, 100, 50)).toBe("M0.0 50.0 L100.0 50.0");
  });
});

describe("what activity is", () => {
  it("names the kind from the category, the way the server counts them", () => {
    expect(activityKind("movies.search.completed")).toBe("search");
    expect(activityKind("series.release.force-grabbed")).toBe("grab");
    expect(activityKind("filesystem.import.season-pack.completed")).toBe("import");
    expect(activityKind("download.orphan.removed")).toBe("cleanup");
    expect(activityKind("retention.removed")).toBe("retention");
    expect(activityKind("subtitle.downloaded")).toBe("subtitle");
    expect(activityKind("intake.list.synced")).toBe("lists");
    expect(activityKind("whatever.happened", "error")).toBe("failed");
    expect(activityKind("something.else")).toBe("other");
    expect(activityKind("processing.completed")).toBe("process");
    expect(activityKind("processing.timed-out")).toBe("failed");
    for (const routine of ["job.queued", "job.started", "job.completed", "metadata.artwork.cleanup", "library.title.rescanned", "external.file-changed"]) expect(isRoutine(activityKind(routine))).toBe(true);
    expect(isRoutine(activityKind("movies.release.grabbed"))).toBe(false);
  });
});

describe("the journey of a title", () => {
  const queue = (overrides: Record<string, unknown>) => ({
    id: "q", clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: "Sintel (2010)", releaseName: "Sintel.2010.1080p", category: "", status: "downloading",
    progress: 40, speedMbps: 2, etaSeconds: 600, sizeBytes: 2e9, downloadedBytes: 8e8, peers: 3, indexerName: "NZBgeek", errorMessage: null, addedUtc: iso(-600_000), sourcePath: null, libraryId: null, healthFindings: null, ...overrides
  }) as never;
  const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };

  it("puts each download in the lane its state says, with its details", () => {
    const cards = buildJourney({
      ...base,
      queue: [queue({ id: "a", status: "queued" }), queue({ id: "b", releaseName: "B.2011" }), queue({ id: "c", releaseName: "C.2012", status: "importReady" }), queue({ id: "d", releaseName: "D.2013", status: "waitingForProcessor" }), queue({ id: "e", releaseName: "E.2014", status: "imported" })]
    });
    const grouped = byStage(cards);
    expect(grouped.grabbed.map((card) => card.key)).toEqual(["c1:a"]);
    expect(grouped.downloading[0]).toMatchObject({ client: "Deluge", indexer: "NZBgeek", progress: 40, state: { label: "Downloading", tone: "info" } });
    expect(grouped.importing.map((card) => card.key)).toEqual(["c1:c"]);
    expect(grouped.processor[0].state.label).toBe("Being handed over");
    expect(cards.some((card) => card.key === "c1:e")).toBe(false);
  });

  it("follows a pushed reading only when it is newer than the stored queue", () => {
    const live = new Map([["b", { progress: 77, speedMbps: 5, status: "downloading", at: NOW }]]);
    const fresh = buildJourney({ ...base, queue: [queue({ id: "b" })], live, queueCapturedUtc: iso(-5000) });
    expect(byStage(fresh).downloading[0]).toMatchObject({ progress: 77, speedMbps: 5 });
    const stale = buildJourney({ ...base, queue: [queue({ id: "b" })], live, queueCapturedUtc: iso(5000) });
    expect(byStage(stale).downloading[0]).toMatchObject({ progress: 40 });
  });

  it("shows the processor's own state and name, including a failure", () => {
    const handoff = (overrides: Record<string, unknown>) => ({ id: "h", libraryId: "l", mediaType: "movie", clientId: "c1", queueItemId: "q", releaseName: "Metropolis.1927", sourcePath: "", processorName: "Cleaner", status: "started", outputPath: null, importJobId: null, failureMessage: null, createdUtc: iso(-3_600_000), updatedUtc: iso(-60_000), ...overrides }) as never;
    const working = byStage(buildJourney({ ...base, handoffs: [handoff({})] })).processor;
    expect(working[0]).toMatchObject({ processor: "Cleaner", state: { label: "Being cleaned", tone: "info" }, title: "Metropolis (1927)" });
    const failed = byStage(buildJourney({ ...base, handoffs: [handoff({ status: "failed", failureMessage: "timed out" })] })).processor;
    expect(failed[0].state).toEqual({ tone: "bad", label: "Processing failed" });
    // The same words Transfers uses (#1186): a download that was gone is not a processor failure.
    const gone = byStage(buildJourney({ ...base, handoffs: [handoff({ status: "source-gone", failureMessage: "The download was gone before Cleaner could clean it." })] })).processor;
    expect(gone[0].state).toEqual({ tone: "bad", label: handoffStatusLabel("source-gone") });
    expect(gone[0].state.label).toBe("Download gone");
    expect(failed[0].reason).toBe("The processor took too long and Deluno stopped waiting.");
    const returned = byStage(buildJourney({ ...base, handoffs: [handoff({ status: "completed" })] }));
    expect(returned.importing[0].state.label).toBe("Back from the processor");
  });

  it("lets go of a hand-off once its import ended, imported or refused (golden path rc.30.8)", () => {
    const handoff = (overrides: Record<string, unknown>) => ({ id: "h", libraryId: "l", mediaType: "movie", clientId: "c1", queueItemId: "q", releaseName: "Retry.Film.2017.1080p", sourcePath: "", processorName: "Cleaner", status: "completed", outputPath: null, importJobId: null, failureMessage: null, createdUtc: iso(-3_600_000), updatedUtc: iso(-60_000), ...overrides }) as never;
    const dispatch = (overrides: Record<string, unknown>) => ({ id: "d", libraryId: "l", mediaType: "movies", entityType: "movie", entityId: "m", releaseName: "Retry Film (2017) 1080p", indexerName: "I", downloadClientId: "c1", downloadClientName: "Deluge", status: "sent", notesJson: null, createdUtc: iso(-3_600_000), ...overrides }) as never;
    // Still waiting: the lane shows it.
    expect(byStage(buildJourney({ ...base, handoffs: [handoff({})] })).importing).toHaveLength(1);
    // The processor's own name for the release differs from the send's, so only the outcome and the queue say it is over.
    expect(byStage(buildJourney({ ...base, handoffs: [handoff({ outcome: { outcome: "imported" } })] })).importing).toHaveLength(0);
    expect(byStage(buildJourney({ ...base, handoffs: [handoff({ outcome: { outcome: "not-imported" } })] })).importing).toHaveLength(0);
    expect(byStage(buildJourney({ ...base, queue: [queue({ id: "q", status: "imported", releaseName: "Retry.Film.2017.1080p" })], handoffs: [handoff({})] })).importing).toHaveLength(0);
    // The send says its import failed or finished.
    expect(byStage(buildJourney({ ...base, dispatches: [dispatch({ releaseName: "Retry.Film.2017.1080p", importStatus: "failed" })], handoffs: [handoff({})] })).importing).toHaveLength(0);
    expect(byStage(buildJourney({ ...base, dispatches: [dispatch({ releaseName: "Retry.Film.2017.1080p", importStatus: "imported" })], handoffs: [handoff({})] })).importing).toHaveLength(0);
  });

  it("lists what was imported today, newest first, and the last search", () => {
    const dispatch = (id: string, hoursAgo: number) => ({ id, libraryId: "l", mediaType: "movies", entityType: "movie", entityId: `m-${id}`, releaseName: `Film.${2000 + Number(id)}.1080p`, indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "Deluge", status: "done", notesJson: null, createdUtc: iso(-hoursAgo * 3_600_000 - 600_000), importCompletedUtc: iso(-hoursAgo * 3_600_000), decisionQuality: "Bluray 1080p" }) as never;
    const grouped = byStage(buildJourney({ ...base, dispatches: [dispatch("1", 3), dispatch("2", 1)], now: NOW }));
    expect(grouped.imported.map((card) => card.title)).toEqual(["Film (2002)", "Film (2001)"]);
    expect(grouped.imported[0]).toMatchObject({ to: "/movies/m-2", quality: "Bluray 1080p", state: { tone: "ok" } });
  });

  it("says each lane's count from the larger of the lists and the summary", () => {
    const grouped = byStage(buildJourney({ ...base, queue: [queue({ id: "a", status: "queued" })] }));
    const counts = laneCounts(grouped, fullSummary);
    expect(counts.grabbed).toBe(1);
    expect(counts.downloading).toBe(fullSummary.downloads!.active);
    expect(counts.imported).toBe(4);
  });

  it("finds the cards that changed stage since the last look", () => {
    const before = new Map<string, Stage>([["c1:b", "downloading"], ["c1:x", "grabbed"]]);
    const cards = buildJourney({ ...base, queue: [queue({ id: "b", status: "importReady" }), queue({ id: "x", status: "queued" })] });
    expect([...movedCards(before, cards)]).toEqual(["c1:b"]);
    expect(movedCards(new Map(), cards).size).toBe(0);
  });

  it("reads a release name as a title", () => {
    expect(titleFromRelease("Sintel.2010.1080p.BluRay.x264")).toBe("Sintel (2010)");
    expect(titleFromRelease("The.Kid.1921.REMASTERED")).toBe("The Kid (1921)");
    expect(titleFromRelease("Some Show S01E02")).toBe("Some Show S01E02");
    // A site's banner in front of the name is not part of the title.
    expect(titleFromRelease("www Torrenting com The Wild Robot 2024 1080p")).toBe("The Wild Robot (2024)");
    expect(titleFromRelease("www.UIndex.org - Say.Yes.to.the.Dress.2007.S04E02")).toBe("Say Yes to the Dress (2007) S04E02");
    // A show episode reads as the show and the code; what follows the code is the release itself.
    expect(titleFromRelease("Lioness.S03E08.1080p.WEB.h264-ETHEL")).toBe("Lioness S03E08");
  });

  it("reads the episode or season of a release", () => {
    expect(episodeFromRelease("Say.Yes.to.the.Dress.2007.S04E02.720p")).toBe("S04E02");
    expect(episodeFromRelease("Some.Show.S02.COMPLETE")).toBe("Season 2");
    expect(episodeFromRelease("A.Film.2001.1080p")).toBeNull();
  });
});

describe("stuck downloads", () => {
  const queue = (overrides: Record<string, unknown>) => ({
    id: "q", clientId: "c1", clientName: "Deluge on WIN-7K3", protocol: "torrent", mediaType: "movie", title: "x", releaseName: "Sintel.2010.1080p", category: "", status: "downloading",
    progress: 40, speedMbps: 0, etaSeconds: 0, sizeBytes: 2e9, downloadedBytes: 8e8, peers: 0, indexerName: "Deluge on WIN-7K3", errorMessage: null, addedUtc: iso(-600_000), sourcePath: null, libraryId: null, healthFindings: null, ...overrides
  }) as never;
  const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };
  const dispatch = { id: "d1", libraryId: "l", mediaType: "movies", entityType: "movie", entityId: "m1", releaseName: "Sintel.2010.1080p", indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "Deluge on WIN-7K3", status: "done", notesJson: null, createdUtc: iso(-3_600_000), decisionQuality: "WEB 1080p", torrentHashOrItemId: "q" } as never;

  it("keeps a stopped download where it is, amber and stuck, not as a problem card", () => {
    const cards = buildJourney({
      ...base,
      queue: [
        queue({ id: "a", status: "stalled", releaseName: "A.2001", progress: 43 }),
        queue({ id: "b", errorMessage: "missing or invalid torrent data", releaseName: "B.2002", progress: 20 }),
        queue({ id: "c", status: "downloading", progress: 40, speedMbps: 3, releaseName: "C.2003" }),
        queue({ id: "d", status: "queued", errorMessage: "Not found", releaseName: "D.2004" })
      ]
    });
    const grouped = byStage(cards);
    // The moving one leads its station; the stuck ones follow and carry the client's own words only as a tooltip.
    expect(grouped.downloading.map((card) => card.key)).toEqual(["c1:c", "c1:a", "c1:b"]);
    expect(grouped.downloading[0].stuck).toBe(false);
    expect(grouped.downloading[1]).toMatchObject({ stuck: true, state: { tone: "warn", label: "Stuck" }, reason: "It stopped moving in the download client." });
    expect(grouped.downloading[2]).toMatchObject({ stuck: true, reason: "Torrent data missing or broken.", reasonFull: "missing or invalid torrent data" });
    // A stuck download that was never started stays in Grabbed; another copy grabbed later is simply a new card there.
    expect(grouped.grabbed.map((card) => card.key)).toEqual(["c1:d"]);
    expect(grouped.grabbed[0].stuck).toBe(true);
    expect(laneCounts(grouped, undefined).downloading).toBe(3);
  });

  it("drops a download from the journey once its import has landed, even while the client keeps seeding it", () => {
    const cards = buildJourney({ ...base, queue: [queue({ status: "completed", progress: 100, downloadedBytes: 2e9 })], dispatches: [{ ...(dispatch as object), importCompletedUtc: iso(-(LANDED_MS + 1_000)) } as never] });
    expect(cards.filter((card) => card.stage !== "imported")).toEqual([]);
  });

  it("holds a title whose import has just finished at Importing, full and green, for a moment before it leaves", () => {
    const [card] = buildJourney({ ...base, queue: [queue({ status: "completed", progress: 100, downloadedBytes: 2e9 })], dispatches: [{ ...(dispatch as object), importCompletedUtc: iso(-1_000) } as never] }).filter((item) => item.stage !== "imported");
    expect(card).toMatchObject({ stage: "importing", landed: true, progress: 100, state: { tone: "ok", label: "Imported" } });
  });

  it("never calls a finished download stuck: one the client reports stalled at 100% is downloaded", () => {
    const [card] = buildJourney({ ...base, queue: [queue({ status: "stalled", progress: 100, downloadedBytes: 2e9 })], dispatches: [dispatch] });
    expect(card).toMatchObject({ stage: "importing", stuck: false, state: { tone: "idle", label: "Downloaded" } });
  });

  it("a download whose import is under way says Importing now, and one still waiting says Downloaded, so the band and the journey agree", () => {
    const finished = queue({ status: "stalled", progress: 100, downloadedBytes: 2e9 });
    const waiting = buildJourney({ ...base, queue: [finished], dispatches: [dispatch] })[0];
    const running = buildJourney({ ...base, queue: [finished], dispatches: [dispatch], work: [{ key: "import:d1", kind: "import", text: "Importing x", release: "Sintel.2010.1080p", to: "/queue" }] })[0];
    expect(waiting.state.label).toBe("Downloaded");
    expect(running).toMatchObject({ stage: "importing", state: { tone: "info", label: IMPORTING_NOW } });
    expect(buildOperations([], [waiting], NO_TITLES).map((op) => op.kind)).not.toContain("import");
    expect(buildOperations([], [running], NO_TITLES).map((op) => op.kind)).toContain("import");
  });

  it("finds the library title from the sent download, and keeps the release as the quiet line", () => {
    const [card] = buildJourney({ ...base, queue: [queue({ status: "downloading", speedMbps: 2 })], dispatches: [dispatch] });
    expect(card.entity).toEqual({ kind: "movie", id: "m1" });
    expect(card.release).toBe("Sintel.2010.1080p");
    const lookup = { movies: new Map([["m1", { title: "Sintel", year: 2010, posterUrl: "/poster.jpg" }]]), series: new Map(), episodes: new Map() };
    expect(faceOf(card, lookup)).toMatchObject({ title: "Sintel (2010)", poster: "/poster.jpg", linked: true, to: "/movies/m1" });
    // A title that is gone keeps the release's own name.
    expect(faceOf(card, NO_TITLES)).toMatchObject({ title: "Sintel (2010)", linked: false, to: "/movies/m1" });
    expect(wantedTitles([card])).toEqual({ movies: ["m1"], series: [], episodes: [] });
  });

  it("names an episode as its show plus SxxEyy, and a season pack from its release", () => {
    const episode = { ...(dispatch as object), entityType: "episode", entityId: "e9", releaseName: "Show.S04E02.720p" } as never;
    const pack = { ...(dispatch as object), id: "d2", entityType: "series", entityId: "s1", releaseName: "Show.S04.COMPLETE", torrentHashOrItemId: "p" } as never;
    const cards = buildJourney({ ...base, queue: [queue({ id: "q", releaseName: "Show.S04E02.720p" }), queue({ id: "p", releaseName: "Show.S04.COMPLETE" })], dispatches: [episode, pack] });
    const lookup = { movies: new Map(), series: new Map([["s1", { title: "Show", posterUrl: null }]]), episodes: new Map([["e9", { seriesId: "s1", season: 4, episode: 2 }]]) };
    expect(faceOf(cards[0], lookup)).toMatchObject({ title: "Show S04E02", to: "/tv/s1" });
    expect(faceOf(cards[1], lookup)).toMatchObject({ title: "Show Season 4", to: "/tv/s1" });
  });

  it("writes the route as indexer, client, processor with short names and no stop twice", () => {
    const [card] = buildJourney({ ...base, queue: [queue({ status: "waitingForProcessor" })], dispatches: [dispatch], handoffs: [{ id: "h", queueItemId: "q", releaseName: "Sintel.2010.1080p", processorName: "Cleaner on WIN-7K3", status: "started", libraryId: "l", mediaType: "movie", clientId: "c1", sourcePath: "", outputPath: null, importJobId: null, failureMessage: null, createdUtc: iso(-1), updatedUtc: iso(-1) } as never] });
    expect(routeOf(card)).toBe("NZBgeek → Deluge → Cleaner");
    expect(routeOf({ indexer: "Deluge", client: "Deluge", processor: null })).toBe("Deluge");
    // With two machines the suffix is kept, so they can be told apart.
    const name = shortNamer(["Deluge on WIN-A", "Deluge on NAS"]);
    expect(name("Deluge on WIN-A")).toBe("Deluge on WIN-A");
    expect(shortNamer(["Deluge on WIN-A"])("Deluge on WIN-A")).toBe("Deluge");
  });

  it("keeps a refused hand-off with the processor, first in its station", () => {
    const handoff = (id: string, status: string) => ({ id, queueItemId: "gone", releaseName: `Film.${id}.2000`, processorName: "Cleaner", status, libraryId: "l", mediaType: "movie", clientId: "c1", sourcePath: "", outputPath: null, importJobId: null, failureMessage: status === "failed" ? "timed out" : null, createdUtc: iso(-1), updatedUtc: iso(-60_000) }) as never;
    const grouped = byStage(buildJourney({ ...base, handoffs: [handoff("ok", "started"), handoff("bad", "failed")] }));
    expect(grouped.processor.map((card) => card.key)).toEqual(["handoff:bad", "handoff:ok"]);
    expect(grouped.processor[0]).toMatchObject({ state: { tone: "bad" }, reason: "The processor took too long and Deluno stopped waiting.", reasonFull: "timed out" });
  });
});

describe("when a download should be done", () => {
  const item = { etaSeconds: 600, sizeBytes: 2 * 1024 * 1024 * 1024 };
  it("counts the client's own time-left from the moment it was read", () => {
    expect(finishAt(item, NOW - 60_000, undefined, NOW)).toBe(NOW - 60_000 + 600_000);
  });
  it("works it out again from a pushed reading that is newer: what is left over how fast it goes", () => {
    // Half of 2 GiB left at 2 MiB/s is 512 seconds.
    expect(finishAt(item, NOW - 60_000, { progress: 50, speedMbps: 2, at: NOW }, NOW)).toBe(NOW + 512_000);
    // An older pushed reading is not trusted over the stored one.
    expect(finishAt(item, NOW, { progress: 50, speedMbps: 2, at: NOW - 5_000 }, NOW)).toBe(NOW + 600_000);
  });
  it("has nothing to say when the client gives no reading", () => {
    expect(finishAt({ ...item, etaSeconds: 0 }, NOW, undefined, NOW)).toBeUndefined();
    expect(finishAt({ ...item, etaSeconds: Number.NaN }, NOW, undefined, NOW)).toBeUndefined();
  });
  it("is on a downloading card only while it is really moving", () => {
    const row = (overrides: Record<string, unknown>) => ({ id: "q", clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: "x", releaseName: "Sintel.2010.1080p", category: "", status: "downloading", progress: 40, speedMbps: 2, etaSeconds: 600, sizeBytes: 2e9, downloadedBytes: 8e8, peers: 3, indexerName: "NZBgeek", errorMessage: null, addedUtc: iso(-600_000), sourcePath: null, libraryId: null, healthFindings: null, ...overrides }) as never;
    const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW, queueCapturedUtc: iso(0) };
    expect(buildJourney({ ...base, queue: [row({})] })[0].finishAtMs).toBe(NOW + 600_000);
    expect(buildJourney({ ...base, queue: [row({ status: "stalled" })] })[0].finishAtMs).toBeUndefined();
    expect(buildJourney({ ...base, queue: [row({ etaSeconds: 0 })] })[0].finishAtMs).toBeUndefined();
  });
});

describe("why a title is moving", () => {
  it("reads the words the server and the grab path use", () => {
    expect(startedByFromWord("manual")).toBe("you");
    expect(startedByFromWord("movie-added")).toBe("you");
    expect(startedByFromWord("Overseerr")).toBe("seerr");
    expect(startedByFromWord("trakt")).toBe("list");
    expect(startedByFromWord("upgrade")).toBe("upgrade");
    expect(startedByFromWord("missing")).toBe("missing");
    expect(startedByFromWord("schedule")).toBeUndefined();
    expect(startedByFromWord(null)).toBeUndefined();
  });

  it("tells it only from what was recorded, and shows no tag when nothing says", () => {
    const dispatch = (overrides: Record<string, unknown>) => ({ indexerName: "NZBgeek", notesJson: null, ...overrides }) as never;
    expect(startedByOfDispatch(dispatch({ startedBy: "upgrade" }))).toBe("upgrade");
    expect(startedByOfDispatch(dispatch({ indexerName: "Manual selection" }))).toBe("you");
    expect(startedByOfDispatch(dispatch({ notesJson: JSON.stringify({ chosenOnSearch: true }) }))).toBe("you");
    expect(startedByOfDispatch(dispatch({ notesJson: JSON.stringify({ triggeredBy: "manual-season" }) }))).toBe("you");
    // An automatic search records its plan, not a reason: no tag rather than a guess.
    expect(startedByOfDispatch(dispatch({ notesJson: JSON.stringify({ searchPlan: { reason: "ok" } }) }))).toBeUndefined();
    expect(startedByOfDispatch(dispatch({ notesJson: "not json" }))).toBeUndefined();
    expect(startedByOfDispatch(undefined)).toBeUndefined();
  });
});

describe("plain words for a failure", () => {
  it("says what a refused hand-off, a timeout, a broken torrent and a vanished download mean, and keeps the original", () => {
    expect(plainReason("The processor refused the hand-off with HTTP 400: The hand-off names 'X' which does not exist.")).toEqual({ text: "The processor refused the hand-off.", full: "The processor refused the hand-off with HTTP 400: The hand-off names 'X' which does not exist." });
    expect(plainReason("The processor refused the hand-off with HTTP 401")?.text).toMatch(/refused Deluno's key/);
    expect(plainReason("Missing or invalid torrent data!")?.text).toBe("Torrent data missing or broken.");
    expect(plainReason("qBittorrent no longer lists this download.")?.text).toBe("The download client no longer has it.");
    expect(plainReason("Something nobody wrote a rule for")?.text).toBe("Something nobody wrote a rule for");
    expect(plainReason("  ")).toBeNull();
  });
});
