import { describe, expect, it } from "vitest";
import { initialsOf, tileHue } from "./card";

describe("the poster tile", () => {
  it("reads two letters from the title, leaving out articles, years and episode codes", () => {
    expect(initialsOf("The Wild Robot (2024)")).toBe("WR");
    expect(initialsOf("Lioness S03E08")).toBe("L");
    expect(initialsOf("Say Yes to the Dress S04E07")).toBe("SY");
    expect(initialsOf("An import did not finish")).toBe("ID");
  });
});

describe("the tint of a poster that has no picture", () => {
  it("is the same every time for a film, a show and an episode, and neutral when the kind is not known", () => {
    expect(tileHue("movie")).toBe(tileHue("movies"));
    expect(tileHue("series")).toBe(tileHue("episode"));
    expect(tileHue("movie")).not.toBe(tileHue("series"));
    expect(tileHue(undefined)).toBe(210);
  });
});
