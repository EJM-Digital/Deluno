import { describe, expect, it } from "vitest";
import { ago, bytesText, percent, speedText, total, until, uptimeText } from "./format";

const NOW = Date.parse("2026-10-01T12:00:00Z");
const at = (ms: number) => new Date(NOW + ms).toISOString();
const MIN = 60_000;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;

describe("the Dashboard's wording helpers", () => {
  it("says how long ago, in the unit that reads best, and never for a missing or unreadable time", () => {
    expect(ago(null, NOW)).toBe("never");
    expect(ago(undefined, NOW)).toBe("never");
    expect(ago("not a time", NOW)).toBe("never");
    expect(ago(at(-10_000), NOW)).toBe("just now");
    expect(ago(at(5_000), NOW)).toBe("just now");
    expect(ago(at(-5 * MIN), NOW)).toBe("5 min ago");
    expect(ago(at(-3 * HOUR), NOW)).toBe("3 h ago");
    expect(ago(at(-DAY), NOW)).toBe("1 day ago");
    expect(ago(at(-4 * DAY), NOW)).toBe("4 days ago");
  });

  it("says how long until, and when nothing is scheduled", () => {
    expect(until(null, NOW)).toBe("not scheduled");
    expect(until("nonsense", NOW)).toBe("not scheduled");
    expect(until(at(10_000), NOW)).toBe("any moment");
    expect(until(at(-5 * MIN), NOW)).toBe("any moment");
    expect(until(at(15 * MIN), NOW)).toBe("in 15 min");
    expect(until(at(15 * HOUR), NOW)).toBe("in 15 h");
    expect(until(at(DAY), NOW)).toBe("in 1 day");
    expect(until(at(2 * DAY), NOW)).toBe("in 2 days");
  });

  it("reads uptime as just started, minutes, hours with minutes, or days", () => {
    expect(uptimeText(Number.NaN)).toBe("just started");
    expect(uptimeText(59)).toBe("just started");
    expect(uptimeText(5 * 60)).toBe("5 min");
    expect(uptimeText(3 * 3600 + 7 * 60)).toBe("3 h 7 min");
    expect(uptimeText(50 * 3600)).toBe("2 days");
  });

  it("writes sizes, saying unknown when there is none", () => {
    expect(bytesText(null)).toBe("unknown");
    expect(bytesText(undefined)).toBe("unknown");
    expect(bytesText(Number.POSITIVE_INFINITY)).toBe("unknown");
    expect(bytesText(0)).not.toBe("unknown");
  });

  it("writes speeds with a decimal under ten and whole numbers from ten, never negative or NaN", () => {
    expect(speedText(2.46)).toBe("2.5 MB/s");
    expect(speedText(12.4)).toBe("12 MB/s");
    expect(speedText(-3)).toBe("0.0 MB/s");
    expect(speedText(Number.NaN)).toBe("0.0 MB/s");
  });

  it("adds up a record of counts and a missing one is nothing", () => {
    expect(total({ a: 2, b: 3 })).toBe(5);
    expect(total(null)).toBe(0);
    expect(total(undefined)).toBe(0);
  });

  it("gives a whole-number percentage, and zero when there is no whole", () => {
    expect(percent(1, 3)).toBe(33);
    expect(percent(5, 0)).toBe(0);
  });
});
