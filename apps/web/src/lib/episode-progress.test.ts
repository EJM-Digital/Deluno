import { describe, expect, it } from "vitest";
import type { SeriesEpisodeInventoryItem } from "./api";
import { hasAired, isEpisodeMissing, isEpisodeUpcoming, summariseEpisodes } from "./episode-progress";

const NOW = new Date("2026-08-27T00:00:00Z");

function episode(overrides: Partial<SeriesEpisodeInventoryItem>): SeriesEpisodeInventoryItem {
  return {
    episodeId: "e",
    seasonNumber: 1,
    episodeNumber: 1,
    title: null,
    airDateUtc: "2026-08-01T00:00:00Z",
    monitored: true,
    hasFile: false,
    wantedStatus: "missing",
    wantedReason: "",
    qualityCutoffMet: false,
    lastSearchUtc: null,
    nextEligibleSearchUtc: null,
    updatedUtc: "2026-08-01T00:00:00Z",
    ...overrides
  } as SeriesEpisodeInventoryItem;
}

describe("counting a show's episodes", () => {
  /**
   * The defect this exists to stop: Slow Horses reported "Find 36 missing
   * episodes" when 30 had aired. Six of them could not have been found by
   * anyone, and Deluno was offering to go and look.
   */
  it("does not count an episode that has not aired as missing", () => {
    const unaired = episode({ airDateUtc: "2026-09-16T00:00:00Z" });

    expect(hasAired(unaired, NOW)).toBe(false);
    expect(isEpisodeMissing(unaired, NOW)).toBe(false);
    expect(isEpisodeUpcoming(unaired, NOW)).toBe(true);
  });

  it("counts an aired episode with no file as missing", () => {
    const aired = episode({ airDateUtc: "2026-08-01T00:00:00Z" });

    expect(isEpisodeMissing(aired, NOW)).toBe(true);
    expect(isEpisodeUpcoming(aired, NOW)).toBe(false);
  });

  it("counts an episode with no air date as not aired rather than missing", () => {
    // Announced but unscheduled. Deluno cannot search for it and should not
    // report it as a shortfall.
    const unscheduled = episode({ airDateUtc: null });

    expect(hasAired(unscheduled, NOW)).toBe(false);
    expect(isEpisodeMissing(unscheduled, NOW)).toBe(false);
    expect(isEpisodeUpcoming(unscheduled, NOW)).toBe(true);
  });

  it("never calls an episode you already have missing", () => {
    expect(isEpisodeMissing(episode({ hasFile: true }), NOW)).toBe(false);
    expect(isEpisodeUpcoming(episode({ hasFile: true, airDateUtc: "2026-12-01T00:00:00Z" }), NOW)).toBe(false);
  });

  it("splits a part-way season into aired, held, missing and upcoming", () => {
    // The Slow Horses shape: 36 catalogued, 30 aired, none held.
    const episodes = [
      ...Array.from({ length: 30 }, (_, index) =>
        episode({ episodeId: `aired-${index}`, airDateUtc: "2026-08-01T00:00:00Z" })),
      ...Array.from({ length: 6 }, (_, index) =>
        episode({ episodeId: `later-${index}`, airDateUtc: "2026-09-16T00:00:00Z" }))
    ];

    const progress = summariseEpisodes(episodes, NOW);

    expect(progress.total).toBe(36);
    expect(progress.aired).toBe(30);
    expect(progress.held).toBe(0);
    expect(progress.missing).toBe(30);
    expect(progress.upcoming).toBe(6);
  });

  it("counts an upgradable episode without counting it missing", () => {
    const episodes = [
      episode({ episodeId: "held", hasFile: true, wantedStatus: "upgrade" }),
      episode({ episodeId: "gone", hasFile: false })
    ];

    const progress = summariseEpisodes(episodes, NOW);

    expect(progress.held).toBe(1);
    expect(progress.missing).toBe(1);
    expect(progress.upgradable).toBe(1);
  });
});
