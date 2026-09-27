import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useSlidingPill } from "./use-sliding-pill";

/**
 * jsdom has no ResizeObserver. This stand-in is enough to drive the hook: it
 * records what it is asked to observe and lets a test fire the callback by
 * hand, which is what a real browser does when a watched element's box
 * changes.
 */
class FakeResizeObserver {
  static instances: FakeResizeObserver[] = [];
  observed = new Set<Element>();
  constructor(private readonly callback: ResizeObserverCallback) {
    FakeResizeObserver.instances.push(this);
  }
  observe(element: Element) { this.observed.add(element); }
  unobserve(element: Element) { this.observed.delete(element); }
  disconnect() { this.observed.clear(); }
  fire() { this.callback([], this as unknown as ResizeObserver); }
}

function rect(left: number, width: number) {
  return { left, width, top: 0, height: 0, right: left + width, bottom: 0, x: left, y: 0, toJSON() { return {}; } };
}

function makeEl(left: number, width: number) {
  const el = document.createElement("button");
  el.getBoundingClientRect = () => rect(left, width) as DOMRect;
  document.body.appendChild(el);
  return el;
}

describe("useSlidingPill (#934)", () => {
  const originalResizeObserver = (globalThis as { ResizeObserver?: unknown }).ResizeObserver;

  beforeEach(() => {
    FakeResizeObserver.instances = [];
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = FakeResizeObserver;
  });

  afterEach(() => {
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = originalResizeObserver;
    document.body.innerHTML = "";
  });

  it("measures the active chip and animates when the pick itself changes", () => {
    const track = makeEl(0, 300);
    const a = makeEl(0, 80);
    const b = makeEl(80, 100);

    const { result, rerender } = renderHook(
      ({ active, signal }: { active: "a" | "b"; signal: string }) => useSlidingPill(active, signal),
      { initialProps: { active: "a", signal: "a:All:0|b:Missing:0" } }
    );

    act(() => {
      result.current.trackRef(track);
      result.current.registerElement("a", a);
      result.current.registerElement("b", b);
    });

    expect(result.current.pill).toMatchObject({ left: 0, width: 80, ready: true });

    rerender({ active: "b", signal: "a:All:0|b:Missing:0" });

    expect(result.current.pill).toMatchObject({ left: 80, width: 100, ready: true, animate: true });
  });

  it("re-measures without animating when a watched element resizes", () => {
    const track = makeEl(0, 300);
    const a = makeEl(0, 80);

    const { result } = renderHook(() => useSlidingPill("a", "a:All:0"));

    act(() => {
      result.current.trackRef(track);
      result.current.registerElement("a", a);
    });
    expect(result.current.pill).toMatchObject({ left: 0, width: 80 });

    // The count behind the chip changes its rendered width — a real resize,
    // not a click — and the observer is what is supposed to notice it.
    a.getBoundingClientRect = () => rect(0, 140) as DOMRect;
    act(() => { FakeResizeObserver.instances[0]?.fire(); });

    expect(result.current.pill).toMatchObject({ left: 0, width: 140, ready: true, animate: false });
  });

  it("re-measures without animating when the label/count signal changes", () => {
    const track = makeEl(0, 300);
    const a = makeEl(0, 80);

    const { result, rerender } = renderHook(
      ({ signal }: { signal: string }) => useSlidingPill("a", signal),
      { initialProps: { signal: "a:Missing:0" } }
    );

    act(() => {
      result.current.trackRef(track);
      result.current.registerElement("a", a);
    });

    // The facets count for this chip arrives after first paint and widens it,
    // with no click and (in this test) no observed resize either — the
    // signal string is the net that catches it.
    a.getBoundingClientRect = () => rect(0, 96) as DOMRect;
    rerender({ signal: "a:Missing:19" });

    expect(result.current.pill).toMatchObject({ left: 0, width: 96, ready: true, animate: false });
  });

  it("re-measures on window resize", () => {
    const track = makeEl(0, 300);
    const a = makeEl(0, 80);

    const { result } = renderHook(() => useSlidingPill("a", "a:All:0"));

    act(() => {
      result.current.trackRef(track);
      result.current.registerElement("a", a);
    });

    a.getBoundingClientRect = () => rect(0, 60) as DOMRect;
    act(() => { window.dispatchEvent(new Event("resize")); });

    expect(result.current.pill).toMatchObject({ left: 0, width: 60, ready: true, animate: false });
  });

  it("re-measures once fonts finish loading", async () => {
    const track = makeEl(0, 300);
    const a = makeEl(0, 80);
    let resolveReady: () => void = () => {};
    const ready = new Promise<FontFaceSet>((resolve) => {
      resolveReady = () => resolve({} as FontFaceSet);
    });
    const originalFonts = document.fonts;
    Object.defineProperty(document, "fonts", { configurable: true, value: { ready } });

    const { result } = renderHook(() => useSlidingPill("a", "a:All:0"));
    act(() => {
      result.current.trackRef(track);
      result.current.registerElement("a", a);
    });

    a.getBoundingClientRect = () => rect(0, 72) as DOMRect;
    await act(async () => {
      resolveReady();
      await ready;
    });

    expect(result.current.pill).toMatchObject({ left: 0, width: 72, ready: true, animate: false });
    Object.defineProperty(document, "fonts", { configurable: true, value: originalFonts });
  });
});
