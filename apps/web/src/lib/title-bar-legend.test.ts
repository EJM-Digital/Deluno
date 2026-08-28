import { describe, expect, it } from "vitest";
import {
  TITLE_BAR_SEGMENTS,
  TITLE_MARK_LADDER,
  TITLE_MARK_PRESENTATION,
  titleBar
} from "./status-tones";

/**
 * The bar and its legend, held to one source — #327.
 *
 * The bar used to write `--success` and `--destructive` straight into a
 * gradient. Adding a legend that reads `TITLE_MARK_PRESENTATION` would have made
 * two places name the same two colours, which is the shape every defect in this
 * codebase has had. These are the assertions that stop it coming back.
 */
describe("the subtitle bar's colours", () => {
  it("names only marks the presentation table can draw", () => {
    for (const segment of TITLE_BAR_SEGMENTS) {
      expect(TITLE_MARK_PRESENTATION[segment.mark]).toBeDefined();
      expect(TITLE_MARK_PRESENTATION[segment.mark].cssVar).toMatch(/^--/);
    }
  });

  it("reads held before missing, the way a bar is read", () => {
    expect(TITLE_BAR_SEGMENTS.map((segment) => segment.mark)).toEqual(["upgrade", "missing"]);
  });

  it("leaves gold out until a bar can be gold", () => {
    // DESIGN-002: "Two colours are enough until upgrades exist; gold arrives
    // with them." A legend listing a colour nothing can be is the same defect
    // as a filter chip that can never match.
    expect(TITLE_BAR_SEGMENTS.some((segment) => segment.mark === "covered")).toBe(false);
    // And it is still a rung on the ladder, so this is a deliberate omission
    // rather than a colour that does not exist.
    expect(TITLE_MARK_LADDER).toContain("covered");
  });

  it("gives every segment a label and a sentence, so colour is never the only carrier", () => {
    // #318: colour must never be the only thing saying what something means.
    for (const segment of TITLE_BAR_SEGMENTS) {
      expect(segment.label.trim().length).toBeGreaterThan(0);
      expect(segment.hint.trim().length).toBeGreaterThan(0);
    }
  });
});

describe("what the legend claims about the bar", () => {
  it("is right that a title holding no files has no bar", () => {
    // The sentence in the legend, asserted against the function it describes:
    // a show with ten aired episodes and nothing downloaded.
    expect(titleBar({
      airedEpisodeCount: 10,
      airedWithFileCount: 0,
      subtitleLanguagesWanted: 1,
      subtitleLanguagesHeld: 0
    }).wanted).toBe(0);

    expect(titleBar({
      hasFile: false,
      subtitleLanguagesWanted: 2,
      subtitleLanguagesHeld: 0
    }).wanted).toBe(0);
  });

  it("is right that a shelf asking for no languages has no bar", () => {
    expect(titleBar({ hasFile: true, subtitleLanguagesWanted: 0 }).wanted).toBe(0);
  });

  it("counts a show over the files it holds, not the episodes it is missing", () => {
    // Three held episodes, two languages asked of each: six, not twenty.
    expect(titleBar({
      airedEpisodeCount: 10,
      airedWithFileCount: 3,
      subtitleLanguagesWanted: 2,
      subtitleLanguagesHeld: 5
    })).toEqual({ held: 5, wanted: 6, noun: "subtitle languages" });
  });
});
