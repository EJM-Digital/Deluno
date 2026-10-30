import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, JobQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/** Which view of In progress the test opens (#1426): the hand-offs and imports moved off the live downloads. */
let viewEntry = "/queue";

/**
 * In progress: the words a row says. One row per download with its stage, its detail and a status chip, one row per import job
 * with its stage and chip, and the connector's own chips. The words are what a person reads to know where a download is.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const revalidateMock = vi.fn();
const emptyRecovery = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as unknown[] };
const queueLoaderData = {
  telemetry: {
    summary: {
      activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0,
      processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0
    },
    clients: [] as DownloadClientTelemetrySnapshot[],
    capturedUtc: new Date().toISOString()
  },
  dispatches: [] as unknown[],
  movieRecovery: { ...emptyRecovery },
  seriesRecovery: { ...emptyRecovery },
  libraries: [],
  settings: emptyPlatformSettingsSnapshot,
  jobs: [] as unknown[],
  failedJobs: [] as JobQueueItem[],
  healthRecords: [],
  processorConnections: [] as unknown[],
  processorHandoffs: [] as unknown[],
  activityEvents: [],
  orphans: [] as OrphanedDownloadItem[],
  heldReleases: [] as HeldReleaseRow[]
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(queueLoaderData, revalidateMock)
  };
});

function item(release: string, overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id: release, clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies", title: release, releaseName: release,
    category: "movies", status: "downloading", progress: 40, speedMbps: 0, etaSeconds: 0, sizeBytes: 1_000_000_000, downloadedBytes: 400_000_000, peers: 3,
    indexerName: "Fixture", errorMessage: null, addedUtc: new Date().toISOString(), sourcePath: null, libraryId: null, healthFindings: null,
    ...overrides
  };
}

function showQueue(items: DownloadQueueItem[]) {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: items, history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
    }]
  };
  render(
    <MemoryRouter initialEntries={[viewEntry]}>
      <QueuePage />
    </MemoryRouter>
  );
}

/** The row for one release: everything it says, in one string per cell. */
function rowOf(release: string) {
  const row = screen.getAllByText(release)[0].closest("[role=row]") as HTMLElement;
  return { row, within: within(row), text: row.textContent ?? "" };
}

afterEach(() => {
  authedFetchMock.mockReset();
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
  queueLoaderData.jobs = [];
  queueLoaderData.processorConnections = [];
  queueLoaderData.processorHandoffs = [];
});

describe("In progress: the Stage column says where each download is", () => {
  it.each([
    ["handingOver", "Handing over"],
    ["handedOverElsewhere", "Another copy was handed over"],
    ["notHandedOver", "Needs attention"],
    ["processing", "Checking media"],
    ["importQueued", "Importing & naming"],
    ["imported", "In library"],
    ["importedFileLeft", "Imported earlier"],
    ["filesGone", "Files are gone"],
    ["importFailed", "Needs attention"],
    ["importReady", "Ready for import"],
    ["completed", "Ready for import"],
    ["paused", "Paused"],
    ["notTransferring", "Not transferring"],
    ["queued", "Waiting for client"],
    ["downloading", "Downloading"]
  ])("a download whose state is %s reads %s", (status, stage) => {
    const release = `Row.${status}.2026.1080p-GROUP`;
    showQueue([item(release, { status, progress: status === "downloading" || status === "notTransferring" ? 40 : 100 })]);
    // A finished download sits in its own folded group (#1448): open it to read its row.
    const folded = screen.queryByRole("button", { name: "Show finished downloads" });
    if (folded) fireEvent.click(folded);

    expect(rowOf(release).text).toContain(stage);
  });

  it("a finished download with no hand-off yet reads Handing over and never Waiting for processor", () => {
    showQueue([item("Just.Finished.2026.1080p-GROUP", { status: "waitingForProcessor", progress: 100 })]);

    const { text } = rowOf("Just.Finished.2026.1080p-GROUP");
    expect(text).toContain("Handing over");
    expect(text).not.toContain("Waiting for processor");
  });

  it("a download at 99.9 percent or more that is still downloading reads Finishing in the stage and the chip, and never Stalled", () => {
    showQueue([item("Almost.Done.2026.1080p-GROUP", { status: "downloading", progress: 99.95 })]);

    const { text } = rowOf("Almost.Done.2026.1080p-GROUP");
    expect(text).toContain("Finishing");
    expect(text).not.toContain("Stalled");
  });

  it("one just short of that is plainly Downloading", () => {
    showQueue([item("Midway.2026.1080p-GROUP", { status: "downloading", progress: 99.5 })]);

    const { text } = rowOf("Midway.2026.1080p-GROUP");
    expect(text).toContain("Downloading");
    expect(text).not.toContain("Finishing");
  });
});

