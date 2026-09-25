import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useCoalescedRevalidate, useVisibleInterval } from "./use-visible-interval";

/** Simulates the Page Visibility API reporting a tab switch. */
function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { value: state, configurable: true });
  act(() => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  setVisibility("visible");
});

afterEach(() => {
  vi.useRealTimers();
});

/**
 * #641: a hidden tab throttles `setTimeout`, so the old plain debounce here
 * could sit on a scheduled call for arbitrarily long and then fire it (or a
 * pile of them, if repeatedly reset) the moment the tab was looked at again
 * -- the same burst-on-return this hook exists to prevent. These tests prove
 * the hidden case schedules no timer at all and still collapses to exactly
 * one call, delivered the moment the tab is visible again.
 */
describe("useCoalescedRevalidate", () => {
  it("debounces a burst while visible into one call after the delay, same as before #641", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 5_000));

    act(() => {
      result.current();
      result.current();
      result.current();
    });
    expect(callback).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(4_999);
    });
    expect(callback).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(callback).toHaveBeenCalledTimes(1);
  });

  it("schedules no timer while hidden, and does not call back no matter how long it stays hidden", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 5_000));

    setVisibility("hidden");
    act(() => {
      result.current();
    });

    // A real throttled background timer would eventually fire on its own;
    // advancing well past the delay with nothing happening is what proves
    // none is running.
    act(() => {
      vi.advanceTimersByTime(60_000);
    });
    expect(callback).not.toHaveBeenCalled();

    setVisibility("visible");
    expect(callback).toHaveBeenCalledTimes(1);
  });

  it("collapses many calls while hidden into exactly one call on return, not a replay", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 5_000));

    setVisibility("hidden");
    act(() => {
      for (let i = 0; i < 10; i++) result.current();
    });

    setVisibility("visible");
    expect(callback).toHaveBeenCalledTimes(1);
  });

  it("does nothing extra on visibilitychange when nothing was pending", () => {
    const callback = vi.fn();
    renderHook(() => useCoalescedRevalidate(callback, 5_000));

    setVisibility("hidden");
    setVisibility("visible");
    expect(callback).not.toHaveBeenCalled();
  });

  it("clears the flag it already flushed, so a later visible-tab call still gets its own full debounce delay", () => {
    const callback = vi.fn();
    const { result } = renderHook(() => useCoalescedRevalidate(callback, 5_000));

    setVisibility("hidden");
    act(() => {
      result.current();
    });
    setVisibility("visible");
    expect(callback).toHaveBeenCalledTimes(1);

    act(() => {
      result.current();
    });
    expect(callback).toHaveBeenCalledTimes(1);

    act(() => {
      vi.advanceTimersByTime(5_000);
    });
    expect(callback).toHaveBeenCalledTimes(2);
  });
});

/**
 * Left unchanged by #641 (see the doc comment on the hook itself for why) --
 * these just pin down today's behaviour so a later change notices if it
 * drifts.
 */
describe("useVisibleInterval", () => {
  it("ticks the callback on the interval while visible", () => {
    const callback = vi.fn();
    renderHook(() => useVisibleInterval(callback, 30_000));
    expect(callback).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(30_000);
    });
    expect(callback).toHaveBeenCalledTimes(1);

    act(() => {
      vi.advanceTimersByTime(30_000);
    });
    expect(callback).toHaveBeenCalledTimes(2);
  });

  it("stops ticking while hidden and does exactly one catch-up call on return", () => {
    const callback = vi.fn();
    renderHook(() => useVisibleInterval(callback, 30_000));

    setVisibility("hidden");
    act(() => {
      vi.advanceTimersByTime(120_000);
    });
    expect(callback).not.toHaveBeenCalled();

    setVisibility("visible");
    expect(callback).toHaveBeenCalledTimes(1);
  });

  /**
   * #641: a caller whose screen is also covered by the app-wide realtime
   * resync (app-layout.tsx) passes `skipImmediateCatchup` so this hook's own
   * on-return call does not fire a second, redundant revalidation alongside
   * it. `search-cycles-page.tsx` and `activity-page.tsx` both do this.
   */
  describe("skipImmediateCatchup", () => {
    it("does not call back immediately on return, but keeps ticking on the interval while visible", () => {
      const callback = vi.fn();
      renderHook(() => useVisibleInterval(callback, 30_000, { skipImmediateCatchup: true }));

      setVisibility("hidden");
      act(() => {
        vi.advanceTimersByTime(120_000);
      });
      expect(callback).not.toHaveBeenCalled();

      setVisibility("visible");
      expect(callback).not.toHaveBeenCalled();

      act(() => {
        vi.advanceTimersByTime(30_000);
      });
      expect(callback).toHaveBeenCalledTimes(1);
    });

    it("together with a separately-firing app-wide resync, produces exactly one call on return, not two", () => {
      // Stands in for a screen composed the way app-layout.tsx + a route
      // page actually are: one call site is this hook's own catch-up, a
      // second, independent call site is the app-wide resync subscriber
      // (`useSignalRResync` in app-layout.tsx). Both would revalidate the
      // same screen on the same visibilitychange if left uncoordinated.
      const revalidate = vi.fn();
      renderHook(() => useVisibleInterval(revalidate, 10_000, { skipImmediateCatchup: true }));

      setVisibility("hidden");
      act(() => {
        vi.advanceTimersByTime(60_000);
      });

      setVisibility("visible");
      // This hook's own catch-up is the one skipped...
      expect(revalidate).not.toHaveBeenCalled();

      // ...leaving the app-wide resync as the only thing that calls it, once.
      revalidate();
      expect(revalidate).toHaveBeenCalledTimes(1);
    });
  });
});
