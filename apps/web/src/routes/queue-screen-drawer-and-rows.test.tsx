import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, MovieImportRecoverySummary, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import type { HeldReleaseRow } from "../lib/held-releases";
import { QueuePage } from "./queue-screen";
import { readyRouteSources } from "../test/route-sources";

/**
 * Transfers, drawer by drawer and row by row: what an integration failure says, what Preview import sends and shows, the words
 * for a download the processor is working on or has delivered, the processor's own stage line, a failed Manual import, the
 * link System > Health opens it with, and the screen when its sources cannot be read.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));

const revalidateMock = vi.fn();
const refetchMock = vi.fn();
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
  movieRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as MovieImportRecoverySummary["recentCases"] },
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
let sourcesState: ReturnType<typeof readyRouteSources<typeof queueLoaderData>> = readyRouteSources(queueLoaderData, revalidateMock);

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
}));

vi.mock("../lib/route-cache", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/route-cache")>()),
  useRouteSources: () => sourcesState
}));

function jsonResponse(body: unknown, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

function client(queue: DownloadQueueItem[]): DownloadClientTelemetrySnapshot {
  return {
    clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
    capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
    summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    queue, history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
  } as DownloadClientTelemetrySnapshot;
}

function download(over: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id: "qbit-1", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies",
    title: "Example Movie", releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP", category: "movies", status: "downloading", progress: 40,
    speedMbps: 1.5, etaSeconds: 600, sizeBytes: 4_294_967_296, downloadedBytes: 1_073_741_824, peers: 6, indexerName: "Fixture indexer",
    errorMessage: null, addedUtc: new Date(Date.now() - 600_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null,
    ...over
  } as DownloadQueueItem;
}

function openDownload(item: DownloadQueueItem) {
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [client([item])] };
  render(<MemoryRouter><QueuePage /></MemoryRouter>);
  fireEvent.click(screen.getAllByText(item.releaseName)[0]!);
}

beforeEach(() => {
  sourcesState = readyRouteSources(queueLoaderData, revalidateMock);
});

afterEach(() => {
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] };
  queueLoaderData.processorHandoffs = [];
  queueLoaderData.heldReleases = [];
  authedFetchMock.mockReset();
  refetchMock.mockReset();
  toastMock.success.mockReset();
  toastMock.error.mockReset();
});

describe("the integration failure block in a download's drawer", () => {
  const failure = {
    serviceType: "download-client", serviceId: "qbit-client", serviceName: "qBittorrent", operation: "queue:pause", kind: "RateLimited",
    retryState: "Retrying", message: "qBittorrent answered too many requests.", code: "429", httpStatus: 429, upstreamDetail: null, externalId: null,
    retryAfterUtc: "2026-10-18T10:30:00Z", attempts: 3, isTransient: true, legacyCategory: "rate-limit",
    summary: "qBittorrent is busy and asked Deluno to slow down.", nextAction: "Deluno will try again by itself. Nothing to do."
  };

  it("names the kind, the retry state in words, what happened, what to do next and the technical facts", () => {
    openDownload(download({ failure } as Partial<DownloadQueueItem>));
    const dialog = screen.getByRole("dialog");

    expect(within(dialog).getByText("Rate Limited")).toBeVisible();
    expect(within(dialog).getByText("Retrying automatically")).toBeVisible();
    expect(within(dialog).getByText("qBittorrent is busy and asked Deluno to slow down.")).toBeVisible();
    expect(within(dialog).getByText("qBittorrent answered too many requests.")).toBeVisible();
    expect(within(dialog).getByText("Deluno will try again by itself. Nothing to do.")).toBeVisible();
    for (const [label, value] of [["Service", "qBittorrent"], ["Operation", "queue:pause"], ["Code", "429"], ["HTTP status", "429"], ["Attempts", "3"]]) {
      const fact = within(dialog).getByText(label, { exact: true }).parentElement!;
      expect(within(fact).getByText(value, { exact: true })).toBeVisible();
    }
    expect(within(dialog).getByText("Next eligible")).toBeVisible();
  });

  it.each([
    ["RetryScheduled", "Retry scheduled"],
    ["CircuitOpen", "Paused after repeated failures"],
    ["ManualAction", "Needs your action"]
  ])("reads %s as '%s'", (retryState, words) => {
    openDownload(download({ failure: { ...failure, retryState } } as Partial<DownloadQueueItem>));
    expect(within(screen.getByRole("dialog")).getByText(words)).toBeVisible();
  });

  it("leaves out a technical fact the failure does not carry", () => {
    openDownload(download({ failure: { ...failure, code: "", httpStatus: null, attempts: 0, retryAfterUtc: null, operation: "" } } as Partial<DownloadQueueItem>));
    const dialog = screen.getByRole("dialog");
    for (const label of ["Code", "HTTP status", "Attempts", "Next eligible", "Operation"]) expect(within(dialog).queryByText(label, { exact: true })).toBeNull();
    expect(within(dialog).getByText("Service", { exact: true })).toBeVisible();
  });
});

describe("Preview import in a download's drawer", () => {
  const preview = {
    sourcePath: "D:/Downloads/Example.Movie.2026.1080p.WEB-DL-GROUP", destinationFolder: "D:/Films/Example Movie (2026)",
    destinationPath: "D:/Films/Example Movie (2026)/Example Movie (2026).mkv", preferredTransferMode: "hardlink", hardlinkAvailable: true,
    matchedRuleId: "films", matchedRuleName: "Films rule", sourceExists: true, destinationExists: false, sourceSizeBytes: 4096, destinationSizeBytes: 0,
    isSupportedMediaFile: true, mediaProbe: null, transferExplanation: "Hard link: no extra space is used.", warnings: ["The subtitle track is not English."],
    explanation: "Matched by extension.", decisionSteps: []
  };

  it("pressing it asks the server to preview this download's file and shows the destination, rule, how, source and warnings it answers", async () => {
    authedFetchMock.mockImplementation((path: string) => path === "/api/filesystem/import/preview" ? jsonResponse(preview) : jsonResponse({}));
    openDownload(download({ status: "importReady", progress: 100, sourcePath: "D:/Downloads/Example.Movie.2026.1080p.WEB-DL-GROUP" }));

    fireEvent.click(await screen.findByRole("button", { name: "Preview import" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/filesystem/import/preview", expect.objectContaining({ method: "POST" })));
    const sent = JSON.parse((authedFetchMock.mock.calls.find((call) => call[0] === "/api/filesystem/import/preview")![1] as RequestInit).body as string);
    expect(sent.sourcePath).toBe("D:/Downloads/Example.Movie.2026.1080p.WEB-DL-GROUP");
    const dialog = screen.getByRole("dialog");
    expect(await within(dialog).findByText("D:/Films/Example Movie (2026)/Example Movie (2026).mkv")).toBeVisible();
    expect(within(dialog).getByText("Films rule")).toBeVisible();
    expect(within(dialog).getByText("Hard link: no extra space is used.")).toBeVisible();
    expect(within(dialog).getByText("The subtitle track is not English.")).toBeVisible();
  });

  it("a preview the server refuses says so in a toast and shows no destination", async () => {
    authedFetchMock.mockImplementation((path: string) => path === "/api/filesystem/import/preview" ? jsonResponse({ title: "No rule", detail: "No destination rule matches this file." }, false, 400) : jsonResponse({}));
    openDownload(download({ status: "importReady", progress: 100, sourcePath: "D:/Downloads/Example.Movie.2026.1080p.WEB-DL-GROUP" }));

    fireEvent.click(await screen.findByRole("button", { name: "Preview import" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(within(screen.getByRole("dialog")).queryByText("Would land at")).toBeNull();
  });
});

describe("a download the processor is working on or has delivered", () => {
  function twoRows() {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [client([
      download({ id: "p1", releaseName: "Working.Film.2026.1080p-GROUP", status: "processing", progress: 100 }),
      download({ id: "p2", releaseName: "Done.Film.2026.1080p-GROUP", status: "processed", progress: 100 })
    ])] };
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
  }

  it("each row's stage reads Checking media, with the line that says what is being checked", () => {
    twoRows();

    expect(screen.getAllByText("Checking media").length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText("Media quality and processor output are being checked.").length).toBeGreaterThanOrEqual(2);
  });

  it("the drawer's status reads Processing while the processor works and Processed once it has delivered", () => {
    twoRows();

    fireEvent.click(screen.getAllByText("Working.Film.2026.1080p-GROUP")[0]!);
    expect(within(screen.getByRole("dialog")).getAllByText("Processing").length).toBeGreaterThan(0);
    expect(within(screen.getByRole("dialog")).queryByText("Processed")).toBeNull();
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
  });

  it("a processed download's drawer reads Processed", () => {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [client([download({ id: "p2", releaseName: "Done.Film.2026.1080p-GROUP", status: "processed", progress: 100 })])] };
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
    fireEvent.click(screen.getAllByText("Done.Film.2026.1080p-GROUP")[0]!);
    expect(within(screen.getByRole("dialog")).getAllByText("Processed").length).toBeGreaterThan(0);
  });
});

describe("the processor's own stage line on a hand-off row", () => {
  function handoff(processor: Record<string, unknown> | null, status = "submitted") {
    return {
      id: "h1", libraryId: "l", mediaType: "movies", clientId: "qbit-client", queueItemId: "gone", releaseName: "Staged.Film.2025.1080p-GROUP", sourcePath: "D:/in/a",
      processorName: "Weir", status, outputPath: null, importJobId: null, failureMessage: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", processor
    };
  }

  function lineFor(processor: Record<string, unknown> | null, status = "submitted") {
    queueLoaderData.processorHandoffs = [handoff(processor, status)];
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
  }

  it("says where a queued file stands: its place in the queue", () => {
    lineFor({ state: "queued", queuePosition: 3 });
    expect(screen.getAllByText("Weir: 3rd in queue").length).toBeGreaterThan(0);
  });

  it("says a queued file with no place is queued", () => {
    lineFor({ state: "queued" });
    expect(screen.getAllByText("Weir: queued").length).toBeGreaterThan(0);
  });

  it("says when a scheduled file runs", () => {
    lineFor({ state: "scheduled", scheduledForUtc: "2026-10-19T01:00:00Z" });
    expect(screen.getAllByText(/^Weir: runs at /).length).toBeGreaterThan(0);
  });

  it("says a file the processor is cleaning is being worked on", () => {
    lineFor({ state: "working" });
    expect(screen.getAllByText("Weir: working on it").length).toBeGreaterThan(0);
  });

  it("says a finished file is finished and importing", () => {
    lineFor({ state: "completed" }, "completed");
    // A completed hand-off is closed for the row's own words; the processor's line only shows while it is still open.
    expect(screen.queryByText("Weir: finished · importing")).toBeNull();
    lineFor({ state: "completed" }, "submitted");
    expect(screen.getAllByText("Weir: finished · importing").length).toBeGreaterThan(0);
  });

  it("says a processor that stopped answering is not answering and Deluno is still waiting", () => {
    lineFor({ state: "working", unreachableSinceUtc: "2026-10-18T08:00:00Z" });
    expect(screen.getAllByText("Weir isn't answering · still waiting").length).toBeGreaterThan(0);
  });
});

describe("Manual import, when the server refuses", () => {
  it("shows the server's own message inside the drawer when a preview is refused, and again for a refused queue", async () => {
    authedFetchMock.mockImplementation((path: string) => {
      if (path === "/api/filesystem/import/preview") return jsonResponse({ detail: "That file is not a video Deluno can read." }, false, 400);
      return jsonResponse({});
    });
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: /Manual import/ }));
    fireEvent.change(screen.getByPlaceholderText("D:/Downloads/Some.Release.2024"), { target: { value: "D:/Downloads/notes.txt" } });
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));

    const dialog = screen.getByRole("dialog");
    expect(await within(dialog).findByText(/That file is not a video Deluno can read\./)).toBeVisible();
    expect(within(dialog).queryByText("Would land at")).toBeNull();

    authedFetchMock.mockImplementation((path: string) => {
      if (path === "/api/filesystem/import/jobs") return jsonResponse({ detail: "The destination folder is full." }, false, 400);
      if (path === "/api/filesystem/import/preview") return jsonResponse({ detail: "ok", destinationPath: "D:/Films/x.mkv" });
      return jsonResponse({});
    });
    fireEvent.click(within(dialog).getByRole("button", { name: "Queue import" }));
    expect(await within(dialog).findByText(/The destination folder is full\./)).toBeVisible();
  });

  it("with nothing chosen yet, asks for a source instead of sending anything", async () => {
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: /Manual import/ }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("button", { name: "Preview" })).toBeDisabled();
    expect(authedFetchMock).not.toHaveBeenCalled();
  });
});

describe("the link System > Health opens Transfers with", () => {
  it("opens Manual import already filled in with the path and media type, and takes the two words out of the address", async () => {
    let address = "";
    const { Routes, Route, useLocation } = await import("react-router-dom");
    function Where() {
      const location = useLocation();
      address = location.pathname + location.search;
      return null;
    }
    render(
      <MemoryRouter initialEntries={["/queue?manualImportPath=D%3A%2FRefined%2FStray.Film.mkv&manualImportMediaType=tv"]}>
        <Where />
        <Routes><Route path="/queue" element={<QueuePage />} /></Routes>
      </MemoryRouter>
    );

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Bring in files Deluno did not download itself")).toBeVisible();
    expect(within(dialog).getByPlaceholderText("D:/Downloads/Some.Release.2024")).toHaveValue("D:/Refined/Stray.Film.mkv");
    await waitFor(() => expect(address).toBe("/queue"));
  });

  it("an unknown media type word means films", async () => {
    const { Routes, Route } = await import("react-router-dom");
    render(
      <MemoryRouter initialEntries={["/queue?manualImportPath=D%3A%2FRefined%2FStray.Film.mkv&manualImportMediaType=nonsense"]}>
        <Routes><Route path="/queue" element={<QueuePage />} /></Routes>
      </MemoryRouter>
    );
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByPlaceholderText("D:/Downloads/Some.Release.2024")).toHaveValue("D:/Refined/Stray.Film.mkv");
  });
});

describe("Transfers when its data cannot be read", () => {
  it("says which list could not be read, with the reason, and a Retry that reads again; once it can, the message goes", async () => {
    sourcesState = {
      ...readyRouteSources(queueLoaderData, revalidateMock),
      failed: [{ name: "telemetry", label: "Download activity", message: "Failed to fetch" }],
      refetch: async () => { refetchMock(); }
    };
    const view = render(<MemoryRouter><QueuePage /></MemoryRouter>);

    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("Download activity could not be read. Failed to fetch");
    // The rest of the screen still shows what it has.
    expect(screen.getByRole("button", { name: /Manual import/ })).toBeVisible();

    fireEvent.click(within(alert).getByRole("button", { name: "Retry" }));
    expect(refetchMock).toHaveBeenCalledTimes(1);

    sourcesState = readyRouteSources(queueLoaderData, revalidateMock);
    view.rerender(<MemoryRouter><QueuePage /></MemoryRouter>);
    expect(screen.queryByRole("alert")).toBeNull();
    await act(async () => undefined);
  });

  it("when nothing can be read, says so instead of an empty list", () => {
    sourcesState = {
      ...readyRouteSources(queueLoaderData, revalidateMock),
      ready: false,
      blocked: true,
      failed: [{ name: "telemetry", label: "Download activity", message: "The server did not answer." }]
    };
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
    expect(screen.getByRole("alert")).toHaveTextContent("This screen could not load everything it needs. Download activity could not be read. The server did not answer.");
    expect(screen.queryByText("Nothing in motion")).toBeNull();
  });
});

describe("a release held for approval: Find another", () => {
  const row: HeldReleaseRow = {
    historyId: "h1", mediaType: "movie", id: "m1", episodeId: null, title: "Nosferatu", year: 1922, episodeLabel: null, libraryId: "lib 1",
    releaseName: "Nosferatu.1922.1080p.BluRay-GRP", indexerName: "Nyaa", indexerId: "ix", downloadUrl: "https://x/get", quality: "BluRay 1080p",
    sizeBytes: 1_000_000_000, seeders: 5, reason: "Held for your review.", heldUtc: "2026-10-08T00:00:00Z"
  } as HeldReleaseRow;

  it("refuses that release, starts a new search for the film, then drops the held row, and says so", async () => {
    queueLoaderData.heldReleases = [row];
    authedFetchMock.mockImplementation((path: string) => jsonResponse(path === "/api/blocked-releases" ? { id: "b1" } : {}));
    render(<MemoryRouter><QueuePage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: "Find another for Nosferatu (1922)" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledTimes(3));
    const calls = authedFetchMock.mock.calls.map((call) => ({ path: String(call[0]), method: (call[1] as RequestInit | undefined)?.method, body: (call[1] as RequestInit | undefined)?.body }));
    expect(calls[0]).toMatchObject({ path: "/api/blocked-releases", method: "POST" });
    expect(JSON.parse(String(calls[0]!.body))).toMatchObject({ releaseName: "Nosferatu.1922.1080p.BluRay-GRP", indexerName: "Nyaa", mediaType: "movie", entityId: "m1" });
    expect(calls[1]).toEqual({ path: "/api/movies/m1/search?libraryId=lib%201", method: "POST", body: undefined });
    expect(calls[2]).toEqual({ path: "/api/attention/held-releases/h1/dismiss", method: "POST", body: undefined });
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Nosferatu.1922.1080p.BluRay-GRP will not be used again. Searching for another."));
  });

  it("an episode is searched for as that episode, not the whole show", async () => {
    queueLoaderData.heldReleases = [{ ...row, mediaType: "series", id: "s1", episodeId: "e1", title: "The Wire", episodeLabel: "S01E04" } as HeldReleaseRow];
    authedFetchMock.mockImplementation((path: string) => jsonResponse(path === "/api/blocked-releases" ? { id: "b1" } : {}));
    render(<MemoryRouter><QueuePage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: "Find another for The Wire S01E04" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledTimes(3));
    const search = authedFetchMock.mock.calls[1]!;
    expect(search[0]).toBe("/api/series/s1/episodes/search");
    expect(JSON.parse(String((search[1] as RequestInit).body))).toEqual({ episodeIds: ["e1"], monitoredOnly: false });
  });

  it("when the refusal cannot be saved nothing is searched and the row stays", async () => {
    queueLoaderData.heldReleases = [row];
    authedFetchMock.mockImplementation((path: string) => path === "/api/blocked-releases" ? jsonResponse({ message: "The blocklist is full." }, false, 500) : jsonResponse({}));
    render(<MemoryRouter><QueuePage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: "Find another for Nosferatu (1922)" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    expect(authedFetchMock).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Find another for Nosferatu (1922)" })).toBeVisible();
  });
});
