import { describe, expect, it } from "vitest";
import { detailLines, detailRoom, sizePair, spent } from "./detail";
import type { JourneyCard } from "./journey";

const NOW = Date.parse("2026-11-04T12:00:00Z");
const GB = 1024 ** 3;
const card = (overrides: Partial<JourneyCard>): JourneyCard => ({ key: "k", stage: "searching", title: "Arrival", state: { tone: "info", label: "x" }, ...overrides }) as JourneyCard;
const bold = (text: string) => ({ bold: text });

describe("two sizes said together", () => {
  it("says the unit once when it is the same, and twice when it differs", () => {
    expect(sizePair(1.2 * GB, 3.2 * GB)).toBe("1.2 of 3.2 GB");
    expect(sizePair(500 * 1024 ** 2, 3.2 * GB)).toBe("500 MB of 3.2 GB");
  });

  it("says a dash twice for sizes it cannot read", () => {
    expect(sizePair(-1, -1)).toBe("— of —");
  });
});

describe("how long ago something began, as a duration", () => {
  it("says nothing without a time, with an unreadable one, or for a time that has not come", () => {
    expect(spent(null, NOW)).toBeUndefined();
    expect(spent(undefined, NOW)).toBeUndefined();
    expect(spent("garbage", NOW)).toBeUndefined();
    expect(spent(new Date(NOW).toISOString(), NOW)).toBeUndefined();
    expect(spent(new Date(NOW + 5000).toISOString(), NOW)).toBeUndefined();
  });

  it("says at least a minute for anything shorter, and the longer spans as they are", () => {
    expect(spent(new Date(NOW - 5000).toISOString(), NOW)).toBe("1 min");
    expect(spent(new Date(NOW - 4 * 60_000).toISOString(), NOW)).toBe("4 min");
  });
});

describe("the room a card has for detail lines", () => {
  it("fits one line of 15px for each 15px left once 8px are kept under them, and never goes below none", () => {
    expect(detailRoom(200, 100)).toBe(6);
    expect(detailRoom(123, 100)).toBe(1);
    expect(detailRoom(122, 100)).toBe(0);
    expect(detailRoom(100, 200)).toBe(0);
  });
});

describe("a searching card", () => {
  it("says who asked, in bold, with when", () => {
    const lines = detailLines(card({ routeNote: "Asked by Sam", at: new Date(NOW - 5 * 60_000).toISOString() }), 0, NOW);
    expect(lines[0].parts).toEqual(["Asked by ", bold("Sam")]);
    expect(lines[0].right).toBe("5 min ago");
  });

  it("says a route note that is not a request as it is, with no time when the card has none or its time is unreadable", () => {
    expect(detailLines(card({ routeNote: "Looking" }), 0, NOW)).toEqual([{ parts: ["Looking"], right: undefined }]);
    expect(detailLines(card({ routeNote: "Looking", at: "garbage" }), 0, NOW)[0].right).toBeUndefined();
  });

  it("says nothing when there is no note", () => {
    expect(detailLines(card({}), 0, NOW)).toEqual([]);
  });

  it("says how many indexers a request has to ask, singular or plural, and leaves it out when there are none", () => {
    expect(detailLines(card({ startedBy: "seerr" }), 3, NOW)).toEqual([{ parts: [bold("3"), " indexers to ask"] }]);
    expect(detailLines(card({ startedBy: "seerr" }), 1, NOW)).toEqual([{ parts: [bold("1"), " indexer to ask"] }]);
    expect(detailLines(card({ startedBy: "seerr" }), 0, NOW)).toEqual([]);
    expect(detailLines(card({}), 5, NOW)).toEqual([]);
  });
});

