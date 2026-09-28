import { describe, expect, it } from "vitest";
import { KNOWN_IMPORT_FAILURE_REASONS, reasonWords } from "./failure-reasons";

describe("reasonWords", () => {
  it.each(KNOWN_IMPORT_FAILURE_REASONS)(
    "has plain words for the known backend reason %s",
    (reasonCode) => {
      const words = reasonWords(reasonCode);
      // A missing entry falls back to the raw code (see reasonWords), which
      // is exactly the leak #967 was filed over — a code on screen instead
      // of a sentence a person would say.
      expect(words).not.toBe(reasonCode);
      expect(words.length).toBeGreaterThan(0);
    }
  );

  it("falls back to the raw code for a reason nobody has answered yet", () => {
    expect(reasonWords("somethingBrandNew")).toBe("somethingBrandNew");
  });
});
