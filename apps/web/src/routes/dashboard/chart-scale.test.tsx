import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ChartLayer, clockAt, nearestIndex, quarterMax, quarterTicks, rateText, scaleLabel, traceHitAt } from "./chart-scale";
import { shapeSpeedSeries } from "./live/series";
import { speedHitAt } from "./live/speed-chart";
import { traceTop, TraceChart } from "./system/band";

describe("a scale in quarters (the owner: 25 / 50 / 75 / 100)", () => {
  it("rounds up to 1, 2, 4 or 10 times a power of ten, so every quarter is a clean number", () => {
    expect(quarterMax(0)).toBe(1);
    expect(quarterMax(0.37)).toBe(0.4);
    expect(quarterMax(3.2)).toBe(4);
    expect(quarterMax(7)).toBe(10);
    expect(quarterMax(12)).toBe(20);
    expect(quarterMax(260)).toBe(400);
    expect(quarterMax(10)).toBe(10);
    expect(quarterMax(Number.NaN)).toBe(1);
  });

  it("draws four lines from the top down, labelled at each quarter, the 75% and 25% ones minor", () => {
    const ticks = quarterTicks(10, "MB/s", 0, 100);
    expect(ticks.map((tick) => [tick.top, tick.label, tick.minor])).toEqual([
      [0, "10 MB/s", false],
      [25, "7.5 MB/s", true],
      [50, "5 MB/s", false],
      [75, "2.5 MB/s", true]
    ]);
    expect(quarterTicks(100, "%", 8, 100).map((tick) => tick.label)).toEqual(["100%", "75%", "50%", "25%"]);
  });

  it("leaves the minor labels off a chart too short for four, keeping their lines", () => {
    const { container } = render(<ChartLayer ticks={quarterTicks(100, "%", 8, 100)} startLabel="10 min ago" endLabel="now" resolve={() => null} />);
    // jsdom has no layout: an unmeasured chart shows only the main labels, and every line.
    expect(container.querySelectorAll(".cs-grid")).toHaveLength(4);
    expect(Array.from(container.querySelectorAll(".cs-tick")).map((el) => el.textContent)).toEqual(["100%", "50%"]);
    // Where a span is given (the Speed chart), it is one label at the right.
    expect(container.querySelector(".cs-now")?.textContent).toBe("10 min ago – now");
    expect(container.querySelector(".cs-start")).toBeNull();
  });
});

describe("labels", () => {
  it("says the scale in plain units", () => {
    expect(scaleLabel(10, "MB/s")).toBe("10 MB/s");
    expect(scaleLabel(2.5 / 2, "MB/s")).toBe("1.25 MB/s");
    expect(scaleLabel(50, "%")).toBe("50%");
    expect(rateText(6.24)).toBe("6.2 MB/s");
    expect(rateText(0)).toBe("0 MB/s");
  });
});

describe("the reading under the pointer", () => {
  const now = Date.parse("2026-10-02T14:14:30");
  const series = shapeSpeedSeries(
    [
      { capturedUtc: new Date(now - 3 * 3_600_000).toISOString(), speedMbps: 6.2, uploadMbps: 1.1, activeCount: 1 },
      { capturedUtc: new Date(now - 1 * 3_600_000).toISOString(), speedMbps: 2, uploadMbps: 0, activeCount: 1 }
    ],
    null,
    now,
    6
  );

  it("finds the nearest sample for a pointer position", () => {
    expect(nearestIndex([0, 0.5, 1], 0.4)).toBe(1);
    expect(nearestIndex([0, 0.5, 1], 0.9)).toBe(2);
    const hit = speedHitAt(series, 0.5);
    expect(hit?.text).toBe("6.2 MB/s down · 1.1 MB/s up");
    expect(hit?.time).toBe(clockAt(now - 3 * 3_600_000));
    expect(speedHitAt(series, 0.9)?.text).toBe("2.0 MB/s down · 0 MB/s up");
  });

  it("reads a trace of ten-second readings, newest last", () => {
    const values = [10, 20, 37, 50];
    const hit = traceHitAt(values, 0.66, now, (value) => `${value}%`);
    expect(hit?.text).toBe("37%");
    expect(hit?.time).toBe(clockAt(now - 10_000, true));
    expect(traceHitAt([], 0.5, now, String)).toBeNull();
    expect(traceHitAt([3_000_000], 1, now, (value) => rateText(value / 1e6))?.text).toBe("3.0 MB/s");
  });

  it("puts a network trace on a top whose quarters are round numbers, rising with the traffic, and a percentage on 100", () => {
    expect(traceTop([3_200_000, 100], { kind: "rate" })).toBe(4);
    expect(traceTop([3_200_000, 9_000_000], { kind: "rate" })).toBe(10);
    expect(traceTop([31_000_000], { kind: "rate" })).toBe(40);
    expect(traceTop([], { kind: "rate" })).toBe(1);
    expect(traceTop([12], { kind: "percent" })).toBe(100);
  });

  it("shows the scale on a trace, and a reading on hover that hides on leave", () => {
    const { container } = render(<TraceChart values={[10, 20, 30, 40]} scale={{ kind: "percent" }} colour="red" />);
    expect(container.textContent).toContain("100%");
    expect(container.textContent).toContain("50%");
    // The card says "last 10 minutes" in its heading, so the trace does not say its span again.
    expect(container.querySelector(".cs-now")).toBeNull();
    const layer = container.querySelector<HTMLElement>(".cs-layer")!;
    layer.getBoundingClientRect = () => ({ left: 0, right: 200, top: 0, bottom: 50, width: 200, height: 50, x: 0, y: 0, toJSON: () => ({}) });
    fireEvent.pointerMove(layer, { clientX: 200, pointerType: "mouse" });
    expect(container.querySelector(".cs-read")?.textContent).toMatch(/· 40%$/);
    fireEvent.pointerLeave(layer, { pointerType: "mouse" });
    expect(container.querySelector(".cs-read")).toBeNull();
  });
});
