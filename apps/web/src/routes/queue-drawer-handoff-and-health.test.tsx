import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadHealthFinding, DownloadQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/**
 * The download drawer's other buttons on Transfers: the findings in its Health box, the way out of a failed hand-off, the
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
  fireEvent.click(screen.getByText(item.releaseName));
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

describe("download drawer: In the client", () => {
  it("Re-check posts the recheck action for that download and says Re-check was sent to the client", async () => {
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Re-check" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Re-check sent to qBittorrent"));
    expect(bodyOf(calls("/api/download-clients/qbit-client/queue/actions")[0])).toEqual({ action: "recheck", queueItemId: "qbit-1" });
  });

  it.each([
    ["Move to top of queue", "move-to-top", "Move to top sent to qBittorrent"],
    ["Move up in queue", "move-up", "Move up sent to qBittorrent"],
    ["Move down in queue", "move-down", "Move down sent to qBittorrent"],
    ["Move to bottom of queue", "move-to-bottom", "Move to bottom sent to qBittorrent"]
  ])("the %s button posts its own action, %s, and says so", async (label, action, toastText) => {
    openDrawerFor(queueItem(), { supportsQueueReorder: true });

    fireEvent.click(screen.getByRole("button", { name: label }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith(toastText));
    expect(bodyOf(calls("/api/download-clients/qbit-client/queue/actions")[0])).toEqual({ action, queueItemId: "qbit-1" });
  });

  it("offers no reorder buttons to a client that cannot, and says why in words", () => {
    openDrawerFor(queueItem(), { supportsQueueReorder: false });

    expect(screen.queryByRole("button", { name: "Move up in queue" })).not.toBeInTheDocument();
    expect(screen.getByText("Reordering the queue is not available for qBittorrent.")).toBeVisible();
  });

  it("a refused action shows the server's own sentence as an error and opens the buttons again", async () => {
    authedFetchMock.mockImplementation(() => reply({ message: "qBittorrent does not have that download." }, false, 409));
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Pause" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("qBittorrent does not have that download."));
    expect(toastSuccess).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole("button", { name: "Resume" })).toBeEnabled());
    expect(screen.getByRole("button", { name: "Pause" })).toBeEnabled();
  });

  it("while one action is out, every other button in the drawer is closed", async () => {
    authedFetchMock.mockReturnValue(new Promise(() => undefined));
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Pause" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Resume" })).toBeDisabled());
    expect(screen.getByRole("button", { name: "Re-check" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Remove from client" })).toBeDisabled();
  });

  it("Ignore this download posts its hash and name to the ignore list and says it is exempt", async () => {
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Ignore this download" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Added to the ignore list — exempt from every health rule until removed"));
    expect(bodyOf(calls("/api/download-health/ignore-list")[0])).toEqual({ hash: "qbit-1", note: "Example Movie" });
  });
});

describe("download drawer: a finding in the Health box", () => {
  it("Dismiss quiets that kind of finding on that download for seven days and says so", async () => {
    openDrawerFor(queueItem({ healthFindings: [finding()] }));

    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Dismissed for a week, or until it happens again"));
    const call = calls("/api/download-clients/qbit-client/queue/qbit-1/health/client-stalled/ignore")[0];
    expect(call).toBeDefined();
    expect(bodyOf(call)).toEqual({ durationDays: 7 });
  });

  it("a SABnzbd queue-level warning is cleared at the client, not ignored for one download", async () => {
    openDrawerFor(queueItem({ healthFindings: [finding({ kind: "sabnzbd-client-warning", summary: "SABnzbd has a warning." })] }));

    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Cleared this client's warnings"));
    expect(calls("/api/download-clients/qbit-client/warnings/clear")).toHaveLength(1);
    expect(authedFetchMock.mock.calls.some(([url]) => String(url).includes("/health/"))).toBe(false);
  });

  it("a lost hard link offers Remove this copy, which removes it with the reason reclaim, instead of Dismiss", async () => {
    authedFetchMock.mockImplementation(() => reply({ succeeded: true, message: "Removed the copy from qBittorrent. The library file was not touched." }));
    openDrawerFor(queueItem({ healthFindings: [finding({ kind: "hardlink-lost", summary: "This copy no longer shares data with the library." })] }));
    const drawer = within(await screen.findByRole("dialog"));

    expect(drawer.queryByRole("button", { name: "Dismiss" })).not.toBeInTheDocument();
    fireEvent.click(drawer.getByRole("button", { name: "Remove this copy" }));

    await waitFor(() => expect(calls("/api/download-clients/qbit-client/queue/qbit-1/remove")).toHaveLength(1));
    expect(bodyOf(calls("/api/download-clients/qbit-client/queue/qbit-1/remove")[0])).toEqual({ reason: "reclaim" });
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Removed the copy from qBittorrent. The library file was not touched."));
  });
});

describe("download drawer: Remove from client asks why", () => {
  it.each([
    ["It's bad", "failed", "Removed it, deleted its files, blocked the release and started a new search."],
    ["Just stop tracking it", "forget", "Deluno stopped tracking it. qBittorrent keeps the download and its files untouched."]
  ])("choosing %s posts reason %s and the toast carries the server's sentence", async (label, reason, sentence) => {
    authedFetchMock.mockImplementation(() => reply({ succeeded: true, message: sentence }));
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "Remove from client" }));
    fireEvent.click(await screen.findByRole("button", { name: new RegExp(label.replace("'", "'")) }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith(sentence));
    expect(bodyOf(calls("/api/download-clients/qbit-client/queue/qbit-1/remove")[0])).toEqual({ reason });
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("a refused removal shows the server's message, keeps the dialog open and opens the choices again", async () => {
    authedFetchMock.mockImplementation(() => reply({ succeeded: false, message: "qBittorrent is not answering, so nothing was removed." }, false, 400));
    openDrawerFor(queueItem());
    fireEvent.click(screen.getByRole("button", { name: "Remove from client" }));

    fireEvent.click(await screen.findByRole("button", { name: /I don't want it/ }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("qBittorrent is not answering, so nothing was removed."));
    expect(toastSuccess).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole("button", { name: /I don't want it/ })).toBeEnabled());
    expect(screen.getByRole("button", { name: /Just stop tracking it/ })).toBeEnabled();
  });
});

describe("download drawer: the way out of a hand-off the processor did not finish", () => {
  const release = "Nosferatu.2024.1080p.BluRay-GROUP";
  const handoff = (status: string) => ({
    id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "qbit-client", queueItemId: "qbit-1", releaseName: release,
    sourcePath: "/downloads/nosferatu", processorName: "Weir", status, outputPath: null, importJobId: null,
    failureMessage: "Weir could not read the file.", createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString()
  });

  function openFailedHandoff(status = "failed") {
    queueLoaderData.processorHandoffs = [handoff(status)];
    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );
    fireEvent.click(screen.getAllByText(release)[0]);
  }

  it("Import original posts the hand-off's import-original and says the release will be imported unprocessed", async () => {
    openFailedHandoff();

    fireEvent.click(await screen.findByRole("button", { name: "Import original" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith(`${release} will be imported unprocessed`));
    expect(calls("/api/integrations/processors/handoffs/h1/import-original")).toHaveLength(1);
  });

  it("Search for another release posts search-again, says what the server did and closes the drawer", async () => {
    authedFetchMock.mockImplementation(() => reply({ message: "Deluno blocklisted the release and started a new search." }));
    openFailedHandoff();

    fireEvent.click(await screen.findByRole("button", { name: "Search for another release" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Deluno blocklisted the release and started a new search."));
    expect(calls("/api/integrations/processors/handoffs/h1/search-again")).toHaveLength(1);
    await waitFor(() => expect(screen.queryByRole("button", { name: "Search for another release" })).not.toBeInTheDocument());
  });

  it("Keep waiting posts keep-waiting for that hand-off and says what the server said", async () => {
    authedFetchMock.mockImplementation(() => reply({ message: "Deluno will keep waiting for Weir." }));
    openFailedHandoff();

    fireEvent.click(await screen.findByRole("button", { name: "Keep waiting" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Deluno will keep waiting for Weir."));
    expect(calls("/api/integrations/processors/handoffs/h1/keep-waiting")).toHaveLength(1);
  });

  it("Dismiss posts the dismiss, says the release is dismissed and closes the drawer", async () => {
    openFailedHandoff();

    fireEvent.click(await screen.findByRole("button", { name: "Dismiss this hand-off" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith(`${release} dismissed`));
    expect(calls("/api/integrations/processors/handoffs/h1/dismiss")).toHaveLength(1);
  });

  it("a hand-off whose download was gone says nothing can be sent again and offers another copy and Dismiss only", async () => {
    openFailedHandoff("source-gone");

    expect(await screen.findByText(/The download was gone before Weir could clean it, so there is nothing to send again or import\./)).toBeVisible();
    expect(screen.getByRole("button", { name: "Search for another copy" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Dismiss this hand-off" })).toBeVisible();
    expect(screen.queryByRole("button", { name: /Send to Weir again/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Import original" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Search for another release" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Search for another copy" }));

    await waitFor(() => expect(calls("/api/integrations/processors/handoffs/h1/search-again")).toHaveLength(1));
  });
});
