import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { queryClient } from "../lib/route-cache";
import { IndexersPage, indexersSources } from "./connections-screen";

// #1329: the Find & Download tabs on the data they are handed: the indexer and download-client lists, the empty states of the
// path mappings, routing, proxies and scoreboard, and what the screen does when the server pushes a change.

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const fetchJson = vi.hoisted(() => vi.fn(async (..._args: any[]): Promise<unknown> => []));
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", () => ({ authedFetch: async () => ({ ok: true, status: 200, json: async () => [], text: async () => "[]" }) }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() }, Toaster: () => null }));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const sources: any = {};
const pending = { on: false };
const pushHandlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
const refetchSources = vi.hoisted(() => vi.fn(async (..._keys: unknown[]) => undefined));

vi.mock("../lib/use-signalr", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/use-signalr")>();
  const register = (event: string, handler: (payload: unknown) => void) => {
    const set = pushHandlers.get(event) ?? new Set();
    set.add(handler);
    pushHandlers.set(event, set);
  };
  return { ...original, useSignalREvent: (event: string, _subjects: unknown, handler: (payload: unknown) => void) => register(event, handler), useLiveEvent: register, useLiveWatching: () => undefined };
});

vi.mock("../lib/live-refresh", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useCoalescedSourceRefresh: () => (...keys: unknown[]) => { (refetchSources as (...args: unknown[]) => unknown)(...keys); }
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => (pending.on ? { ...readyRouteSources(sources, vi.fn()), ready: false } : readyRouteSources(sources, vi.fn())),
    refetchSources
  };
});

const indexer = (over: Record<string, unknown> = {}) => ({
  id: "i1", name: "Alpha", protocol: "torznab", privacy: "public", baseUrl: "https://alpha.example/api", priority: 1, categories: "", tags: "", isEnabled: true,
  mediaScope: "both", healthStatus: "healthy", lastHealthMessage: null, consecutiveFailures: 0, rateLimitedUntilUtc: null, disabledReason: null,
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", lastHealthTestUtc: null, ...over
});
const client = (over: Record<string, unknown> = {}) => ({
  id: "c1", name: "My qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8080, endpointUrl: null, useSsl: false, addPaused: false, moviesCategory: "movies", tvCategory: "tv",
  categoryTemplate: null, priority: 1, isEnabled: true, healthStatus: "healthy", lastHealthMessage: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", lastHealthTestUtc: null, ...over
});

function reset(over: Record<string, unknown> = {}) {
  for (const key of Object.keys(sources)) delete sources[key];
  Object.assign(sources, {
    indexers: [], clients: [], libraries: [], routing: [], pathMappings: [], proxies: [], settings: emptyPlatformSettingsSnapshot, telemetry: null,
    outboundThrottle: { hosts: [] }, scoreboard: { indexers: [], generatedUtc: "2026-10-29T00:00:00Z" }, qbittorrentBlocklistSync: {}, ...over
  });
}

function show(path: string) {
  const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: [path] });
  const view = render(<RouterProvider router={router} />);
  let version = 0;
  return { ...view, again: () => view.rerender(<RouterProvider key={++version} router={router} />) };
}

const rowNames = () => screen.getAllByRole("row").filter((row) => row.querySelector(":scope > [role=cell]")).map((row) => within(row).getAllByRole("cell")[0].textContent ?? "");

afterEach(() => { pending.on = false; cleanup(); pushHandlers.clear(); refetchSources.mockClear(); });

