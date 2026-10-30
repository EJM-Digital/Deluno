import { cleanup, configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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
    if (path.startsWith("/api/movies/page") || path.startsWith("/api/series/page")) return page;
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

  it("an empty box submitted says Type a movie name first. and searches nothing, with Add movie manually disabled", async () => {
    const dialog = await openDialog();
    const before = searches().length;

    fireEvent.submit(titleBox(dialog).closest("form")!);

    await waitFor(() => expect(mocks.toast.info).toHaveBeenCalledWith("Type a movie name first."));
    expect(searches().length).toBe(before);
    expect(within(dialog).getByRole("button", { name: "Add movie manually" })).toBeDisabled();
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

  it("an over-long title is refused in the server's own sentence, not a generic one", async () => {
    mocks.authedFetch.mockImplementation(async () => new Response(JSON.stringify({ message: "The title is too long. It can be at most 200 characters.", member: "title" }), { status: 400 }));
    const dialog = await openDialog();

    fireEvent.change(titleBox(dialog), { target: { value: "A".repeat(201) } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("The title is too long. It can be at most 200 characters."));
  });

  it("a validation refusal shows its first sentence", async () => {
    mocks.authedFetch.mockImplementation(async () => new Response(JSON.stringify({ title: "One or more validation errors occurred.", errors: { releaseYear: ["Release year must be between 1888 and 2100."] } }), { status: 400 }));
    const dialog = await openDialog();

    fireEvent.change(titleBox(dialog), { target: { value: "Home Movie" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Release year must be between 1888 and 2100."));
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

describe("search on add (#1399)", () => {
  async function addManually(untickMonitor: boolean) {
    const dialog = await openDialog();
    fireEvent.change(titleBox(dialog), { target: { value: "Home Movie" } });
    if (untickMonitor) fireEvent.click(within(dialog).getByRole("checkbox", { name: "Monitor it" }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Add movie manually" }));
    await waitFor(() => expect(calls.filter((c) => c.url === "/api/movies")).toHaveLength(1));
    return calls.find((c) => c.url === "/api/movies")!.body;
  }

  it("a monitored add asks for the search", async () => {
    expect(await addManually(false)).toMatchObject({ monitored: true, searchForMissingNow: true, searchForCutoffUnmetNow: true });
  });

  it("an unmonitored add asks for no search, and the search box is off and unavailable", async () => {
    const dialog = await openDialog();
    fireEvent.click(within(dialog).getByRole("checkbox", { name: "Monitor it" }));
    const box = within(dialog).getByRole("checkbox", { name: "Search for it as soon as it is added" });
    expect(box).toBeDisabled();
    expect(box).not.toBeChecked();
    cleanup();
    expect(await addManually(true)).toMatchObject({ monitored: false, searchForMissingNow: false, searchForCutoffUnmetNow: false });
  });
  it("an unmonitored show asks for no search either, and a monitored one does", async () => {
    mocks.fetchJson.mockImplementation(async (path: string) => {
      if (path.startsWith("/api/series/page")) return page;
      if (path.endsWith("/controls")) return { kind: "series", filterFields: [], sortFields: [], posterOptions: [] };
      if (path === "/api/libraries") return [{ id: "lib-s", name: "Shows", mediaType: "tv", rootPath: "/shows", qualityProfileId: "p1" }];
      return [];
    });
    for (const unmonitor of [true, false]) {
      calls = [];
      const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="shows" metadataStatus={null} /> }], { initialEntries: ["/"] });
      render(<RouterProvider router={router} />);
      await screen.findByRole("button", { name: /^Search these \d+$/ });
      fireEvent.click(screen.getAllByRole("button", { name: "Add TV show" })[0]);
      const dialog = await screen.findByRole("dialog", { name: /Add TV show/ });
      fireEvent.change(within(dialog).getByPlaceholderText(/Search TV shows, for example/), { target: { value: "Reacher" } });
      if (unmonitor) fireEvent.click(within(dialog).getByRole("checkbox", { name: "Monitor it" }));
      // The "which episodes to monitor" choice only exists while the show is monitored: an unmonitored add carries no episode preset.
      expect(within(dialog).queryByLabelText("Which episodes to monitor") === null).toBe(unmonitor);
      fireEvent.click(within(dialog).getByRole("button", { name: "Add TV show manually" }));
      await waitFor(() => expect(calls.filter((c) => c.url === "/api/series")).toHaveLength(1));
      expect(calls.find((c) => c.url === "/api/series")!.body).toMatchObject({ monitored: !unmonitor, searchForMissingNow: !unmonitor, searchForCutoffUnmetNow: !unmonitor });
      if (unmonitor) expect(calls.find((c) => c.url === "/api/series")!.body).toMatchObject({ listMonitorMode: null });
      cleanup();
    }
  });
});

describe("closing the Add dialog (#1329)", () => {
  async function openFromLink() {
    metadataReply = () => [match("1", "Arrival")];
    const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" metadataStatus={null} /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: ["/?add=true&title=Arrival"] });
    render(<RouterProvider router={router} />);
    const dialog = await screen.findByRole("dialog", { name: /Add movie/ });
    fireEvent.click(await within(dialog).findByText("Arrival"));
    expect(await within(dialog).findByRole("button", { name: "Add selected movie" })).toBeInTheDocument();
    expect(router.state.location.search).toContain("add=true");
    return router;
  }

  const closers: Array<[string, () => void]> = [
    ["the X", () => fireEvent.click(screen.getByRole("button", { name: "Close add movie" }))],
    ["Cancel", () => fireEvent.click(within(screen.getByRole("dialog", { name: /Add movie/ })).getByRole("button", { name: "Cancel" }))],
    ["Esc", () => fireEvent.keyDown(screen.getByRole("dialog", { name: /Add movie/ }), { key: "Escape", code: "Escape" })],
    ["a click on the overlay", () => { fireEvent.pointerDown(document.body); fireEvent.mouseDown(document.body); fireEvent.click(document.body); }]
  ];

  it.each(closers)("closing with %s drops the picked matches and takes add=true off the address", async (_name, close) => {
    const router = await openFromLink();

    close();

    await waitFor(() => expect(screen.queryByRole("dialog", { name: /Add movie/ })).toBeNull());
    await waitFor(() => expect(router.state.location.search).not.toContain("add="));
    fireEvent.click(screen.getAllByRole("button", { name: "Add movie" })[0]);
    const reopened = await screen.findByRole("dialog", { name: /Add movie/ });
    expect(within(reopened).queryByRole("button", { name: "Add selected movie" })).toBeNull();
    expect(within(reopened).queryByRole("button", { name: /^Add \d+ movies$/ })).toBeNull();
  });
});
