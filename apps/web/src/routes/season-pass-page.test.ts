import { describe, expect, it } from "vitest";
import type { SeriesSeasonPassItem } from "../lib/api";
import { groupSeasonPassBySeries, seasonMonitorState } from "./season-pass-page";

function season(overrides: Partial<SeriesSeasonPassItem> = {}): SeriesSeasonPassItem {
  return {
    seriesId: "series-1",
    seriesTitle: "Severance",
    seasonNumber: 1,
    episodeCount: 9,
    monitoredCount: 9,
    ...overrides
  };
}

describe("seasonMonitorState (#791)", () => {
  it("reads 'all' when every episode is monitored", () => {
    expect(seasonMonitorState(9, 9)).toBe("all");
  });

  it("reads 'none' when nothing is monitored", () => {
    expect(seasonMonitorState(9, 0)).toBe("none");
  });

  it("reads 'some' for a partially-monitored season", () => {
    expect(seasonMonitorState(9, 4)).toBe("some");
  });

  it("reads 'none' for a season with no episodes at all, rather than dividing by zero", () => {
    expect(seasonMonitorState(0, 0)).toBe("none");
  });
});

describe("groupSeasonPassBySeries (#791)", () => {
  it("groups seasons under their own series, sorted by season number", () => {
    const rows = [
      season({ seriesId: "a", seriesTitle: "Severance", seasonNumber: 2 }),
      season({ seriesId: "a", seriesTitle: "Severance", seasonNumber: 1 }),
      season({ seriesId: "b", seriesTitle: "Silo", seasonNumber: 1 })
    ];

    const grouped = groupSeasonPassBySeries(rows);

    expect(grouped).toHaveLength(2);
    expect(grouped[0].map((item) => item.seasonNumber)).toEqual([1, 2]);
    expect(grouped[0][0].seriesTitle).toBe("Severance");
  });

  it("sorts series alphabetically by title", () => {
    const rows = [season({ seriesId: "b", seriesTitle: "Silo" }), season({ seriesId: "a", seriesTitle: "Severance" })];

    const grouped = groupSeasonPassBySeries(rows);

    expect(grouped.map((group) => group[0].seriesTitle)).toEqual(["Severance", "Silo"]);
  });

  it("returns an empty grouping for an empty list", () => {
    expect(groupSeasonPassBySeries([])).toEqual([]);
  });
});
