import { describe, expect, it } from "vitest";
import { recentArrivalItems } from "./extras";
import { arrivedWhen, LATEST_ARRIVALS_HEADING, POSTER_WIDTH_CSS } from "./shelf";
import { speedAside } from "./speed-chart";
import type { DashboardLiveExtras } from "../../../lib/api/types/operations";

describe("the shelf's latest arrivals (#1098)", () => {
  it("names a past arrival by calendar day: yesterday, then days ago", () => {
    const now = new Date(2026, 9, 1, 15, 0).getTime();
    expect(arrivedWhen(new Date(2026, 9, 1, 14, 40).toISOString(), now)).toBe("20 min ago");
    expect(arrivedWhen(new Date(2026, 8, 30, 23, 50).toISOString(), now)).toBe("yesterday");
    expect(arrivedWhen(new Date(2026, 8, 28, 9, 0).toISOString(), now)).toBe("3 days ago");
  });

  it("says plainly that the shelf shows the latest arrivals", () => {
    expect(LATEST_ARRIVALS_HEADING).toBe("Nothing yet today · latest arrivals");
  });

  it("turns the server's recent arrivals into shelf items, newest first, and is empty without them", () => {
    const extras = { requests: [], recentArrivals: [{ entityType: "movie", entityId: "m1", release: "Moon.2009.1080p", quality: "WEB 1080p", previousQuality: null, upgraded: false, sizeBytes: 1, importedUtc: "2026-09-29T10:00:00Z" }] } as unknown as DashboardLiveExtras;
    expect(recentArrivalItems(extras).map((item) => item.at)).toEqual(["2026-09-29T10:00:00Z"]);
    expect(recentArrivalItems({ requests: [] } as unknown as DashboardLiveExtras)).toEqual([]);
    expect(recentArrivalItems(undefined)).toEqual([]);
  });

  it("sizes a poster from the box it sits in, so it can never be taller than its row", () => {
    expect(POSTER_WIDTH_CSS).toContain("100cqh");
    expect(POSTER_WIDTH_CSS).toContain("var(--poster-rows)");
    expect(POSTER_WIDTH_CSS).toContain("var(--poster-caption)");
  });
});

describe("the speed card's note", () => {
  it("agrees with what the chart draws", () => {
    expect(speedAside(8.8, 0, 6)).toBe("peak 8.8 MB/s · last 6 hours");
    expect(speedAside(0, 7.4, 6)).toBe("nothing downloading · up peaked 7.4 MB/s");
    expect(speedAside(12, 3, 6)).toBe("peak 12 MB/s down · 3.0 MB/s up");
    expect(speedAside(0, 0, 6)).toBe("nothing moved in 6 hours");
  });
});
