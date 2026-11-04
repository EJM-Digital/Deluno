import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DashboardLiveExtras } from "../../../lib/api/types/operations";
import { useConnections, useTurnedDown } from "./use-pushed";

// #1329: the two pushed events that light up the lower half of Live: a bar when releases are turned down, a tile when a connection is used.

const handlers = vi.hoisted(() => new Map<string, (event: unknown) => void>());
vi.mock("../../../lib/use-signalr", () => ({
  useLiveEvent: (name: string, handler: (event: unknown) => void) => { handlers.set(name, handler); }
}));

const push = (name: string, event: unknown) => act(() => { handlers.get(name)!(event); });
const turnedDown = (...groups: string[]) => ({ items: groups.map((group) => ({ group })) });

beforeEach(() => { vi.useFakeTimers(); handlers.clear(); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

describe("releases turned down, pushed as they happen", () => {
  it("lights the reason that moved for 1.4 seconds, then lets it go", () => {
    const { result } = renderHook(() => useTurnedDown(null));
    expect(result.current.lit.size).toBe(0);

    push("ReleasesTurnedDown", turnedDown("quality"));
    expect([...result.current.lit]).toEqual(["quality"]);
    act(() => { vi.advanceTimersByTime(1399); });
    expect(result.current.lit.has("quality")).toBe(true);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current.lit.size).toBe(0);
  });

  it("starts the 1.4 seconds again when the same reason moves again, and keeps other reasons lit meanwhile", () => {
    const { result } = renderHook(() => useTurnedDown(null));
    push("ReleasesTurnedDown", turnedDown("quality"));
    act(() => { vi.advanceTimersByTime(1000); });
    push("ReleasesTurnedDown", turnedDown("quality", "size"));
    act(() => { vi.advanceTimersByTime(1000); });
    expect([...result.current.lit].sort()).toEqual(["quality", "size"]);
    act(() => { vi.advanceTimersByTime(400); });
    expect(result.current.lit.size).toBe(0);
  });

  it("lights nothing for a push that names nothing", () => {
    const { result } = renderHook(() => useTurnedDown(null));
    push("ReleasesTurnedDown", turnedDown());
    expect(result.current.lit.size).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("drops its timers when the screen goes away", () => {
    const { unmount } = renderHook(() => useTurnedDown(null));
    push("ReleasesTurnedDown", turnedDown("quality"));
    expect(vi.getTimerCount()).toBe(1);
    unmount();
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe("connections used, pushed as they happen", () => {
  const extras = { asOfUtc: "2026-10-01T00:00:00Z", connections: [] } as unknown as DashboardLiveExtras;

  it("has no connections until the summary has them, and lights none", () => {
    const { result } = renderHook(() => useConnections(null));
    expect(result.current.connections).toBeNull();
    expect(result.current.lit.size).toBe(0);
  });

  it("lights the tile of a connection that was used, then lets it go", () => {
    const { result } = renderHook(() => useConnections(extras));
    expect(result.current.connections).toEqual([]);
    push("ConnectionUsed", { kind: "processor", id: "p1", name: "Weir", countToday: 3, busy: 1, atUtc: "2026-10-01T00:00:01Z" });
    expect(result.current.lit.size).toBe(1);
    act(() => { vi.advanceTimersByTime(1400); });
    expect(result.current.lit.size).toBe(0);
  });
});
