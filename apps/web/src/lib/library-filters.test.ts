import { describe, expect, it } from "vitest";
import type { MediaItem } from "./media-types";
import { filterAndSortLibraryItems, isAttentionCandidate, isUpgradeCandidate, matchesCustomRule, parseCustomRules, parseDisplayOptions, resolveRuleValue, type CustomFilterRule, type FilterField } from "./library-filters";

function makeItem(overrides: Partial<MediaItem> = {}): MediaItem {
  return {
    id: "item-1",
    title: "Arrival",
    year: 2016,
    type: "movie",
    poster: null,
    backdrop: null,
    quality: "WEB 1080p",
    status: "downloaded",
    monitored: true,
    sizeGb: 12.5,
    rating: 8,
    genres: ["Science Fiction", "Drama"],
    added: "2024-01-02T00:00:00Z",
    overview: "Linguist meets aliens",
    network: "Example Network",
    tags: ["favourite", "atmos"],
    keywords: ["language", "alien"],
    ...overrides
  };
}

function rule(field: FilterField, comparator: CustomFilterRule["comparator"], value: string): CustomFilterRule {
  return { id: `${field}-${comparator}`, field, comparator, value };
}

describe("library filter predicates", () => {
  it("resolves every supported value shape and unknown fields safely", () => {
    const item = makeItem();
    expect(resolveRuleValue(item, "title")).toBe("Arrival");
    expect(resolveRuleValue(item, "genre")).toBe("Science Fiction Drama");
    expect(resolveRuleValue(item, "tags")).toBe("favourite atmos");
    expect(resolveRuleValue(item, "keywords")).toBe("language alien");
    expect(resolveRuleValue(item, "network")).toBe("Example Network");
    expect(resolveRuleValue(item, "year")).toBe(2016);
    expect(resolveRuleValue(item, "sizeGb")).toBe(12.5);
    expect(resolveRuleValue(item, "monitored")).toBe(true);
    expect(resolveRuleValue(item, "network")).toBe("Example Network");
    expect(resolveRuleValue(item, "not-a-field" as FilterField)).toBeNull();
  });

  it("applies empty, nullable, numeric and string comparator rules", () => {
    const item = makeItem();
    expect(matchesCustomRule(item, rule("title", "contains", "  "))).toBe(true);
    expect(matchesCustomRule(makeItem({ network: undefined }), rule("network", "contains", "hbo"))).toBe(false);

    expect(matchesCustomRule(item, rule("year", "equals", "2016"))).toBe(true);
    expect(matchesCustomRule(item, rule("year", "gt", "2015"))).toBe(true);
    expect(matchesCustomRule(item, rule("year", "gte", "2016"))).toBe(true);
    expect(matchesCustomRule(item, rule("year", "lt", "2017"))).toBe(true);
    expect(matchesCustomRule(item, rule("year", "lte", "2016"))).toBe(true);
    expect(matchesCustomRule(item, rule("year", "gt", "not-a-number"))).toBe(false);

    expect(matchesCustomRule(item, rule("genre", "contains", "fiction"))).toBe(true);
    expect(matchesCustomRule(item, rule("title", "equals", "arrival"))).toBe(true);
    expect(matchesCustomRule(item, rule("title", "notEquals", "dune"))).toBe(true);
  });

  it("parses display options defensively", () => {
    const allOn = { showTitle: true, showMeta: true, showStatusPill: true, showQualityBadge: true, showRating: true };
    expect(parseDisplayOptions(null)).toEqual(allOn);
    expect(parseDisplayOptions("{}")).toEqual(allOn);
    expect(parseDisplayOptions("not json")).toEqual(allOn);
    expect(parseDisplayOptions('{"showTitle":false,"showRating":false}')).toEqual({ ...allOn, showTitle: false, showRating: false });
  });

  it("parses custom rules defensively", () => {
    expect(parseCustomRules(null)).toEqual([]);
    expect(parseCustomRules("not json")).toEqual([]);
    expect(parseCustomRules('[{"id":"genre","field":"genre","comparator":"contains","value":"Animation"}]')).toEqual([{ id: "genre", field: "genre", comparator: "contains", value: "Animation" }]);
  });

  it("identifies upgrades and attention cases without false positives", () => {
    expect(isUpgradeCandidate(makeItem({ wantedReason: "Quality upgrade requested" }))).toBe(true);
    expect(isUpgradeCandidate(makeItem({ wantedReason: undefined, currentQuality: "WEB 1080p", targetQuality: "Bluray 2160p" }))).toBe(true);
    expect(isUpgradeCandidate(makeItem({ wantedReason: undefined, currentQuality: "WEB 1080p", targetQuality: "WEB 1080p" }))).toBe(false);
    expect(isAttentionCandidate(makeItem({ status: "importFailed" }))).toBe(true);
    expect(isAttentionCandidate(makeItem({ status: "processingFailed" }))).toBe(true);
    expect(isAttentionCandidate(makeItem())).toBe(false);
  });

  it("filters using non-title fields, quick filters, ANDed rules, and sort directions", () => {
    const items = [
      makeItem({ id: "arrival", title: "Arrival", year: 2016, sizeGb: 12, genres: ["Science Fiction"], status: "downloaded" }),
      makeItem({ id: "dune", title: "Dune", year: 2021, sizeGb: 20, genres: ["Science Fiction"], status: "missing", monitored: true }),
      makeItem({ id: "spirited", title: "Spirited Away", year: 2001, sizeGb: 8, genres: ["Animation"], status: "missing", monitored: false })
    ];
    const base = { query: "", quickFilter: "all" as const, customRules: [] as CustomFilterRule[], sortField: "title" as const, sortDirection: "asc" as const };

    expect(filterAndSortLibraryItems(items, { ...base, query: "animation" }).map((item) => item.id)).toEqual(["spirited"]);
    expect(filterAndSortLibraryItems(items, { ...base, quickFilter: "monitored" }).map((item) => item.id)).toEqual(["arrival", "dune"]);
    expect(filterAndSortLibraryItems(items, { ...base, quickFilter: "unmonitored" }).map((item) => item.id)).toEqual(["spirited"]);
    expect(filterAndSortLibraryItems(items, { ...base, quickFilter: "downloaded" }).map((item) => item.id)).toEqual(["arrival"]);
    expect(filterAndSortLibraryItems(items, { ...base, quickFilter: "missing" }).map((item) => item.id)).toEqual(["dune", "spirited"]);
    expect(filterAndSortLibraryItems(items, { ...base, customRules: [rule("year", "gt", "2000"), rule("genre", "contains", "science")] }).map((item) => item.id)).toEqual(["arrival", "dune"]);
    expect(filterAndSortLibraryItems(items, { ...base, sortField: "year", sortDirection: "asc" }).map((item) => item.id)).toEqual(["spirited", "arrival", "dune"]);
    expect(filterAndSortLibraryItems(items, { ...base, sortField: "year", sortDirection: "desc" }).map((item) => item.id)).toEqual(["dune", "arrival", "spirited"]);
    expect(filterAndSortLibraryItems(items, { ...base, sortField: "title", sortDirection: "desc" }).map((item) => item.id)).toEqual(["spirited", "dune", "arrival"]);
  });
});
