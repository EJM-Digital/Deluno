import { describe, expect, it } from "vitest";
import { clientLights, clientMeta, clientsSummary } from "./clients";
import { arrivalItems, requestCards, withSeerr } from "./extras";
import type { JourneyCard } from "./journey";
import { qualityShare } from "./standing";

const request = (entityId: string, status: string, title = "Shogun", mediaType = "series") => ({ mediaType, entityId, title, status, requestedBy: "Sam", createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T01:00:00Z" });
const card = (extra: Partial<JourneyCard>): JourneyCard => ({ key: "k", stage: "downloading", title: "x", state: { tone: "info", label: "Downloading" }, to: "/queue", ...extra });

describe("requests in the flow", () => {
  it("makes the first stage of a request that is still being searched for, and leaves out one already further along", () => {
    const cards = requestCards([request("s5", "added"), request("m1", "added", "Dune Part Two", "movie")], [card({ release: "Dune.Part.Two.2024.1080p", entity: { kind: "movie", id: "m1" } })]);
    expect(cards.map((item) => item.title)).toEqual(["Shogun"]);
    expect(cards[0]).toMatchObject({ stage: "requested", state: { label: "Requested · searching" }, routeNote: "Asked by Sam" });
  });

  it("says Seerr on a request, leaves out one whose search failed (the bell has it) and one that is in the library", () => {
    const cards = requestCards([request("s6", "search-failed"), request("s7", "available"), request("s8", "added")], []);
    expect(cards).toHaveLength(1);
    expect(cards[0]).toMatchObject({ stage: "requested", startedBy: "seerr" });
  });

  it("tags a card that came from a Seerr request wherever it is on its way, and no other", () => {
    const tagged = withSeerr([card({ key: "a", entity: { kind: "movie", id: "m1" } }), card({ key: "b", entity: { kind: "movie", id: "m2" } })], [request("m1", "available", "Dune", "movie")]);
    expect(tagged.map((item) => item.startedBy)).toEqual(["seerr", undefined]);
  });
});

describe("today's arrivals", () => {
  it("lists the day's imports with who asked for them, and falls back to the imported cards", () => {
    const extras = { asOfUtc: "", sinceUtc: "", importsToday: 1, importedBytesToday: 1, upgrades: [], protected: { refusedToday: 0, unsafeToday: 0, latest: [] }, retentionReclaimBytes: 0, requests: [request("m1", "available", "Dune", "movie")], arrivals: [{ entityType: "movie", entityId: "m1", release: "Dune.2021.1080p", quality: "WEB 1080p", previousQuality: "WEB 720p", upgraded: true, sizeBytes: 5, importedUtc: "2026-10-01T02:00:00Z" }] };
    expect(arrivalItems(extras, [])[0]).toMatchObject({ upgraded: true, previousQuality: "WEB 720p", requestedBy: "Sam", fallbackTitle: "Dune (2021)" });
    expect(arrivalItems(null, [card({ stage: "imported", title: "Moon (2009)", at: "2026-10-01T02:00:00Z" })])[0].fallbackTitle).toBe("Moon (2009)");
  });
});

describe("download client lights", () => {
  const row = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, protocol: "torrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0, ...extra }) as never;
  const item = (id: string, isEnabled = true) => ({ id, name: id, isEnabled }) as never;
  it("is grey when switched off, red when not answering, amber when paused, blue while moving and green otherwise", () => {
    const lights = clientLights([row("a"), row("b", { reachable: false }), row("c", { paused: true }), row("d")], [item("a"), item("b"), item("c"), item("d"), item("e", false)], new Map([["a", 6.4]]));
    const tone = (id: string) => lights.find((light) => light.id === id)?.tone;
    expect([tone("a"), tone("b"), tone("c"), tone("d"), tone("e")]).toEqual(["info", "bad", "warn", "ok", "idle"]);
    expect(lights[0]).toMatchObject({ id: "a", moving: true });
  });

  it("says the speed while moving and how many it is seeding, and counts both in the header", () => {
    const lights = clientLights([row("a"), row("d")], [item("a"), item("d"), item("e", false)], new Map([["a", 6.4]]), new Map([["a", 8], ["d", 6]]));
    expect(clientMeta(lights.find((light) => light.id === "a")!)).toBe("6.4 MB/s · 8 seeding");
    expect(clientMeta(lights.find((light) => light.id === "d")!)).toBe("6 seeding");
    expect(clientMeta(lights.find((light) => light.id === "e")!)).toBe("switched off");
    expect(clientsSummary(lights)).toBe("1 moving · 14 seeding");
  });
});

describe("standing facts", () => {
  it("says the share of the library meeting its goal", () => {
    const counts = (total: number, qualityMet: number, upgradable: number) => ({ total, qualityMet, upgradable });
    expect(qualityShare({ library: { movies: counts(100, 60, 5), shows: counts(100, 80, 3) } } as never)).toMatchObject({ percent: 70, upgradable: 8 });
    expect(qualityShare({ library: { movies: counts(0, 0, 0), shows: counts(0, 0, 0) } } as never)).toBeNull();
  });
});
