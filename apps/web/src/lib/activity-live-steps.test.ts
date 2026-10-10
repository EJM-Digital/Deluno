import { describe, expect, it } from "vitest";
import { clientHistoryKey, downloadClientEntries, downloadHealthEntries, processingEntries } from "./activity-live-steps";

const NOW = Date.parse("2026-10-10T12:00:00Z");

const record = (overrides: Record<string, unknown> = {}) => ({
  clientId: "c1", queueItemId: "q 3", releaseName: "Stalled.Show", kind: "stalled", severity: "critical", evidence: "No progress.",
  firstObservedUtc: "2026-10-10T01:00:00Z", lastObservedUtc: "2026-10-10T07:00:00Z", strikeCount: 3, ignoredUntilUtc: null, ...overrides
}) as never;

const telemetry = {
  clients: [{
    clientId: "c1", clientName: "NZBGet",
    history: [{ id: "x1", title: "Dupe.Film", releaseName: "Dupe.Film", outcome: "failed", indexerName: "", completedUtc: "2026-10-10T05:00:00Z", errorMessage: "Duplicate." }]
  }]
} as never;

describe("Every Failed row on Activity's steps can be dismissed (#1218)", () => {
  it("a stalled download counts as failed until it is dismissed, then it is done", () => {
    const [open] = downloadHealthEntries([record()], NOW);
    expect(open?.status).toBe("failed");
    expect(open?.dismissPath).toBe("/api/download-clients/c1/queue/q%203/health/stalled/ignore");
    expect(open?.dismissBody).toEqual({ durationDays: 7 });

    const [ignored] = downloadHealthEntries([record({ ignoredUntilUtc: "2026-10-17T12:00:00Z" })], NOW);
    expect(ignored?.status).toBe("done");
    expect(ignored?.dismissPath).toBeUndefined();

    // A dismissal that has run out counts again.
    expect(downloadHealthEntries([record({ ignoredUntilUtc: "2026-10-09T12:00:00Z" })], NOW)[0]?.status).toBe("failed");
  });

  it("a failed client download counts as failed until it is dismissed, then it is done", () => {
    const [open] = downloadClientEntries(telemetry);
    expect(open?.status).toBe("failed");
    expect(open?.dismissPath).toBe("/api/download-clients/c1/history/x1/dismiss");

    const [closed] = downloadClientEntries(telemetry, new Set([clientHistoryKey("c1", "x1")]));
    expect(closed?.status).toBe("done");
    expect(closed?.dismissPath).toBeUndefined();
  });

  it("a failed hand-off carries the dismiss that the Transfers screen uses", () => {
    const [entry] = processingEntries([{
      id: "h1", releaseName: "Retryfilm", status: "failed", failureMessage: "Weir never picked it up", sourcePath: "D:/x", outputPath: null,
      processorName: "Weir", updatedUtc: "2026-10-10T05:00:00Z"
    } as never]);
    expect(entry?.dismissPath).toBe("/api/integrations/processors/handoffs/h1/dismiss");
  });
});
