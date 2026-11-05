import { describe, expect, it } from "vitest";
import { inProgressFromLists, inProgressTotal, pipelineCards } from "./in-progress";
import { buildJourney, byStage, laneCounts } from "./journey";
import { journeyHeading } from "./times";

/** #1499: the number beside In progress is the Dashboard pipeline's total, from one function. */

const NOW = Date.parse("2026-11-05T04:39:00Z");

const queueItem = (overrides: Record<string, unknown>) => ({
  id: "q", clientId: "qbit", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movie", title: "Film (2024)", releaseName: "Film.2024.1080p", category: "", status: "downloading",
  progress: 9, speedMbps: 5, etaSeconds: 600, sizeBytes: 2e9, downloadedBytes: 1e8, peers: 3, indexerName: "NZBgeek", errorMessage: null, addedUtc: "2026-11-05T04:30:00Z",
  sourcePath: null, libraryId: null, healthFindings: null, ...overrides
}) as never;

const searching = { entityType: "movie", entityId: "m9", title: "Dune", startedBy: "manual", label: "Searching", startedUtc: "2026-11-05T04:38:00Z" };
const extrasWith = (rows: unknown[]) => ({ searching: rows, requests: [], arrivals: [] }) as never;

describe("the In progress number", () => {
  it("reads 1 when one download is moving, as on the rig, however many things need the person", () => {
    const queue = [queueItem({ id: "d", releaseName: "Deadpool.and.Wolverine.2024", progress: 9 })];

    expect(inProgressFromLists({ queue, handoffs: [], dispatches: [], extras: null, now: NOW })).toBe(1);
  });

  it("is exactly the Dashboard pipeline's total, in the words the pipeline says", () => {
    const queue = [
      queueItem({ id: "a", releaseName: "Gladiator.II.2024" }),
      queueItem({ id: "b", releaseName: "The.Batman.2022", status: "queued", progress: 0, queuePosition: 2 }),
      queueItem({ id: "c", releaseName: "Barbie.2023", status: "imported", progress: 100 })
    ];
    const extras = extrasWith([searching]);
    const sidebar = inProgressFromLists({ queue, handoffs: [], dispatches: [], extras, now: NOW });

    // The Dashboard's own steps, in its own order.
    const built = buildJourney({ queue, handoffs: [], dispatches: [], live: new Map(), summary: undefined, now: NOW });
    const counts = laneCounts(byStage(pipelineCards(built, extras)), undefined);
    const pipelineSays = journeyHeading(inProgressTotal(counts), undefined);

    expect(pipelineSays).toBe(`${sidebar} in progress`);
    // Two downloads and one search; the imported one is in the library, not in progress.
    expect(sidebar).toBe(3);
  });

  it("counts nothing for nothing, and the pipeline says so", () => {
    expect(inProgressFromLists({ queue: [], handoffs: [], dispatches: [], extras: null, now: NOW })).toBe(0);
    expect(journeyHeading(0, undefined)).toBe("nothing in progress right now");
  });

  it("leaves the library out of the total", () => {
    expect(inProgressTotal({ searching: 1, grabbed: 2, downloading: 3, processor: 4, importing: 5, imported: 99 })).toBe(15);
  });
});
