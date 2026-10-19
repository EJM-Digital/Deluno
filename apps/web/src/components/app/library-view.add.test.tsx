import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The Add dialog's search and tags (coverage register, area library): the real LibraryView is rendered whole; only the network
 * and the live connection are stood in for. What is asserted is when the metadata search goes out, what the dialog says when the
 * service cannot match titles, and which tags a title is added with and the next add starts from.
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
const { ApiRequestError } = await import("../../lib/api/client");

const page = { items: [], totalCount: 0, nextPageToken: null, facets: { all: 0, unmonitored: 0, covered: 0 } };
const match = (id: string, title: string) => ({
  provider: "tmdb", providerId: id, mediaType: "movies", title, originalTitle: null, year: 2016, overview: "About it.", posterUrl: "https://image.example/p.jpg",
  backdropUrl: null, rating: 7.4, ratings: [], genres: ["Drama"], cast: [], crew: [], imdbId: `tt${id}`, externalUrl: null, dataSource: "tmdb"
});

let metadataReply: () => unknown;
let calls: Array<{ url: string; body: any }>;
const searches = () => calls.filter((c) => c.url.startsWith("/api/metadata/search"));

beforeEach(() => {
  window.localStorage.clear();
  metadataReply = () => [];
  calls = [];
  mocks.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => {
    calls.push({ url: path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    if (path.startsWith("/api/movies/page")) return page;
    if (path.endsWith("/controls")) return { kind: "movies", filterFields: [], sortFields: [], posterOptions: [] };
    if (path === "/api/libraries") return [{ id: "lib-a", name: "Films", mediaType: "movies", rootPath: "/films", qualityProfileId: "p1" }, { id: "lib-b", name: "More films", mediaType: "movies", rootPath: "/more", qualityProfileId: "p1" }];
    if (path.startsWith("/api/metadata/search")) return metadataReply();
    return [];
  });
  mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), body: init?.body ? JSON.parse(String(init.body)) : undefined });
    return new Response(JSON.stringify({ id: "new" }), { status: 200, headers: { "Content-Type": "application/json" } });
  });
});
afterEach(() => {
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
  vi.clearAllMocks();
});

async function openDialog(status?: { isConfigured: boolean }) {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" metadataStatus={status ? ({ provider: "tmdb", mode: "unconfigured", message: "", sources: [], ...status } as never) : null} /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: ["/"] });
  render(<RouterProvider router={router} />);
  await screen.findByRole("button", { name: /^Search these \d+$/ });
  fireEvent.click(screen.getAllByRole("button", { name: "Add movie" })[0]);
  return await screen.findByRole("dialog", { name: /Add movie/ });
}
const titleBox = (dialog: HTMLElement) => within(dialog).getByPlaceholderText(/Search movies, for example/);

describe("the title search in the Add dialog", () => {
  it("Enter searches at once, without waiting out the pause that typing waits for", async () => {
    metadataReply = () => [match("1", "Arrival")];
    const dialog = await openDialog();
    const before = searches().length;

    fireEvent.change(titleBox(dialog), { target: { value: "Ar" } });
    expect(searches().length).toBe(before);
    fireEvent.submit(titleBox(dialog).closest("form")!);

    await waitFor(() => expect(searches().length).toBe(before + 1), { timeout: 250 });
    expect(await within(dialog).findByText("Arrival")).toBeInTheDocument();
    expect(within(dialog).getByText("7.4 rating")).toBeInTheDocument();
  });

  it("asks again as the text changes, and the answer for the newest text replaces the older one", async () => {
    metadataReply = () => (searches().at(-1)?.url.includes("query=Arriv") ? [match("2", "Arrival")] : [match("1", "Arrow")]);
    const dialog = await openDialog();

    fireEvent.change(titleBox(dialog), { target: { value: "Arr" } });
    expect(await within(dialog).findByText("Arrow")).toBeInTheDocument();
    fireEvent.change(titleBox(dialog), { target: { value: "Arriv" } });

    expect(await within(dialog).findByText("Arrival")).toBeInTheDocument();
    expect(within(dialog).queryByText("Arrow")).toBeNull();
    expect(searches().map((c) => new URLSearchParams(c.url.split("?")[1]).get("query"))).toEqual(["Arr", "Arriv"]);
  });

  it("a search that finds nothing, asked for with Enter, says No metadata matches found.", async () => {
    const dialog = await openDialog();

    fireEvent.change(titleBox(dialog), { target: { value: "Zzzz" } });
    fireEvent.submit(titleBox(dialog).closest("form")!);

    await waitFor(() => expect(mocks.toast.info).toHaveBeenCalledWith("No metadata matches found."));
  });

  it("a service that is not answering is worded as an outage with the way out, never as no matches", async () => {
    metadataReply = () => { throw new ApiRequestError("The metadata service is not answering right now.", 503, "/api/metadata/search", JSON.stringify({ error: "metadata-unavailable" })); };
    const dialog = await openDialog();

    fireEvent.change(titleBox(dialog), { target: { value: "Arrival" } });
    fireEvent.submit(titleBox(dialog).closest("form")!);

    await waitFor(() => expect(mocks.toast.warning).toHaveBeenCalledWith("The metadata service is not answering right now.", { description: "You can still add the title by hand." }));
    expect(mocks.toast.info).not.toHaveBeenCalledWith("No metadata matches found.");
  });
});

