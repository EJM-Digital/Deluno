import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { stubVirtualLayout } from "../../test/virtual-layout";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 60_000 });

/**
 * The shelf with its controls wired up (coverage register, area library): the real LibraryView is rendered whole; only the network
 * and the live connection are stood in for. What is asserted is the request each control sends to the catalogue, the shelf that
 * comes back, the layout each choice draws, and that a very large library stays virtualised and selectable as a whole.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn(() => "toast-1"), dismiss: vi.fn() })
}));

vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast, realtimeToast: {}, Toaster: () => null }));

window.matchMedia ??= ((query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
Element.prototype.scrollIntoView = vi.fn();

let LibraryView: typeof import("./library-view").LibraryView;

const film = (id: string, name: string, over: Record<string, unknown> = {}) => ({
  id, title: name, releaseYear: 2020, monitored: true, wantedStatus: "missing", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null,
  genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null, ...over
});
const page = (items: unknown[], facets: Record<string, number> = {}) => ({ items, totalCount: items.length, nextPageToken: null, facets: { all: items.length, unmonitored: 0, covered: 0, ...facets } });

const CONTROLS = {
  kind: "movies",
  filterFields: [{ id: "year", label: "Year", hint: "When it came out.", group: "title", valueKind: "year", operators: ["min", "max"], options: null }],
  sortFields: [{ id: "title", label: "Title", hint: "A to Z." }, { id: "year", label: "Year", hint: "Release year." }, { id: "added", label: "Date added", hint: "Newest first." }],
  posterOptions: []
};

let pageFor: (params: URLSearchParams, path: string) => unknown;
let paged: URLSearchParams[];
let writes: Array<{ url: string; method: string; body: any }>;
let restoreLayout: () => void;

beforeEach(async () => {
  // The shelf keeps a snapshot of what it last read in module memory; each test starts from a fresh copy of it.
  vi.resetModules();
  ({ LibraryView } = await import("./library-view"));
  restoreLayout = stubVirtualLayout();
  window.localStorage.clear();
  window.sessionStorage.clear();
  paged = [];
  writes = [];
  pageFor = () => page([film("m1", "Alien"), film("m2", "Aliens"), film("m3", "Heat")]);
  mocks.fetchJson.mockImplementation(async (path: string) => {
    if (path.startsWith("/api/movies/page") || path.startsWith("/api/series/page")) {
      const params = new URLSearchParams(path.split("?")[1]);
      paged.push(params);
      return pageFor(params, path);
    }
    if (path === "/api/movies/controls") return CONTROLS;
    if (path === "/api/series/controls") return { ...CONTROLS, kind: "shows", sortFields: [...CONTROLS.sortFields] };
    if (path === "/api/libraries") {
      return [
        { id: "lib-t", name: "Shows", mediaType: "tv", rootPath: "/shows", qualityProfileId: "p2" },
        { id: "lib-b", name: "Kids films", mediaType: "movies", rootPath: "/kids", qualityProfileId: "p1" },
        { id: "lib-a", name: "4K films", mediaType: "movies", rootPath: "/films", qualityProfileId: "p1" }
      ];
    }
    return [];
  });
  mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    writes.push({ url: String(input), method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined });
    return new Response(JSON.stringify({ updated: 1 }), { status: 200, headers: { "Content-Type": "application/json" } });
  });
});
afterEach(() => {
  restoreLayout();
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
  vi.clearAllMocks();
});

async function renderShelf(variant: "movies" | "shows" = "movies") {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant={variant} /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: ["/"] });
  render(<RouterProvider router={router} />);
  await screen.findByRole("button", { name: /^Search these \d+$/ });
}
const lastPage = () => paged.at(-1)!;
async function chooseLayout(label: string) {
  fireEvent.click(screen.getByText("View"));
  fireEvent.click(screen.getByText(label).closest("button")!);
  fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
  await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
}
const rowButtons = () => screen.queryAllByRole("button", { name: /^(De)?select (?!all)/i });

describe("the library picker on the shelf", () => {
  it("lists All libraries then this shelf's own libraries by name, and picking one asks the catalogue for that library and shows only its rows and count", async () => {
    pageFor = (params) => (params.get("libraryId") === "lib-b" ? page([film("m9", "Bambi", { libraryId: "lib-b" })]) : page([film("m1", "Alien"), film("m2", "Aliens"), film("m3", "Heat")]));
    await renderShelf();

    const picker = await screen.findByRole("combobox", { name: "Library" });
    fireEvent.click(picker);
    await waitFor(() => expect(within(screen.getByRole("listbox", { name: "Library" })).getAllByRole("option").map((option) => option.textContent)).toEqual(["All libraries", "4K films", "Kids films"]));
    fireEvent.click(screen.getByRole("option", { name: "Kids films" }));

    expect(await screen.findByRole("button", { name: "Search these 1" })).toBeInTheDocument();
    expect(lastPage().get("libraryId")).toBe("lib-b");
    expect(await screen.findByRole("button", { name: /^(De)?select Bambi$/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^(De)?select Alien$/i })).toBeNull();
  });
});

describe("the sort menu and direction button on the shelf", () => {
  it("picking a sort asks again in that order and the rows come back in it; the direction button flips the order, its label and the request", async () => {
    pageFor = (params) => {
      const rows = [film("m1", "Alien", { releaseYear: 1979 }), film("m2", "Heat", { releaseYear: 1995 }), film("m3", "Tenet", { releaseYear: 2020 })];
      const ordered = params.get("sort") === "year" ? rows : [rows[1], rows[0], rows[2]];
      return page(params.get("direction") === "asc" ? ordered : [...ordered].reverse());
    };
    await renderShelf();
    const order = () => rowButtons().map((button) => button.getAttribute("aria-label")!.replace(/^(De)?select /i, ""));
    await waitFor(() => expect(order()).toHaveLength(3));

    fireEvent.click(screen.getByRole("combobox", { name: "Sort" }));
    fireEvent.click(await screen.findByRole("option", { name: /^Year/ }));
    await waitFor(() => expect(lastPage().get("sort")).toBe("year"));
    const ascending = ["Alien", "Heat", "Tenet"];
    const direction = () => lastPage().get("direction");
    await waitFor(() => expect(order()).toEqual(direction() === "asc" ? ascending : [...ascending].reverse()));
    const first = direction();
    expect(screen.getByRole("button", { name: first === "asc" ? "Ascending — switch to descending" : "Descending — switch to ascending" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /switch to/ }));
    await waitFor(() => expect(direction()).toBe(first === "asc" ? "desc" : "asc"));
    await waitFor(() => expect(order()).toEqual(direction() === "asc" ? ascending : [...ascending].reverse()));
    expect(screen.getByRole("button", { name: first === "asc" ? "Descending — switch to ascending" : "Ascending — switch to descending" })).toBeInTheDocument();
  });
});

describe("the layout choice", () => {
  it("Compact list draws a table of the same titles and Overview a row each, and Poster grid brings the posters back", async () => {
    await renderShelf();
    await waitFor(() => expect(rowButtons()).toHaveLength(3));

    await chooseLayout("Compact list");
    expect(await screen.findByRole("grid")).toBeInTheDocument();
    expect(screen.getAllByRole("checkbox", { name: "Select row" })).toHaveLength(3);
    await chooseLayout("Overview");
    await waitFor(() => expect(screen.getAllByRole("button", { name: /^Open / })).toHaveLength(3));
    expect(screen.queryByRole("grid")).toBeNull();
    await chooseLayout("Poster grid");
    await waitFor(() => expect(rowButtons()).toHaveLength(3));
    expect(screen.queryByRole("grid")).toBeNull();
  });
});

describe("the TV pills on the shelf", () => {
  it("Continuing asks the catalogue for airing shows and shows TV's own counts", async () => {
    pageFor = (params) => (params.get("status") === "airing"
      ? { ...page([film("s1", "Severance")], { airing: 1 }), facets: { all: 3, airing: 1, unmonitored: 0, covered: 0 } }
      : { ...page([film("s1", "Severance"), film("s2", "Silo"), film("s3", "Andor")]), facets: { all: 3, airing: 1, unmonitored: 0, covered: 0 } });
    await renderShelf("shows");

    const continuing = await screen.findByRole("button", { name: /Continuing/ });
    await waitFor(() => expect(continuing).toHaveTextContent("1"));
    expect(screen.getByRole("button", { name: /^All/ })).toHaveTextContent("3");
    fireEvent.click(continuing);

    await waitFor(() => expect(lastPage().get("status")).toBe("airing"));
    expect(await screen.findByRole("button", { name: "Search these 1" })).toBeInTheDocument();
  });
});

describe("conditions on the shelf", () => {
  it("travel as one f per finished condition; a row still waiting for its value is not sent", async () => {
    await renderShelf();
    fireEvent.click(screen.getByText("Filter"));
    const drawer = await screen.findByRole("dialog");
    fireEvent.click(within(drawer).getByRole("button", { name: "Add a field" }));
    fireEvent.click(within(drawer).getByRole("button", { name: /^Year/ }));

    expect(within(drawer).getByText("Nothing is narrowed by this yet — year needs a value.")).toBeInTheDocument();
    expect(paged.every((params) => params.getAll("f").length === 0)).toBe(true);
    expect(screen.getByText("none")).toBeInTheDocument();

    fireEvent.change(within(drawer).getByRole("spinbutton", { name: "Year" }), { target: { value: "2000" } });
    await waitFor(() => expect(lastPage().getAll("f")).toEqual(["year:min:2000"]));
    expect(screen.getByText("1 narrowing")).toBeInTheDocument();
  });
});

describe("a very large library", () => {
  const BIG = Array.from({ length: 5000 }, (_, index) => film(`m${index}`, `Title ${String(index).padStart(4, "0")}`));

  it("draws only the posters near the screen, and Select all shown selects all 5000 so one Monitor sends them all", async () => {
    pageFor = () => page(BIG);
    await renderShelf();
    await waitFor(() => expect(rowButtons().length).toBeGreaterThan(0));
    expect(rowButtons().length).toBeLessThan(120);
    expect(await screen.findByRole("button", { name: "Search these 5000" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Select all shown" }));
    expect(await screen.findByText("items selected")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "5000 selected" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Monitor" }));
    const box = (await screen.findByText("Bulk workflow")).closest("div.fixed") as HTMLElement;
    expect(within(box).getByText("5000 selected titles")).toBeInTheDocument();
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    fireEvent.click(await within(box).findByRole("button", { name: "Confirm and run" }));

    await waitFor(() => expect(writes.filter((call) => call.url === "/api/movies/monitoring")).toHaveLength(1));
    const sent = writes.find((call) => call.url === "/api/movies/monitoring")!;
    expect(sent.body.movieIds).toHaveLength(5000);
    expect(new Set(sent.body.movieIds).size).toBe(5000);
    expect(sent.body.monitored).toBe(true);
  });

  it("the compact list draws only the rows near the screen, and its header checkbox selects all 5000", async () => {
    pageFor = () => page(BIG);
    await renderShelf();
    await chooseLayout("Compact list");
    await screen.findByRole("grid");

    await waitFor(() => expect(screen.getAllByRole("checkbox", { name: "Select row" }).length).toBeGreaterThan(0));
    expect(screen.getAllByRole("checkbox", { name: "Select row" }).length).toBeLessThan(80);
    expect(screen.getByRole("grid")).toHaveAttribute("aria-rowcount", "5000");
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all rows" }));
    expect(await screen.findByText("items selected")).toBeInTheDocument();
    expect(screen.getAllByText("5000").length).toBeGreaterThan(0);
  });

  it("typing in the search box asks the catalogue once for what was typed, not once per key", async () => {
    pageFor = (params) => (params.get("search") ? page([film("m7", "Title 0007")]) : page(BIG));
    await renderShelf();
    await waitFor(() => expect(rowButtons().length).toBeGreaterThan(0));
    const before = paged.length;

    const box = screen.getByPlaceholderText("Search movies…");
    for (const text of ["T", "Ti", "Tit", "Titl", "Title 0007"]) fireEvent.change(box, { target: { value: text } });

    expect(await screen.findByRole("button", { name: "Search these 1" })).toBeInTheDocument();
    const searches = paged.slice(before).map((params) => params.get("search")).filter(Boolean);
    expect(searches).toEqual(["Title 0007"]);
  });

  it("a TV library of 5000 shows is virtualised too, the pills carry TV's own counts, Continuing narrows through the catalogue, and a typed search asks once", async () => {
    const shows = Array.from({ length: 5000 }, (_, index) => film(`s${index}`, `Show ${String(index).padStart(4, "0")}`));
    pageFor = (params) => {
      const rows = params.get("search") ? [film("s7", "Show 0007")] : params.get("status") === "airing" ? shows.slice(0, 120) : shows;
      return { ...page(rows), totalCount: rows.length, facets: { all: 5000, airing: 120, covered: 3000, unmonitored: 10 } };
    };
    await renderShelf("shows");
    await waitFor(() => expect(rowButtons().length).toBeGreaterThan(0));
    expect(rowButtons().length).toBeLessThan(120);
    expect(screen.getByRole("button", { name: /^All/ })).toHaveTextContent("5000");
    expect(screen.getByRole("button", { name: /Continuing/ })).toHaveTextContent("120");
    expect(await screen.findByRole("button", { name: "Search these 5000" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Continuing/ }));
    expect(await screen.findByRole("button", { name: "Search these 120" })).toBeInTheDocument();
    expect(lastPage().get("status")).toBe("airing");
    const before = paged.length;
    const box = screen.getByPlaceholderText("Search TV shows…");
    for (const text of ["S", "Sh", "Show", "Show 0007"]) fireEvent.change(box, { target: { value: text } });
    expect(await screen.findByRole("button", { name: "Search these 1" })).toBeInTheDocument();
    expect(paged.slice(before).map((params) => params.get("search")).filter(Boolean)).toEqual(["Show 0007"]);
  });

  // DEFECT (not virtualised): the register promises virtualised rows for the Overview layout at 5000+ titles. LibraryOverview maps
  // every item to a row, so 5000 titles draw 5000 rows of poster, text and badges at once.
  it("the Overview layout draws only the rows near the screen", async () => {
    pageFor = () => page(BIG);
    await renderShelf();
    await chooseLayout("Overview");
    await waitFor(() => expect(screen.queryAllByRole("button", { name: /^Open Title/ }).length).toBeGreaterThan(0));
    expect(screen.getAllByRole("button", { name: /^Open Title/ }).length).toBeLessThan(200);
  });
});
