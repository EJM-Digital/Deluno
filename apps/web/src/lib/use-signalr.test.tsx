import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import type { SignalREventName, SignalREventPayload } from "./use-signalr";

/**
 * Covers #641: while the Deluno tab is hidden, `useSignalREvent` subscribers
 * should not be hammered with every queued update the moment the tab is
 * looked at again. See the "hidden tab" paragraph in use-signalr.tsx's file
 * header for the design this proves.
 *
 * `@microsoft/signalr` is replaced with a small fake connection so a test can
 * push envelopes directly through the same `"RealtimeEvent"` handler the real
 * hub would call, and flip `document.visibilityState` to simulate the tab
 * being backgrounded. Mutable mock state goes through `vi.hoisted` because
 * `vi.mock` factories are hoisted above ordinary top-level `let`/`const` (see
 * https://vitest.dev/api/vi.html#vi-mock) -- mirrors the `await import`
 * pattern already used in `../components/shell/toaster.test.tsx`.
 */

type FakeEnvelope = { seq: number; name: string; subject: string; at: string; data: unknown };

const mocks = vi.hoisted(() => ({
  realtimeHandler: null as ((envelope: FakeEnvelope) => void) | null,
  connectionState: "Disconnected" as "Disconnected" | "Connected"
}));

vi.mock("@microsoft/signalr", () => {
  class FakeHubConnection {
    get state() {
      return mocks.connectionState;
    }
    on(event: string, handler: (envelope: FakeEnvelope) => void) {
      if (event === "RealtimeEvent") mocks.realtimeHandler = handler;
    }
    onreconnecting() {}
    onreconnected() {}
    onclose() {}
    async start() {
      mocks.connectionState = "Connected";
    }
    async stop() {
      mocks.connectionState = "Disconnected";
    }
    async invoke(method: string) {
      if (method === "Resume") return { status: "CaughtUp", envelopes: [] };
      return undefined;
    }
  }

  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return new FakeHubConnection(); }
  }

  return {
    HubConnectionBuilder,
    HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
    LogLevel: { Information: 1, None: 0 }
  };
});

const { SignalRProvider, useSignalREvent, useIsTabVisible } = await import("./use-signalr");

/** A named hook so `renderHook(() => useEventHarness(...))` reads as a normal hook call to eslint's rules-of-hooks, same shape already used by `../hooks/library-view-memory.test.ts`. */
function useEventHarness<T extends SignalREventName>(
  event: T,
  subject: string,
  handler: (payload: SignalREventPayload<T>) => void
) {
  useSignalREvent(event, subject, handler);
}

/** Mirrors system-screen.tsx's liveActiveJobs pattern: a raw counter that only ever adds or subtracts one, never reads the payload. */
function useQueueCounterHarness(onAdded: () => void, onRemoved: () => void) {
  useEventHarness("QueueItemAdded", "queue", onAdded);
  useEventHarness("QueueItemRemoved", "queue", onRemoved);
}

function wrapper({ children }: { children: ReactNode }) {
  return <SignalRProvider accessToken={null}>{children}</SignalRProvider>;
}

/** Renders inside a SignalRProvider and waits for its (mocked) connect sequence to settle, so the state update it makes on connect doesn't land outside act() later. */
async function mount<T>(callback: () => T) {
  const view = renderHook(callback, { wrapper });
  await waitFor(() => {
    expect(mocks.connectionState).toBe("Connected");
  });
  return view;
}

let seq = 0;
function nextEnvelope(name: string, subject: string, data: unknown): FakeEnvelope {
  seq += 1;
  return { seq, name, subject, at: new Date().toISOString(), data };
}

/** Simulates the hub pushing one envelope down the (mocked) socket. */
function send(envelope: FakeEnvelope) {
  act(() => {
    mocks.realtimeHandler?.(envelope);
  });
}

/** Simulates the Page Visibility API reporting a tab switch. */
function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { value: state, configurable: true });
  act(() => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
}

