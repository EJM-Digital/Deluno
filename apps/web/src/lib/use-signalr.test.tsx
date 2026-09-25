import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import type { SignalREventName, SignalREventPayload } from "./use-signalr";

/**
 * Covers #641: Deluno stays live while its tab is hidden. Every envelope
 * reaches its `useSignalREvent` subscribers as it arrives, hidden or not, so
 * the data behind a screen is already current when the tab is looked at again,
 * and nothing -- no replay, no resync -- fires on return.
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

const { SignalRProvider, useSignalREvent, useSignalRResync, useIsTabVisible } = await import("./use-signalr");

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

/** Mirrors a screen subscribed to both a live event and the app-wide resync (e.g. app-layout.tsx). */
function useEventAndResyncHarness<T extends SignalREventName>(
  event: T,
  subject: string,
  onEvent: (payload: SignalREventPayload<T>) => void,
  onResync: () => void
) {
  useEventHarness(event, subject, onEvent);
  useSignalRResync(onResync);
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
  it("delivers every envelope immediately while visible", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("DownloadProgress", "queue", handler));

    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 10 }));
    expect(handler).toHaveBeenCalledTimes(1);

    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 40 }));
    expect(handler).toHaveBeenCalledTimes(2);
    expect(handler).toHaveBeenLastCalledWith({ id: "a", progress: 40 });
  });

  it("keeps delivering while hidden, so the latest value is already in place", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("DownloadProgress", "queue", handler));

    setVisibility("hidden");
    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 10 }));
    send(nextEnvelope("DownloadProgress", "queue", { id: "a", progress: 91 }));
    expect(handler).toHaveBeenCalledTimes(2);
    expect(handler).toHaveBeenLastCalledWith({ id: "a", progress: 91 });
  });

  it("calls nothing on return: there is nothing to catch up", async () => {
    const handler = vi.fn();
    await mount(() => useEventHarness("QueueItemAdded", "queue", handler));

    setVisibility("hidden");
    send(nextEnvelope("QueueItemAdded", "queue", { id: "a", title: "A", type: "movie", status: "queued" }));
    send(nextEnvelope("QueueItemAdded", "queue", { id: "b", title: "B", type: "movie", status: "queued" }));
    expect(handler).toHaveBeenCalledTimes(2);

    setVisibility("visible");
    expect(handler).toHaveBeenCalledTimes(2);
  });

  it("keeps a raw increment/decrement counter correct through a hidden spell", async () => {
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

    expect(count).toBe(1);
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

describe("useSignalRResync and returning to the tab (#641)", () => {
  it("does not fire when the tab comes back, however much arrived while hidden", async () => {
    const onEvent = vi.fn();
    const onResync = vi.fn();
    await mount(() => useEventAndResyncHarness("DispatchImportCompleted", "queue", onEvent, onResync));

    setVisibility("hidden");
    send(nextEnvelope("DispatchImportCompleted", "queue", {
      dispatchId: "d1", releaseName: "Movie", succeeded: true, importedPath: "/x", failureReason: null, timestamp: new Date().toISOString()
    }));
    setVisibility("visible");

    expect(onEvent).toHaveBeenCalledTimes(1);
    expect(onResync).not.toHaveBeenCalled();
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
