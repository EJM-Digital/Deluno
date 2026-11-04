import { describe, expect, it } from "vitest";
import type { DownloadHealthRecord, DownloadTelemetryOverview, ProcessorHandoffItem } from "./api";
import { clientHistoryKey, downloadClientEntries, downloadHealthEntries, isLiveStep, LIVE_STEPS, processingEntries } from "./activity-live-steps";

const handoff = (overrides: Partial<ProcessorHandoffItem> = {}): ProcessorHandoffItem =>
  ({ id: "h 1", releaseName: "Dune.2021", status: "completed", updatedUtc: "2026-11-04T10:00:00Z", processorName: "Weir", sourcePath: "C:\\in\\Dune.mkv", outputPath: null, failureMessage: null, ...overrides }) as ProcessorHandoffItem;
const record = (overrides: Partial<DownloadHealthRecord> = {}): DownloadHealthRecord =>
  ({ clientId: "c 1", queueItemId: "q 1", releaseName: "Dune.2021", kind: "stalled", severity: "warning", evidence: "Not moving.", firstObservedUtc: "2026-11-01T00:00:00Z", lastObservedUtc: "2026-11-04T09:00:00Z", strikeCount: 1, ignoredUntilUtc: null, ...overrides }) as DownloadHealthRecord;
const NOW = Date.parse("2026-11-04T12:00:00Z");

describe("the three steps Activity lists from what In progress reads", () => {
  it("names them, and knows which words are one of them", () => {
    expect(LIVE_STEPS.map((step) => [step.value, step.label])).toEqual([["processing", "Processing"], ["download-health", "Download health"], ["download-client", "Download client"]]);
    expect(isLiveStep("processing")).toBe(true);
    expect(isLiveStep("download-client")).toBe(true);
    expect(isLiveStep("import")).toBe(false);
    expect(isLiveStep("")).toBe(false);
  });
});

describe("files handed to the processor and back", () => {
  it("is in progress while the processor has the file, saying so", () => {
    const [entry] = processingEntries([handoff({ status: "Started", sourcePath: "/in/Dune.mkv" })]);
    expect(entry).toMatchObject({ key: "processing-h 1", step: "processing", status: "in-progress", whenUtc: "2026-11-04T10:00:00Z", title: "Dune.2021", stepLabel: "Weir", detail: "With the processor", detailSub: "/in/Dune.mkv", dismissPath: undefined });
    expect(entry.facts).toEqual([{ label: "Processor", value: "Weir" }, { label: "Sent", value: "/in/Dune.mkv", mono: true }]);
  });

  it("is done when it came back, naming the file it came back as", () => {
    const [entry] = processingEntries([handoff({ outputPath: "C:\\out\\Dune.mkv" })]);
    expect(entry).toMatchObject({ status: "done", detail: "Processed and handed back", detailSub: "C:\\out\\Dune.mkv" });
    expect(entry.facts).toEqual([
      { label: "Processor", value: "Weir" },
      { label: "Sent", value: "C:\\in\\Dune.mkv", mono: true },
      { label: "Came back as", value: "C:\\out\\Dune.mkv", mono: true }
    ]);
  });

  it("says the hand-off finished when nothing came back and nothing failed", () => {
    const [entry] = processingEntries([handoff()]);
    expect(entry).toMatchObject({ status: "done", detail: "Hand-off finished", detailSub: "C:\\in\\Dune.mkv" });
  });

  it("is failed, with the reason and a way to dismiss it, when it needs a person", () => {
    const [entry] = processingEntries([handoff({ status: "failed", failureMessage: "Could not clean it." })]);
    expect(entry).toMatchObject({ status: "failed", detail: "Could not clean it.", dismissPath: "/api/integrations/processors/handoffs/h%201/dismiss" });
  });

  it("is failed for a failure status without a message, and for a message on any status", () => {
    expect(processingEntries([handoff({ status: "timed-out" })])[0]).toMatchObject({ status: "failed", detail: "Hand-off finished" });
    expect(processingEntries([handoff({ status: "completed", failureMessage: "Odd." })])[0]).toMatchObject({ status: "failed", detail: "Odd." });
  });

  it("is not failed once dismissed", () => {
    const [entry] = processingEntries([handoff({ status: "dismissed", failureMessage: "Could not clean it." })]);
    expect(entry.status).toBe("done");
    expect(entry.dismissPath).toBeUndefined();
  });

  it("calls the processor a processor when it has no name", () => {
    const [entry] = processingEntries([handoff({ processorName: null })]);
    expect(entry.stepLabel).toBe("Processor");
    expect(entry.facts[0]).toEqual({ label: "Processor", value: "Processor" });
  });

  it("keeps the order it was given and lists nothing for nothing", () => {
    expect(processingEntries([handoff({ id: "a" }), handoff({ id: "b" })]).map((e) => e.key)).toEqual(["processing-a", "processing-b"]);
    expect(processingEntries([])).toEqual([]);
  });
});