describe("a grabbed card", () => {
  it("gives the release name, then the size and the client it went to", () => {
    const lines = detailLines(card({ stage: "grabbed", release: "Arrival.2016", sizeBytes: 2 * GB, client: "qBittorrent" }), 0, NOW);
    expect(lines).toEqual([{ parts: ["Arrival.2016"], mono: true }, { parts: [bold("2.0 GB")], right: "to qBittorrent" }]);
  });

  it("leaves out the client when there is none, and the size when it is zero or missing", () => {
    expect(detailLines(card({ stage: "grabbed", sizeBytes: 2 * GB }), 0, NOW)).toEqual([{ parts: [bold("2.0 GB")], right: undefined }]);
    expect(detailLines(card({ stage: "grabbed", sizeBytes: 0, release: "R" }), 0, NOW)).toEqual([{ parts: ["R"], mono: true }]);
    expect(detailLines(card({ stage: "grabbed", sizeBytes: null }), 0, NOW)).toEqual([]);
  });
});

describe("a downloading card", () => {
  it("gives what has come of the whole, when it is done, how many peers and the release", () => {
    const lines = detailLines(card({ stage: "downloading", sizeBytes: 4 * GB, downloadedBytes: 1 * GB, finishAtMs: NOW + 10 * 60_000, peers: 12, release: "R", protocol: "torrent" }), 0, NOW);
    expect(lines[0].parts).toEqual([bold("1.0 of 4.0 GB")]);
    expect(lines[0].right).toBeTruthy();
    expect(lines[1]).toEqual({ parts: [bold("12"), " peers"] });
    expect(lines[2]).toEqual({ parts: ["R"], mono: true });
  });

  it("works out what has come from the progress when the client gave no figure", () => {
    expect(detailLines(card({ stage: "downloading", sizeBytes: 4 * GB, progress: 25 }), 0, NOW)[0].parts).toEqual([bold("1.0 of 4.0 GB")]);
    expect(detailLines(card({ stage: "downloading", sizeBytes: 4 * GB }), 0, NOW)).toEqual([]);
  });

  it("says seeders beside peers, with the swarm when the client reports it (#1516)", () => {
    expect(detailLines(card({ stage: "downloading", peers: 5, seeders: 2, protocol: "torrent" }), 0, NOW)[0].parts).toEqual([bold("2"), " seeders · ", bold("5"), " peers"]);
    expect(detailLines(card({ stage: "downloading", peers: 1, seeders: 1, protocol: "torrent" }), 0, NOW)[0].parts).toEqual([bold("1"), " seeder · ", bold("1"), " peer"]);
    expect(detailLines(card({ stage: "downloading", peers: 5, seeders: 3, swarmSeeders: 41, protocol: "torrent" }), 0, NOW)[0].parts).toEqual([bold("3 of 41"), " seeders · ", bold("5"), " peers"]);
    expect(detailLines(card({ stage: "downloading", peers: 8, seeders: 3, protocol: "usenet" }), 0, NOW)[0].parts).toEqual([bold("8"), " connections"]);
  });

  it("calls them connections on Usenet, and one peer a peer", () => {
    expect(detailLines(card({ stage: "downloading", peers: 8, protocol: "usenet" }), 0, NOW)[0].parts).toEqual([bold("8"), " connections"]);
    expect(detailLines(card({ stage: "downloading", peers: 1, protocol: "torrent" }), 0, NOW)[0].parts).toEqual([bold("1"), " peer"]);
  });

  it("says only when it will be done, when the size is not known", () => {
    const lines = detailLines(card({ stage: "downloading", finishAtMs: NOW + 10 * 60_000 }), 0, NOW);
    expect(lines).toHaveLength(1);
    expect(lines[0].parts).toEqual([]);
    expect(lines[0].right).toBeTruthy();
  });

  it("says nothing more once it is stuck, and gives no time it will be done", () => {
    const lines = detailLines(card({ stage: "downloading", sizeBytes: 4 * GB, downloadedBytes: 1 * GB, finishAtMs: NOW + 10 * 60_000, stuck: true, peers: 3, release: "R" }), 0, NOW);
    expect(lines).toEqual([{ parts: [bold("1.0 of 4.0 GB")], right: undefined }]);
  });

  it("leaves out the peers when there are none or the count is zero", () => {
    expect(detailLines(card({ stage: "downloading", peers: 0, release: "R" }), 0, NOW)).toEqual([{ parts: ["R"], mono: true }]);
  });
});

