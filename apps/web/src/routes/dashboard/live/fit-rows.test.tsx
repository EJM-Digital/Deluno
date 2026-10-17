import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useFittingRows } from "./fit-rows";

/**
 * #1348: the Dashboard's Live board showed "This area could not load" (React error #185, Maximum update depth exceeded) at
 * 390 px with a busy pipeline. The trace names the cause: `useFittingRows` measures in a layout effect after every render
 * and sets what it measured, and a card whose own height depends on how many rows it shows (the "N more" link in its header
 * wraps, the box shrinks) can answer the measure with a different number each time, so the sets never stop.
 *
 * The page below is that card with the layout worked out by hand: with 2 rows showing the box is tall enough for 4, and
 * with 4 showing it is only tall enough for 2. Each row is 50 px tall.
 */
const ROW_PX = 50;
const ROWS = 6;

function stubLayout(host: HTMLElement) {
  const boxFor = (shown: number) => (shown === 2 ? 4 * ROW_PX : 2 * ROW_PX);
  vi.spyOn(host, "getBoundingClientRect").mockImplementation(() => {
    const shown = Number(host.dataset.shown);
    return { top: 0, left: 0, right: 100, width: 100, x: 0, y: 0, bottom: boxFor(shown), height: boxFor(shown), toJSON: () => ({}) };
  });
  host.querySelectorAll<HTMLElement>("[data-fit]").forEach((row, index) => {
    vi.spyOn(row, "getBoundingClientRect").mockImplementation(() => ({ top: index * ROW_PX, left: 0, right: 100, width: 100, x: 0, y: index * ROW_PX, bottom: (index + 1) * ROW_PX, height: ROW_PX, toJSON: () => ({}) }));
  });
}

function Card() {
  const [ref, fits] = useFittingRows();
  const shown = Math.min(fits, ROWS);
  return (
    <div
      ref={(node) => {
        ref.current = node;
        if (node) stubLayout(node);
      }}
      data-shown={shown}
    >
      {Array.from({ length: ROWS }, (_, index) => <p key={index} data-fit="">row {index}</p>)}
    </div>
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("useFittingRows when the card's height depends on the rows it shows", () => {
  it("settles instead of setting a different number on every render", () => {
    const errors = vi.spyOn(console, "error").mockImplementation(() => undefined);
    expect(() => render(<Card />)).not.toThrow();
    expect(errors.mock.calls.flat().join(" ")).not.toMatch(/Maximum update depth/);
  });

  it("keeps the rows hidden that the settled number says do not fit", () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const { container } = render(<Card />);
    const hidden = Array.from(container.querySelectorAll<HTMLElement>("[data-fit]")).filter((row) => row.style.visibility === "hidden").length;
    expect(ROWS - hidden).toBeLessThanOrEqual(4);
    expect(ROWS - hidden).toBeGreaterThanOrEqual(2);
  });
});

describe("useFittingRows watching the card", () => {
  function Plain() {
    const [ref, fits] = useFittingRows();
    return (
      <div ref={(node) => { ref.current = node; }} data-fits={fits}>
        <p data-fit="">row</p>
      </div>
    );
  }

  it("measures again when the card changes size, stops watching and ignores a late callback once it is gone", () => {
    const callbacks: Array<() => void> = [];
    const disconnect = vi.fn();
    const observe = vi.fn();
    vi.stubGlobal("ResizeObserver", class {
      constructor(callback: () => void) { callbacks.push(callback); }
      observe = observe;
      disconnect = disconnect;
    });
    try {
      const { unmount } = render(<Plain />);
      expect(observe).toHaveBeenCalledTimes(1);
      act(() => callbacks[0]());

      unmount();
      expect(disconnect).toHaveBeenCalledTimes(1);
      expect(() => callbacks[0]()).not.toThrow();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("keeps the smaller number when the card swings, and takes a fresh measure once its width changes", () => {
    const callbacks: Array<() => void> = [];
    vi.stubGlobal("ResizeObserver", class {
      constructor(callback: () => void) { callbacks.push(callback); }
      observe() {}
      disconnect() {}
    });
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    let width = 100;
    function WideCard() {
      const [ref, fits] = useFittingRows();
      const shown = Math.min(fits, ROWS);
      return (
        <div
          ref={(node) => {
            ref.current = node;
            if (!node) return;
            stubLayout(node);
            vi.spyOn(node, "getBoundingClientRect").mockImplementation(() => {
              const box = Number(node.dataset.shown) === 2 ? 4 * ROW_PX : 2 * ROW_PX;
              return { top: 0, left: 0, right: width, width, x: 0, y: 0, bottom: box, height: box, toJSON: () => ({}) };
            });
          }}
          data-shown={shown}
        >
          {Array.from({ length: ROWS }, (_, index) => <p key={index} data-fit="">row {index}</p>)}
        </div>
      );
    }
    try {
      const { container } = render(<WideCard />);
      const visible = () => Array.from(container.querySelectorAll<HTMLElement>("[data-fit]")).filter((row) => row.style.visibility !== "hidden").length;
      const settled = visible();
      expect(settled).toBeLessThanOrEqual(4);

      width = 120;
      act(() => callbacks[0]());
      expect(visible()).toBeLessThanOrEqual(4);
      expect(visible()).toBeGreaterThanOrEqual(2);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
