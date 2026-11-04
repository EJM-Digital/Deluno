import { describe, expect, it } from "vitest";
import { episodeSpanOf, groupArrivals, groupMet } from "./arrival-groups";
import type { ArrivalItem } from "./extras";
import type { TitleLookup } from "./titles";

const titles: TitleLookup = {
  movies: new Map([["m1", { title: "Sintel", year: 2010 }]]),
  series: new Map([["s1", { title: "Breaking Bad" }], ["s2", { title: "Lioness" }]]),
  episodes: new Map([["e1", { seriesId: "s1", season: 1, episode: 1 }], ["e2", { seriesId: "s1", season: 1, episode: 2 }], ["e5", { seriesId: "s1", season: 1, episode: 5 }], ["e9", { seriesId: "s1", season: 2, episode: 9 }], ["x1", { seriesId: "s2", season: 1, episode: 1 }]])
};
const item = (key: string, overrides: Partial<ArrivalItem> = {}): ArrivalItem =>
  ({ key, entity: { kind: "episode", id: key }, fallbackTitle: "Fallback", release: null, quality: "WEB 1080p", previousQuality: null, upgraded: false, sizeBytes: 10, at: "2026-11-04T10:00:00", requestedBy: null, to: "/queue", ...overrides }) as ArrivalItem;

describe("the span of a run of episodes", () => {
  it("is a range with the count for one season's episodes", () => {
    expect(episodeSpanOf(["S01E05", "S01E08", "S01E06"])).toBe("S01E05–E08 · 3 episodes");
    expect(episodeSpanOf(["s02e03", "S02E03"])).toBe("S02E03 · 2 episodes");
    expect(episodeSpanOf(["S01E01", "S01E10"])).toBe("S01E01–E10 · 2 episodes");
  });

  it("is nothing for fewer than two, for numbers not all known, or for several seasons", () => {
    expect(episodeSpanOf([])).toBeNull();
    expect(episodeSpanOf(["S01E01"])).toBeNull();
    expect(episodeSpanOf(["S01E01", null])).toBeNull();
    expect(episodeSpanOf(["S01E01", "Season 1"])).toBeNull();
    expect(episodeSpanOf(["S01E01", "S02E02"])).toBeNull();
  });
});

describe("whether a group met its quality", () => {
  const met = (qualityMet: boolean | null | undefined) => item("k", { qualityMet });
  it("is not met when any can still be upgraded, met only when every one met it, and unknown otherwise", () => {
    expect(groupMet([met(true), met(false)])).toBe(false);
    expect(groupMet([met(true), met(true)])).toBe(true);
    expect(groupMet([met(true), met(null)])).toBeNull();
    expect(groupMet([met(undefined)])).toBeNull();
    expect(groupMet([])).toBeNull();
  });
});

