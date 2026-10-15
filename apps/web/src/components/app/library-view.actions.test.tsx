import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The shelf's own actions (coverage register, area library): what "Search these N" searches, what "Update all metadata" and the
 * Add dialog send, and what the shelf says when a read fails or finds nothing. The real LibraryView is rendered whole; only the
 * network and the live connection are stood in for.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn(() => "toast-1"), dismiss: vi.fn() })
}));

vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: mocks.fetchJson
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast, realtimeToast: {}, Toaster: () => null }));

window.matchMedia ??= ((query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const { LibraryView } = await import("./library-view");

const movie = (id: string, title: string) => ({
  id, title, releaseYear: 2020, monitored: true, wantedStatus: "missing", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null,
  genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null
});

const ALL = [movie("m1", "Alien"), movie("m2", "Aliens"), movie("m3", "Heat")];
const page = (items: unknown[], extra: Record<string, unknown> = {}) => ({
  items, totalCount: items.length, nextPageToken: null, facets: { all: items.length, unmonitored: 0, covered: 0 }, ...extra
});

let pageFor: (params: URLSearchParams) => unknown | Promise<unknown>;
let bulkSearchReply: { ok: boolean; status?: number; body: unknown };
let createReply: { ok: boolean; status?: number; body: unknown };
let metadataMatches: unknown[];
let calls: Array<{ url: string; body: any }>;

beforeEach(() => {
  window.localStorage.clear();
  pageFor = () => page(ALL);
  bulkSearchReply = { ok: true, body: { searchesTriggered: 2, libraryCount: 1 } };
  createReply = { ok: true, body: { id: "new" } };
  metadataMatches = [];
  calls = [];
  mocks.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => {
    calls.push({ url: path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    if (path.startsWith("/api/movies/page")) return pageFor(new URLSearchParams(path.split("?")[1]));
    if (path.endsWith("/controls")) return { kind: "movies", filterFields: [], sortFields: [], posterOptions: [] };
    if (path === "/api/libraries") return [{ id: "lib-a", name: "Films", mediaType: "movies", rootPath: "/films", qualityProfileId: "p1" }];
    if (path.startsWith("/api/metadata/search")) return metadataMatches;
    if (path === "/api/metadata/status") return { isConfigured: true };
    if (path === "/api/movies/metadata/jobs") return { message: "Queued 12 titles for a metadata update." };
    return [];
  });
  mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const reply = url === "/api/movies/bulk/search" ? bulkSearchReply : url === "/api/movies" ? createReply : { ok: true, body: {} };
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? (reply.ok ? 200 : 400), headers: { "Content-Type": "application/json" } });
  });
});
afterEach(() => {
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
  vi.clearAllMocks();
});

async function renderShelf(url = "/") {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: [url] });
  render(<RouterProvider router={router} />);
  return router;
}

const searchButton = () => screen.findByRole("button", { name: /^Search these \d+$/ });
const bodyOf = (url: string) => calls.filter((c) => c.url === url).map((c) => c.body);