beforeEach(() => {
  mocks.realtimeHandler = null;
  mocks.connectionState = "Disconnected";
  seq = 0;
  setVisibility("visible");
});

describe("realtime dispatch while the tab is hidden (#641)", () => {
  it("delivers every envelope immediately while visible, unchanged", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("DownloadProgress", "queue", handler));

    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 10 }));
    expect(handler).toHaveBeenCalledTimes(1);

    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 40 }));
    expect(handler).toHaveBeenCalledTimes(2);
    expect(handler).toHaveBeenLastCalledWith({ id: "a", progress: 40 });
  });

  it("holds a same-entity burst while hidden and delivers only the latest value once, on return", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("DownloadProgress", "queue", handler));

    setVisibility("hidden");
    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 10 }));
    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 45 }));
    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 91 }));
    // Nothing painted while hidden, and nothing queued to replay either.
    expect(handler).not.toHaveBeenCalled();

    setVisibility("visible");
    // One clean update with whatever is current, not a replay of all three.
    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith({ id: "a", progress: 91 });
  });

  it("does not drop distinct entities: each gets its own delivery on return, not one for the whole burst", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("QueueItemAdded", "queue", handler));

    setVisibility("hidden");
    send(nextEnvelope("QueueItemAdded", "queue", { id: "a", title: "A", type: "movie", status: "queued" }));
    send(nextEnvelope("QueueItemAdded", "queue", { id: "b", title: "B", type: "movie", status: "queued" }));
    expect(handler).not.toHaveBeenCalled();

    setVisibility("visible");
    expect(handler).toHaveBeenCalledTimes(2);
    const ids = handler.mock.calls.map((call) => (call[0] as { id: string }).id).sort();
    expect(ids).toEqual(["a", "b"]);
  });

  it("nets out correctly for a raw increment/decrement counter across an add-then-remove while hidden", async () => {
    let count = 0;
    await mount(() => useQueueCounterHarness(
      () => { count += 1; },
      () => { count -= 1; }
    ));

    setVisibility("hidden");
    send(nextEnvelope("QueueItemAdded", "queue", { id: "a" }));
    send(nextEnvelope("QueueItemAdded", "queue", { id: "b" }));
    send(nextEnvelope("QueueItemRemoved", "queue", { id: "a" }));
    setVisibility("visible");

    // a: added then removed nets to 0; b: added and still present. Net +1,
    // not the 0 a burst collapsed by event name alone would have produced.
    expect(count).toBe(1);
  });

  it("collapses a burst of one-shot events for the SAME entity to a single delivery (stands in for toast/animation spam)", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("DispatchGrabAttempt", "queue", handler));

    setVisibility("hidden");
    for (let i = 0; i < 5; i++) {
      send(nextEnvelope("DispatchGrabAttempt", "queue", {
        dispatchId: "same-dispatch",
        releaseName: `Attempt ${i}`,
        clientId: "c1",
        clientName: "Client",
        timestamp: new Date().toISOString()
      }));
    }
    expect(handler).not.toHaveBeenCalled();

    setVisibility("visible");
    expect(handler).toHaveBeenCalledTimes(1);
    expect((handler.mock.calls[0][0] as { releaseName: string }).releaseName).toBe("Attempt 4");
  });

  it("still ignores a stale replay of an already-applied seq", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("HealthChanged", "dashboard", handler));

    const first = nextEnvelope("HealthChanged", "dashboard", { source: "indexer", status: "degraded", message: "slow" });
    send(first);
    expect(handler).toHaveBeenCalledTimes(1);

    send(first);
    expect(handler).toHaveBeenCalledTimes(1);
  });
});

describe("useIsTabVisible", () => {
  it("reflects document.visibilityState and updates on visibilitychange", () => {
    setVisibility("visible");
    const { result } = renderHook(() => useIsTabVisible());
    expect(result.current).toBe(true);

    setVisibility("hidden");
    expect(result.current).toBe(false);

    setVisibility("visible");
    expect(result.current).toBe(true);
  });
});
