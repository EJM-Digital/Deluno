import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, JobQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage } from "./queue-screen";

/** In progress shows what is happening now, and folds the finished downloads still in a client away from it (#1448). */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

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
  processorConnections: [] as unknown[],
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

function item(release: string, overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id: release, clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies", title: release, releaseName: release,
    category: "movies", status: "downloading", progress: 40, speedMbps: 0, etaSeconds: 0, sizeBytes: 1_000_000_000, downloadedBytes: 400_000_000, peers: 3,
    indexerName: "Fixture", errorMessage: null, addedUtc: new Date().toISOString(), sourcePath: null, libraryId: null, healthFindings: null,
    ...overrides
  };
}

function showQueue(items: DownloadQueueItem[]) {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: items, history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
    }]
  };
  render(<MemoryRouter initialEntries={["/queue"]}><QueuePage /></MemoryRouter>);
}

const held = (n: number): HeldReleaseRow => ({
  historyId: `h${n}`, mediaType: "movie", id: `m${n}`, episodeId: null, title: `Held ${n}`, year: 2026, episodeLabel: null, libraryId: "l1", releaseName: `Held.${n}.2026.1080p-GRP`,
  indexerName: "Fixture", indexerId: "i1", downloadUrl: "http://x", quality: "1080p", sizeBytes: 1, seeders: 3, reason: "Needs your yes", heldUtc: "2026-10-31T00:00:00Z"
});

const seeding = Array.from({ length: 41 }, (_, n) => item(`Seeding.${n}.2026.1080p-GRP`, { status: "imported", progress: 100, downloadedBytes: 1_000_000_000 }));

beforeEach(() => {
  queueLoaderData.heldReleases = [];
  queueLoaderData.settings = { ...emptyPlatformSettingsSnapshot, sharingMode: "share-then-tidy", sharingForHours: 48, sharingUntilRatio: 2, sharingCompletionAction: "reclaim" };
});
afterEach(() => {
  authedFetchMock.mockReset();
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
  queueLoaderData.heldReleases = [];
});

describe("In progress: what is happening now (#1448)", () => {
  it("41 imported downloads still seeding are not in flight: the header counts the one that is downloading", () => {
    showQueue([item("Moving.Now.2026.1080p-GRP"), ...seeding]);

    expect(screen.getByText("1 happening now · 0 importing")).toBeVisible();
    expect(screen.queryByText(/in flight/)).toBeNull();
    // Only the moving one is a row above the fold.
    expect(screen.getAllByRole("row").filter((row) => row.textContent?.includes("Seeding."))).toHaveLength(0);
  });

  it("the finished ones are folded in their own group with their count, and open to say what Deluno will do", () => {
    showQueue([item("Moving.Now.2026.1080p-GRP"), ...seeding]);

    expect(screen.getByText("Finished, still in the client (41)")).toBeVisible();
    expect(screen.queryByText("Seeding.0.2026.1080p-GRP")).toBeNull();
    const toggle = screen.getByRole("button", { name: "Show finished downloads" });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    fireEvent.click(toggle);
    expect(screen.getByRole("button", { name: "Hide finished downloads" })).toHaveAttribute("aria-expanded", "true");
    const row = screen.getAllByText("Seeding.0.2026.1080p-GRP")[0].closest("[role=row]") as HTMLElement;
    expect(row.textContent).toContain("Seeding until 2 days or a ratio of 2, then Deluno removes it from the client.");
    expect(within(row).getByText("In library")).toBeVisible();
  });

  it("the seeding rule is the one in Settings: leave-alone says Deluno leaves it", () => {
    queueLoaderData.settings = { ...queueLoaderData.settings, sharingMode: "leave-alone" };
    showQueue(seeding.slice(0, 2));
    fireEvent.click(screen.getByRole("button", { name: "Show finished downloads" }));

    expect(screen.getAllByText("Deluno leaves it in the client. You decide when to remove it.")).toHaveLength(2);
  });

  it("an imported download whose client entry is gone is in no group: nothing is listed", () => {
    showQueue([]);

    expect(screen.queryByText(/Finished, still in the client/)).toBeNull();
    expect(screen.getByText("Nothing in motion")).toBeVisible();
  });

  it("files gone is finished business: folded away, saying the files are gone", () => {
    showQueue([item("Oppenheimer.2023.2160p-GRP", { status: "filesGone", progress: 100 })]);

    expect(screen.getByText("Finished, still in the client (1)")).toBeVisible();
    expect(screen.getByText("Everything Deluno sent has finished. What is still in a client is listed below.")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Show finished downloads" }));
    expect(screen.getByText(/The files are gone\. Search for another copy/)).toBeVisible();
  });

  it("downloading, waiting in the client, importing and with the processor stay on top, and the header equals its rows", () => {
    showQueue([
      item("Down.2026.1080p-GRP"),
      item("Wait.2026.1080p-GRP", { status: "queued", progress: 0 }),
      item("Proc.2026.1080p-GRP", { status: "processing", progress: 100 }),
      item("Imp.2026.1080p-GRP", { status: "importQueued", progress: 100 }),
      ...seeding.slice(0, 3)
    ]);

    expect(screen.getByText("4 happening now · 0 importing")).toBeVisible();
    const rows = screen.getAllByRole("row").filter((row) => /(Down|Wait|Proc|Imp)\.2026/.test(row.textContent ?? ""));
    expect(rows).toHaveLength(4);
    expect(screen.getByText("Finished, still in the client (3)")).toBeVisible();
  });

  it("releases waiting for a yes are Needs action, never in flight", () => {
    queueLoaderData.heldReleases = [held(1), held(2)];
    showQueue([]);

    const tile = screen.getByText("Needs action").closest("div") as HTMLElement;
    expect(tile.parentElement?.textContent).toContain("2");
    expect(screen.queryByText(/happening now/)).toBeNull();
  });
});
