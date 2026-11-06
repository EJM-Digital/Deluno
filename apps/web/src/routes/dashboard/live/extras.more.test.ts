import { describe, expect, it } from "vitest";
import type { DashboardLiveExtras, LiveArrivalItem, LiveRequestItem } from "../../../lib/api/types/operations";
import type { JourneyCard } from "./journey";
import { arrivalItems, recentArrivalItems, searchingCards, withSeerr } from "./extras";

type Search = NonNullable<DashboardLiveExtras["searching"]>[number];
const search = (overrides: Partial<Search> = {}): Search => ({ entityType: "movie", entityId: "m1", title: "Dune", startedBy: "manual", label: "Searching", startedUtc: "2026-11-04T10:00:00Z", ...overrides });
const request = (overrides: Partial<LiveRequestItem> = {}): LiveRequestItem => ({ mediaType: "movie", entityId: "m1", title: "Dune", status: "added", requestedBy: "Sam", createdUtc: "2026-11-04T09:00:00Z", updatedUtc: "2026-11-04T09:00:00Z", ...overrides });
const card = (overrides: Partial<JourneyCard> = {}): JourneyCard => ({ key: "k", stage: "downloading", title: "Other", state: { tone: "info", label: "x" }, ...overrides }) as JourneyCard;
const arrival = (overrides: Partial<LiveArrivalItem> = {}): LiveArrivalItem => ({ entityType: "movie", entityId: "m1", release: "Dune.2021.1080p", quality: "WEB 1080p", previousQuality: null, upgraded: false, sizeBytes: 100, importedUtc: "2026-11-04T11:00:00Z", ...overrides });

describe("the titles being searched for, as the first station", () => {
  it("makes a card of each search, with the words the server gave and a link to the title", () => {
    const [one] = searchingCards([search()], undefined, []);
    expect(one).toEqual({ key: "search:m1", stage: "searching", title: "Dune", entity: { kind: "movie", id: "m1" }, state: { tone: "info", label: "Searching" }, startedBy: "you", routeNote: undefined, at: "2026-11-04T10:00:00Z", to: "/movies/m1" });
  });

  it("treats anything but a film as a show", () => {
    expect(searchingCards([search({ entityType: "Series", entityId: "s1" })], undefined, [])[0]).toMatchObject({ entity: { kind: "series", id: "s1" }, to: "/tv/s1" });
    expect(searchingCards([search({ entityType: "MOVIE" })], undefined, [])[0].entity?.kind).toBe("movie");
  });

  it("says why each began", () => {
    const by = (startedBy: string) => searchingCards([search({ startedBy })], undefined, [])[0].startedBy;
    expect(by("manual")).toBe("you");
    expect(by("missing")).toBe("missing");
    expect(by("upgrade")).toBe("upgrade");
    expect(by("inherit")).toBeUndefined();
    expect(by("")).toBeUndefined();
  });

  it("says who asked when a Seerr request is for the same title, from when they asked", () => {
    const [one] = searchingCards([search()], [request()], []);
    expect(one).toMatchObject({ startedBy: "seerr", routeNote: "Requested by Sam", at: "2026-11-04T09:00:00Z" });
  });

  it("says the request came from Seerr when it does not say who", () => {
    expect(searchingCards([search()], [request({ requestedBy: null })], [])[0].routeNote).toBe("Seerr request");
    expect(searchingCards([search()], [request({ requestedBy: "" })], [])[0].routeNote).toBe("Seerr request");
  });

  it("ignores requests for other titles", () => {
    expect(searchingCards([search()], [request({ entityId: "m2" })], [])[0]).toMatchObject({ startedBy: "you", routeNote: undefined });
  });

  it("lists nothing for nothing", () => {
    expect(searchingCards(undefined, undefined, [])).toEqual([]);
    expect(searchingCards(null, [request()], [])).toEqual([]);
    expect(searchingCards([], undefined, [])).toEqual([]);
  });

  it("does not say a search twice once a card for the same title is further along", () => {
    expect(searchingCards([search()], undefined, [card({ entity: { kind: "movie", id: "m1" } })])).toEqual([]);
    expect(searchingCards([search({ entityType: "series", entityId: "s1" })], undefined, [card({ entity: { kind: "episode", id: "s1" } })])).toEqual([]);
  });

  it("keeps the search when the card is for another kind of title with the same id, or for another title", () => {
    expect(searchingCards([search()], undefined, [card({ entity: { kind: "series", id: "m1" } })])).toHaveLength(1);
    expect(searchingCards([search()], undefined, [card({ entity: { kind: "movie", id: "m2" } })])).toHaveLength(1);
  });

  it("does not count a card that has only just been searched for or is already imported", () => {
    expect(searchingCards([search()], undefined, [card({ stage: "imported", entity: { kind: "movie", id: "m1" } })])).toHaveLength(1);
    expect(searchingCards([search()], undefined, [card({ stage: "searching", entity: { kind: "movie", id: "m1" } })])).toHaveLength(1);
  });

  it("recognises a card further along by the release name when the library has not named it", () => {
    expect(searchingCards([search({ title: "Dune" })], undefined, [card({ release: "Dune.2021.1080p", title: "x" })])).toEqual([]);
    expect(searchingCards([search({ title: "Dune" })], undefined, [card({ release: undefined, title: "Dune Part Two" })])).toEqual([]);
    expect(searchingCards([search({ title: "Dune" })], undefined, [card({ release: "Heat.1995", title: "Dune" })])).toHaveLength(1);
    expect(searchingCards([search({ title: "Dune" })], undefined, [card({ release: "Heat.1995", title: "x" })])).toHaveLength(1);
  });

  it("never matches on an empty title", () => {
    expect(searchingCards([search({ title: "!!!" })], undefined, [card({ release: "Heat.1995" })])).toHaveLength(1);
  });
});

