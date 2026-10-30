import { describe, expect, it } from "vitest";
import { CHOSEN_ON_SEARCH_LABEL, isChosenOnSearch, relatedEntityLabel } from "./dispatch-target";

describe("what a download or job was for (#1062)", () => {
  it("recognises a release chosen by hand on Search", () => {
    expect(isChosenOnSearch("release")).toBe(true);
    expect(isChosenOnSearch("movie")).toBe(false);
    expect(isChosenOnSearch(null)).toBe(false);
    expect(isChosenOnSearch(undefined)).toBe(false);
  });

  it("says Chosen on Search instead of a generated id", () => {
    expect(relatedEntityLabel("release", "3f2a-generated")).toEqual({ primary: CHOSEN_ON_SEARCH_LABEL, secondary: undefined });
  });

  it("gives the type and then the id for anything else", () => {
    expect(relatedEntityLabel("movie", "m-1")).toEqual({ primary: "movie", secondary: "m-1" });
  });

  it("gives a dash and no id when neither is known", () => {
    expect(relatedEntityLabel(null, null)).toEqual({ primary: "—", secondary: undefined });
    expect(relatedEntityLabel(undefined, undefined)).toEqual({ primary: "—", secondary: undefined });
  });
});
