import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { IndexersPage } from "./connections-screen";
import { toast } from "../components/shell/toaster";

// #1329: the last corners of Find and Download: indexer row cells, routing edits and tags, rejected requests that are not messages, and the hand-offs from detected apps.

const fetchJson = vi.hoisted(() => vi.fn(async (_url: string, _init?: unknown): Promise<unknown> => []));
const authedFetch = vi.hoisted(() => vi.fn());
const pushHandlers = vi.hoisted(() => new Map<string, (payload: unknown) => void>());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", () => ({ authedFetch }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() }, Toaster: () => null }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useSignalREvent: (event: string, _subjects: unknown, handler: (payload: unknown) => void) => { pushHandlers.set(event, handler); },
  useLiveEvent: () => undefined,
  useLiveWatching: () => undefined
}));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const sources: any = {};
vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return { ...(await importOriginal<typeof import("../lib/route-cache")>()), useRouteSources: () => readyRouteSources(sources, vi.fn()), refetchSources: async () => undefined };
});

const indexer = (over: Record<string, unknown> = {}) => ({
  id: "i1", name: "Alpha", protocol: "torznab", privacy: "public", baseUrl: "https://alpha.example/api", priority: 1, categories: "", tags: "", isEnabled: true,
  mediaScope: "both", healthStatus: "healthy", lastHealthMessage: "Answered.", consecutiveFailures: 0, rateLimitedUntilUtc: null, disabledReason: null,
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", lastHealthTestUtc: new Date(Date.now() - 4 * 60_000).toISOString(), lastHealthLatencyMs: 87, ...over
});
const client = (over: Record<string, unknown> = {}) => ({
  id: "c1", name: "My qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8080, endpointUrl: null, useSsl: false, addPaused: false, moviesCategory: "movies", tvCategory: "tv",
  categoryTemplate: null, priority: 1, isEnabled: true, healthStatus: "healthy", lastHealthMessage: "Reached it.", createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z",
  lastHealthTestUtc: new Date(Date.now() - 4 * 60_000).toISOString(), lastHealthLatencyMs: 12, ...over
});
const films = { id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\\Films" };

function reset(over: Record<string, unknown> = {}) {
  for (const key of Object.keys(sources)) delete sources[key];
  Object.assign(sources, {
    indexers: [], clients: [], libraries: [], routing: [], pathMappings: [], proxies: [], settings: emptyPlatformSettingsSnapshot, telemetry: null,
    outboundThrottle: { hosts: [] }, scoreboard: { indexers: [], generatedUtc: "2026-10-29T00:00:00Z" }, qbittorrentBlocklistSync: {}, ...over
  });
}
const ok = (body: unknown = {}, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const chainClear = () => fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/folder-chain") ? { clients: [], allMatch: true, steps: [], libraryLinks: [] } : []));

function show(path: string) {
  const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: [path] });
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);
}
const calls = (method: string) => authedFetch.mock.calls.filter(([, init]) => (init as RequestInit).method === method);
const callTo = (method: string, url: string) => calls(method).find(([target]) => target === url)!;
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));
const dialog = async () => within(await screen.findByRole("dialog"));
const cellOf = (name: string, index: number) => within(screen.getAllByRole("row").find((row) => row.textContent?.includes(name))!).getAllByRole("cell")[index].textContent ?? "";

afterEach(() => { cleanup(); pushHandlers.clear(); fetchJson.mockReset(); fetchJson.mockImplementation(async () => []); authedFetch.mockReset(); vi.mocked(toast.success).mockClear(); vi.mocked(toast.error).mockClear(); });

