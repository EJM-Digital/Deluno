import { useCallback, useEffect, useRef } from "react";
import { scheduleOnce, subscribeTick } from "../lib/live-ticker";

/**
 * Runs a refresh every `intervalMs`, whether or not the tab is on screen.
 *
 * Deluno stays live in the background (#641): a hidden tab isn't painted, so
 * keeping its data current costs a request now and then and nothing else, and
 * it means the screen is already right the moment it is looked at again. A
 * poll that paused while hidden had to catch up on return, which is the
 * visible refresh #641 set out to remove.
 *
 * Driven by the shared worker ticker (#937, lib/live-ticker.ts) rather than
 * its own `setInterval`: Chrome throttles a hidden or minimised page's own
 * chained timers to at most one firing a minute after about five minutes,
 * which would make this poll fall behind and then jump on return -- exactly
 * the catch-up #641 set out to remove. A dedicated worker isn't subject to
 * that throttling.
 */
export function useLiveInterval(callback: () => void, intervalMs: number) {
  const callbackRef = useRef(callback);
  callbackRef.current = callback;

  useEffect(() => subscribeTick(intervalMs, () => callbackRef.current()), [intervalMs]);
}

/**
 * Turns a burst of realtime envelopes into at most one refresh per `delayMs`.
 *
 * The first call starts a timer and the refresh runs when it fires; calls in
 * between only ask for that same refresh, so a steady stream still refreshes
 * every `delayMs` rather than waiting for a quiet spell that may never come.
 * It behaves the same whether or not the tab is on screen (#641): the refresh
 * runs in the background too, so nothing is owed when the tab comes back.
 *
 * Uses the shared worker ticker's `scheduleOnce` (#937) for the same reason
 * `useLiveInterval` does: a page-timer debounce would inherit Chrome's
 * background throttling and could sit pending for up to a minute once the
 * tab has been hidden a while.
 */
export function useCoalescedRevalidate(callback: () => void, delayMs: number) {
  const callbackRef = useRef(callback);
  const cancelRef = useRef<(() => void) | undefined>(undefined);
  callbackRef.current = callback;

  useEffect(() => () => {
    cancelRef.current?.();
    cancelRef.current = undefined;
  }, []);

  return useCallback(() => {
    if (cancelRef.current) return;
    cancelRef.current = scheduleOnce(delayMs, () => {
      cancelRef.current = undefined;
      callbackRef.current();
    });
  }, [delayMs]);
}
