import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { queryClient as sharedQueryClient } from "../lib/route-cache";
import { IndexersPage, indexersSources } from "./connections-screen";
import { toast } from "../components/shell/toaster";

// #1329: the rest of Find and Download: the download client list's cells, the shared path mappings, the routing links and checks, and the detected-app hand-offs.

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
const mapping = (over: Record<string, unknown> = {}) => ({
  id: "m1", downloadClientId: "c1", remotePath: "/downloads/complete", localPath: "D:\\Downloads\\complete", isEnabled: true, priority: 10, host: "localhost",
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...over
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

function Where() {
  return <span data-testid="where">{useLocation().pathname + useLocation().search}</span>;
}
function show(path: string) {
  const router = createMemoryRouter([{ path: "*", element: <><IndexersPage /><Where /></> }], { initialEntries: [path] });
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);
}
const calls = (method: string) => authedFetch.mock.calls.filter(([, init]) => (init as RequestInit).method === method);
const callTo = (method: string, url: string) => calls(method).find(([target]) => target === url)!;
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));
const dialog = async () => within(await screen.findByRole("dialog"));
const rowOf = (name: string) => screen.getAllByRole("row").find((row) => row.textContent?.includes(name))!;
const cellOf = (name: string, index: number) => within(rowOf(name)).getAllByRole("cell")[index].textContent ?? "";
const rowNames = (pattern: RegExp) => screen.getAllByRole("row").map((row) => pattern.exec(row.textContent ?? "")?.[0]).filter(Boolean);
const chainClear = () => fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/folder-chain") ? { clients: [], allMatch: true, steps: [], libraryLinks: [] } : []));
const press = (label: string) => fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));

afterEach(() => { cleanup(); fetchJson.mockReset(); fetchJson.mockImplementation(async () => []); authedFetch.mockReset(); vi.mocked(toast.success).mockClear(); vi.mocked(toast.error).mockClear(); sharedQueryClient.clear(); });

describe("the library routing source", () => {
  it("reads each library's routing, and a library whose routing cannot be read counts as routed nowhere", async () => {
    fetchJson.mockImplementation(async (url: string) => {
      if (url === "/api/libraries") return [films, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "D:\\Shows" }];
      if (url === "/api/libraries/l1/routing") return { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1 }], downloadClients: [] };
      throw new Error("down");
    });
    expect(await indexersSources.routing.fetch()).toEqual([
      { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1 }], downloadClients: [] },
      { libraryId: "l2", libraryName: "Shows", sources: [], downloadClients: [] }
    ]);
  });
});

