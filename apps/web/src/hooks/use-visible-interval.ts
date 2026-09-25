import { useCallback, useEffect, useRef } from "react";

export interface UseVisibleIntervalOptions {
  /**
   * Skip the immediate catch-up call this hook would otherwise make the
   * moment the tab becomes visible again (#641). The recurring interval
   * itself is unaffected -- only that one on-return call is dropped.
   *
   * For a caller whose screen is also covered by the app-wide realtime
   * resync (`useSignalRResync`, wired once in `app-layout.tsx`), that resync
   * already revalidates the same data on the very same return: without this,
   * the two fire back to back and the same refresh happens twice. A caller
   * with no such coverage must leave this unset -- the fallback poll is the
   * only thing that will ever catch it up.
   */
  skipImmediateCatchup?: boolean;
}

/**
 * Runs a refresh only while the document is visible. The route loader remains
 * the initial hydrate; returning to a visible tab gets exactly one catch-up
 * refresh instead of one request for every interval elapsed in the background.
 *
 * Left as-is by #641: this is a general polling fallback, used by callers
 * with no realtime coverage at all (and by at least one pure UI clock tick),
 * not exclusively paired with `useSignalREvent`. Whether its catch-up fetch
 * is redundant now depends on whether THIS caller's data also arrives over
 * SignalR — this hook has no way to know that on its own, and guessing wrong
 * trades a harmless extra request for a stale screen after a long hidden
 * spell, which is the worse failure — hence `skipImmediateCatchup` being
 * opt-in rather than inferred. `useCoalescedRevalidate` below is the one
 * changed unconditionally: it is used exclusively downstream of realtime
 * events, where the tradeoff is unambiguous.
 */
export function useVisibleInterval(callback: () => void, intervalMs: number, options?: UseVisibleIntervalOptions) {
  const callbackRef = useRef(callback);
  callbackRef.current = callback;
  const skipImmediateCatchup = options?.skipImmediateCatchup ?? false;

  useEffect(() => {
    let timer: number | undefined;

    const stop = () => {
      if (timer !== undefined) {
        window.clearInterval(timer);
        timer = undefined;
      }
    };

    const start = () => {
      stop();
      timer = window.setInterval(() => callbackRef.current(), intervalMs);
    };

    const syncVisibility = () => {
      if (document.visibilityState === "visible") {
        if (!skipImmediateCatchup) callbackRef.current();
        start();
      } else {
        stop();
      }
    };

    if (document.visibilityState === "visible") {
      start();
    }

    document.addEventListener("visibilitychange", syncVisibility);
    return () => {
      stop();
      document.removeEventListener("visibilitychange", syncVisibility);
    };
  }, [intervalMs, skipImmediateCatchup]);
}

/**
 * Coalesces a burst of realtime envelopes into one route revalidation.
 *
 * While the tab is visible this is a plain trailing debounce: each call
 * resets a `delayMs` timer, so a burst settles into one callback shortly
 * after it stops arriving.
 *
 * While hidden, a timer buys nothing (#641): background tabs throttle
 * `setTimeout`, and a *trailing* debounce that keeps getting reset by new
 * arrivals may never fire at all until the tab is looked at again — at which
 * point every timer a throttled tab was sitting on can fire together, which
 * is the same burst this exists to prevent. So no timer runs while hidden.
 * A call instead marks a revalidate as owed (latest wins — a second call
 * while hidden has nothing more to record than the first), and the one
 * pending revalidate fires the moment the tab becomes visible, not a replay
 * of every call that asked for it.
 */
export function useCoalescedRevalidate(callback: () => void, delayMs: number) {
  const callbackRef = useRef(callback);
  const timerRef = useRef<number | undefined>(undefined);
  const pendingRef = useRef(false);
  callbackRef.current = callback;

  const clearTimer = useCallback(() => {
    if (timerRef.current !== undefined) {
      window.clearTimeout(timerRef.current);
      timerRef.current = undefined;
    }
  }, []);

  useEffect(() => {
    if (typeof document === "undefined") return;

    const onVisibilityChange = () => {
      if (document.visibilityState !== "visible" || !pendingRef.current) return;
      pendingRef.current = false;
      clearTimer();
      callbackRef.current();
    };

    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => document.removeEventListener("visibilitychange", onVisibilityChange);
  }, [clearTimer]);

  useEffect(() => clearTimer, [clearTimer]);

  return useCallback(() => {
    if (typeof document !== "undefined" && document.visibilityState === "hidden") {
      pendingRef.current = true;
      return;
    }

    clearTimer();
    timerRef.current = window.setTimeout(() => {
      timerRef.current = undefined;
      pendingRef.current = false;
      callbackRef.current();
    }, delayMs);
  }, [clearTimer, delayMs]);
}
