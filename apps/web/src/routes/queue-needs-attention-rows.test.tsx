import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, JobQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/**
 * Transfers, Needs attention: each kind of row, what it says and the request each of its buttons sends. The routes behind
 * them are tested on the real host (TvRecoveryCasesOverHttpOnRealFilesTests, HeldReleasesOverHttpTests, the job routes).
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
    useRouteSources: () => readyRouteSources(queueLoaderData, revalidateMock)
  };
});

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

const recoveryCase = (overrides: Record<string, unknown> = {}) => ({
  id: "case-1", title: "Nosferatu", failureKind: "downloadFailed", summary: "The download failed.", recommendedAction: "Try it again.",
  detailsJson: JSON.stringify({ DispatchId: "dispatch-1" }), detectedUtc: "2026-10-14T01:00:00Z", ...overrides
});

const failedJob = (overrides: Partial<JobQueueItem> = {}): JobQueueItem => ({
  id: "job-1", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "dead-letter",
  payloadJson: JSON.stringify({ title: "Arrival (2016)", fileName: "Arrival.2016.mkv" }), attempts: 3, createdUtc: "2026-10-14T01:00:00Z",
  scheduledUtc: "2026-10-14T01:00:00Z", startedUtc: null, completedUtc: null, leasedUntilUtc: null, workerId: null,
  lastError: "The destination folder is full.", relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null,
  ...overrides
} as JobQueueItem);

const held = (overrides: Partial<HeldReleaseRow> = {}): HeldReleaseRow => ({
  historyId: "held-1", mediaType: "movie", id: "movie-7", episodeId: null, title: "Dune", year: 2021, episodeLabel: null, libraryId: "library-1",
  releaseName: "Dune.2021.2160p.WEB-DL-GROUP", indexerName: "Fixture", indexerId: "indexer-1", downloadUrl: null, quality: "2160p", sizeBytes: 9_000_000_000,
  seeders: 12, reason: "Larger than the cap.", heldUtc: "2026-10-14T01:00:00Z", ...overrides
});

const calls = (path: string) => authedFetchMock.mock.calls.filter(([url]) => String(url) === path);

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
  authedFetchMock.mockImplementation(() => reply());
});

afterEach(() => {
  queueLoaderData.movieRecovery = { ...emptyRecovery };
  queueLoaderData.seriesRecovery = { ...emptyRecovery };
  queueLoaderData.failedJobs = [];
  queueLoaderData.heldReleases = [];
  queueLoaderData.processorHandoffs = [];
  queueLoaderData.dispatches = [];
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
});

describe("Needs attention: a failed import job", () => {
  beforeEach(() => {
    queueLoaderData.failedJobs = [failedJob()];
  });

  it("shows the title, the reason in plain words, the attempt and a way to select and dismiss it", () => {
    open();

    expect(screen.getByText("Arrival (2016)")).toBeVisible();
    expect(screen.getByText("Import job")).toBeVisible();
    expect(screen.getByText("Attempt 3")).toBeVisible();
    expect(screen.getByRole("switch", { name: /Select Arrival/ })).toBeVisible();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeVisible();
  });

  it("Dismiss posts the job's own dismiss and says the title is dismissed", async () => {
    open();

    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Arrival (2016) dismissed"));
    expect(calls("/api/jobs/job-1/dismiss")).toHaveLength(1);
  });

  it("Retry N failed posts the retry for every failed job and says how many were requeued", async () => {
    queueLoaderData.failedJobs = [failedJob(), failedJob({ id: "job-2", payloadJson: JSON.stringify({ title: "Dune (2021)" }) })];
    open();

    fireEvent.click(screen.getByRole("button", { name: "Retry 2 failed" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("2 failed jobs requeued"));
    expect(calls("/api/jobs/retry-failed")).toHaveLength(1);
  });

  it("a job with another attempt coming is retrying, so it asks nothing of anyone and has no Retry button", () => {
    queueLoaderData.failedJobs = [failedJob({ status: "failed", attempts: 1, maxAttempts: 5, nextAttemptUtc: "2099-01-01T00:00:00Z" } as Partial<JobQueueItem>)];
    open();

    expect(screen.queryByRole("button", { name: /Retry \d+ failed/ })).not.toBeInTheDocument();
  });
});

describe("Needs attention: a recovery case", () => {
  it("Try again posts the case's own retry and shows the server's sentence", async () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase()] };
    authedFetchMock.mockImplementation(() => reply({ message: "Deluno is searching for another copy of Nosferatu." }));
    open();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Deluno is searching for another copy of Nosferatu."));
    expect(calls("/api/movies/import-recovery/case-1/retry")).toHaveLength(1);
  });

  it("a series case posts to the series side", async () => {
    queueLoaderData.seriesRecovery = { ...emptyRecovery, recentCases: [recoveryCase({ id: "case-s", title: "Some Show S01E02" })] };
    open();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(calls("/api/series/import-recovery/case-s/retry")).toHaveLength(1));
  });

  it("says why it cannot act yet when the server refuses, instead of failing blankly", async () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase()] };
    authedFetchMock.mockImplementation(() => reply({ message: "Nothing failed to import for that download any more." }, false, 409));
    open();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Nothing failed to import for that download any more."));
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("a case that names no download has no Try again, only Dismiss", () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase({ failureKind: "corrupt", detailsJson: null })] };
    open();

    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeVisible();
  });

  it("a download that vanished reads Search for another, offers Stop wanting it, and that posts stop-wanting", async () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase({ failureKind: "vanished-downloading", summary: "The client no longer lists it." })] };
    authedFetchMock.mockImplementation(() => reply({ message: "Deluno stopped wanting Nosferatu." }));
    open();

    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Search for another" })).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Stop wanting it" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Deluno stopped wanting Nosferatu."));
    expect(calls("/api/movies/import-recovery/case-1/stop-wanting")).toHaveLength(1);
  });

  it("a processed file that is less than the original offers the processor again, the original or another release, and never a blocklist", async () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase({ failureKind: "processedFileIsLess", summary: "The processed file runs 1h 02m; the download runs 2h 46m." })] };
    open();

    expect(screen.getByRole("button", { name: "Run the processor again" })).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Import original" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Nosferatu will be imported unprocessed."));
    fireEvent.click(screen.getByRole("button", { name: "Search for another release" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Searching for another release for Nosferatu."));
    fireEvent.click(screen.getByRole("button", { name: "Run the processor again" }));

    await waitFor(() => expect(calls("/api/movies/import-recovery/case-1/retry")).toHaveLength(1));
    expect(calls("/api/movies/import-recovery/case-1/import-original")).toHaveLength(1);
    expect(calls("/api/movies/import-recovery/case-1/search-again")).toHaveLength(1);
    expect(authedFetchMock.mock.calls.some(([url]) => String(url).includes("blocked-releases"))).toBe(false);
  });
});

describe("Needs attention: a release held for approval", () => {
  beforeEach(() => {
    queueLoaderData.heldReleases = [held()];
  });

  it("lists the candidate with its three answers", () => {
    open();

    expect(screen.getByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP")).toBeVisible();
    expect(screen.getByRole("button", { name: "Approve Dune.2021.2160p.WEB-DL-GROUP" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Find another for Dune (2021)" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Dismiss Dune.2021.2160p.WEB-DL-GROUP" })).toBeVisible();
  });

  it("Approve asks the server to judge and send that release, says it was sent, and never calls the grab route itself", async () => {
    open();

    fireEvent.click(screen.getByRole("button", { name: "Approve Dune.2021.2160p.WEB-DL-GROUP" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Sent Dune.2021.2160p.WEB-DL-GROUP to your download client."));
    expect(calls("/api/attention/held-releases/held-1/approve")).toHaveLength(1);
    expect(authedFetchMock.mock.calls.some(([url]) => /\/grab|\/download-clients\/.*\/grab/.test(String(url)))).toBe(false);
  });

  it("Approve shows the server's plain reason when it refuses, and sends nothing", async () => {
    authedFetchMock.mockImplementation(() => reply({ message: "Dune already has its file, so nothing was sent." }, false, 409));
    open();

    fireEvent.click(screen.getByRole("button", { name: "Approve Dune.2021.2160p.WEB-DL-GROUP" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Dune already has its file, so nothing was sent."));
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("Dismiss posts the held release's dismiss and says the title is dismissed", async () => {
    open();

    fireEvent.click(screen.getByRole("button", { name: "Dismiss Dune.2021.2160p.WEB-DL-GROUP" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Dune (2021) dismissed"));
    expect(calls("/api/attention/held-releases/held-1/dismiss")).toHaveLength(1);
  });
});

describe("Needs attention: select several and Dismiss", () => {
  it("covers a held release, a failed import, a failed hand-off and a recovery case with the same request each one's own Dismiss sends, one at a time", async () => {
    queueLoaderData.heldReleases = [held()];
    queueLoaderData.failedJobs = [failedJob()];
    queueLoaderData.movieRecovery = { ...emptyRecovery, recentCases: [recoveryCase({ failureKind: "corrupt", detailsJson: null })] };
    queueLoaderData.processorHandoffs = [{
      id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "qbit-client", queueItemId: "gone-1", releaseName: "Retwice.2021.1080p",
      sourcePath: "/downloads/retwice", processorName: "Weir", status: "failed", outputPath: null, importJobId: null, failureMessage: "Weir could not read it.",
      createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString()
    }];
    open();

    fireEvent.click(screen.getByRole("button", { name: "Select all 4 to dismiss" }));
    fireEvent.click(screen.getByRole("button", { name: "Dismiss 4 selected" }));

    await waitFor(() => expect(calls("/api/integrations/processors/handoffs/h1/dismiss")).toHaveLength(1));
    expect(calls("/api/attention/held-releases/held-1/dismiss")).toHaveLength(1);
    expect(calls("/api/jobs/job-1/dismiss")).toHaveLength(1);
    expect(calls("/api/movies/import-recovery/case-1/dismiss")).toHaveLength(1);
  });

  it("when one of them is refused the rest still go and the message says how many went and why the other did not", async () => {
    queueLoaderData.movieRecovery = {
      ...emptyRecovery,
      recentCases: [recoveryCase({ id: "case-a", title: "Film A", failureKind: "corrupt", detailsJson: null }), recoveryCase({ id: "case-b", title: "Film B", failureKind: "corrupt", detailsJson: null })]
    };
    authedFetchMock.mockImplementation((url: string) => String(url).includes("case-a") ? reply({ message: "Film A is already closed." }, false, 409) : reply());
    open();

    fireEvent.click(screen.getByRole("button", { name: "Select all 2 to dismiss" }));
    fireEvent.click(screen.getByRole("button", { name: "Dismiss 2 selected" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith(expect.stringContaining("Dismissed 1 of 2.")));
    expect(calls("/api/movies/import-recovery/case-b/dismiss")).toHaveLength(1);
  });
});

describe("Needs attention: a download that is still in the client", () => {
  it("is listed with the reason, the strikes with their evidence and Open to fix, and opens the download's own drawer", async () => {
    const item = {
      id: "qbit-9", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies", title: "Stuck Film",
      releaseName: "Stuck.Film.2020.1080p-GROUP", category: "movies", status: "stalled", progress: 60, speedMbps: 0, etaSeconds: 0, sizeBytes: 1000,
      downloadedBytes: 600, peers: 0, indexerName: "Fixture", errorMessage: null, addedUtc: new Date().toISOString(), sourcePath: null, libraryId: null,
      healthFindings: [{
        severity: "critical", kind: "no-progress", summary: "No bytes for 40 minutes.", evidence: "Nothing arrived.", recommendedAction: "Search for another.",
        canSafelyRetry: true, canSafelyRemove: true, strikeCount: 3, candidateBlocked: true, ignoredUntilUtc: null, ruleId: null, ruleActionPreset: null,
        ruleStrikeLimit: null, nextStrikeUtc: null
      }]
    } as unknown as DownloadQueueItem;
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 1, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
        queue: [item], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    open();

    expect(screen.getByText("Release refused: it stopped downloading")).toBeVisible();
    expect(screen.getByText("3 strikes · Nothing arrived.")).toBeVisible();
    expect(screen.getByText("Open to fix")).toBeVisible();
    fireEvent.click(screen.getByText("Release refused: it stopped downloading"));

    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("Stuck.Film.2020.1080p-GROUP")).toBeVisible();
    expect(drawer.getByRole("button", { name: "Remove from client" })).toBeVisible();
  });
});
