import { describe, expect, it } from "vitest";
import type { SeriesSearchHistoryItem } from "../lib/api";
import { ALL_NUMBERING_SCHEME_OPTIONS, explainNumberingScheme, filterSearchHistoryByEpisode } from "./show-detail-page";

function search(overrides: Partial<SeriesSearchHistoryItem> = {}): SeriesSearchHistoryItem {
  return {
    id: "search-1",
    seriesId: "series-1",
    episodeId: null,
    seasonNumber: null,
    episodeNumber: null,
    libraryId: "library-1",
    triggerKind: "scheduled",
    outcome: "matched",
    releaseName: "Example.S01E01.1080p",
    indexerName: "Example indexer",
    detailsJson: null,
    createdUtc: "2026-09-27T00:00:00Z",
    ...overrides
  };
}

describe("filterSearchHistoryByEpisode (#795)", () => {
  it("narrows the list down to one episode's own searches", () => {
    const s01e01 = search({ id: "s1", episodeId: "ep-1" });
    const s01e02 = search({ id: "s2", episodeId: "ep-2" });
    const wholeSeries = search({ id: "s3", episodeId: null });

    expect(filterSearchHistoryByEpisode([s01e01, s01e02, wholeSeries], "ep-1")).toEqual([s01e01]);
  });

  it("returns the full list, unfiltered, when no episode is selected", () => {
    const all = [search({ id: "s1", episodeId: "ep-1" }), search({ id: "s2", episodeId: "ep-2" })];

    expect(filterSearchHistoryByEpisode(all, null)).toEqual(all);
  });

  it("clearing the filter (passing null again) returns to the full series history", () => {
    const all = [search({ id: "s1", episodeId: "ep-1" }), search({ id: "s2", episodeId: "ep-2" })];

    const filtered = filterSearchHistoryByEpisode(all, "ep-1");
    expect(filtered).toHaveLength(1);

    const cleared = filterSearchHistoryByEpisode(all, null);
    expect(cleared).toEqual(all);
  });

  it("returns nothing for an episode with no searches yet, rather than falling back to the whole show", () => {
    const all = [search({ id: "s1", episodeId: "ep-1" })];

    expect(filterSearchHistoryByEpisode(all, "ep-99")).toEqual([]);
  });
});

describe("episode ordering switcher (#863)", () => {
  it("names an actual consequence for every scheme the switcher can offer, in plain words", () => {
    for (const option of ALL_NUMBERING_SCHEME_OPTIONS) {
      const explanation = explainNumberingScheme(option.value);
      expect(explanation.length).toBeGreaterThan(0);
      expect(explanation.toLowerCase()).toContain("match");
    }
  });

  it("gives the DVD-order scheme its own explanation, distinct from the aired default", () => {
    expect(explainNumberingScheme("dvd")).not.toEqual(explainNumberingScheme("standard"));
    expect(explainNumberingScheme("dvd").toLowerCase()).toContain("dvd");
  });

  it("falls back to the aired-order explanation for an unrecognised or missing scheme", () => {
    expect(explainNumberingScheme(undefined)).toEqual(explainNumberingScheme("standard"));
    expect(explainNumberingScheme("something-new")).toEqual(explainNumberingScheme("standard"));
  });
});
