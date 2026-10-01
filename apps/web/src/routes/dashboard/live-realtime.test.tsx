import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { fullSummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

/**
 * #1097: Live is fully live. Each test sends one pushed event the way the server
 * does and checks the board changed, and that no list was asked for again.
 */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../lib/use-signalr", async (importOriginal) => {
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
    ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => useListen(name, handler),
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => useListen(name, handler)
  };
});
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve({ ok: true, json: async () => ({}) })) }));

const lists: Record<string, unknown> = {};
const asked = vi.hoisted(() => ({ paths: [] as string[] }));
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    asked.paths.push(path);
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") return { lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" };
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    return key ? lists[key] : [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

function send(name: string, payload: unknown) {
  act(() => {
    for (const handler of handlers.get(name) ?? []) handler(payload);
  });
}

const at = () => new Date().toISOString();

function seed() {
  lists["/api/download-clients/telemetry"] = {
    capturedUtc: "2026-09-30T10:00:00Z",
    summary: {},
    clients: [{
      clientId: "c1", clientName: "Deluge",
      queue: [{ id: "q1", clientId: "c1", clientName: "Deluge", title: "Metropolis (1927)", releaseName: "Metropolis.1927.1080p", status: "waitingForProcessor", progress: 100, speedMbps: 0, etaSeconds: 0, errorMessage: null, indexerName: "NZBgeek" }]
    }]
  };
  lists["/api/integrations/processors/handoffs"] = [{ id: "h1", queueItemId: "q1", releaseName: "Metropolis.1927.1080p", status: "waiting", failureMessage: null, processorName: "Cleaner", createdUtc: at(), updatedUtc: at() }];
  lists["/api/movies/import-recovery"] = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] };
  lists["/api/series/import-recovery"] = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] };
}

function renderTab() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <LiveTab summary={fullSummary} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  queryClient.clear();
  handlers.clear();
  asked.paths.length = 0;
  for (const key of Object.keys(lists)) delete lists[key];
});

describe("Live applies each pushed change with no fetch after it", () => {
  it("moves a title into the Needs you strip the moment its hand-off fails, and back onto the journey when the processor takes it up again", async () => {
    seed();
    renderTab();
    const board = screen.getByRole("region", { name: /^every title/i });
    const strip = screen.getByRole("region", { name: "Needs you" });
    expect(await within(board).findByText("Metropolis (1927)")).toBeInTheDocument();
    expect(within(strip).getByText("Nothing needs you.")).toBeInTheDocument();
    const before = asked.paths.length;

    send("ProcessorHandoffChanged", { handoffId: "h1", queueItemId: "q1", libraryId: "l1", releaseName: "Metropolis.1927.1080p", state: "failed", processorName: "Cleaner", message: "The processor timed out.", at: at() });
    expect(await within(strip).findByText("1 hand-off refused")).toBeInTheDocument();
    expect(within(within(board).getByRole("group", { name: "With the processor" })).queryByText("Metropolis (1927)")).toBeNull();

    send("ProcessorHandoffChanged", { handoffId: "h1", queueItemId: "q1", libraryId: "l1", releaseName: "Metropolis.1927.1080p", state: "working", processorName: "Cleaner", message: null, at: at() });
    await waitFor(() => expect(within(strip).queryByText("1 hand-off refused")).toBeNull());
    expect(within(await within(board).findByRole("group", { name: "With the processor" })).getByText("Metropolis (1927)")).toBeInTheDocument();
    expect(asked.paths.length).toBe(before);
  });

  it("adds an import case to the Needs you strip as it opens and takes it away as it closes", async () => {
    seed();
    renderTab();
    const board = screen.getByRole("region", { name: /^every title/i });
    const strip = screen.getByRole("region", { name: "Needs you" });
    await within(board).findByText("Metropolis (1927)");
    const before = asked.paths.length;

    send("RecoveryCaseChanged", { caseId: "r9", state: "opened", kind: "importFailed", title: "Primer.2004.1080p", entityType: "movie", entityId: null, at: at() });
    expect(await within(strip).findByText("1 import to review")).toBeInTheDocument();

    send("RecoveryCaseChanged", { caseId: "r9", state: "closed", kind: null, title: null, entityType: "movie", entityId: null, at: at() });
    await waitFor(() => expect(within(strip).queryByText("1 import to review")).toBeNull());
    expect(asked.paths.length).toBe(before);
  });

  it("says how far a search is as it checks each title, and drops it when it finishes", async () => {
    seed();
    renderTab();
    const band = screen.getByRole("region", { name: "Doing now" });
    const board = screen.getByRole("region", { name: /^every title/i });
    await within(board).findByText("Metropolis (1927)");
    const before = asked.paths.length;

    send("SearchCycleProgress", { libraryId: "l9", libraryName: "Movies", mediaType: "movies", state: "running", checked: 12, planned: 40, at: at() });
    expect(await within(band).findByText("Searching Movies: 12 of 40 checked")).toBeInTheDocument();
    // A library search is said in Right now, not as a title on the journey.
    expect(within(board).queryByRole("group", { name: "Searching" })).toBeNull();

    send("SearchCycleProgress", { libraryId: "l9", libraryName: "Movies", mediaType: "movies", state: "finished", checked: 40, planned: 40, at: at() });
    await waitFor(() => expect(within(band).queryByText(/Searching Movies/)).toBeNull());
    expect(within(board).queryByRole("group", { name: "Searching" })).toBeNull();
    expect(asked.paths.length).toBe(before);
  });

  it("names a job as it starts and stops naming it as it ends", async () => {
    seed();
    renderTab();
    const band = screen.getByRole("region", { name: "Doing now" });
    send("JobChanged", { jobId: "j1", kind: "library.read", state: "started", title: "Reading your library files (TV)", libraryId: null, message: null, at: at() });
    expect(await within(band).findByText("Reading your library files (TV)")).toBeInTheDocument();
    send("JobChanged", { jobId: "j1", kind: "library.read", state: "finished", title: "Reading your library files (TV)", libraryId: null, message: null, at: at() });
    await waitFor(() => expect(within(band).queryByText("Reading your library files (TV)")).toBeNull());
  });
});