describe("a card with the processor", () => {
  it("gives its step, what was handed over and how long ago, its place in line and the release", () => {
    const lines = detailLines(card({ stage: "processor", processorStep: "Cleaning", sizeBytes: GB, handedOverUtc: new Date(NOW - 4 * 60_000).toISOString(), processorQueuePosition: 2, release: "R" }), 0, NOW);
    expect(lines).toEqual([
      { parts: [bold("Cleaning")] },
      { parts: [bold("1.0 GB"), " handed over"], right: "4 min" },
      { parts: ["Number 2 in its line"] },
      { parts: ["R"], mono: true }
    ]);
  });

  it("says just Handed over when the size is not known, and leaves out a step, a place of zero and a missing time", () => {
    expect(detailLines(card({ stage: "processor", processorQueuePosition: 0 }), 0, NOW)).toEqual([{ parts: ["Handed over"], right: undefined }]);
  });
});

describe("a card being imported", () => {
  it("gives where it is going, then the size and quality together, then the release", () => {
    const lines = detailLines(card({ stage: "importing", destination: "Movies", sizeBytes: 2 * GB, quality: "WEB 1080p", release: "R" }), 0, NOW);
    expect(lines).toEqual([
      { parts: ["To ", bold("Movies")] },
      { parts: [bold("2.0 GB"), " · WEB 1080p"] },
      { parts: ["R"], mono: true }
    ]);
  });

  it("gives just the quality, or just the size, when that is all it has, and nothing when it has neither", () => {
    expect(detailLines(card({ stage: "importing", quality: "WEB 1080p" }), 0, NOW)).toEqual([{ parts: [bold("WEB 1080p")] }]);
    expect(detailLines(card({ stage: "importing", sizeBytes: 2 * GB }), 0, NOW)).toEqual([{ parts: [bold("2.0 GB")] }]);
    expect(detailLines(card({ stage: "importing" }), 0, NOW)).toEqual([]);
  });
});

describe("a finished card, and the line that says why it is being fetched", () => {
  it("has no detail of its own at a stage that needs none", () => {
    expect(detailLines(card({ stage: "imported" as JourneyCard["stage"] }), 0, NOW)).toEqual([]);
  });

  it("adds why Deluno is fetching it at the end, for any stage, and never more than four lines", () => {
    const lines = detailLines(card({ stage: "processor", processorStep: "Cleaning", sizeBytes: GB, processorQueuePosition: 2, release: "R", startedBy: "list" }), 0, NOW);
    expect(lines).toHaveLength(4);
    const withReason = detailLines(card({ stage: "grabbed", startedBy: "list" }), 0, NOW);
    expect(withReason).toHaveLength(1);
    expect(withReason[0].parts).toHaveLength(1);
  });

  it("says why a request is being fetched at every stage after the search, and why anything else is at the search too", () => {
    expect(detailLines(card({ stage: "grabbed", startedBy: "seerr" }), 0, NOW)).toHaveLength(1);
    expect(detailLines(card({ stage: "searching", startedBy: "list" }), 0, NOW)).toHaveLength(1);
    expect(detailLines(card({ stage: "searching", startedBy: "seerr" }), 2, NOW)).toHaveLength(1);
  });

  it("gives no size line for a size below nothing, and no place in line for a place below nothing", () => {
    expect(detailLines(card({ stage: "grabbed", sizeBytes: -5 }), 0, NOW)).toEqual([]);
    expect(detailLines(card({ stage: "processor", processorQueuePosition: -1 }), 0, NOW)).toEqual([{ parts: ["Handed over"], right: undefined }]);
  });
});
