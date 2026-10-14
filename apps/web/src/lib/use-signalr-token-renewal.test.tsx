import { act, render, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

/**
 * #1178: the token is renewed while the page is open (#1165), which gives the
 * provider a new access token and so a new connection. Every connection has its
 * own hub-side state (a dropped one forgets its subjects), so the new one must
 * be joined to everything the screens want, however quickly the token changes.
 */
interface Fake { id: number; state: string; joined: Set<string> }
const world = vi.hoisted(() => ({ connections: [] as Array<{ id: number; state: string; joined: Set<string> }> }));

vi.mock("@microsoft/signalr", () => {
  class FakeHubConnection {
    readonly self: Fake = { id: world.connections.length, state: "Disconnected", joined: new Set() };
    constructor() { world.connections.push(this.self); }
    get state() { return this.self.state; }
    on() {}
    onreconnecting() {}
    onreconnected() {}
    onclose() {}
    async start() {
      this.self.state = "Connecting";
      await new Promise((resolve) => setTimeout(resolve, 5)); // reason: the fake hub takes a moment to connect, as a real one does; the renewal under test must cope
      if (this.self.state !== "Connecting") throw new Error("stopped while starting");
      this.self.state = "Connected";
    }
    async stop() { this.self.state = "Disconnected"; this.self.joined.clear(); }
    async invoke(method: string, ...args: unknown[]) {
      await Promise.resolve();
      if (this.self.state !== "Connected") throw new Error("not connected");
      if (method === "Resume") return { status: "CaughtUp", envelopes: [] };
      for (const subject of args[0] as string[]) {
        if (method === "Subscribe") this.self.joined.add(subject);
        else this.self.joined.delete(subject);
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
  return { HubConnectionBuilder, HubConnectionState: { Connected: "Connected" }, LogLevel: { Information: 1, None: 0 } };
});

const { SignalRProvider, useSignalREvent, useLiveEvent } = await import("./use-signalr");

function Screens() {
  useSignalREvent("DownloadProgress", "queue", () => {});
  useSignalREvent("ActivityEventAdded", "activity", () => {});
  useLiveEvent("DownloadSpeedSample", () => {});
  return null;
}

const wanted = new Set(["queue", "activity", "live"]);

describe("a renewed token (#1178)", () => {
  it("leaves the current connection joined to every subject, however fast the token changes", async () => {
    const view = render(<SignalRProvider accessToken="t0"><Screens /></SignalRProvider>);
    await waitFor(() => expect(world.connections.at(-1)!.joined).toEqual(wanted));

    for (const token of ["t1", "t2", "t3"]) {
      await act(async () => { view.rerender(<SignalRProvider accessToken={token}><Screens /></SignalRProvider>); });
    }
    await waitFor(() => expect(world.connections.at(-1)!.joined).toEqual(wanted));
    // The older connections were stopped, not left running beside it.
    expect(world.connections.slice(0, -1).every((c) => c.state === "Disconnected")).toBe(true);
  });

  it("does the same when the token changes while the first connection is still starting", async () => {
    const view = render(<SignalRProvider accessToken="t0"><Screens /></SignalRProvider>);
    await act(async () => { view.rerender(<SignalRProvider accessToken="t1"><Screens /></SignalRProvider>); });
    await act(async () => { view.rerender(<SignalRProvider accessToken="t2"><Screens /></SignalRProvider>); });
    await waitFor(() => expect(world.connections.at(-1)!.joined).toEqual(wanted));
  });
});
