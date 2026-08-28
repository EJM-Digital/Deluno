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

  it("reads the way a bar is read, best first", () => {
    // Gold, then green, then red — the same order a title climbs the dot's
    // ladder, so the strip under a poster is a miniature of the dot above it.
    expect(TITLE_BAR_SEGMENTS.map((segment) => segment.mark)).toEqual([
      "covered",
      "upgrade",
      "missing"
    ]);
  });

  it("includes gold now that a bar can be gold", () => {
    // DESIGN-002: "Two colours are enough until upgrades exist; gold arrives
    // with them." Upgrades exist — a subtitle made for the file it sits beside
    // is at the cutoff, and Deluno has stopped looking. The legend was correct
    // to omit gold before and would be lying to omit it now.
    expect(TITLE_BAR_SEGMENTS.some((segment) => segment.mark === "covered")).toBe(true);
    expect(TITLE_MARK_LADDER).toContain("covered");
  });

  it("never claims more is done than is held", () => {
    // The gold segment is drawn inside the green one. A settled count larger
    // than the held count would paint gold over a language nobody has, which is
    // the exact claim the cutoff exists to stop making.
    const bar = titleBar({
      hasFile: true,
      subtitleLanguagesWanted: 2,
      subtitleLanguagesHeld: 1,
      subtitleLanguagesSettled: 9
    });

    expect(bar.settled).toBe(1);
    expect(bar.settled).toBeLessThanOrEqual(bar.held);
  });

  it("treats a subtitle of unknown provenance as not done", () => {
    const bar = titleBar({ hasFile: true, subtitleLanguagesWanted: 1, subtitleLanguagesHeld: 1 });

    expect(bar.held).toBe(1);
    expect(bar.settled).toBe(0);
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
      subtitleLanguagesHeld: 5,
      subtitleLanguagesSettled: 4
    })).toEqual({ held: 5, settled: 4, wanted: 6, noun: "subtitle languages" });
  });
});