describe("In progress: the Status chip", () => {
  it.each([
    ["stalled", "Stalled"],
    ["paused", "Paused"],
    ["importReady", "Ready to import"],
    ["notTransferring", "Not transferring"],
    ["processingFailed", "Processing failed"],
    ["importFailed", "Import failed"],
    ["downloading", "Downloading"],
    ["handingOver", "Handing over"]
  ])("a download whose state is %s wears the chip %s", (status, chip) => {
    const release = `Chip.${status}.2026.1080p-GROUP`;
    showQueue([item(release, { status })]);

    const chips = within(rowOf(release).row).getAllByText(chip);
    expect(chips.length).toBeGreaterThan(0);
  });

  it("a download with a health finding reads as needing a look rather than as plain Downloading", () => {
    showQueue([item("Needs.A.Look.2026.1080p-GROUP", {
      status: "downloading",
      healthFindings: [{
        severity: "warning", kind: "low-peers", summary: "Very few peers.", evidence: "1 peer.", recommendedAction: "Wait.", canSafelyRetry: true,
        canSafelyRemove: false, strikeCount: 1, candidateBlocked: false, ignoredUntilUtc: null, ruleId: null, ruleActionPreset: null, ruleStrikeLimit: null, nextStrikeUtc: null
      }]
    })]);

    const { text } = rowOf("Needs.A.Look.2026.1080p-GROUP");
    expect(text).toContain("Needs a look");
  });
});

describe("In progress: Import & naming rows", () => {
  beforeEach(() => { viewEntry = "/queue?view=handoffs"; });
  afterEach(() => { viewEntry = "/queue"; });
  const job = (overrides: Partial<JobQueueItem>) => ({
    id: "job-1", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "running",
    payloadJson: JSON.stringify({ title: "Arrival (2016)", fileName: "Arrival.2016.mkv", sourcePath: "D:/Downloads/Arrival.2016.mkv" }),
    attempts: 1, maxAttempts: 5, nextAttemptUtc: null, createdUtc: "2026-10-14T01:00:00Z", scheduledUtc: "2026-10-14T01:00:00Z", startedUtc: null,
    completedUtc: null, leasedUntilUtc: null, workerId: null, lastError: null, relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null,
    ...overrides
  }) as unknown as JobQueueItem;

  it.each([
    ["queued", {}, "Waiting to import"],
    ["running", {}, "Writing to library"],
    ["completed", {}, "Imported and named"],
    ["failed", { attempts: 2, maxAttempts: 5, nextAttemptUtc: "2099-01-01T00:00:00Z" }, "Waiting for the file to settle"],
    ["failed", { attempts: 5, maxAttempts: 5, nextAttemptUtc: null }, "Import failed"],
    ["dead-letter", { attempts: 5, maxAttempts: 5 }, "Import gave up"]
  ])("a job that is %s (%j) reads %s", (status, extra, stage) => {
    queueLoaderData.jobs = [job({ status, ...extra })];
    showQueue([]);

    expect(screen.getByText("Arrival (2016)")).toBeVisible();
    expect(screen.getAllByText(stage).length).toBeGreaterThan(0);
  });

  it("an attempt that failed with another coming reads retrying, never failed", () => {
    queueLoaderData.jobs = [job({ status: "failed", attempts: 2, maxAttempts: 5, nextAttemptUtc: "2099-01-01T00:00:00Z" })];
    showQueue([]);

    expect(screen.getByText("retrying")).toBeVisible();
    expect(screen.getByText("Attempt 2")).toBeVisible();
    expect(screen.queryByText("failed")).not.toBeInTheDocument();
  });

  it("shows the file name, where it came from and 'Named as it imports' until the destination is known", () => {
    queueLoaderData.jobs = [job({ status: "running" })];
    showQueue([]);

    expect(screen.getByText("Arrival.2016.mkv")).toBeVisible();
    expect(screen.getByText("From D:/Downloads/Arrival.2016.mkv")).toBeVisible();
    expect(screen.getByText("Named as it imports")).toBeVisible();
  });

  it("counts the active ones in the card's header", () => {
    queueLoaderData.jobs = [job({ status: "running" }), job({ id: "job-2", status: "completed" })];
    showQueue([]);

    expect(screen.getByText("1 active · latest 2")).toBeVisible();
  });
});