describe("a card for something asked for in Seerr", () => {
  it("says so wherever it is on its way, once", () => {
    const cards = [card({ entity: { kind: "movie", id: "m1" } }), card({ key: "b", entity: { kind: "movie", id: "m2" } }), card({ key: "c" }), card({ key: "d", entity: { kind: "movie", id: "m1" }, startedBy: "seerr" })];
    const result = withSeerr(cards, [request()]);
    expect(result.map((c) => c.startedBy)).toEqual(["seerr", undefined, undefined, "seerr"]);
    expect(result[3]).toBe(cards[3]);
    expect(result[1]).toBe(cards[1]);
  });

  it("leaves the cards as they are with no requests", () => {
    const cards = [card()];
    expect(withSeerr(cards, undefined)).toBe(cards);
    expect(withSeerr(cards, [])).toBe(cards);
  });
});

describe("today's arrivals", () => {
  const extras = (overrides: Partial<DashboardLiveExtras> = {}) => ({ arrivals: [arrival()], requests: [], ...overrides }) as unknown as DashboardLiveExtras;

  it("is the day's real imports when the server has them, each with its title, quality and where it landed", () => {
    const [one] = arrivalItems(extras({ arrivals: [arrival({ qualityMet: true, libraryId: "lib1", libraryName: "Films" })] }), []);
    expect(one).toEqual({
      key: "arrival:m1", entity: { kind: "movie", id: "m1" }, fallbackTitle: "Dune (2021)", release: "Dune.2021.1080p", quality: "WEB 1080p", previousQuality: null,
      upgraded: false, sizeBytes: 100, at: "2026-11-04T11:00:00Z", qualityMet: true, libraryId: "lib1", libraryName: "Films", requestedBy: null, to: "/movies/m1"
    });
  });

  it("says a title when the release is unknown, and keys an arrival with no title by its place in the list", () => {
    const items = arrivalItems(extras({ arrivals: [arrival({ release: null, entityId: null }), arrival({ entityId: null })] }), []);
    expect(items[0]).toMatchObject({ fallbackTitle: "A title", entity: undefined, to: "/queue", key: "arrival:row0" });
    expect(items[1].key).toBe("arrival:row1");
  });

  it("is not known to have met its quality, or where it landed, when the server did not say", () => {
    const [one] = arrivalItems(extras(), []);
    expect(one).toMatchObject({ qualityMet: null, libraryId: null, libraryName: null });
    expect(arrivalItems(extras({ arrivals: [arrival({ qualityMet: false, libraryId: null, libraryName: null })] }), [])[0].qualityMet).toBe(false);
  });

  it("says who asked for it in Seerr, or no one", () => {
    const items = arrivalItems(extras({ requests: [request({ entityId: "m1", requestedBy: "Sam" }), request({ entityId: "m2", requestedBy: null })], arrivals: [arrival(), arrival({ entityId: "m2" }), arrival({ entityId: "m3" }), arrival({ entityId: null })] }), []);
    expect(items.map((i) => i.requestedBy)).toEqual(["Sam", null, null, null]);
  });

  it("works without any requests at all", () => {
    expect(arrivalItems({ arrivals: [arrival()] } as unknown as DashboardLiveExtras, [])[0].requestedBy).toBeNull();
  });

  it("is the imported cards when the server has not answered", () => {
    const cards = [card({ stage: "imported", key: "imp", entity: { kind: "movie", id: "m1" }, title: "Dune", release: "Dune.2021", quality: "WEB 1080p", at: "2026-11-04T11:00:00Z", sizeBytes: 5 }), card({ stage: "downloading" })];
    const items = arrivalItems(null, cards);
    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({ key: "imp", fallbackTitle: "Dune", release: "Dune.2021", quality: "WEB 1080p", previousQuality: null, upgraded: false, sizeBytes: 5, requestedBy: null });
    expect(arrivalItems(undefined, [card({ stage: "imported", release: undefined, quality: undefined, at: undefined })])[0]).toMatchObject({ release: null, quality: null, at: "" });
  });
});

