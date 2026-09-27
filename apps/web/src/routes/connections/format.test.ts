import { describe, expect, it } from "vitest";
import { indexerFlagSelected, splitIndexerFlags, toggleIndexerFlag } from "./format";

describe("indexer flag helpers (#745)", () => {
  it("splits and trims a comma-separated flag list", () => {
    expect(splitIndexerFlags(" freeleech ,  double upload,")).toEqual(["freeleech", "double upload"]);
    expect(splitIndexerFlags("")).toEqual([]);
  });

  it("finds a flag regardless of case", () => {
    expect(indexerFlagSelected("Freeleech, Internal", "freeleech")).toBe(true);
    expect(indexerFlagSelected("Freeleech, Internal", "Half Leech")).toBe(false);
  });

  it("adds a flag that isn't already selected", () => {
    expect(toggleIndexerFlag("", "Freeleech")).toBe("Freeleech");
    expect(toggleIndexerFlag("Freeleech", "Internal")).toBe("Freeleech, Internal");
  });

  it("removes a flag that is already selected, leaving the rest untouched", () => {
    expect(toggleIndexerFlag("Freeleech, Internal", "freeleech")).toBe("Internal");
  });

  it("keeps a custom typed flag the known list doesn't know about", () => {
    expect(toggleIndexerFlag("custom-site-flag", "Freeleech")).toBe("custom-site-flag, Freeleech");
  });
});
