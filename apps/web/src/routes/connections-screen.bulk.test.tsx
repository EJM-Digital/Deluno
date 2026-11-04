import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { IndexersPage } from "./connections-screen";
import { toast } from "../components/shell/toaster";

// #1329: Find & Download > Indexers, selecting several: enable, pause, change what they are used for, tags, and remove.

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

afterEach(() => { cleanup(); fetchJson.mockReset(); fetchJson.mockImplementation(async () => []); authedFetch.mockReset(); });

const two = () => reset({ indexers: [indexer({ id: "i1", name: "Alpha" }), indexer({ id: "i2", name: "Bravo", baseUrl: "https://bravo.example/api" })] });
const putBodies = () => authedFetch.mock.calls.filter(([, init]) => (init as RequestInit).method === "PUT").map(([url, init]) => [url, JSON.parse(String((init as RequestInit).body))]);

async function selectBoth() {
  show("/indexers/indexers");
  fireEvent.click(screen.getByRole("button", { name: "Select" }));
  fireEvent.click(await screen.findByRole("checkbox", { name: "Select Alpha" }));
  fireEvent.click(screen.getByRole("checkbox", { name: "Select Bravo" }));
  return screen.getByRole("toolbar", { name: "Bulk indexer actions" });
}

describe("selecting several indexers (#737)", () => {
  it("Select becomes Done, and Done leaves selecting with nothing selected", async () => {
    two();
    show("/indexers/indexers");
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    fireEvent.click(await screen.findByRole("checkbox", { name: "Select Alpha" }));
    expect(screen.getByText("1 selected")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("toolbar", { name: "Bulk indexer actions" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    expect(screen.queryByText("1 selected")).toBeNull();
  });

  it("selects and unselects one indexer by its box, and clears the selection with Clear", async () => {
    two();
    const bar = await selectBoth();
    expect(within(bar).getByText("2 selected")).toBeTruthy();
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Alpha" }));
    expect(within(bar).getByText("1 selected")).toBeTruthy();
    fireEvent.click(within(bar).getByRole("button", { name: "Clear selection" }));
    expect(screen.queryByText(/selected/)).toBeNull();
  });

  it("pauses every selected indexer, says how many, and clears the selection", async () => {
    two();
    authedFetch.mockImplementation(async () => ok({}));
    const bar = await selectBoth();
    fireEvent.click(within(bar).getByRole("button", { name: "Pause" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("2 indexers paused"));
    expect(putBodies()).toEqual([["/api/indexers/i1", { isEnabled: false }], ["/api/indexers/i2", { isEnabled: false }]]);
    await waitFor(() => expect(screen.queryByText(/selected/)).toBeNull());
  });

  it("enables every selected indexer", async () => {
    two();
    authedFetch.mockImplementation(async () => ok({}));
    const bar = await selectBoth();
    fireEvent.click(within(bar).getByRole("button", { name: "Enable" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("2 indexers enabled"));
  });

  it("does nothing for Enable, Pause or a new scope when nothing is selected", async () => {
    two();
    show("/indexers/indexers");
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    expect(screen.queryByRole("toolbar", { name: "Bulk indexer actions" })).toBeNull();
    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("says the first reason when a bulk change fails, or that it failed when the failure gives none", async () => {
    two();
    authedFetch.mockImplementation(async () => ok({ errors: { x: ["Alpha cannot be paused."] } }, 400));
    const bar = await selectBoth();
    fireEvent.click(within(bar).getByRole("button", { name: "Pause" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Alpha cannot be paused."));
    authedFetch.mockImplementation(async () => { throw "boom"; });
    fireEvent.click(within(bar).getByRole("button", { name: "Enable" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Bulk update failed."));
  });

  it("changes what the selected indexers are used for", async () => {
    two();
    authedFetch.mockImplementation(async () => ok({}));
    const bar = await selectBoth();
    fireEvent.change(within(bar).getByLabelText("Change used-for scope for selected indexers"), { target: { value: "tv" } });
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("2 indexers set to TV"));
    expect(putBodies()[0]).toEqual(["/api/indexers/i1", { mediaScope: "tv" }]);
  });

  it("says why when a scope change fails", async () => {
    two();
    authedFetch.mockImplementation(async () => { throw new Error("offline"); });
    const bar = await selectBoth();
    fireEvent.change(within(bar).getByLabelText("Change used-for scope for selected indexers"), { target: { value: "tv" } });
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("offline"));
    authedFetch.mockImplementation(async () => { throw "boom"; });
    fireEvent.change(within(bar).getByLabelText("Change used-for scope for selected indexers"), { target: { value: "movies" } });
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Bulk update failed."));
  });

  it("asks before it removes the selected indexers, then removes them all", async () => {
    two();
    authedFetch.mockImplementation(async () => new Response(null, { status: 204 }));
    const bar = await selectBoth();
    fireEvent.click(within(bar).getByRole("button", { name: "Remove" }));
    expect(authedFetch).not.toHaveBeenCalled();
    expect(await screen.findByText("Remove 2 indexers?")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Remove indexers" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("2 indexers removed"));
    expect(authedFetch.mock.calls.map(([url, init]) => [url, (init as RequestInit).method])).toEqual([["/api/indexers/i1", "DELETE"], ["/api/indexers/i2", "DELETE"]]);
  });

  it("closes the question with Cancel, removing nothing, and says why when removing fails", async () => {
    two();
    const bar = await selectBoth();
    fireEvent.click(within(bar).getByRole("button", { name: "Remove" }));
    await screen.findByText("Remove 2 indexers?");
    const cancels = screen.getAllByRole("button", { name: "Cancel" });
    fireEvent.click(cancels[cancels.length - 1]);
    await waitFor(() => expect(screen.queryByText("Remove 2 indexers?")).toBeNull());
    expect(authedFetch).not.toHaveBeenCalled();

    authedFetch.mockImplementation(async () => ok({ title: "Alpha is still used." }, 409));
    fireEvent.click(within(bar).getByRole("button", { name: "Remove" }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove indexers" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Alpha is still used."));
    authedFetch.mockImplementation(async () => { throw "boom"; });
    fireEvent.click(within(bar).getByRole("button", { name: "Remove" }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove indexers" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Bulk remove failed."));
  });
});
