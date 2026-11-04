import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { IndexersPage } from "./connections-screen";
import { toast } from "../components/shell/toaster";

// #1329: the rest of Find and Download: adding and testing an indexer, its health buttons, the empty tabs, sorting, and closing a drawer.

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
const proxy = (over: Record<string, unknown> = {}) => ({
  id: "p1", name: "HTTP proxy", proxyType: "http", address: "http://proxy.example:3128", username: null, hasPassword: false, isEnabled: true,
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", assignedIndexerNames: [], ...over
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

function show(path: string) {
  const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: [path] });
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);
}
const calls = (method: string) => authedFetch.mock.calls.filter(([, init]) => (init as RequestInit).method === method);
const callTo = (method: string, url: string) => calls(method).find(([target]) => target === url)!;
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));
const dialog = async () => within(await screen.findByRole("dialog"));
const rowNames = (pattern: RegExp) => screen.getAllByRole("row").map((row) => pattern.exec(row.textContent ?? "")?.[0]).filter(Boolean);
const press = (label: string) => fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));

afterEach(() => { cleanup(); fetchJson.mockReset(); fetchJson.mockImplementation(async () => []); authedFetch.mockReset(); vi.mocked(toast.success).mockClear(); vi.mocked(toast.error).mockClear(); });

async function openNewIndexer() {
  show("/indexers/indexers");
  fireEvent.click(screen.getAllByRole("button", { name: "New indexer" })[0]);
  const drawer = await dialog();
  fireEvent.change(drawer.getByPlaceholderText("https://api.nzbgeek.info"), { target: { value: "https://x.example/api" } });
  fireEvent.change(drawer.getByPlaceholderText("Paste your API key"), { target: { value: "secret" } });
  return drawer;
}
const added = { id: "i9", name: "x", baseUrl: "https://x.example/api", protocol: "newznab", priority: 10, categories: "", mediaScope: "both", isEnabled: true };

describe("testing an indexer that is not saved yet", () => {
  it("fills in the categories and where it is used from what the indexer says it offers, and sends that answer with the add", async () => {
    reset();
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? ok({ healthStatus: "healthy", message: "Reached it.", failureCategory: "none", latencyMs: 40, caps: { categories: "2000,5000", mediaScope: "tv" } }) : ok({ ...added, categories: "2000,5000", mediaScope: "tv" })));
    const drawer = await openNewIndexer();
    fireEvent.click(drawer.getByRole("button", { name: /Test/ }));
    expect(await drawer.findByText("Reached it.")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/indexers/test"))).toMatchObject({ baseUrl: "https://x.example/api", apiKey: "secret" });
    fireEvent.click(drawer.getByRole("button", { name: "Add indexer" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(2));
    expect(bodyOf(callTo("POST", "/api/indexers"))).toMatchObject({ categories: "2000,5000", mediaScope: "tv", routeToLibraries: true, testedHealth: { healthStatus: "healthy", message: "Reached it.", failureCategory: "none", latencyMs: 40 } });
  });

  it("keeps its own categories and use when the answer names none, and an answer with nothing in it reads as unknown", async () => {
    reset();
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? ok({ caps: { categories: null, mediaScope: null } }) : ok(added)));
    const drawer = await openNewIndexer();
    const before = (drawer.getAllByRole("textbox") as HTMLInputElement[]).map((box) => box.value);
    fireEvent.click(drawer.getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(calls("POST")).toHaveLength(1));
    await waitFor(() => expect((drawer.getByRole("button", { name: /Test/ }) as HTMLButtonElement).disabled).toBe(false));
    expect((drawer.getAllByRole("textbox") as HTMLInputElement[]).map((box) => box.value)).toEqual(before);
    fireEvent.click(drawer.getByRole("button", { name: "Add indexer" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(2));
    expect(bodyOf(callTo("POST", "/api/indexers")).testedHealth).toEqual({ healthStatus: "unknown", message: "", failureCategory: null, latencyMs: null });
  });

  it("an answer without caps leaves the form alone, and the test is dropped once the form was changed after it", async () => {
    reset();
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? ok({ healthStatus: "healthy", lastHealthMessage: "Fine." }) : ok(added)));
    const drawer = await openNewIndexer();
    fireEvent.click(drawer.getByRole("button", { name: /Test/ }));
    expect(await drawer.findByText("Fine.")).toBeTruthy();
    fireEvent.change(drawer.getByPlaceholderText("https://api.nzbgeek.info"), { target: { value: "https://other.example/api" } });
    fireEvent.click(drawer.getByRole("button", { name: "Add indexer" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(2));
    expect(bodyOf(callTo("POST", "/api/indexers")).testedHealth).toBeUndefined();
  });

  it("asks for what is missing instead of testing", async () => {
    reset();
    show("/indexers/indexers");
    fireEvent.click(screen.getAllByRole("button", { name: "New indexer" })[0]);
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: /Test/ }));
    expect(await drawer.findByText("Enter the indexer URL.")).toBeTruthy();
    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("a test that fails for a reason that is not a message still says it failed", async () => {
    reset();
    authedFetch.mockImplementation(async () => { throw "offline"; });
    const drawer = await openNewIndexer();
    fireEvent.click(drawer.getByRole("button", { name: /Test/ }));
    expect(await drawer.findByText("Test failed.")).toBeTruthy();
  });
});