describe("when title matching is unavailable", () => {
  it("the dialog says Manual entry and that matching is temporarily unavailable, searches nothing, and still adds by hand", async () => {
    const dialog = await openDialog({ isConfigured: false });

    await within(dialog).findByText("Manual entry");
    expect(await within(dialog).findByText("Metadata matching is currently unavailable.")).toBeInTheDocument();
    expect(within(dialog).getByText("Title matching is temporarily unavailable. You can still add this title manually below.")).toBeInTheDocument();
    fireEvent.change(titleBox(dialog), { target: { value: "Home Movie" } });
    await new Promise((resolve) => setTimeout(resolve, 600)); // reason: longer than the 350 ms pause, to show nothing is sent
    expect(searches()).toHaveLength(0);
    expect(within(dialog).getByRole("button", { name: "Add movie manually" })).toBeEnabled();
  });
});

describe("tags on add", () => {
  it("start from the tags last used for the library, are sent with the title, and are what the next add starts from", async () => {
    window.localStorage.setItem("deluno:add-tags:lib-a", "kids, 4k");
    const dialog = await openDialog();
    await waitFor(() => expect(within(dialog).getByText("kids")).toBeInTheDocument());
    expect(within(dialog).getByText("4k")).toBeInTheDocument();

    fireEvent.change(titleBox(dialog), { target: { value: "Home Movie" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(calls.filter((c) => c.url === "/api/movies")).toHaveLength(1));
    expect(calls.find((c) => c.url === "/api/movies")!.body).toMatchObject({ title: "Home Movie", tags: ["kids", "4k"], libraryId: "lib-a" });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Movie added"));
    expect(window.localStorage.getItem("deluno:add-tags:lib-a")).toBe("kids, 4k");
  });

  it("a refused add does not change what the next add starts from", async () => {
    window.localStorage.setItem("deluno:add-tags:lib-a", "kids");
    mocks.authedFetch.mockImplementation(async () => new Response(JSON.stringify({ title: "No." }), { status: 400, headers: { "Content-Type": "application/problem+json" } }));
    const dialog = await openDialog();
    await waitFor(() => expect(within(dialog).getByText("kids")).toBeInTheDocument());

    fireEvent.change(titleBox(dialog), { target: { value: "Home Movie" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("No."));
    expect(window.localStorage.getItem("deluno:add-tags:lib-a")).toBe("kids");
  });

  it("with no tags remembered the picker starts empty and the title is added with none", async () => {
    const dialog = await openDialog();
    fireEvent.change(titleBox(dialog), { target: { value: "Home Movie" } });

    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(calls.filter((c) => c.url === "/api/movies")).toHaveLength(1));
    expect(calls.find((c) => c.url === "/api/movies")!.body.tags).toEqual([]);
    expect(window.localStorage.getItem("deluno:add-tags:lib-a")).toBeNull();
  });
});
