import { describe, expect, it } from "vitest";
import {
  candidateSortValue,
  defaultSortDirection,
  filterCandidates,
  formatGrabs,
  formatProtocol,
  formatReleaseAge,
  formatSeedersAndLeechers,
  sortCandidates,
  type CandidateColumnsInput
} from "./release-candidate-columns";

/**
 * #753: the shared sort/filter/format logic behind the enriched results
 * columns (protocol, release age, seeders/leechers, size) on both the
 * per-title interactive search results and the dedicated Search page.
 */

function candidate(overrides: Partial<CandidateColumnsInput> & { indexerName: string }): CandidateColumnsInput {
  return { indexerProtocol: null, releaseAgeHours: null, seeders: null, leechers: null, sizeBytes: null, grabs: null, ...overrides };
}

describe("sortCandidates", () => {
  it("sorts by seeders descending by default", () => {
    const items = [
      candidate({ indexerName: "a", seeders: 5 }),
      candidate({ indexerName: "b", seeders: 50 }),
      candidate({ indexerName: "c", seeders: 1 })
    ];

    const sorted = sortCandidates(items, "seeders", defaultSortDirection("seeders"));

    expect(sorted.map((item) => item.indexerName)).toEqual(["b", "a", "c"]);
  });

  it("sorts by protocol alphabetically ascending by default", () => {
    const items = [
      candidate({ indexerName: "a", indexerProtocol: "torznab" }),
      candidate({ indexerName: "b", indexerProtocol: "newznab" })
    ];

    const sorted = sortCandidates(items, "protocol", defaultSortDirection("protocol"));

    expect(sorted.map((item) => item.indexerName)).toEqual(["b", "a"]);
  });

  it("sorts unknown release age last regardless of direction", () => {
    const items = [
      candidate({ indexerName: "known", releaseAgeHours: 2 }),
      candidate({ indexerName: "unknown", releaseAgeHours: null })
    ];

    expect(sortCandidates(items, "age", "asc").map((item) => item.indexerName)).toEqual(["known", "unknown"]);
    expect(sortCandidates(items, "age", "desc").map((item) => item.indexerName)).toEqual(["unknown", "known"]);
  });

  it("reverses ascending order for descending", () => {
    const items = [candidate({ indexerName: "a", sizeBytes: 100 }), candidate({ indexerName: "b", sizeBytes: 200 })];

    expect(sortCandidates(items, "size", "asc").map((item) => item.indexerName)).toEqual(["a", "b"]);
    expect(sortCandidates(items, "size", "desc").map((item) => item.indexerName)).toEqual(["b", "a"]);
  });

  it("sorts by grabs, with an unreported grab count last in descending order (#969)", () => {
    const items = [
      candidate({ indexerName: "reported-low", grabs: 2 }),
      candidate({ indexerName: "unreported", grabs: null }),
      candidate({ indexerName: "reported-high", grabs: 40 })
    ];

    expect(sortCandidates(items, "grabs", "desc").map((item) => item.indexerName)).toEqual([
      "reported-high",
      "reported-low",
      "unreported"
    ]);
  });
});

describe("filterCandidates", () => {
  const items = [
    candidate({ indexerName: "EZTV", indexerProtocol: "torznab" }),
    candidate({ indexerName: "NZBGeek", indexerProtocol: "newznab" })
  ];

  it("filters by protocol", () => {
    expect(filterCandidates(items, { protocol: "newznab" }).map((item) => item.indexerName)).toEqual(["NZBGeek"]);
  });

  it("filters by indexer name", () => {
    expect(filterCandidates(items, { indexerName: "EZTV" }).map((item) => item.indexerName)).toEqual(["EZTV"]);
  });

  it("returns everything when no filter is set", () => {
    expect(filterCandidates(items, {})).toHaveLength(2);
  });
});

describe("formatting helpers", () => {
  it("formats protocol codes in plain English", () => {
    expect(formatProtocol("torznab")).toBe("Torrent");
    expect(formatProtocol("newznab")).toBe("Usenet");
    expect(formatProtocol("tracker")).toBe("Torrent");
    expect(formatProtocol(null)).toBe("—");
  });

  it("formats release age", () => {
    expect(formatReleaseAge(0.5)).toBe("<1h");
    expect(formatReleaseAge(5)).toBe("5h");
    expect(formatReleaseAge(72)).toBe("3d");
    expect(formatReleaseAge(null)).toBe("—");
  });

  it("formats seeders and leechers together, missing values as a dash", () => {
    expect(formatSeedersAndLeechers(12, 3)).toBe("12 / 3");
    expect(formatSeedersAndLeechers(null, null)).toBe("— / —");
  });

  it("formats the grab count, blank when the indexer does not report it (#969)", () => {
    expect(formatGrabs(47)).toBe("47");
    expect(formatGrabs(0)).toBe("0");
    expect(formatGrabs(null)).toBe("—");
    expect(formatGrabs(undefined)).toBe("—");
  });
});

describe("candidateSortValue (the sort key each results column uses)", () => {
  it("gives each column its own value, with the protocol lower-cased and a zero kept as zero", () => {
    const full = candidate({ indexerName: "A", indexerProtocol: "Torznab", releaseAgeHours: 5, seeders: 0, leechers: 2, sizeBytes: 900, grabs: 3 });

    expect(candidateSortValue(full, "protocol")).toBe("torznab");
    expect(candidateSortValue(full, "age")).toBe(5);
    expect(candidateSortValue(full, "seeders")).toBe(0);
    expect(candidateSortValue(full, "leechers")).toBe(2);
    expect(candidateSortValue(full, "size")).toBe(900);
    expect(candidateSortValue(full, "grabs")).toBe(3);
  });

  it("gives null for a value the indexer did not report, in every column", () => {
    const empty = candidate({ indexerName: "B" });

    for (const key of ["protocol", "age", "seeders", "leechers", "size", "grabs"] as const) {
      expect(candidateSortValue(empty, key)).toBeNull();
    }
  });
});