describe("the newest arrivals of the last seven days", () => {
  it("is empty when the server does not send them", () => {
    expect(recentArrivalItems(null)).toEqual([]);
    expect(recentArrivalItems(undefined)).toEqual([]);
    expect(recentArrivalItems({ arrivals: [] } as unknown as DashboardLiveExtras)).toEqual([]);
  });

  it("lists them with who asked", () => {
    const items = recentArrivalItems({ recentArrivals: [arrival(), arrival({ entityId: "m2" })], requests: [request()] } as unknown as DashboardLiveExtras);
    expect(items.map((i) => i.requestedBy)).toEqual(["Sam", null]);
    expect(recentArrivalItems({ recentArrivals: [arrival()] } as unknown as DashboardLiveExtras)[0].requestedBy).toBeNull();
  });
});

describe("an arrival's key does not depend on the time of the import (#1508)", () => {
  const row = (importedUtc: string, entityId = "m1") => ({ entityType: "movie", entityId, release: "Dune.2021.1080p", quality: null, previousQuality: null, upgraded: false, sizeBytes: null, importedUtc }) as never;
  const keysOf = (arrivals: unknown[]) => arrivalItems({ arrivals, requests: [] } as unknown as DashboardLiveExtras, []).map((item) => item.key);

  it("is the same for the pushed row and the stored row of one import", () => {
    expect(keysOf([row("2026-11-04T11:00:00.000Z")])).toEqual(keysOf([row("2026-11-04T11:00:02.431Z")]));
  });

  it("keeps the older import's key when a newer import of the same title lands", () => {
    const [older] = keysOf([row("2026-11-04T09:00:00Z")]);
    const both = keysOf([row("2026-11-04T11:00:00Z"), row("2026-11-04T09:00:00Z")]);
    expect(both[1]).toBe(older);
    expect(new Set(both).size).toBe(2);
  });
});
