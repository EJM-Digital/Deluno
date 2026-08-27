import { describe, expect, it } from "vitest";
import {
  applyCustomFilters,
  customFilterCount,
  defaultDisplayOptions,
  emptyCustomFilters,
  parseDisplayOptions
} from "./library-filters";

/**
 * The narrowing a shelf can be asked for, on the browser's side of the wire.
 *
 * A file called `library-filters.test.ts` was deleted in #302 along with the
 * client-side filter engine it tested — an engine nothing imported, holding a
 * second definition of states the server already owned. This is the opposite
 * shape: nothing here decides which titles match. It only counts what is being
 * asked and writes it onto a request, and those are the two things that can go
 * wrong silently.
 */
describe("custom filters", () => {
  it("asks for nothing by default", () => {
    const filters = emptyCustomFilters();
    expect(customFilterCount(filters)).toBe(0);

    const params = new URLSearchParams();
    applyCustomFilters(params, filters);
    // The rule that keeps this free for anybody not using it: an unfiltered
    // page must send the request it sent before the feature existed.
    expect([...params.keys()]).toEqual([]);
  });

  it("counts a range once, not once per end", () => {
    expect(customFilterCount({ ...emptyCustomFilters(), minSizeGb: 5 })).toBe(1);
    expect(customFilterCount({ ...emptyCustomFilters(), minSizeGb: 5, maxSizeGb: 40 })).toBe(1);
    // Two different questions, two on the badge.
    expect(customFilterCount({ ...emptyCustomFilters(), minSizeGb: 5, minYear: 2000 })).toBe(2);
  });

  it("sends only what was set", () => {
    const params = new URLSearchParams();
    applyCustomFilters(params, {
      ...emptyCustomFilters(),
      qualities: ["Remux 2160p", "WEB 2160p"],
      genres: ["Drama"],
      maxSizeGb: 40
    });

    expect(params.get("quality")).toBe("Remux 2160p,WEB 2160p");
    expect(params.get("genre")).toBe("Drama");
    expect(params.get("maxSizeGb")).toBe("40");
    // Not asked for, so not sent — a blank end means "no limit", and sending
    // it as zero would be a filter that matches nothing.
    expect(params.has("minSizeGb")).toBe(false);
    expect(params.has("minRating")).toBe(false);
  });

  it("keeps a zero, because zero is an answer", () => {
    const params = new URLSearchParams();
    applyCustomFilters(params, { ...emptyCustomFilters(), minRating: 0 });
    expect(params.get("minRating")).toBe("0");
    expect(customFilterCount({ ...emptyCustomFilters(), minRating: 0 })).toBe(1);
  });
});

describe("display options", () => {
  it("gives a reader who has never chosen the essentials and none of the extras", () => {
    const options = defaultDisplayOptions();
    expect(options.showTitle).toBe(true);
    expect(options.showQualityBadge).toBe(true);
    // The extras are the answer to "show me more". A card that arrives already
    // carrying everything has nothing left to ask for.
    expect(options.showSize).toBe(false);
    expect(options.showCodec).toBe(false);
  });

  it("fills in options a stored choice predates", () => {
    // Somebody who saved their display options before the extras existed must
    // not get `undefined` for them, which renders as neither on nor off.
    const stored = JSON.stringify({ showTitle: false, showRating: false });
    const options = parseDisplayOptions(stored);

    expect(options.showTitle).toBe(false);
    expect(options.showRating).toBe(false);
    expect(options.showSize).toBe(false);
    expect(options.showGenres).toBe(false);
  });

  it("falls back rather than throwing on anything unreadable", () => {
    expect(parseDisplayOptions("not json")).toEqual(defaultDisplayOptions());
    expect(parseDisplayOptions(null)).toEqual(defaultDisplayOptions());
  });
});
