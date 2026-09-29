import { describe, expect, it } from "vitest";
import { describeConditions, emptyCondition, isValidRange, joinRange, ruleProblem, splitRange } from "./auto-tagging";

/** #1024: a rule read back in plain words, and the checks before it is sent. */
describe("auto-tagging rules", () => {
  it("joins conditions of one kind with 'or' and different kinds with 'and', as the server checks them", () => {
    const text = describeConditions([
      { type: "genre", negate: false, value: "Animation" },
      { type: "genre", negate: false, value: "Anime" },
      { type: "originalLanguage", negate: false, value: "ja" }
    ]);
    expect(text).toBe("Genre is Animation or Genre is Anime and Original language is ja");
  });

  it("says 'is not' for a turned-around condition and names profiles and libraries", () => {
    const text = describeConditions(
      [
        { type: "qualityProfile", negate: true, value: "p1" },
        { type: "library", negate: false, value: "l1" },
        { type: "monitored", negate: false, value: "false" }
      ],
      { qualityProfile: () => "HD-1080p", library: () => "Anime" }
    );
    expect(text).toBe("Quality profile is not HD-1080p and Library is Anime and Monitored is no");
  });

  it("writes a year range with either end open in words", () => {
    expect(describeConditions([{ type: "year", negate: false, value: "2000-2010" }])).toBe("Year is 2000 to 2010");
    expect(describeConditions([{ type: "year", negate: false, value: "2015-" }])).toBe("Year is 2015 or later");
    expect(describeConditions([{ type: "year", negate: true, value: "-1999" }])).toBe("Year is not 1999 or earlier");
    expect(describeConditions([{ type: "runtime", negate: false, value: "90" }])).toBe("Runtime (minutes) is 90");
  });

  it("splits and joins the two boxes of a range", () => {
    expect(joinRange("2000", "2010")).toBe("2000-2010");
    expect(joinRange("", "1999")).toBe("-1999");
    expect(joinRange("2015", "")).toBe("2015-");
    expect(joinRange("2020", "2020")).toBe("2020");
    expect(splitRange("2000-2010")).toEqual({ from: "2000", to: "2010" });
    expect(splitRange("-1999")).toEqual({ from: "", to: "1999" });
    expect(splitRange("2020")).toEqual({ from: "2020", to: "2020" });
  });

  it("accepts whole numbers and ranges, and refuses anything else", () => {
    for (const good of ["2020", "2000-2010", "-1999", "2015-"]) expect(isValidRange(good)).toBe(true);
    for (const bad of ["", "-", "recent", "2010-2000", "20a0"]) expect(isValidRange(bad)).toBe(false);
  });

  it("asks for a name, a tag and a real condition before saving", () => {
    expect(ruleProblem({ name: "", tags: [], specifications: [] })).toEqual({
      name: "Give the rule a name.",
      tags: "Choose at least one tag for the rule to give.",
      specifications: "Add at least one condition, or the rule would never match anything."
    });
    expect(ruleProblem({ name: "Anime", tags: ["anime"], specifications: [{ type: "genre", negate: false, value: " " }] }).specifications).toBe("Condition 1 needs a value.");
    expect(ruleProblem({ name: "Anime", tags: ["anime"], specifications: [{ type: "year", negate: false, value: "soon" }] }).specifications).toContain("range");
    expect(ruleProblem({ name: "Anime", tags: ["anime"], specifications: [emptyCondition("monitored")] })).toEqual({});
  });
});
