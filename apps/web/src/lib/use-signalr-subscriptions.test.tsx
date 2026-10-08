import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

/**
 * #1178: every subject a screen asks for is joined on the hub whatever the order
 * (before the connection starts, while it starts, after it is up), joined again
 * after every reconnect, and left when the last listener leaves.
 *
 * The fake connection stands in for the hub: it keeps the set of subjects the
 * "server" believes this connection joined, forgets it when the connection drops,
 * and can be made to refuse a request.
 */
const hub = vi.hoisted(() => ({
  state: "Disconnected" as "Disconnected" | "Connecting" | "Connected" | "Reconnecting",
  joined: new Set<string>(),
  calls: [] as Array<{ method: string; subjects: string[] }>,
  releaseStart: null as null | (() => void),
  holdStart: false,
  refuse: null as null | ((method: string, subjects: string[]) => boolean),
  onreconnecting: null as null | (() => void),
  onreconnected: null as null | (() => void),
  onclose: null as null | (() => void),
  failStarts: 0
}));

vi.mock("@microsoft/signalr", () => {
  class FakeHubConnection {
    get state() {
      return hub.state;
    }
    on() {}
    onreconnecting(handler: () => void) { hub.onreconnecting = handler; }
    onreconnected(handler: () => void) { hub.onreconnected = handler; }
    onclose(handler: () => void) { hub.onclose = handler; }
    async start() {
      hub.state = "Connecting";
      if (hub.failStarts > 0) {
        hub.failStarts -= 1;
        hub.state = "Disconnected";
        throw new Error("cannot connect");
      }
      if (hub.holdStart) await new Promise<void>((resolve) => { hub.releaseStart = resolve; });
      hub.state = "Connected";
    }
    async stop() {
      hub.state = "Disconnected";
      hub.joined.clear();
    }
    async invoke(method: string, ...args: unknown[]) {
      await Promise.resolve();
      if (method === "Resume") return { status: "CaughtUp", envelopes: [] };
      const subjects = args[0] as string[];
      hub.calls.push({ method, subjects });
      if (hub.state !== "Connected") throw new Error("not connected");
      if (hub.refuse?.(method, subjects)) throw new Error("refused");
      for (const subject of subjects) {
        if (method === "Subscribe") hub.joined.add(subject);
        else hub.joined.delete(subject);
      }
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

const { SignalRProvider, useSignalREvent, useLiveEvent } = await import("./use-signalr");

function wrapper({ children }: { children: ReactNode }) {
  return <SignalRProvider accessToken={null}>{children}</SignalRProvider>;
}

/** Hooks under test, switched on and off through props so the order of registration is the test's choice. */
function useScreens({ queue, activity, live }: { queue?: boolean; activity?: boolean; live?: boolean }) {
  useSignalREvent("DownloadProgress", queue ? "queue" : [], () => {});
  useSignalREvent("ActivityEventAdded", activity ? "activity" : [], () => {});
  useLiveEvent("DownloadSpeedSample", () => {});
  void live;
}

function dropConnection() {
  hub.state = "Reconnecting";
  hub.joined.clear();
  hub.onreconnecting?.();
}

const subscribeCalls = (subject: string) =>
  hub.calls.filter((call) => call.method === "Subscribe" && call.subjects.includes(subject));

beforeEach(() => {
  hub.state = "Disconnected";
  hub.joined.clear();
  hub.calls = [];
  hub.releaseStart = null;
  hub.holdStart = false;
  hub.refuse = null;
  hub.onreconnecting = null;
  hub.onreconnected = null;
  hub.onclose = null;
  hub.failStarts = 0;
});

describe("hub subjects (#1178)", () => {
  it("joins a subject registered before the connection starts, exactly once", async () => {
    renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true));
    expect(subscribeCalls("queue")).toHaveLength(1);
  });

  it("joins a subject registered while the connection is starting, exactly once", async () => {
    hub.holdStart = true;
    const view = renderHook(
      ({ queue, activity }) => useScreens({ queue, activity }),
      { wrapper, initialProps: { queue: true, activity: false } }
    );
    await waitFor(() => expect(hub.state).toBe("Connecting"));
    view.rerender({ queue: true, activity: true });
    expect(hub.joined.size).toBe(0);
    await act(async () => { hub.releaseStart?.(); });
    await waitFor(() => expect(hub.joined).toEqual(new Set(["queue", "activity", "live"])));
    expect(subscribeCalls("queue")).toHaveLength(1);
    expect(subscribeCalls("activity")).toHaveLength(1);
  });

  it("joins a subject registered after the connection is up, exactly once", async () => {
    const view = renderHook(
      ({ queue, activity }) => useScreens({ queue, activity }),
      { wrapper, initialProps: { queue: true, activity: false } }
    );
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true));
    view.rerender({ queue: true, activity: true });
    await waitFor(() => expect(hub.joined.has("activity")).toBe(true));
    expect(subscribeCalls("activity")).toHaveLength(1);
    expect(subscribeCalls("queue")).toHaveLength(1);
  });

  it("joins everything again after a reconnect, including a subject added while it was down", async () => {
    const view = renderHook(
      ({ queue, activity }) => useScreens({ queue, activity }),
      { wrapper, initialProps: { queue: true, activity: false } }
    );
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true));

    act(() => dropConnection());
    view.rerender({ queue: true, activity: true });
    expect(hub.joined.size).toBe(0);
    await act(async () => {
      hub.state = "Connected";
      hub.onreconnected?.();
    });
    await waitFor(() => expect(hub.joined).toEqual(new Set(["queue", "activity", "live"])));
  });

  it("leaves a subject when the last listener goes, and keeps it while another listener stays", async () => {
    const view = renderHook(
      ({ a, b }) => {
        useSignalREvent("DownloadProgress", a ? "queue" : [], () => {});
        useSignalREvent("QueueItemAdded", b ? "queue" : [], () => {});
      },
      { wrapper, initialProps: { a: true, b: true } }
    );
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true));

    view.rerender({ a: false, b: true });
    await act(async () => { await Promise.resolve(); });
    expect(hub.joined.has("queue")).toBe(true);

    view.rerender({ a: false, b: false });
    await waitFor(() => expect(hub.joined.has("queue")).toBe(false));
  });

  it("one subject the hub refuses does not cost the others, and the refused one is tried again", async () => {
    let refusing = true;
    hub.refuse = (method, subjects) => method === "Subscribe" && refusing && subjects.includes("activity");
    renderHook(() => {
      useSignalREvent("DownloadProgress", "queue", () => {});
      useSignalREvent("ActivityEventAdded", "activity", () => {});
      useSignalREvent("HealthChanged", "dashboard", () => {});
    }, { wrapper });
    await waitFor(() => expect(hub.joined).toEqual(new Set(["queue", "dashboard"])));
    refusing = false;
    await waitFor(() => expect(hub.joined.has("activity")).toBe(true), { timeout: 4000 });
  });

  it("does not stay unsubscribed when the first request fails: it is tried again", async () => {
    let refusing = true;
    hub.refuse = () => refusing;
    renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
    await waitFor(() => expect(hub.calls.length).toBeGreaterThan(0));
    refusing = false;
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true), { timeout: 4000 });
  });

  it("brings the connection back by itself after SignalR gives up, and joins everything again", async () => {
    renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true));

    // The automatic reconnect ran out: the connection is closed. The first new attempt also fails.
    hub.failStarts = 1;
    await act(async () => {
      hub.state = "Disconnected";
      hub.joined.clear();
      hub.onclose?.();
    });
    expect(hub.joined.size).toBe(0);
    await waitFor(() => expect(hub.joined.has("queue")).toBe(true), { timeout: 6000 });
  });
});
