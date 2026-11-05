import { describe, expect, it } from "vitest";
import { buildJourney, STAGES } from "./journey";
import { fullSummary } from "../downloads-fixtures";

const NOW = Date.parse("2026-11-05T04:50:34Z");
const RELEASE = "Inside.Out.2.2024.iTA.ENG.Bluray.1080p.x264-Dr4gon";
const base = { queue: [], handoffs: [], dispatches: [], live: new Map(), summary: fullSummary, now: NOW };

/** Inside Out 2 on the rig (#1496): qBittorrent reads 100% and still calls it stalled/downloading, the server has not handed it over yet. */
const queue = (overrides: Record<string, unknown> = {}) => ({
  id: "abc123", clientId: "qb", clientName: "qBittorrent", protocol: "torrent", mediaType: "movie", title: "Inside Out 2", releaseName: RELEASE, category: "", status: "stalled",
  progress: 100, speedMbps: 0, etaSeconds: 0, sizeBytes: 9e9, downloadedBytes: 9e9, peers: 0, indexerName: "", errorMessage: null, addedUtc: "2026-11-05T03:00:00Z",
  sourcePath: "/downloads/inside-out-2", libraryId: "lib-movies", healthFindings: null, handsToProcessor: true, ...overrides
}) as never;
const handoff = (status: string) => ({
  id: "h1", libraryId: "lib-movies", mediaType: "movie", clientId: "qb", queueItemId: "abc123", releaseName: RELEASE, sourcePath: "/downloads/inside-out-2",
  processorName: "Weir", status, outputPath: null, importJobId: null, failureMessage: null, createdUtc: "2026-11-05T04:50:34Z", updatedUtc: "2026-11-05T04:50:34Z"
}) as never;

const stageOf = (cards: ReturnType<typeof buildJourney>) => cards[0]?.stage;

describe("a finished download headed for a processor never shows as importing (#1496)", () => {
  it("is Being handed over in Processing from the moment it finishes, then stays there as it is submitted and waiting", () => {
    const finished = buildJourney({ ...base, queue: [queue()] });
    expect(finished[0]).toMatchObject({ stage: "processor", state: { label: "Being handed over" } });
    expect(stageOf(buildJourney({ ...base, queue: [queue()], handoffs: [handoff("submitted")] }))).toBe("processor");
    expect(stageOf(buildJourney({ ...base, queue: [queue()], handoffs: [handoff("waiting")] }))).toBe("processor");
  });

  it("goes straight to Importing when the library has no processor", () => {
    const cards = buildJourney({ ...base, queue: [queue({ handsToProcessor: false })] });
    expect(cards[0]).toMatchObject({ stage: "importing", state: { label: "Downloaded" } });
  });

  it("never moves a card back a stage, with or without a processor", () => {
    const readings = (processor: boolean) => processor
      ? [
        { queue: queue({ progress: 40, status: "downloading" }), handoffs: [] },
        { queue: queue(), handoffs: [] },
        { queue: queue(), handoffs: [handoff("submitted")] },
        { queue: queue(), handoffs: [handoff("waiting")] },
        { queue: queue({ status: "waitingForProcessor" }), handoffs: [handoff("accepted")] },
        { queue: queue({ status: "importReady" }), handoffs: [handoff("completed")] }
      ]
      : [
        { queue: queue({ progress: 40, status: "downloading", handsToProcessor: false }), handoffs: [] },
        { queue: queue({ handsToProcessor: false }), handoffs: [] },
        { queue: queue({ status: "importReady", handsToProcessor: false }), handoffs: [] },
        { queue: queue({ status: "importQueued", handsToProcessor: false }), handoffs: [] }
      ];
    for (const processor of [true, false]) {
      let furthest = -1;
      for (const step of readings(processor)) {
        const stage = stageOf(buildJourney({ ...base, queue: [step.queue], handoffs: step.handoffs }));
        const index = STAGES.indexOf(stage!);
        expect(index, `${processor ? "with" : "without"} a processor`).toBeGreaterThanOrEqual(furthest);
        furthest = Math.max(furthest, index);
      }
    }
  });
});
