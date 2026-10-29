import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The workstation can be busy with other builds; a slow render is not a failed page.
configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The Search page's own screen states (coverage register, area search-grab): the query box, the search and grab buttons while
 * they are busy, the category and indexer lists, the results header, the Type column, sorting, word matching, the seeding flag,
 * the hard-rule findings in the grab drawer, and what a failed grab or a cancel does. The page is rendered whole; only the
 * network is answered.
 */
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch }));
const toast = vi.hoisted(() => Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast, realtimeToast: {}, Toaster: () => null }));

const { SearchPage } = await import("./search-page");

type Reply = { status?: number; body?: unknown };

const INDEXERS = [
  { id: "idx-1", name: "Enabled One", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid/1", priority: 1, categories: "2000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "movies" },
  { id: "idx-2", name: "Enabled Two", protocol: "newznab", privacy: "public", baseUrl: "https://fixture.invalid/2", priority: 2, categories: "5000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "tv" }
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

/** Answers every call from the fixtures above; a test swaps one route for a held or failing answer. */
function answer(url: string): Reply {
  if (url.startsWith("/api/search/grab")) return grabReply;
  if (url.startsWith("/api/search/categories")) return { body: CATEGORIES };
  if (url.startsWith("/api/search")) return searchReply;
  if (url.startsWith("/api/indexers")) return { body: INDEXERS };
  return { body: {} };
}

beforeEach(() => {
  searchReply = { body: { candidates: [row({})], indexersQueried: 2 } };
  grabReply = { body: { succeeded: true, downloadClientName: "qBittorrent" } };
  calls = [];
  authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const reply = answer(url);
    return new Response(JSON.stringify(reply.body ?? {}), { status: reply.status ?? 200, headers: { "Content-Type": "application/json" } });
  });
});
afterEach(() => {
  authedFetch.mockReset();
  vi.clearAllMocks();
});

const callsTo = (url: string) => calls.filter((c) => c.url === url);
const searchBody = () => callsTo("/api/search").at(-1)!.body;

/** Holds every call to one route until `release()`; the other routes answer straight away. `sent()` counts the calls that arrived. */
function holdRoute(route: string) {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  let arrived = 0;
  const base = authedFetch.getMockImplementation()!;
  authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.startsWith(route)) {
      arrived += 1;
      await gate;
    }
    return base(input, init);
  });
  return { release, sent: () => arrived };
}

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

describe("the Search page's query and type", () => {
  it("the query box takes focus when the page opens", async () => {
    await renderPage();

    expect(screen.getByPlaceholderText("Search for a release…")).toHaveFocus();
  });

  it("the Search button shows a spinner and is disabled while a search runs, so a second press sends nothing", async () => {
    await renderPage();
    const held = holdRoute("/api/search");
    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });

    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));

    await waitFor(() => expect(screen.getByRole("button", { name: /Search/ })).toBeDisabled());
    expect(screen.getByRole("button", { name: /Search/ }).querySelector(".animate-spin")).not.toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /Search/ }));
    expect(held.sent()).toBe(1);

    held.release();
    await waitFor(() => expect(screen.getByRole("button", { name: /^Search$/ })).toBeEnabled());
  });

  it("a TV search lists only the TV categories, and Basic lists both families", async () => {
    await renderPage();
    await screen.findByRole("checkbox", { name: "Movies" });
    expect(screen.queryByRole("checkbox", { name: "TV" })).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    expect(screen.getByRole("checkbox", { name: "TV" })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Movies" })).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: "Basic" }));
    expect(screen.getByRole("checkbox", { name: "Movies" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "TV" })).toBeInTheDocument();
  });

  it("changing the type clears a category picked for the other type, so the next search does not send it", async () => {
    await renderPage();
    fireEvent.click(await screen.findByRole("checkbox", { name: "Movies" }));

    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    await search();

    await waitFor(() => expect(searchBody().categoryIds).toBeNull());
    expect(screen.getByRole("checkbox", { name: "TV" })).not.toBeChecked();
  });

  it("shows Loading categories while the list is on its way, and no list when the categories call fails", async () => {
    const held = holdRoute("/api/search/categories");
    render(<MemoryRouter><SearchPage /></MemoryRouter>);
    expect(await screen.findByText("Loading categories…")).toBeInTheDocument();

    held.release();
    await waitFor(() => expect(screen.queryByText("Loading categories…")).toBeNull());
    expect(screen.getByRole("checkbox", { name: "Movies" })).toBeInTheDocument();
  });

  it("a failed categories call leaves no category list and no crash", async () => {
    const base = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.startsWith("/api/search/categories")) throw new Error("categories are down");
      return base(input, init);
    });
    await renderPage();

    await waitFor(() => expect(screen.queryByText("Loading categories…")).toBeNull());
    expect(screen.queryByRole("checkbox", { name: "Movies" })).toBeNull();
    expect(screen.getByPlaceholderText("Search for a release…")).toBeInTheDocument();
  });

  it("the indexer list says Loading indexers until the list arrives", async () => {
    const held = holdRoute("/api/indexers");
    render(<MemoryRouter><SearchPage /></MemoryRouter>);
    expect(await screen.findByText("Loading indexers…")).toBeInTheDocument();

    held.release();
    await screen.findByRole("checkbox", { name: "Enabled One" });
    expect(screen.queryByText("Loading indexers…")).toBeNull();
  });

  it("an empty indexer list says No indexers configured yet, and points at Find & Download", async () => {
    const base = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.startsWith("/api/indexers")) return new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } });
      return base(input, init);
    });
    render(<MemoryRouter><SearchPage /></MemoryRouter>);

    expect(await screen.findByText(/No indexers configured yet\. Add one under Find & Download\./)).toBeInTheDocument();
  });

  it("an indexers call that fails falls back to the empty message", async () => {
    const base = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.startsWith("/api/indexers")) throw new Error("indexers are down");
      return base(input, init);
    });
    render(<MemoryRouter><SearchPage /></MemoryRouter>);

    expect(await screen.findByText(/No indexers configured yet\. Add one under Find/)).toBeInTheDocument();
  });
});