describe("download-health findings", () => {
  it("needs a decision for an ordinary finding, with how often it was seen and how to dismiss it", () => {
    const [entry] = downloadHealthEntries([record({ strikeCount: 3 })], NOW);
    expect(entry).toMatchObject({
      key: "download-health-c 1-q 1-stalled", step: "download-health", status: "needs-decision", whenUtc: "2026-11-04T09:00:00Z", title: "Dune.2021",
      stepLabel: "Download health", detail: "Not moving.", detailSub: "Seen 3 times", dismissBody: { durationDays: 7 },
      dismissPath: "/api/download-clients/c%201/queue/q%201/health/stalled/ignore"
    });
    expect(entry.facts).toEqual([{ label: "Times seen", value: "3" }]);
  });

  it("says nothing extra for a finding seen once", () => {
    expect(downloadHealthEntries([record()], NOW)[0].detailSub).toBeUndefined();
  });

  it("is failed when it is critical", () => {
    expect(downloadHealthEntries([record({ severity: "critical" })], NOW)[0].status).toBe("failed");
  });

  it("is done and dismissed while the person's dismissal lasts, with nothing to dismiss", () => {
    const [entry] = downloadHealthEntries([record({ severity: "critical", strikeCount: 4, ignoredUntilUtc: "2026-11-05T00:00:00Z" })], NOW);
    expect(entry).toMatchObject({ status: "done", detailSub: "Dismissed", dismissPath: undefined });
  });

  it("counts again once the dismissal has passed, or at the very moment it ends", () => {
    expect(downloadHealthEntries([record({ ignoredUntilUtc: "2026-11-04T11:59:59Z" })], NOW)[0].status).toBe("needs-decision");
    expect(downloadHealthEntries([record({ ignoredUntilUtc: "2026-11-04T12:00:00Z" })], NOW)[0].status).toBe("needs-decision");
    expect(downloadHealthEntries([record({ ignoredUntilUtc: "2026-11-04T12:00:01Z" })], NOW)[0].status).toBe("done");
  });

  it("reads the clock itself when it is not given one", () => {
    expect(downloadHealthEntries([record({ ignoredUntilUtc: "2999-01-01T00:00:00Z" })])[0].status).toBe("done");
    expect(downloadHealthEntries([record({ ignoredUntilUtc: "2000-01-01T00:00:00Z" })])[0].status).toBe("needs-decision");
  });
});

describe("what the download clients reported", () => {
  const item = (overrides: Record<string, unknown> = {}) => ({ id: "i 1", title: "Dune", releaseName: "Dune.2021", outcome: "completed", completedUtc: "2026-11-04T08:00:00Z", errorMessage: null, failure: null, indexerName: "Nebula", ...overrides });
  const telemetry = (...items: Array<Record<string, unknown>>) => ({ clients: [{ clientId: "c 1", clientName: "qBittorrent", history: items }] }) as unknown as DownloadTelemetryOverview;

  it("remembers a dismissed row by the client and the item", () => {
    expect(clientHistoryKey("c1", "i1")).toBe("client-history:c1:i1");
  });

  it("is done for something that finished, saying what the client reported and where it came from", () => {
    const [entry] = downloadClientEntries(telemetry(item()));
    expect(entry).toMatchObject({ key: "download-client-c 1-i 1", step: "download-client", status: "done", whenUtc: "2026-11-04T08:00:00Z", title: "Dune", stepLabel: "qBittorrent", detail: "The client reported: Finished downloading", detailSub: "Nebula", dismissPath: undefined });
    expect(entry.facts).toEqual([{ label: "Client", value: "qBittorrent" }, { label: "From", value: "Nebula" }, { label: "Reported", value: "Finished downloading" }]);
  });

  it("names the release when it has no title, and leaves out an indexer it does not know", () => {
    const [entry] = downloadClientEntries(telemetry(item({ title: "", indexerName: "" })));
    expect(entry.title).toBe("Dune.2021");
    expect(entry.detailSub).toBeUndefined();
    expect(entry.facts.map((f) => f.label)).toEqual(["Client", "Reported"]);
    expect(downloadClientEntries(telemetry(item({ indexerName: null })))[0].detailSub).toBeUndefined();
  });

  it("says an outcome it has no words for as it is", () => {
    expect(downloadClientEntries(telemetry(item({ outcome: "weird" })))[0].detail).toBe("The client reported: weird");
  });

  it("is failed, with the client's own error, and can be dismissed", () => {
    const [entry] = downloadClientEntries(telemetry(item({ errorMessage: "Disk full", outcome: "failed" })));
    expect(entry).toMatchObject({ status: "failed", detail: "Disk full", dismissPath: "/api/download-clients/c%201/history/i%201/dismiss" });
  });

  it("uses the failure's summary, then its message, when there is no error text", () => {
    expect(downloadClientEntries(telemetry(item({ failure: { summary: "Summary.", message: "Message." } })))[0].detail).toBe("Summary.");
    expect(downloadClientEntries(telemetry(item({ failure: { message: "Message." } })))[0].detail).toBe("Message.");
    expect(downloadClientEntries(telemetry(item({ failure: {} })))[0].detail).toBe("The client reported: Finished downloading");
  });

  it("is done and dismissed once the person dismissed it, and a dismissal does not hide a row that was never a problem", () => {
    const dismissed = new Set([clientHistoryKey("c 1", "i 1")]);
    expect(downloadClientEntries(telemetry(item({ errorMessage: "Disk full" })), dismissed)[0]).toMatchObject({ status: "done", detailSub: "Dismissed", dismissPath: undefined });
    expect(downloadClientEntries(telemetry(item()), dismissed)[0]).toMatchObject({ status: "done", detailSub: "Nebula" });
    expect(downloadClientEntries(telemetry(item({ errorMessage: "Disk full" })), new Set([clientHistoryKey("c 1", "other")]))[0].status).toBe("failed");
  });

  it("lists every client's history, one entry for each item", () => {
    const two = { clients: [{ clientId: "a", clientName: "A", history: [item({ id: "1" }), item({ id: "2" })] }, { clientId: "b", clientName: "B", history: [item({ id: "3" })] }, { clientId: "c", clientName: "C", history: [] }] } as unknown as DownloadTelemetryOverview;
    expect(downloadClientEntries(two).map((e) => e.key)).toEqual(["download-client-a-1", "download-client-a-2", "download-client-b-3"]);
  });
});
