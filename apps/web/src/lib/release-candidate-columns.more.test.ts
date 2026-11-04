import { describe, expect, it } from "vitest";
import {
  candidateSortValue, defaultSortDirection, filterCandidates, formatGrabs, formatProtocol, formatReleaseAge, formatSeedersAndLeechers, sortCandidates,
  type CandidateColumnsInput, type CandidateSortKey
} from "./release-candidate-columns";

const c = (name: string, overrides: Partial<CandidateColumnsInput> = {}): CandidateColumnsInput => ({ indexerName: name, ...overrides });
const names = (list: CandidateColumnsInput[]) => list.map((x) => x.indexerName);

describe("which way each column sorts first", () => {
  it("is alphabetical for the protocol and biggest first for everything else", () => {
    expect(defaultSortDirection("protocol")).toBe("asc");
    for (const key of ["age", "seeders", "leechers", "size", "grabs"] as CandidateSortKey[]) expect(defaultSortDirection(key)).toBe("desc");
  });
});

describe("the value a column sorts by when its header is clicked", () => {
  it("is the column's own value, or nothing when the release has none", () => {
    const full = c("a", { indexerProtocol: "Torznab", releaseAgeHours: 5, seeders: 7, leechers: 2, sizeBytes: 100, grabs: 9 });
    expect(candidateSortValue(full, "protocol")).toBe("torznab");
    expect(candidateSortValue(full, "age")).toBe(5);
    expect(candidateSortValue(full, "seeders")).toBe(7);
    expect(candidateSortValue(full, "leechers")).toBe(2);
    expect(candidateSortValue(full, "size")).toBe(100);
    expect(candidateSortValue(full, "grabs")).toBe(9);
    for (const key of ["protocol", "age", "seeders", "leechers", "size", "grabs"] as CandidateSortKey[]) expect(candidateSortValue(c("b"), key)).toBeNull();
    expect(candidateSortValue(c("b", { indexerProtocol: "" }), "protocol")).toBeNull();
  });

  it("keeps a real zero", () => {
    expect(candidateSortValue(c("a", { seeders: 0, leechers: 0, sizeBytes: 0, grabs: 0, releaseAgeHours: 0 }), "seeders")).toBe(0);
    expect(candidateSortValue(c("a", { releaseAgeHours: 0 }), "age")).toBe(0);
  });
});

describe("sorting the results", () => {
  it.each([
    ["seeders", [c("low", { seeders: 1 }), c("high", { seeders: 9 }), c("none")], ["none", "low", "high"]],
    ["leechers", [c("low", { leechers: 1 }), c("high", { leechers: 9 }), c("none")], ["none", "low", "high"]],
    ["size", [c("big", { sizeBytes: 900 }), c("small", { sizeBytes: 100 }), c("none")], ["none", "small", "big"]],
    ["grabs", [c("many", { grabs: 50 }), c("few", { grabs: 2 }), c("none")], ["none", "few", "many"]],
    ["age", [c("old", { releaseAgeHours: 500 }), c("new", { releaseAgeHours: 1 }), c("unknown")], ["new", "old", "unknown"]],
    ["protocol", [c("usenet", { indexerProtocol: "Newznab" }), c("torrent", { indexerProtocol: "torznab" }), c("blank")], ["blank", "usenet", "torrent"]]
  ] as const)("by %s, ascending", (key, list, expected) => {
    expect(names(sortCandidates(list as unknown as CandidateColumnsInput[], key, "asc"))).toEqual(expected);
  });

  it("puts a release with no size or grab count below one that reports zero", () => {
    expect(names(sortCandidates([c("zero", { sizeBytes: 0 }), c("none")], "size", "asc"))).toEqual(["none", "zero"]);
    expect(names(sortCandidates([c("zero", { grabs: 0 }), c("none")], "grabs", "asc"))).toEqual(["none", "zero"]);
    expect(names(sortCandidates([c("zero", { seeders: 0 }), c("none")], "seeders", "asc"))).toEqual(["none", "zero"]);
    expect(names(sortCandidates([c("zero", { leechers: 0 }), c("none")], "leechers", "asc"))).toEqual(["none", "zero"]);
  });

  it("sorts protocols without regard to case, and a release with no protocol first either way round", () => {
    expect(names(sortCandidates([c("upper", { indexerProtocol: "Torznab" }), c("lower", { indexerProtocol: "torznab" })], "protocol", "asc"))).toEqual(["upper", "lower"]);
    expect(names(sortCandidates([c("a", { indexerProtocol: "newznab" }), c("blank")], "protocol", "asc"))).toEqual(["blank", "a"]);
    expect(names(sortCandidates([c("blank"), c("a", { indexerProtocol: "newznab" })], "protocol", "asc"))).toEqual(["blank", "a"]);
  });

  it("reverses for descending, without touching the list it was given", () => {
    const list = [c("a", { seeders: 1 }), c("b", { seeders: 9 })];
    expect(names(sortCandidates(list, "seeders", "desc"))).toEqual(["b", "a"]);
    expect(names(list)).toEqual(["a", "b"]);
  });

  it("keeps an unknown value after a known one, whichever way it sorts, for age", () => {
    const list = [c("unknown"), c("known", { releaseAgeHours: 3 })];
    expect(names(sortCandidates(list, "age", "asc"))).toEqual(["known", "unknown"]);
  });
});

