import { afterEach, describe, expect, it, vi } from "vitest";
import {
  DEFAULT_HISTORY_DAYS,
  HISTORY_RANGES,
  HISTORY_STORAGE_KEY,
  isHistoryDays,
  readStoredHistoryDays,
  windowLabel,
  writeStoredHistoryDays
} from "./dashboard-history-range";

afterEach(() => {
  window.localStorage.clear();
  vi.restoreAllMocks();
});

describe("dashboard history range", () => {
  it("offers the four windows the control renders", () => {
    expect(HISTORY_RANGES.map((range) => range.value)).toEqual(["7", "30", "90", "365"]);
    expect(HISTORY_RANGES.map((range) => range.label)).toEqual(["7d", "30d", "90d", "1y"]);
  });

  it("defaults to the window the route loader fetches", () => {
    // The loader requests days=30 and seeds the query with it. If these ever
    // disagree the first paint shows one window's data under another's label.
    expect(DEFAULT_HISTORY_DAYS).toBe("30");
    expect(readStoredHistoryDays()).toBe("30");
  });

  it("round-trips a stored choice", () => {
    writeStoredHistoryDays("90");
    expect(window.localStorage.getItem(HISTORY_STORAGE_KEY)).toBe("90");
    expect(readStoredHistoryDays()).toBe("90");
  });

  it("falls back to the default when the stored value is not a window we offer", () => {
    window.localStorage.setItem(HISTORY_STORAGE_KEY, "45");
    expect(readStoredHistoryDays()).toBe(DEFAULT_HISTORY_DAYS);

    window.localStorage.setItem(HISTORY_STORAGE_KEY, "");
    expect(readStoredHistoryDays()).toBe(DEFAULT_HISTORY_DAYS);
  });

  it("survives storage being unavailable", () => {
    // Private mode throws on both reads and writes rather than returning null.
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });

    expect(readStoredHistoryDays()).toBe(DEFAULT_HISTORY_DAYS);
    expect(() => writeStoredHistoryDays("7")).not.toThrow();
  });

  it("recognises only the windows it offers", () => {
    expect(isHistoryDays("30")).toBe(true);
    expect(isHistoryDays(30)).toBe(false);
    expect(isHistoryDays("31")).toBe(false);
    expect(isHistoryDays(null)).toBe(false);
  });

  describe("subtitle wording", () => {
    it("writes each offered window the way a person would say it", () => {
      expect(windowLabel(7)).toBe("the last week");
      expect(windowLabel(30)).toBe("the last 30 days");
      expect(windowLabel(90)).toBe("the last 90 days");
      expect(windowLabel(365)).toBe("the last year");
    });

    it("reports a clamped window rather than the one that was asked for", () => {
      // The endpoint clamps to 1..3650, so the response can name a window the
      // control never offered. The subtitle has to be able to write it.
      expect(windowLabel(1)).toBe("the last day");
      expect(windowLabel(3650)).toBe("the last 10 years");
    });
  });
});