describe("testing a saved item after an edit", () => {
  it("an indexer is tried as a draft that names the saved one, and a client the same", async () => {
    reset({ indexers: [indexer()], clients: [client()] });
    authedFetch.mockImplementation(async () => ok({ healthStatus: "healthy", message: "Draft fine." }));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    let drawer = await dialog();
    fireEvent.change(drawer.getByDisplayValue("https://alpha.example/api"), { target: { value: "https://beta.example/api" } });
    fireEvent.click(drawer.getByRole("button", { name: /^Test/ }));
    expect(await drawer.findByText("Draft fine.")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/indexers/test?id=i1"))).toMatchObject({ baseUrl: "https://beta.example/api" });
    expect(calls("POST")).toHaveLength(1);

    cleanup();
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    drawer = await dialog();
    fireEvent.change(drawer.getAllByRole("combobox").find((box) => (box as HTMLSelectElement).value === "8080")!, { target: { value: "9091" } });
    fireEvent.click(drawer.getByRole("button", { name: /^Test/ }));
    await waitFor(() => expect(callTo("POST", "/api/download-clients/test?id=c1")).toBeTruthy());
    expect(bodyOf(callTo("POST", "/api/download-clients/test?id=c1"))).toMatchObject({ port: 9091 });
  });
});

describe("adding an indexer or a client", () => {
  it("says which libraries now search an indexer, or just that it was added when none does", async () => {
    reset({ libraries: [films] });
    fetchJson.mockImplementation(async (url: string) => (url === "/api/libraries/l1/routing" ? { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "i9", indexerName: "x", priority: 1 }], downloadClients: [] } : []));
    authedFetch.mockImplementation(async () => ok(added, 201));
    let drawer = await openNewIndexer();
    fireEvent.click(drawer.getByRole("button", { name: "Add indexer" }));
    expect(await drawer.findByText("Indexer added and switched on for Films")).toBeTruthy();

    cleanup();
    fetchJson.mockImplementation(async (url: string) => { if (url === "/api/libraries/l1/routing") throw new Error("down"); return []; });
    drawer = await openNewIndexer();
    fireEvent.click(drawer.getByRole("button", { name: "Add indexer" }));
    expect(await drawer.findByText("Indexer added")).toBeTruthy();
  });

  it("does not look at the libraries when the person chose to route an indexer themselves", async () => {
    reset({ libraries: [films] });
    authedFetch.mockImplementation(async () => ok(added, 201));
    const drawer = await openNewIndexer();
    fireEvent.click(drawer.getByRole("switch", { name: /Search it in my libraries/ }));
    fireEvent.click(drawer.getByRole("button", { name: "Add indexer" }));
    expect(await drawer.findByText("Indexer added")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/indexers")).routeToLibraries).toBe(false);
    expect(fetchJson).not.toHaveBeenCalledWith("/api/libraries/l1/routing");
  });

  it("a client the person chose to route themselves is added without asking the libraries", async () => {
    reset({ libraries: [films] });
    authedFetch.mockImplementation(async () => ok(client({ id: "c9" }), 201));
    show("/indexers/download-clients");
    fireEvent.click(screen.getAllByRole("button", { name: /New client|Add client/ })[0]);
    const drawer = await dialog();
    fireEvent.click(drawer.getAllByRole("switch").find((box) => /librar/i.test(box.getAttribute("aria-label") ?? box.parentElement?.textContent ?? ""))!);
    fireEvent.click(drawer.getByRole("button", { name: "Add client" }));
    expect(await drawer.findByText("Client added")).toBeTruthy();
    expect(bodyOf(callTo("POST", "/api/download-clients")).routeToLibraries).toBe(false);
  });
});

