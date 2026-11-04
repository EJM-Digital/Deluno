import { describe, expect, it } from "vitest";
import type { MetadataSearchResult } from "./api";
import { METADATA_SEARCH_PAGE_SIZE, mergeMetadataPage, nextMetadataPage } from "./metadata-search-paging";

const item = (provider: string, providerId: string): MetadataSearchResult => ({
  provider, providerId, mediaType: "movies", title: `${provider}${providerId}`, originalTitle: null, year: 2000,
  overview: null, posterUrl: null, backdropUrl: null, rating: null, genres: [], imdbId: null, externalUrl: null
});

describe("a page of title matches", () => {
  it("is TMDb's twenty, the size the server returns", () => {
    expect(METADATA_SEARCH_PAGE_SIZE).toBe(20);
  });

  it("has a next page only after a whole page came back", () => {
    expect(nextMetadataPage(1, 20)).toBe(2);
    expect(nextMetadataPage(4, 20)).toBe(5);
    expect(nextMetadataPage(1, 19)).toBeNull();
    expect(nextMetadataPage(3, 0)).toBeNull();
  });

  it("adds a page below the matches shown and keeps a film TMDb repeated once", () => {
    const merged = mergeMetadataPage([item("tmdb", "1"), item("tmdb", "2")], [item("tmdb", "2"), item("tmdb", "3"), item("tvdb", "1")]);
    expect(merged.map((m) => `${m.provider}:${m.providerId}`)).toEqual(["tmdb:1", "tmdb:2", "tmdb:3", "tvdb:1"]);
    expect(mergeMetadataPage([], [])).toEqual([]);
  });
});
