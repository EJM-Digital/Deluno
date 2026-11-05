import { describe, expect, it } from "vitest";
import { buildJourney } from "./journey";

/** #1493: a download the client holds in its own queue sits in Downloading, saying its place, and is never Grabbed or Stuck. */

const NOW = Date.parse("2026-11-05T04:39:00Z");

const queue = (overrides: Record<string, unknown>) => ({
  id: "q", clientId: "qbit", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movie", title: "Film (2024)", releaseName: "Film.2024.1080p", category: "", status: "queued",
  progress: 0, speedMbps: 0, etaSeconds: 0, sizeBytes: 2e9, downloadedBytes: 0, peers: 0, indexerName: "NZBgeek", errorMessage: null, addedUtc: "2026-11-05T04:30:00Z",
  sourcePath: null, libraryId: null, healthFindings: null, ...overrides
}) as never;

const summaryWithLimit = (limit: number | null) => ({ downloads: { clients: [{ id: "qbit", activeDownloadLimit: limit }] } }) as never;

describe("the journey of a download the client holds in its own queue", () => {
  const input = (limit: number | null) => ({
    handoffs: [], dispatches: [], live: new Map(), summary: summaryWithLimit(limit), now: NOW,
    queue: [
      queue({ id: "g", releaseName: "Gladiator.II.2024", status: "downloading", progress: 40, speedMbps: 7.5, queuePosition: 1 }),
      queue({ id: "b", releaseName: "The.Batman.2022", queuePosition: 4 }),
      queue({ id: "w", releaseName: "Barbie.2023", queuePosition: 5 }),
      queue({ id: "d", releaseName: "Deadpool.and.Wolverine.2024", queuePosition: 6 })
    ]
  });

  it("is in Downloading with its place and why, never in Grabbed", () => {
    const cards = buildJourney(input(3));
    const barbie = cards.find((card) => card.key === "qbit:w");

    expect(cards.filter((card) => card.stage === "grabbed")).toEqual([]);
    expect(barbie).toMatchObject({ stage: "downloading", waiting: true, stuck: false, state: { tone: "info", label: "Waiting in qBittorrent's queue, 2nd of 3" }, reasonFull: "qBittorrent runs 3 downloads at once" });
  });

  it("is never stuck, even when the client has put a word against it", () => {
    const cards = buildJourney({ ...input(3), queue: [queue({ id: "x", status: "queued", errorMessage: "Not found", queuePosition: 2 })] });

    expect(cards[0]).toMatchObject({ stage: "downloading", stuck: false, waiting: true });
    expect(cards[0].state.tone).toBe("info");
  });

  it("names no reason when the client does not say how many it runs", () => {
    const barbie = buildJourney(input(null)).find((card) => card.key === "qbit:w");

    expect(barbie?.state.label).toBe("Waiting in qBittorrent's queue, 2nd of 3");
    expect(barbie?.reasonFull).toBeNull();
  });

  it("moves up as the client frees slots", () => {
    const base = input(3);
    const later = { ...base, queue: base.queue.filter((_, index) => index !== 1) };
    const barbie = buildJourney(later).find((card) => card.key === "qbit:w");

    expect(barbie?.state.label).toBe("Waiting in qBittorrent's queue, 1st of 2");
  });

  it("leaves a download that is moving as Downloading", () => {
    const gladiator = buildJourney(input(3)).find((card) => card.key === "qbit:g");

    expect(gladiator).toMatchObject({ stage: "downloading", state: { label: "Downloading" } });
  });
});
