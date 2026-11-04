import { describe, expect, it } from "vitest";
import type { ReleasesTurnedDownEvent } from "../../../lib/api/types/live-events";
import type { LiveTurnedDown } from "../../../lib/api/types/operations";
import { barFraction, LATEST_LIMIT, mergeTurnedDown, REASON_COLOUR, REASON_LABEL, REASON_ORDER, reasonsFit } from "./turned-down";

const event = (overrides: Partial<ReleasesTurnedDownEvent> = {}): ReleasesTurnedDownEvent =>
  ({ title: "Dune", entityType: "movie", entityId: "m1", atUtc: "2026-11-04T10:00:00Z", items: [{ release: "Dune.1080p", group: "tooBig", label: "Bigger than your size limit" }], ...overrides }) as ReleasesTurnedDownEvent;
const base = (overrides: Partial<LiveTurnedDown> = {}): LiveTurnedDown => ({
  total: 5,
  reasons: [{ group: "language", label: "Wrong language", count: 3 }, { group: "tooBig", label: "Too big", count: 2 }],
  latest: [{ release: "Old.1080p", group: "language", label: "Wrong language", atUtc: "2026-11-04T08:00:00Z" }],
  ...overrides
});

describe("the reasons and their words", () => {
  it("lists eleven reasons, each with words and a colour, with other last", () => {
    expect(REASON_ORDER).toHaveLength(11);
    expect(REASON_ORDER[REASON_ORDER.length - 1]).toBe("other");
    for (const group of REASON_ORDER) {
      expect(REASON_LABEL[group].length).toBeGreaterThan(5);
      expect(REASON_COLOUR[group]).toMatch(/^hsl\(var\(--/);
    }
    expect(LATEST_LIMIT).toBe(20);
  });
});

describe("the stored counts with the pushed turn-downs on top", () => {
  it("starts empty with nothing stored and nothing pushed, with every reason at zero in the fixed order", () => {
    const view = mergeTurnedDown(null, [], null);
    expect(view.total).toBe(0);
    expect(view.latest).toEqual([]);
    expect(view.reasons.map((r) => [r.group, r.label, r.count])).toEqual(REASON_ORDER.map((group) => [group, REASON_LABEL[group], 0]));
  });

  it("starts from the stored total and counts, putting the biggest reasons first and keeping the server's words", () => {
    const view = mergeTurnedDown(base(), [], null);
    expect(view.total).toBe(5);
    expect(view.reasons.slice(0, 2)).toEqual([{ group: "language", label: "Wrong language", count: 3 }, { group: "tooBig", label: "Too big", count: 2 }]);
    expect(view.reasons[2].count).toBe(0);
    expect(view.latest).toHaveLength(1);
  });

  it("adds a release pushed after the stored counts, with the event's title and place, newest first", () => {
    const view = mergeTurnedDown(base(), [event()], "2026-11-04T09:00:00Z");
    expect(view.total).toBe(6);
    expect(view.reasons.find((r) => r.group === "tooBig")?.count).toBe(3);
    expect(view.latest[0]).toEqual({ release: "Dune.1080p", group: "tooBig", label: "Bigger than your size limit", title: "Dune", entityType: "movie", entityId: "m1", atUtc: "2026-11-04T10:00:00Z" });
    expect(view.latest[1].release).toBe("Old.1080p");
  });

  it("does not count a push that is no newer than the stored counts, even by a second", () => {
    expect(mergeTurnedDown(base(), [event({ atUtc: "2026-11-04T09:00:00Z" })], "2026-11-04T09:00:00Z").total).toBe(5);
    expect(mergeTurnedDown(base(), [event({ atUtc: "2026-11-04T09:00:01Z" })], "2026-11-04T09:00:00Z").total).toBe(6);
  });

  it("counts every push when the stored counts have no time, or the push has none that can be read", () => {
    expect(mergeTurnedDown(null, [event()], null).total).toBe(1);
    expect(mergeTurnedDown(null, [event()], "garbage").total).toBe(1);
    expect(mergeTurnedDown(base(), [event({ atUtc: "garbage" })], "2026-11-04T09:00:00Z").total).toBe(6);
  });

  it("counts a release in a reason it does not know towards the total, without making a bar for it", () => {
    const view = mergeTurnedDown(null, [event({ items: [{ release: "A", group: "brandNew" as never, label: "Brand new" }] })], null);
    expect(view.total).toBe(1);
    expect(view.reasons.reduce((sum, r) => sum + r.count, 0)).toBe(0);
    expect(view.latest[0].label).toBe("Brand new");
  });

  it("does not count a release that was already counted today, and counts one marked new or unmarked", () => {
    const items = [
      { release: "A", group: "tooBig" as const, label: "x", newToday: false },
      { release: "B", group: "tooBig" as const, label: "x", newToday: true },
      { release: "C", group: "fake" as const, label: "x" }
    ];
    const view = mergeTurnedDown(null, [event({ items })], null);
    expect(view.total).toBe(2);
    expect(view.latest.map((r) => r.release)).toEqual(["C", "B"]);
  });

  it("uses the reason's own words when the push gave none", () => {
    const view = mergeTurnedDown(null, [event({ items: [{ release: "A", group: "fake", label: "" }] })], null);
    expect(view.latest[0].label).toBe("Fake or mislabelled");
  });

  it("keeps a series and leaves the place empty for anything else or nothing", () => {
    expect(mergeTurnedDown(null, [event({ entityType: "series" })], null).latest[0].entityType).toBe("series");
    expect(mergeTurnedDown(null, [event({ entityType: "episode" })], null).latest[0].entityType).toBeNull();
    const bare = mergeTurnedDown(null, [event({ title: null, entityType: null, entityId: null })], null).latest[0];
    expect(bare).toMatchObject({ title: null, entityType: null, entityId: null });
  });

  it("keeps the newest twenty releases only, the pushed ones ahead of the stored ones", () => {
    const items = Array.from({ length: 25 }, (_, n) => ({ release: `R${n}`, group: "other" as const, label: "x" }));
    const view = mergeTurnedDown(base(), [event({ items })], null);
    expect(view.latest).toHaveLength(20);
    expect(view.latest[0].release).toBe("R24");
    expect(view.latest[19].release).toBe("R5");
    expect(view.total).toBe(30);
  });

  it("puts a reason with the biggest count first and keeps the fixed order between equal counts", () => {
    const view = mergeTurnedDown(null, [event({ items: [{ release: "A", group: "fake", label: "x" }, { release: "B", group: "language", label: "x" }, { release: "C", group: "fake", label: "x" }] })], null);
    expect(view.reasons.slice(0, 2).map((r) => r.group)).toEqual(["fake", "language"]);
    expect(view.reasons.slice(2).map((r) => r.group)).toEqual(REASON_ORDER.filter((g) => g !== "fake" && g !== "language"));
  });
});

describe("the width of a reason's bar", () => {
  it("is full for the biggest reason and in proportion for the rest", () => {
    const reasons = [{ count: 10 }, { count: 4 }, { count: 0 }];
    expect(barFraction(10, reasons)).toBe(1);
    expect(barFraction(4, reasons)).toBe(0.4);
    expect(barFraction(0, reasons)).toBe(0);
  });

  it("is never more than full, and nothing when nothing is counted", () => {
    expect(barFraction(20, [{ count: 10 }])).toBe(1);
    expect(barFraction(5, [{ count: 0 }])).toBe(0);
    expect(barFraction(5, [])).toBe(0);
  });
});

describe("how many reason bars the card holds", () => {
  it("shows every reason before the card has been measured", () => {
    expect(reasonsFit(0, 20, 11)).toBe(11);
    expect(reasonsFit(300, 0, 11)).toBe(11);
    expect(reasonsFit(50, 0, 11)).toBe(11);
    expect(reasonsFit(Number.NaN, 20, 11)).toBe(11);
    expect(reasonsFit(300, Number.NaN, 11)).toBe(11);
    expect(reasonsFit(-5, 20, 11)).toBe(11);
  });

  it("shows them all when they all fit, down to the last pixel", () => {
    // 11 bars of 20px with 8px gaps: 11 * 28 = 308, less the 8px after the last, plus 14px of padding = 314.
    expect(reasonsFit(314, 20, 11)).toBe(11);
    expect(reasonsFit(313, 20, 11)).toBeLessThan(11);
  });

  it("holds as many as fit above the and-more line, and never fewer than one", () => {
    expect(reasonsFit(150, 20, 11)).toBe(Math.floor((150 - 14 - 24 + 8) / 28));
    expect(reasonsFit(150, 20, 11)).toBe(4);
    expect(reasonsFit(40, 20, 11)).toBe(1);
  });

  it("never holds more than there are", () => {
    expect(reasonsFit(1000, 20, 3)).toBe(3);
    expect(reasonsFit(60, 20, 1)).toBe(1);
  });
});
