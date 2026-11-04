import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { IndexersPage } from "./connections-screen";
import { toast } from "../components/shell/toaster";

// #1329: Find and Download, adding, saving, testing, switching and removing a download client, a proxy and an indexer, and the links that open them.

const fetchJson = vi.hoisted(() => vi.fn(async (_url: string, _init?: unknown): Promise<unknown> => []));
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", () => ({ authedFetch }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() }, Toaster: () => null }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useSignalREvent: () => undefined,
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

function reset(over: Record<string, unknown> = {}) {
  for (const key of Object.keys(sources)) delete sources[key];
  Object.assign(sources, {
    indexers: [], clients: [], libraries: [], routing: [], pathMappings: [], proxies: [], settings: emptyPlatformSettingsSnapshot, telemetry: null,
    outboundThrottle: { hosts: [] }, scoreboard: { indexers: [], generatedUtc: "2026-10-29T00:00:00Z" }, qbittorrentBlocklistSync: {}, ...over
  });
}
const ok = (body: unknown = {}, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function show(path: string) {
  const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: [path] });
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);
}

const proxy = (over: Record<string, unknown> = {}) => ({
  id: "p1", name: "HTTP proxy", proxyType: "http", address: "http://proxy.example:3128", username: null, hasPassword: false, isEnabled: true,
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", assignedIndexerNames: [], ...over
});
const calls = (method: string) => authedFetch.mock.calls.filter(([, init]) => (init as RequestInit).method === method);
const callTo = (method: string, url: string) => calls(method).find(([target]) => target === url)!;
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));
const dialog = async () => within(await screen.findByRole("dialog"));

