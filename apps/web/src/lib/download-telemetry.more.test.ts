import { describe, expect, it } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadTelemetryOverview } from "./api";
import {
  authModeLabel, CLIENT_HISTORY_OUTCOMES, clientHistoryOutcomeLabel, downloadQueueStatuses, isFinishedInClient, isImportReadyStatus, isProcessingStatus,
  patchTelemetryProgress, queueStatusLabel, telemetryCapabilityChips
} from "./download-telemetry";

describe("which queue statuses mean what", () => {
  it("is import ready for import ready and completed only", () => {
    expect(isImportReadyStatus("importReady")).toBe(true);
    expect(isImportReadyStatus("completed")).toBe(true);
    expect(isImportReadyStatus("imported")).toBe(false);
    expect(isImportReadyStatus("")).toBe(false);
  });

  it.each(["imported", "importedFileLeft", "filesGone", "handedOverElsewhere"])("%s is finished in the client", (status) => {
    expect(isFinishedInClient(status)).toBe(true);
  });

  it.each(["downloading", "importReady", "completed", "importFailed", "", "Imported"])("%s is not finished in the client", (status) => {
    expect(isFinishedInClient(status)).toBe(false);
  });

  it.each(["processing", "processed", "processingFailed", "waitingForProcessor", "handingOver", "notHandedOver", "importQueued"])("%s is with the processor", (status) => {
    expect(isProcessingStatus(status)).toBe(true);
  });

  it.each(["downloading", "imported", "importReady", "completed", "paused", ""])("%s is not with the processor", (status) => {
    expect(isProcessingStatus(status)).toBe(false);
  });

  it("uses the words the server sends", () => {
    expect(downloadQueueStatuses.importFailed).toBe("importFailed");
    expect(downloadQueueStatuses.handedOverElsewhere).toBe("handedOverElsewhere");
  });
});

describe("the words for a queue status", () => {
  it.each([
    ["downloading", "Downloading"], ["queued", "Queued"], ["completed", "Import ready"], ["importReady", "Import ready"], ["waitingForProcessor", "Waiting for processor"],
    ["handingOver", "Handing over"], ["handedOverElsewhere", "Another copy handed over"], ["notHandedOver", "Not handed over"], ["processing", "Processing"],
    ["processed", "Processed"], ["processingFailed", "Processing failed"], ["importQueued", "Import queued"], ["imported", "Imported"],
    ["importedFileLeft", "Imported earlier, but the file has since left the library"], ["filesGone", "Files are gone"], ["importFailed", "Import failed"],
    ["stalled", "Stalled"], ["notTransferring", "Not transferring"], ["paused", "Paused"], ["somethingNew", "somethingNew"]
  ])("%s reads %s", (status, words) => {
    expect(queueStatusLabel(status)).toBe(words);
  });
});

describe("the words for what a client reported", () => {
  it("has words for every outcome it can carry, and gives an unknown code as it is", () => {
    for (const outcome of CLIENT_HISTORY_OUTCOMES) expect(clientHistoryOutcomeLabel(outcome)).not.toBe(outcome);
    expect(clientHistoryOutcomeLabel("duplicate")).toBe("Already had it");
    expect(clientHistoryOutcomeLabel("needsAttention")).toBe("Needs your attention");
    expect(clientHistoryOutcomeLabel("scriptFailed")).toBe("The client's own script failed");
    expect(clientHistoryOutcomeLabel("scriptWarning")).toBe("The client's own script warned");
    expect(clientHistoryOutcomeLabel("failed")).toBe("Failed");
    expect(clientHistoryOutcomeLabel("completed")).toBe("Finished downloading");
    expect(clientHistoryOutcomeLabel("imported")).toBe("Imported");
    expect(clientHistoryOutcomeLabel("importReady")).toBe("Ready to import");
    expect(clientHistoryOutcomeLabel("sent")).toBe("Sent to the client");
    expect(clientHistoryOutcomeLabel("unknown")).toBe("Unknown");
    expect(clientHistoryOutcomeLabel("brandNew")).toBe("brandNew");
  });
});

