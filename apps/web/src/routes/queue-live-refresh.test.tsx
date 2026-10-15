import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../lib/route-cache";
import { sharedSources } from "../lib/shared-sources";
import { useCoalescedRefresh, useTransfersLiveEvents } from "./queue-live";

/**
 * Transfers follows the server's pushes with no timer: each event reads again only the lists it can change, a burst reads them
 * once, and a manual import's row moves only when the import job itself says it finished.
 */

const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  const useListen = (name: string, handler: (payload: unknown) => void) => {
    const latest = useRef(handler);
    useEffect(() => { latest.current = handler; });
    useEffect(() => {
      const call = (payload: unknown) => latest.current(payload);
      const set = handlers.get(name) ?? new Set();
      set.add(call);
      handlers.set(name, set);
      return () => { set.delete(call); };
    }, [name]);
  };
  return {
    ...(await importOriginal<typeof import("../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => useListen(name, handler),
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => useListen(name, handler),
    useLiveWatching: () => {}
  };
});

function send(name: string, payload: unknown = {}) {
  act(() => { for (const handler of handlers.get(name) ?? []) handler(payload); });
}

function mount() {
  const refresh = { dispatches: vi.fn(), activity: vi.fn(), jobs: vi.fn(), telemetry: vi.fn() };
  renderHook(() => useTransfersLiveEvents(refresh.dispatches, refresh.activity, refresh.jobs, refresh.telemetry));
  return refresh;
}

beforeEach(() => {
  handlers.clear();
  queryClient.clear();
});

describe("what each push reads again on Transfers", () => {
  it("JobChanged reads the jobs list, and a failed job also reads the failed-jobs list", () => {
    const refresh = mount();
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");

    send("JobChanged", { state: "completed" });
    expect(refresh.jobs).toHaveBeenCalledTimes(1);
    expect(invalidate).not.toHaveBeenCalled();

    send("JobChanged", { state: "failed" });
    expect(refresh.jobs).toHaveBeenCalledTimes(2);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: sharedSources.failedJobs.key, exact: true });
    invalidate.mockRestore();
  });

  it("QueueItemAdded reads the telemetry, because a new item has its client, size and route only in the reading", () => {
    const refresh = mount();

    send("QueueItemAdded", { id: "q9" });

    expect(refresh.telemetry).toHaveBeenCalledTimes(1);
    expect(refresh.jobs).not.toHaveBeenCalled();
    expect(refresh.dispatches).not.toHaveBeenCalled();
  });

  it("ImportStateChanged reads the jobs, the dispatches and the telemetry", () => {
    const refresh = mount();

    send("ImportStateChanged", { jobId: "j1", state: "completed" });

    expect(refresh.jobs).toHaveBeenCalledTimes(1);
    expect(refresh.dispatches).toHaveBeenCalledTimes(1);
    expect(refresh.telemetry).toHaveBeenCalledTimes(1);
  });

  it.each(["DispatchGrabCompleted", "DispatchDetected", "DispatchImportStarted", "DispatchImportCompleted"])("%s reads the dispatches", (name) => {
    const refresh = mount();

    send(name, { dispatchId: "d1" });

    expect(refresh.dispatches).toHaveBeenCalledTimes(1);
    expect(refresh.jobs).not.toHaveBeenCalled();
    expect(refresh.telemetry).not.toHaveBeenCalled();
  });
});

describe("a burst of pushes reads a list once", () => {
  it("coalesces many calls in the wait into one read of each list it names", () => {
    vi.useFakeTimers();
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const { result } = renderHook(() => useCoalescedRefresh([sharedSources.telemetry.key, sharedSources.failedJobs.key]));

    for (let burst = 0; burst < 10; burst++) result.current();
    expect(invalidate).not.toHaveBeenCalled();
    act(() => { vi.advanceTimersByTime(1_000); });

    expect(invalidate).toHaveBeenCalledTimes(2);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: sharedSources.telemetry.key, exact: true });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: sharedSources.failedJobs.key, exact: true });
    invalidate.mockClear();
    result.current();
    act(() => { vi.advanceTimersByTime(1_000); });
    expect(invalidate).toHaveBeenCalledTimes(2);
    invalidate.mockRestore();
    vi.useRealTimers();
  });

  it("reads nothing before the wait is over and never on a timer of its own", () => {
    vi.useFakeTimers();
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    renderHook(() => useCoalescedRefresh([sharedSources.telemetry.key]));

    act(() => { vi.advanceTimersByTime(60_000); });

    expect(invalidate).not.toHaveBeenCalled();
    invalidate.mockRestore();
    vi.useRealTimers();
  });
});
