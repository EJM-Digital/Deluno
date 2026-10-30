import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useCoalescedRevalidate, useLiveInterval } from "./use-live-interval";

function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => state });
  document.dispatchEvent(new Event("visibilitychange"));
}

describe("useLiveInterval (#641)", () => {
  beforeEach(() => { vi.useFakeTimers(); setVisibility("visible"); });
  afterEach(() => { vi.useRealTimers(); setVisibility("visible"); });

  it("keeps refreshing while the tab is hidden", () => {
    const callback = vi.fn();
    renderHook(() => useLiveInterval(callback, 1_000));
    setVisibility("hidden");
    act(() => { vi.advanceTimersByTime(3_000); });
    expect(callback).toHaveBeenCalledTimes(3);
  });

  it("makes no catch-up call when the tab comes back", () => {
    const callback = vi.fn();
    renderHook(() => useLiveInterval(callback, 1_000));
    setVisibility("hidden");
    act(() => { vi.advanceTimersByTime(500); });
    setVisibility("visible");
    expect(callback).not.toHaveBeenCalled();
  });

  it("stops when unmounted", () => {
    const callback = vi.fn();
    const { unmount } = renderHook(() => useLiveInterval(callback, 1_000));
    unmount();
    act(() => { vi.advanceTimersByTime(5_000); });
    expect(callback).not.toHaveBeenCalled();
  });
});

describe("useCoalescedRevalidate (#641)", () => {
  beforeEach(() => { vi.useFakeTimers(); setVisibility("visible"); });
  afterEach(() => { vi.useRealTimers(); setVisibility("visible"); });

  it("turns a burst into one refresh", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 1_000));
    act(() => { for (let i = 0; i < 20; i++) result.current(); });
    act(() => { vi.advanceTimersByTime(1_000); });
    expect(callback).toHaveBeenCalledTimes(1);
  });

  it("still refreshes during a stream that never goes quiet", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 1_000));
    for (let i = 0; i < 30; i++) {
      act(() => { result.current(); vi.advanceTimersByTime(100); });
    }
    expect(callback).toHaveBeenCalledTimes(3);
  });

  it("refreshes while hidden and owes nothing on return", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 1_000));
    setVisibility("hidden");
    act(() => { result.current(); vi.advanceTimersByTime(1_000); });
    expect(callback).toHaveBeenCalledTimes(1);
    setVisibility("visible");
    act(() => { vi.advanceTimersByTime(5_000); });
    expect(callback).toHaveBeenCalledTimes(1);
  });

  it("drops a pending refresh when unmounted", () => {
    const callback = vi.fn();
    const { result, unmount } = renderHook(() => useCoalescedRevalidate(callback, 1_000));
    act(() => { result.current(); });
    unmount();
    act(() => { vi.advanceTimersByTime(2_000); });
    expect(callback).not.toHaveBeenCalled();
  });
});

describe("useLiveInterval with no timer", () => {
  it("never calls back when the interval is false", () => {
    vi.useFakeTimers();
    const callback = vi.fn();
    renderHook(() => useLiveInterval(callback, false));
    act(() => { vi.advanceTimersByTime(10_000); });
    expect(callback).not.toHaveBeenCalled();
    vi.useRealTimers();
  });
});