describe("the Indexers tab, low rows (#1329)", () => {
  it("an empty list says No indexers yet and offers a New indexer button", () => {
    reset();
    show("/indexers/indexers");

    expect(screen.getByText("No indexers yet")).toBeTruthy();
    expect(screen.getByText("0 indexers")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /New indexer/ }).length).toBeGreaterThan(0);
  });

  it("the count line names why the number ready is short", () => {
    reset({ indexers: [
      indexer({ id: "a", name: "A" }), indexer({ id: "b", name: "B", isEnabled: false }), indexer({ id: "c", name: "C", isEnabled: false }), indexer({ id: "d", name: "D", healthStatus: "degraded" })
    ] });
    show("/indexers/indexers");

    expect(screen.getByText("1 of 4 ready to search")).toBeTruthy();
    expect(screen.getByText("Not counted: 1 degraded, 2 paused")).toBeTruthy();
  });

  it("filtering to nothing says No indexers match these filters with the hint, and Clear filters brings the list back", () => {
    reset({ indexers: [indexer({ id: "a", name: "Alpha", protocol: "torznab", privacy: "public" }), indexer({ id: "b", name: "Bravo", protocol: "newznab", privacy: "private" })] });
    show("/indexers/indexers");
    expect(rowNames()).toHaveLength(2);

    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    fireEvent.click(within(screen.getByRole("radiogroup", { name: "Filter by protocol" })).getByRole("radio", { name: "Torznab" }));
    fireEvent.click(within(screen.getByRole("radiogroup", { name: "Filter by privacy" })).getByRole("radio", { name: "private" }));

    expect(screen.getByText("No indexers match these filters")).toBeTruthy();
    expect(screen.getByText("Clear a filter to see the rest of your indexers.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(rowNames()).toHaveLength(2);
    expect(screen.queryByText("No indexers match these filters")).toBeNull();
  });

  it("sorts by Protocol, Used for and Pacing, each ascending then reversed", () => {
    reset({
      indexers: [
        indexer({ id: "a", name: "Alpha", protocol: "torznab", mediaScope: "tv", baseUrl: "https://alpha.example/api" }),
        indexer({ id: "b", name: "Bravo", protocol: "newznab", mediaScope: "movies", baseUrl: "https://bravo.example/api" }),
        indexer({ id: "c", name: "Charlie", protocol: "rss", mediaScope: "both", baseUrl: "https://charlie.example/api" })
      ],
      outboundThrottle: { hosts: [{ host: "bravo.example", waiting: 3, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 2 }, { host: "charlie.example", waiting: 1, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 1 }] }
    });
    show("/indexers/indexers");
    const names = () => rowNames().map((text) => /Alpha|Bravo|Charlie/.exec(text)![0]);
    const press = (label: string) => fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));

    press("Protocol");
    const byProtocol = names();
    press("Protocol");
    expect(names()).toEqual([...byProtocol].reverse());
    expect(byProtocol).toEqual(["Bravo", "Charlie", "Alpha"]);

    press("Pacing");
    expect(names()).toEqual(["Alpha", "Charlie", "Bravo"]);
    press("Pacing");
    expect(names()).toEqual(["Bravo", "Charlie", "Alpha"]);

    press("Used for");
    const byScope = names();
    press("Used for");
    expect(names()).toEqual([...byScope].reverse());
    expect(new Set(byScope).size).toBe(3);
  });

  it("an edit or removal made elsewhere shows in the open list, and each change event reads the lists it moves again", () => {
    reset({ indexers: [indexer({ id: "a", name: "Alpha" })] });
    const view = show("/indexers/indexers");
    expect(screen.getByText("Alpha")).toBeTruthy();

    const fires = (event: string) => {
      expect(pushHandlers.get(event)?.size, event).toBeGreaterThan(0);
      refetchSources.mockClear();
      act(() => { for (const handler of pushHandlers.get(event)!) handler({}); });
      return [...new Set(refetchSources.mock.calls.flat())];
    };
    expect(fires("IndexerChanged")).toEqual([indexersSources.indexers, indexersSources.scoreboard, indexersSources.routing]);
    expect(fires("HealthChanged")).toEqual([indexersSources.indexers, indexersSources.clients]);
    expect(fires("ConnectionChecked")).toEqual([indexersSources.indexers, indexersSources.clients]);

    reset({ indexers: [indexer({ id: "a", name: "Alpha renamed" })] });
    view.again();
    expect(screen.getByText("Alpha renamed")).toBeTruthy();
    expect(screen.queryByText("Alpha")).toBeNull();

    reset({ indexers: [] });
    view.again();
    expect(screen.getByText("No indexers yet")).toBeTruthy();
  });
});

