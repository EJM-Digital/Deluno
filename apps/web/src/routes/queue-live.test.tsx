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

describe("patching Transfers from events", () => {
  it("reads the server's time left", () => {
    expect(etaSecondsOf("00:09:00")).toBe(540);
    expect(etaSecondsOf(null)).toBe(0);
  });

  it("moves a download's progress, speed and time left, and the totals above it", () => {
    const client = new QueryClient();
    client.setQueryData(sharedSources.telemetry.key, overview());
    expect(applyDownloadProgress(client, { id: "q1", title: "x", progress: 88, speedMbps: 5.5, eta: "00:02:00", status: "downloading" })).toBe(true);
    const next = client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key)!;
    expect(next.clients[0].queue[0]).toMatchObject({ progress: 88, speedMbps: 5.5, etaSeconds: 120 });
    expect(next.summary.totalSpeedMbps).toBe(5.5);
    expect(applyDownloadProgress(client, { id: "nope", title: "x", progress: 1, speedMbps: 1, eta: null, status: "downloading" })).toBe(false);
  });

  it("marks a client unreachable and back", () => {
    const client = new QueryClient();
    client.setQueryData(sharedSources.telemetry.key, overview());
    expect(applyClientStateToTelemetry(client, { clientId: "c1", clientName: "Deluge", reachable: false, paused: null, message: "refused", at: "x" })).toBe(true);
    expect(client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key)!.clients[0].healthStatus).toBe("unreachable");
    expect(applyClientStateToTelemetry(client, { clientId: "c1", clientName: "Deluge", reachable: true, paused: null, message: null, at: "x" })).toBe(true);
    expect(client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key)!.clients[0].healthStatus).toBe("healthy");
  });
});

describe("Queue import shows its outcome as the click returns (#1209)", () => {
  const job = (over: Record<string, unknown> = {}) => ({ id: "j1", jobType: "filesystem.import.execute", status: "queued", attempts: 0, maxAttempts: 3, nextAttemptUtc: null, lastError: null, ...over }) as never;

  it("reads Import queued, Imported or Import failed from the job the click returned", () => {
    expect(importOutcomeStatus(job())).toBe("importQueued");
    expect(importOutcomeStatus(job({ status: "running" }))).toBe("importQueued");
    expect(importOutcomeStatus(job({ status: "completed" }))).toBe("imported");
    expect(importOutcomeStatus(job({ status: "dead-letter", attempts: 3 }))).toBe("importFailed");
    // A failed attempt with another coming is still on its way, not a failure.
    expect(importOutcomeStatus(job({ status: "failed", attempts: 1, nextAttemptUtc: "2026-10-10T10:00:00Z" }))).toBe("importQueued");
  });

  it("moves the row off Ready to import and adds the job, with no wait for the next read", () => {
    const client = new QueryClient();
    client.setQueryData(sharedSources.telemetry.key, { ...overview(), clients: [{ ...overview().clients[0], queue: [item({ status: "importReady" })] }] });
    client.setQueryData(TRANSFERS_JOBS_KEY, []);
    applyImportOutcome(client, "q1", job());
    expect(client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key)!.clients[0].queue[0].status).toBe("importQueued");
    expect(client.getQueryData<unknown[]>(TRANSFERS_JOBS_KEY)).toHaveLength(1);
    applyImportOutcome(client, "q1", job({ status: "completed" }));
    expect(client.getQueryData<DownloadTelemetryOverview>(sharedSources.telemetry.key)!.clients[0].queue[0].status).toBe("imported");
    expect(client.getQueryData<unknown[]>(TRANSFERS_JOBS_KEY)).toHaveLength(1);
  });
});

describe("the Transfers screen is live", () => {
  it("changes a bar, a speed and a client count from pushes, and has no Refresh button", async () => {
    lists["/api/download-clients/telemetry"] = overview();
    lists["/api/settings"] = emptyPlatformSettingsSnapshot;
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter><QueuePage /></MemoryRouter>
      </QueryClientProvider>
    );
    await screen.findAllByText(/Photographer/);
    expect(screen.queryByRole("button", { name: /^Refresh$/ })).toBeNull();
    expect((await screen.findAllByText(/^61%/)).length).toBeGreaterThan(0);

    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 88, speedMbps: 5.5, eta: null, status: "downloading" });
    await waitFor(() => expect(screen.getAllByText(/^88%/).length).toBeGreaterThan(0));
    expect(document.body.textContent).toContain("5.5 MB/s");

    send("ClientStateChanged", { clientId: "c1", clientName: "Deluge", reachable: false, paused: null, message: "refused", at: new Date().toISOString() });
    await waitFor(() => expect(document.body.textContent).toContain("0/1"));
  });
});

describe("Transfers after a reconnect that replays what it missed", () => {
  it("applies the replayed events in the order the server sent them, so the last word wins, and shows each change once", async () => {
    lists["/api/download-clients/telemetry"] = overview();
    lists["/api/settings"] = emptyPlatformSettingsSnapshot;
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter><QueuePage /></MemoryRouter>
      </QueryClientProvider>
    );
    expect((await screen.findAllByText(/^61%/)).length).toBeGreaterThan(0);

    // The connection dropped; on return the hub replays what was missed, oldest first, through the same listeners a live push uses.
    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 70, speedMbps: 4.1, eta: null, status: "downloading" });
    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 85, speedMbps: 2.2, eta: null, status: "downloading" });
    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 97, speedMbps: 0.4, eta: null, status: "downloading" });

    await waitFor(() => expect(screen.getAllByText(/^97%/).length).toBeGreaterThan(0));
    // Nothing older is left on screen from the replay, and the number is not drawn twice for one download.
    expect(screen.queryByText(/^70%/)).toBeNull();
    expect(screen.queryByText(/^85%/)).toBeNull();
    expect(document.body.textContent).toContain("0.4 MB/s");

    // A replayed removal ends it, whatever came before.
    send("QueueItemRemoved", { id: "q1" });
    await waitFor(() => expect(screen.queryByText(/^97%/)).toBeNull());
  });
});