describe("In progress: the Processing connector card", () => {
  beforeEach(() => { viewEntry = "/queue?view=handoffs"; });
  afterEach(() => { viewEntry = "/queue"; });
  const connection = (overrides: Record<string, unknown>) => ({
    id: "conn-1", name: "Weir", provider: "weir", submissionUrl: "http://weir", authHeaderName: null, isEnabled: true,
    healthStatus: "healthy", lastHealthMessage: null, lastHealthCheckUtc: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...overrides
  });

  it.each([
    [{ isEnabled: false }, "Weir · disabled"],
    [{ healthStatus: "healthy" }, "Weir · healthy"],
    [{ healthStatus: "degraded" }, "Weir · degraded"],
    [{ healthStatus: "unreachable" }, "Weir · unreachable"],
    [{ healthStatus: "unknown" }, "Weir · unknown"]
  ])("a connection with %j reads %s", (overrides, words) => {
    queueLoaderData.processorConnections = [connection(overrides)];
    showQueue([]);

    expect(screen.getByText(words)).toBeVisible();
  });
});

describe("In progress: the summary strip cells", () => {
  const zeroSummary = {
    activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0,
    processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0
  };
  const cellOf = (label: string) => screen.getAllByText(label, { selector: "span" })[0].parentElement as HTMLElement;

  beforeEach(() => {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, summary: { ...zeroSummary } };
    queueLoaderData.processorConnections = [];
    queueLoaderData.processorHandoffs = [];
    queueLoaderData.jobs = [];
  });

  it("Downloading counts the active downloads and says the combined speed to one decimal", () => {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, summary: { ...zeroSummary, activeCount: 3, totalSpeedMbps: 12.34 } };
    showQueue([]);

    expect(cellOf("Downloading")).toHaveTextContent("3");
    expect(cellOf("Downloading")).toHaveTextContent("12.3 MB/s combined");
  });

  it("Processing says no processor is connected when none is configured", () => {
    showQueue([]);

    expect(cellOf("Processing")).toHaveTextContent("no processor connected");
  });

  it("Processing counts the hand-offs the processor is holding and says how many are with it", () => {
    queueLoaderData.processorHandoffs = [{
      id: "handoff-1", status: "submitted", releaseName: "Handed.Over.2026.1080p-GROUP", processorName: "Weir", mediaType: "movies",
      createdUtc: "2026-10-14T01:00:00Z", updatedUtc: "2026-10-14T01:00:00Z", sourcePath: null, detail: null, failureReason: null
    }];
    showQueue([]);

    expect(cellOf("Processing")).toHaveTextContent("1 with the processor");
  });

  it("Importing counts the active import jobs and says what is being written, or that nothing is", () => {
    queueLoaderData.jobs = [{ id: "job-run", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "running", payloadJson: "{}", attempts: 1, maxAttempts: 5, nextAttemptUtc: null, createdUtc: "2026-10-14T01:00:00Z", scheduledUtc: "2026-10-14T01:00:00Z", startedUtc: null, completedUtc: null, leasedUntilUtc: null, workerId: null, lastError: null, relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null } as unknown as JobQueueItem];
    showQueue([]);
    expect(cellOf("Importing")).toHaveTextContent("writing into your library");
    expect(cellOf("Importing")).toHaveTextContent("1");
  });

  it("Importing reads nothing being written when no import job is active", () => {
    showQueue([]);

    expect(cellOf("Importing")).toHaveTextContent("nothing being written");
  });

  it("Ready to import counts the downloads Deluno imports next, and reads nothing waiting when there are none", () => {
    showQueue([item("Ready.One.2026.1080p-GROUP", { status: "importReady", progress: 100 })]);
    expect(cellOf("Ready to import")).toHaveTextContent("Deluno imports these next");
    expect(cellOf("Ready to import")).toHaveTextContent("1");
  });

  it("Ready to import reads nothing waiting when no download is ready", () => {
    showQueue([]);

    expect(cellOf("Ready to import")).toHaveTextContent("nothing waiting");
  });

  it("Needs action counts a release the strike rules blocked", () => {
    showQueue([item("Blocked.Release.2026.1080p-GROUP", {
      status: "downloading",
      healthFindings: [{
        severity: "warning", kind: "stall", summary: "Stuck.", evidence: "No progress.", recommendedAction: "Block.", canSafelyRetry: false,
        canSafelyRemove: true, strikeCount: 3, candidateBlocked: true, ignoredUntilUtc: null, ruleId: null, ruleActionPreset: null, ruleStrikeLimit: null, nextStrikeUtc: null
      }]
    })]);

    expect(cellOf("Needs action")).toHaveTextContent("1 release blocked");
  });

  it("with nothing in motion, the table says Nothing in motion and why it is empty", () => {
    showQueue([]);

    expect(screen.getByText("Nothing in motion")).toBeVisible();
    expect(screen.getByText(/Media appears here from the moment Deluno sends it to a download client/)).toBeVisible();
  });
});

