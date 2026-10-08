import { describe, expect, it } from "vitest";
import { BAND_MIN_PX, GRID_MIN_PX, cardsBudget, gridRows, JOURNEY_PX, GRID_AREAS, GRID_COLUMNS, LOW_COLUMNS, pageLayout, stackedJourneyBudget } from "./layout";

describe("page layout from the measured main area", () => {
  it("puts the right column beside the page from about 1056px of main area", () => {
    expect(pageLayout(1055, 16).sideBySide).toBe(false);
    expect(pageLayout(1056, 16).sideBySide).toBe(true);
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

describe("the shared page grid", () => {
  it("is the mockup's: Sources spans the band and the journey, Turned down today sits beside the bottom row", () => {
    expect(GRID_COLUMNS).toBe("minmax(0, 1fr) clamp(340px, 28%, 600px)");
    expect(GRID_AREAS).toBe('"now src" "board src" "low act"');
    expect(LOW_COLUMNS).toBe("minmax(0, 1.15fr) minmax(0, 1fr)");
  });
  it("is the mockup's fitRows exactly, at the three sizes it was approved at (the heights are the grid's)", () => {
    // 1568x765 inside the app: the grid has 651px. The band (never under 144px) and the journey keep their least and the bottom row gives way to the 159px left (never under 110px), so nothing is cut off.
    // (The mockup's journey least is 314px, 10px short of three rows and their line; the app counts the real chrome: 324px.)
    expect(gridRows(651, 651)).toEqual({ band: 144, journey: 324, low: 159, template: "144px 324px 159px" });
    // A window with room for the band's 20% (156px), the journey's 398px and the bottom row's 202px (the gaps between cards are 12px).
    expect(gridRows(780, 780)).toEqual({ band: 156, journey: 398, low: 202, template: "156px 398px 202px" });
    // A tall window: spare height goes to the band (up to 240px), the bottom row takes a third at most, and the journey's cards grow.
    expect(gridRows(1100, 1100)).toEqual({ band: 240, journey: 481, low: 355, template: "240px 481px 355px" });
  });
  it("gives way band first (never under 144px, Weir's, so the band tiles keep their rows), then the journey (never under what three rows of 60px cards need)", () => {
    expect(JOURNEY_PX).toBe(398);
    expect(gridRows(700, 700).band).toBeGreaterThanOrEqual(144);
    expect(BAND_MIN_PX).toBe(144);
    expect(gridRows(300, 300)).toMatchObject({ band: 144, journey: 106 + 2 * 68 + 60 + 22, low: 110 });
    expect(gridRows(2000, 2000).journey).toBe(JOURNEY_PX + 3 * (130 - 94));
  });
  it("reads only the grid's own height, so Live and System, which share it, always agree", () => {
    expect(gridRows(1000, 780)).toEqual(gridRows(780, 780));
    expect(gridRows(0, 780)).toEqual(gridRows(780, 780));
  });
  it("uses a roomy window until measured", () => {
    expect(gridRows(0)).toMatchObject({ band: 240, journey: 414, low: 322 });
  });
  it("lets the stacked journey be at most most of a window tall", () => {
    expect(stackedJourneyBudget(1000)).toBe(700);
    expect(stackedJourneyBudget(0)).toBeUndefined();
  });
  it("leaves the cards what the chrome does not use", () => {
    expect(cardsBudget(500, 120)).toBe(380);
    expect(cardsBudget(undefined, 120)).toBeUndefined();
    expect(cardsBudget(100, 120)).toBe(0);
  });
});

describe("the least height the shared grid is drawn in", () => {
  it("is exactly what the three rows and their gaps take at their smallest, so a shorter tab scrolls instead of overlapping", () => {
    const rows = gridRows(GRID_MIN_PX, GRID_MIN_PX);
    expect(rows.band + rows.journey + rows.low + 24).toBe(GRID_MIN_PX);
    expect(rows.band).toBe(BAND_MIN_PX);
  });
});
