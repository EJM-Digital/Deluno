import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The workstation can be busy with other builds; a slow render is not a failed page.
configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The Search page's requests and grabs (coverage register, area search-grab): what it asks the server for in each mode, which
 * indexers it offers, and what it says when a search or a grab goes wrong. The page is rendered whole; only the network is answered.
 */
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch }));
const toast = vi.hoisted(() => Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast, realtimeToast: {}, Toaster: () => null }));

const { SearchPage } = await import("./search-page");

type Reply = { status?: number; body?: unknown };

const INDEXERS = [
  { id: "idx-1", name: "Enabled One", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid/1", priority: 1, categories: "2000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "movies" },
  { id: "idx-2", name: "Enabled Two", protocol: "newznab", privacy: "public", baseUrl: "https://fixture.invalid/2", priority: 2, categories: "5000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "tv" },
  { id: "idx-3", name: "Switched Off", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid/3", priority: 3, categories: "2000", tags: "", isEnabled: false, healthStatus: "healthy", mediaScope: "movies" }
];
const CATEGORIES = [{ value: 2000, label: "Movies" }, { value: 5000, label: "TV" }];

const row = (over: Record<string, unknown>) => ({
  releaseName: "Example.Release.WEB.1080p",
  indexerId: "idx-1",
  indexerName: "Enabled One",
  quality: "WEB 1080p",
  meetsCutoff: true,
  decisionStatus: "eligible",
  sizeBytes: 2_000_000_000,
  seeders: 12,
  leechers: 3,
  grabs: 4,
  indexerProtocol: "torznab",
  releaseAgeHours: 5,
  downloadUrl: "https://fixture.invalid/x.torrent",
  ...over
});

let searchReply: Reply = { body: { candidates: [row({})], indexersQueried: 2 } };
let grabReply: Reply = { body: { succeeded: true, downloadClientName: "qBittorrent" } };
let calls: Array<{ url: string; body: any }> = [];

beforeEach(() => {
  searchReply = { body: { candidates: [row({})], indexersQueried: 2 } };
  grabReply = { body: { succeeded: true, downloadClientName: "qBittorrent" } };
  calls = [];
  authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const reply: Reply = url.startsWith("/api/search/grab") ? grabReply : url.startsWith("/api/search/categories") ? { body: CATEGORIES } : url.startsWith("/api/search") ? searchReply : url.startsWith("/api/indexers") ? { body: INDEXERS } : { body: {} };
    return new Response(JSON.stringify(reply.body ?? {}), { status: reply.status ?? 200, headers: { "Content-Type": "application/json" } });
  });
});
afterEach(() => {
  authedFetch.mockReset();
  vi.clearAllMocks();
});

const searchBody = () => calls.find((c) => c.url === "/api/search")!.body;
const grabBody = () => calls.find((c) => c.url === "/api/search/grab")!.body;

async function renderPage() {
  render(
    <MemoryRouter>
      <SearchPage />
    </MemoryRouter>
  );
  await screen.findByRole("checkbox", { name: "Enabled One" });
}

async function search(text = "Example") {
  fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: text } });
  fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
}

describe("what the Search page asks for", () => {
  it("pressing Enter in the query box starts the search", async () => {
    await renderPage();
    const box = screen.getByPlaceholderText("Search for a release…");
    fireEvent.change(box, { target: { value: "Example" } });

    fireEvent.keyDown(box, { key: "Enter" });

    await waitFor(() => expect(searchBody()).toMatchObject({ query: "Example" }));
  });

  it("sends tv for a TV search and basic for a Basic search", async () => {
    await renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    await search();
    await waitFor(() => expect(searchBody().searchType).toBe("tv"));

    calls = [];
    fireEvent.click(screen.getByRole("radio", { name: "Basic" }));
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await waitFor(() => expect(searchBody().searchType).toBe("basic"));
  });

  it("offers only the enabled indexers, and a disabled one is not a checkbox", async () => {
    await renderPage();

    expect(screen.getByRole("checkbox", { name: "Enabled One" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Enabled Two" })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Switched Off" })).toBeNull();
  });

  it("ticking an indexer sends its id, and ticking none sends null for every enabled indexer", async () => {
    await renderPage();
    await search();
    await waitFor(() => expect(searchBody().indexerIds).toBeNull());

    calls = [];
    fireEvent.click(screen.getByRole("checkbox", { name: "Enabled Two" }));
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await waitFor(() => expect(searchBody().indexerIds).toEqual(["idx-2"]));
  });

  it("tells the person when the search reached no enabled indexer", async () => {
    searchReply = { body: { candidates: [], indexersQueried: 0 } };
    await renderPage();

    await search();

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("No enabled indexer covers this search. Check Find & Download -> Indexers."));
  });

  it("a search that fails shows the server's sentence and an empty result, and Search works again", async () => {
    searchReply = { status: 400, body: { message: "The indexer said no." } };
    await renderPage();

    await search();

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(String(toast.error.mock.calls[0][0])).toContain("The indexer said no.");
    expect(await screen.findByText("No releases found")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Search$/ })).toBeEnabled();
  });

  it("a search with no releases says No releases found and what to try", async () => {
    searchReply = { body: { candidates: [], indexersQueried: 2 } };
    await renderPage();

    await search();

    expect(await screen.findByText("No releases found")).toBeInTheDocument();
    expect(screen.getByText(/Try a different query, or check that an indexer covering this type is enabled\./)).toBeInTheDocument();
  });

  it("refuses an empty query with a toast and asks the server nothing", async () => {
    await renderPage();

    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));

    expect(toast.error).toHaveBeenCalledWith("Type something to search for.");
    expect(calls.some((c) => c.url === "/api/search")).toBe(false);
  });
});

