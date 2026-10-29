import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { IndexersPage } from "./connections-screen";

// #1329: the indexer and download-client drawers of Find & Download: field errors, the footer after Add and Save, discarding
// unsaved edits, the remove confirmation, and the health a saved connection shows.

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

describe("the indexer drawer, low rows (#1329)", () => {
  it("pressing Add with the address empty puts the error under its field, and typing in the field clears it", async () => {
    reset();
    show("/indexers/indexers");
    fireEvent.click(screen.getAllByRole("button", { name: "New indexer" })[0]);
    const dialog = await screen.findByRole("dialog");

    fireEvent.change(within(dialog).getByPlaceholderText("The indexer's name"), { target: { value: "Mine" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Add indexer" }));

    expect(await within(dialog).findByText("Enter the indexer URL.")).toBeTruthy();
    expect(authedFetch).not.toHaveBeenCalled();
    fireEvent.change(within(dialog).getByPlaceholderText("https://api.nzbgeek.info"), { target: { value: "https://x.example/api" } });
    await waitFor(() => expect(within(dialog).queryByText("Enter the indexer URL.")).toBeNull());
  });

  it("after Add the footer names the libraries the indexer was switched on for", async () => {
    reset({ libraries: [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "D:\\Films" }, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "D:\\Shows" }] });
    const created = indexer({ id: "new1", name: "Mine", baseUrl: "https://x.example/api" });
    authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/indexers" && init?.method === "POST" ? ok(created, 201) : ok({})));
    fetchJson.mockImplementation(async (url: string) => (url === "/api/libraries/l1/routing"
      ? { libraryId: "l1", libraryName: "Films", sources: [{ indexerId: "new1", indexerName: "Mine", priority: 1 }], downloadClients: [] }
      : url === "/api/libraries/l2/routing" ? { libraryId: "l2", libraryName: "Shows", sources: [], downloadClients: [] } : []));
    show("/indexers/indexers");
    fireEvent.click(screen.getAllByRole("button", { name: "New indexer" })[0]);
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByPlaceholderText("https://api.nzbgeek.info"), { target: { value: "https://x.example/api" } });
    fireEvent.change(within(dialog).getByPlaceholderText("Paste your API key"), { target: { value: "secret" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Add indexer" }));

    expect(await within(dialog).findByText("Indexer added and switched on for Films")).toBeTruthy();
  });

  it("the footer says Saved just now after a save, shows the server's error text when it fails, and Save is off while the request runs", async () => {
    reset({ indexers: [indexer()] });
    let release!: (response: Response) => void;
    authedFetch.mockImplementationOnce(() => new Promise<Response>((resolve) => { release = resolve; }));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByPlaceholderText("alpha.example"), { target: { value: "Alpha renamed" } });
    const save = within(dialog).getByRole("button", { name: "Save indexer" }) as HTMLButtonElement;
    expect(save.disabled).toBe(false);

    fireEvent.click(save);

    await waitFor(() => expect(save.disabled).toBe(true));
    expect(within(dialog).getByText("Saving…")).toBeTruthy();
    release(ok({}));
    expect(await within(dialog).findByText("Saved just now")).toBeTruthy();

    authedFetch.mockImplementationOnce(async () => ok({ title: "That address is already used by another indexer." }, 409));
    fireEvent.change(within(dialog).getByPlaceholderText("alpha.example"), { target: { value: "Alpha again" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save indexer" }));
    expect(await within(dialog).findByText("That address is already used by another indexer.")).toBeTruthy();
  });

  it("closing a drawer with unsaved edits asks Discard unsaved changes, and keep editing leaves it open while Discard closes it", async () => {
    reset({ indexers: [indexer()] });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByPlaceholderText("alpha.example"), { target: { value: "Edited" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();
    expect(screen.getByText("Your edits haven't been saved.")).toBeTruthy();
    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());
    expect(screen.getAllByRole("dialog").some((candidate) => candidate.textContent?.includes("Edited") || (candidate.querySelector("input") as HTMLInputElement | null)?.value === "Edited")).toBe(true);
    expect(screen.getAllByPlaceholderText("alpha.example").some((input) => (input as HTMLInputElement).value === "Edited")).toBe(true);

    fireEvent.click(within(screen.getAllByPlaceholderText("alpha.example")[0].closest('[role="dialog"]') as HTMLElement).getByRole("button", { name: "Cancel" }));
    fireEvent.click(await screen.findByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByPlaceholderText("alpha.example")).toBeNull());
  });

  it("cancelling the remove confirmation leaves the indexer in place and removing deletes it", async () => {
    reset({ indexers: [indexer()] });
    authedFetch.mockImplementation(async () => ok({}, 204));
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const dialog = await screen.findByRole("dialog");

    fireEvent.click(within(dialog).getByRole("button", { name: /^Remove/ }));
    expect(await screen.findByText("Remove “Alpha”?")).toBeTruthy();
    const cancels = screen.getAllByRole("button", { name: "Cancel" });
    fireEvent.click(cancels[cancels.length - 1]);

    await waitFor(() => expect(screen.queryByText("Remove “Alpha”?")).toBeNull());
    expect(authedFetch).not.toHaveBeenCalled();
    expect(screen.getByPlaceholderText("alpha.example")).toBeTruthy();
    expect(screen.getAllByText("Alpha").length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("button", { name: /^Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove indexer" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/indexers/i1", expect.objectContaining({ method: "DELETE" })));
  });

  it("a saved indexer's Health block shows the last test with its age and latency, the message, the cause and the failures in a row", async () => {
    reset({ indexers: [indexer({
      healthStatus: "failed", lastHealthMessage: "The site refused the key.", consecutiveFailures: 3,
      lastHealthFailure: { category: "authentication", summary: "The API key was refused.", nextAction: "Enter the key again.", nextEligibleAttemptUtc: new Date(Date.now() + 30 * 60_000).toISOString() }
    })] });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const dialog = await screen.findByRole("dialog");

    expect(within(dialog).getByText("Health")).toBeTruthy();
    expect(dialog.textContent).toContain("The site refused the key.");
    expect(dialog.textContent).toContain("87 ms");
    expect(dialog.textContent).toContain("Last test");
    expect(dialog.textContent).toContain("The API key was refused.");
    expect(dialog.textContent).toContain("Enter the key again.");
    expect(dialog.textContent).toMatch(/3 in a row/);
  });

  it("a reached cap shows in the drawer Health as Search limit reached with the day count and the time it resets", async () => {
    const resets = new Date(Date.now() + 3 * 3600_000);
    reset({ indexers: [indexer({ queryLimit: 100, limitsUnit: "day", queryLimitStatus: { allowed: false, limit: 100, count: 100, resetsAtUtc: resets.toISOString(), unit: "day" } })] });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const dialog = await screen.findByRole("dialog");

    expect(dialog.textContent).toMatch(/Search limit reached \(100 per day\)\. Resets at \d{1,2}:\d{2}/);
  });

  it("the Sharing question offers No, Yes it is strict and Set my own numbers, and states the rule it makes", async () => {
    reset({ indexers: [indexer({ protocol: "torznab" })] });
    show("/indexers/indexers");
    fireEvent.click(screen.getByText("Alpha"));
    const dialog = await screen.findByRole("dialog");

    expect(within(dialog).getByText("Does this site expect you to keep sharing?")).toBeTruthy();
    for (const label of ["No, my normal rule", "Yes, it is strict", "Set my own numbers"]) {
      expect(within(dialog).getByRole("radio", { name: label }), label).toBeTruthy();
    }
    fireEvent.click(within(dialog).getByRole("radio", { name: "Yes, it is strict" }));
    expect(within(dialog).getByRole("radio", { name: "Yes, it is strict" }).getAttribute("aria-checked")).toBe("true");
    expect(within(dialog).getByText(/^Deluno keeps sharing for .*, and until you have given back as much as you took. It never stops early on its own.$/)).toBeTruthy();
    expect(within(dialog).queryByText(/^Your normal rule:/)).toBeNull();
  });
});

describe("the download client drawer, low rows (#1329)", () => {
  const openClient = async (over: Record<string, unknown> = {}, extra: Record<string, unknown> = {}) => {
    reset({ clients: [client(over)], ...extra });
    show("/indexers/download-clients");
    fireEvent.click(screen.getByText(String(over.name ?? "My qBittorrent")));
    return await screen.findByRole("dialog");
  };

  it("a saved client with a protocol Deluno cannot send to shows it as such and leaves the picker open to change it", async () => {
    const dialog = await openClient({ protocol: "torrent" });

    const picker = within(dialog).getByLabelText("Client") as HTMLSelectElement;
    expect(picker.disabled).toBe(false);
    expect(picker.value).toBe("torrent");
    expect([...picker.options].some((option) => option.textContent === "torrent — Deluno cannot send to this")).toBe(true);
    expect([...picker.options].some((option) => option.textContent?.startsWith("qBittorrent"))).toBe(true);
  });

  it("the Movies and TV category fields say Same as movies, downloads will mix when the two are equal and nothing when they differ", async () => {
    const same = await openClient({ moviesCategory: "deluno", tvCategory: "deluno" });
    expect(within(same).getByText("Same as movies — downloads will mix in the client.")).toBeTruthy();
    cleanup();

    const different = await openClient({ moviesCategory: "movies", tvCategory: "tv" });
    expect(within(different).queryByText("Same as movies — downloads will mix in the client.")).toBeNull();
    expect(within(different).getByText("Should differ from the movies category.")).toBeTruthy();
    expect(within(different).getByText("Movies category")).toBeTruthy();
    expect(within(different).getByText("TV category")).toBeTruthy();
  });

  it("the Content blocklist sync line shows the last push of a saved qBittorrent client: succeeded, failed with the reason, or not synced yet", async () => {
    const synced = new Date(Date.now() - 10 * 60_000).toISOString();
    const succeeded = await openClient({}, { qbittorrentBlocklistSync: { c1: { clientId: "c1", blocklistHash: "h", succeeded: true, message: null, syncedUtc: synced } } });
    expect(within(succeeded).getByText("Content blocklist sync")).toBeTruthy();
    expect(within(succeeded).getByText("Last push succeeded.")).toBeTruthy();
    cleanup();

    const failed = await openClient({}, { qbittorrentBlocklistSync: { c1: { clientId: "c1", blocklistHash: "h", succeeded: false, message: "qBittorrent refused the login", syncedUtc: synced } } });
    expect(within(failed).getByText("Last push failed: qBittorrent refused the login.")).toBeTruthy();
    cleanup();

    const never = await openClient();
    expect(within(never).getByText(/^Not synced yet./)).toBeTruthy();
  });

  it("a saved client's Health section shows the last test with its age and latency, the message and the cause with its next action", async () => {
    const dialog = await openClient({
      healthStatus: "failed", lastHealthMessage: "qBittorrent refused the login.", lastHealthLatencyMs: 12,
      lastHealthFailure: { category: "authentication", summary: "The login was refused.", nextAction: "Enter the password again." }
    });

    expect(within(dialog).getByText("Health")).toBeTruthy();
    expect(dialog.textContent).toContain("qBittorrent refused the login.");
    expect(dialog.textContent).toContain("12 ms");
    expect(dialog.textContent).toContain("The login was refused.");
    expect(dialog.textContent).toContain("Enter the password again.");
    expect(dialog.textContent).toMatch(/4 min ago|4 minutes ago/);
  });

  it("the Enabled switch is in the drawer, and a client that is off reads Off in the list", async () => {
    reset({ clients: [client({ isEnabled: false, healthStatus: "healthy" })] });
    show("/indexers/download-clients");
    const row = screen.getAllByRole("row").find((candidate) => candidate.textContent?.includes("My qBittorrent"))!;
    expect(within(row).getByText(/^(Off|Paused)$/)).toBeTruthy();
    expect(within(row).getByRole("switch", { name: "Enable My qBittorrent" }).getAttribute("aria-checked")).toBe("false");

    fireEvent.click(screen.getByText("My qBittorrent"));
    const dialog = await screen.findByRole("dialog");
    const enabled = within(dialog).getByRole("switch", { name: /Enabled/ });
    expect(enabled.getAttribute("aria-checked")).toBe("false");
  });
});

describe("the Tags button of the indexers list, low rows (#1329)", () => {
  it("opens the tags dialog for the ticked indexers and, once applied, clears the selection", async () => {
    reset({ indexers: [indexer({ id: "a", name: "Alpha" }), indexer({ id: "b", name: "Bravo", baseUrl: "https://bravo.example/api" }), indexer({ id: "c", name: "Charlie", baseUrl: "https://charlie.example/api" })] });
    fetchJson.mockImplementation(async (url: string) => (url === "/api/tags" ? [{ id: "t1", name: "Kids", color: "blue" }] : []));
    authedFetch.mockImplementation(async () => ok({ updated: 2 }));
    show("/indexers/indexers");

    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Alpha" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Bravo" }));
    fireEvent.click(screen.getByRole("button", { name: "Change tags on the selected indexers" }));

    expect(await screen.findByText("Change tags: 2 selected indexers")).toBeTruthy();
    fireEvent.click(screen.getByRole("textbox", { name: "Tags to apply" }));
    fireEvent.click(await screen.findByRole("button", { name: "Add Kids to Tags to apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Add tags" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/tags/bulk-connections", expect.objectContaining({ method: "POST" })));
    const body = JSON.parse(String((authedFetch.mock.calls[0][1] as RequestInit).body));
    expect(body).toMatchObject({ kind: "indexer", tags: ["Kids"], mode: "add" });
    expect([...body.ids].sort()).toEqual(["a", "b"]);
    await waitFor(() => expect(screen.queryByText("Change tags: 2 selected indexers")).toBeNull());
    expect((screen.getByRole("checkbox", { name: "Select Alpha" }) as HTMLInputElement).getAttribute("aria-checked")).not.toBe("true");
    expect((screen.getByRole("checkbox", { name: "Select Bravo" }) as HTMLInputElement).getAttribute("aria-checked")).not.toBe("true");
  });
});

describe("an indexer link from Transfers (#1426)", () => {
  it("?indexer=<name> opens that indexer's own page once, whatever the case of the name, and leaves the address clean", async () => {
    reset({ indexers: [indexer(), indexer({ id: "i2", name: "Beta", baseUrl: "https://beta.example/api" })] });
    const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: ["/indexers/indexers?indexer=beta"] });
    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByDisplayValue("Beta")).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toBe(""));
    expect(router.state.location.pathname).toBe("/indexers/indexers");
    // The link is not left in the history for Back to open again.
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("opens the indexer of a second link followed while the page is already open", async () => {
    reset({ indexers: [indexer(), indexer({ id: "i2", name: "Beta", baseUrl: "https://beta.example/api" })] });
    const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: ["/indexers/indexers"] });
    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);
    expect(screen.queryByRole("dialog")).toBeNull();

    await router.navigate("/indexers/indexers?indexer=Alpha");

    expect(await within(await screen.findByRole("dialog")).findByDisplayValue("Alpha")).toBeInTheDocument();
  });

  it("a name no indexer has opens nothing and still clears the address", async () => {
    reset({ indexers: [indexer()] });
    const router = createMemoryRouter([{ path: "*", element: <IndexersPage /> }], { initialEntries: ["/indexers/indexers?indexer=Gone"] });
    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>);

    await waitFor(() => expect(router.state.location.search).toBe(""));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
