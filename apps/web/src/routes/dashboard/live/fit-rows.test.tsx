import { cleanup, render } from "@testing-library/react";
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
