import { describe, expect, it } from "vitest";
import type { DownloadQueueItem } from "./api";
import { finishedPlan, seedTargetWords, splitQueue } from "./in-progress";
import { buildJourney } from "../routes/dashboard/live/journey";

/** What In progress counts as happening now and what it says about a finished download (#1448). */

const item = (status: string, over: Partial<DownloadQueueItem> = {}): DownloadQueueItem => ({
  id: `id-${status}`, clientId: "qbit", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies", title: status, releaseName: `Rel.${status}.2026.1080p-GRP`,
  category: "movies", status, progress: status === "downloading" ? 40 : 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 1_000_000_000, downloadedBytes: 1_000_000_000, peers: 0,
  indexerName: "Fixture", errorMessage: null, addedUtc: "2026-10-31T00:00:00Z", sourcePath: null, libraryId: null, healthFindings: null, ...over
});

const rule = { sharingMode: "share-then-tidy", sharingForHours: 48, sharingUntilRatio: 2, sharingCompletionAction: "reclaim" };

describe("splitQueue", () => {
  it("keeps downloading, waiting, with-the-processor and importing at the top, and finished ones apart", () => {
    const statuses = ["downloading", "queued", "paused", "waitingForProcessor", "processing", "importQueued", "importReady", "handingOver", "imported", "importedFileLeft", "filesGone", "handedOverElsewhere"];
    const { happeningNow, finished } = splitQueue(statuses.map((s) => item(s)));
    expect(happeningNow.map((x) => x.status)).toEqual(["downloading", "queued", "paused", "waitingForProcessor", "processing", "importQueued", "importReady", "handingOver"]);
    expect(finished.map((x) => x.status)).toEqual(["imported", "importedFileLeft", "filesGone", "handedOverElsewhere"]);
    expect(happeningNow.length + finished.length).toBe(statuses.length);
  });

  it("41 imported downloads that are seeding count as none in flight", () => {
    const seeding = Array.from({ length: 41 }, (_, n) => item("imported", { id: `s${n}`, releaseName: `Seeding.${n}.2026-GRP` }));
    const { happeningNow, finished } = splitQueue([...seeding, item("downloading")]);
    expect(happeningNow).toHaveLength(1);
    expect(finished).toHaveLength(41);
  });

  it("agrees with the Dashboard: it draws no card for a finished download, and one for each that is happening", () => {
    const statuses = ["downloading", "queued", "paused", "processing", "importQueued", "importReady", "imported", "importedFileLeft", "filesGone"];
    const queue = statuses.map((s) => item(s));
    const cards = buildJourney({ queue, handoffs: [], dispatches: [], live: new Map(), summary: undefined, now: Date.now() });
    const drawn = (q: DownloadQueueItem) => cards.some((card) => card.stage !== "imported" && card.key.includes(q.id));
    const { happeningNow, finished } = splitQueue(queue);
    expect(finished.filter(drawn)).toEqual([]);
    expect(happeningNow.filter((q) => !drawn(q))).toEqual([]);
  });
});

describe("finishedPlan", () => {
  it("a torrent seeding after import says what it seeds until, then what Deluno does", () => {
    expect(finishedPlan(item("imported"), rule)).toBe("Seeding until 2 days or a ratio of 2, then Deluno removes it from the client.");
  });

  it("with Stop seeding chosen it says it stops and leaves the entry", () => {
    expect(finishedPlan(item("imported"), { ...rule, sharingCompletionAction: "stop" })).toContain("stops seeding it and leaves it in the client");
  });

  it("names the client's own limit when it reports one", () => {
    expect(finishedPlan(item("imported", { seedLimit: "ratio 1.5" }), rule)).toContain("The client's own limit: ratio 1.5.");
  });

  it("leave-alone never removes anything, and tidy-now or Usenet removes after the import is checked", () => {
    expect(finishedPlan(item("imported"), { ...rule, sharingMode: "leave-alone" })).toBe("Deluno leaves it in the client. You decide when to remove it.");
    expect(finishedPlan(item("imported"), { ...rule, sharingMode: "tidy-now" })).toContain("once the import is checked");
    expect(finishedPlan(item("imported", { protocol: "sabnzbd" }), rule)).toContain("once the import is checked");
  });

  it("a seeding rule with no target says it keeps the entry", () => {
    expect(finishedPlan(item("imported"), { ...rule, sharingForHours: null, sharingUntilRatio: null })).toContain("no target set");
  });

  it("files gone and another copy handed over say so", () => {
    expect(finishedPlan(item("filesGone"), rule)).toContain("The files are gone");
    expect(finishedPlan(item("handedOverElsewhere"), rule)).toContain("nothing is owed");
  });

  it("words hours and days", () => {
    expect(seedTargetWords({ sharingForHours: 1, sharingUntilRatio: null })).toBe("1 hour");
    expect(seedTargetWords({ sharingForHours: 36, sharingUntilRatio: 1.5 })).toBe("36 hours or a ratio of 1.5");
  });
});
