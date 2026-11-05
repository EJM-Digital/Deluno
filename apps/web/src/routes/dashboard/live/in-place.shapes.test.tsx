import { act, render, renderHook, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CardContext } from "./card";
import { byStage, laneCounts, type JourneyCard } from "./journey";
import { FRESH_ROW_MS, useLiveActivity } from "../live-events";
import { ActivityStream } from "./stream";
import { InstanceCard } from "../system/band";
import { NO_TITLES } from "./titles";
import { Journey } from "./track";

/**
 * More of the Dashboard's in-place rules (#1508): the narrow Pipeline, a live Activity line that stops being new, the Activity list
 * that stays in the page when it empties, and a health ring that says nothing until the checks have been read.
 */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../../lib/use-signalr")>()),
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

const context: CardContext = { titles: NO_TITLES, arrivals: new Set(), indexers: 3 };
const card = (extra: Partial<JourneyCard>): JourneyCard => ({ key: "qb:1", stage: "downloading", title: "The Fall Guy (2024)", state: { tone: "info", label: "Downloading" }, to: "/queue", progress: 10, dispatchId: "d1", ...extra });

function board(cards: JourneyCard[]) {
  const grouped = byStage(cards);
  return <MemoryRouter><Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={context} hasProcessor usual={{}} by={undefined} lastArrival={null} /></MemoryRouter>;
}

describe("the narrow Pipeline", () => {
  const rect = HTMLElement.prototype.getBoundingClientRect;
  const observer = globalThis.ResizeObserver;
  beforeEach(() => {
    globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
    HTMLElement.prototype.getBoundingClientRect = function sized() {
      const name = typeof this.className === "string" ? this.className : "";
      const make = (width: number, height: number) => ({ width, height, top: 0, left: 0, right: width, bottom: height, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
      return name.includes("lj ") ? make(360, 600) : make(0, 0);
    };
  });
  afterEach(() => {
    HTMLElement.prototype.getBoundingClientRect = rect;
    globalThis.ResizeObserver = observer;
  });

  it("keeps a card's element when the card moves to another station, and places it under that station", () => {
    const { container, rerender } = render(board([card({})]));
    expect(container.querySelector(".lj-stack")).not.toBeNull();
    const before = container.querySelector<HTMLElement>(".lj-token")!;
    rerender(board([card({ key: "handoff:9", stage: "processor", state: { tone: "info", label: "Being cleaned" } })]));
    const after = container.querySelector<HTMLElement>(".lj-token")!;
    expect(after).toBe(before);
    // Its place is the processor's station (CSS order), not a different parent.
    const station = container.querySelector<HTMLElement>('[data-station="processor"]')!;
    expect(Number(after.style.order)).toBeGreaterThan(Number(station.style.order));
    expect(Number(after.style.order)).toBeLessThan(Number(station.style.order) + 1000);
  });

  it("keeps the other cards' elements when one card is added or leaves", () => {
    const { container, rerender } = render(board([card({ key: "a", dispatchId: "da" }), card({ key: "b", dispatchId: "db", stage: "grabbed" })]));
    const tokens = Array.from(container.querySelectorAll<HTMLElement>(".lj-token"));
    rerender(board([card({ key: "a", dispatchId: "da" }), card({ key: "b", dispatchId: "db", stage: "grabbed" }), card({ key: "c", dispatchId: "dc" })]));
    const again = Array.from(container.querySelectorAll<HTMLElement>(".lj-token"));
    for (const token of tokens) expect(again).toContain(token);
  });
});

describe("a live Activity line", () => {
  beforeEach(() => { vi.useFakeTimers(); handlers.clear(); });
  afterEach(() => { vi.useRealTimers(); });

  it("stops being new after a few seconds, so a redraw never slides it in again", () => {
    const { result } = renderHook(() => useLiveActivity([]));
    act(() => { for (const call of handlers.get("ActivityEventAdded") ?? []) call({ id: "e1", message: "Imported Sintel", category: "import", createdUtc: new Date().toISOString() }); });
    expect(result.current[0]).toMatchObject({ id: "e1", fresh: true });
    act(() => { vi.advanceTimersByTime(FRESH_ROW_MS + 10); });
    expect(result.current[0]).toMatchObject({ id: "e1", fresh: false });
  });
});

describe("the Activity list", () => {
  it("stays the same element when it empties and fills again", () => {
    const row = { id: "e1", category: "download.dispatch.recorded", message: "Deluno sent Sintel (2010) to qBittorrent.", createdUtc: new Date().toISOString(), relatedEntityType: null, relatedEntityId: null, severity: null, fresh: false };
    const view = (rows: typeof row[]) => <MemoryRouter><ActivityStream rows={rows as never} titles={NO_TITLES} /></MemoryRouter>;
    const { rerender } = render(view([row]));
    const list = screen.getByTestId("live-stream");
    rerender(view([]));
    expect(screen.getByTestId("live-stream")).toBe(list);
    rerender(view([row]));
    expect(screen.getByTestId("live-stream")).toBe(list);
  });
});

describe("the health ring before the checks have been read", () => {
  it("says neither \"All good\" nor \"0 of 0\", and keeps the room the words take", () => {
    const { container, rerender } = render(<InstanceCard instance={null} summary={undefined} health={{ pass: 0, total: 0, need: 0, anyBad: false, read: false }} short={false} onJump={() => {}} />);
    expect(screen.queryByText("All good")).toBeNull();
    expect(container.textContent).not.toContain("of 0");
    expect(container.querySelector(".sy-needs")).not.toBeNull();
    rerender(<InstanceCard instance={null} summary={undefined} health={{ pass: 7, total: 7, need: 0, anyBad: false, read: true }} short={false} onJump={() => {}} />);
    expect(screen.getByText("All good")).toBeInTheDocument();
  });
});
