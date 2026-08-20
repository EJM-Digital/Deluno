import { useCallback, useEffect, useRef } from "react";

/**
 * Runs a refresh only while the document is visible. The route loader remains
 * the initial hydrate; returning to a visible tab gets exactly one catch-up
 * refresh instead of one request for every interval elapsed in the background.
 */
export function useVisibleInterval(callback: () => void, intervalMs: number) {
  const callbackRef = useRef(callback);
  callbackRef.current = callback;

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
        callbackRef.current();
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
  }, [intervalMs]);
}

/** Coalesces a burst of realtime envelopes into one route revalidation. */
export function useCoalescedRevalidate(callback: () => void, delayMs: number) {
  const callbackRef = useRef(callback);
  const timerRef = useRef<number | undefined>(undefined);
  callbackRef.current = callback;

  useEffect(() => () => {
    if (timerRef.current !== undefined) {
      window.clearTimeout(timerRef.current);
    }
  }, []);

  return useCallback(() => {
    if (timerRef.current !== undefined) {
      window.clearTimeout(timerRef.current);
    }

    timerRef.current = window.setTimeout(() => {
      timerRef.current = undefined;
      callbackRef.current();
    }, delayMs);
  }, [delayMs]);
}
