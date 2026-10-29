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

beforeEach(() => { queueLoaderData.orphans = []; });
const leftover = (name: string, over: Record<string, unknown> = {}) => ({
  path: `/downloads/complete/${name}`, name, libraryId: "library-1", libraryName: "TV", sizeBytes: 1_073_741_824, lastWrittenUtc: new Date().toISOString(), ...over
});

describe("Needs attention: leftover folders (low rows)", () => {
  it("lists a leftover with its folder name, '<library> download folder' and what is left, and opens its drawer", async () => {
    queueLoaderData.orphans = [leftover("Left.Over")];
    render(<MemoryRouter><QueuePage /></MemoryRouter>);

    const row = screen.getByText("Left.Over").closest("[role='row']") as HTMLElement;
    expect(within(row).getByText("TV download folder")).toBeInTheDocument();
    expect(within(row).getByText("Left in the download folder. No client, import or hand-off is using it.")).toBeInTheDocument();

    fireEvent.click(screen.getByText("Left.Over"));
    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByText("Left in the download folder. No client, import or hand-off is using it.")).toBeInTheDocument();
  });

  it("Select all leftovers selects the removable ones and Clear selection empties the selection again", async () => {
    queueLoaderData.orphans = [leftover("Left.One"), leftover("Left.Two")];
    render(<MemoryRouter><QueuePage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: "Select all leftovers" }));
    expect(screen.getByRole("button", { name: /^Remove \d+ selected$/ })).toBeInTheDocument();
    expect((screen.getByRole("switch", { name: "Select Left.One" }) as HTMLButtonElement).getAttribute("aria-checked")).toBe("true");

    fireEvent.click(screen.getByRole("button", { name: "Clear selection" }));

    expect(screen.getByRole("button", { name: "Select all leftovers" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Remove \d+ selected$/ })).toBeNull();
    expect((screen.getByRole("switch", { name: "Select Left.One" }) as HTMLButtonElement).getAttribute("aria-checked")).toBe("false");
    expect(authedFetchMock).not.toHaveBeenCalled();
  });
});
