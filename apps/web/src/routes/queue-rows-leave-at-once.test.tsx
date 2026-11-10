import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadHealthFinding, DownloadQueueItem, JobQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/**
 * #1538: a Needs attention row took ages to leave after its action worked. The screen is handed the list as it was BEFORE the
 * action for the whole test (the loader data below never changes, and revalidate does nothing): that is a stale read. Each
 * row must leave on the success reply anyway, stay with its reason when the server refuses, and not come back.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() }),
    realtimeToast: { success: vi.fn(), error: vi.fn(), message: vi.fn(), info: vi.fn(), warning: vi.fn() }
  };
});

const revalidateMock = vi.fn();
const emptyRecovery = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as unknown[] };
const queueLoaderData = {
  telemetry: {
    summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
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
  processorConnections: [],
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
    useRouteSources: () => readyRouteSources({ ...queueLoaderData }, revalidateMock)
  };
});

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

const held = (overrides: Partial<HeldReleaseRow> = {}): HeldReleaseRow => ({
  historyId: "held-1", mediaType: "movie", id: "movie-7", episodeId: null, title: "Dune", year: 2021, episodeLabel: null, libraryId: "library-1",
  releaseName: "Dune.2021.2160p.WEB-DL-GROUP", indexerName: "Fixture", indexerId: "indexer-1", downloadUrl: null, quality: "2160p", sizeBytes: 9_000_000_000,
  seeders: 12, reason: "Larger than the cap.", heldUtc: "2026-10-14T01:00:00Z", ...overrides
});

const failedJob = (): JobQueueItem => ({
  id: "job-1", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "dead-letter",
  payloadJson: JSON.stringify({ title: "Arrival (2016)" }), attempts: 3, createdUtc: "2026-10-14T01:00:00Z", scheduledUtc: "2026-10-14T01:00:00Z",
  startedUtc: null, completedUtc: null, leasedUntilUtc: null, workerId: null, lastError: "The destination folder is full.", relatedEntityType: null,
  relatedEntityId: null, idempotencyKey: null, dedupeKey: null
} as JobQueueItem);

const recoveryCase = {
  id: "case-1", title: "Nosferatu", failureKind: "corrupt", summary: "The file is damaged.", recommendedAction: "Look for another copy.",
  detailsJson: null, detectedUtc: "2026-10-14T01:00:00Z"
};

const failedHandoff = {
  id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "qbit-client", queueItemId: "gone-1", releaseName: "Retwice.2021.1080p",
  sourcePath: "/downloads/retwice", processorName: "Weir", status: "failed", outputPath: null, importJobId: null, failureMessage: "Weir could not read it.",
  createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString()
};

const finding = (overrides: Partial<DownloadHealthFinding> = {}): DownloadHealthFinding => ({
  severity: "critical", kind: "no-progress", summary: "No bytes for 40 minutes.", evidence: "Nothing arrived.", recommendedAction: "Search for another.",
  canSafelyRetry: true, canSafelyRemove: true, strikeCount: 3, candidateBlocked: true, ignoredUntilUtc: null, ruleId: null, ruleActionPreset: null,
  ruleStrikeLimit: null, nextStrikeUtc: null, ...overrides
});

function stuckItem(): DownloadQueueItem {
  return {
    id: "qbit-9", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies", title: "Stuck Film",
    releaseName: "Stuck.Film.2020.1080p-GROUP", category: "movies", status: "stalled", progress: 60, speedMbps: 0, etaSeconds: 0, sizeBytes: 1000,
    downloadedBytes: 600, peers: 0, indexerName: "Fixture", errorMessage: null, addedUtc: new Date().toISOString(), sourcePath: null, libraryId: null,
    healthFindings: [finding()]
  } as unknown as DownloadQueueItem;
}

function withStuckItem() {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 1, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: [stuckItem()], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
    }]
  };
}

function open() {
  render(
    <MemoryRouter>
      <QueuePage />
    </MemoryRouter>
  );
}

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply({ succeeded: true, message: "Done." }));
});

afterEach(() => {
  queueLoaderData.movieRecovery = { ...emptyRecovery };
  queueLoaderData.failedJobs = [];
  queueLoaderData.heldReleases = [];
  queueLoaderData.processorHandoffs = [];
  queueLoaderData.dispatches = [];
  queueLoaderData.orphans = [];
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
});

describe("a held release leaves Needs attention on the success reply", () => {
  it.each([
    ["Approve", "Approve Dune.2021.2160p.WEB-DL-GROUP"],
    ["Dismiss", "Dismiss Dune.2021.2160p.WEB-DL-GROUP"],
    ["Find another", "Find another for Dune (2021)"]
  ])("%s", async (_label, name) => {
    queueLoaderData.heldReleases = [held()];
    open();
    expect(screen.getByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP")).toBeVisible();

    fireEvent.click(screen.getByRole("button", { name }));

    await waitFor(() => expect(screen.queryByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP")).not.toBeInTheDocument());
  });

  it("stays, with the server's reason beside it, when Approve is refused", async () => {
    queueLoaderData.heldReleases = [held()];
    authedFetchMock.mockImplementation(() => reply({ message: "Dune is already in the library." }, false, 409));
    open();

    fireEvent.click(screen.getByRole("button", { name: "Approve Dune.2021.2160p.WEB-DL-GROUP" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Dune is already in the library.");
    expect(screen.getByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP")).toBeVisible();
  });

  const heldText = "Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP";

  it("does not come back when a read that began before the action hands the old list again", async () => {
    queueLoaderData.heldReleases = [held()];
    const { rerender } = render(<MemoryRouter><QueuePage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss Dune.2021.2160p.WEB-DL-GROUP" }));
    await waitFor(() => expect(screen.queryByText(heldText)).not.toBeInTheDocument());

    rerender(<MemoryRouter><QueuePage /></MemoryRouter>);
    rerender(<MemoryRouter><QueuePage /></MemoryRouter>);

    expect(screen.queryByText(heldText)).not.toBeInTheDocument();
  });

  it("is shown again when the server stopped listing it and then lists it as a new item", async () => {
    queueLoaderData.heldReleases = [held()];
    const { rerender } = render(<MemoryRouter><QueuePage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss Dune.2021.2160p.WEB-DL-GROUP" }));
    await waitFor(() => expect(screen.queryByText(heldText)).not.toBeInTheDocument());

    queueLoaderData.heldReleases = [];
    rerender(<MemoryRouter><QueuePage /></MemoryRouter>);
    queueLoaderData.heldReleases = [held()];
    rerender(<MemoryRouter><QueuePage /></MemoryRouter>);

    expect(await screen.findByText(heldText)).toBeVisible();
  });
});

describe("the other rows of Needs attention leave on the success reply and stay when refused", () => {
  it("a failed import job: Dismiss", async () => {
    queueLoaderData.failedJobs = [failedJob()];
    open();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(screen.queryByText("Import job")).not.toBeInTheDocument());
  });

  it("a failed import job refused: the row stays and the reason is shown", async () => {
    queueLoaderData.failedJobs = [failedJob()];
    authedFetchMock.mockImplementation(() => reply({ message: "That job is already closed." }, false, 409));
    open();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(screen.getByText("Import job")).toBeVisible();
  });

  it("a recovery case: Dismiss", async () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase] };
    open();
    expect(screen.getByText("The file is damaged.")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(screen.queryByText("The file is damaged.")).not.toBeInTheDocument());
  });

  it("a failed hand-off: Dismiss in its drawer", async () => {
    queueLoaderData.processorHandoffs = [failedHandoff];
    open();
    fireEvent.click(screen.getByText("Retwice.2021.1080p"));
    fireEvent.click(await screen.findByRole("button", { name: "Dismiss this hand-off" }));
    await waitFor(() => expect(screen.queryByText("Retwice.2021.1080p")).not.toBeInTheDocument());
  });

  it("select all then Dismiss clears every row that was dismissed, and keeps the one that was refused", async () => {
    queueLoaderData.heldReleases = [held()];
    queueLoaderData.failedJobs = [failedJob()];
    authedFetchMock.mockImplementation((url: string) => String(url).includes("/api/jobs/") ? reply({ message: "That job is already closed." }, false, 409) : reply());
    open();

    fireEvent.click(screen.getByRole("button", { name: "Select all 2 to dismiss" }));
    fireEvent.click(screen.getByRole("button", { name: "Dismiss 2 selected" }));

    await waitFor(() => expect(screen.queryByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP")).not.toBeInTheDocument());
    expect(screen.getByText("Import job")).toBeVisible();
  });

  it("select all then Dismiss clears them all when all are accepted", async () => {
    queueLoaderData.heldReleases = [held()];
    queueLoaderData.failedJobs = [failedJob()];
    open();

    fireEvent.click(screen.getByRole("button", { name: "Select all 2 to dismiss" }));
    fireEvent.click(screen.getByRole("button", { name: "Dismiss 2 selected" }));

    await waitFor(() => expect(screen.queryByText("Import job")).not.toBeInTheDocument());
    expect(screen.queryByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP")).not.toBeInTheDocument();
  });
});

describe("a download still in the client", () => {
  it("leaves Needs attention and the list the moment its removal is accepted, even though the next read still lists it", async () => {
    withStuckItem();
    open();
    expect(screen.getByText("Release refused: it stopped downloading")).toBeVisible();
    fireEvent.click(screen.getByText("Release refused: it stopped downloading"));
    fireEvent.click(await screen.findByRole("button", { name: "Remove from client" }));

    fireEvent.click(await screen.findByRole("button", { name: /I don't want it/ }));

    await waitFor(() => expect(screen.queryByText("Release refused: it stopped downloading")).not.toBeInTheDocument());
    expect(screen.queryByText("Stuck.Film.2020.1080p-GROUP")).not.toBeInTheDocument();
  });

  it("stays when the client refuses the removal", async () => {
    withStuckItem();
    authedFetchMock.mockImplementation(() => reply({ succeeded: false, message: "qBittorrent is not answering." }, false, 400));
    open();
    fireEvent.click(screen.getByText("Release refused: it stopped downloading"));
    fireEvent.click(await screen.findByRole("button", { name: "Remove from client" }));

    fireEvent.click(await screen.findByRole("button", { name: /I don't want it/ }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("qBittorrent is not answering."));
    expect(screen.getAllByText("Stuck.Film.2020.1080p-GROUP").length).toBeGreaterThan(0);
  });

  it("a download that is removed and comes back as a new item is shown again", async () => {
    withStuckItem();
    const { rerender } = render(<MemoryRouter><QueuePage /></MemoryRouter>);
    fireEvent.click(screen.getByText("Release refused: it stopped downloading"));
    fireEvent.click(await screen.findByRole("button", { name: "Remove from client" }));
    fireEvent.click(await screen.findByRole("button", { name: /I don't want it/ }));
    await waitFor(() => expect(screen.queryByText("Release refused: it stopped downloading")).not.toBeInTheDocument());

    // The next list no longer has it; later the same release is grabbed again and is listed as new.
    const client = queueLoaderData.telemetry.clients[0];
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [{ ...client, queue: [] }] };
    rerender(<MemoryRouter><QueuePage /></MemoryRouter>);
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [{ ...client, queue: [{ ...stuckItem(), addedUtc: new Date(Date.now() + 5000).toISOString() }] }] };
    rerender(<MemoryRouter><QueuePage /></MemoryRouter>);

    expect(await screen.findByText("Release refused: it stopped downloading")).toBeVisible();
  });

  it("Ignore this download takes its findings off the row at once", async () => {
    withStuckItem();
    open();
    fireEvent.click(screen.getByText("Release refused: it stopped downloading"));

    fireEvent.click(await screen.findByRole("button", { name: "Ignore this download" }));

    await waitFor(() => expect(screen.queryByText("Release refused: it stopped downloading")).not.toBeInTheDocument());
  });

  it("Dismiss on a finding takes the finding off the row at once", async () => {
    withStuckItem();
    open();
    fireEvent.click(screen.getByText("Release refused: it stopped downloading"));

    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(screen.queryByText("Release refused: it stopped downloading")).not.toBeInTheDocument());
  });
});
