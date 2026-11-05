import { describe, expect, it } from "vitest";
import { ago, formatEta, plainWords, until } from "./downloads-kit";

const NOW = Date.parse("2026-11-05T05:00:00Z");
const before = (seconds: number) => new Date(NOW - seconds * 1000).toISOString();
const after = (seconds: number) => new Date(NOW + seconds * 1000).toISOString();

describe("how a time past is worded", () => {
  it("is empty for a missing or unreadable time", () => {
    expect(ago(null, NOW)).toBe("");
    expect(ago(undefined, NOW)).toBe("");
    expect(ago("", NOW)).toBe("");
    expect(ago("not a time", NOW)).toBe("");
  });

  it("says just now for the last 45 seconds and for a time that has not come yet", () => {
    expect(ago(before(10), NOW)).toBe("just now");
    expect(ago(before(44), NOW)).toBe("just now");
    expect(ago(after(30), NOW)).toBe("just now");
  });

  it("counts minutes, then hours, then yesterday, then days", () => {
    expect(ago(before(60), NOW)).toBe("1 min ago");
    expect(ago(before(59 * 60), NOW)).toBe("59 min ago");
    expect(ago(before(3 * 3600), NOW)).toBe("3 h ago");
    expect(ago(before(30 * 3600), NOW)).toBe("yesterday");
    expect(ago(before(3 * 86400), NOW)).toBe("3 days ago");
  });

  it("reads the clock itself when it is given no time to compare with", () => {
    expect(ago(new Date().toISOString())).toBe("just now");
  });
});

describe("how a time to come is worded", () => {
  it("is empty for a missing or unreadable time", () => {
    expect(until(null, NOW)).toBe("");
    expect(until("", NOW)).toBe("");
    expect(until("not a time", NOW)).toBe("");
  });

  it("says due now for a time already past", () => {
    expect(until(before(5), NOW)).toBe("due now");
    expect(until(after(0), NOW)).toBe("due now");
  });

  it("counts seconds, minutes, hours, then days", () => {
    expect(until(after(14), NOW)).toBe("in 14 s");
    expect(until(after(5 * 60), NOW)).toBe("in 5 min");
    expect(until(after(3 * 3600), NOW)).toBe("in 3 h");
    expect(until(after(3 * 86400), NOW)).toBe("in 3 days");
  });

  it("reads the clock itself when it is given no time to compare with", () => {
    expect(until(new Date(Date.now() + 40_000).toISOString())).toMatch(/^in (39|40|41) s$/);
  });
});

describe("how the time left on a download is worded", () => {
  it("is a dash when the client does not say", () => {
    for (const value of [null, undefined, 0, -5, Number.NaN, Number.POSITIVE_INFINITY]) expect(formatEta(value)).toBe("–");
  });

  it("reads under a minute, minutes, hours with or without minutes, then days", () => {
    expect(formatEta(20)).toBe("under 1 min");
    expect(formatEta(9 * 60)).toBe("9 min");
    expect(formatEta(81 * 60)).toBe("1 h 21 min");
    expect(formatEta(2 * 3600)).toBe("2 h");
    expect(formatEta(50 * 3600)).toBe("2 days");
  });
});

describe("how an unfamiliar code is worded", () => {
  it("uses the fallback for nothing, and for a code with no letters in it", () => {
    expect(plainWords(null, "Something")).toBe("Something");
    expect(plainWords("", "Something")).toBe("Something");
    expect(plainWords("---", "Something")).toBe("Something");
  });

  it("turns separators and camel case into a plain phrase", () => {
    expect(plainWords("rate-limited", "x")).toBe("Rate limited");
    expect(plainWords("tooSlow", "x")).toBe("Too slow");
    expect(plainWords("no_peers.found", "x")).toBe("No peers found");
  });
});