describe("how a client signs in", () => {
  it.each([["api-key", "API key"], ["basic", "Basic auth"], ["basic-token", "Token auth"], ["form", "Web login"], ["password", "Password"], ["unknown", "Auth unknown"], ["", "Auth unknown"]])("%s reads %s", (mode, words) => {
    expect(authModeLabel(mode)).toBe(words);
  });
});

describe("what a client can do", () => {
  const caps = (overrides: Record<string, unknown> = {}) => ({ capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "api-key", ...overrides } }) as unknown as DownloadClientTelemetrySnapshot;

  it("says each thing it can do, in the order a person reads them", () => {
    expect(telemetryCapabilityChips(caps())).toEqual([
      { label: "Queue telemetry", enabled: true }, { label: "History", enabled: true }, { label: "Pause/resume", enabled: true }, { label: "Remove", enabled: true },
      { label: "Recheck", enabled: true }, { label: "Import path", enabled: true }, { label: "API key", enabled: true }
    ]);
  });

  it("says each thing it cannot do", () => {
    expect(telemetryCapabilityChips(caps({ supportsQueue: false, supportsHistory: false, supportsPauseResume: false, supportsRemove: false, supportsRecheck: false, supportsImportPath: false, authMode: "unknown" }))).toEqual([
      { label: "No queue telemetry", enabled: false }, { label: "History limited", enabled: false }, { label: "No pause/resume", enabled: false }, { label: "No remove", enabled: false },
      { label: "No recheck", enabled: false }, { label: "Path limited", enabled: false }, { label: "Auth unknown", enabled: false }
    ]);
  });
});

describe("a pushed progress reading", () => {
  const item = (id: string, speedMbps: number, progress = 10) => ({ id, speedMbps, progress, title: id });
  const overview = (): DownloadTelemetryOverview => ({
    summary: { totalSpeedMbps: 10, activeCount: 2 },
    clients: [
      { clientId: "a", queue: [item("x", 4), item("y", 2)], summary: { totalSpeedMbps: 6 } },
      { clientId: "b", queue: [item("z", 4)], summary: { totalSpeedMbps: 4 } }
    ]
  }) as unknown as DownloadTelemetryOverview;

  it("leaves nothing alone as nothing", () => {
    expect(patchTelemetryProgress(null, { id: "x", progress: 1, speedMbps: 1 })).toBeNull();
    expect(patchTelemetryProgress(undefined, { id: "x", progress: 1, speedMbps: 1 })).toBeUndefined();
  });

  it("returns the same object when the download is not there", () => {
    const before = overview();
    expect(patchTelemetryProgress(before, { id: "nope", progress: 1, speedMbps: 1 })).toBe(before);
  });

  it("changes that item's progress and speed, and moves its client's and the overall speed by the difference", () => {
    const before = overview();
    const after = patchTelemetryProgress(before, { id: "x", progress: 55, speedMbps: 7 })!;
    expect(after.clients[0].queue[0]).toMatchObject({ id: "x", progress: 55, speedMbps: 7, title: "x" });
    expect(after.clients[0].queue[1]).toEqual(item("y", 2));
    expect(after.clients[0].summary.totalSpeedMbps).toBe(9);
    expect(after.clients[1]).toBe(before.clients[1]);
    expect(after.summary.totalSpeedMbps).toBe(13);
    expect(before.clients[0].queue[0].progress).toBe(10);
    expect(after.summary.activeCount).toBe(2);
  });

  it("moves the speeds down when the download slows, and never below nothing", () => {
    const slowed = patchTelemetryProgress(overview(), { id: "x", progress: 20, speedMbps: 1 })!;
    expect(slowed.clients[0].summary.totalSpeedMbps).toBe(3);
    expect(slowed.summary.totalSpeedMbps).toBe(7);
    const stopped = patchTelemetryProgress({ ...overview(), summary: { totalSpeedMbps: 1 } } as unknown as DownloadTelemetryOverview, { id: "z", progress: 20, speedMbps: 0 })!;
    expect(stopped.summary.totalSpeedMbps).toBe(0);
    expect(stopped.clients[1].summary.totalSpeedMbps).toBe(0);
  });
});
