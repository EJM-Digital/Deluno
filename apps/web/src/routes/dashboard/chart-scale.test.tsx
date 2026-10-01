import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { clockAt, nearestIndex, niceMax, rateText, scaleLabel, spanLabel, traceHitAt } from "./chart-scale";
import { shapeSpeedSeries } from "./live/series";
import { speedHitAt } from "./live/speed-chart";
import { traceTop, TraceChart } from "./system/band";

describe("niceMax", () => {
  it("rounds up to 1, 2, 2.5, 5 or 10 times a power of ten", () => {
    expect(niceMax(0)).toBe(1);
    expect(niceMax(0.37)).toBe(0.5);
    expect(niceMax(3.2)).toBe(5);
    expect(niceMax(7)).toBe(10);
    expect(niceMax(12)).toBe(20);
    expect(niceMax(260)).toBe(500);
    expect(niceMax(2.2)).toBe(2.5);
    expect(niceMax(10)).toBe(10);
    expect(niceMax(Number.NaN)).toBe(1);
  });
});

describe("labels", () => {
  it("says the scale in plain units", () => {
    expect(scaleLabel(10, "MB/s")).toBe("10 MB/s");
    expect(scaleLabel(2.5 / 2, "MB/s")).toBe("1.25 MB/s");
    expect(scaleLabel(50, "%")).toBe("50%");
    expect(rateText(6.24)).toBe("6.2 MB/s");
    expect(rateText(0)).toBe("0 MB/s");
    expect(spanLabel(61)).toBe("10 min ago");
    expect(spanLabel(4)).toBe("30 s ago");
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

  it("puts a network trace on a round MB/s top and a percentage on 100", () => {
    expect(traceTop([3_200_000, 100], { kind: "rate" })).toBe(5);
    expect(traceTop([], { kind: "rate" })).toBe(1);
    expect(traceTop([12], { kind: "percent" })).toBe(100);
  });

  it("shows the scale and span on a trace, and a reading on hover that hides on leave", () => {
    const { container } = render(<TraceChart values={[10, 20, 30, 40]} scale={{ kind: "percent" }} colour="red" />);
    expect(container.textContent).toContain("100%");
    expect(container.textContent).toContain("50%");
    expect(container.textContent).toContain("30 s ago");
    expect(container.textContent).toContain("now");
    const layer = container.querySelector<HTMLElement>(".cs-layer")!;
    layer.getBoundingClientRect = () => ({ left: 0, right: 200, top: 0, bottom: 50, width: 200, height: 50, x: 0, y: 0, toJSON: () => ({}) });
    fireEvent.pointerMove(layer, { clientX: 200, pointerType: "mouse" });
    expect(container.querySelector(".cs-read")?.textContent).toMatch(/· 40%$/);
    fireEvent.pointerLeave(layer, { pointerType: "mouse" });
    expect(container.querySelector(".cs-read")).toBeNull();
  });
});