describe("the download client list, cell by cell", () => {
  it("a client with no library to send to says nothing about routing, a known type names how it signs in, and an unknown one shows only its protocol", () => {
    reset({ clients: [client({ id: "a", name: "Known" }), client({ id: "b", name: "Odd", protocol: "mystery", moviesCategory: null, tvCategory: null, categoryTemplate: "{kind}" })] });
    show("/indexers/download-clients");
    expect(cellOf("Known", 1)).toMatch(/^qBittorrent.*·/);
    expect(cellOf("Known", 1)).not.toContain("Route it");
    expect(cellOf("Odd", 1)).toBe("mystery");
    expect(cellOf("Odd", 2)).toBe("{kind}Movies · TV");
  });

  it("with libraries, a client nobody sends to links to Library Routing, and one that is used names its libraries", () => {
    const routed = { libraryId: "l1", libraryName: "Films", sources: [], downloadClients: [{ downloadClientId: "b", downloadClientName: "Used", priority: 1 }] };
    reset({ libraries: [films], clients: [client({ id: "a", name: "Idle" }), client({ id: "b", name: "Used" })], routing: [routed] });
    show("/indexers/download-clients");
    expect(cellOf("Idle", 1)).toContain("No library sends downloads here");
    expect(within(rowOf("Idle")).getByRole("link", { name: /No library sends downloads here/ }).getAttribute("href")).toContain("/indexers/library-routing");
    expect(cellOf("Used", 1)).toContain("Used by Films");
  });

  it("the categories cell is a dash with none, warns when both are the same, and otherwise counts the file-location links", () => {
    reset({
      clients: [client({ id: "a", name: "None", moviesCategory: null, tvCategory: null }), client({ id: "b", name: "Same", moviesCategory: "all", tvCategory: "all" }), client({ id: "c", name: "Linked" })],
      pathMappings: [mapping({ id: "m1", downloadClientId: "c" }), mapping({ id: "m2", downloadClientId: "c", remotePath: "/other" })]
    });
    show("/indexers/download-clients");
    expect(cellOf("None", 2)).toBe("—Movies · TV");
    expect(cellOf("Same", 2)).toBe("allSame for both — files will mix".replace("all", "all · all"));
    expect(cellOf("Linked", 2)).toContain("2 file-location links");
  });

  it("sorts by Type, Categories, Now, Status and On, each reversed by a second press", () => {
    reset({
      clients: [
        client({ id: "a", name: "Aa", protocol: "sabnzbd", moviesCategory: "zeta", tvCategory: null, isEnabled: false, healthStatus: "unhealthy" }),
        client({ id: "b", name: "Bb", protocol: "qbittorrent", moviesCategory: null, categoryTemplate: "alpha", tvCategory: "tv" }),
        client({ id: "c", name: "Cc", protocol: "mystery", moviesCategory: "mid", tvCategory: "tv" })
      ],
      telemetry: { clients: [{ clientId: "c", clientPaused: true, summary: { activeCount: 3, totalSpeedMbps: 1, importReadyCount: 0 } }, { clientId: "b", clientPaused: false, summary: { activeCount: 1, totalSpeedMbps: 1, importReadyCount: 0 } }] }
    });
    show("/indexers/download-clients");
    const names = () => rowNames(/Aa|Bb|Cc/);
    const order: Record<string, string[]> = {
      Name: ["Aa", "Bb", "Cc"],
      Type: ["Cc", "Bb", "Aa"],
      Categories: ["Bb", "Cc", "Aa"],
      Now: ["Bb", "Cc", "Aa"],
      Status: ["Aa", "Cc", "Bb"]
    };
    for (const [label, expected] of Object.entries(order)) {
      press(label);
      expect(names()).toEqual(expected);
      press(label);
      // A client with no reading sorts last either way.
      expect(names()).toEqual(label === "Now" ? ["Cc", "Bb", "Aa"] : [...expected].reverse());
    }
    press("On");
    expect(names()[0]).toBe("Aa");
    press("On");
    expect(names()[2]).toBe("Aa");
  });

  it("select mode swaps the columns for a checkbox on each client, and Select all ticks them all", () => {
    reset({ clients: [client({ id: "a", name: "Aa" }), client({ id: "b", name: "Bb" })] });
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("button", { name: /^Select/ }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Aa" }));
    expect(screen.getByText("1 selected")).toBeTruthy();
    expect((screen.getByRole("checkbox", { name: "Select Bb" }) as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all download clients" }));
    expect(screen.getByText("2 selected")).toBeTruthy();
    expect((screen.getByRole("checkbox", { name: "Select Bb" }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Clear selection" }));
    expect(screen.queryByText("2 selected")).toBeNull();
  });
});

describe("a warning on a download client", () => {
  const warned = () => ({ clients: [{ clientId: "c1", clientPaused: false, clientWarnings: ["The disk is nearly full."], summary: { activeCount: 0, totalSpeedMbps: 0, importReadyCount: 0 } }] });

  it("is cleared at the client from the list and says so", async () => {
    reset({ clients: [client()], telemetry: warned() });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("button", { name: "Clear My qBittorrent's warnings" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Cleared My qBittorrent's warnings"));
    expect(callTo("POST", "/api/download-clients/c1/warnings/clear")).toBeTruthy();
    expect(screen.queryByTestId("where")?.textContent).toBe("/indexers/download-clients");
  });

  it("says why it could not be cleared, and from the drawer the same clear runs", async () => {
    reset({ clients: [client()], telemetry: warned() });
    authedFetch.mockImplementation(async () => ok({ title: "SABnzbd would not clear it." }, 400));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("button", { name: "Clear My qBittorrent's warnings" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("SABnzbd would not clear it."));

    fireEvent.click(screen.getByText("My qBittorrent"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: "Clear all of My qBittorrent's warnings" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(2));
  });
});

describe("switching a client or an indexer while its drawer is open", () => {
  it("an untouched drawer follows the switch, so it does not ask to discard anything", async () => {
    reset({ indexers: [indexer()], clients: [client()] });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    const drawer = await dialog();
    fireEvent.click(screen.getAllByRole("switch", { name: "Pause My qBittorrent", hidden: true })[0]);
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.queryByText("Discard unsaved changes?")).toBeNull();

    cleanup();
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const indexerDrawer = await dialog();
    fireEvent.click(screen.getAllByRole("switch", { name: "Pause Alpha", hidden: true })[0]);
    await waitFor(() => expect(calls("PUT")).toHaveLength(2));
    fireEvent.click(indexerDrawer.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.queryByText("Discard unsaved changes?")).toBeNull();
  });

  it("a switch the server refuses says why, and leaves the drawer as it was", async () => {
    reset({ clients: [client()] });
    authedFetch.mockImplementation(async () => ok({ title: "Could not reach it." }, 400));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByRole("switch", { name: "Pause My qBittorrent" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Could not reach it."));
  });
});

describe("the remote path mappings tab", () => {
  const withClients = () => reset({
    clients: [client({ id: "c1", name: "Alpha client" }), client({ id: "c2", name: "Beta client", host: "nas.example" })],
    pathMappings: [mapping(), mapping({ id: "m2", downloadClientId: "c2", host: "gone.example", remotePath: "/z", localPath: "E:\\z" }), mapping({ id: "m3", downloadClientId: "c1", host: null, remotePath: "/a", localPath: "E:\\a" })]
  });

  it("lists every mapping with its host, both paths and the clients it applies to, and sorts by each column", () => {
    withClients();
    show("/indexers/path-mappings");
    expect(cellOf("/downloads/complete", 0)).toBe("localhost");
    expect(cellOf("/downloads/complete", 3)).toBe("Alpha client");
    expect(cellOf("/z", 3)).toBe("Beta client");
    expect(cellOf("/a", 0)).toBe("—");
    const names = () => rowNames(/\/downloads\/complete|\/z|\/a(?=E:)/);
    const order: Record<string, string[]> = {
      Host: ["/a", "/z", "/downloads/complete"],
      "Client path": ["/a", "/downloads/complete", "/z"],
      "Deluno path": ["/downloads/complete", "/a", "/z"],
      "Applies to": ["/downloads/complete", "/a", "/z"]
    };
    for (const [label, expected] of Object.entries(order)) {
      press(label);
      expect(names()).toEqual(expected);
      press(label);
      // Two mappings share a client name, so they keep their order on a tie.
      expect(names()).toEqual(label === "Applies to" ? ["/z", "/downloads/complete", "/a"] : [...expected].reverse());
    }
  });

  it("says no client sits at a host when the mapping's client is gone", () => {
    reset({ clients: [client({ id: "c1" })], pathMappings: [mapping({ id: "m9", downloadClientId: "missing", host: "gone.example" })] });
    show("/indexers/path-mappings");
    expect(cellOf("/downloads/complete", 3)).toBe("No client at this host yet");
  });

  it("adds a mapping filed under the first client by default, ignoring blanks and a bad priority", async () => {
    withClients();
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/path-mappings");
    const add = screen.getByRole("button", { name: "Add mapping" }) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Client path"), { target: { value: "  /new  " } });
    expect(add.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Deluno path"), { target: { value: "  E:\\new " } });
    fireEvent.change(screen.getByLabelText("Host (leave blank to use the selected client's own host)"), { target: { value: " nas.example " } });
    fireEvent.click(add);
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Remote path mapping added"));
    expect(bodyOf(callTo("POST", "/api/download-clients/c1/path-mappings"))).toEqual({ remotePath: "/new", localPath: "E:\\new", isEnabled: true, priority: 10, host: "nas.example" });
    expect((screen.getByLabelText("Client path") as HTMLInputElement).value).toBe("");
  });

  it("files a mapping under the client chosen from the list, without a host when none is typed", async () => {
    withClients();
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/path-mappings");
    fireEvent.change(screen.getByLabelText("Download client this mapping is filed under"), { target: { value: "c2" } });
    fireEvent.change(screen.getByLabelText("Client path"), { target: { value: "/p" } });
    fireEvent.change(screen.getByLabelText("Deluno path"), { target: { value: "E:\\p" } });
    fireEvent.click(screen.getByRole("button", { name: "Add mapping" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(1));
    expect(callTo("POST", "/api/download-clients/c2/path-mappings")).toBeTruthy();
    expect("host" in bodyOf(callTo("POST", "/api/download-clients/c2/path-mappings"))).toBe(false);
  });

  it("edits a mapping in one request to its own client, with the client choice locked, and Cancel puts the form back", async () => {
    withClients();
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/path-mappings");
    fireEvent.click(within(rowOf("/z")).getByRole("button", { name: "Edit" }));
    expect((screen.getByLabelText("Download client this mapping is filed under") as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByLabelText("Client path") as HTMLInputElement).value).toBe("/z");
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect((screen.getByLabelText("Client path") as HTMLInputElement).value).toBe("");

    fireEvent.click(within(rowOf("/a")).getByRole("button", { name: "Edit" }));
    expect((screen.getByLabelText("Host (leave blank to use the selected client's own host)") as HTMLInputElement).value).toBe("");
    fireEvent.change(screen.getByLabelText("Client path"), { target: { value: "/a2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Remote path mapping updated"));
    expect(bodyOf(callTo("PUT", "/api/download-clients/c1/path-mappings/m3"))).toMatchObject({ remotePath: "/a2", priority: 10 });
    expect(screen.getByRole("button", { name: "Add mapping" })).toBeTruthy();
  });

  it("a mapping that the server refuses says why and keeps the typed text", async () => {
    withClients();
    authedFetch.mockImplementation(async () => ok({ title: "That mapping already exists." }, 400));
    show("/indexers/path-mappings");
    fireEvent.change(screen.getByLabelText("Client path"), { target: { value: "/p" } });
    fireEvent.change(screen.getByLabelText("Deluno path"), { target: { value: "E:\\p" } });
    fireEvent.click(screen.getByRole("button", { name: "Add mapping" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That mapping already exists."));
    expect((screen.getByLabelText("Client path") as HTMLInputElement).value).toBe("/p");
  });

  it("tests a mapping and reports whether it could be reached, removes it, and clears an edit of the mapping that was removed", async () => {
    withClients();
    let reachable = true;
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (init?.method === "DELETE" ? new Response(null, { status: 204 }) : String(url).endsWith("/test") ? ok({ reachable, message: reachable ? "Found it." : "Not found." }) : ok({})));
    show("/indexers/path-mappings");
    fireEvent.click(within(rowOf("/z")).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Found it."));
    reachable = false;
    fireEvent.click(within(rowOf("/z")).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Not found."));
    expect(callTo("POST", "/api/download-clients/c2/path-mappings/m2/test")).toBeTruthy();

    fireEvent.click(within(rowOf("/a")).getByRole("button", { name: "Edit" }));
    fireEvent.click(within(rowOf("/z")).getByRole("button", { name: "Remove the gone.example mapping" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Remote path mapping removed"));
    expect((screen.getByLabelText("Client path") as HTMLInputElement).value).toBe("/a");

    fireEvent.click(within(rowOf("/a")).getByRole("button", { name: "Remove the /a mapping" }));
    await waitFor(() => expect(calls("DELETE")).toHaveLength(2));
    expect((screen.getByLabelText("Client path") as HTMLInputElement).value).toBe("");
  });

  it("says why a test could not run", async () => {
    withClients();
    authedFetch.mockImplementation(async () => ok({}, 500));
    show("/indexers/path-mappings");
    fireEvent.click(within(rowOf("/z")).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Deluno could not test this file location."));
  });
});

describe("a download client's own file-location links", () => {
  it("are linked, tested and removed from its drawer, and a link with a blank side is not sent", async () => {
    reset({ clients: [client()], pathMappings: [mapping()] });
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (init?.method === "DELETE" ? new Response(null, { status: 204 }) : String(url).endsWith("/test") ? ok({ reachable: true, message: "Reachable." }) : ok({})));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByText("Advanced path mapping"));
    fireEvent.click(within(drawer.getByTitle("/downloads/complete").parentElement!).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Reachable."));
    fireEvent.change(drawer.getByLabelText("Path reported by My qBittorrent"), { target: { value: "/x" } });
    fireEvent.change(drawer.getByLabelText("Same path as Deluno sees it"), { target: { value: "E:\\x" } });
    fireEvent.click(drawer.getByRole("button", { name: "Link paths" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("File location linked"));
    expect(bodyOf(callTo("POST", "/api/download-clients/c1/path-mappings"))).toMatchObject({ remotePath: "/x", localPath: "E:\\x", priority: 10 });
    fireEvent.click(drawer.getByRole("button", { name: "Remove /downloads/complete link" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("File location link removed"));
    expect(callTo("DELETE", "/api/download-clients/c1/path-mappings/m1")).toBeTruthy();
  });

  it("a link the server refuses says why and keeps the typed paths", async () => {
    reset({ clients: [client()] });
    authedFetch.mockImplementation(async () => ok({ title: "Already linked." }, 400));
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByText("Advanced path mapping"));
    fireEvent.change(drawer.getByLabelText("Path reported by My qBittorrent"), { target: { value: "/x" } });
    fireEvent.change(drawer.getByLabelText("Same path as Deluno sees it"), { target: { value: "E:\\x" } });
    fireEvent.click(drawer.getByRole("button", { name: "Link paths" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Already linked."));
    expect((drawer.getByLabelText("Path reported by My qBittorrent") as HTMLInputElement).value).toBe("/x");
  });
});

describe("the routing links and checks", () => {
  const routed = { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1, requiredTags: "hd", excludedTags: "cam" }], downloadClients: [{ downloadClientId: "c1", downloadClientName: "My qBittorrent", priority: 1, category: "films" }] };
  const base = () => {
    reset({ libraries: [films], indexers: [indexer()], clients: [client()], routing: [routed] });
    chainClear();
  };

  it("a link that names a library opens its routing once and then drops the name from the address", async () => {
    base();
    show("/indexers/library-routing?libraryId=l1");
    expect(await screen.findByRole("dialog")).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/indexers/library-routing"));
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
  });

  it("a link that names a library that is not there, or none, opens nothing", async () => {
    base();
    show("/indexers/library-routing?libraryId=nope");
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/indexers/library-routing?libraryId=nope"));
    expect(screen.queryByRole("dialog")).toBeNull();
    cleanup();
    show("/indexers/library-routing");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("sorts the libraries by each column", () => {
    reset({
      libraries: [films, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "D:\\Shows" }, { id: "l3", name: "Anime", mediaType: "tv", rootPath: "D:\\Anime" }],
      indexers: [indexer()], clients: [client()],
      routing: [routed,
        { libraryId: "l2", libraryName: "Shows", sources: [{ indexerId: "i1", indexerName: "Alpha", priority: 1 }, { indexerId: "i2", indexerName: "Bravo", priority: 2 }], downloadClients: [] },
        { libraryId: "l3", libraryName: "Anime", sources: [], downloadClients: [{ downloadClientId: "c1", downloadClientName: "My qBittorrent", priority: 1 }, { downloadClientId: "c2", downloadClientName: "Other", priority: 2 }] }]
    });
    chainClear();
    show("/indexers/library-routing");
    const names = () => rowNames(/Films|Shows|Anime/);
    for (const label of ["Name", "Indexers", "Download clients", "Status"]) {
      press(label);
      const ascending = names();
      expect(new Set(ascending).size).toBe(3);
      press(label);
      expect(names()).toEqual([...ascending].reverse());
    }
    expect(cellOf("Films", 1)).toContain("1 of 1");
    expect(cellOf("Films", 2)).toContain("My qBittorrent · films");
    expect(cellOf("Anime", 1)).toContain("Deluno can't search for this library");
  });

  it("saves the routing with the tags and category as typed, and settles the drawer on what was saved", async () => {
    base();
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.change(await drawer.findByLabelText("Optional category sent to the download client"), { target: { value: "  kids  " } });
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("Routing saved")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/libraries/l1/routing"))).toEqual({
      sources: [{ indexerId: "i1", priority: 1, requiredTags: "hd", excludedTags: "cam" }],
      downloadClients: [{ downloadClientId: "c1", priority: 1, category: "kids" }]
    });
  });

  it("saves a client with no category as the client's own default, and a source added with no tags as untagged", async () => {
    chainClear();
    reset({ libraries: [films], indexers: [indexer(), indexer({ id: "i2", name: "Bravo" })], clients: [client()], routing: [{ libraryId: "l1", libraryName: "Films", sources: [], downloadClients: [{ downloadClientId: "c1", downloadClientName: "My qBittorrent", priority: 1 }] }] });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.click(await drawer.findByRole("switch", { name: /Bravo/ }));
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("Routing saved")).toBeTruthy();
    expect(bodyOf(callTo("PUT", "/api/libraries/l1/routing"))).toEqual({
      sources: [{ indexerId: "i2", priority: 1, requiredTags: "", excludedTags: "" }],
      downloadClients: [{ downloadClientId: "c1", priority: 1, category: null }]
    });
  });

  it("says what the server refused when saving the routing", async () => {
    base();
    authedFetch.mockImplementation(async () => ok({ title: "That routing is not allowed." }, 400));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.change(await drawer.findByLabelText("Optional category sent to the download client"), { target: { value: "kids" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save routing" }));
    expect(await drawer.findByText("That routing is not allowed.")).toBeTruthy();
  });

  it("checks a category with the client, and a category that is missing or unreachable is an error while an unknown answer is quiet", async () => {
    base();
    const answers = [{ status: "ready", message: "It is ready." }, { status: "missing", message: "It is missing." }, { status: "unreachable", message: "Cannot reach it." }, { status: "unknown", message: "Not sure." }];
    authedFetch.mockImplementation(async () => ok(answers.shift()));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    await drawer.findByLabelText("Optional category sent to the download client");
    const check = () => fireEvent.click(drawer.getByRole("button", { name: "Check category" }));
    check();
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("It is ready."));
    expect(callTo("POST", "/api/download-clients/c1/categories/check")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/download-clients/c1/categories/check"))).toEqual({ category: "films" });
    check();
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("It is missing."));
    check();
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Cannot reach it."));
    check();
    await waitFor(() => expect(answers).toHaveLength(0));
    expect(toast.error).toHaveBeenCalledTimes(2);
    expect(toast.success).toHaveBeenCalledTimes(1);
  });

  it("a category check that fails says why, and an empty category is not sent for checking", async () => {
    base();
    authedFetch.mockImplementation(async () => ok({ title: "The client did not answer." }, 400));
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    const box = await drawer.findByLabelText("Optional category sent to the download client");
    fireEvent.click(drawer.getByRole("button", { name: "Check category" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The client did not answer."));
    fireEvent.change(box, { target: { value: "   " } });
    authedFetch.mockClear();
    const check = drawer.queryByRole("button", { name: "Check category" });
    if (check) fireEvent.click(check);
    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("closing a routing drawer with edits asks first, and Discard closes it", async () => {
    base();
    show("/indexers/library-routing");
    fireEvent.click(screen.getByText("Films"));
    const drawer = await dialog();
    fireEvent.change(await drawer.findByLabelText("Optional category sent to the download client"), { target: { value: "kids" } });
    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("an installed app that Deluno could not sign in to", () => {
  const found = { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 8080 };
  const running = { kind: "nzbget", displayName: "NZBGet", category: "download-client", installed: false, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 6789 };

  it("opens a new client with the host, port and name filled in, tests it by itself, and shows the answer", async () => {
    reset();
    fetchJson.mockImplementation(async (url: string) => (url === "/api/system/bundled-components" ? [running] : []));
    authedFetch.mockImplementation(async () => ok({ healthStatus: "healthy", message: "Reached NZBGet." }));
    show("/indexers/download-clients");
    fireEvent.click(await screen.findByRole("button", { name: "Use NZBGet (localhost:6789)" }));
    const drawer = await dialog();
    expect(await drawer.findByText("Reached NZBGet.")).toBeTruthy();
    expect(callTo("POST", "/api/download-clients/test")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/download-clients/test"))).toMatchObject({ name: "NZBGet", host: "localhost", port: 6789, protocol: "nzbget" });
  });

  it("when Connect needs a login, opens the client with the address the app answered on and does not test it", async () => {
    reset();
    fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/connect") ? { name: "qBittorrent", healthy: false, message: "Enter your own login.", needsSignIn: true, host: "192.168.1.10", port: 8081 } : url === "/api/system/bundled-components" ? [found] : []));
    show("/indexers/download-clients");
    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));
    const drawer = await dialog();
    expect(drawer.getByDisplayValue("8081")).toBeTruthy();
    expect(drawer.getByDisplayValue("192.168.1.10")).toBeTruthy();
    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("when Connect needs a login but names no address, the app's own default is used", async () => {
    reset();
    fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/connect") ? { name: "qBittorrent", healthy: false, message: "Enter your own login.", needsSignIn: true } : url === "/api/system/bundled-components" ? [found] : []));
    show("/indexers/download-clients");
    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));
    const drawer = await dialog();
    expect((drawer.getAllByRole("combobox").find((box) => (box as HTMLSelectElement).value === "8080")) ?? null).not.toBeNull();
  });

  it("an app connected from here is read into the list again", async () => {
    reset();
    fetchJson.mockImplementation(async (url: string) => (String(url).endsWith("/connect") ? { name: "qBittorrent", healthy: true, message: "Connected." } : url === "/api/system/bundled-components" ? [found] : []));
    show("/indexers/download-clients");
    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));
    expect(await screen.findByText("Connected.")).toBeTruthy();
  });
});
