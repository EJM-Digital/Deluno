import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { IndexersPage } from "./connections-screen";

// #1329: the library routing drawer: switching a download client off takes its category text and its check result with it.

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


describe("the library routing drawer, low rows (#1329)", () => {
  it("switching a client off removes its category text and its check result, and switching it back on starts from the client's own default", async () => {
    const routed = { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1 }], downloadClients: [{ downloadClientId: "c1", downloadClientName: "My qBittorrent", priority: 1 }] };
    reset({ libraries: [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\Films" }], indexers: [indexer()], clients: [client()], routing: [routed] });
    fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/folder-chain")
      ? { clients: [{ clientName: "My qBittorrent", links: [{ status: "problem", message: "The movies category is missing." }] }], allMatch: false, steps: [], libraryLinks: [] }
      : []));
    authedFetch.mockImplementation(async () => ok({ status: "ready", message: "The kids category is ready in the client." }));
    show("/indexers/library-routing");

    fireEvent.click(await screen.findByRole("button", { name: "Fix Films routing" }));
    const box = (await screen.findByLabelText("Optional category sent to the download client")) as HTMLInputElement;
    fireEvent.change(box, { target: { value: "kids" } });
    expect(await screen.findByText("Library category: kids")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Check category" }));
    expect(await screen.findByText("Ready")).toBeTruthy();
    expect(screen.getByText("The kids category is ready in the client.")).toBeTruthy();

    fireEvent.click(screen.getByRole("switch", { name: /My qBittorrent/ }));
    expect(screen.queryByLabelText("Optional category sent to the download client")).toBeNull();
    expect(screen.queryByText("Ready")).toBeNull();
    expect(screen.queryByText("The kids category is ready in the client.")).toBeNull();

    fireEvent.click(screen.getByRole("switch", { name: /My qBittorrent/ }));
    expect((await screen.findByLabelText("Optional category sent to the download client") as HTMLInputElement).value).toBe("");
    expect(screen.getByText("Using client default")).toBeTruthy();
    expect(screen.queryByText("The kids category is ready in the client.")).toBeNull();
  });
});
