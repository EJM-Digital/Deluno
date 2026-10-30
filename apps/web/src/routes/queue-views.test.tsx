import type { HeldReleaseRow } from "../lib/held-releases";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/**
 * The views of the one In progress page (#1426): live downloads, hand-offs and the history. Each view holds its own
 * cards, the chips move between them and write the address, and a release name links to its indexer.
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

function reply(body: unknown = {}) {
  return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

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


const download: DownloadQueueItem = {
  id: "qbit-1", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies",
  title: "Example Movie", releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP", category: "movies", status: "downloading", progress: 40,
  speedMbps: 1.5, etaSeconds: 600, sizeBytes: 4_294_967_296, downloadedBytes: 1_073_741_824, peers: 6, indexerName: "Fixture indexer",
  errorMessage: null, addedUtc: new Date(Date.now() - 600_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null
};

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}{location.search}</output>;
}

function show(address: string) {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 1.5, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: [download], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
    }]
  };
  (queueLoaderData as { processorConnections: unknown[] }).processorConnections = [connection()];
  queueLoaderData.processorHandoffs = [handoff()];
  return render(<MemoryRouter initialEntries={[address]}><QueuePage /><Where /></MemoryRouter>);
}

beforeEach(() => {
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(() => reply());
});

describe("In progress views", () => {
  it("opens on the live downloads: the pipeline and the cleanup guardrails, and none of the hand-off cards", () => {
    show("/queue");

    expect(screen.getByRole("radio", { name: /^Live downloads/ })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByText("Media Pipeline")).toBeInTheDocument();
    expect(screen.getByText("Cleanup Guardrails")).toBeInTheDocument();
    expect(screen.queryByText("Processing Connector")).toBeNull();
    expect(screen.queryByText("Import & Naming")).toBeNull();
  });

  it("the live downloads draw no processing connector card when there is no connector and no hand-off", () => {
    show("/queue");
    cleanup();
    (queueLoaderData as { processorConnections: unknown[] }).processorConnections = [];
    queueLoaderData.processorHandoffs = [];
    render(<MemoryRouter initialEntries={["/queue"]}><QueuePage /></MemoryRouter>);
    expect(screen.queryByText("Processing Connector")).toBeNull();
    expect(screen.queryByText("Nothing is with a processor or being imported")).toBeNull();
    expect(screen.getByText("Media Pipeline")).toBeInTheDocument();
  });

  it("the Hand-offs view holds the processing connector and the imports, and none of the live downloads' cards", () => {
    show("/queue?view=handoffs");

    expect(screen.getByRole("radio", { name: /^Hand-offs/ })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByText("Processing Connector")).toBeInTheDocument();
    expect(screen.getByText("Cleaned.Film.2026.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Media Pipeline")).toBeNull();
    expect(screen.queryByText("Cleanup Guardrails")).toBeNull();
  });

  it("says plainly that nothing is with a processor or being imported", () => {
    show("/queue?view=handoffs");
    (queueLoaderData as { processorConnections: unknown[] }).processorConnections = [];
    queueLoaderData.processorHandoffs = [];
    render(<MemoryRouter initialEntries={["/queue?view=handoffs"]}><QueuePage /></MemoryRouter>);

    expect(screen.getByText("Nothing is with a processor or being imported")).toBeInTheDocument();
  });

  it("the chips write the view into the address, and Live downloads takes it out again", () => {
    show("/queue");

    fireEvent.click(screen.getByRole("radio", { name: /^Hand-offs/ }));
    expect(screen.getByTestId("where").textContent).toBe("/queue?view=handoffs");
    expect(screen.getByText("Processing Connector")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: /^Live downloads/ }));
    expect(screen.getByTestId("where").textContent).toBe("/queue");
    expect(screen.getByText("Media Pipeline")).toBeInTheDocument();
  });

  it("the chips count what is in flight and what is with the processor", () => {
    show("/queue");

    expect(screen.getByRole("radio", { name: "Live downloads 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Hand-offs 0" })).toBeInTheDocument();
  });

  it("a release name links to its indexer's page, and pressing it does not open the download's drawer", () => {
    show("/queue");

    const link = screen.getByRole("link", { name: download.releaseName });
    expect(link).toHaveAttribute("href", "/indexers/indexers?indexer=Fixture+indexer");
    fireEvent.click(link);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("a download with no indexer name shows its release name as plain words", () => {
    download.indexerName = "";
    try {
      show("/queue");
      expect(screen.queryByRole("link", { name: download.releaseName })).toBeNull();
      expect(screen.getAllByText(download.releaseName).length).toBeGreaterThan(0);
    } finally {
      download.indexerName = "Fixture indexer";
    }
  });

  it("the link to the history opens the History view", () => {
    show("/queue");
    expect(screen.getByRole("link", { name: /See the history and events/ })).toHaveAttribute("href", "/queue?view=history");
  });

  const orphan = { path: "D:/Downloads/Left.Over", name: "Left.Over", libraryName: "Films", sizeBytes: 1000, isSafeToRemove: true };
  const importJob = (status: string) => ({
    id: "job-1", jobType: "filesystem.import.execute", status, attempts: 1, maxAttempts: 5, label: "Import", sourceLabel: "Films", target: "Arrival", lastError: null,
    payloadJson: JSON.stringify({ title: "Arrival (2016)", fileName: "Arrival.2016.mkv", sourcePath: "D:/in/Arrival.2016.mkv", destinationPath: "D:/Films/Arrival (2016)/Arrival.2016.mkv" }),
    scheduledUtc: "2026-10-01T00:00:00Z", startedUtc: "2026-10-01T00:00:01Z", completedUtc: null
  });

  it("Needs attention is on the live downloads only, and only when something needs a person", () => {
    (queueLoaderData as { orphans: unknown[] }).orphans = [orphan];
    try {
      show("/queue");
      expect(screen.getByText("Needs Attention")).toBeInTheDocument();
      const handoffsView = show("/queue?view=handoffs");
      expect(within(handoffsView.container).queryByText("Needs Attention")).toBeNull();
    } finally {
      (queueLoaderData as { orphans: unknown[] }).orphans = [];
    }
    cleanup();
    show("/queue");
    expect(screen.queryByText("Needs Attention")).toBeNull();
  });

  it("the Hand-offs view shows a connector with no hand-off yet, and not the empty note", () => {
    show("/queue?view=handoffs");
    queueLoaderData.processorHandoffs = [];
    cleanup();
    show("/queue?view=handoffs");
    queueLoaderData.processorHandoffs = [];
    cleanup();
    (queueLoaderData as { processorConnections: unknown[] }).processorConnections = [connection()];
    render(<MemoryRouter initialEntries={["/queue?view=handoffs"]}><QueuePage /></MemoryRouter>);
    expect(screen.getByText("Processing Connector")).toBeInTheDocument();
    expect(screen.queryByText("Nothing is with a processor or being imported")).toBeNull();
  });

  it("an import being written shows on the Hand-offs view with its own card, never on the live downloads, and counts on its chip", () => {
    (queueLoaderData as { jobs: unknown[] }).jobs = [importJob("running")];
    try {
      const handoffs = show("/queue?view=handoffs");
      expect(within(handoffs.container).getByText("Import & Naming")).toBeInTheDocument();
      expect(within(handoffs.container).queryByText("Nothing is with a processor or being imported")).toBeNull();
      // One import being written, and the one hand-off that is still with the processor.
      queueLoaderData.processorHandoffs = [handoff({ status: "started" })];
      cleanup();
      render(<MemoryRouter initialEntries={["/queue?view=handoffs"]}><QueuePage /></MemoryRouter>);
      expect(screen.getByRole("radio", { name: "Hand-offs 2" })).toBeInTheDocument();
      cleanup();
      show("/queue");
      expect(screen.queryByText("Import & Naming")).toBeNull();
    } finally {
      (queueLoaderData as { jobs: unknown[] }).jobs = [];
    }
  });
});