describe("Search these N", () => {
  it("searches exactly the titles on screen with their ids, and the button counts them", async () => {
    await renderShelf();

    const button = await screen.findByRole("button", { name: "Search these 3" });
    fireEvent.click(button);

    await waitFor(() => expect(bodyOf("/api/movies/bulk/search")).toEqual([{ movieIds: ["m1", "m2", "m3"] }]));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Searching for 2 movies. Watch it in Transfers.", { id: "toast-1" }));
  });

  it("after the shelf is narrowed it searches only the titles still shown", async () => {
    pageFor = (params) => (params.get("search") === "alien" ? page([ALL[0], ALL[1]]) : page(ALL));
    await renderShelf();
    await screen.findByRole("button", { name: "Search these 3" });

    fireEvent.change(screen.getByPlaceholderText("Search movies…"), { target: { value: "alien" } });

    const narrowed = await screen.findByRole("button", { name: "Search these 2" });
    fireEvent.click(narrowed);
    await waitFor(() => expect(bodyOf("/api/movies/bulk/search")).toEqual([{ movieIds: ["m1", "m2"] }]));
  });

  it("is disabled with nothing shown and says so", async () => {
    pageFor = () => page([]);
    await renderShelf();

    const button = await screen.findByRole("button", { name: "Search these 0" });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("title", "No movies on screen to search for");
  });

  it("says none of them was ready when the server triggered no search", async () => {
    bulkSearchReply = { ok: true, body: { searchesTriggered: 0 } };
    await renderShelf();
    fireEvent.click(await screen.findByRole("button", { name: "Search these 3" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalled());
    expect(String(mocks.toast.success.mock.calls[0][0])).toMatch(/^None of these movies was ready to search\./);
  });

  it("shows Searching… while the request runs", async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    const inner = mocks.authedFetch.getMockImplementation()!;
    mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input) === "/api/movies/bulk/search") await held;
      return inner(input, init);
    });
    await renderShelf();
    fireEvent.click(await screen.findByRole("button", { name: "Search these 3" }));

    expect(await screen.findByRole("button", { name: "Searching…" })).toBeDisabled();
    release();
    await waitFor(() => expect(screen.getByRole("button", { name: "Search these 3" })).toBeEnabled());
  });

  it("explains a failed search with a Check indexers action", async () => {
    bulkSearchReply = { ok: false, status: 400, body: { message: "no" } };
    await renderShelf();
    fireEvent.click(await screen.findByRole("button", { name: "Search these 3" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect((mocks.toast.error.mock.calls[0][1] as { action: { label: string } }).action.label).toBe("Check indexers");
  });
});

describe("Update all metadata", () => {
  it("queues a forced refresh of up to 500 titles, toasts the server's message and reads the shelf again", async () => {
    await renderShelf();
    await searchButton();
    const readsBefore = calls.filter((c) => c.url.startsWith("/api/movies/page")).length;

    fireEvent.click(screen.getByRole("button", { name: "Update all metadata" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Queued 12 titles for a metadata update."));
    expect(bodyOf("/api/movies/metadata/jobs")).toEqual([{ forceAll: true, take: 500 }]);
    await waitFor(() => expect(calls.filter((c) => c.url.startsWith("/api/movies/page")).length).toBeGreaterThan(readsBefore));
  });

  it("says the movie library could not be queued when the server refuses", async () => {
    const inner = mocks.fetchJson.getMockImplementation()!;
    mocks.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === "/api/movies/metadata/jobs") throw new Error("down");
      return inner(path, init);
    });
    await renderShelf();
    await searchButton();

    fireEvent.click(screen.getByRole("button", { name: "Update all metadata" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not queue a metadata update for the movie library."));
  });
});

describe("what the shelf says when it has nothing or cannot read", () => {
  it("a genuinely empty library says so and offers Add movie", async () => {
    pageFor = () => page([]);
    await renderShelf();

    expect(await screen.findByText("Your movies library is empty")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Add movie" }).length).toBeGreaterThan(0);
  });

  it("a search that matches nothing says Nothing matches with the library total, and Clear filters resets the search", async () => {
    pageFor = (params) => (params.get("search") ? page([], { totalCount: 0 }) : page(ALL));
    await renderShelf();
    await screen.findByRole("button", { name: "Search these 3" });

    fireEvent.change(screen.getByPlaceholderText("Search movies…"), { target: { value: "zzz" } });
    expect(await screen.findByText("Nothing matches")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));

    expect(await screen.findByRole("button", { name: "Search these 3" })).toBeInTheDocument();
    expect((screen.getByPlaceholderText("Search movies…") as HTMLInputElement).value).toBe("");
  });

  it("a first page that fails shows an error toast and an empty shelf", async () => {
    pageFor = () => { throw new Error("down"); };
    await renderShelf();

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not load the library."));
    expect(await screen.findByRole("button", { name: "Search these 0" })).toBeDisabled();
  });

  it("a later slice that fails keeps what arrived and says the rest could not be loaded", async () => {
    pageFor = (params) => {
      if (params.get("pageToken")) throw new Error("down");
      return page([ALL[0]], { totalCount: 3, nextPageToken: "next" });
    };
    await renderShelf();

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not load the rest of the library."));
    expect(await screen.findByRole("button", { name: "Search these 1" })).toBeEnabled();
  });
});

describe("the Add dialog", () => {
  const match = (id: string, title: string, extra: Record<string, unknown> = {}) => ({
    provider: "tmdb", providerId: id, mediaType: "movies", title, originalTitle: null, year: 2016, overview: "About it.", posterUrl: "https://image.example/p.jpg",
    backdropUrl: null, rating: 7.4, ratings: [], genres: ["Drama"], cast: [], crew: [], imdbId: `tt${id}`, externalUrl: `https://tmdb.example/${id}`, dataSource: "tmdb", ...extra
  });

  async function openDialog() {
    await renderShelf();
    await searchButton();
    fireEvent.click(screen.getAllByRole("button", { name: "Add movie" })[0]);
    return await screen.findByRole("dialog", { name: /Add movie/ });
  }

  it("opens from the toolbar and Cancel closes it", async () => {
    const dialog = await openDialog();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog", { name: /Add movie/ })).toBeNull());
  });

  it("typing two characters searches the metadata provider after a pause and lists the matches with year and source", async () => {
    metadataMatches = [match("1", "Arrival"), match("2", "Arrival of a Train")];
    const dialog = await openDialog();

    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "Ar" } });

    expect(await within(dialog).findByText("Arrival")).toBeInTheDocument();
    expect(within(dialog).getAllByText(/2016 · TMDb/).length).toBe(2);
    expect(calls.some((c) => c.url.startsWith("/api/metadata/search?") && c.url.includes("query=Ar") && c.url.includes("mediaType=movies"))).toBe(true);
  });

  it("does not search for a single character", async () => {
    const dialog = await openDialog();
    const before = calls.filter((c) => c.url.startsWith("/api/metadata/search")).length;

    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "A" } });
    await new Promise((resolve) => setTimeout(resolve, 600));

    expect(calls.filter((c) => c.url.startsWith("/api/metadata/search")).length).toBe(before);
  });

  it("picking a match then Add selected movie creates it with the provider's record, poster, overview, genres, rating and link", async () => {
    metadataMatches = [match("1", "Arrival")];
    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "Arrival" } });
    fireEvent.click(await within(dialog).findByText("Arrival"));

    const add = await within(dialog).findByRole("button", { name: "Add selected movie" });
    fireEvent.click(add);

    await waitFor(() => expect(bodyOf("/api/movies")).toHaveLength(1));
    expect(bodyOf("/api/movies")[0]).toMatchObject({ title: "Arrival", releaseYear: 2016, imdbId: "tt1", monitored: true });
    expect(JSON.stringify(bodyOf("/api/movies")[0])).toContain("https://image.example/p.jpg");
    expect(JSON.stringify(bodyOf("/api/movies")[0])).toContain("About it.");
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("1 movie added"));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /Add movie/ })).toBeNull());
  });

  it("selecting several matches adds them all with Add N movies", async () => {
    metadataMatches = [match("1", "Arrival"), match("2", "Heat")];
    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "Ar" } });
    fireEvent.click(await within(dialog).findByText("Arrival"));
    fireEvent.click(within(dialog).getByText("Heat"));

    fireEvent.click(await within(dialog).findByRole("button", { name: "Add 2 movies" }));

    await waitFor(() => expect(bodyOf("/api/movies")).toHaveLength(2));
    expect(bodyOf("/api/movies").map((body) => body.title).sort()).toEqual(["Arrival", "Heat"]);
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("2 movies added"));
  });

  it("when one of several fails, the failed one stays selected and the count says how many could not be added", async () => {
    metadataMatches = [match("1", "Arrival"), match("2", "Heat")];
    const inner = mocks.authedFetch.getMockImplementation()!;
    mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input) === "/api/movies" && String(init?.body).includes('"Heat"')) {
        return new Response(JSON.stringify({ title: "Heat could not be added." }), { status: 400, headers: { "Content-Type": "application/problem+json" } });
      }
      return inner(input, init);
    });
    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "Ar" } });
    fireEvent.click(await within(dialog).findByText("Arrival"));
    fireEvent.click(within(dialog).getByText("Heat"));

    fireEvent.click(await within(dialog).findByRole("button", { name: "Add 2 movies" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("1 title could not be added."));
    expect(mocks.toast.success).toHaveBeenCalledWith("1 movie added");
    expect(await within(screen.getByRole("dialog", { name: /Add movie/ })).findByRole("button", { name: "Add selected movie" })).toBeInTheDocument();
  });

  it("a manual add with nothing chosen asks for a name first and sends nothing", async () => {
    const dialog = await openDialog();

    const submit = within(dialog).getByRole("button", { name: "Add movie manually" });
    expect(submit).toBeDisabled();
    expect(bodyOf("/api/movies")).toHaveLength(0);
  });

  it("a manual add posts the typed title, year and IMDb id, toasts Movie added and closes", async () => {
    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "Home Movie" } });
    fireEvent.click(within(dialog).getByText("Can’t find it? Add it manually"));
    fireEvent.change(within(dialog).getByPlaceholderText("Year (optional)"), { target: { value: "2001" } });
    fireEvent.change(within(dialog).getByPlaceholderText("IMDb ID (optional)"), { target: { value: "tt0000001" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(bodyOf("/api/movies")).toHaveLength(1));
    expect(bodyOf("/api/movies")[0]).toMatchObject({ title: "Home Movie", releaseYear: 2001, imdbId: "tt0000001", monitored: true });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Movie added"));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /Add movie/ })).toBeNull());
  });

  it("a server refusal on add is shown as the server's sentence", async () => {
    createReply = { ok: false, status: 400, body: { title: "That title is already in your library." } };
    const dialog = await openDialog();
    fireEvent.change(within(dialog).getByPlaceholderText(/Search movies, for example/), { target: { value: "Home Movie" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That title is already in your library."));
    expect(screen.getByRole("dialog", { name: /Add movie/ })).toBeInTheDocument();
  });
});

describe("the ?filter= link and ?add=true link", () => {
  it("?add=true&title=X opens the Add dialog with X already in the title box", async () => {
    await renderShelf("/?add=true&title=Nosferatu");

    const dialog = await screen.findByRole("dialog", { name: /Add movie/ });
    expect((within(dialog).getByPlaceholderText(/Search movies, for example/) as HTMLInputElement).value).toBe("Nosferatu");
  });
});