describe("the Download Clients tab, low rows (#1329)", () => {
  it("the heading counts the clients, and with none it says 0 clients and names the clients it can connect", () => {
    reset();
    show("/indexers/download-clients");
    expect(screen.getByText("0 clients")).toBeTruthy();
    expect(screen.getByText("No download client yet")).toBeTruthy();
    const hint = screen.getByText(/^Connect qBittorrent, SABnzbd, NZBGet, Deluge, Transmission or uTorrent/);
    expect(hint).toBeTruthy();
    cleanup();

    reset({ clients: [client({ id: "a", name: "One" })] });
    show("/indexers/download-clients");
    expect(screen.getByText("1 client")).toBeTruthy();
    cleanup();

    reset({ clients: [client({ id: "a", name: "One" }), client({ id: "b", name: "Two" })] });
    show("/indexers/download-clients");
    expect(screen.getByText("2 clients")).toBeTruthy();
  });

  it("the Now cell shows how many are active with the speed and the ready to import count, or when it was last tested, or Not tested", () => {
    reset({
      clients: [client({ id: "a", name: "Busy" }), client({ id: "b", name: "Tested", lastHealthTestUtc: new Date(Date.now() - 3 * 60_000).toISOString() }), client({ id: "c", name: "Never" })],
      telemetry: { clients: [{ clientId: "a", clientPaused: false, summary: { activeCount: 2, totalSpeedMbps: 12.34, importReadyCount: 1 } }] }
    });
    show("/indexers/download-clients");

    const cellOf = (name: string) => within(screen.getAllByRole("row").find((row) => row.textContent?.includes(name))!).getAllByRole("cell")[3].textContent ?? "";
    expect(cellOf("Busy")).toContain("2 active");
    expect(cellOf("Busy")).toContain("12.3 MB/s · 1 ready to import");
    expect(cellOf("Tested")).toMatch(/Tested .*ago/);
    expect(cellOf("Never")).toContain("Not tested");
  });

  it("a switched-off client with nothing running still shows when it was last tested, even though the server reports it as idle", () => {
    const idle = { activeCount: 0, totalSpeedMbps: 0, importReadyCount: 0 };
    reset({
      clients: [client({ id: "a", name: "Off tested", isEnabled: false, lastHealthTestUtc: new Date(Date.now() - 3 * 60_000).toISOString() }), client({ id: "b", name: "Off never", isEnabled: false })],
      telemetry: { clients: [{ clientId: "a", clientPaused: false, summary: idle }, { clientId: "b", clientPaused: false, summary: idle }] }
    });
    show("/indexers/download-clients");
    const cellOf = (name: string) => within(screen.getAllByRole("row").find((row) => row.textContent?.includes(name))!).getAllByRole("cell")[3].textContent ?? "";
    expect(cellOf("Off tested")).toMatch(/Tested .*ago/);
    expect(cellOf("Off never")).toContain("Not tested");
  });

  it("a pushed download-progress reading moves that download's figures and its client's speed in the held telemetry, without asking the server", () => {
    const summary = { activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 6, totalUploadSpeedMbps: 0 };
    queryClient.setQueryData(indexersSources.telemetry.key, { capturedUtc: "2026-10-29T00:00:00Z", summary, clients: [{ clientId: "a", clientPaused: false, summary, queue: [{ id: "one", progress: 0.1, speedMbps: 6 }] }] });
    reset({ clients: [client({ id: "a", name: "Busy" })] });
    show("/indexers/download-clients");
    fetchJson.mockClear();

    expect(pushHandlers.get("DownloadProgress")?.size).toBeGreaterThan(0);
    act(() => { for (const handler of pushHandlers.get("DownloadProgress")!) handler({ id: "one", progress: 0.5, speedMbps: 9 }); });

    const held = queryClient.getQueryData(indexersSources.telemetry.key) as { summary: { totalSpeedMbps: number }; clients: { summary: { totalSpeedMbps: number }; queue: { progress: number; speedMbps: number }[] }[] };
    expect(held.clients[0].queue[0]).toMatchObject({ progress: 0.5, speedMbps: 9 });
    expect(held.clients[0].summary.totalSpeedMbps).toBe(9);
    expect(held.summary.totalSpeedMbps).toBe(9);
    expect(fetchJson).not.toHaveBeenCalled();
  });
});