describe("filtering the results", () => {
  const list = [c("A", { indexerProtocol: "Torznab" }), c("B", { indexerProtocol: "newznab" }), c("C")];

  it("keeps everything when nothing is asked for", () => {
    expect(filterCandidates(list, {})).toHaveLength(3);
    expect(filterCandidates(list, { protocol: "", indexerName: "" })).toHaveLength(3);
  });

  it("keeps the releases of one protocol, whatever its case", () => {
    expect(names(filterCandidates(list, { protocol: "TORZNAB" }))).toEqual(["A"]);
    expect(names(filterCandidates(list, { protocol: "newznab" }))).toEqual(["B"]);
  });

  it("drops a release with no protocol when a protocol is asked for", () => {
    expect(names(filterCandidates(list, { protocol: "torznab" }))).not.toContain("C");
  });

  it("keeps the releases of one indexer, spelt exactly", () => {
    expect(names(filterCandidates(list, { indexerName: "B" }))).toEqual(["B"]);
    expect(filterCandidates(list, { indexerName: "b" })).toEqual([]);
  });

  it("keeps only what matches both", () => {
    expect(names(filterCandidates(list, { protocol: "torznab", indexerName: "A" }))).toEqual(["A"]);
    expect(filterCandidates(list, { protocol: "torznab", indexerName: "B" })).toEqual([]);
  });
});

describe("the words in the results table", () => {
  it.each([[undefined, "—"], [null, "—"], [Number.NaN, "—"], [0, "<1h"], [0.9, "<1h"], [1, "1h"], [1.4, "1h"], [47, "47h"], [47.6, "48h"], [48, "2d"], [72, "3d"], [100, "4d"]])("an age of %s reads %s", (hours, text) => {
    expect(formatReleaseAge(hours as number | null | undefined)).toBe(text);
  });

  it("gives seeders over leechers, with a dash for a count never reported, and keeps a zero", () => {
    expect(formatSeedersAndLeechers(12, 3)).toBe("12 / 3");
    expect(formatSeedersAndLeechers(0, 0)).toBe("0 / 0");
    expect(formatSeedersAndLeechers(null, 3)).toBe("— / 3");
    expect(formatSeedersAndLeechers(12, undefined)).toBe("12 / —");
    expect(formatSeedersAndLeechers()).toBe("— / —");
  });

  it("gives the grab count, with a dash when the indexer does not report it, and keeps a zero", () => {
    expect(formatGrabs(7)).toBe("7");
    expect(formatGrabs(0)).toBe("0");
    expect(formatGrabs(null)).toBe("—");
    expect(formatGrabs(undefined)).toBe("—");
  });

  it.each([[undefined, "—"], [null, "—"], ["", "—"], ["newznab", "Usenet"], ["Usenet", "Usenet"], ["torznab", "Torrent"], ["TRACKER", "Torrent"], ["rss", "RSS"], ["RSS", "RSS"], ["custom", "custom"]])("a protocol of %s reads %s", (protocol, text) => {
    expect(formatProtocol(protocol as string | null | undefined)).toBe(text);
  });
});