describe("the Search page's results", () => {
  it("the results header counts the names shown out of all releases", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Example.Release.1080p" }), row({ releaseName: "Unrelated.Title.1080p" })], indexersQueried: 2 } };
    await renderPage();
    await search();

    expect(await screen.findByText("1 of 2 releases")).toBeInTheDocument();
  });

  it("the results header says release, not releases, when one release is shown", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Example.Release.1080p" })], indexersQueried: 2 } };
    await renderPage();
    await search();

    expect(await screen.findByText("1 of 1 release")).toBeInTheDocument();
  });

  it("a Type column (Movie or TV) appears only on a Basic search", async () => {
    searchReply = { body: { candidates: [row({ searchType: "tv" })], indexersQueried: 2 } };
    await renderPage();
    await search();
    await screen.findByRole("columnheader", { name: /^Release/ });
    expect(screen.queryByRole("columnheader", { name: /^Type/ })).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: "Basic" }));
    await search();
    expect(await screen.findByRole("columnheader", { name: /^Type/ })).toBeInTheDocument();
    expect(screen.getByText("Example.Release.WEB.1080p").closest('[role="row"]')).toHaveTextContent("TV");
  });

  it("clicking a column header sorts the rows ascending, then descending, and a row with no value goes last", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Big.Film", seeders: 50 }), row({ releaseName: "Missing.Film", seeders: null }), row({ releaseName: "Small.Film", seeders: 2 })], indexersQueried: 2 } };
    await renderPage();
    await search("Film");
    const header = await screen.findByRole("columnheader", { name: /^Seeders/ });

    fireEvent.click(within(header).getByRole("button"));
    expect(screen.getAllByText(/\.Film$/).map((el) => el.textContent)).toEqual(["Small.Film", "Big.Film", "Missing.Film"]);

    fireEvent.click(within(header).getByRole("button"));
    expect(screen.getAllByText(/\.Film$/).map((el) => el.textContent)).toEqual(["Big.Film", "Small.Film", "Missing.Film"]);
  });

  it("a new search resets both the protocol and the indexer filters to Any", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Example.A.1080p" }), row({ releaseName: "Example.B.1080p", indexerProtocol: "newznab", indexerId: "idx-2", indexerName: "Enabled Two" })], indexersQueried: 2 } };
    await renderPage();
    await search();
    fireEvent.click(await screen.findByRole("combobox", { name: "Filter by protocol" }));
    fireEvent.click(await screen.findByRole("option", { name: "Usenet" }));
    await waitFor(() => expect(screen.queryByText("Example.A.1080p")).toBeNull());
    fireEvent.click(screen.getByRole("combobox", { name: "Filter by indexer" }));
    fireEvent.click(await screen.findByRole("option", { name: "Enabled Two" }));

    await search();
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Filter by protocol" })).toHaveTextContent("Any protocol"));
    expect(screen.getByRole("combobox", { name: "Filter by indexer" })).toHaveTextContent("Any indexer");
    expect(await screen.findByText("Example.A.1080p")).toBeInTheDocument();
  });

  it("a word-matching search leaves out the small words and ignores accents and punctuation", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Matrix.1999.1080p" }), row({ releaseName: "Café.Film.1080p" }), row({ releaseName: "Other.Movie.1080p" })], indexersQueried: 2 } };
    await renderPage();
    await search("The Matrix");
    expect(await screen.findByText("Matrix.1999.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Other.Movie.1080p")).toBeNull();

    await search("Cafe Film");
    expect(await screen.findByText("Café.Film.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Matrix.1999.1080p")).toBeNull();
  });
});

