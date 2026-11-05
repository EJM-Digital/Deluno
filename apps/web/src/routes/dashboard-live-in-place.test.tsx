import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, renderHook, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The Dashboard updates in place (#1508, #1512): after its first paint no push, refetch or reconnect replaces a panel,
 * blanks the page or shows a loading state, and a title that arrives by a live update shows its poster.
 * The page, the cache and the Live tab are real; only the network and the SignalR hub are stood in for.
 */
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
    useSignalREvents: () => undefined,
    useSignalRResync: (handler: () => void) => useListen("resync", handler),
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => useListen(name, handler)
  };
});
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}), text: async () => "" }) }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
// jsdom measures nothing: the shelf and the board get a box.
vi.mock("./dashboard/live/hooks", async (importOriginal) => {
  const { createRef } = await import("react");
  const original = await importOriginal<typeof import("./dashboard/live/hooks")>();
  return { ...original, useElementSize: () => [createRef(), { width: 1200, height: 700 }] };
});

const world = vi.hoisted(() => ({
  paths: [] as string[],
  summaryDelay: null as Promise<void> | null,
  summaryFails: false,
  settingsDelay: null as Promise<void> | null,
  movieTitles: {} as Record<string, unknown>,
  arrivals: [] as unknown[]
}));

vi.mock("../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/api")>();
  const read = async (path: string) => {
    world.paths.push(path);
    if (path === "/api/dashboard/summary") {
      if (world.summaryDelay) await world.summaryDelay;
      if (world.summaryFails) throw new Error("Deluno did not answer");
      return (await import("./dashboard/summary-fixtures")).fullSummary();
    }
    if (path === "/api/settings") {
      if (world.settingsDelay) await world.settingsDelay;
      return original.emptyPlatformSettingsSnapshot;
    }
    if (path === "/api/settings/never") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") return { lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" };
    if (path.startsWith("/api/movies/titles")) return world.movieTitles;
    if (path.startsWith("/api/dashboard/live-extras")) {
      return { arrivals: world.arrivals, importsToday: world.arrivals.length, protected: { refusedToday: 0 }, requests: [], retentionReclaimBytes: 0, turnedDown: null, cleanedUpToday: 0, connections: [] };
    }
    if (path.endsWith("/wanted")) return { totalWanted: 0, missingCount: 0, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: [] };
    if (path.startsWith("/api/download-clients/throughput")) return { hours: 6, samples: [] };
    return [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

const { DashboardPage } = await import("./dashboard-page");
const { queryClient } = await import("../lib/route-cache");

function send(name: string, payload: unknown = {}) {
  act(() => { for (const handler of [...(handlers.get(name) ?? [])]) handler(payload); });
}

function renderLive() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={["/"]}>
        <DashboardPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const wait = (ms: number) => act(async () => { await new Promise((resolve) => setTimeout(resolve, ms)); });

/** Every panel the page is made of: the tab panel, the Live grid and each panel in it. */
function panels(): HTMLElement[] {
  const grid = document.querySelector<HTMLElement>("[data-live-grid]")!;
  return [document.getElementById("dashboard-tabpanel")!, grid, ...(Array.from(grid.children) as HTMLElement[])];
}

/** Anything that says "loading": a skeleton, a busy region, a progress bar. */
function loadingMarks() {
  return document.querySelectorAll('[data-skeleton], [aria-busy="true"], [role="progressbar"]').length;
}

async function paintedOnce() {
  renderLive();
  await waitFor(() => expect(document.querySelector("[data-live-grid]")).not.toBeNull());
  await waitFor(() => expect(world.paths.some((path) => path.startsWith("/api/dashboard/live-extras"))).toBe(true));
  // Let every source answer once: the first paint is over.
  await wait(100);
}

beforeEach(() => {
  queryClient.clear();
  handlers.clear();
  world.paths = [];
  world.summaryDelay = null;
  world.summaryFails = false;
  world.settingsDelay = null;
  world.movieTitles = {};
  world.arrivals = [];
});

describe("the Dashboard after its first paint", () => {
  const updates: Array<[string, () => void]> = [
    ["a settings change", () => send("SettingsChanged")],
    ["a health change", () => send("HealthChanged")],
    ["an indexer change", () => send("IndexerChanged")],
    ["a library change", () => send("MovieChanged")],
    ["a reconnect that has to read everything again", () => send("resync")]
  ];

  it.each(updates)("keeps every panel's element through %s and shows no loading state", async (_name, trigger) => {
    await paintedOnce();
    const before = panels();
    expect(before.length).toBeGreaterThan(5);
    expect(loadingMarks()).toBe(0);
    let sawLoading = 0;
    const watch = new MutationObserver(() => { sawLoading = Math.max(sawLoading, loadingMarks()); });
    watch.observe(document.body, { childList: true, subtree: true, attributes: true });
    // A slow answer: the old numbers stay on screen the whole time it is on its way.
    let release!: () => void;
    world.summaryDelay = new Promise<void>((resolve) => { release = resolve; });
    trigger();
    await wait(1300);
    expect(document.contains(before[0])).toBe(true);
    expect(loadingMarks()).toBe(0);
    release();
    await wait(50);
    watch.disconnect();
    const after = panels();
    expect(after).toHaveLength(before.length);
    after.forEach((element, index) => expect(element).toBe(before[index]));
    expect(sawLoading).toBe(0);
  });

  it("keeps the page when the summary cannot be read again", async () => {
    await paintedOnce();
    const before = panels();
    world.summaryFails = true;
    send("resync");
    await wait(100);
    expect(screen.queryByText("The Dashboard could not load")).toBeNull();
    panels().forEach((element, index) => expect(element).toBe(before[index]));
  });
});

describe("a title that arrives by a live update (#1512)", () => {
  const imported = { dispatchId: "d1", stage: "import_succeeded", at: new Date().toISOString(), entityType: "movie", entityId: "m1", releaseName: "Gladiator.II.2024.1080p.BluRay-GRP", libraryId: "lib1" };

  it("shows its poster when the library already has it", async () => {
    await paintedOnce();
    world.movieTitles = { m1: { title: "Gladiator II", year: 2024, posterUrl: "/api/movies/m1/poster" } };
    send("JourneyStageChanged", imported);
    await waitFor(() => expect(document.querySelector("[data-arrival-poster] img")?.getAttribute("src")).toBe("/api/movies/m1/poster"));
  });

  it("shows its poster once the library has it, though it had none at the import", async () => {
    await paintedOnce();
    world.movieTitles = { m1: { title: "Gladiator II", year: 2024, posterUrl: null } };
    send("JourneyStageChanged", imported);
    await waitFor(() => expect(document.querySelector("[data-arrival-poster]")).not.toBeNull());
    await wait(100);
    expect(document.querySelector("[data-arrival-poster] img")).toBeNull();
    // The library fetches the artwork a moment later and says the movie changed.
    world.movieTitles = { m1: { title: "Gladiator II", year: 2024, posterUrl: "/api/movies/m1/poster" } };
    send("MovieChanged", { id: "m1" });
    await waitFor(() => expect(document.querySelector("[data-arrival-poster] img")?.getAttribute("src")).toBe("/api/movies/m1/poster"), { timeout: 3000 });
  });

  it("asks the library for every title on the shelf, not only the first 48 by id", async () => {
    await paintedOnce();
    const many = Array.from({ length: 60 }, (_, index) => ({ entityType: "movie", entityId: `m${String(index).padStart(3, "0")}`, release: `Film.${index}.2024.1080p-GRP`, quality: null, previousQuality: null, upgraded: false, sizeBytes: null, importedUtc: new Date(Date.now() - index * 1000).toISOString() }));
    world.arrivals = many;
    await act(async () => { await queryClient.invalidateQueries({ queryKey: ["src", "dashboard-live-extras"] }); });
    await waitFor(() => expect(world.paths.some((path) => path.startsWith("/api/movies/titles"))).toBe(true));
    const asked = new Set(world.paths.filter((path) => path.startsWith("/api/movies/titles")).flatMap((path) => decodeURIComponent(path.split("ids=")[1]).split(",")));
    expect(asked.has("m039")).toBe(true);
  });
});

describe("the arrival tile of a title that came by a live update (#1508)", () => {
  it("is the same element when the stored row replaces the pushed one", async () => {
    await paintedOnce();
    world.movieTitles = { m1: { title: "Gladiator II", year: 2024, posterUrl: "/api/movies/m1/poster" } };
    send("JourneyStageChanged", { dispatchId: "d1", stage: "import_succeeded", at: "2026-11-06T10:00:00.000Z", entityType: "movie", entityId: "m1", releaseName: "Gladiator.II.2024.1080p.BluRay-GRP", libraryId: "lib1" });
    await waitFor(() => expect(document.querySelector("[data-arrival-poster] img")).not.toBeNull());
    const tile = document.querySelector("[data-arrival-poster]");
    const picture = document.querySelector("[data-arrival-poster] img");
    // The server's own row for the same import: its time is the import record's, not the event's, and it carries the quality.
    world.arrivals = [{ entityType: "movie", entityId: "m1", release: "Gladiator.II.2024.1080p.BluRay-GRP", quality: "Bluray-1080p", previousQuality: null, upgraded: false, sizeBytes: 9e9, importedUtc: "2026-11-06T10:00:02.431Z", libraryId: "lib1", libraryName: "Movies" }];
    send("HealthChanged");
    await waitFor(() => expect(document.body.textContent).toContain("8.4 GB"));
    expect(document.querySelectorAll("[data-arrival-poster]")).toHaveLength(1);
    expect(document.querySelector("[data-arrival-poster]")).toBe(tile);
    expect(document.querySelector("[data-arrival-poster] img")).toBe(picture);
  });
});

describe("the setup checklist before its lists have answered (#1508)", () => {
  it("is not ready, so nothing is drawn from the empty stand-ins and taken away again", async () => {
    const { useSetupState } = await import("./dashboard/sources");
    let release!: () => void;
    world.settingsDelay = new Promise<void>((resolve) => { release = resolve; });
    const wrapper = ({ children }: { children: React.ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    const { result } = renderHook(() => useSetupState(), { wrapper });
    await wait(100);
    expect(result.current.ready).toBe(false);
    release();
    await waitFor(() => expect(result.current.ready).toBe(true));
  });
});
