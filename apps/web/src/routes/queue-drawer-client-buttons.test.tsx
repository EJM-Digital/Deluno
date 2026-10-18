import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

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
const queueLoaderData = {
  telemetry: {
    summary: {
      activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0,
      processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0
    },
    clients: [] as DownloadClientTelemetrySnapshot[],
    capturedUtc: new Date().toISOString()
  },
  dispatches: [],
  movieRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] },
  seriesRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] },
  libraries: [],
  settings: emptyPlatformSettingsSnapshot,
  jobs: [],
  failedJobs: [],
  healthRecords: [],
  processorConnections: [],
  processorHandoffs: [],
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

function okResponse(body: unknown = {}) {
  return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

function queueItem(overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id: "qbit-1",
    clientId: "qbit-client",
    clientName: "qBittorrent",
    protocol: "qbittorrent",
    mediaType: "movies",
    title: "Example Movie",
    releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP",
    category: "movies",
    status: "downloading",
    progress: 40,
    speedMbps: 1.5,
    etaSeconds: 600,
    sizeBytes: 4_294_967_296,
    downloadedBytes: 1_073_741_824,
    peers: 6,
    indexerName: "Fixture indexer",
    errorMessage: null,
    addedUtc: new Date(Date.now() - 600_000).toISOString(),
    sourcePath: null,
    libraryId: null,
    healthFindings: null,
    ...overrides
  };
}

function openDrawerFor(item: DownloadQueueItem) {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client",
      clientName: "qBittorrent",
      protocol: "qbittorrent",
      endpointUrl: null,
      healthStatus: "healthy",
      lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 1.5, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: [item],
      history: [],
      capturedUtc: new Date().toISOString(),
      historyTruncated: false
    }]
  };
  render(
    <MemoryRouter>
      <QueuePage />
    </MemoryRouter>
  );
  fireEvent.click(screen.getByText(item.releaseName));
}

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  authedFetchMock.mockImplementation(() => okResponse());
});

afterEach(() => {
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
  queueLoaderData.orphans = [];
  queueLoaderData.libraries = [];
});

describe("download drawer: the client's own buttons (In the client)", () => {
  it("Pause posts the pause action for that download to its client and says Pause was sent to the client", async () => {
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Pause" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Pause sent to qBittorrent"), { timeout: 30_000 });
    const call = authedFetchMock.mock.calls.find(([path]) => String(path) === "/api/download-clients/qbit-client/queue/actions");
    expect(call).toBeDefined();
    expect(call![1]).toMatchObject({ method: "POST" });
    expect(JSON.parse(String((call![1] as { body: string }).body))).toEqual({ action: "pause", queueItemId: "qbit-1" });
  }, 40_000);

  it("Resume posts the resume action for that download to its client and says Resume was sent to the client", async () => {
    openDrawerFor(queueItem({ status: "paused" }));

    fireEvent.click(screen.getByRole("button", { name: "Resume" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Resume sent to qBittorrent"));
    const call = authedFetchMock.mock.calls.find(([path]) => String(path) === "/api/download-clients/qbit-client/queue/actions");
    expect(call).toBeDefined();
    expect(JSON.parse(String((call![1] as { body: string }).body))).toEqual({ action: "resume", queueItemId: "qbit-1" });
  });

  it("a client that refuses the action shows its reason and does not say it was sent", async () => {
    authedFetchMock.mockImplementation(() => Promise.resolve({
      ok: false, status: 409, json: () => Promise.resolve({ message: "qBittorrent does not have that download." }),
      text: () => Promise.resolve(JSON.stringify({ message: "qBittorrent does not have that download." })), url: "test"
    } as Response));
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Pause" }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(toastSuccess).not.toHaveBeenCalledWith("Pause sent to qBittorrent");
  });
});

describe("download drawer: Queue import is open only to a download that is ready to import", () => {
  const ready = () => queueItem({ status: "importReady", progress: 100, downloadedBytes: 4_294_967_296, sourcePath: "C:\\Downloads\\Example.Movie.2026.1080p.WEB-DL-GROUP.mkv" });

  it("is disabled while the download is still downloading", () => {
    openDrawerFor(queueItem({ sourcePath: "C:\\Downloads\\Example.Movie.2026.1080p.WEB-DL-GROUP.mkv" }));

    expect(screen.getByRole("button", { name: "Queue import" })).toBeDisabled();
  });

  it("posts the import job with automatic transfer, a copy fallback allowed and the grab it came from, then says the title was queued", async () => {
    authedFetchMock.mockImplementation((path: string) => okResponse(path === "/api/filesystem/import/jobs"
      ? { jobId: "job-1", preview: { sourcePath: "C:\\Downloads\\Example.Movie.2026.1080p.WEB-DL-GROUP.mkv", destinationPath: "Movies/Example Movie (2026)/Example Movie (2026).mkv", destinationFolder: "Movies", warnings: [], decisionSteps: [] }, job: {} }
      : {}));
    openDrawerFor(ready());

    const button = screen.getByRole("button", { name: "Queue import" });
    expect(button).toBeEnabled();
    fireEvent.click(button);

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Example Movie queued for import"));
    const call = authedFetchMock.mock.calls.find(([path]) => String(path) === "/api/filesystem/import/jobs");
    expect(call).toBeDefined();
    const body = JSON.parse(String((call![1] as { body: string }).body));
    expect(body).toMatchObject({ transferMode: "auto", allowCopyFallback: true, overwrite: false, forceReplacement: false, dispatchId: null });
    expect(body.preview.sourcePath).toBe("C:\\Downloads\\Example.Movie.2026.1080p.WEB-DL-GROUP.mkv");
  });
});

describe("leftover downloads: Remove N selected", () => {
  function orphan(name: string): OrphanedDownloadItem {
    return {
      path: `C:\\Downloads\\Complete\\${name}`,
      name,
      libraryId: "library-1",
      libraryName: "TV",
      sizeBytes: 1_073_741_824,
      lastWrittenUtc: new Date().toISOString()
    };
  }

  function selectAllAndRemove() {
    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: "Select all leftovers" }));
    fireEvent.click(screen.getByRole("button", { name: /^Remove \d+ selected$/ }));
  }

  it("asks the server to remove each selected leftover one at a time and says how many were removed", async () => {
    queueLoaderData.orphans = [orphan("First.S01E01"), orphan("Second.S01E02")];

    selectAllAndRemove();

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Removed 2 leftover downloads."));
    const removals = authedFetchMock.mock.calls.filter(([path]) => String(path) === "/api/download-clients/orphans/remove");
    expect(removals.map(([, init]) => JSON.parse(String((init as { body: string }).body)))).toEqual([
      { path: "C:\\Downloads\\Complete\\First.S01E01" },
      { path: "C:\\Downloads\\Complete\\Second.S01E02" }
    ]);
  });

  it("one that fails does not stop the rest, and the message says how many went and why the other did not", async () => {
    queueLoaderData.orphans = [orphan("First.S01E01"), orphan("Second.S01E02")];
    authedFetchMock.mockImplementation((path: string, init: { body?: string }) => {
      if (path === "/api/download-clients/orphans/remove" && String(init?.body).includes("First.S01E01")) {
        return Promise.resolve({ ok: false, status: 409, json: () => Promise.resolve({ message: "Deluno did not download that folder." }), text: () => Promise.resolve("") } as Response);
      }
      return okResponse();
    });

    selectAllAndRemove();

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Removed 1 of 2. Deluno did not download that folder."));
    const removals = authedFetchMock.mock.calls.filter(([path]) => String(path) === "/api/download-clients/orphans/remove");
    expect(removals).toHaveLength(2);
  });
});
