import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadHealthFinding, DownloadQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/**

 * Low-severity Transfers rows (#1329). Harness copied from the drawer tests. The download drawer's other buttons on Transfers: the findings in its Health box, the way out of a failed hand-off, the
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
  fireEvent.click(screen.getAllByText(item.releaseName)[0]);
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

const dialog = () => within(screen.getByRole("dialog"));

describe("download drawer: Health section", () => {
  it("shows nothing called Health when there is no client error, no integration failure and no finding", () => {
    openDrawerFor(queueItem());
    expect(dialog().queryByText("Health")).toBeNull();
  });

  it("shows the client's error message under Health", () => {
    openDrawerFor(queueItem({ errorMessage: "The client reports a disk error." }));
    expect(dialog().getByText("Health")).toBeTruthy();
    expect(dialog().getByText("The client reports a disk error.")).toBeTruthy();
  });

  it("shows an integration failure under Health", () => {
    openDrawerFor(queueItem({ failure: { kind: "disk", summary: "The download folder is full.", message: "No space", nextAction: "Free some space.", occurredUtc: new Date().toISOString() } as never }));
    expect(dialog().getByText("Health")).toBeTruthy();
    expect(dialog().getAllByText(/download folder is full/i).length).toBeGreaterThan(0);
  });

  it("shows a visible finding under Health with its Dismiss button and recommended action", () => {
    openDrawerFor(queueItem({ healthFindings: [finding()] }));
    expect(dialog().getByText("Health")).toBeTruthy();
    expect(dialog().getByText("The client says this download has stalled.")).toBeTruthy();
    expect(dialog().getByText("Give it time or search for another release.")).toBeTruthy();
    expect(dialog().getByRole("button", { name: "Dismiss" })).toBeTruthy();
  });
});

describe("download drawer: In the client (standalone actions)", () => {
  it("'What would removing do?' reads the cleanup preview and lists Would do, Because, Files affected and Allowed", async () => {
    authedFetchMock.mockImplementation((url: string) => String(url).includes("cleanup-preview")
      ? reply({ proposedAction: "Remove and keep the files", reason: "It has stalled for two days.", affectedFiles: "Example.Movie.mkv", removalAllowed: false })
      : reply());
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: "What would removing do?" }));

    await screen.findByText("Would do");
    expect(calls("/api/download-clients/qbit-client/queue/qbit-1/cleanup-preview").length).toBe(1);
    expect(dialog().getByText("Remove and keep the files")).toBeTruthy();
    expect(dialog().getByText("It has stalled for two days.")).toBeTruthy();
    expect(dialog().getByText("Example.Movie.mkv")).toBeTruthy();
    expect(dialog().getByText("Needs review first")).toBeTruthy();
    expect(calls("/api/download-clients/qbit-client/queue/actions").length).toBe(0);
  });

  it("Block this release in the drawer blocks the download's hash and name without removing or searching again", async () => {
    openDrawerFor(queueItem());

    fireEvent.click(screen.getByRole("button", { name: 'Block "Example Movie"' }));
    fireEvent.click(await screen.findByRole("button", { name: "Block this release" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith('"Example Movie" blocked; every indexer\'s copy is refused from now on.'));
    const post = authedFetchMock.mock.calls.find(([url, init]) => String(url) === "/api/blocked-releases" && (init as { method?: string } | undefined)?.method === "POST");
    expect(JSON.parse(String((post![1] as { body: string }).body))).toMatchObject({ releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP", torrentHashOrItemId: "qbit-1", mediaType: "movies" });
    expect(calls("/api/download-clients/qbit-client/queue/actions").length).toBe(0);
    expect(authedFetchMock.mock.calls.some(([url]) => /search|remove/i.test(String(url)))).toBe(false);
  });
});

describe("removal dialog", () => {
  it("Cancel closes the dialog without removing anything", async () => {
    openDrawerFor(queueItem());
    fireEvent.click(screen.getByRole("button", { name: "Remove from client" }));
    const choices = within(screen.getAllByRole("dialog")[1]);
    fireEvent.click(choices.getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.getAllByRole("dialog").length).toBe(1));
    expect(authedFetchMock.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === "DELETE" || (init as { method?: string } | undefined)?.method === "POST").length).toBe(0);
  });

  it("Cancel is disabled while a choice is in flight", async () => {
    authedFetchMock.mockImplementation(() => new Promise(() => undefined));
    openDrawerFor(queueItem());
    fireEvent.click(screen.getByRole("button", { name: "Remove from client" }));
    const choices = within(screen.getAllByRole("dialog")[1]);
    fireEvent.click(choices.getByRole("button", { name: /Just stop tracking it/ }));

    await waitFor(() => expect((choices.getByRole("button", { name: "Cancel" }) as HTMLButtonElement).disabled).toBe(true));
  });
});

describe("status vocabulary", () => {
  function rows(...items: DownloadQueueItem[]) {
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: items.length, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
        queue: items, history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
  }
  const chipOf = (text: string) => screen.getAllByText(text).map((n) => n.closest("span.rounded-full")).find(Boolean) as HTMLElement;

  it("a stalled download shows the Stalled chip in the warning tone", () => {
    rows(queueItem({ status: "stalled" as never }));
    expect(chipOf("Stalled").className).toContain("text-warning");
  });

  it("a download that is checking or moving reads Not transferring in the information tone", () => {
    rows(queueItem({ status: "notTransferring" as never, speedMbps: 0 }));
    expect(chipOf("Not transferring").className).toContain("text-info");
  });

  it("a download that was not handed over tells the person to check the processor connection in System and try again", () => {
    rows(queueItem({ status: "notHandedOver" as never }));
    expect(screen.getByText(/Check the processor connection in System, then try again\./)).toBeTruthy();
  });
});