describe("the Search page's result filters", () => {
  const mixed = {
    candidates: [
      row({ releaseName: "Example.Torrent.One", indexerName: "Enabled One", indexerId: "idx-1", indexerProtocol: "torznab" }),
      row({ releaseName: "Example.Usenet.Two", indexerName: "Enabled Two", indexerId: "idx-2", indexerProtocol: "newznab", seeders: null, leechers: null })
    ],
    indexersQueried: 2
  };

  it("offers a protocol filter only when two protocols are in the results, and choosing Usenet leaves only those rows", async () => {
    searchReply = { body: mixed };
    await renderPage();
    await search();
    await screen.findByText("Example.Torrent.One");

    fireEvent.click(screen.getByRole("combobox", { name: "Filter by protocol" }));
    fireEvent.click(await screen.findByRole("option", { name: "Usenet" }));

    await waitFor(() => expect(screen.queryByText("Example.Torrent.One")).toBeNull());
    expect(screen.getByText("Example.Usenet.Two")).toBeInTheDocument();
  });

  it("offers neither filter when the results hold one protocol and one indexer", async () => {
    await renderPage();
    await search();
    await screen.findByText("Example.Release.WEB.1080p");

    expect(screen.queryByRole("combobox", { name: "Filter by protocol" })).toBeNull();
    expect(screen.queryByRole("combobox", { name: "Filter by indexer" })).toBeNull();
  });

  it("filtering by indexer leaves only that indexer's rows", async () => {
    searchReply = { body: mixed };
    await renderPage();
    await search();
    await screen.findByText("Example.Torrent.One");

    fireEvent.click(screen.getByRole("combobox", { name: "Filter by indexer" }));
    fireEvent.click(await screen.findByRole("option", { name: "Enabled Two" }));

    await waitFor(() => expect(screen.queryByText("Example.Torrent.One")).toBeNull());
    expect(screen.getByText("Example.Usenet.Two")).toBeInTheDocument();
  });

  it("a release with no download link has a disabled Grab button", async () => {
    searchReply = { body: { candidates: [row({ downloadUrl: null })], indexersQueried: 1 } };
    await renderPage();
    await search();
    await screen.findByText("Example.Release.WEB.1080p");

    expect(screen.getByRole("button", { name: "Grab" })).toBeDisabled();
  });
});

describe("grabbing from the Search page", () => {
  async function openGrab(candidates: unknown[] = [row({})], type?: string) {
    searchReply = { body: { candidates, indexersQueried: 1 } };
    await renderPage();
    if (type) fireEvent.click(screen.getByRole("radio", { name: type }));
    await search();
    fireEvent.click((await screen.findAllByRole("button", { name: "Grab" }))[0]);
    return await screen.findByRole("dialog");
  }

  it("a successful grab toasts which release went to which client and closes the drawer", async () => {
    const drawer = await openGrab();

    fireEvent.click(within(drawer).getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Example.Release.WEB.1080p sent to qBittorrent."));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("a movie search grabs as movie, a TV search as tv", async () => {
    const drawer = await openGrab();
    fireEvent.click(within(drawer).getByRole("button", { name: "Send to download client" }));
    await waitFor(() => expect(grabBody().searchType).toBe("movie"));
  });

  it("a TV search sends searchType tv with the grab", async () => {
    const drawer = await openGrab([row({})], "TV");
    fireEvent.click(within(drawer).getByRole("button", { name: "Send to download client" }));
    await waitFor(() => expect(grabBody().searchType).toBe("tv"));
  });

  it("in a Basic search each row grabs through its own engine, never as basic", async () => {
    const drawer = await openGrab([row({ searchType: "tv", releaseName: "Example.Show.S01E01" })], "Basic");
    fireEvent.click(within(drawer).getByRole("button", { name: "Send to download client" }));
    await waitFor(() => expect(grabBody().searchType).toBe("tv"));
    expect(grabBody()).toMatchObject({ releaseName: "Example.Show.S01E01", indexerId: "idx-1", downloadUrl: "https://fixture.invalid/x.torrent", quality: "WEB 1080p", sizeBytes: 2_000_000_000, seeders: 12 });
  });

  it("a grab refused at validation shows the server's sentence and leaves the drawer open", async () => {
    grabReply = { status: 400, body: { errors: { library: ["Link a download client to this library before grabbing a release."] } } };
    const drawer = await openGrab();

    fireEvent.click(within(drawer).getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Link a download client to this library before grabbing a release."));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("a grab the client rejects shows its message and keeps the drawer open", async () => {
    grabReply = { body: { succeeded: false, message: "qBittorrent refused the torrent." } };
    const drawer = await openGrab();

    fireEvent.click(within(drawer).getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("qBittorrent refused the torrent."));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("explains an indexer that failed or timed out during the search", async () => {
    searchReply = { body: { candidates: [row({})], indexersQueried: 2, failures: [{ serviceName: "Enabled Two", operation: "search", kind: "Timeout", message: "Timed out", summary: "Enabled Two search failed: Timed out." }] } };
    await renderPage();

    await search();

    expect(await screen.findByText(/Enabled Two search failed: Timed out\./)).toBeInTheDocument();
  });
});
