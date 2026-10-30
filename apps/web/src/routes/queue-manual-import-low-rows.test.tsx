import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadHealthFinding, DownloadQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/**

 * Low-severity In progress rows (#1329). Harness copied from the drawer tests. The download drawer's other buttons under In progress: the findings in its Health box, the way out of a failed hand-off, the
 * client's own re-check and reorder buttons, and the removal dialog. Each press sends the request the server's routes are
 * tested against, and says in words what happened.
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

const finding = (overrides: Partial<DownloadHealthFinding> = {}): DownloadHealthFinding => ({
  severity: "warning", kind: "client-stalled", summary: "The client says this download has stalled.", evidence: "No bytes for 40 minutes.",
  recommendedAction: "Give it time or search for another release.", canSafelyRetry: true, canSafelyRemove: false, strikeCount: 1,
  candidateBlocked: false, ignoredUntilUtc: null, ruleId: null, ruleActionPreset: null, ruleStrikeLimit: null, nextStrikeUtc: null,
  ...overrides
});

function queueItem(overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id: "qbit-1", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies",
    title: "Example Movie", releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP", category: "movies", status: "downloading", progress: 40,
    speedMbps: 1.5, etaSeconds: 600, sizeBytes: 4_294_967_296, downloadedBytes: 1_073_741_824, peers: 6, indexerName: "Fixture indexer",
    errorMessage: null, addedUtc: new Date(Date.now() - 600_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null,
    ...overrides
  };
}

function openDrawerFor(item: DownloadQueueItem, capabilities: Partial<DownloadClientTelemetrySnapshot["capabilities"]> = {}) {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form", ...capabilities },
      summary: { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 1.5, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: [item], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
    }]
  };
  render(
    <MemoryRouter>
      <QueuePage />
    </MemoryRouter>
  );
  fireEvent.click(screen.getAllByText(item.releaseName)[0]!.closest('[role="row"]') ?? screen.getAllByText(item.releaseName)[0]!);
}

const calls = (path: string) => authedFetchMock.mock.calls.filter(([url]) => String(url) === path);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as { body: string }).body));

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  authedFetchMock.mockImplementation(() => reply());
});

afterEach(() => {
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
  queueLoaderData.processorHandoffs = [];
});

const preview = (over: Record<string, unknown> = {}) => ({
  sourcePath: "C:\\Downloads\\Some.Release.2024", destinationFolder: "C:\\Movies\\Some Release (2024)", destinationPath: "C:\\Movies\\Some Release (2024)\\Some Release (2024).mkv",
  preferredTransferMode: "copy", hardlinkAvailable: false, matchedRuleId: "m", matchedRuleName: "Movies", sourceExists: true, destinationExists: false,
  sourceSizeBytes: 4096, destinationSizeBytes: 0, isSupportedMediaFile: true, mediaProbe: null, transferExplanation: "Copies it.", warnings: [], explanation: "Matched.",
  decisionSteps: [], pack: null, ...over
});
const PATH_BOX = "D:/Downloads/Some.Release.2024";

function openManual() {
  render(<MemoryRouter><QueuePage /></MemoryRouter>);
  fireEvent.click(screen.getByRole("button", { name: /Manual import/ }));
  return screen.getByRole("dialog");
}
const drawer = () => within(screen.getByRole("dialog"));

describe("Manual import drawer: one file (low rows)", () => {
  it("has the source path box, Movies and TV for the media type, and four transfer methods", () => {
    openManual();

    expect(drawer().getByPlaceholderText(PATH_BOX)).toBeInTheDocument();
    expect(drawer().getByRole("radio", { name: "Movies" })).toBeInTheDocument();
    expect(drawer().getByRole("radio", { name: "TV" })).toBeInTheDocument();
    const methods = Array.from(drawer().getByRole("combobox", { name: /Transfer method/ }).querySelectorAll("option")).map((option) => option.textContent);
    expect(methods).toEqual(["Automatic", "Single-copy link (hardlink)", "Copy the file", "Move the file"]);
  });

  it("sends the optional Title, Year and File name, and the chosen media type, with the preview", async () => {
    authedFetchMock.mockImplementation((path: string) => (path === "/api/filesystem/import/preview" ? reply(preview()) : reply()));
    openManual();
    fireEvent.change(drawer().getByPlaceholderText(PATH_BOX), { target: { value: "D:/Downloads/Odd.Name" } });
    fireEvent.click(drawer().getByRole("radio", { name: "TV" }));
    fireEvent.change(drawer().getByRole("textbox", { name: /Title/ }), { target: { value: "Some Release" } });
    fireEvent.change(drawer().getByRole("spinbutton", { name: /Year/ }), { target: { value: "2024" } });
    fireEvent.change(drawer().getByRole("textbox", { name: /File name/ }), { target: { value: "movie.mkv" } });

    fireEvent.click(drawer().getByRole("button", { name: "Preview" }));

    await waitFor(() => expect(calls("/api/filesystem/import/preview")).toHaveLength(1));
    expect(bodyOf(calls("/api/filesystem/import/preview")[0])).toMatchObject({ sourcePath: "D:/Downloads/Odd.Name", mediaType: "tv", title: "Some Release", fileName: "movie.mkv" });
    expect(JSON.stringify(bodyOf(calls("/api/filesystem/import/preview")[0]))).toContain("2024");
  });

  it("keeps Preview and Queue import disabled until a source path is typed", () => {
    openManual();

    expect(drawer().getByRole("button", { name: "Preview" })).toBeDisabled();
    expect(drawer().getByRole("button", { name: "Queue import" })).toBeDisabled();

    fireEvent.change(drawer().getByPlaceholderText(PATH_BOX), { target: { value: "D:/Downloads/Some.Release.2024" } });

    expect(drawer().getByRole("button", { name: "Preview" })).toBeEnabled();
    expect(drawer().getByRole("button", { name: "Queue import" })).toBeEnabled();
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/filesystem/import/jobs", expect.anything());
  });

  async function previewWith(over: Record<string, unknown>, transfer?: string) {
    authedFetchMock.mockImplementation((path: string) => (path === "/api/filesystem/import/preview" ? reply(preview(over)) : reply()));
    openManual();
    fireEvent.change(drawer().getByPlaceholderText(PATH_BOX), { target: { value: "D:/Downloads/Some.Release.2024" } });
    if (transfer) fireEvent.change(drawer().getByRole("combobox", { name: /Transfer method/ }), { target: { value: transfer } });
    fireEvent.click(drawer().getByRole("button", { name: "Preview" }));
  }

  it("says a download client is still sharing the file and that the copy leaves the original", async () => {
    await previewWith({ clientHoldsSource: true });

    expect(await drawer().findByText(/A download client is still sharing this file\. Deluno copies it and leaves the original where the client expects it\./)).toBeInTheDocument();
    expect(drawer().queryByText(/Moving it takes the file away from that client/)).toBeNull();
  });

  it("adds a warning that moving takes the file away from the sharing client, when Move is chosen", async () => {
    await previewWith({ clientHoldsSource: true }, "move");

    expect(await drawer().findByText(/Moving it takes the file away from that client, which will lose it and stop sharing\./)).toBeInTheDocument();
  });

  it("says nothing about a sharing client when none holds the file", async () => {
    await previewWith({ clientHoldsSource: false });

    await waitFor(() => expect(calls("/api/filesystem/import/preview")).toHaveLength(1));
    await drawer().findByText("Preview ready — check the destination before queueing.");
    expect(drawer().queryByText(/still sharing this file/)).toBeNull();
  });
});

describe("Manual import drawer: scan a folder (low rows)", () => {
  const scan = (items: unknown[], issues: string[] = []) => (path: string) => (path === "/api/filesystem/import/scan-folder" ? reply({ folderPath: "D:/Batch", items, issues }) : reply());
  async function scanWith(items: unknown[], issues: string[] = []) {
    authedFetchMock.mockImplementation(scan(items, issues));
    openManual();
    fireEvent.click(drawer().getByRole("radio", { name: "Scan a folder" }));
    fireEvent.change(drawer().getByPlaceholderText(PATH_BOX), { target: { value: "D:/Batch" } });
    fireEvent.click(drawer().getByRole("button", { name: "Scan folder" }));
  }

  it("keeps Scan folder disabled until a folder is typed", () => {
    openManual();
    fireEvent.click(drawer().getByRole("radio", { name: "Scan a folder" }));

    expect(drawer().getByRole("button", { name: "Scan folder" })).toBeDisabled();
    fireEvent.change(drawer().getByPlaceholderText(PATH_BOX), { target: { value: "D:/Batch" } });
    expect(drawer().getByRole("button", { name: "Scan folder" })).toBeEnabled();
  });

  it("reads 'No supported video files were found in this folder.' for an empty result and lists the scan issues as warnings", async () => {
    await scanWith([], ["One subfolder could not be read."]);

    expect(await drawer().findByText("No supported video files were found in this folder.")).toBeInTheDocument();
    expect(drawer().getByText("One subfolder could not be read.").closest("li")).not.toBeNull();
  });

  it("disables Import selected when nothing is included, and enables it once a row is", async () => {
    await scanWith([{ sourcePath: "D:/Batch/unknown_file.mkv", fileName: "unknown_file.mkv", guessedTitle: null, guessedYear: null, matched: false, preview: preview({ destinationPath: "" }) }]);

    await drawer().findByText("unknown_file.mkv");
    expect(drawer().getByRole("button", { name: "Import selected" })).toBeDisabled();
    expect(calls("/api/filesystem/import/execute-batch")).toHaveLength(0);

    fireEvent.click(drawer().getAllByRole("checkbox")[0]);
    expect(drawer().getByRole("button", { name: "Import selected" })).toBeEnabled();
  });
});
