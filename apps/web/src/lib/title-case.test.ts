import { describe, expect, it } from "vitest";
import { titleCaseLabel } from "./title-case";

describe("titleCaseLabel", () => {
  it("normalises display headings while preserving product acronyms", () => {
    expect(titleCaseLabel("media management")).toBe("Media Management");
    expect(titleCaseLabel("library & storage")).toBe("Library & Storage");
    expect(titleCaseLabel("TV shows")).toBe("TV Shows");
    expect(titleCaseLabel("API access")).toBe("API Access");
    expect(titleCaseLabel("title, year, IMDb")).toBe("Title, Year, IMDb");
  });

  it("keeps connector words lower in the middle of a heading", () => {
    expect(titleCaseLabel("what deluno saves")).toBe("What Deluno Saves");
    expect(titleCaseLabel("start over from the beginning")).toBe("Start Over from the Beginning");
  });
});