describe("the Search page's grab drawer", () => {
  it("a torrent with no seeders says no one is seeding it in its drawer", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Torrent.Only.1080p", seeders: 0 })], indexersQueried: 2 } };
    await renderPage();
    await search("Only");

    fireEvent.click(await screen.findByText("Torrent.Only.1080p"));

    expect(await screen.findByText("No one is seeding this release.")).toBeInTheDocument();
  });

  it.each(["newznab", "usenet"])("a %s result with no seeders carries no seeders flag (#1404)", async (indexerProtocol) => {
    searchReply = { body: { candidates: [row({ releaseName: "Usenet.Only.1080p", seeders: 0, indexerProtocol })], indexersQueried: 2 } };
    await renderPage();
    await search("Only");

    fireEvent.click(await screen.findByText("Usenet.Only.1080p"));

    await screen.findByRole("dialog");
    expect(screen.queryByText("No one is seeding this release.")).toBeNull();
  });

  it.each(["torznab", "rss"])("a %s result with no seeders still says no one is seeding it (#1404)", async (indexerProtocol) => {
    searchReply = { body: { candidates: [row({ releaseName: "Torrent.Zero.1080p", seeders: 0, indexerProtocol })], indexersQueried: 2 } };
    await renderPage();
    await search("Zero");

    fireEvent.click(await screen.findByText("Torrent.Zero.1080p"));

    expect(await screen.findByText("No one is seeding this release.")).toBeInTheDocument();
  });


  it("the drawer lists a release's hard-rule findings in words: the cam rejection and its risk flags", async () => {
    searchReply = { body: { candidates: [row({ releaseName: "Cam.Film.1080p", rejectionGate: "Cam release", riskFlags: ["Never-grab group"] })], indexersQueried: 2 } };
    await renderPage();
    await search("Film");

    fireEvent.click(await screen.findByText("Cam.Film.1080p"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Cam release")).toBeInTheDocument();
    expect(within(dialog).getByText("Never-grab group")).toBeInTheDocument();
  });

  it("Cancel closes the drawer without sending anything", async () => {
    await renderPage();
    await search();
    fireEvent.click(await screen.findByText("Example.Release.WEB.1080p"));
    await screen.findByRole("dialog");

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(callsTo("/api/search/grab")).toHaveLength(0);
  });

  it("while the grab is sent the footer shows the saving state and the send button is disabled", async () => {
    await renderPage();
    await search();
    fireEvent.click(await screen.findByText("Example.Release.WEB.1080p"));
    const held = holdRoute("/api/search/grab");

    fireEvent.click(screen.getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Send to download client" })).toBeDisabled());
    held.release();
    await waitFor(() => expect(toast.success).toHaveBeenCalled());
  });

  it("a network failure on grab shows an error toast and the send button is enabled again", async () => {
    await renderPage();
    await search();
    fireEvent.click(await screen.findByText("Example.Release.WEB.1080p"));
    const base = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.startsWith("/api/search/grab")) throw new Error("Network down");
      return base(input, init);
    });

    fireEvent.click(screen.getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Network down"));
    expect(screen.getByRole("button", { name: "Send to download client" })).toBeEnabled();
  });
});
