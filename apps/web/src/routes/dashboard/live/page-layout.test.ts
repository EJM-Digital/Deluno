import { describe, expect, it } from "vitest";
import { cardsBudget, gridRows, JOURNEY_PX, GRID_AREAS, GRID_COLUMNS, LOW_COLUMNS, pageLayout, stackedJourneyBudget } from "./layout";

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
    // 1568x765 inside the app: the grid has 651px. The band and the journey keep their least, the bottom row its 180px.
    // (The mockup's journey least is 314px, 10px short of three rows and their line; the app counts the real chrome: 324px.)
    expect(gridRows(651, 651)).toEqual({ band: 150, journey: 324, low: 180, template: "150px 324px 180px" });
    // A window with room for the band's 20% (156px), the journey's 398px and the bottom row's 198px.
    expect(gridRows(780, 780)).toEqual({ band: 156, journey: 398, low: 198, template: "156px 398px 198px" });
    // A tall window: spare height goes to the band (up to 240px), the bottom row takes a third at most, and the journey's cards grow.
    expect(gridRows(1100, 1100)).toEqual({ band: 240, journey: 478, low: 354, template: "240px 478px 354px" });
  });
  it("gives way band first (never under 150px), then the journey (never under what three rows of 60px cards need)", () => {
    expect(JOURNEY_PX).toBe(398);
    expect(gridRows(700, 700).band).toBeGreaterThanOrEqual(150);
    expect(gridRows(300, 300)).toMatchObject({ band: 150, journey: 106 + 2 * 68 + 60 + 22, low: 180 });
    expect(gridRows(2000, 2000).journey).toBe(JOURNEY_PX + 3 * (130 - 94));
  });
  it("reads only the grid's own height, so Live and System, which share it, always agree", () => {
    expect(gridRows(1000, 780)).toEqual(gridRows(780, 780));
    expect(gridRows(0, 780)).toEqual(gridRows(780, 780));
  });
  it("uses a roomy window until measured", () => {
    expect(gridRows(0)).toMatchObject({ band: 240, journey: 411, low: 321 });
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
