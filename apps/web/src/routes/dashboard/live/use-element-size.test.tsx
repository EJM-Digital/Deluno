import { act, cleanup, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useElementSize } from "./hooks";

/**
 * The height the Dashboard's page is fit to (#1329): read once before the first paint and read again whenever the element is
 * resized by anything, such as a banner appearing above the page, which moves it without the window changing size.
 */
let height = 800;
let observers: Array<{ callback: () => void; disconnected: boolean }> = [];

class FakeResizeObserver {
  private readonly entry: { callback: () => void; disconnected: boolean };

  constructor(callback: () => void) {
    this.entry = { callback, disconnected: false };
    observers.push(this.entry);
  }

  observe() {}

  unobserve() {}

  disconnect() {
    this.entry.disconnected = true;
  }
}

function Page({ onRender }: { onRender: (size: { width: number; height: number }) => void }) {
  const [ref, size] = useElementSize<HTMLDivElement>();
  onRender(size);
  return <div ref={ref} data-testid="page" />;
}

beforeEach(() => {
  height = 800;
  observers = [];
  vi.stubGlobal("ResizeObserver", FakeResizeObserver);
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(() => ({ x: 0, y: 0, left: 0, top: 0, right: 1200, bottom: height, width: 1200, height, toJSON: () => ({}) }) as DOMRect);
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("useElementSize", () => {
  it("reads the size before the first paint, and again when a banner above the page takes some of its height", () => {
    const seen: Array<{ width: number; height: number }> = [];
    render(<Page onRender={(size) => seen.push(size)} />);
    expect(seen.at(-1)).toEqual({ width: 1200, height: 800 });

    height = 752; // a banner 48px tall appears above the page; the window itself has not been resized
    act(() => observers[0]!.callback());

    expect(seen.at(-1)).toEqual({ width: 1200, height: 752 });

    height = 800; // and goes away again
    act(() => observers[0]!.callback());
    expect(seen.at(-1)).toEqual({ width: 1200, height: 800 });
  });

  it("keeps the very same size when the element reports the same one, and stops reading once the page is gone", () => {
    const seen: Array<{ width: number; height: number }> = [];
    const view = render(<Page onRender={(size) => seen.push(size)} />);
    const first = seen.at(-1);

    act(() => observers[0]!.callback());
    expect(seen.at(-1)).toBe(first);

    view.unmount();
    expect(observers[0]!.disconnected).toBe(true);
  });
});
