import { describe, expect, it } from "vitest";
import { countdown, formatCountdown, updatedAgo } from "./countdown";
import { buildJourney, byStage, laneCounts, movedCards, titleFromRelease, type Stage } from "./journey";
import { activityKind } from "./kinds";
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
    const failed = byStage(buildJourney({ ...base, handoffs: [handoff({ status: "failed", failureMessage: "timed out" })] })).processor;
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
    expect(counts.downloading).toBe(6);
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
  });
});