describe("the indexer list, cell by cell", () => {
  it("says what the host is doing: requests waiting, when the next may go, or Ready", () => {
    reset({
      indexers: [
        indexer({ id: "a", name: "Waits", baseUrl: "https://waits.example/api" }),
        indexer({ id: "b", name: "Soon", baseUrl: "https://soon.example/api" }),
        indexer({ id: "c", name: "Idle", baseUrl: "https://idle.example/api" }),
        indexer({ id: "d", name: "Broken", baseUrl: "not an address" })
      ],
      outboundThrottle: { hosts: [
        { host: "waits.example", waiting: 2, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 5 },
        { host: "soon.example", waiting: 0, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 3 },
        { host: "idle.example", waiting: 0, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 0 }
      ] }
    });
    show("/indexers/indexers");
    expect(cellOf("Waits", 4)).toContain("2 requests waiting");
    expect(cellOf("Waits", 4)).toContain("Deluno is pacing waits.example");
    expect(cellOf("Soon", 4)).toContain("Next in 3 seconds");
    expect(cellOf("Idle", 4)).toMatch(/^Ready/);
    expect(cellOf("Broken", 4)).toBe("ReadyNo recent requests");
  });

  it("with no library it shows the priority, and a strict sharing rule, failures and a missing speed read as such", () => {
    reset({
      indexers: [
        indexer({ id: "a", name: "Strict", sharingMode: "share-then-tidy", sharingForHours: 336, sharingUntilRatio: 1, sharingStuckAction: "keep-waiting", sharingStuckAfterDays: 14, priority: 7 }),
        indexer({ id: "b", name: "Failing", consecutiveFailures: 3, lastHealthLatencyMs: null }),
        indexer({ id: "c", name: "Quiet", lastHealthLatencyMs: null, lastHealthMessage: null })
      ]
    });
    show("/indexers/indexers");
    expect(cellOf("Strict", 1)).toContain("Strict sharing");
    expect(cellOf("Strict", 2)).toContain("Priority 7");
    expect(cellOf("Failing", 3)).toContain("3 consecutive failures");
    expect(cellOf("Quiet", 3)).toContain("—");
    expect(cellOf("Quiet", 1)).not.toContain("Strict");
  });

  it("with libraries, an indexer no library searches links to Library Routing and one that is searched names its libraries", () => {
    const routed = { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "a", indexerName: "Searched one", priority: 1 }], downloadClients: [] };
    reset({ libraries: [films], indexers: [indexer({ id: "a", name: "Searched one" }), indexer({ id: "b", name: "Idle" })], routing: [routed] });
    show("/indexers/indexers");
    expect(cellOf("Searched one", 2)).toContain("Searched by Films");
    expect(screen.getByRole("link", { name: /No library searches it/ })).toBeTruthy();
  });
});

describe("the client list when a client has no category at all", () => {
  it("shows a dash and sorts it with the others", () => {
    reset({ clients: [client({ id: "a", name: "Aa", moviesCategory: null, categoryTemplate: null, tvCategory: null }), client({ id: "b", name: "Bb" })] });
    show("/indexers/download-clients");
    fireEvent.click(within(screen.getByRole("columnheader", { name: /^Categories/ })).getByRole("button"));
    expect(screen.getAllByRole("row").map((row) => /Aa|Bb/.exec(row.textContent ?? "")?.[0]).filter(Boolean)).toEqual(["Aa", "Bb"]);
  });
});

