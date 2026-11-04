import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useResizing } from "./use-resizing";

describe("the resizing flag", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("is off until the window is resized, on while it moves, and off again a moment after it stops", () => {
    const { result } = renderHook(() => useResizing(150));
    expect(result.current).toBe(false);

    act(() => { window.dispatchEvent(new Event("resize")); });
    expect(result.current).toBe(true);

    act(() => { vi.advanceTimersByTime(149); });
    expect(result.current).toBe(true);

    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current).toBe(false);
  });

  it("keeps waiting while the window keeps moving, counting the moment from the last resize", () => {
    const { result } = renderHook(() => useResizing(100));

    act(() => { window.dispatchEvent(new Event("resize")); });
    act(() => { vi.advanceTimersByTime(80); });
    act(() => { window.dispatchEvent(new Event("resize")); });
    act(() => { vi.advanceTimersByTime(80); });
    expect(result.current).toBe(true);

    act(() => { vi.advanceTimersByTime(20); });
    expect(result.current).toBe(false);
  });

  it("stops listening and drops a pending wait when the screen goes away", () => {
    const removed = vi.spyOn(window, "removeEventListener");
    const { result, unmount } = renderHook(() => useResizing());
    act(() => { window.dispatchEvent(new Event("resize")); });
    expect(result.current).toBe(true);

    unmount();

    expect(removed).toHaveBeenCalledWith("resize", expect.any(Function));
    expect(vi.getTimerCount()).toBe(0);
  });

  it("uses a settle time of 150 milliseconds when none is given", () => {
    const { result } = renderHook(() => useResizing());
    act(() => { window.dispatchEvent(new Event("resize")); });
    act(() => { vi.advanceTimersByTime(149); });
    expect(result.current).toBe(true);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current).toBe(false);
  });
});
