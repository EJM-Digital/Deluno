import { describe, expect, it } from "vitest";
import type { DownloadTelemetryOverview, DownloadTelemetrySummary } from "./api";
import { ACTIVE_PIPELINE_REFRESH_MS, isPipelineMoving } from "./pipeline-activity";

function telemetry(summary: Partial<DownloadTelemetrySummary> = {}): DownloadTelemetryOverview {
  return {
    summary: {
      activeCount: 0,
      queuedCount: 0,
      completedCount: 0,
      stalledCount: 0,
      processingCount: 0,
      importReadyCount: 0,
      totalSpeedMbps: 0,
      waitingForProcessorCount: 0,
      ...summary
    },
    clients: [],
    capturedUtc: "2026-08-25T00:00:00Z"
  } as DownloadTelemetryOverview;
}

describe("pipeline activity", () => {
  it("stays on the heartbeat when nothing is in flight", () => {
    expect(isPipelineMoving(telemetry())).toBe(false);
  });

  it("treats missing telemetry as idle rather than busy", () => {
    // The query starts undefined and falls back to an empty overview on error.
    // Guessing "busy" there would poll every few seconds against a Deluno that
    // is not answering.
    expect(isPipelineMoving(undefined)).toBe(false);
    expect(isPipelineMoving({} as DownloadTelemetryOverview)).toBe(false);
  });

  it.each([
    ["downloading", { activeCount: 1 }],
    ["queued", { queuedCount: 1 }],
    ["stalled", { stalledCount: 1 }],
    ["processing", { processingCount: 1 }],
    ["ready to import", { importReadyCount: 1 }]
  ])("polls fast while work sits at %s", (_stage, summary) => {
    expect(isPipelineMoving(telemetry(summary))).toBe(true);
  });

  it("does not count finished work as movement", () => {
    // completedCount only grows, so counting it would pin the dashboard to the
    // fast poll forever on any install that has ever downloaded anything.
    expect(isPipelineMoving(telemetry({ completedCount: 4321 }))).toBe(false);
  });

  it("keeps the fast poll slower than a progress bar's own transition", () => {
    // The bar animates width over 500ms; a poll faster than that would restart
    // the transition mid-flight and the bar would stutter instead of climb.
    expect(ACTIVE_PIPELINE_REFRESH_MS).toBeGreaterThan(500);
    expect(ACTIVE_PIPELINE_REFRESH_MS).toBeLessThan(60_000);
  });
});
