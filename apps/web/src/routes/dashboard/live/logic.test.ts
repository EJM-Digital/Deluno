import { describe, expect, it } from "vitest";
import { countdown, formatCountdown, updatedAgo } from "./countdown";
import { buildJourney, byStage, episodeFromRelease, laneCounts, movedCards, routeOf, shortNamer, titleFromRelease, type Stage } from "./journey";
import { plainReason } from "./plain";
import { faceOf, NO_TITLES, wantedTitles } from "./titles";
import { activityKind, isRoutine } from "./kinds";
import { addBuckets, bucketByHour, bucketsFromCounts, linePath, shapeSpeedSeries } from "./series";
import { heartbeatState } from "./status";
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

  it("says how far through the gap between two runs it is, for the ring", () => {
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

describe("the heartbeat", () => {
  it("only beats while connected and says so in words when it is not", () => {
    expect(heartbeatState("connected")).toMatchObject({ label: "Live", beating: true, tone: "ok" });
    expect(heartbeatState("reconnecting")).toMatchObject({ label: "Reconnecting", beating: false, tone: "warn" });
    expect(heartbeatState("disconnected")).toMatchObject({ label: "Offline", beating: false, tone: "bad" });
    expect(heartbeatState("connecting").beating).toBe(false);
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
    expect(empty.points).toEqual([{ t: NOW, down: 0, up: 0 }]);
    const bad = shapeSpeedSeries([{ capturedUtc: iso(-1000), speedMbps: Number.NaN, activeCount: 0, uploadMbps: -3 }], null, NOW, 6);
    expect(bad.points[0]).toMatchObject({ down: 0, up: 0 });
  });

  it("draws a path inside its box, with a zero line at the bottom", () => {
    const series = shapeSpeedSeries([sample(180, 10), sample(0, 0)], null, NOW, 6);
    const path = linePath(series.points, "down", series.start, series.end, series.peakDown, 100, 50);
    expect(path.startsWith("M50.0 0.0")).toBe(true);
    expect(path).toContain("L100.0 50.0");
    expect(linePath(shapeSpeedSeries([], null, NOW, 6).points, "down", NOW - 6 * 3_600_000, NOW, 0, 100, 50)).toBe("M100.0 50.0");
  });
});

describe("activity per hour today", () => {
  it("counts each event once, in its own hour, by kind, and leaves out other days", () => {
    const at = (hour: number) => new Date(new Date(NOW).setHours(hour, 5, 0, 0)).toISOString();
    const yesterday = new Date(NOW - 86_400_000 * 2).toISOString();
    const buckets = bucketByHour(
      [
        { id: "1", category: "wanted.search.completed", createdUtc: at(3) },
        { id: "1", category: "wanted.search.completed", createdUtc: at(3) },
        { id: "2", category: "movies.release.grabbed", createdUtc: at(3) },
        { id: "3", category: "filesystem.import.completed", createdUtc: at(9) },
        { id: "4", category: "job.failed", severity: "error", createdUtc: at(9) },
        { id: "5", category: "filesystem.import.completed", createdUtc: yesterday }
      ],
      NOW
    );
    expect(buckets).toHaveLength(24);
    expect(buckets[3].counts).toEqual({ search: 1, grab: 1 });
    expect(buckets[9].counts).toEqual({ import: 1, failed: 1 });
    expect(buckets.reduce((sum, bucket) => sum + bucket.total, 0)).toBe(4);
  });

  it("builds the day's bars from the server's real counts, and adds events that arrived since", () => {
    const base = bucketsFromCounts([
      { hour: 2, category: "wanted.search.completed", count: 120 },
      { hour: 2, category: "movies.release.grabbed", count: 30 },
      { hour: 9, category: "filesystem.import.completed", count: 7 },
      { hour: 30, category: "wanted.search.completed", count: 5 },
      { hour: 4, category: "wanted.search.completed", count: 0 }
    ]);
    expect(base[2].counts).toEqual({ search: 120, grab: 30 });
    expect(base.reduce((sum, bucket) => sum + bucket.total, 0)).toBe(157);
    const at = new Date(new Date(NOW).setHours(9, 30, 0, 0)).toISOString();
    const more = addBuckets(base, bucketByHour([{ id: "x", category: "filesystem.import.completed", createdUtc: at }], NOW));
    expect(more[9].counts).toEqual({ import: 8 });
    expect(more[9].total).toBe(8);
  });

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
    expect(grouped.processor[0].state.label).toBe("Waiting for the processor");
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
    const failed = byStage(buildJourney({ ...base, handoffs: [handoff({ status: "failed", failureMessage: "timed out" })] })).needs;
    expect(failed[0].state).toEqual({ tone: "bad", label: "Processor failed" });
    const returned = byStage(buildJourney({ ...base, handoffs: [handoff({ status: "completed" })] }));
    expect(returned.importing[0].state.label).toBe("Back from the processor");
  });

  it("lists what was imported today, newest first, and the last search", () => {
    const dispatch = (id: string, hoursAgo: number) => ({ id, libraryId: "l", mediaType: "movies", entityType: "movie", entityId: `m-${id}`, releaseName: `Film.${2000 + Number(id)}.1080p`, indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "Deluge", status: "done", notesJson: null, createdUtc: iso(-hoursAgo * 3_600_000 - 600_000), importCompletedUtc: iso(-hoursAgo * 3_600_000), decisionQuality: "Bluray 1080p" }) as never;
    const grouped = byStage(buildJourney({ ...base, dispatches: [dispatch("1", 3), dispatch("2", 1)], now: NOW }));
    expect(grouped.imported.map((card) => card.title)).toEqual(["Film (2002)", "Film (2001)"]);
    expect(grouped.imported[0]).toMatchObject({ to: "/movies/m-2", quality: "Bluray 1080p", state: { tone: "ok" } });
    expect(grouped.searching).toHaveLength(0);
  });

  it("says each lane's count from the larger of the lists and the summary", () => {
    const grouped = byStage(buildJourney({ ...base, queue: [queue({ id: "a", status: "queued" })] }));
    const counts = laneCounts(grouped, fullSummary);
    expect(counts.grabbed).toBe(1);
    expect(counts.downloading).toBe(fullSummary.downloads!.active);
    expect(counts.needs).toBe(fullSummary.downloads!.stalled);
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
    progress: 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 2e9, downloadedBytes: 2e9, peers: 0, indexerName: "Deluge on WIN-7K3", errorMessage: null, addedUtc: iso(-600_000), sourcePath: null, libraryId: null, healthFindings: null, ...overrides
  }) as never;
  const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };
  const dispatch = { id: "d1", libraryId: "l", mediaType: "movies", entityType: "movie", entityId: "m1", releaseName: "Sintel.2010.1080p", indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "Deluge on WIN-7K3", status: "done", notesJson: null, createdUtc: iso(-3_600_000), decisionQuality: "WEB 1080p", torrentHashOrItemId: "q" } as never;

  it("puts a stalled download, or one its client has a problem with, in Needs you and not in Downloading", () => {
    const cards = buildJourney({
      ...base,
      queue: [
        queue({ id: "a", status: "stalled", releaseName: "A.2001" }),
        queue({ id: "b", errorMessage: "missing or invalid torrent data", releaseName: "B.2002" }),
        queue({ id: "c", status: "downloading", progress: 40, speedMbps: 3, releaseName: "C.2003" })
      ]
    });
    const grouped = byStage(cards);
    expect(grouped.needs.map((card) => card.key)).toEqual(["c1:a", "c1:b"]);
    expect(grouped.needs[0]).toMatchObject({ state: { tone: "bad", label: "Stuck" }, canSearchAgain: true, reason: "It stopped moving in the download client." });
    // The clients own words become a plain sentence, and stay as the tooltip.
    expect(grouped.needs[1]).toMatchObject({ reason: "Torrent data missing or broken.", reasonFull: "missing or invalid torrent data" });
    expect(grouped.downloading.map((card) => card.key)).toEqual(["c1:c"]);
    const counts = laneCounts(grouped, undefined);
    expect(counts.needs).toBe(2);
    expect(counts.downloading).toBe(1);
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

  it("puts a failed hand-off's Retry and Give up on its card, with problems first in the lane", () => {
    const handoff = (id: string, status: string) => ({ id, queueItemId: "gone", releaseName: `Film.${id}.2000`, processorName: "Cleaner", status, libraryId: "l", mediaType: "movie", clientId: "c1", sourcePath: "", outputPath: null, importJobId: null, failureMessage: status === "failed" ? "timed out" : null, createdUtc: iso(-1), updatedUtc: iso(-60_000) }) as never;
    const grouped = byStage(buildJourney({ ...base, handoffs: [handoff("ok", "started"), handoff("bad", "failed")] }));
    // A hand-off the processor refused is for a person to settle: it is in the first lane, and the healthy one stays with the processor.
    expect(grouped.needs[0]).toMatchObject({ failedHandoffId: "bad", state: { tone: "bad" }, reason: "The processor took too long and Deluno stopped waiting.", reasonFull: "timed out" });
    expect(grouped.processor).toHaveLength(1);
    expect(grouped.processor[0].failedHandoffId).toBeUndefined();
  });
});

describe("what needs a person", () => {
  const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };
  const entry = (id: string, title: string, detailsJson: string | null = null) => ({ id, title, failureKind: "importFailed", summary: "Import failed: release refused; replacement blocked.", recommendedAction: "", detailsJson, detectedUtc: iso(-3_600_000), mediaType: "movie" as const });

  it("makes a card of an import case, named from its sent download, and one of a failed import job", () => {
    const dispatch = { id: "d1", libraryId: "l", mediaType: "movies", entityType: "movie", entityId: "m1", releaseName: "Sintel.2010.1080p", indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "Deluge", status: "done", notesJson: null, createdUtc: iso(-3_600_000), decisionQuality: "WEB 1080p" } as never;
    const grouped = byStage(buildJourney({ ...base, dispatches: [dispatch], cases: [entry("r1", "Sintel.2010.1080p", JSON.stringify({ DispatchId: "d1" }))], importJobs: [{ id: "j1", lastError: "Disk was full", createdUtc: iso(-1000), completedUtc: null } as never] }));
    expect(grouped.needs.map((card) => card.key).sort()).toEqual(["case:movie:r1", "job:j1"]);
    const card = grouped.needs.find((item) => item.key === "case:movie:r1")!;
    expect(card).toMatchObject({ entity: { kind: "movie", id: "m1" }, indexer: "NZBgeek", dispatchId: "d1", state: { tone: "warn", label: "Import needs a look" } });
    expect(card.recovery?.id).toBe("r1");
    expect(grouped.needs.find((item) => item.key === "job:j1")).toMatchObject({ importJobId: "j1", title: "An import did not finish", reason: "Disk was full" });
  });

  it("does not make a second card for an import case whose download is already a card", () => {
    const stuck = { id: "q", clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: "x", releaseName: "The.Kid.1921.1080p", category: "", status: "stalled", progress: 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 1, downloadedBytes: 1, peers: 0, indexerName: "Deluge", errorMessage: null, addedUtc: iso(-600_000), sourcePath: null, libraryId: null, healthFindings: null } as never;
    const grouped = byStage(buildJourney({ ...base, queue: [stuck], cases: [entry("r1", "The.Kid.1921.1080p")] }));
    expect(grouped.needs.map((card) => card.key)).toEqual(["c1:q"]);
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
