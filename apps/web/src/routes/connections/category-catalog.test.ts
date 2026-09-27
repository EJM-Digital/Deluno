import { describe, expect, it } from "vitest";
import {
  categoryIdSelected,
  customCategoryIds,
  joinCategoryIds,
  splitCategoryIds,
  standardCategoriesForScope,
  STANDARD_CATEGORIES,
  toggleCategoryId
} from "./category-catalog";
import { INDEXER_PRESETS } from "./presets";

describe("category ids helpers (#751)", () => {
  it("splits and trims a comma-separated category list", () => {
    expect(splitCategoryIds(" 2000 , 2040,  5000,5040 ")).toEqual(["2000", "2040", "5000", "5040"]);
    expect(splitCategoryIds("")).toEqual([]);
  });

  it("finds a category id regardless of spacing elsewhere in the field", () => {
    expect(categoryIdSelected("2000, 2040, 5000", "2040")).toBe(true);
    expect(categoryIdSelected("2000, 2040, 5000", "5040")).toBe(false);
  });

  it("adds a category id that isn't already selected, leaving the rest untouched", () => {
    expect(toggleCategoryId("", "2000")).toBe("2000");
    expect(toggleCategoryId("2000", "2040")).toBe("2000,2040");
  });

  it("removes a category id that is already selected, leaving the rest untouched", () => {
    expect(toggleCategoryId("2000,2040,5000", "2040")).toBe("2000,5000");
  });

  it("keeps a custom category id the standard list doesn't know about", () => {
    expect(toggleCategoryId("2000,2040,9999", "5000")).toBe(joinCategoryIds(["2000", "2040", "9999", "5000"]));
  });

  // #751: picking every standard category in a single-scope group by name
  // reproduces exactly the same stored value the "Use the standard
  // categories" reset button (and, before this picker existed, typing the
  // numbers by hand) already writes. "both" deliberately picks a narrower
  // combined default (INDEXER_PRESETS.defaultCategories), so the picker's
  // full movies+TV list is a superset of it rather than an exact match.
  it("picking every standard category by name produces the same stored value as the existing single-scope default", () => {
    for (const scope of ["movies", "tv"] as const) {
      const expected = INDEXER_PRESETS.find((preset) => preset.protocol === "torznab")!.defaultCategories(scope);
      const picked = standardCategoriesForScope(scope).reduce((categories, category) => toggleCategoryId(categories, category.id), "");
      expect(splitCategoryIds(picked).sort()).toEqual(splitCategoryIds(expected).sort());
    }
  });

  it("picking every standard category for 'both' includes every category the combined default sets", () => {
    const expected = splitCategoryIds(INDEXER_PRESETS.find((preset) => preset.protocol === "torznab")!.defaultCategories("both"));
    const picked = splitCategoryIds(standardCategoriesForScope("both").reduce((categories, category) => toggleCategoryId(categories, category.id), ""));
    expect(expected.every((id) => picked.includes(id))).toBe(true);
  });

  it("only lists categories from the used-for scope's own group(s)", () => {
    expect(standardCategoriesForScope("movies").every((category) => category.group === "Movies")).toBe(true);
    expect(standardCategoriesForScope("tv").every((category) => category.group === "TV")).toBe(true);
    expect(standardCategoriesForScope("both")).toEqual(STANDARD_CATEGORIES);
  });

  it("reports a non-standard number as a custom category id", () => {
    expect(customCategoryIds("2000,2040,9999")).toEqual(["9999"]);
    expect(customCategoryIds("2000,2040")).toEqual([]);
  });
});