describe("one poster for each show for each day", () => {
  it("names a run of episodes of one season, newest import first, and adds up their sizes", () => {
    const [group] = groupArrivals([item("e1", { sizeBytes: 5, at: "2026-11-04T09:00:00" }), item("e2", { sizeBytes: null, at: "2026-11-04T11:00:00" }), item("e5", { sizeBytes: 7, at: "2026-11-04T10:00:00" })], titles);
    expect(group).toMatchObject({ key: "group:show:s1:2026-10-4:", title: "Breaking Bad", episodes: "3 episodes", episodeSpan: "S01E01–E05 · 3 episodes", sizeBytes: 12, at: "2026-11-04T11:00:00", upgrades: 0, previousQuality: null });
    expect(group.items.map((i) => i.key)).toEqual(["e2", "e5", "e1"]);
  });

  it("leaves the span empty when the episodes are of different seasons", () => {
    expect(groupArrivals([item("e1"), item("e9")], titles)[0].episodeSpan).toBeNull();
  });

  it("keeps different days, different shows and different libraries apart", () => {
    expect(groupArrivals([item("e1", { at: "2026-11-04T10:00:00" }), item("e2", { at: "2026-11-05T10:00:00" })], titles)).toHaveLength(2);
    expect(groupArrivals([item("e1"), item("x1")], titles)).toHaveLength(2);
    expect(groupArrivals([item("e1", { libraryId: "a" }), item("e2", { libraryId: "b" })], titles)).toHaveLength(2);
    expect(groupArrivals([item("e1", { libraryId: "a" }), item("e2", { libraryId: "a" })], titles)).toHaveLength(1);
  });

  it("groups by the day whatever the time, and keeps arrivals with an unreadable time as their own", () => {
    expect(groupArrivals([item("e1", { at: "2026-11-04T00:01:00" }), item("e2", { at: "2026-11-04T23:59:00" })], titles)).toHaveLength(1);
    expect(groupArrivals([item("e1", { at: "garbage" }), item("e2", { at: "garbage" })], titles)).toHaveLength(1);
    expect(groupArrivals([item("e1", { at: "garbage" }), item("e2", { at: "also garbage" })], titles)).toHaveLength(2);
  });

  it("groups a series import with its episodes", () => {
    const groups = groupArrivals([item("s1", { entity: { kind: "series", id: "s1" } }), item("e1")], titles);
    expect(groups).toHaveLength(1);
  });

  it("does not group an episode the library does not know, or a film", () => {
    const groups = groupArrivals([item("u1", { entity: { kind: "episode", id: "u1" } }), item("f1", { entity: { kind: "movie", id: "m1" } }), item("n1", { entity: undefined })], titles);
    expect(groups).toHaveLength(3);
  });

  it("leaves out a title that is no longer in the library, once the library has answered", () => {
    const answered: TitleLookup = { ...titles, answered: { movies: true, series: true } };
    expect(groupArrivals([item("m", { entity: { kind: "movie", id: "gone" } }), item("e1")], answered)).toHaveLength(1);
  });

  it("names an episode by its number from the library, or from its release, or not at all", () => {
    expect(groupArrivals([item("e1")], titles)[0].episodes).toBe("S01E01");
    expect(groupArrivals([item("u1", { entity: { kind: "episode", id: "u1" }, release: "Show.S03E07.1080p" })], titles)[0].episodes).toBeNull();
    expect(groupArrivals([item("s1", { entity: { kind: "series", id: "s1" }, release: "Breaking.Bad.S03E07.1080p" })], titles)[0].episodes).toBe("S03E07");
    expect(groupArrivals([item("s1", { entity: { kind: "series", id: "s1" }, release: "Breaking.Bad.Complete" })], titles)[0].episodes).toBeNull();
    expect(groupArrivals([item("s1", { entity: { kind: "series", id: "s1" }, release: null })], titles)[0].episodes).toBeNull();
  });

  it("takes the episode numbers from the releases of a series import for the span", () => {
    const series = (key: string, release: string) => item(key, { entity: { kind: "series", id: "s1" }, release });
    expect(groupArrivals([series("a", "BB.S02E03.x"), series("b", "BB.S02E04.x")], titles)[0].episodeSpan).toBe("S02E03–E04 · 2 episodes");
  });
});

describe("the quality tag and bar of a group", () => {
  it("is the one quality, or mixed when they differ, and leaves out arrivals with no quality", () => {
    expect(groupArrivals([item("e1"), item("e2")], titles)[0]).toMatchObject({ tag: "WEB 1080p", quality: "WEB 1080p" });
    expect(groupArrivals([item("e1"), item("e2", { quality: "WEB 720p" })], titles)[0]).toMatchObject({ tag: "mixed", quality: "mixed" });
    expect(groupArrivals([item("e1"), item("e2", { quality: null })], titles)[0]).toMatchObject({ tag: "WEB 1080p", quality: "WEB 1080p" });
    expect(groupArrivals([item("e1", { quality: null }), item("e2", { quality: null })], titles)[0]).toMatchObject({ tag: null, quality: null });
  });

  it("shows an arrow for a single upgrade with both qualities, and the plain quality when either is missing", () => {
    const [one] = groupArrivals([item("e1", { upgraded: true, previousQuality: "WEB 720p", quality: "WEB 1080p" })], titles);
    expect(one).toMatchObject({ tag: "WEB 720p → WEB 1080p", quality: "WEB 1080p", previousQuality: "WEB 720p", upgrades: 1 });
    expect(groupArrivals([item("e1", { upgraded: true, previousQuality: null })], titles)[0].tag).toBe("WEB 1080p");
    expect(groupArrivals([item("e1", { upgraded: true, previousQuality: "WEB 720p", quality: null })], titles)[0].tag).toBeNull();
    expect(groupArrivals([item("e1", { upgraded: false, previousQuality: "WEB 720p" })], titles)[0].tag).toBe("WEB 1080p");
  });

  it("counts the upgrades in a group after its tag, saying Arrived when it has no quality", () => {
    expect(groupArrivals([item("e1", { upgraded: true }), item("e2")], titles)[0].tag).toBe("WEB 1080p · 1 upgraded");
    expect(groupArrivals([item("e1", { upgraded: true, quality: null }), item("e2", { upgraded: true, quality: null })], titles)[0].tag).toBe("Arrived · 2 upgraded");
  });
});

