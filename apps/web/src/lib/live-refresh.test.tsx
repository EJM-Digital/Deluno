import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryObserver } from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";
import { UNSAFE_DataRouterContext } from "react-router-dom";
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

describe("useLiveRefresh with no refresh of its own (#1179)", () => {
  it("asks the router to read the page's data again", async () => {
    const revalidate = vi.fn();
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(UNSAFE_DataRouterContext.Provider, { value: { router: { revalidate } } as never }, children);
    renderHook(() => useLiveRefresh({ events: ["SettingsChanged"] }), { wrapper });

    push("SettingsChanged");
    await wait(1_500);
    expect(revalidate).toHaveBeenCalledTimes(1);
  });

  it("does nothing, and does not fail, where there is no router", async () => {
    renderHook(() => useLiveRefresh({ events: ["SettingsChanged"] }));
    push("SettingsChanged");
    await wait(1_500);
    expect(handlers.has("SettingsChanged")).toBe(true);
  });

  it("listens only for ConfigChanged when the page names areas but no events", async () => {
    const refresh = vi.fn();
    renderHook(() => useLiveRefresh({ areas: ["tags"] }, refresh));
    expect([...handlers.keys()]).toEqual(["ConfigChanged"]);
    push("ConfigChanged", { area: "tags" });
    await wait(1_500);
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("listens for nothing when the page names neither events nor areas, and ignores an empty list of areas", () => {
    renderHook(() => useLiveRefresh({}, vi.fn()));
    renderHook(() => useLiveRefresh({ events: ["LibraryChanged"], areas: [] }, vi.fn()));
    expect([...handlers.keys()]).toEqual(["LibraryChanged"]);
  });

  it("ignores a ConfigChanged push that names no area", async () => {
    const refresh = vi.fn();
    renderHook(() => useLiveRefresh({ areas: ["tags"] }, refresh));
    push("ConfigChanged", null);
    push("ConfigChanged", {});
    await wait(1_500);
    expect(refresh).not.toHaveBeenCalled();
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
