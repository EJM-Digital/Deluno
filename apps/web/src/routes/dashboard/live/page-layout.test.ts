import { describe, expect, it } from "vitest";
import { boardLayouts, cardsBudget, journeyBudget, pageLayout } from "./layout";

describe("page layout from the measured main area", () => {
  it("puts the right column beside the page from about 1150px of main area", () => {
    expect(pageLayout(1149, 16).sideBySide).toBe(false);
    expect(pageLayout(1150, 16).sideBySide).toBe(true);
    expect(pageLayout(2200, 16).sideBySide).toBe(true);
  });
  it("keeps the top band three across down to about 900px, then stacks it", () => {
    expect(pageLayout(900, 16).band).toBe("across");
    expect(pageLayout(899, 16).band).toBe("stacked");
    expect(pageLayout(352, 16).band).toBe("stacked");
  });
  it("uses the roomy layout until measured", () => {
    expect(pageLayout(0, 16)).toEqual({ sideBySide: true, band: "across" });
  });
  it("scales the thresholds with the type size", () => {
    expect(pageLayout(1150, 20).sideBySide).toBe(false);
  });
});

describe("journey budget", () => {
  it("is its share of the space under the band when held to the window", () => {
    expect(journeyBudget(true, 1000, 900)).toBe(512);
    expect(journeyBudget(true, 0, 900)).toBeUndefined();
  });
  it("is most of the window when the page scrolls", () => {
    expect(journeyBudget(false, 0, 1000)).toBe(700);
  });
  it("leaves the cards what the chrome does not use", () => {
    expect(cardsBudget(500, 120)).toBe(380);
    expect(cardsBudget(undefined, 120)).toBeUndefined();
    expect(cardsBudget(100, 120)).toBe(0);
  });
});


describe("the journey draws one card form on every station", () => {
  const rem = 16;
  it("uses full cards everywhere when every station's cards fit", () => {
    const layouts = boardLayouts([1, 2, 0, 1], [300, 300, 150, 300], 600, 120, rem, "columns");
    expect(layouts.map((layout) => layout.density)).toEqual(["full", "full", "full", "full"]);
  });
  it("makes every station compact when one station's cards do not fit as full cards", () => {
    const layouts = boardLayouts([1, 2, 30, 1], [300, 300, 400, 300], 400, 120, rem, "columns");
    expect(layouts.map((layout) => layout.density)).toEqual(["compact", "compact", "compact", "compact"]);
    expect(layouts[2].columns).toBeGreaterThan(1);
  });
  it("stays full when stacked", () => {
    expect(boardLayouts([50, 1], [300, 300], 400, 120, rem, "stacked").map((layout) => layout.density)).toEqual(["full", "full"]);
  });
});
