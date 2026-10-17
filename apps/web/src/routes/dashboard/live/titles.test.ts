import { beforeEach, describe, expect, it, vi } from "vitest";
import type { JourneyCard } from "./journey";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../../lib/api", () => ({ fetchJson }));

const { faceOf, fetchMovieTitles, fetchSeriesTitles, isGone, NO_TITLES, wantedTitles } = await import("./titles");

const card = (over: Partial<JourneyCard>): JourneyCard => ({
  key: "k", stage: "downloading", title: "Dune.2021.1080p.WEB-DL", state: { tone: "info", label: "Downloading" }, to: "/queue", ...over
} as JourneyCard);

beforeEach(() => fetchJson.mockReset());

describe("which titles the Live board asks the library for", () => {
  it("asks once for each kind of media, with every id once and in a steady order, and never one request per card", () => {
    const cards = [
      card({ key: "a", entity: { kind: "movie", id: "m2" } }),
      card({ key: "b", entity: { kind: "movie", id: "m1" } }),
      card({ key: "c", entity: { kind: "movie", id: "m1" } }),
      card({ key: "d", entity: { kind: "series", id: "s1" } }),
      card({ key: "e", entity: { kind: "episode", id: "e1" } }),
      card({ key: "f" })
    ];

    expect(wantedTitles(cards)).toEqual({ movies: ["m1", "m2"], series: ["s1"], episodes: ["e1"] });
  });

  it("asks for no more than the 48 of a kind the server answers in one request", () => {
    const cards = Array.from({ length: 60 }, (_, index) => card({ key: `c${index}`, entity: { kind: "movie", id: `m${String(index).padStart(2, "0")}` } }));

    expect(wantedTitles(cards).movies).toHaveLength(48);
  });

  it("puts all the film ids in one request and all the show and episode ids in one other", async () => {
    fetchJson.mockResolvedValue({});

    await fetchMovieTitles(["m1", "m2"]);
    await fetchSeriesTitles(["s1"], ["e1", "e2"]);

    expect(fetchJson.mock.calls.map((call) => call[0])).toEqual([
      "/api/movies/titles?ids=m1%2Cm2",
      "/api/series/titles?ids=s1&episodeIds=e1%2Ce2"
    ]);
  });

  it("falls back to the posters alone from a server that has no titles route", async () => {
    fetchJson.mockImplementation(async (...args: string[]) => {
      const path = String(args[0]);
      if (path.startsWith("/api/movies/titles")) throw new Error("404");
      return { m1: "/poster.jpg", m2: null };
    });

    expect(await fetchMovieTitles(["m1", "m2"])).toEqual({ m1: { posterUrl: "/poster.jpg" }, m2: { posterUrl: null } });
  });
});

describe("a card whose title is gone from the library", () => {
  it("keeps the release's own name and is not linked, while a title the library knows is named with its year", () => {
    const known = card({ key: "k1", entity: { kind: "movie", id: "m1" } });
    const gone = card({ key: "k2", entity: { kind: "movie", id: "deleted" }, title: "Alien.1979.1080p.BluRay" });
    const lookup = { ...NO_TITLES, movies: new Map([["m1", { title: "Dune", year: 2021, posterUrl: "/dune.jpg" }]]), answered: { movies: true, series: true } };

    expect(faceOf(known, lookup)).toMatchObject({ title: "Dune (2021)", poster: "/dune.jpg", linked: true });
    expect(faceOf(gone, lookup)).toMatchObject({ title: "Alien.1979.1080p.BluRay", linked: false });
    expect(isGone(gone.entity, lookup)).toBe(true);
    expect(isGone(known.entity, lookup)).toBe(false);
  });

  it("is not called gone before the library has answered", () => {
    expect(isGone({ kind: "movie", id: "m1" }, { ...NO_TITLES })).toBe(false);
  });
});
