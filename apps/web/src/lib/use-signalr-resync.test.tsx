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
  onclose: null as null | (() => void),
  resumes: 0,
  deliver: null as null | ((payload: unknown) => void),
  resumeArgs: [] as unknown[][],
  answer: "ResyncRequired" as string | number,
  envelopes: [] as unknown[],
  instanceId: "run-a",
  hold: null as null | Promise<void>,
  failResume: false
}));

vi.mock("@microsoft/signalr", () => {
  class FakeHubConnection {
    get state() { return hub.state; }
    on(name: string, handler: (payload: unknown) => void) { if (name === "RealtimeEvent") hub.deliver = handler; }
    onreconnecting(handler: () => void) { hub.onreconnecting = handler; }
    onreconnected(handler: () => void) { hub.onreconnected = handler; }
    onclose(handler: () => void) { hub.onclose = handler; }
    async start() { hub.state = "Connected"; }
    async stop() { hub.state = "Disconnected"; }
    private handlers: Record<string, (payload: unknown) => void> = {};
    async invoke(method: string, ...args: unknown[]) {
      await Promise.resolve();
      if (method === "Resume") {
        hub.resumes += 1;
        hub.resumeArgs.push(args);
        if (hub.hold) await hub.hold;
        if (hub.failResume) throw new Error("The hub did not answer");
        return { status: hub.answer, envelopes: hub.envelopes, instanceId: hub.instanceId };
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

const { forgetRealtimePosition, noteRealtimePosition } = await import("./realtime-position");
const { SignalRProvider, useSignalRResync, useSignalREvent, useSignalRStatus } = await import("./use-signalr");

function wrapper({ children }: { children: ReactNode }) {
  return <SignalRProvider accessToken={null}>{children}</SignalRProvider>;
}

beforeEach(() => {
  hub.state = "Disconnected";
  hub.onreconnecting = null;
  hub.onreconnected = null;
  hub.onclose = null;
  hub.resumes = 0;
  hub.resumeArgs = [];
  hub.answer = "ResyncRequired";
  hub.envelopes = [];
  hub.instanceId = "run-a";
  hub.hold = null;
  hub.failResume = false;
  forgetRealtimePosition();
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

  // #1191: a change made between the page's first read and its first join must reach the screen.
  it("on the first connect asks for what was published since the page's first read, and delivers it", async () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "41", "X-Deluno-Event-Instance": "run-a" }) });
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "99" }) }); // a later read does not move it
    hub.answer = "Replayed";
    hub.envelopes = [{ seq: 42, name: "MovieChanged", subject: "dashboard", at: "", data: { id: "m1" } }];
    const seen = vi.fn();
    const resync = vi.fn();
    renderHook(() => {
      useSignalRResync(resync);
      useSignalREvent("MovieChanged", "dashboard", seen);
    }, { wrapper });

    await waitFor(() => expect(hub.resumes).toBe(1));
    expect(hub.resumeArgs[0][0]).toBe(41);
    expect(hub.resumeArgs[0][2]).toBe("run-a");
    await waitFor(() => expect(seen).toHaveBeenCalledTimes(1));
    expect(resync).not.toHaveBeenCalled();
  });

  it("reads once when the hub's buffer no longer reaches back to the page's first read", async () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "7" }) });
    hub.answer = "ResyncRequired";
    const resync = vi.fn();
    renderHook(() => {
      useSignalRResync(resync);
      useSignalREvent("DownloadProgress", "queue", () => {});
    }, { wrapper });

    await waitFor(() => expect(resync).toHaveBeenCalledTimes(1));
  });

  // #1315: the sequence restarts at 0 on every server restart, so it travels with the run it came from.
  it("sends the server run its sequence came from, and the hub's resync after a restart refetches", async () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "41", "X-Deluno-Event-Instance": "run-a" }) });
    hub.answer = "Replayed";
    const resync = vi.fn();
    renderHook(() => {
      useSignalRResync(resync);
      useSignalREvent("DownloadProgress", "queue", () => {});
    }, { wrapper });
    await waitFor(() => expect(hub.resumes).toBe(1));
    expect(hub.resumeArgs[0][2]).toBe("run-a");

    // Deluno restarted: the reconnect still names run-a, and the new run answers resync-required.
    hub.answer = "ResyncRequired";
    hub.instanceId = "run-b";
    await act(async () => {
      hub.state = "Reconnecting";
      hub.onreconnecting?.();
      hub.state = "Connected";
      hub.onreconnected?.();
    });
    await waitFor(() => expect(hub.resumes).toBe(2));
    expect(hub.resumeArgs[1][2]).toBe("run-a");
    await waitFor(() => expect(resync).toHaveBeenCalledTimes(1));

    // The next reconnect names the new run.
    await act(async () => {
      hub.state = "Reconnecting";
      hub.onreconnecting?.();
      hub.state = "Connected";
      hub.onreconnected?.();
    });
    await waitFor(() => expect(hub.resumes).toBe(3));
    expect(hub.resumeArgs[2][2]).toBe("run-b");
  });
  // #1315: a connected page must not go deaf after a restart, when the new run counts from 1 again.
  it("after a server restart a connected page hears the new run's pushes even though their numbers are lower than the last one it saw", async () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "40", "X-Deluno-Event-Instance": "run-a" }) });
    hub.answer = "Replayed";
    const seen = vi.fn();
    renderHook(() => { useSignalREvent("MovieChanged", "dashboard", seen); }, { wrapper });
    await waitFor(() => expect(hub.resumes).toBe(1));
    const push = (seq: number, id: string) => act(async () => { hub.deliver?.({ seq, name: "MovieChanged", subject: "dashboard", at: "", data: { id } }); });

    await push(41, "before-restart");
    await push(41, "same-number-again");
    expect(seen).toHaveBeenCalledTimes(1);

    // The restarted run numbers from 1: without the resync the page would drop these until the count passed 41.
    await push(1, "dropped-without-a-resync");
    expect(seen).toHaveBeenCalledTimes(1);
    hub.answer = "ResyncRequired";
    hub.instanceId = "run-b";
    await act(async () => {
      hub.state = "Reconnecting";
      hub.onreconnecting?.();
      hub.state = "Connected";
      hub.onreconnected?.();
    });
    await waitFor(() => expect(hub.resumes).toBe(2));

    await push(1, "first-of-the-new-run");
    await push(2, "second-of-the-new-run");
    expect(seen).toHaveBeenCalledTimes(3);
    expect(seen.mock.calls.map((call) => (call[0] as { id: string }).id)).toEqual(["before-restart", "first-of-the-new-run", "second-of-the-new-run"]);
  });

  // #1497: the hub sent the enum's number, so after a restart the page never saw "ResyncRequired" and stayed deaf (recorded from a real restart: "the hub answered 2").
  it("reads the hub's answer as a number too, so a restart still refetches and the new run's pushes are heard", async () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "40", "X-Deluno-Event-Instance": "run-a" }) });
    hub.answer = 1;
    const seen = vi.fn();
    const resync = vi.fn();
    renderHook(() => { useSignalRResync(resync); useSignalREvent("MovieChanged", "dashboard", seen); }, { wrapper });
    await waitFor(() => expect(hub.resumes).toBe(1));
    expect(resync).not.toHaveBeenCalled();

    hub.answer = 2;
    hub.instanceId = "run-b";
    await act(async () => { hub.state = "Reconnecting"; hub.onreconnecting?.(); hub.state = "Connected"; hub.onreconnected?.(); });
    await waitFor(() => expect(resync).toHaveBeenCalledTimes(1));
    await act(async () => { hub.deliver?.({ seq: 1, name: "MovieChanged", subject: "dashboard", at: "", data: { id: "new-run" } }); });
    expect(seen).toHaveBeenCalledTimes(1);
  });

  it("treats an answer it cannot read as a reason to refetch", async () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "40", "X-Deluno-Event-Instance": "run-a" }) });
    hub.answer = "CaughtUp";
    const resync = vi.fn();
    renderHook(() => { useSignalRResync(resync); useSignalREvent("MovieChanged", "dashboard", () => {}); }, { wrapper });
    await waitFor(() => expect(hub.resumes).toBe(1));
    hub.answer = "SomethingNew";
    await act(async () => { hub.state = "Reconnecting"; hub.onreconnecting?.(); hub.state = "Connected"; hub.onreconnected?.(); });
    await waitFor(() => expect(resync).toHaveBeenCalledTimes(1));
  });

  // #1497: the notice stays up until the screens have been told to read again.
  it("stays reconnecting until the hub has answered the resume, then says connected", async () => {
    hub.answer = "ResyncRequired";
    const { result } = renderHook(() => useSignalRStatus(), { wrapper });
    await waitFor(() => expect(result.current).toBe("connected"));

    let release: () => void = () => {};
    hub.hold = new Promise<void>((resolve) => { release = resolve; });
    await act(async () => { hub.state = "Reconnecting"; hub.onreconnecting?.(); hub.state = "Connected"; hub.onreconnected?.(); });
    expect(result.current).toBe("reconnecting");
    await act(async () => { release(); hub.hold = null; await Promise.resolve(); });
    await waitFor(() => expect(result.current).toBe("connected"));
  });

  // #1497: a resume the hub cannot answer is written to the console as one line with the error, and the page still says connected.
  it("writes one console line when the hub cannot answer the resume, and stays connected", async () => {
    const info = vi.spyOn(console, "info").mockImplementation(() => {});
    hub.failResume = true;
    const { result } = renderHook(() => useSignalRStatus(), { wrapper });

    await waitFor(() => expect(info.mock.calls.some((call) => call[0] === "[Deluno live] resume failed" && call[1] instanceof Error)).toBe(true));
    await waitFor(() => expect(result.current).toBe("connected"));
    info.mockRestore();
  });

  // The screens have been told to read again only if the connection is still up; if it dropped meanwhile, the notice stays.
  it("stays reconnecting when the connection drops again before the resume has answered", async () => {
    hub.answer = "ResyncRequired";
    const { result } = renderHook(() => useSignalRStatus(), { wrapper });
    await waitFor(() => expect(result.current).toBe("connected"));

    let release: () => void = () => {};
    hub.hold = new Promise<void>((resolve) => { release = resolve; });
    await act(async () => { hub.state = "Reconnecting"; hub.onreconnecting?.(); hub.state = "Connected"; hub.onreconnected?.(); });
    hub.state = "Reconnecting";
    await act(async () => { release(); hub.hold = null; await Promise.resolve(); await Promise.resolve(); });

    expect(result.current).toBe("reconnecting");
  });

  // A connection that closed is started again by the page; the notice stays until the screens have read, and stays if it dropped once more by then.
  it("after a close and a start again stays reconnecting when the connection is lost before the resume has answered", async () => {
    hub.answer = "ResyncRequired";
    const { result } = renderHook(() => useSignalRStatus(), { wrapper });
    await waitFor(() => expect(result.current).toBe("connected"));
    await waitFor(() => expect(hub.resumes).toBe(1));

    let release: () => void = () => {};
    hub.hold = new Promise<void>((resolve) => { release = resolve; });
    await act(async () => { hub.state = "Disconnected"; hub.onclose?.(); await Promise.resolve(); });
    await waitFor(() => expect(hub.resumes).toBe(2));
    expect(result.current).toBe("reconnecting");

    hub.state = "Reconnecting";
    await act(async () => { release(); hub.hold = null; await Promise.resolve(); await Promise.resolve(); });
    expect(result.current).toBe("reconnecting");
  });
});
