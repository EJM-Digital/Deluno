import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * #1508: a row that arrives live is marked fresh for a moment and then settles, and a repeated push of the same
 * event neither adds a row nor wakes the mark again. The other rows are left as they are.
 */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => {
      const latest = useRef(handler);
      useEffect(() => { latest.current = handler; });
      useEffect(() => {
        const call = (payload: unknown) => latest.current(payload);
        const set = handlers.get(name) ?? new Set();
        set.add(call);
        handlers.set(name, set);
        return () => { set.delete(call); };
      }, [name]);
    }
  };
});

const { useLiveActivity } = await import("./live-events");

const event = (id: string, message: string) => ({ id, message, label: null, severity: "info", category: "download.client.started", createdUtc: "2026-11-06T09:00:00Z", relatedEntityType: null, relatedEntityId: null });

function send(payload: unknown) {
  act(() => {
    for (const handler of handlers.get("ActivityEventAdded") ?? []) handler(payload);
  });
}

afterEach(() => {
  vi.useRealTimers();
  handlers.clear();
});

describe("a live Activity row", () => {
  it("is fresh when it arrives, settles after a moment, and a second push of the same event changes nothing", () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useLiveActivity([]));

    send(event("a", "Arrival started downloading."));
    send(event("b", "Sicario started downloading."));
    send(event("a", "Arrival started downloading."));

    expect(result.current.map((row) => [row.id, row.fresh])).toEqual([["b", true], ["a", true]]);
    act(() => { vi.advanceTimersByTime(60_000); });

    expect(result.current.map((row) => [row.id, row.fresh])).toEqual([["b", false], ["a", false]]);
  });
});
