import { describe, expect, it } from "vitest";
import { cardBucket, inProgressFromLists, inProgressTotal, needYouOnBoard, pipelineCards } from "./in-progress";
import { downloadQueueStatuses } from "../../../lib/download-telemetry";
import { buildJourney, byStage, type JourneyCard, type Stage } from "./journey";
import { needsAction } from "../../../lib/needs-action";
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
    const pipelineSays = journeyHeading(inProgressTotal(byStage(pipelineCards(built, extras))), undefined);

    expect(pipelineSays).toBe(`${sidebar} in progress`);
    // Two downloads and one search; the imported one is in the library, not in progress.
    expect(sidebar).toBe(3);
  });

  it("counts nothing for nothing, and the pipeline says so", () => {
    expect(inProgressFromLists({ queue: [], handoffs: [], dispatches: [], extras: null, now: NOW })).toBe(0);
    expect(journeyHeading(0, undefined)).toBe("nothing in progress right now");
  });

  it("leaves the library out of the total", () => {
    const card = (key: string, stage: Stage, tone: "info" | "bad" | "ok" = "info", stuck = false) => ({ key, stage, title: key, state: { tone, label: key }, stuck }) as JourneyCard;
    const grouped = { searching: [card("s", "searching")], grabbed: [card("g", "grabbed")], downloading: [card("d", "downloading")], processor: [card("p", "processor")], importing: [card("i", "importing")], imported: [card("x", "imported", "ok"), card("y", "imported", "ok")] };

    expect(inProgressTotal(grouped)).toBe(5);
  });

  it("leaves out cards that need the person, whichever station they sit at", () => {
    const card = (key: string, stage: Stage, tone: "info" | "bad", stuck = false) => ({ key, stage, title: key, state: { tone, label: key }, stuck }) as JourneyCard;
    const grouped = { searching: [], grabbed: [], downloading: [card("moving", "downloading", "info"), card("stuck", "downloading", "info", true)], processor: [card("pf", "processor", "bad")], importing: [card("if", "importing", "bad")], imported: [] };

    expect(inProgressTotal(grouped)).toBe(1);
  });

  it("reads 0 on the rig's state: one failed import and nothing moving, the failure counted in need you (#1499)", () => {
    const failed = queueItem({ id: "g", releaseName: "Gladiator.II.2024", status: "importFailed", progress: 100, speedMbps: 0, etaSeconds: null, errorMessage: "Import failed: no video file" });
    const queue = [failed];

    expect(inProgressFromLists({ queue, handoffs: [], dispatches: [], extras: null, now: NOW })).toBe(0);
    // The card is still on the board, in the Importing lane, in the bad tone.
    const cards = buildJourney({ queue, handoffs: [], dispatches: [], live: new Map(), summary: undefined, now: NOW });
    expect(cards.map((c) => [c.stage, c.state.label])).toEqual([["importing", "Import failed"]]);
    // And it is the amber figure's to count.
    const telemetry = { clients: [{ queue }] } as never;
    expect(needsAction({ telemetry }).total).toBe(1);
  });

  it("reads 0 for a failed import the client reports only by status and health finding, and counts it in need you", () => {
    const queue = [queueItem({ id: "g", releaseName: "Gladiator.II.2024", status: "importFailed", progress: 100, speedMbps: 0, etaSeconds: null, errorMessage: null, healthFindings: [{ kind: "import-failed", summary: "The import failed." }] })];

    expect(inProgressFromLists({ queue, handoffs: [], dispatches: [], extras: null, now: NOW })).toBe(0);
    expect(needsAction({ telemetry: { clients: [{ queue }] } as never }).total).toBe(1);
  });
});

describe("in progress and need you partition the board (#1499)", () => {
  const findings = [null, [], [{ kind: "import-failed", summary: "The import failed." }], [{ kind: "stalled", summary: "Not moving." }]];
  const statuses = Object.values(downloadQueueStatuses);

  it("puts every card the board draws in exactly one bucket, and need you counts exactly the cards in it", () => {
    let checked = 0;
    for (const status of statuses) for (const healthFindings of findings) for (const errorMessage of [null, "It failed."]) for (const progress of [0, 50, 100]) {
      const queue = [queueItem({ id: "q1", status, healthFindings, errorMessage, progress, speedMbps: progress === 50 ? 0 : 5 })];
      const cards = pipelineCards(buildJourney({ queue, handoffs: [], dispatches: [], live: new Map(), summary: undefined, now: NOW }), null);
      const grouped = byStage(cards);
      const buckets = cards.map(cardBucket);
      const label = JSON.stringify({ status, healthFindings, errorMessage, progress });
      const needs = needsAction({ telemetry: { clients: [{ queue }] } as never }).total;

      expect(inProgressTotal(grouped) + needYouOnBoard(grouped) + buckets.filter((b) => b === "neither").length, label).toBe(cards.length);
      // Every card that is drawn as needing the person is in the amber figure, and none that is in progress is.
      if (buckets.includes("needsYou")) expect(needs, label).toBeGreaterThanOrEqual(1);
      if (buckets.includes("inProgress")) expect(needs, label).toBe(0);
      checked += 1;
    }
    expect(checked).toBe(statuses.length * findings.length * 2 * 3);
  });
});
