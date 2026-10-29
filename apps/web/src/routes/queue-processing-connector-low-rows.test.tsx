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

const connection = (over: Record<string, unknown> = {}) => ({
  id: "pc1", name: "Weir", provider: "generic-webhook", submissionUrl: "http://127.0.0.1:1/", authHeaderName: "X-Key", secretConfigured: true, isEnabled: true,
  healthStatus: "healthy", lastHealthMessage: null, lastHealthTestUtc: null, createdUtc: "2026-10-01T00:00:00Z", ...over
});
const handoff = (over: Record<string, unknown> = {}) => ({
  id: "h1", libraryId: "l1", mediaType: "movies", clientId: "gone-client", queueItemId: "gone-1", releaseName: "Cleaned.Film.2026.1080p", sourcePath: "C:/Downloads/Cleaned.Film.2026.1080p",
  processorName: "Weir", status: "completed", outputPath: "C:/Out/Cleaned.Film.2026.mkv", importJobId: null, failureMessage: null,
  createdUtc: new Date(Date.now() - 600_000).toISOString(), updatedUtc: new Date(Date.now() - 300_000).toISOString(), ...over
});

beforeEach(() => {
  queueLoaderData.processorHandoffs = [];
  (queueLoaderData as { processorConnections: unknown[] }).processorConnections = [];
});
afterEach(() => {
  (queueLoaderData as { processorConnections: unknown[] }).processorConnections = [];
});

const show = () => render(<MemoryRouter><QueuePage /></MemoryRouter>);
const connectors = (...items: unknown[]) => { (queueLoaderData as { processorConnections: unknown[] }).processorConnections = items; };

describe("Processing connector card (low rows)", () => {
  it("is not drawn with no connector configured and no hand-off without a row of its own", () => {
    show();
    expect(screen.queryByText("Processing Connector")).toBeNull();
  });

  it("is drawn for a configured connector alone, saying 'N configured · latest M' and that no media is being processed", () => {
    connectors(connection());
    show();

    expect(screen.getByText("Processing Connector")).toBeInTheDocument();
    expect(screen.getByText("1 configured · latest 0")).toBeInTheDocument();
    expect(screen.getByText("Weir · healthy")).toBeInTheDocument();
    expect(screen.getByText("No media is being processed")).toBeInTheDocument();
  });

  it("counts the hand-offs with the processor as active beside the configured connectors", () => {
    connectors(connection());
    queueLoaderData.processorHandoffs = [handoff({ id: "h2", status: "started", outputPath: null })];
    show();

    expect(screen.getByText("1 active · 1 configured")).toBeInTheDocument();
  });

  it("says everything with the processor is listed elsewhere when every hand-off already has a row in the pipeline", () => {
    connectors(connection());
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "gone-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 1, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
        queue: [queueItem({ id: "gone-1", clientId: "gone-client", releaseName: "Cleaned.Film.2026.1080p", title: "Cleaned Film", status: "processing" })],
        history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    queueLoaderData.processorHandoffs = [handoff({ status: "started", outputPath: null })];
    show();

    expect(screen.getByText("Everything with the processor is listed elsewhere on this page")).toBeInTheDocument();
  });

  it("lists a hand-off with its release and Movies or TV, the connector, source and output, and the status", () => {
    connectors(connection());
    queueLoaderData.processorHandoffs = [
      handoff(),
      handoff({ id: "h3", queueItemId: "gone-3", mediaType: "tv", releaseName: "Cleaned.Show.S01E01", sourcePath: "C:/Downloads/Cleaned.Show.S01E01", outputPath: null, status: "started" })
    ];
    show();

    const movieRow = screen.getByText("Cleaned.Film.2026.1080p").closest("[role='row']") as HTMLElement;
    expect(within(movieRow).getByText("Movies")).toBeInTheDocument();
    expect(within(movieRow).getByText("Weir")).toBeInTheDocument();
    expect(within(movieRow).getByText("C:/Out/Cleaned.Film.2026.mkv")).toBeInTheDocument();
    expect(within(movieRow).getByText("From C:/Downloads/Cleaned.Film.2026.1080p")).toBeInTheDocument();

    const tvRow = screen.getByText("Cleaned.Show.S01E01", { selector: "span, p, div" }).closest("[role='row']") as HTMLElement;
    expect(within(tvRow).getByText("TV")).toBeInTheDocument();
    expect(within(tvRow).getByText("Source hand-off")).toBeInTheDocument();
  });

  it("a hand-off row opens its drawer with Outcome, When, Kind, From and the output, without repeating the outcome as a Detail", async () => {
    connectors(connection());
    queueLoaderData.processorHandoffs = [handoff()];
    show();

    fireEvent.click(screen.getByText("Cleaned.Film.2026.1080p"));
    const drawer = within(await screen.findByRole("dialog"));

    for (const label of ["Outcome", "When", "Kind", "From", "Output"]) expect(drawer.getAllByText(label).length).toBeGreaterThan(0);
    expect(drawer.getAllByText("Processed output confirmed")).toHaveLength(1);
    expect(drawer.getByText("Processor hand-off")).toBeInTheDocument();
    expect(drawer.queryByText("Detail")).toBeNull();
    expect(drawer.getAllByText("C:/Out/Cleaned.Film.2026.mkv").length).toBeGreaterThan(0);
    expect(drawer.getAllByText("C:/Downloads/Cleaned.Film.2026.1080p").length).toBeGreaterThan(0);
  });
});