describe("the empty tabs, low rows (#1329)", () => {
  it("Remote Path Mappings says No download client yet with no client, and No remote path mappings yet with a client and no mapping", () => {
    reset();
    show("/indexers/path-mappings");
    expect(screen.getByText("No download client yet")).toBeTruthy();
    expect(screen.getByText(/Add a download client first, then a remote path mapping can be filed under its host\./)).toBeTruthy();
    cleanup();

    reset({ clients: [client()] });
    show("/indexers/path-mappings");
    expect(screen.getByText("No remote path mappings yet")).toBeTruthy();
    expect(screen.getByText(/Add one below if a client's reported path does not match what Deluno sees\./)).toBeTruthy();
  });

  it("Library Routing says 0 libraries and No libraries yet, or counts the libraries with a row each", () => {
    reset();
    show("/indexers/library-routing");
    expect(screen.getByText("0 libraries")).toBeTruthy();
    expect(screen.getByText("No libraries yet")).toBeTruthy();
    cleanup();

    reset({ libraries: [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\\Films" }], routing: [{ libraryId: "l1", libraryName: "Films", sources: [], downloadClients: [] }] });
    show("/indexers/library-routing");
    expect(screen.getByText("1 library")).toBeTruthy();
    expect(screen.getByText("Films")).toBeTruthy();
  });

  it("Proxies explains what a proxy is for and that FlareSolverr here is for one indexer, and offers New proxy", () => {
    reset();
    show("/indexers/proxies");

    expect(screen.getByText("No proxies yet")).toBeTruthy();
    expect(screen.getByText("0 proxies")).toBeTruthy();
    expect(screen.getByText(/FlareSolverr/)).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /New proxy/ }).length).toBeGreaterThan(0);
  });

  it("the Scoreboard says there is no indexer activity yet when nothing has been recorded", () => {
    reset();
    show("/indexers/scoreboard");

    expect(screen.getByText(/No indexer activity/)).toBeTruthy();
  });
});

describe("the routing list and the loading and failing sources, low rows (#1329)", () => {
  it("a library row names the library with Movies or TV shows under it, and a row that Needs a fix has a Fix button that opens its routing once", async () => {
    const routed = (id: string, name: string) => ({ libraryId: id, libraryName: name, sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1 }], downloadClients: [{ downloadClientId: "c1", downloadClientName: "My qBittorrent", priority: 1 }] });
    reset({
      libraries: [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\\Films" }, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "D:\\Shows" }],
      indexers: [indexer()], clients: [client()], routing: [routed("l1", "Films"), routed("l2", "Shows")]
    });
    fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/folder-chain")
      ? (String(url).includes("l1") ? { clients: [{ clientName: "My qBittorrent", links: [{ status: "problem", message: "The movies category is missing." }] }], allMatch: false, steps: [] } : { clients: [{ clientName: "My qBittorrent", links: [{ status: "ok", message: "Matches." }] }], allMatch: true, steps: [] })
      : []));
    show("/indexers/library-routing");

    const row = (name: string) => screen.getAllByRole("row").find((candidate) => candidate.textContent?.includes(name))!;
    expect(row("Films").textContent).toContain("Movies");
    expect(row("Shows").textContent).toContain("TV shows");
    const fix = await screen.findByRole("button", { name: "Fix Films routing" });
    expect(row("Films").textContent).toContain("Needs a fix");
    expect(screen.queryByRole("button", { name: "Fix Shows routing" })).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(fix);

    expect(await screen.findAllByRole("dialog")).toHaveLength(1);
    fetchJson.mockImplementation(async () => []);
  });

  it("library, indexer and download-client changes each read again the lists the routing list is made from", () => {
    reset({ libraries: [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\\Films" }], routing: [{ libraryId: "l1", libraryName: "Films", sources: [], downloadClients: [] }] });
    show("/indexers/library-routing");
    const fires = (event: string) => {
      expect(pushHandlers.get(event)?.size, event).toBeGreaterThan(0);
      refetchSources.mockClear();
      act(() => { for (const handler of pushHandlers.get(event)!) handler({}); });
      return [...new Set(refetchSources.mock.calls.flat())];
    };

    expect(fires("LibraryChanged")).toEqual([indexersSources.libraries, indexersSources.routing]);
    expect(fires("IndexerChanged")).toContain(indexersSources.routing);
    expect(fires("DownloadClientChanged")).toEqual([indexersSources.clients, indexersSources.pathMappings, indexersSources.routing, indexersSources.telemetry]);
  });

  it("while a tab's required sources are still arriving it shows the loading shape, not the tab", () => {
    reset({ indexers: [indexer()] });
    pending.on = true;
    const view = show("/indexers/indexers");

    expect(view.container.querySelector("[data-route-skeleton]")).not.toBeNull();
    expect(screen.queryByText("Alpha")).toBeNull();
  });

  it("the soft sources fall back to empty when the server cannot answer, so the page still renders", async () => {
    fetchJson.mockRejectedValue(new Error("down"));

    try {
      expect(await indexersSources.telemetry.fetch()).toBeNull();
      expect(await indexersSources.outboundThrottle.fetch()).toEqual({ hosts: [] });
      expect(await indexersSources.proxies.fetch()).toEqual([]);
      expect(await indexersSources.pathMappings.fetch()).toEqual([]);
      expect(await indexersSources.qbittorrentBlocklistSync.fetch()).toEqual({});
      const scoreboard = await indexersSources.scoreboard.fetch();
      expect(scoreboard.indexers).toEqual([]);
    } finally {
      fetchJson.mockReset();
      fetchJson.mockImplementation(async () => []);
    }
    reset();
    show("/indexers/indexers");
    expect(screen.getByText("No indexers yet")).toBeTruthy();
  });
});
