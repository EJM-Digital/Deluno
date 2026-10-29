import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

/** #1329: the keep-alive Ping the live connection sends from its own clock, so a hidden tab is not dropped. */
const hub = vi.hoisted(() => ({ state: "Disconnected" as string, invoked: [] as string[] }));

vi.mock("@microsoft/signalr", () => {
  class FakeHubConnection {
    get state() { return hub.state; }
    on() {}
    onreconnecting() {}
    onreconnected() {}
    onclose() {}
    async start() { hub.state = "Connected"; }
    async stop() { hub.state = "Disconnected"; }
    async invoke(method: string) {
      hub.invoked.push(method);
      if (method === "Resume") return { status: "Replayed", envelopes: [], instanceId: "run-a" };
      return undefined;
    }
  }
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return new FakeHubConnection(); }
  }
  return { HubConnectionBuilder, HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" }, LogLevel: { Information: 1, None: 0 } };
});

const { SignalRProvider, useSignalREvent } = await import("./use-signalr");

function wrapper({ children }: { children: ReactNode }) {
  return <SignalRProvider accessToken={null}>{children}</SignalRProvider>;
}

const pings = () => hub.invoked.filter((method) => method === "Ping").length;

beforeEach(() => {
  hub.state = "Disconnected";
  hub.invoked = [];
});
afterEach(() => vi.useRealTimers());

describe("the keep-alive Ping (#1329)", () => {
  it("goes out every 20 seconds while connected, and not before", async () => {
    vi.useFakeTimers();
    renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(hub.state).toBe("Connected");
    expect(pings()).toBe(0);

    await act(async () => { await vi.advanceTimersByTimeAsync(19_000); });
    expect(pings()).toBe(0);
    await act(async () => { await vi.advanceTimersByTimeAsync(1_500); });
    expect(pings()).toBe(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(pings()).toBe(2);
  });

  it("is not sent while the connection is down", async () => {
    vi.useFakeTimers();
    renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    hub.state = "Disconnected";

    await act(async () => { await vi.advanceTimersByTimeAsync(65_000); });

    expect(pings()).toBe(0);
  });

  it("keeps going with the tab hidden, because it runs off the clock the hidden tab does not throttle", async () => {
    vi.useFakeTimers();
    Object.defineProperty(document, "visibilityState", { value: "hidden", configurable: true });
    try {
      renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
      await act(async () => { await vi.advanceTimersByTimeAsync(10); });
      await act(async () => { await vi.advanceTimersByTimeAsync(41_000); });
      expect(pings()).toBe(2);
    } finally {
      Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
    }
  });

  it("stops when the connection is closed down with the page", async () => {
    vi.useFakeTimers();
    const view = renderHook(() => useSignalREvent("DownloadProgress", "queue", () => {}), { wrapper });
    await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(hub.state).toBe("Connected");
    view.unmount();
    hub.state = "Connected"; // were the clock still running, the guard on the state would let its Ping through
    const before = pings();

    await act(async () => { await vi.advanceTimersByTimeAsync(65_000); });

    expect(pings()).toBe(before);
  });
});
