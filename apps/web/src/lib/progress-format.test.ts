import { describe, expect, it } from "vitest";
import { floorProgress, formatProgressPercent } from "./progress-format";

describe("formatProgressPercent (#1236)", () => {
  it("floors a download that is nearly done and never calls it 100%", () => {
    expect(formatProgressPercent(99.9916)).toBe("99.9%");
    expect(formatProgressPercent(99.9916, 2)).toBe("99.99%");
    expect(formatProgressPercent(99.99999999999)).toBe("99.9%");
    expect(formatProgressPercent(99.9999999999, 2)).toBe("99.99%");
    expect(formatProgressPercent(99.96)).toBe("99.9%");
  });

  it("says 100% only for a download that is complete", () => {
    expect(formatProgressPercent(100)).toBe("100%");
    expect(formatProgressPercent(100.0000001)).toBe("100%");
    expect(formatProgressPercent(250)).toBe("100%");
  });

  it("floors, not rounds, below 99 and keeps exact values exact", () => {
    expect(formatProgressPercent(42.99)).toBe("42%");
    expect(formatProgressPercent(98.99)).toBe("98%");
    expect(formatProgressPercent(29)).toBe("29%");
    expect(formatProgressPercent(0.29 * 100)).toBe("29%");
    expect(formatProgressPercent(99)).toBe("99%");
    expect(formatProgressPercent(99.05)).toBe("99%");
    expect(formatProgressPercent(99.5)).toBe("99.5%");
    expect(formatProgressPercent(0)).toBe("0%");
  });

  it("reads a missing or broken value as 0%", () => {
    expect(formatProgressPercent(null)).toBe("0%");
    expect(formatProgressPercent(undefined)).toBe("0%");
    expect(formatProgressPercent(Number.NaN)).toBe("0%");
    expect(formatProgressPercent(-3)).toBe("0%");
    expect(floorProgress(99.9916)).toBe(99.9);
  });
});
