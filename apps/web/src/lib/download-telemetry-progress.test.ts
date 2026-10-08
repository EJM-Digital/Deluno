import { describe, expect, it } from "vitest";
import type { DownloadTelemetryOverview } from "./api";
import { patchTelemetryProgress } from "./download-telemetry";

function overview(): DownloadTelemetryOverview {
  const summary = { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 10, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 };
  const item = (id: string, speedMbps: number) => ({ id, progress: 0.1, speedMbps }) as never;
  return {
    capturedUtc: "2026-10-08T00:00:00Z",
    summary,
    clients: [
      { clientId: "a", summary: { ...summary, totalSpeedMbps: 6 }, queue: [item("one", 6)] },
      { clientId: "b", summary: { ...summary, totalSpeedMbps: 4 }, queue: [item("two", 4)] }
    ] as never
  };
}

describe("patchTelemetryProgress (#1179)", () => {
  it("moves the download, its client and the overall speed", () => {
    const next = patchTelemetryProgress(overview(), { id: "two", progress: 0.5, speedMbps: 9 })!;
    expect(next.clients[1]!.queue[0]).toMatchObject({ progress: 0.5, speedMbps: 9 });
    expect(next.clients[1]!.summary.totalSpeedMbps).toBe(9);
    expect(next.clients[0]!.summary.totalSpeedMbps).toBe(6);
    expect(next.summary.totalSpeedMbps).toBe(15);
  });

  it("leaves everything alone for a download it does not hold", () => {
    const before = overview();
    expect(patchTelemetryProgress(before, { id: "nope", progress: 1, speedMbps: 1 })).toBe(before);
    expect(patchTelemetryProgress(null, { id: "one", progress: 1, speedMbps: 1 })).toBeNull();
  });
});