describe("In progress: the Media pipeline table", () => {
  it("the header counts the downloads in flight and the import jobs writing, in one line", () => {
    queueLoaderData.jobs = [{ id: "job-run", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "running", payloadJson: "{}", attempts: 1, maxAttempts: 5, nextAttemptUtc: null, createdUtc: "2026-10-14T01:00:00Z", scheduledUtc: "2026-10-14T01:00:00Z", startedUtc: null, completedUtc: null, leasedUntilUtc: null, workerId: null, lastError: null, relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null } as unknown as JobQueueItem];
    showQueue([item("Count.Me.2026.1080p-GROUP")]);

    expect(screen.getByText("1 happening now · 1 importing")).toBeVisible();
  });

  it("a finished download shows only its size, with no progress figure in the row", () => {
    const release = "Finished.Row.2026.1080p-GROUP";
    showQueue([item(release, { status: "completed", progress: 100, downloadedBytes: 1_000_000_000 })]);

    const { text } = rowOf(release);
    expect(text).not.toContain("%");
    expect(text).not.toContain("MB/s");
  });

  it("a download that failed shows the client's failure sentence under its Stage", () => {
    const release = "Failed.Row.2026.1080p-GROUP";
    showQueue([item(release, { status: "failed", errorMessage: "The client refused the file." })]);

    expect(rowOf(release).text).toContain("The client refused the file.");
  });
});

describe("In progress: the cleanup guardrails card", () => {
  afterEach(() => {
    queueLoaderData.settings = emptyPlatformSettingsSnapshot;
  });

  it("the card header says manual review, safeguards or removal, by which switches are on", () => {
    showQueue([]);
    expect(screen.getByText("manual review before removal")).toBeVisible();
  });

  it("the header says automatic safeguards when only a safeguard is on, and automatic removal when a removal is on", () => {
    queueLoaderData.settings = { ...emptyPlatformSettingsSnapshot, cleanupBlockReleaseAfterThreshold: true };
    showQueue([]);
    expect(screen.getByText("automatic safeguards enabled")).toBeVisible();
  });

  it("the header says automatic removal when a removal switch is on", () => {
    queueLoaderData.settings = { ...emptyPlatformSettingsSnapshot, cleanupPurgePayloadAfterThreshold: true };
    showQueue([]);
    expect(screen.getByText("automatic removal enabled")).toBeVisible();
  });

  it("each of the four facts reads Enabled or Off from its own switch", () => {
    queueLoaderData.settings = { ...emptyPlatformSettingsSnapshot, cleanupBlockReleaseAfterThreshold: true, cleanupRemoveClientEntryAfterThreshold: true };
    showQueue([]);

    expect(screen.getAllByText("Block release")[0].nextElementSibling).toHaveTextContent("Enabled");
    expect(screen.getAllByText("Remove client entry")[0].nextElementSibling).toHaveTextContent("Enabled");
    expect(screen.getAllByText("Queue replacement")[0].nextElementSibling).toHaveTextContent("Off");
    expect(screen.getAllByText("Purge payload")[0].nextElementSibling).toHaveTextContent("Off");
  });

  it("Review Automation & Recovery links to the search cycles page", () => {
    showQueue([]);
    expect(screen.getByRole("link", { name: "Review Automation & Recovery" })).toHaveAttribute("href", "/search-cycles");
  });
});
