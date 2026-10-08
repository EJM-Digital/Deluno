import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

/**
 * #1179: the server answers "resume from nothing" with resync-required. On the first connect of a
 * page every screen has just loaded its data, so that answer must not refetch it all; on a
 * reconnect, the screens read once to catch up.
 */
const hub = vi.hoisted(() => ({
  state: "Disconnected" as string,
  onreconnecting: null as null | (() => void),
  onreconnected: null as null | (() => void),
  resumes: 0
}));

vi.mock("@microsoft/signalr", () => {
  class FakeHubConnection {
    get state() { return hub.state; }
    on() {}
    onreconnecting(handler: () => void) { hub.onreconnecting = handler; }
    onreconnected(handler: () => void) { hub.onreconnected = handler; }
    onclose() {}
    async start() { hub.state = "Connected"; }
    async stop() { hub.state = "Disconnected"; }
    async invoke(method: string) {
      await Promise.resolve();
      if (method === "Resume") {
        hub.resumes += 1;
        return { status: "ResyncRequired", envelopes: [] };
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
  return { HubConnectionBuilder, HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" }, LogLevel: { Information: 1, None: 0 } };
});

const { SignalRProvider, useSignalRResync, useSignalREvent } = await import("./use-signalr");

function wrapper({ children }: { children: ReactNode }) {
  return <SignalRProvider accessToken={null}>{children}</SignalRProvider>;
}

beforeEach(() => {
  hub.state = "Disconnected";
  hub.onreconnecting = null;
  hub.onreconnected = null;
  hub.resumes = 0;
});

describe("resync (#1179)", () => {
  it("does nothing on the first connect, and reads once after a reconnect", async () => {
    const resync = vi.fn();
    renderHook(() => {
      useSignalRResync(resync);
      useSignalREvent("DownloadProgress", "queue", () => {});
    }, { wrapper });

    await waitFor(() => expect(hub.resumes).toBe(1));
    expect(resync).not.toHaveBeenCalled();

    await act(async () => {
      hub.state = "Reconnecting";
      hub.onreconnecting?.();
      hub.state = "Connected";
      hub.onreconnected?.();
    });
    await waitFor(() => expect(hub.resumes).toBe(2));
    await waitFor(() => expect(resync).toHaveBeenCalledTimes(1));
  });
});