describe("a group's place and library", () => {
  it("is in the tv row for a show, the movies row for a film, and carries the library it landed in", () => {
    expect(groupArrivals([item("e1", { libraryId: "lib1", libraryName: "Shows" })], titles)[0]).toMatchObject({ library: "tv", libraryId: "lib1", libraryName: "Shows" });
    expect(groupArrivals([item("f", { entity: { kind: "movie", id: "m1" } })], titles)[0]).toMatchObject({ library: "movies", libraryId: null, libraryName: null, title: "Sintel (2010)" });
    expect(groupArrivals([item("u1", { entity: { kind: "episode", id: "u1" } })], titles)[0].library).toBe("tv");
    expect(groupArrivals([item("s9", { entity: { kind: "series", id: "unknown" } })], titles)[0].library).toBe("tv");
  });

  it("names a show without an episode code, a title the library does not know by its own fallback, and links to the screen", () => {
    expect(groupArrivals([item("e1")], titles)[0].title).toBe("Breaking Bad");
    const unknown = groupArrivals([item("u1", { entity: { kind: "episode", id: "u1" }, fallbackTitle: "Lost Show S01E02 extra", to: "/queue" })], titles)[0];
    expect(unknown).toMatchObject({ title: "Lost Show S01E02 extra", to: "/queue" });
    expect(groupArrivals([item("f", { entity: { kind: "movie", id: "m1" } })], titles)[0].to).toBe("/movies/m1");
  });

  it("puts the newest group first, and a single arrival keeps its own key", () => {
    const groups = groupArrivals([item("f", { entity: { kind: "movie", id: "m1" }, at: "2026-11-04T08:00:00" }), item("e1", { at: "2026-11-04T12:00:00" })], titles);
    expect(groups.map((g) => g.key)).toEqual(["e1", "f"]);
  });

  it("is empty for no arrivals", () => {
    expect(groupArrivals([], titles)).toEqual([]);
  });

  it("does not take a film or a series for an episode that happens to share its id", () => {
    const clash: TitleLookup = { ...titles, episodes: new Map([...titles.episodes, ["m1", { seriesId: "s1", season: 4, episode: 4 }], ["s1", { seriesId: "s1", season: 4, episode: 5 }]]) };
    const film = groupArrivals([item("f", { entity: { kind: "movie", id: "m1" } })], clash)[0];
    expect(film).toMatchObject({ library: "movies", episodes: null });
    expect(film.key).toBe("f");
    expect(groupArrivals([item("f", { entity: { kind: "movie", id: "m1" } }), item("e1")], clash)).toHaveLength(2);
    expect(groupArrivals([item("a", { entity: { kind: "series", id: "s1" }, release: "BB.S01E01.x" }), item("b", { entity: { kind: "series", id: "s1" }, release: "BB.S01E02.x" })], clash)[0].episodeSpan).toBe("S01E01–E02 · 2 episodes");
  });

  it("reads an episode span only from codes that are exactly a season and an episode", () => {
    expect(episodeSpanOf(["xS01E02", "S01E03"])).toBeNull();
    expect(episodeSpanOf(["S01E02x", "S01E03"])).toBeNull();
  });

  it("strips an episode code and everything after it from a show name", () => {
    const odd: TitleLookup = { ...titles, series: new Map([["s1", { title: "Odd  S10E100 Show" }]]) };
    expect(groupArrivals([item("e1")], odd)[0].title).toBe("Odd");
    const plain: TitleLookup = { ...titles, series: new Map([["s1", { title: "Odd S01E02 Show" }]]) };
    expect(groupArrivals([item("e1")], plain)[0].title).toBe("Odd");
    const one: TitleLookup = { ...titles, series: new Map([["s1", { title: "Odd S1E2 Show" }]]) };
    expect(groupArrivals([item("e1")], one)[0].title).toBe("Odd");
  });

  it("keeps the previous quality only for a single arrival", () => {
    const [group] = groupArrivals([item("e1", { previousQuality: "WEB 720p", at: "2026-11-04T11:00:00" }), item("e2")], titles);
    expect(group.previousQuality).toBeNull();
  });
});
