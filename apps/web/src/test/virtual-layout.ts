import { vi } from "vitest";

/**
 * jsdom lays nothing out, so a virtualised shelf measures a zero-size scroller and draws no rows. This gives every element a screen
 * to be drawn on (1200 by 800), and the hidden probe the grid uses to size a poster a narrow width, so the grid gets several
 * columns and the list gets its rows. Call it in `beforeEach`; `vi.restoreAllMocks()` (or the returned function) undoes it.
 */
export function stubVirtualLayout(): () => void {
  const rect = (width: number, height: number) => ({ width, height, top: 0, left: 0, right: width, bottom: height, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
  const spies = [
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return this.style.position === "absolute" ? rect(200, 10) : rect(1200, 800);
    }),
    vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockReturnValue(1200),
    vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockReturnValue(800),
    vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockReturnValue(1200),
    vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(800)
  ];
  return () => spies.forEach((spy) => spy.mockRestore());
}