describe("a proxy", () => {
  it("is added from the empty list: the address is asked for, then it is sent and the drawer stays on the new proxy", async () => {
    reset();
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/indexer-proxies" && init?.method === "POST" ? ok(proxy({ id: "p9", address: "http://new.example:3128" }), 201) : ok({})));
    show("/indexers/proxies");
    fireEvent.click(screen.getAllByRole("button", { name: /New proxy/ })[0]);
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: "Add proxy" }));
    expect(await drawer.findByText("Add the address Deluno should send requests through.")).toBeTruthy();
    expect(authedFetch).not.toHaveBeenCalled();
    fireEvent.change(drawer.getByPlaceholderText(/^http/), { target: { value: "http://new.example:3128" } });
    fireEvent.click(drawer.getByRole("button", { name: "Add proxy" }));
    expect(await drawer.findByText("Proxy added")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/indexer-proxies"))).toMatchObject({ address: "http://new.example:3128" });
  });

  it("is saved when edited, and removed after a question", async () => {
    reset({ proxies: [proxy()] });
    authedFetch.mockImplementation(async (_url: string, init?: RequestInit) => (init?.method === "DELETE" ? new Response(null, { status: 204 }) : ok({})));
    show("/indexers/proxies");
    fireEvent.click(screen.getByText("HTTP proxy"));
    const drawer = await dialog();
    fireEvent.change(drawer.getByDisplayValue("http://proxy.example:3128"), { target: { value: "http://other.example:3128" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save proxy" }));
    expect(await drawer.findByText("Saved just now")).toBeTruthy();
    expect(callTo("PUT", "/api/indexer-proxies/p1")).toBeTruthy();

    fireEvent.click(drawer.getByRole("button", { name: /^Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: /^Remove proxy/ }));
    await waitFor(() => expect(calls("DELETE")[0][0]).toBe("/api/indexer-proxies/p1"));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("HTTP proxy removed"));
  });

  it("says what the server refused when saving", async () => {
    reset({ proxies: [proxy()] });
    authedFetch.mockImplementation(async () => ok({ title: "That proxy address is not allowed." }, 400));
    show("/indexers/proxies");
    fireEvent.click(screen.getByText("HTTP proxy"));
    const drawer = await dialog();
    fireEvent.change(drawer.getByDisplayValue("http://proxy.example:3128"), { target: { value: "http://other.example:3128" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save proxy" }));
    expect(await drawer.findByText("That proxy address is not allowed.")).toBeTruthy();
  });
});

describe("a download client", () => {
  it("is added with a host, sent as it stands, and says where it was routed", async () => {
    reset({ libraries: [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\\Films" }] });
    const created = client({ id: "c9", name: "qBittorrent", host: "nas.example" });
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/download-clients" && init?.method === "POST" ? ok(created, 201) : ok({})));
    fetchJson.mockImplementation(async (url: string) => (url === "/api/libraries/l1/routing" ? { libraryId: "l1", libraryName: "Films", sources: [], downloadClients: [{ downloadClientId: "c9", downloadClientName: "qBittorrent", priority: 1 }] } : []));
    show("/indexers/download-clients");
    fireEvent.click(screen.getAllByRole("button", { name: /New client|Add client/ })[0]);
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: "Add client" }));
    expect(await drawer.findByText("Client added and routed to Films")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/download-clients"))).toMatchObject({ host: "localhost", port: 8080 });
  });

  it("says plainly that a client was added when no library took it", async () => {
    reset();
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/download-clients" && init?.method === "POST" ? ok(client({ id: "c9" }), 201) : ok({})));
    show("/indexers/download-clients");
    fireEvent.click(screen.getAllByRole("button", { name: /New client|Add client/ })[0]);
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: "Add client" }));
    expect(await drawer.findByText("Client added")).toBeTruthy();
  });

  it("is saved when edited, and removed after a question", async () => {
    reset({ clients: [client()] });
    authedFetch.mockImplementation(async (_url: string, init?: RequestInit) => (init?.method === "DELETE" ? new Response(null, { status: 204 }) : ok({})));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    const drawer = await dialog();
    fireEvent.change(drawer.getAllByRole("combobox").find((box) => (box as HTMLSelectElement).value === "8080")!, { target: { value: "9091" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save client" }));
    expect(await drawer.findByText("Saved just now")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/download-clients/c1"))).toMatchObject({ port: 9091 });

    fireEvent.click(drawer.getByRole("button", { name: /^Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: /^Remove (download )?client/ }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("My qBittorrent removed"));
  });

  it("opens the client a notice names, and an unknown one opens nothing", async () => {
    reset({ clients: [client()] });
    show("/indexers/download-clients?connection=c1");
    expect(await dialog()).toBeTruthy();
    cleanup();
    reset({ clients: [client()] });
    show("/indexers/download-clients?connection=nope");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("testing a connection from its drawer", () => {
  it("tests an unsaved indexer as a draft and shows the answer", async () => {
    reset();
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? ok({ healthStatus: "healthy", message: "Reached it.", caps: { categories: "2000,5000", mediaScope: "tv" } }) : ok({})));
    show("/indexers/indexers");
    fireEvent.click(screen.getAllByRole("button", { name: "New indexer" })[0]);
    const drawer = await dialog();
    fireEvent.change(drawer.getByPlaceholderText("https://api.nzbgeek.info"), { target: { value: "https://x.example/api" } });
    fireEvent.change(drawer.getByPlaceholderText("Paste your API key"), { target: { value: "secret" } });
    fireEvent.click(drawer.getByRole("button", { name: /Test/ }));
    expect(await drawer.findByText("Reached it.")).toBeTruthy();
  });

  it("tests a saved, unchanged indexer by its own test, and a failure says why", async () => {
    reset({ indexers: [indexer()] });
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/i1/test" ? ok({ healthStatus: "healthy", message: "Still fine." }) : ok({})));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: /^Test/ }));
    expect(await drawer.findByText("Still fine.")).toBeTruthy();

    authedFetch.mockImplementation(async () => { throw new Error("The server did not answer."); });
    fireEvent.click(drawer.getByRole("button", { name: /^Test/ }));
    expect(await drawer.findByText("The server did not answer.")).toBeTruthy();
  });

  it("tests a download client with the form as it stands", async () => {
    reset({ clients: [client()] });
    authedFetch.mockImplementation(async (url: string) => (url.includes("/test") ? ok({ healthStatus: "healthy", message: "Client answered." }) : ok({})));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: /^Test/ }));
    expect(await drawer.findByText("Client answered.")).toBeTruthy();
  });

  it("a test that is cancelled says so", async () => {
    reset({ indexers: [indexer()] });
    authedFetch.mockImplementation((_url: string, init?: RequestInit) => new Promise((_, reject) => { init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))); }));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: /^Test/ }));
    fireEvent.click(await drawer.findByRole("button", { name: "Cancel test" }));
    expect(await drawer.findByText("Test cancelled.")).toBeTruthy();
  });
});

describe("the switches and the links that open an indexer", () => {
  it("opens the indexer a release names, whatever the case", async () => {
    reset({ indexers: [indexer()] });
    show("/indexers/indexers?indexer=ALPHA");
    expect(await dialog()).toBeTruthy();
  });

  it("opens nothing for an indexer name that is not there", async () => {
    reset({ indexers: [indexer()] });
    show("/indexers/indexers?indexer=Nobody");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("pauses an indexer and a client with their switches", async () => {
    reset({ indexers: [indexer()], clients: [client()] });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/indexers");
    fireEvent.click(screen.getByRole("switch", { name: "Pause Alpha" }));
    await waitFor(() => expect(calls("PUT").length).toBeGreaterThan(0));
    const first = calls("PUT").length;
    cleanup();
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("switch", { name: "Pause My qBittorrent" }));
    await waitFor(() => expect(calls("PUT").length).toBeGreaterThan(first));
  });
});
