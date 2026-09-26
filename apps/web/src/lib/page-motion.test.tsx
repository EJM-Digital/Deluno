import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render } from "@testing-library/react";
import { CountUp } from "../components/ui/count-up";
import { RadialGauge } from "../components/ui/radial-gauge";
import { installHiddenPageMotionGuard, motionAllowed } from "./page-motion";

// #873: a value that changes while the tab is hidden must be shown at its
// current value on return, not counted up from the value on screen when the
// tab was left.

let visibility: DocumentVisibilityState = "visible";
const frames: FrameRequestCallback[] = [];

function setVisibility(state: DocumentVisibilityState) {
  visibility = state;
  document.dispatchEvent(new Event("visibilitychange"));
}

function flushFrames(at: number) {
  const pending = frames.splice(0);
  pending.forEach((frame) => frame(at));
}

beforeEach(() => {
  visibility = "visible";
  frames.length = 0;
  vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
  vi.spyOn(window, "requestAnimationFrame").mockImplementation((cb) => { frames.push(cb); return frames.length; });
  vi.spyOn(window, "cancelAnimationFrame").mockImplementation(() => {});
  window.matchMedia = vi.fn().mockReturnValue({ matches: false }) as unknown as typeof window.matchMedia;
});

afterEach(() => {
  vi.restoreAllMocks();
  document.documentElement.removeAttribute("data-page-hidden");
});

const shown = (container: HTMLElement) => container.querySelector("[aria-hidden]")?.textContent;

describe("CountUp while the page is hidden (#873)", () => {
  it("shows a value that changed in the background at once, with no tween from the old value", () => {
    const { container, rerender } = render(<CountUp value={10} />);
    setVisibility("hidden");
    rerender(<CountUp value={250} />);
    expect(shown(container)).toBe("250");
    setVisibility("visible");
    expect(frames).toHaveLength(0);
    expect(shown(container)).toBe("250");
  });

  it("lands an animation under way on its final value when the page is hidden", () => {
    const { container, rerender } = render(<CountUp value={0} />);
    rerender(<CountUp value={100} />);
    act(() => flushFrames(performance.now() + 100));
    expect(shown(container)).not.toBe("100");
    act(() => setVisibility("hidden"));
    expect(shown(container)).toBe("100");
  });

  it("still animates a change the person can see", () => {
    const { container, rerender } = render(<CountUp value={0} />);
    rerender(<CountUp value={100} />);
    expect(frames.length).toBeGreaterThan(0);
    expect(shown(container)).toBe("0");
  });

  it("finishes on the first late frame instead of replaying the whole tween", () => {
    const { container, rerender } = render(<CountUp value={0} />);
    rerender(<CountUp value={100} />);
    act(() => flushFrames(performance.now() + 60_000));
    expect(shown(container)).toBe("100");
  });
});

describe("RadialGauge while the page is hidden (#873)", () => {
  it("draws a value that changed in the background at once", () => {
    const { rerender } = render(<RadialGauge value={0.1} label="10%" />);
    setVisibility("hidden");
    rerender(<RadialGauge value={0.9} label="90%" />);
    expect(frames).toHaveLength(0);
  });
});

describe("hidden-page motion guard (#873)", () => {
  it("switches transitions off while hidden and back on two frames after the page is shown", () => {
    const stop = installHiddenPageMotionGuard();
    setVisibility("hidden");
    expect(document.documentElement.hasAttribute("data-page-hidden")).toBe(true);
    setVisibility("visible");
    expect(document.documentElement.hasAttribute("data-page-hidden")).toBe(true);
    flushFrames(0);
    flushFrames(0);
    expect(document.documentElement.hasAttribute("data-page-hidden")).toBe(false);
    stop();
  });

  it("allows motion only when the page is visible", () => {
    expect(motionAllowed()).toBe(true);
    setVisibility("hidden");
    expect(motionAllowed()).toBe(false);
  });
});