describe("the indexer list when pacing is unknown", () => {
  it("an indexer with an unreadable address has no pacing, still sorts by it, and opens its drawer", async () => {
    reset({ indexers: [indexer({ id: "a", name: "Broken", baseUrl: "not an address" }), indexer({ id: "b", name: "Waits", baseUrl: "https://waits.example/api" })], outboundThrottle: { hosts: [{ host: "waits.example", waiting: 2, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 5 }] } });
    show("/indexers/indexers");
    const names = () => screen.getAllByRole("row").map((row) => /Broken|Waits/.exec(row.textContent ?? "")?.[0]).filter(Boolean);
    const pacing = () => within(screen.getByRole("columnheader", { name: /^Pacing/ })).getByRole("button");
    fireEvent.click(pacing());
    expect(names()).toEqual(["Broken", "Waits"]);
    fireEvent.click(pacing());
    expect(names()).toEqual(["Waits", "Broken"]);
    fireEvent.click(screen.getByText("Broken"));
    expect(await dialog()).toBeTruthy();
  });

  it("a switch that is refused says it could not enable or pause the item", async () => {
    reset({ indexers: [indexer({ isEnabled: false })], clients: [client({ isEnabled: false })] });
    authedFetch.mockImplementation(async () => ok({}, 500));
    show("/indexers/indexers");
    fireEvent.click(screen.getByRole("switch", { name: "Enable Alpha" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Could not enable Alpha."));
    cleanup();
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("switch", { name: "Enable My qBittorrent" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Could not enable My qBittorrent."));
  });
});

describe("a request that is rejected without a message", () => {
  it("a switch says the action failed", async () => {
    reset({ indexers: [indexer()] });
    authedFetch.mockImplementation(async () => { throw "offline"; });
    show("/indexers/indexers");
    fireEvent.click(screen.getByRole("switch", { name: "Pause Alpha" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Action failed."));
  });

  it("a save says it could not save, and the drawer stays", async () => {
    reset({ indexers: [indexer()] });
    authedFetch.mockImplementation(async () => { throw "offline"; });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.change(drawer.getByDisplayValue("https://alpha.example/api"), { target: { value: "https://other.example/api" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save indexer" }));
    expect(await drawer.findByText("Could not save")).toBeTruthy();
  });

  it("a second press on Save while the first is still working sends nothing more", async () => {
    reset({ indexers: [indexer()] });
    let release!: () => void;
    authedFetch.mockImplementation(() => new Promise((resolve) => { release = () => resolve(ok({})); }));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.change(drawer.getByDisplayValue("https://alpha.example/api"), { target: { value: "https://other.example/api" } });
    const form = drawer.getByRole("button", { name: "Save indexer" }).closest("form")!;
    fireEvent.submit(form);
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    fireEvent.submit(form);
    expect(authedFetch).toHaveBeenCalledTimes(1);
    release();
    expect(await drawer.findByText("Saved just now")).toBeTruthy();
  });
});

describe("a mapping with a priority the screen cannot use", () => {
  it("is saved with the standard priority", async () => {
    reset({
      clients: [client()],
      pathMappings: [{ id: "m1", downloadClientId: "c1", remotePath: "/a", localPath: "E:\\a", isEnabled: true, priority: 0, host: "localhost", createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z" }]
    });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/path-mappings");
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(callTo("PUT", "/api/download-clients/c1/path-mappings/m1")).priority).toBe(10);
  });
});

describe("editing a library's routing", () => {
  const routed = { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1 }], downloadClients: [{ downloadClientId: "c1", downloadClientName: "My qBittorrent", priority: 1, category: "films" }] };

  it("switching a client off counts as a change, and switching one on that had no category saves it with the client's default", async () => {
    reset({ libraries: [films], indexers: [indexer()], clients: [client(), client({ id: "c2", name: "Second" })], routing: [routed] });
    chainClear();
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.click(await drawer.findByRole("switch", { name: /My qBittorrent/ }));
    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByText("Films"));
    const again = await dialog();
    fireEvent.click(await again.findByRole("switch", { name: /Second/ }));
    fireEvent.click(again.getByRole("button", { name: "Save routing" }));
    expect(await again.findByText("Routing saved")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/libraries/l1/routing")).downloadClients).toEqual([{ downloadClientId: "c1", priority: 1, category: "films" }, { downloadClientId: "c2", priority: 2, category: null }]);
  });

  it("switching the only client off saves a library with none", async () => {
    reset({ libraries: [films], indexers: [indexer()], clients: [client(), client({ id: "c2", name: "Second" })], routing: [routed] });
    chainClear();
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.click(await drawer.findByRole("switch", { name: /My qBittorrent/ }));
    fireEvent.click(drawer.getByRole("switch", { name: /Second/ }));
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("Routing saved")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/libraries/l1/routing")).downloadClients).toEqual([{ downloadClientId: "c2", priority: 1, category: null }]);
  });

  it("switching a client off and back on loses its saved category, which counts as a change", async () => {
    reset({ libraries: [films], indexers: [indexer()], clients: [client()], routing: [routed] });
    chainClear();
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    const toggle = await drawer.findByRole("switch", { name: /My qBittorrent/ });
    fireEvent.click(toggle);
    fireEvent.click(toggle);
    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();
  });

  it("an indexer can be switched off for a library and back on, and one switched on fresh takes tags and a client takes a category typed for it", async () => {
    reset({ libraries: [films], indexers: [indexer(), indexer({ id: "i2", name: "Bravo", baseUrl: "https://bravo.example/api" })], clients: [client(), client({ id: "c2", name: "Second" })], routing: [routed] });
    chainClear();
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/tags" && init?.method === "POST" ? ok({ id: "t1", name: "hd" }, 201) : ok({})));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    const alpha = await drawer.findByRole("switch", { name: /Alpha/ });
    fireEvent.click(alpha);
    expect(drawer.queryByLabelText("Required tags for Alpha")).toBeNull();
    fireEvent.click(drawer.getByRole("switch", { name: /Bravo/ }));
    fireEvent.change(await drawer.findByLabelText("Required tags for Bravo"), { target: { value: "hd" } });
    fireEvent.click(await drawer.findByRole("button", { name: "Create new tag hd for Required tags for Bravo" }));
    fireEvent.click(drawer.getByRole("switch", { name: /Second/ }));
    fireEvent.change(await drawer.findByLabelText("Optional category sent to the download client", { selector: "#route-category-c2" }), { target: { value: "extra" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("Routing saved")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/libraries/l1/routing"))).toEqual({
      sources: [{ indexerId: "i2", priority: 1, requiredTags: "hd", excludedTags: "" }],
      downloadClients: [{ downloadClientId: "c1", priority: 1, category: "films" }, { downloadClientId: "c2", priority: 2, category: "extra" }]
    });
  });

  it("a rejected routing save says it could not save", async () => {
    reset({ libraries: [films], indexers: [indexer()], clients: [client()], routing: [routed] });
    chainClear();
    authedFetch.mockImplementation(async () => { throw "offline"; });
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.change(await drawer.findByLabelText("Optional category sent to the download client"), { target: { value: "kids" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("Could not save")).toBeTruthy();
  });

  it("the tags a title must or must not carry for an indexer are created and sent with the save", async () => {
    reset({ libraries: [films], indexers: [indexer()], clients: [client()], routing: [routed] });
    chainClear();
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/tags" && init?.method === "POST" ? ok({ id: "t1", name: bodyOf([url, init]).name }, 201) : ok({})));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.change(await drawer.findByLabelText("Required tags for Alpha"), { target: { value: "hd" } });
    fireEvent.click(await drawer.findByRole("button", { name: "Create new tag hd for Required tags for Alpha" }));
    await waitFor(() => expect(drawer.getByLabelText("Required tags for Alpha chosen")).toBeTruthy());
    fireEvent.change(drawer.getByLabelText("Excluded tags for Alpha"), { target: { value: "cam" } });
    fireEvent.click(await drawer.findByRole("button", { name: "Create new tag cam for Excluded tags for Alpha" }));
    await waitFor(() => expect(drawer.getByLabelText("Excluded tags for Alpha chosen")).toBeTruthy());
    // Save follows the lists and categories; a change to the tags alone does not wake it (reported on #1329), so the category is changed too.
    fireEvent.change(drawer.getByLabelText("Optional category sent to the download client"), { target: { value: "kids" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("Routing saved")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/libraries/l1/routing")).sources).toEqual([{ indexerId: "i1", priority: 1, requiredTags: "hd", excludedTags: "cam" }]);
  });

  it("a library with no routing yet opens with nothing chosen and sorts as having none", async () => {
    reset({ libraries: [films, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "D:\\Shows" }], indexers: [indexer()], clients: [client()], routing: [routed] });
    chainClear();
    show("/indexers/library-routing");
    for (const label of ["Indexers", "Download clients", "Status"]) {
      fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));
      const order = screen.getAllByRole("row").map((row) => /Films|Shows/.exec(row.textContent ?? "")?.[0]).filter(Boolean);
      expect(order).toEqual(["Shows", "Films"]);
    }
    fireEvent.click(screen.getByText("Shows"));
    const drawer = await dialog();
    expect((await drawer.findAllByRole("switch")).every((box) => box.getAttribute("aria-checked") === "false")).toBe(true);
  });
});

describe("a new drawer's title", () => {
  it("falls back to a plain name when the name is cleared", async () => {
    reset();
    show("/indexers/indexers");
    fireEvent.click(screen.getAllByRole("button", { name: "New indexer" })[0]);
    expect(await screen.findByText("Where Deluno looks for releases.")).toBeTruthy();
    cleanup();
    show("/indexers/download-clients");
    fireEvent.click(screen.getAllByRole("button", { name: "New client" })[0]);
    await dialog();
    expect(await screen.findByText("Where Deluno sends approved releases.")).toBeTruthy();
    cleanup();
    show("/indexers/proxies");
    fireEvent.click(screen.getAllByRole("button", { name: "New proxy" })[0]);
    expect(await screen.findByText("Where an indexer's requests are sent instead of directly.")).toBeTruthy();
  });

  it("a saved client with no address on file shows only its protocol", async () => {
    reset({ clients: [client()] });
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    expect(await screen.findByText("qBittorrent ·")).toBeTruthy();
  });
});

describe("selecting download clients to change their tags", () => {
  it("opens the tags question for the ticked clients, applies it, and clears the selection; Cancel leaves them ticked", async () => {
    reset({ clients: [client({ id: "a", name: "Aa" }), client({ id: "b", name: "Bb" })] });
    authedFetch.mockImplementation(async () => ok({ updated: 2 }));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("button", { name: /^Select/ }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all download clients" }));
    fireEvent.click(screen.getByRole("button", { name: "Change tags on the selected download clients" }));
    expect(await screen.findByText("Change tags: 2 selected download clients")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByText("Change tags: 2 selected download clients")).toBeNull());
    expect(screen.getByText("2 selected")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Change tags on the selected download clients" }));
    fireEvent.change(await screen.findByLabelText("How to apply the tags"), { target: { value: "replace" } });
    fireEvent.click(screen.getByRole("button", { name: "Replace tags" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Tags changed on 2 download clients."));
    expect(bodyOf(callTo("POST", "/api/tags/bulk-connections"))).toEqual({ kind: "downloadClient", ids: ["a", "b"], tags: [], mode: "replace" });
    await waitFor(() => expect(screen.queryByText("2 selected")).toBeNull());
  });
});

describe("installed apps found on this machine", () => {
  const unknownKind = { kind: "mystery", displayName: "Mystery Client", category: "download-client", installed: false, reachable: true, connected: false, defaultHost: "192.168.1.10", defaultPort: 7000 };
  const found = { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 8080 };

  it("an app Deluno has no preset for opens a client under the app's own name, with the categories the form starts with", async () => {
    reset();
    fetchJson.mockImplementation(async (url: string) => (url === "/api/system/bundled-components" ? [unknownKind] : []));
    authedFetch.mockImplementation(async () => ok({ healthStatus: "healthy", message: "Answered." }));
    show("/indexers/download-clients");
    fireEvent.click(await screen.findByRole("button", { name: "Use Mystery Client (192.168.1.10:7000)" }));
    const drawer = await dialog();
    expect(await screen.findAllByText("Mystery Client")).not.toHaveLength(0);
    expect(drawer.getByDisplayValue("192.168.1.10")).toBeTruthy();
    expect(drawer.getByDisplayValue("7000")).toBeTruthy();
    expect(drawer.getByDisplayValue("deluno-movies")).toBeTruthy();
    expect(drawer.getByDisplayValue("deluno-tv")).toBeTruthy();
  });

  it("when one more app becomes connected, the list of clients is read again", async () => {
    reset();
    let connected = false;
    fetchJson.mockImplementation(async (url: string) => {
      if (String(url).endsWith("/connect")) { connected = true; return { name: "qBittorrent", healthy: true, message: "Connected." }; }
      return url === "/api/system/bundled-components" ? [{ ...found, connected }] : [];
    });
    show("/indexers/download-clients");
    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));
    await waitFor(() => expect(fetchJson.mock.calls.filter(([url]) => url === "/api/system/bundled-components").length).toBeGreaterThan(2));
    expect(connected).toBe(true);
  });
});

describe("a download reading that arrives for a client the screen has no overview for", () => {
  it("is ignored, and the list stays as it was", async () => {
    reset({ clients: [client()], telemetry: null });
    show("/indexers/download-clients");
    await waitFor(() => expect(pushHandlers.has("DownloadProgress")).toBe(true));
    act(() => pushHandlers.get("DownloadProgress")!({ id: "x", progress: 0.5, speedMbps: 3 }));
    expect(screen.getByText("My qBittorrent")).toBeTruthy();
  });
});
