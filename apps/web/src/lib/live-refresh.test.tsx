import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryObserver } from "@tanstack/react-query";
import { queryClient, defineSources } from "./route-cache";

const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());

vi.mock("./use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard" },
  useSignalREvents: (events: readonly string[], _subjects: unknown, handler: (name: string, payload: unknown) => void) => {
    for (const name of events) {
      const set = handlers.get(name) ?? new Set();
      set.add((payload) => handler(name, payload));
      handlers.set(name, set);
    }
  }
}));

const { useLiveRefresh, useCoalescedSourceRefresh } = await import("./live-refresh");

const push = (name: string, payload: unknown = {}) => {
  for (const handler of handlers.get(name) ?? []) handler(payload);
};
const wait = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

beforeEach(() => {
  vi.useFakeTimers();
  handlers.clear();
  queryClient.clear();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useLiveRefresh (#1179)", () => {
  it("refreshes for its events and for a ConfigChanged naming its area, and for nothing else", async () => {
    const refresh = vi.fn();
    renderHook(() => useLiveRefresh({ events: ["SettingsChanged"], areas: ["tags"] }, refresh));

    push("ConfigChanged", { area: "retention" });
    await wait(1_500);
    expect(refresh).not.toHaveBeenCalled();

    push("ConfigChanged", { area: "tags" });
    await wait(1_500);
    expect(refresh).toHaveBeenCalledTimes(1);

    push("SettingsChanged");
    push("SettingsChanged");
    await wait(1_500);
    expect(refresh).toHaveBeenCalledTimes(2);
  });

  it("does not listen for ConfigChanged when the page names no area", () => {
    renderHook(() => useLiveRefresh({ events: ["LibraryChanged"] }, vi.fn()));
    expect(handlers.has("ConfigChanged")).toBe(false);
    expect(handlers.has("LibraryChanged")).toBe(true);
  });
});

describe("useCoalescedSourceRefresh (#1179)", () => {
  it("reads only the lists the events named, each once", async () => {
    const a = vi.fn(() => Promise.resolve(1));
    const b = vi.fn(() => Promise.resolve(2));
    const sources = defineSources({
      a: { key: ["t", "a"], label: "A", fetch: a },
      b: { key: ["t", "b"], label: "B", fetch: b }
    });
    // Both lists are on screen, so an invalidation reads them again.
    const watchA = new QueryObserver(queryClient, { queryKey: sources.a.key, queryFn: sources.a.fetch }).subscribe(() => {});
    const watchB = new QueryObserver(queryClient, { queryKey: sources.b.key, queryFn: sources.b.fetch }).subscribe(() => {});
    await wait(50);
    a.mockClear();
    b.mockClear();

    const { result } = renderHook(() => useCoalescedSourceRefresh(100));
    act(() => {
      result.current(sources.a);
      result.current(sources.a);
    });
    await wait(1_500);

    expect(a).toHaveBeenCalledTimes(1);
    expect(b).not.toHaveBeenCalled();
    watchA();
    watchB();
  });
});
