import { describe, expect, it } from "vitest";
import { initialsOf, tileHue } from "./card";

describe("the poster tile", () => {
  it("reads two letters from the title, leaving out articles, years and episode codes", () => {
    expect(initialsOf("The Wild Robot (2024)")).toBe("WR");
    expect(initialsOf("Lioness S03E08")).toBe("L");
    expect(initialsOf("Say Yes to the Dress S04E07")).toBe("SY");
    expect(initialsOf("An import did not finish")).toBe("ID");
    expect(initialsOf("The")).toBe("T");
    expect(initialsOf("S01E02")).toBe("S");
    expect(initialsOf("Dune: Part Two")).toBe("DP");
    expect(initialsOf("Dune(2021)Part Two")).toBe("DP");
    expect(initialsOf("Theory of Everything")).toBe("TE");
    expect(initialsOf("Breathe Again")).toBe("BA");
    expect(initialsOf("Lioness S01E02a")).toBe("LS");
    expect(initialsOf("ThisS01E02 Show")).toBe("TS");
  });
});

describe("the tint of a poster that has no picture", () => {
  it("is the same every time for a film, a show and an episode, and neutral when the kind is not known", () => {
    expect(tileHue("movie")).toBe(tileHue("movies"));
    expect(tileHue("series")).toBe(tileHue("episode"));
    expect(tileHue("tv")).toBe(265);
    expect(tileHue("series")).toBe(265);
    expect(tileHue("episode")).toBe(265);
    expect(tileHue("movie")).toBe(205);
    expect(tileHue("movie")).not.toBe(tileHue("series"));
    expect(tileHue(undefined)).toBe(210);
  });
});
