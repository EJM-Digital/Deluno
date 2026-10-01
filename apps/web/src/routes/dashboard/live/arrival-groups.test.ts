import { describe, expect, it } from "vitest";
import { groupArrivals } from "./arrival-groups";
import type { ArrivalItem } from "./extras";
import type { TitleLookup } from "./titles";

const titles: TitleLookup = {
  movies: new Map([["m1", { title: "Sintel", year: 2010 }]]),
  series: new Map([["s1", { title: "Breaking Bad" }]]),
  episodes: new Map([1, 2, 3].map((n) => [`e${n}`, { seriesId: "s1", season: 1, episode: n }]))
};
const ep = (n: number, extra: Partial<ArrivalItem> = {}): ArrivalItem => ({ key: `k${n}`, entity: { kind: "episode", id: `e${n}` }, fallbackTitle: "x", release: null, quality: "WEB 1080p", previousQuality: null, upgraded: false, sizeBytes: 10, at: `2026-10-01T0${n}:00:00Z`, requestedBy: null, to: "/tv", ...extra });
const film: ArrivalItem = { key: "f", entity: { kind: "movie", id: "m1" }, fallbackTitle: "Sintel", release: null, quality: "Bluray 1080p", previousQuality: null, upgraded: false, sizeBytes: 5, at: "2026-10-01T05:00:00Z", requestedBy: null, to: "/movies/m1" };

describe("groupArrivals", () => {
  it("makes one poster of a show's episodes that arrived on the same day, newest import first, and leaves films alone", () => {
    const groups = groupArrivals([ep(3), ep(2), film, ep(1)], titles);
    expect(groups).toHaveLength(2);
    expect(groups[0]).toMatchObject({ title: "Sintel (2010)", episodes: null });
    expect(groups[1]).toMatchObject({ title: "Breaking Bad", episodes: "3 episodes", tag: "WEB 1080p", poster: undefined });
    expect(groups[1].items).toHaveLength(3);
  });

  it("names one episode by its number, says mixed qualities, and counts upgrades", () => {
    expect(groupArrivals([ep(1)], titles)[0]).toMatchObject({ title: "Breaking Bad", episodes: "S01E01" });
    const mixed = groupArrivals([ep(1), ep(2, { quality: "WEB 720p", upgraded: true })], titles)[0];
    expect(mixed.tag).toBe("mixed · 1 upgraded");
  });

  it("keeps the same show on different days apart", () => {
    expect(groupArrivals([ep(1), ep(2, { at: "2026-09-28T03:00:00Z" })], titles)).toHaveLength(2);
  });
});
