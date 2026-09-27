/**
 * Shared live ticker (#937): one dedicated Worker drives every background
 * poll behind `useLiveInterval` (apps/web/src/hooks/use-live-interval.ts),
 * instead of each poll owning its own page timer.
 *
 * Chrome throttles a hidden, occluded, or minimised page's own chained
 * `setInterval`/`setTimeout` calls to at most one firing a minute after
 * about five minutes
 * (https://developer.chrome.com/blog/background_tabs/). A dedicated Worker
 * is a separate execution context that policy does not reach, so its timer
 * keeps firing at its real rate the whole time Deluno sits behind another
 * window. Every subscriber shares the one worker and the one 1-second
 * heartbeat, and is told apart only by how often it is actually due --
 * that keeps this a single background timer, not one per poll.
 *
 * Falls back to a same-thread `setInterval` when `Worker` isn't available:
 * older embedders, and every unit test run under jsdom, which has no Worker
 * implementation at all. That fallback carries the same throttling risk
 * this module exists to remove, but it is no worse than the pre-#937
 * behaviour, and keeping it lets `vi.useFakeTimers()` exercise this code
 * exactly as it did before.
 */

const GRANULARITY_MS = 1_000;

interface Subscriber {
  intervalMs: number;
  callback: () => void;
  dueAt: number;
}

let worker: Worker | null | undefined; // undefined = not yet decided this run
let fallbackTimer: ReturnType<typeof setInterval> | undefined;
const subscribers = new Map<symbol, Subscriber>();

function fire(now: number) {
  for (const subscriber of subscribers.values()) {
    if (now >= subscriber.dueAt) {
      subscriber.dueAt = now + subscriber.intervalMs;
      subscriber.callback();
    }
  }
}

function ensureRunning() {
  if (worker !== undefined || fallbackTimer !== undefined) return;

  if (typeof Worker !== "undefined") {
    try {
      const dedicated = new Worker(new URL("../workers/live-ticker.worker.ts", import.meta.url), { type: "module" });
      dedicated.onmessage = (event: MessageEvent<{ type: "tick"; at: number }>) => fire(event.data.at);
      dedicated.postMessage({ type: "start", intervalMs: GRANULARITY_MS });
      worker = dedicated;
      return;
    } catch {
      // Fall through to the same-thread fallback below.
    }
  }

  worker = null;
  fallbackTimer = setInterval(() => fire(Date.now()), GRANULARITY_MS);
}

function stopIfIdle() {
  if (subscribers.size > 0) return;
  if (worker) {
    worker.postMessage({ type: "stop" });
    worker.terminate();
  }
  if (fallbackTimer !== undefined) {
    clearInterval(fallbackTimer);
  }
  worker = undefined;
  fallbackTimer = undefined;
}

/**
 * Subscribe to the shared ticker: `callback` fires roughly every
 * `intervalMs`, whether or not the page is on screen. Returns the
 * unsubscribe; call it to stop.
 */
export function subscribeTick(intervalMs: number, callback: () => void): () => void {
  ensureRunning();
  const key = Symbol("live-ticker-subscriber");
  subscribers.set(key, { intervalMs, callback, dueAt: Date.now() + intervalMs });
  return () => {
    subscribers.delete(key);
    stopIfIdle();
  };
}

/**
 * Fires `callback` once, `delayMs` from now, off the same worker-driven
 * clock as `subscribeTick` -- so a one-shot debounce (see
 * `useCoalescedRevalidate`) keeps its real delay while hidden too, instead
 * of falling under Chrome's page-timer throttling. Returns a cancel
 * function; calling it before the delay elapses drops the pending call.
 */
export function scheduleOnce(delayMs: number, callback: () => void): () => void {
  let cancel: () => void = () => {};
  cancel = subscribeTick(delayMs, () => {
    cancel();
    callback();
  });
  return () => cancel();
}
