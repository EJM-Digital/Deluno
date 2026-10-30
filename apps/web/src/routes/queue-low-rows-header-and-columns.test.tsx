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

beforeEach(() => { window.localStorage.clear(); });

describe("download drawer: header", () => {
  it("the drawer title is the title and the description reads '<client> - <status>'", () => {
    openDrawerFor(queueItem({ title: "Example Movie" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("heading", { name: "Example Movie" })).toBeTruthy();
    expect(within(dialog).getByText("qBittorrent · Downloading")).toBeTruthy();
  });

  it("with no title the drawer title is the release name", () => {
    openDrawerFor(queueItem({ title: "" }));
    expect(within(screen.getByRole("dialog")).getByRole("heading", { name: "Example.Movie.2026.1080p.WEB-DL-GROUP" })).toBeTruthy();
  });

  it.each([0, 1])("every way of closing the drawer (button %i of the Close pair) closes it", async (index) => {
    openDrawerFor(queueItem());
    const closers = within(screen.getByRole("dialog")).getAllByRole("button", { name: "Close" });
    expect(closers.length).toBe(2);
    fireEvent.click(closers[index]);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("the Progress section shows the bar and the Done fact as percent, downloaded and size", () => {
    openDrawerFor(queueItem({ progress: 25, downloadedBytes: 1_073_741_824, sizeBytes: 4_294_967_296 }));
    const dialog = screen.getByRole("dialog");
    const done = within(dialog).getByText("Done").parentElement as HTMLElement;
    expect(done.textContent).toContain("25% — 1.0 GB of 4.0 GB");
    expect((dialog.querySelector('span[aria-hidden] > span[style]') as HTMLElement).style.width).toBe("25%");
  });
});

describe("media pipeline table columns", () => {
  function showTwo() {
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: 2, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 1.5, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
        queue: [
          queueItem({ id: "a", title: "Alpha", releaseName: "Alpha.2026", progress: 80, speedMbps: 1, sizeBytes: 3_000_000_000, etaSeconds: 100 }),
          queueItem({ id: "b", title: "Bravo", releaseName: "Bravo.2026", progress: 10, speedMbps: 9, sizeBytes: 1_000_000_000, etaSeconds: 900 })
        ],
        history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    render(<MemoryRouter><QueuePage /></MemoryRouter>);
  }
  const headers = () => screen.getAllByRole("columnheader").map((h) => h.textContent);
  const order = () => screen.getAllByText(/^(Alpha|Bravo)$/).map((n) => n.textContent);

  it("the column picker toggles each optional column off and back on, and remembers the choice under queue-screen.columns", () => {
    showTwo();
    expect(headers()).toEqual(expect.arrayContaining(["Quality", "Custom format", "Language", "Size", "Timeleft"]));
    fireEvent.click(screen.getByRole("button", { name: "Show columns" }));
    const menu = screen.getByRole("menu");
    for (const [label, header] of [["Quality", "Quality"], ["Custom format score", "Custom format"], ["Language", "Language"], ["Size", "Size"], ["Timeleft", "Timeleft"]]) {
      fireEvent.click(within(menu).getByRole("menuitemcheckbox", { name: label }));
      expect(headers()).not.toContain(header);
    }
    expect(JSON.parse(window.localStorage.getItem("queue-screen.columns") ?? "null")).toEqual([]);
    fireEvent.click(within(menu).getByRole("menuitemcheckbox", { name: "Size" }));
    expect(headers()).toContain("Size");
    expect(JSON.parse(window.localStorage.getItem("queue-screen.columns") ?? "null")).toEqual(["size"]);
  });

  it("a remembered column choice is read back on the next visit", () => {
    window.localStorage.setItem("queue-screen.columns", JSON.stringify(["language"]));
    showTwo();
    expect(headers()).toContain("Language");
    expect(headers()).not.toContain("Quality");
    expect(headers()).not.toContain("Size");
  });

  it.each([
    ["Media", ["Alpha", "Bravo"], ["Bravo", "Alpha"]],
    ["Progress", ["Bravo", "Alpha"], ["Alpha", "Bravo"]],
    ["Speed / left", ["Alpha", "Bravo"], ["Bravo", "Alpha"]],
    ["Size", ["Bravo", "Alpha"], ["Alpha", "Bravo"]],
    ["Timeleft", ["Alpha", "Bravo"], ["Bravo", "Alpha"]]
  ])("clicking the %s header sorts ascending then descending", (label, asc, desc) => {
    showTwo();
    const header = () => screen.getAllByRole("columnheader").find((h) => h.textContent === label) as HTMLElement;
    fireEvent.click(within(header()).getByRole("button"));
    expect(header().getAttribute("aria-sort")).toBe("ascending");
    expect(order()).toEqual(asc);
    fireEvent.click(within(header()).getByRole("button"));
    expect(header().getAttribute("aria-sort")).toBe("descending");
    expect(order()).toEqual(desc);
  });

  it.each(["Stage", "Client", "Quality", "Custom format", "Language", "Status"])("the %s header is a sort button", (label) => {
    showTwo();
    const header = screen.getAllByRole("columnheader").find((h) => h.textContent === label) as HTMLElement;
    fireEvent.click(within(header).getByRole("button"));
    expect(header.getAttribute("aria-sort")).toBe("ascending");
  });
});
