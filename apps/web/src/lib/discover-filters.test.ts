import { describe, expect, it } from "vitest";
import {
  LANGUAGES,
  NO_FILTERS,
  TV_GENRES,
  MOVIE_GENRES,
  activeFilterCount,
  describeFilters,
  discoverLists,
  discoverQuery,
  genresFor
} from "./discover-filters";

describe("Discover lists and filters (#1429)", () => {
  it("offers popular, trending and top rated for both, upcoming and in cinemas for films, on air for shows", () => {
    expect(discoverLists("movies").map((item) => item.value)).toEqual(["popular", "trending", "top-rated", "upcoming", "now-playing"]);
    expect(discoverLists("series").map((item) => item.value)).toEqual(["popular", "trending", "top-rated", "now-playing"]);
    expect(discoverLists("series").find((item) => item.value === "now-playing")?.label).toBe("On air");
    expect(discoverLists("movies").find((item) => item.value === "now-playing")?.label).toBe("In cinemas");
    expect(discoverLists("movies")[0]).toEqual({ value: "popular", label: "Popular", title: "Popular on TMDb" });
    expect(discoverLists("movies")).toEqual([
      { value: "popular", label: "Popular", title: "Popular on TMDb" },
      { value: "trending", label: "Trending", title: "Trending this week on TMDb" },
      { value: "top-rated", label: "Top rated", title: "Top rated on TMDb" },
      { value: "upcoming", label: "Upcoming", title: "Upcoming films on TMDb" },
      { value: "now-playing", label: "In cinemas", title: "In cinemas now, on TMDb" }
    ]);
    expect(discoverLists("series")[3]).toEqual({ value: "now-playing", label: "On air", title: "On air now, on TMDb" });
  });

  it("offers fourteen original languages, each by its TMDb code", () => {
    expect(LANGUAGES.map((language) => `${language.value}:${language.label}`)).toEqual([
      "en:English", "fr:French", "de:German", "es:Spanish", "it:Italian", "pt:Portuguese", "ja:Japanese",
      "ko:Korean", "zh:Chinese", "hi:Hindi", "ru:Russian", "sv:Swedish", "da:Danish", "nl:Dutch"
    ]);
  });

  it("has each kind's own genres", () => {
    expect(genresFor("movies")).toBe(MOVIE_GENRES);
    expect(genresFor("series")).toBe(TV_GENRES);
    expect(MOVIE_GENRES.find((genre) => genre.name === "Action")?.id).toBe(28);
    expect(TV_GENRES.find((genre) => genre.name === "Action & Adventure")?.id).toBe(10759);
  });

  it("counts each narrowing filter once, a year range as one", () => {
    expect(activeFilterCount(NO_FILTERS)).toBe(0);
    expect(activeFilterCount({ ...NO_FILTERS, release: "released" })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, yearFrom: "1999" })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, yearTo: "2005" })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, yearFrom: "1999", yearTo: "2005" })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, yearFrom: "  " })).toBe(0);
    expect(activeFilterCount({ ...NO_FILTERS, yearTo: "  " })).toBe(0);
    expect(activeFilterCount({ ...NO_FILTERS, yearFrom: " ", yearTo: " " })).toBe(0);
    expect(activeFilterCount({ ...NO_FILTERS, genres: [28] })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, ratingMin: "7" })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, language: "ja" })).toBe(1);
    expect(activeFilterCount({ ...NO_FILTERS, sort: "newest" })).toBe(1);
    expect(activeFilterCount({ release: "upcoming", yearFrom: "2027", yearTo: "", genres: [28, 12], ratingMin: "6", language: "en", sort: "rating" })).toBe(6);
  });

  it("asks for a plain list by name, and for nothing extra for the popular list on its first page", () => {
    expect(discoverQuery("movies", "popular", NO_FILTERS, 1)).toBe("mediaType=movies");
    expect(discoverQuery("series", "trending", NO_FILTERS, 1)).toBe("mediaType=series&list=trending");
    expect(discoverQuery("movies", "upcoming", NO_FILTERS, 3)).toBe("mediaType=movies&page=3&list=upcoming");
  });

  it("sends every filter, and the filters instead of the list name", () => {
    const query = new URLSearchParams(discoverQuery("movies", "trending", {
      release: "upcoming", yearFrom: "2027", yearTo: "2030", genres: [28, 12], ratingMin: "7", language: "ja", sort: "rating"
    }, 2));
    expect(Object.fromEntries(query)).toEqual({
      mediaType: "movies", page: "2", release: "upcoming", yearFrom: "2027", yearTo: "2030", genres: "28,12", ratingMin: "7", language: "ja", sort: "rating"
    });
    expect(query.has("list")).toBe(false);
  });

  it("sends a filter only when it is set", () => {
    const query = new URLSearchParams(discoverQuery("movies", "popular", { ...NO_FILTERS, ratingMin: "7" }, 1));
    expect([...query.keys()].sort()).toEqual(["mediaType", "ratingMin"]);
    expect(Object.fromEntries(new URLSearchParams(discoverQuery("series", "popular", { ...NO_FILTERS, language: "ja" }, 1)))).toEqual({ mediaType: "series", language: "ja" });
    expect(Object.fromEntries(new URLSearchParams(discoverQuery("series", "popular", { ...NO_FILTERS, sort: "oldest" }, 1)))).toEqual({ mediaType: "series", sort: "oldest" });
    expect(Object.fromEntries(new URLSearchParams(discoverQuery("movies", "popular", { ...NO_FILTERS, release: "released" }, 1)))).toEqual({ mediaType: "movies", release: "released" });
  });

  it("leaves out a year that is not a number", () => {
    const query = new URLSearchParams(discoverQuery("movies", "popular", { ...NO_FILTERS, yearFrom: "abc", yearTo: "", genres: [18] }, 1));
    expect(query.has("yearFrom")).toBe(false);
    expect(query.has("yearTo")).toBe(false);
    expect(query.get("genres")).toBe("18");
  });

  it("says in a sentence what is being filtered", () => {
    expect(describeFilters("movies", NO_FILTERS)).toBe("");
    expect(describeFilters("movies", { ...NO_FILTERS, release: "released" })).toBe("already released");
    expect(describeFilters("movies", { ...NO_FILTERS, release: "upcoming" })).toBe("not out yet");
    expect(describeFilters("movies", { ...NO_FILTERS, yearFrom: "1990", yearTo: "1999" })).toBe("1990 to 1999");
    expect(describeFilters("movies", { ...NO_FILTERS, yearFrom: "1990" })).toBe("from 1990");
    expect(describeFilters("movies", { ...NO_FILTERS, yearTo: "1999" })).toBe("up to 1999");
    expect(describeFilters("movies", { ...NO_FILTERS, yearFrom: " ", yearTo: "1999" })).toBe("up to 1999");
    expect(describeFilters("movies", { ...NO_FILTERS, yearFrom: "1990", yearTo: " " })).toBe("from 1990");
    expect(describeFilters("movies", { ...NO_FILTERS, genres: [28, 12] })).toBe("Action and Adventure");
    expect(describeFilters("series", { ...NO_FILTERS, genres: [10759] })).toBe("Action & Adventure");
    expect(describeFilters("movies", { ...NO_FILTERS, ratingMin: "7", language: "ja" })).toBe("rated 7+, in Japanese");
    expect(describeFilters("movies", { ...NO_FILTERS, language: "xx" })).toBe("in xx");
    expect(describeFilters("movies", { release: "released", yearFrom: "2000", yearTo: "", genres: [35], ratingMin: "6", language: "fr", sort: "newest" }))
      .toBe("already released, from 2000, Comedy, rated 6+, in French");
  });
});
