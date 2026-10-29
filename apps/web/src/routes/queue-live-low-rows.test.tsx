import { QueryClientProvider, QueryClient } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadTelemetryOverview } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { queryClient } from "../lib/route-cache";
import { sharedSources } from "../lib/shared-sources";
import { applyClientStateToTelemetry, applyDownloadProgress, applyImportOutcome, etaSecondsOf, importOutcomeStatus, TRANSFERS_JOBS_KEY } from "./queue-live";
import { QueuePage } from "./queue-screen";

/** #1179: Transfers follows what the server pushes, with no reload and no Refresh button. */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  const useListen = (name: string, handler: (payload: unknown) => void) => {
    const latest = useRef(handler);
    useEffect(() => { latest.current = handler; });
    useEffect(() => {
      const call = (payload: unknown) => latest.current(payload);
      const set = handlers.get(name) ?? new Set();
      set.add(call);
      handlers.set(name, set);
      return () => { set.delete(call); };
    }, [name]);
  };
  return {
    ...(await importOriginal<typeof import("../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => useListen(name, handler),
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => useListen(name, handler),
    useLiveWatching: () => {}
  };
});
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve({ ok: true, json: async () => ({}) })) }));

const lists: Record<string, unknown> = {};
vi.mock("../lib/api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/api/client")>();
  const read = async (path: string) => {
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    return key ? lists[key] : [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});
vi.mock("../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/api")>();
  const read = async (path: string) => {
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    return key ? lists[key] : [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

function send(name: string, payload: unknown) {
  act(() => { for (const handler of handlers.get(name) ?? []) handler(payload); });
}

const item = (over: Record<string, unknown> = {}) => ({
  id: "q1", clientId: "c1", clientName: "Deluge", protocol: "torrent", mediaType: "movie", title: "Photographer (2026)",
  releaseName: "Photographer.2026.1080p", category: "movies", status: "downloading", progress: 61, speedMbps: 1.8, etaSeconds: 540,
  sizeBytes: 4e9, downloadedBytes: 1e9, peers: 3, indexerName: "Indexer", errorMessage: null, addedUtc: "2026-09-30T10:00:00Z",
  sourcePath: null, libraryId: "l1", healthFindings: null, ...over
});

function overview(): DownloadTelemetryOverview {
  return {
    capturedUtc: "2026-09-30T10:00:00Z",
    summary: { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 1.8, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [{ clientId: "c1", clientName: "Deluge", protocol: "torrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null, capabilities: {} as never, summary: {} as never, queue: [item()] as never, history: [], capturedUtc: "2026-09-30T10:00:00Z", historyTruncated: false }]
  };
}

beforeEach(() => {
  queryClient.clear();
  handlers.clear();
  for (const key of Object.keys(lists)) delete lists[key];
});

const toastInfo = vi.hoisted(() => vi.fn());
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return { ...original, realtimeToast: { ...original.realtimeToast, info: (...args: unknown[]) => toastInfo(...args) } };
});

beforeEach(() => {
  toastInfo.mockReset();
  lists["/api/download-clients/telemetry"] = overview();
  lists["/api/settings"] = emptyPlatformSettingsSnapshot;
});

function showTransfers() {
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter><QueuePage /></MemoryRouter>
    </QueryClientProvider>
  );
}

const when = () => new Date().toISOString();

describe("Transfers live pushes (low rows)", () => {
  it("holds a burst of progress readings for 250 ms and applies each download's last reading once", async () => {
    showTransfers();
    expect((await screen.findAllByText(/^61%/)).length).toBeGreaterThan(0);

    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 70, speedMbps: 4.1, eta: null, status: "downloading" });
    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 85, speedMbps: 2.2, eta: null, status: "downloading" });
    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 97, speedMbps: 0.4, eta: null, status: "downloading" });

    // Held: the screen still shows the old reading right after the burst.
    expect(screen.getAllByText(/^61%/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/^97%/)).toBeNull();
    await waitFor(() => expect(screen.getAllByText(/^97%/).length).toBeGreaterThan(0));
    expect(screen.queryByText(/^70%/)).toBeNull();
    expect(screen.queryByText(/^85%/)).toBeNull();
  });

  it("DispatchGrabAttempt raises an info toast naming the release and the client", async () => {
    showTransfers();
    await screen.findAllByText(/Photographer/);

    send("DispatchGrabAttempt", { releaseName: "Some.Release.2026.1080p", clientName: "Deluge" });

    expect(toastInfo).toHaveBeenCalledWith('Sent "Some.Release.2026.1080p" to Deluge');
  });

  it("QueueItemStatusChanged patches the row's status and QueueItemRemoved takes the row away, with no read", async () => {
    showTransfers();
    expect((await screen.findAllByText("Downloading")).length).toBeGreaterThan(0);
    const reads = Object.keys(lists).length;

    send("QueueItemStatusChanged", { id: "q1", status: "paused", errorMessage: null });
    await waitFor(() => expect(screen.getAllByText("Paused").length).toBeGreaterThan(0));
    expect(Object.keys(lists).length).toBe(reads);

    send("QueueItemRemoved", { id: "q1" });
    await waitFor(() => expect(screen.queryByText(/Photographer\.2026/)).toBeNull());
  });

  it("RecoveryCaseChanged opens a case in Needs attention, counts it once, and closing it takes it away", async () => {
    lists["/api/movies/import-recovery"] = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] };
    showTransfers();
    await screen.findAllByText(/Photographer/);

    send("RecoveryCaseChanged", { caseId: "r1", state: "opened", kind: "importFailed", title: "Brand New Film", entityType: "movie", entityId: "m9", at: when() });
    send("RecoveryCaseChanged", { caseId: "r1", state: "opened", kind: "importFailed", title: "Brand New Film", entityType: "movie", entityId: "m9", at: when() });
    await waitFor(() => expect(screen.getAllByText(/Brand New Film/).length).toBeGreaterThan(0));
    const shownOnce = screen.getAllByText(/Brand New Film/).length;
    send("RecoveryCaseChanged", { caseId: "r1", state: "opened", kind: "importFailed", title: "Brand New Film", entityType: "movie", entityId: "m9", at: when() });
    expect(screen.getAllByText(/Brand New Film/).length).toBe(shownOnce);

    send("RecoveryCaseChanged", { caseId: "r1", state: "closed", kind: null, title: null, entityType: "movie", entityId: "m9", at: when() });
    await waitFor(() => expect(screen.queryByText(/Brand New Film/)).toBeNull());
  });

  it("ProcessorHandoffChanged moves a hand-off to the lane its new state says", async () => {
    lists["/api/integrations/processors/handoffs"] = [{
      id: "h1", libraryId: "l1", mediaType: "movies", clientId: "c1", queueItemId: "gone-1", releaseName: "Handed.Over.Release.2026", sourcePath: "/d/Handed.Over.Release.2026",
      processorName: "Weir", status: "started", outputPath: null, importJobId: null, failureMessage: null, createdUtc: when(), updatedUtc: when()
    }];
    showTransfers();
    await screen.findAllByText(/Handed\.Over\.Release/);
    expect(screen.queryByText(/The processor could not clean it up/)).toBeNull();

    send("ProcessorHandoffChanged", { handoffId: "h1", queueItemId: "gone-1", libraryId: "l1", releaseName: "Handed.Over.Release.2026", state: "failed", processorName: "Weir", message: "The processor could not clean it up", at: when() });

    await waitFor(() => expect(document.body.textContent).toContain("The processor could not clean it up"));
  });
});
