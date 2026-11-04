import { describe, expect, it } from "vitest";
import { countdown, formatCountdown, updatedAgo } from "./countdown";

describe("how long until something runs", () => {
  it.each([
    [0, "now"],
    [-5000, "now"],
    [1, "0:01"],
    [59_000, "0:59"],
    [12 * 60_000 + 4000, "12:04"],
    [3_599_000, "59:59"],
    [3_600_000, "1:00:00"],
    [3_600_000 + 5 * 60_000 + 30_000, "1:05:30"],
    [86_399_000, "23:59:59"],
    [86_400_000, "1 day"],
    [86_400_000 + 3 * 3_600_000, "1 day 3 h"],
    [2 * 86_400_000 + 3 * 3_600_000, "2 days 3 h"],
    [2 * 86_400_000 + 59 * 60_000, "2 days"]
  ])("%i ms left reads %s", (ms, text) => {
    expect(formatCountdown(ms)).toBe(text);
  });

  it("says nothing when no run is planned, or when the time is unreadable", () => {
    expect(countdown(null, null, 0)).toBeNull();
    expect(countdown(undefined, "2026-11-04T10:00:00Z", 0)).toBeNull();
    expect(countdown("not a time", null, 0)).toBeNull();
  });

  it("measures how far through the gap between two runs we are", () => {
    const now = Date.parse("2026-11-04T10:30:00Z");
    const result = countdown("2026-11-04T11:00:00Z", "2026-11-04T10:00:00Z", now);
    expect(result).toEqual({ remainingMs: 30 * 60_000, due: false, fraction: 0.5, text: "30:00" });
  });

  it("has no fraction when the last run is unknown, unreadable or not before the next", () => {
    const now = Date.parse("2026-11-04T10:30:00Z");
    expect(countdown("2026-11-04T11:00:00Z", null, now)?.fraction).toBeNull();
    expect(countdown("2026-11-04T11:00:00Z", "garbage", now)?.fraction).toBeNull();
    expect(countdown("2026-11-04T11:00:00Z", "2026-11-04T11:00:00Z", now)?.fraction).toBeNull();
    expect(countdown("2026-11-04T11:00:00Z", "2026-11-04T12:00:00Z", now)?.fraction).toBeNull();
  });

  it("keeps the fraction between nothing and all of it, and is due once the time has come", () => {
    const next = "2026-11-04T11:00:00Z";
    const last = "2026-11-04T10:00:00Z";
    expect(countdown(next, last, Date.parse("2026-11-04T09:00:00Z"))?.fraction).toBe(0);
    const late = countdown(next, last, Date.parse("2026-11-04T11:30:00Z"));
    expect(late).toEqual({ remainingMs: 0, due: true, fraction: 1, text: "now" });
    expect(countdown(next, last, Date.parse(next))?.due).toBe(true);
  });
});

describe("how long ago a card was updated", () => {
  it.each([
    [-3000, "updated just now"],
    [0, "updated just now"],
    [4999, "updated just now"],
    [5000, "updated 5 s ago"],
    [89_000, "updated 89 s ago"],
    [90_000, "updated 2 min ago"],
    [89 * 60_000, "updated 89 min ago"],
    [90 * 60_000, "updated 2 h ago"],
    [5 * 3_600_000, "updated 5 h ago"]
  ])("%i ms ago reads %s", (age, text) => {
    expect(updatedAgo(1_000_000, 1_000_000 + age)).toBe(text);
  });
});
