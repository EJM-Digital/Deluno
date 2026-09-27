import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * live-ticker (#937): the shared, worker-driven clock behind
 * `useLiveInterval`. jsdom has no `Worker`, so most of these run the
 * same-thread fallback path -- exercised here directly rather than through
 * the hook, which use-live-interval.test.ts already covers end to end. The
 * dedicated-worker path is covered separately with a stubbed `Worker`.
 */
describe("live-ticker", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("fires a subscriber every intervalMs on the fallback clock", async () => {
    const { subscribeTick } = await import("./live-ticker");
    const callback = vi.fn();
    const unsubscribe = subscribeTick(1_000, callback);
    vi.advanceTimersByTime(3_000);
    expect(callback).toHaveBeenCalledTimes(3);
    unsubscribe();
  });

  it("staggers subscribers with different intervals off one shared clock", async () => {
    const { subscribeTick } = await import("./live-ticker");
    const fast = vi.fn();
    const slow = vi.fn();
    const unsubscribeFast = subscribeTick(1_000, fast);
    const unsubscribeSlow = subscribeTick(3_000, slow);
    vi.advanceTimersByTime(3_000);
    expect(fast).toHaveBeenCalledTimes(3);
    expect(slow).toHaveBeenCalledTimes(1);
    unsubscribeFast();
    unsubscribeSlow();
  });

  it("stops the underlying clock once every subscriber has gone, and restarts cleanly", async () => {
    const { subscribeTick } = await import("./live-ticker");
    const callback = vi.fn();
    const unsubscribe = subscribeTick(1_000, callback);
    vi.advanceTimersByTime(1_000);
    unsubscribe();
    vi.advanceTimersByTime(5_000);
    expect(callback).toHaveBeenCalledTimes(1); // nothing fires once idle

    const again = subscribeTick(1_000, callback);
    vi.advanceTimersByTime(2_000);
    expect(callback).toHaveBeenCalledTimes(3); // and a fresh subscribe starts a fresh clock
    again();
  });

  it("scheduleOnce fires exactly once and can be cancelled before it does", async () => {
    const { scheduleOnce } = await import("./live-ticker");
    const fired = vi.fn();
    const cancel = scheduleOnce(1_000, fired);
    vi.advanceTimersByTime(1_000);
    expect(fired).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(5_000);
    expect(fired).toHaveBeenCalledTimes(1); // one-shot, not repeating

    const dropped = vi.fn();
    const cancelDropped = scheduleOnce(1_000, dropped);
    cancelDropped();
    vi.advanceTimersByTime(2_000);
    expect(dropped).not.toHaveBeenCalled();
    cancel();
  });

  it("drives ticks from a dedicated Worker when one is available, not a page timer", async () => {
    const listeners: Array<(event: { data: unknown }) => void> = [];
    const posted: unknown[] = [];
    class FakeWorker {
      onmessage: ((event: { data: unknown }) => void) | null = null;
      constructor(_url: URL, _options?: unknown) {
        listeners.push((event) => this.onmessage?.(event));
      }
      postMessage(message: unknown) { posted.push(message); }
      terminate() { /* no-op for the fake */ }
    }
    vi.stubGlobal("Worker", FakeWorker);

    const { subscribeTick } = await import("./live-ticker");
    const callback = vi.fn();
    const unsubscribe = subscribeTick(1_000, callback);

    expect(posted).toEqual([{ type: "start", intervalMs: 1_000 }]);
    // No page timer should have been armed for this subscriber: advancing
    // the fake page clock does nothing until the worker "posts" a tick.
    vi.advanceTimersByTime(10_000);
    expect(callback).not.toHaveBeenCalled();

    listeners[0]({ data: { type: "tick", at: Date.now() } });
    expect(callback).toHaveBeenCalledTimes(1);

    unsubscribe();
    expect(posted).toContainEqual({ type: "stop" });
  });
});