describe("an indexer's health buttons", () => {
  it("Reset failures clears the circuit and says so, and a refusal says why", async () => {
    reset({ indexers: [indexer({ consecutiveFailures: 3, healthStatus: "unhealthy" })] });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: "Reset failures" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Circuit reset for Alpha"));
    expect(callTo("POST", "/api/indexers/i1/reset-circuit")).toBeTruthy();
    authedFetch.mockImplementation(async () => ok({}, 500));
    fireEvent.click(drawer.getByRole("button", { name: "Reset failures" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Could not reset."));
  });

  it("Acknowledge on a VIP warning says it was acknowledged, and a refusal says why", async () => {
    reset({ indexers: [indexer({ vipExpiresUtc: new Date(Date.now() + 2 * 86_400_000).toISOString() })] });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: "Acknowledge" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("VIP warning acknowledged for Alpha"));
    expect(bodyOf(callTo("PUT", "/api/indexers/i1"))).toEqual({ acknowledgeVipExpiry: true });
    authedFetch.mockImplementation(async () => ok({}, 500));
    fireEvent.click(drawer.getByRole("button", { name: "Acknowledge" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Could not acknowledge."));
  });

  it("shows how the host is being paced when the indexer's host is waiting", async () => {
    reset({ indexers: [indexer()], outboundThrottle: { hosts: [{ host: "alpha.example", waiting: 2, grantedCount: 1, refusedCount: 0, totalWaitedSeconds: 1, nextPermitInSeconds: 3 }] } });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    expect(drawer.getByText("Deluno is waiting on alpha.example before sending 2 requests.")).toBeTruthy();
  });
});

describe("the switches around an open drawer", () => {
  it("a switch on another row, or on this row while its drawer has unsaved edits, leaves the drawer's form as it is", async () => {
    reset({ indexers: [indexer(), indexer({ id: "i2", name: "Bravo", baseUrl: "https://bravo.example/api" })], clients: [client(), client({ id: "c2", name: "Other" })] });
    authedFetch.mockImplementation(async () => ok({}));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.click(screen.getAllByRole("switch", { name: "Pause Bravo", hidden: true })[0]);
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    fireEvent.change(drawer.getByDisplayValue("https://alpha.example/api"), { target: { value: "https://edited.example/api" } });
    fireEvent.click(screen.getAllByRole("switch", { name: "Pause Alpha", hidden: true })[0]);
    await waitFor(() => expect(calls("PUT")).toHaveLength(2));
    expect(drawer.getByDisplayValue("https://edited.example/api")).toBeTruthy();

    cleanup();
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    const clientDrawer = await dialog();
    fireEvent.click(screen.getAllByRole("switch", { name: "Pause Other", hidden: true })[0]);
    await waitFor(() => expect(calls("PUT")).toHaveLength(3));
    const port = () => clientDrawer.getAllByRole("combobox").find((box) => ["8080", "9091"].includes((box as HTMLSelectElement).value)) as HTMLSelectElement;
    fireEvent.change(port(), { target: { value: "9091" } });
    fireEvent.click(screen.getAllByRole("switch", { name: "Pause My qBittorrent", hidden: true })[0]);
    await waitFor(() => expect(calls("PUT")).toHaveLength(4));
    expect(port().value).toBe("9091");
  });
});

describe("the empty tabs and their buttons", () => {
  it("each empty tab offers its own New button, which opens a blank drawer", async () => {
    reset();
    for (const [path, title, label] of [["/indexers/indexers", "No indexers yet", "New indexer"], ["/indexers/download-clients", "No download client yet", "New client"], ["/indexers/proxies", "No proxies yet", "New proxy"]] as const) {
      show(path);
      const empty = screen.getByText(title).parentElement!.parentElement!;
      fireEvent.click(within(empty).getByRole("button", { name: label }));
      expect(await screen.findByRole("dialog")).toBeTruthy();
      cleanup();
    }
  });
});

describe("sorting the indexer and proxy lists", () => {
  it("sorts indexers by Name, Last test, Status and On, each reversed by a second press", () => {
    const at = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
    reset({
      indexers: [
        indexer({ id: "a", name: "Aa", lastHealthTestUtc: at(30), isEnabled: false }),
        indexer({ id: "b", name: "Bb", lastHealthTestUtc: at(10), healthStatus: "unhealthy" }),
        indexer({ id: "c", name: "Cc", lastHealthTestUtc: at(20) })
      ]
    });
    show("/indexers/indexers");
    const names = () => rowNames(/Aa|Bb|Cc/);
    press("Name");
    expect(names()).toEqual(["Aa", "Bb", "Cc"]);
    press("Name");
    expect(names()).toEqual(["Cc", "Bb", "Aa"]);
    press("Last test");
    const byTest = names();
    press("Last test");
    expect(names()).toEqual([...byTest].reverse());
    expect(new Set(byTest).size).toBe(3);
    press("Status");
    const byStatus = names();
    press("Status");
    expect(names()).toEqual([...byStatus].reverse());
    press("On");
    expect(names()[0]).toBe("Aa");
    press("On");
    expect(names()[2]).toBe("Aa");
  });

  it("selecting indexers puts a select-all box in the header", () => {
    reset({ indexers: [indexer({ id: "a", name: "Aa" }), indexer({ id: "b", name: "Bb" })] });
    show("/indexers/indexers");
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    fireEvent.click(screen.getByRole("checkbox", { name: /Select all/ }));
    expect(screen.getByText("2 selected")).toBeTruthy();
  });

  it("sorts proxies by Name, Type, Used by and On, and lists what each is used by", () => {
    reset({
      proxies: [
        proxy({ id: "a", name: "Aa", proxyType: "socks5", assignedIndexerNames: ["X", "Y"], isEnabled: false }),
        proxy({ id: "b", name: "Bb", proxyType: "http", assignedIndexerNames: [] }),
        proxy({ id: "c", name: "Cc", proxyType: "weird", assignedIndexerNames: ["Z"] })
      ]
    });
    show("/indexers/proxies");
    const names = () => rowNames(/Aa|Bb|Cc/);
    const order: Record<string, string[]> = { Name: ["Aa", "Bb", "Cc"], Type: ["Bb", "Aa", "Cc"], "Used by": ["Bb", "Cc", "Aa"] };
    for (const [label, expected] of Object.entries(order)) {
      press(label);
      expect(names()).toEqual(expected);
      press(label);
      expect(names()).toEqual([...expected].reverse());
    }
    press("On");
    expect(names()[0]).toBe("Aa");
    press("On");
    expect(names()[2]).toBe("Aa");
    const row = (name: string) => screen.getAllByRole("row").find((candidate) => candidate.textContent?.includes(name))!;
    expect(row("Aa").textContent).toContain("2 indexers");
    expect(row("Aa").textContent).toContain("X, Y");
    expect(row("Aa").textContent).toContain("SOCKS5");
    expect(row("Aa").textContent).toContain("Off");
    expect(row("Bb").textContent).toContain("None");
    expect(row("Cc").textContent).toContain("weird");
    expect(row("Cc").textContent).toContain("On");
  });
});

describe("a drawer's title and closing", () => {
  it("names a proxy by its type and address, and a client by its protocol and address", async () => {
    reset({ proxies: [proxy({ proxyType: "weird" })], clients: [client({ endpointUrl: "http://localhost:8080" })] });
    show("/indexers/proxies");
    fireEvent.click(screen.getByText("HTTP proxy"));
    expect(await screen.findByText("weird · http://proxy.example:3128")).toBeTruthy();
    cleanup();
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText("My qBittorrent"));
    expect(await screen.findByText(/qBittorrent · http:\/\/localhost:8080/)).toBeTruthy();
  });

  it("Escape closes a drawer nobody changed, and asks first about one that was changed", async () => {
    reset({ indexers: [indexer()] });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.keyDown(drawer.getByRole("button", { name: "Cancel" }), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByText("Alpha"));
    const again = await dialog();
    fireEvent.change(again.getByDisplayValue("https://alpha.example/api"), { target: { value: "https://edited.example/api" } });
    fireEvent.keyDown(again.getByRole("button", { name: "Cancel" }), { key: "Escape" });
    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();
  });
});

describe("removing an item from its drawer", () => {
  it("removes an indexer after the question, and says what the server refused when removal fails", async () => {
    reset({ indexers: [indexer()] });
    authedFetch.mockImplementation(async () => ok({ title: "It is still in use." }, 400));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const drawer = await dialog();
    fireEvent.click(drawer.getByRole("button", { name: /^Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove indexer" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("It is still in use."));
    // #1477: a failed Remove keeps the drawer open, with the reason on screen.
    await waitFor(() => expect(screen.queryByRole("button", { name: "Remove indexer" })).toBeNull());
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByText("It is still in use.")).toBeTruthy();
    authedFetch.mockImplementation(async () => new Response(null, { status: 204 }));
    fireEvent.click((await dialog()).getByRole("button", { name: /^Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove indexer" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Alpha removed"));
    expect(callTo("DELETE", "/api/indexers/i1")).toBeTruthy();
  });
});
