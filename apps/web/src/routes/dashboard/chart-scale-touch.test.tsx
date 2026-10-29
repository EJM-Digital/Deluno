import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TraceChart } from "./system/band";

describe("the reading on a touch screen", () => {
  it("a tap shows the reading and stays after the finger lifts, and a tap elsewhere hides it", () => {
    const { container } = render(<div><TraceChart values={[10, 20, 30, 40]} scale={{ kind: "percent" }} colour="red" /><button type="button">Elsewhere</button></div>);
    const layer = container.querySelector<HTMLElement>(".cs-layer")!;
    layer.getBoundingClientRect = () => ({ left: 0, right: 200, top: 0, bottom: 50, width: 200, height: 50, x: 0, y: 0, toJSON: () => ({}) });
    expect(container.querySelector(".cs-read")).toBeNull();

    fireEvent.pointerDown(layer, { clientX: 200, pointerType: "touch" });
    expect(container.querySelector(".cs-read")?.textContent).toMatch(/· 40%$/);

    // A finger leaving the layer after the tap must not hide the reading (a mouse leaving does).
    fireEvent.pointerLeave(layer, { pointerType: "touch" });
    expect(container.querySelector(".cs-read")).not.toBeNull();

    // A tap inside the chart moves the reading rather than hiding it.
    fireEvent.pointerDown(layer, { clientX: 0, pointerType: "touch" });
    expect(container.querySelector(".cs-read")?.textContent).toMatch(/· 10%$/);

    fireEvent.pointerDown(container.querySelector("button")!, { pointerType: "touch" });
    expect(container.querySelector(".cs-read")).toBeNull();
  });
});
