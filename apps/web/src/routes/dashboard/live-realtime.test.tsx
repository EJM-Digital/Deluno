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

function liveExtras() {
  return {
    asOfUtc: new Date().toISOString(), sinceUtc: "", arrivals: [], importsToday: 0, importedBytesToday: 0, upgrades: [], requests: [], retentionReclaimBytes: 0,
    protected: { refusedToday: 0, unsafeToday: 0, latest: [] },
    turnedDown: {
      total: 43,
      reasons: [{ group: "language", label: "Not in a language you want", count: 14 }, { group: "notBetter", label: "No better than what you have", count: 11 }, { group: "tooBig", label: "Bigger than your size limit", count: 9 }, { group: "fewSeeders", label: "Too few people sharing it", count: 5 }, { group: "fake", label: "Fake or mislabelled", count: 4 }, { group: "other", label: "Other reasons", count: 0 }],
      latest: []
    },
    connections: [
      { kind: "processor", id: "p1", name: "Weir", enabled: true, healthy: true, countToday: 6, busy: 0 },
      { kind: "mediaServer", id: "ms1", name: "Plex", enabled: true, healthy: true, countToday: 5, busy: 0 }
    ]
  };
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
  it("keeps a title with the processor when its hand-off fails, saying so, and says it is working again when the processor takes it up", async () => {
    seed();
    renderTab();
    const board = screen.getByRole("region", { name: /^pipeline/i });
    const titles = () => board.querySelector<HTMLElement>('[data-glide-stage="processor"]')!;
    expect(await within(board).findByText("Metropolis (1927)")).toBeInTheDocument();
    expect(titles().getAttribute("aria-label")).toContain("Metropolis (1927): Cleaner is working on it");
    const before = asked.paths.length;

    send("ProcessorHandoffChanged", { handoffId: "h1", queueItemId: "q1", libraryId: "l1", releaseName: "Metropolis.1927.1080p", state: "failed", processorName: "Cleaner", message: "The processor timed out.", at: at() });
    await waitFor(() => expect(titles().getAttribute("aria-label")).toContain("Metropolis (1927): Processor failed"));
    // A problem is for the bell: nothing on Live asks to be settled.
    expect(screen.queryByRole("region", { name: "Needs you" })).toBeNull();
    expect(within(board).queryByRole("button")).toBeNull();

    send("ProcessorHandoffChanged", { handoffId: "h1", queueItemId: "q1", libraryId: "l1", releaseName: "Metropolis.1927.1080p", state: "working", processorName: "Cleaner", message: null, at: at() });
    await waitFor(() => expect(titles().getAttribute("aria-label")).toContain("Metropolis (1927): Cleaner is working on it"));
    expect(asked.paths.length).toBe(before);
  });

  it("says how far a search is as it checks each title, with a bar, and drops it when it finishes", async () => {
    seed();
    renderTab();
    const band = screen.getByRole("region", { name: "Working on now" });
    const board = screen.getByRole("region", { name: /^pipeline/i });
    await within(board).findByText("Metropolis (1927)");
    const before = asked.paths.length;

    send("SearchCycleProgress", { libraryId: "l9", libraryName: "Movies", mediaType: "movies", state: "running", checked: 12, planned: 40, at: at() });
    expect(await within(band).findByText(/Searching Movies/)).toBeInTheDocument();
    expect(within(band).getByText("12 of 40")).toBeInTheDocument();
    expect(band.querySelector<HTMLElement>(".lb-bar i")?.style.width).toBe("30%");
    // A library search is said in Working on now, not as a title on the journey.
    expect(within(board).queryByText(/Searching Movies/)).toBeNull();

    send("SearchCycleProgress", { libraryId: "l9", libraryName: "Movies", mediaType: "movies", state: "finished", checked: 40, planned: 40, at: at() });
    await waitFor(() => expect(within(band).queryByText(/Searching Movies/)).toBeNull());
    expect(asked.paths.length).toBe(before);
  });

  it("names a job as it starts and stops naming it as it ends", async () => {
    seed();
    renderTab();
    const band = screen.getByRole("region", { name: "Working on now" });
    send("JobChanged", { jobId: "j1", kind: "library.read", state: "started", title: "Reading your library files (TV)", libraryId: null, message: null, at: at() });
    expect(await within(band).findByText("Reading your library files (TV)")).toBeInTheDocument();
    send("JobChanged", { jobId: "j1", kind: "library.read", state: "finished", title: "Reading your library files (TV)", libraryId: null, message: null, at: at() });
    await waitFor(() => expect(within(band).queryByText("Reading your library files (TV)")).toBeNull());
  });

  it("moves a bar and its count the moment a search turns releases down, and lights it", async () => {
    seed();
    lists["/api/dashboard/live-extras"] = liveExtras();
    renderTab();
    const card = await screen.findByRole("region", { name: "Turned down today" });
    await within(card).findByText("43 releases");
    const language = () => card.querySelector<HTMLElement>('[data-reason="language"]')!;
    expect(within(language()).getByText("14")).toBeInTheDocument();

    send("ReleasesTurnedDown", { title: "Dune", entityType: "movie", entityId: "m1", atUtc: new Date(Date.now() + 5_000).toISOString(), items: [{ release: "Dune.2021.iTA.1080p", group: "language", label: "Not in a language you want" }, { release: "Dune.2021.720p", group: "notBetter", label: "No better than what you have" }] });
    expect(await within(card).findByText("45 releases")).toBeInTheDocument();
    expect(within(language()).getByText("15")).toBeInTheDocument();
    expect(language()).toHaveAttribute("data-hit");
    // An older release the stored counts already hold is not counted twice.
    const fake = () => card.querySelector<HTMLElement>('[data-reason="fake"]')!;
    const before = fake().textContent;
    send("ReleasesTurnedDown", { atUtc: "2000-01-01T00:00:00Z", items: [{ release: "Old.1999", group: "fake", label: "Fake or mislabelled" }] });
    expect(fake().textContent).toBe(before);
    expect(within(card).queryByText("Old.1999")).toBeNull();
  });

  it("moves a connection's count on the Sources card when Deluno uses it, and lights the tile", async () => {
    seed();
    lists["/api/dashboard/live-extras"] = liveExtras();
    renderTab();
    const group = await screen.findByRole("region", { name: "Sources" });
    expect(await within(group).findByRole("link", { name: "Plex: Media server · 5 sent today, 5 today" })).toBeInTheDocument();

    send("ConnectionUsed", { kind: "mediaServer", id: "ms1", name: "Plex", countToday: 6, busy: 0, atUtc: new Date(Date.now() + 5_000).toISOString() });
    const tile = await within(group).findByRole("link", { name: "Plex: Media server · 6 sent today, 6 today" });
    await waitFor(() => expect(tile).toHaveClass("lit"));
    // A processor with a title glows while it has it.
    send("ConnectionUsed", { kind: "processor", id: "p1", name: "Weir", countToday: 7, busy: 1, atUtc: new Date(Date.now() + 6_000).toISOString() });
    expect(await within(group).findByRole("link", { name: "Weir: Processor · working on 1, 7 today" })).toBeInTheDocument();
  });
});
