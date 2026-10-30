import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { RangeAxis, RangeSlider, scalePercent, scaleTicks } from "./range-slider";

describe("the slider scale", () => {
  it("places a value on the track, clamped, linear or square-root, and nowhere on an empty scale", () => {
    expect(scalePercent(50, 100)).toBe(50);
    expect(scalePercent(25, 100, "sqrt")).toBe(50);
    expect(scalePercent(-5, 100)).toBe(0);
    expect(scalePercent(500, 100)).toBe(100);
    expect(scalePercent(5, 0)).toBe(0);
  });

  it("draws evenly spaced ticks at numbers worth printing", () => {
    expect(scaleTicks(100)).toEqual([0, 20, 50, 100]);
    expect(scaleTicks(130, "sqrt")).toEqual([0, 5, 20, 50, 100, 130]);
    expect(scaleTicks(100, "linear", 2)).toEqual([0, 50, 100]);
    expect(scaleTicks(50)).toEqual([0, 10, 20, 50]);
    expect(scaleTicks(0, "linear", 2)).toEqual([0]);
  });

  it("labels the ruler, with a unit on the last tick only when given", () => {
    const { rerender } = render(<RangeAxis scaleMax={100} />);
    expect(screen.getByText("100")).toBeTruthy();
    rerender(<RangeAxis scaleMax={100} scale="sqrt" unit="GB" format={(v) => `${v}!`} className="x" />);
    expect(screen.getByText("100! GB")).toBeTruthy();
  });
});

function setup(props: Partial<React.ComponentProps<typeof RangeSlider>> = {}) {
  const onChange = vi.fn();
  render(
    <RangeSlider min={20} max={60} step={1} scaleMax={100} minLabel="Smallest" maxLabel="Largest" formatValue={(v) => `${v} units`} onChange={onChange} {...props} />
  );
  return { onChange, low: screen.getByLabelText("Smallest") as HTMLInputElement, high: screen.getByLabelText("Largest") as HTMLInputElement };
}

describe("the two-handle slider", () => {
  it("reads out each handle in real units", () => {
    const { low, high } = setup();
    expect(low.getAttribute("aria-valuetext")).toBe("20 units");
    expect(high.getAttribute("aria-valuetext")).toBe("60 units");
    expect(low.value).toBe("200");
    expect(high.value).toBe("600");
  });

  it("moving a handle reports the new band and never lets the handles cross", () => {
    const { onChange, low, high } = setup();
    fireEvent.change(low, { target: { value: "300" } });
    expect(onChange).toHaveBeenLastCalledWith({ min: 30, max: 60 });
    fireEvent.change(low, { target: { value: "900" } });
    expect(onChange).toHaveBeenLastCalledWith({ min: 60, max: 60 });
    fireEvent.change(high, { target: { value: "800" } });
    expect(onChange).toHaveBeenLastCalledWith({ min: 20, max: 80 });
    fireEvent.change(high, { target: { value: "100" } });
    expect(onChange).toHaveBeenLastCalledWith({ min: 10, max: 10 });
  });

  it("arrow, page, home and end keys move a handle by real size", () => {
    const { onChange, low, high } = setup();
    const press = (el: HTMLElement, key: string) => fireEvent.keyDown(el, { key });
    press(low, "ArrowRight"); expect(onChange).toHaveBeenLastCalledWith({ min: 21, max: 60 });
    press(low, "ArrowUp"); expect(onChange).toHaveBeenLastCalledWith({ min: 21, max: 60 });
    press(low, "ArrowLeft"); expect(onChange).toHaveBeenLastCalledWith({ min: 19, max: 60 });
    press(low, "ArrowDown"); expect(onChange).toHaveBeenLastCalledWith({ min: 19, max: 60 });
    press(low, "PageUp"); expect(onChange).toHaveBeenLastCalledWith({ min: 30, max: 60 });
    press(low, "PageDown"); expect(onChange).toHaveBeenLastCalledWith({ min: 10, max: 60 });
    press(low, "Home"); expect(onChange).toHaveBeenLastCalledWith({ min: 0, max: 60 });
    press(low, "End"); expect(onChange).toHaveBeenLastCalledWith({ min: 60, max: 60 });
    press(high, "ArrowRight"); expect(onChange).toHaveBeenLastCalledWith({ min: 20, max: 61 });
    press(high, "Home"); expect(onChange).toHaveBeenLastCalledWith({ min: 0, max: 0 });
    press(high, "End"); expect(onChange).toHaveBeenLastCalledWith({ min: 20, max: 100 });
    const calls = onChange.mock.calls.length;
    press(high, "Tab");
    expect(onChange).toHaveBeenCalledTimes(calls);
  });

  it("moving a handle on a square-root track reports the real size under it", () => {
    const { onChange, high } = setup({ scaleMax: 100, scale: "sqrt", min: 0, max: 25 });
    fireEvent.change(high, { target: { value: "600" } });
    expect(onChange).toHaveBeenLastCalledWith({ min: 0, max: 36 });
  });

  it("keeps a fractional step to one decimal and clamps at the ends", () => {
    const { onChange, low } = setup({ min: 0.1, max: 5, step: 0.1, scaleMax: 130, scale: "sqrt" });
    fireEvent.keyDown(low, { key: "ArrowLeft" });
    expect(onChange).toHaveBeenLastCalledWith({ min: 0, max: 5 });
    fireEvent.keyDown(low, { key: "ArrowRight" });
    expect(onChange).toHaveBeenLastCalledWith({ min: 0.2, max: 5 });
  });

  it("reads 0 to 0 as off, and a 0 limit as no upper limit, when asked to", () => {
    const off = setup({ min: 0, max: 0, zeroMaxIsUnlimited: true });
    expect(off.high.value).toBe("0");
    expect(off.low.value).toBe("0");
    cleanup();
    const unlimited = setup({ min: 10, max: 0, zeroMaxIsUnlimited: true });
    expect(unlimited.high.value).toBe("1000");
  });

  it("draws the usual band behind the track, at least a sliver wide", () => {
    const { container } = render(
      <RangeSlider min={1} max={2} step={1} scaleMax={100} typical={{ min: 5, max: 5 }} minLabel="a" maxLabel="b" onChange={() => undefined} className="mine" />
    );
    const band = container.querySelector(".deluno-range-typical") as HTMLElement;
    expect(band.style.width).toBe("0.75%");
    expect(container.firstElementChild!.className).toContain("mine");
  });

  it("works without a spoken format or a usual band", () => {
    const { container } = render(<RangeSlider min={1} max={2} step={1} scaleMax={100} minLabel="a" maxLabel="b" onChange={() => undefined} />);
    expect(container.querySelector(".deluno-range-typical")).toBeNull();
    expect(screen.getByLabelText("a").hasAttribute("aria-valuetext")).toBe(false);
  });
});
