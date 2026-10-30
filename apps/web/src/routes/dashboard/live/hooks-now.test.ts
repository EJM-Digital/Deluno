import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { formatCountdown } from "./countdown";
import { msToNextSecond, useNow } from "./hooks";

describe("useNow shows every second (#1443)", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it("waits for just past the next whole second", () => {
    expect(msToNextSecond(10_000)).toBe(1005);
    expect(msToNextSecond(10_999)).toBe(6);
  });

  it("never skips a second of a countdown, even when ticks run late", () => {
    vi.setSystemTime(new Date(1_000_000_000_400));
    const target = 1_000_000_000_400 + 20_000;
    const { result } = renderHook(() => useNow(1000));
    const seen: string[] = [formatCountdown(target - result.current)];
    for (let i = 0; i < 12; i++) {
      // A late stretch now and then: the page was busy for 300 ms.
      if (i % 4 === 2) act(() => { vi.advanceTimersByTime(0); });
      act(() => { vi.advanceTimersByTime(400); vi.advanceTimersByTime(600); });
      seen.push(formatCountdown(target - result.current));
    }
    const seconds = seen.map((text) => { const [m, s] = text.split(":").map(Number); return m * 60 + s; });
    for (let i = 1; i < seconds.length; i++) expect(seconds[i - 1] - seconds[i]).toBeLessThanOrEqual(1);
  });

  it("catches up to the real time when the tab comes back", () => {
    vi.setSystemTime(new Date(1_000_000_000_000));
    const { result } = renderHook(() => useNow(1000));
    const before = result.current;
    vi.setSystemTime(new Date(1_000_000_000_000 + 90_000));
    act(() => { document.dispatchEvent(new Event("visibilitychange")); });
    expect(result.current - before).toBe(90_000);
  });
});
