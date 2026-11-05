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
    // A tall window: the band is a fifth (220px), and the journey's cards grow while the bottom row keeps a third of the 1076px left (355px).
    expect(gridRows(1100, 1100)).toEqual({ band: 220, journey: 501, low: 355, template: "220px 501px 355px" });
  });
  it("gives way band first (never under 144px, Weir's, so the band tiles keep their rows), then the journey (never under what three rows of 60px cards need)", () => {
    expect(JOURNEY_PX).toBe(398);
    expect(gridRows(700, 700).band).toBeGreaterThanOrEqual(144);
    expect(BAND_MIN_PX).toBe(144);
    expect(gridRows(300, 300)).toMatchObject({ band: 144, journey: 106 + 2 * 68 + 60 + 22, low: 110 });
    expect(gridRows(2000, 2000).journey).toBe(JOURNEY_PX + 3 * (130 - 94));
  });
  it("is Weir's rule, which depends only on the grid's height: 226 / 506 / 376 at 1132", () => {
    // avail 1108; band round(226.4) = 226; journey 398; low 484 keeps max(260, round(365.6) = 366), so the journey grows by 108 (its cap) to 506; low 376.
    expect(gridRows(1132, 1132)).toEqual({ band: 226, journey: 506, low: 376, template: "226px 506px 376px" });
  });
  it("takes the height it is given when no separate available height is, and assumes 1000 when it has neither", () => {
    expect(gridRows(800)).toEqual(gridRows(800, 800));
    expect(gridRows(0)).toEqual(gridRows(1000, 1000));
  });
  it("draws the same rows whatever the bottom row's content would like", () => {
    const rule = gridRows as (...args: unknown[]) => ReturnType<typeof gridRows>;
    for (const need of [0, 180, 323, 500, 900]) expect(rule(1132, 1132, { lowNeed: need })).toEqual(gridRows(1132, 1132));
  });
  it("follows the rule at heights from 300 to 2000, worked out by hand", () => {
    // 300: avail 276, nothing fits, so the band is 144, the journey its least 324 and the bottom row its floor 110.
    expect(gridRows(300, 300).template).toBe("144px 324px 110px");
    // 630: avail 606; band 144 (a fifth is 126); the journey gives way to 324; the bottom row has 138.
    expect(gridRows(630, 630).template).toBe("144px 324px 138px");
    // 676: avail 652; band 144; journey 652 - 144 - 180 = 328; the bottom row keeps 180.
    expect(gridRows(676, 676).template).toBe("144px 328px 180px");
    // 800: avail 776; band 160; journey 398; low 218 is under the 260 it keeps, so the journey does not grow.
    expect(gridRows(800, 800).template).toBe("160px 398px 218px");
    // 900: avail 876; band 180; low 298 keeps max(260, 289) = 289, so the journey grows by 9 to 407.
    expect(gridRows(900, 900).template).toBe("180px 407px 289px");
    // 1080: avail 1056; band 216; low 442 keeps 348; the journey grows by 94 to 492.
    expect(gridRows(1080, 1080).template).toBe("216px 492px 348px");
    // 1183: avail 1159; band 237; low 524 keeps 382; 142 spare, so the journey is at its cap, 506.
    expect(gridRows(1183, 1183).template).toBe("237px 506px 416px");
    // 1250: the band's fifth is 250, capped at 240; avail 1226; journey capped at 506; low 480.
    expect(gridRows(1250, 1250).template).toBe("240px 506px 480px");
    // 2000: band 240, journey 506, the bottom row takes the rest: 1976 - 746 = 1230.
    expect(gridRows(2000, 2000).template).toBe("240px 506px 1230px");
  });
  it("never gives the band spare height: it stays a fifth of the grid, up to 240px", () => {
    expect(gridRows(1200, 1200).band).toBe(240);
    expect(gridRows(3000, 3000).band).toBe(240);
  });
  it("reads only the grid's own height, so Live and System, which share it, always agree", () => {
    expect(gridRows(1000, 780)).toEqual(gridRows(780, 780));
    expect(gridRows(0, 780)).toEqual(gridRows(780, 780));
  });
  it("uses a roomy window until measured", () => {
    expect(gridRows(0)).toEqual({ band: 200, journey: 454, low: 322, template: "200px 454px 322px" });
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
