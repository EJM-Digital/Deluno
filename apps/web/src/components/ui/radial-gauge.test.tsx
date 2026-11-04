import { act, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RadialGauge } from "./radial-gauge";

const TRACK = (240 / 360) * 2 * Math.PI * 42;

let visibility: DocumentVisibilityState = "visible";
let clock = 1_000;
let frames = new Map<number, FrameRequestCallback>();
let nextFrame = 1;

function arcs(container: HTMLElement) {
  const circles = Array.from(container.querySelectorAll("circle"));
  return { track: circles[0], fill: circles[1], tip: circles[2] };
}

/** How much of the arc is filled, 0 to 1, read back from the drawing. */
function filled(container: HTMLElement) {
  return Number(arcs(container).fill.getAttribute("stroke-dasharray")!.split(" ")[0]) / TRACK;
}

function runFrame(at: number) {
  const pending = Array.from(frames.entries());
  frames = new Map();
  act(() => pending.forEach(([, frame]) => frame(at)));
}

beforeEach(() => {
  visibility = "visible";
  clock = 1_000;
  frames = new Map();
  nextFrame = 1;
  vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
  vi.spyOn(performance, "now").mockImplementation(() => clock);
  vi.spyOn(window, "requestAnimationFrame").mockImplementation((cb) => {
    const id = nextFrame++;
    frames.set(id, cb);
    return id;
  });
  vi.spyOn(window, "cancelAnimationFrame").mockImplementation((id) => { frames.delete(id); });
  window.matchMedia = vi.fn().mockReturnValue({ matches: false }) as unknown as typeof window.matchMedia;
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("a radial gauge", () => {
  it("prints the number and the caption inside the arc", () => {
    const { getByText } = render(<RadialGauge value={0.5} label="50%" caption="of the disk" />);
    expect(getByText("50%")).toBeInTheDocument();
    expect(getByText("of the disk")).toBeInTheDocument();
  });

  it("leaves out the caption line when there is none", () => {
    const { container } = render(<RadialGauge value={0.5} label="50%" />);
    expect(container.querySelectorAll("span")).toHaveLength(1);
  });

  it("passes its class on to the outer box and hides the drawing from screen readers", () => {
    const { container } = render(<RadialGauge value={0.5} label="50%" className="size-24" />);
    expect(container.firstElementChild).toHaveClass("size-24");
    expect(container.querySelector("svg")).toHaveAttribute("aria-hidden", "true");
  });

  it("draws the filled part of the arc for the value, and a glow at its tip", () => {
    const { container } = render(<RadialGauge value={0.5} label="50%" />);
    const { track, fill, tip } = arcs(container);
    expect(filled(container)).toBeCloseTo(0.5, 5);
    expect(fill).toHaveAttribute("stroke-dasharray", `${TRACK * 0.5} ${2 * Math.PI * 42}`);
    expect(track).toHaveAttribute("stroke-dasharray", `${TRACK} ${2 * Math.PI * 42}`);
    expect(fill).toHaveAttribute("transform", "rotate(150 50 50)");
    expect(track).toHaveAttribute("transform", "rotate(150 50 50)");
    for (const circle of [track, fill]) {
      expect(circle).toHaveAttribute("cx", "50");
      expect(circle).toHaveAttribute("cy", "50");
      expect(circle).toHaveAttribute("r", "42");
    }
    expect(container.querySelector("svg")).toHaveAttribute("viewBox", "0 0 100 100");
    expect(container.firstElementChild).toHaveClass("relative", "flex");
    // Halfway round a 240 degree sweep that starts at 150 degrees ends at 270 degrees: straight up.
    expect(Number(tip.getAttribute("cx"))).toBeCloseTo(50, 5);
    expect(Number(tip.getAttribute("cy"))).toBeCloseTo(8, 5);
  });

  it("puts the glow at the end of a full arc, 30 degrees past the right-hand side", () => {
    const { container } = render(<RadialGauge value={1} label="100%" />);
    const { tip } = arcs(container);
    expect(Number(tip.getAttribute("cx"))).toBeCloseTo(50 + 42 * Math.cos(Math.PI / 6), 5);
    expect(Number(tip.getAttribute("cy"))).toBeCloseTo(71, 5);
  });

  it("shows no glow at empty, or for a sliver too small to see", () => {
    expect(arcs(render(<RadialGauge value={0} label="0%" />).container).tip).toBeUndefined();
    expect(arcs(render(<RadialGauge value={0.01} label="1%" />).container).tip).toBeUndefined();
    expect(arcs(render(<RadialGauge value={0.011} label="1%" />).container).tip).toBeDefined();
  });

  it.each([
    [2, 1],
    [-1, 0],
    [Number.NaN, 0],
    [Number.POSITIVE_INFINITY, 0]
  ])("clamps %s to %s rather than drawing off the arc", (value, expected) => {
    const { container } = render(<RadialGauge value={value} label="x" />);
    expect(filled(container)).toBeCloseTo(expected, 5);
  });

  it.each([
    ["primary", "hsl(var(--primary))"],
    ["success", "hsl(var(--success))"],
    ["warning", "hsl(var(--warning))"],
    ["danger", "hsl(var(--destructive))"],
    ["info", "hsl(var(--info))"]
  ] as const)("colours the %s tone", (tone, stroke) => {
    const { container } = render(<RadialGauge value={0.5} tone={tone} label="x" />);
    const { fill, tip } = arcs(container);
    expect(fill).toHaveAttribute("stroke", stroke);
    expect(tip).toHaveAttribute("fill", stroke);
  });

  it("uses the primary colour unless told otherwise", () => {
    const { container } = render(<RadialGauge value={0.5} label="x" />);
    expect(arcs(container).fill).toHaveAttribute("stroke", "hsl(var(--primary))");
  });
});

describe("a radial gauge whose value changes", () => {
  it("sweeps from where it was to the new value over 650 milliseconds, easing out", () => {
    const { container, rerender } = render(<RadialGauge value={0.2} label="x" />);
    expect(frames.size).toBe(0);
    rerender(<RadialGauge value={0.9} label="x" />);
    expect(filled(container)).toBeCloseTo(0.2, 5);

    runFrame(clock + 325);
    // Halfway through the time is 87.5% of the way, because the sweep eases out.
    expect(filled(container)).toBeCloseTo(0.2 + 0.7 * 0.875, 5);
    expect(frames.size).toBe(1);

    runFrame(clock + 650);
    expect(arcs(container).fill).toHaveAttribute("stroke-dasharray", `${TRACK * 0.9} ${2 * Math.PI * 42}`);
    expect(frames.size).toBe(0);
  });

  it("does not run backwards if a frame is stamped before the sweep began", () => {
    const { container, rerender } = render(<RadialGauge value={0.2} label="x" />);
    rerender(<RadialGauge value={0.8} label="x" />);
    runFrame(clock - 100);
    expect(filled(container)).toBeCloseTo(0.2, 5);
  });

  it("lands on the value even if the first frame arrives late", () => {
    const { container, rerender } = render(<RadialGauge value={0.2} label="x" />);
    rerender(<RadialGauge value={0.8} label="x" />);
    runFrame(clock + 60_000);
    expect(filled(container)).toBeCloseTo(0.8, 5);
  });

  it("starts the next sweep from where the last one had got to", () => {
    const { container, rerender } = render(<RadialGauge value={0} label="x" />);
    rerender(<RadialGauge value={1} label="x" />);
    runFrame(clock + 325);
    const midway = filled(container);
    expect(midway).toBeCloseTo(0.875, 5);

    rerender(<RadialGauge value={0} label="x" />);
    expect(filled(container)).toBeCloseTo(midway, 5);
    runFrame(clock + 325);
    expect(filled(container)).toBeCloseTo(midway * (1 - 0.875), 5);
  });

  it("stops the sweep it was drawing when the value changes again, and when it goes away", () => {
    const { rerender, unmount } = render(<RadialGauge value={0} label="x" />);
    rerender(<RadialGauge value={1} label="x" />);
    const first = Array.from(frames.keys())[0];
    rerender(<RadialGauge value={0.5} label="x" />);
    expect(window.cancelAnimationFrame).toHaveBeenCalledWith(first);
    const second = Array.from(frames.keys())[0];
    unmount();
    expect(window.cancelAnimationFrame).toHaveBeenCalledWith(second);
    expect(frames.size).toBe(0);
  });

  it("does nothing when the value is rewritten to what it already is", () => {
    const { rerender } = render(<RadialGauge value={0.4} label="x" />);
    rerender(<RadialGauge value={0.4} label="y" />);
    expect(frames.size).toBe(0);
  });

  it("jumps straight to the value when the page is hidden", () => {
    const { container, rerender } = render(<RadialGauge value={0.1} label="x" />);
    visibility = "hidden";
    rerender(<RadialGauge value={0.9} label="x" />);
    expect(frames.size).toBe(0);
    expect(filled(container)).toBeCloseTo(0.9, 5);
  });

  it("jumps straight to the value when the person asked for less motion", () => {
    window.matchMedia = vi.fn().mockReturnValue({ matches: true }) as unknown as typeof window.matchMedia;
    const { container, rerender } = render(<RadialGauge value={0.1} label="x" />);
    rerender(<RadialGauge value={0.9} label="x" />);
    expect(frames.size).toBe(0);
    expect(filled(container)).toBeCloseTo(0.9, 5);
  });

  it("finishes at once if the page is hidden part-way through a sweep", () => {
    const { container, rerender } = render(<RadialGauge value={0.1} label="x" />);
    rerender(<RadialGauge value={0.9} label="x" />);
    runFrame(clock + 100);
    expect(filled(container)).toBeLessThan(0.9);

    act(() => {
      visibility = "hidden";
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expect(frames.size).toBe(0);
    expect(filled(container)).toBeCloseTo(0.9, 5);
  });

  it("stops listening for the page being hidden once the gauge is gone", () => {
    const remove = vi.spyOn(document, "removeEventListener");
    const { rerender, unmount } = render(<RadialGauge value={0.1} label="x" />);
    rerender(<RadialGauge value={0.9} label="x" />);
    unmount();
    expect(remove).toHaveBeenCalledWith("visibilitychange", expect.any(Function));
  });
});
