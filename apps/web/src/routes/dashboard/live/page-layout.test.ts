import { describe, expect, it } from "vitest";
import { cardsBudget, gridRows, GRID_AREAS, GRID_COLUMNS, LOW_COLUMNS, pageLayout, stackedJourneyBudget } from "./layout";

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
  it("gives the band 20% of the height within 150 to 200px and the bottom row 27% within 180 to 300px, the journey the rest", () => {
    expect(gridRows(1000)).toEqual({ band: 200, low: 270, template: "200px minmax(0, 1fr) 270px" });
    expect(gridRows(600)).toEqual({ band: 150, low: 180, template: "150px minmax(0, 1fr) 180px" });
    expect(gridRows(2000)).toMatchObject({ band: 200, low: 300 });
    expect(gridRows(700)).toMatchObject({ band: 150, low: 189 });
  });
  it("gives way together when the grid is too short, so the journey keeps a share", () => {
    const short = gridRows(1080, 400);
    expect(short.band + short.low).toBeLessThanOrEqual(160);
    expect(gridRows(1080, 900)).toMatchObject({ band: 200, low: 292 });
  });
  it("uses a roomy window until measured", () => {
    expect(gridRows(0)).toMatchObject({ band: 200, low: 270 });
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
