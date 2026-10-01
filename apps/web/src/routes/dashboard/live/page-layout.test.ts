import { describe, expect, it } from "vitest";
import { cardsBudget, gridRows, JOURNEY_PX, GRID_AREAS, GRID_COLUMNS, LOW_COLUMNS, pageLayout, stackedJourneyBudget } from "./layout";

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

describe("the shared page grid", () => {
  it("is the mockup's: Sources spans the band and the journey, Activity sits beside the bottom row", () => {
    expect(GRID_COLUMNS).toBe("minmax(0, 1fr) clamp(340px, 28%, 600px)");
    expect(GRID_AREAS).toBe('"now src" "board src" "low act"');
    expect(LOW_COLUMNS).toBe("minmax(0, 1fr) clamp(260px, 36%, 420px)");
  });
  it("holds the journey at three rows of cards, the band at 20% of the window within 150 to 200px, and gives the bottom row the rest", () => {
    expect(JOURNEY_PX).toBe(116 + 2 * 94 + 86 + 22);
    expect(gridRows(1000, 900)).toEqual({ band: 200, journey: JOURNEY_PX, low: 900 - 28 - 200 - JOURNEY_PX, template: `200px ${JOURNEY_PX}px minmax(0, 1fr)` });
    expect(gridRows(2000, 1300)).toMatchObject({ band: 200, journey: JOURNEY_PX });
  });
  it("on a short page gives the band way first (down to 130px), then the journey's cards shrink, keeping the bottom row 170px", () => {
    const short = gridRows(765, 690);
    expect(short.band).toBe(130);
    expect(short.low).toBeGreaterThanOrEqual(170);
    expect(short.journey).toBeLessThan(JOURNEY_PX);
    expect(gridRows(765, 300).journey).toBe(116 + 2 * 68 + 60 + 22);
  });
  it("uses a roomy window until measured", () => {
    expect(gridRows(0)).toMatchObject({ band: 200, journey: JOURNEY_PX, low: 270 });
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
