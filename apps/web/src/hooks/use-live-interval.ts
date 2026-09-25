import { useCallback, useEffect, useRef } from "react";

/**
 * Runs a refresh every `intervalMs`, whether or not the tab is on screen.
 *
 * Deluno stays live in the background (#641): a hidden tab isn't painted, so
 * keeping its data current costs a request now and then and nothing else, and
 * it means the screen is already right the moment it is looked at again. A
 * poll that paused while hidden had to catch up on return, which is the
 * visible refresh #641 set out to remove. The browser already slows timers in
 * a background tab, which bounds what this costs there.
 */
export function useLiveInterval(callback: () => void, intervalMs: number) {
  const callbackRef = useRef(callback);
  callbackRef.current = callback;

  useEffect(() => {
    const timer = window.setInterval(() => callbackRef.current(), intervalMs);
    return () => window.clearInterval(timer);
  }, [intervalMs]);
}

/**
 * Turns a burst of realtime envelopes into at most one refresh per `delayMs`.
 *
 * The first call starts a timer and the refresh runs when it fires; calls in
 * between only ask for that same refresh, so a steady stream still refreshes
 * every `delayMs` rather than waiting for a quiet spell that may never come.
 * It behaves the same whether or not the tab is on screen (#641): the refresh
 * runs in the background too, so nothing is owed when the tab comes back.
 */
export function useCoalescedRevalidate(callback: () => void, delayMs: number) {
  const callbackRef = useRef(callback);
  const timerRef = useRef<number | undefined>(undefined);
  callbackRef.current = callback;

  useEffect(() => () => {
    if (timerRef.current !== undefined) {
      window.clearTimeout(timerRef.current);
      timerRef.current = undefined;
    }
  }, []);

  return useCallback(() => {
    if (timerRef.current !== undefined) return;
    timerRef.current = window.setTimeout(() => {
      timerRef.current = undefined;
      callbackRef.current();
    }, delayMs);
  }, [delayMs]);
}
