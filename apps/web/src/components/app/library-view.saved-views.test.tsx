import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { stubVirtualLayout } from "../../test/virtual-layout";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * Saved filters (coverage register, area library): the real LibraryView is rendered whole; only the network and the live
 * connection are stood in for. What is asserted is exactly what is sent to be saved, what clicking a saved filter puts back on the
 * shelf, and the automation choice each saved filter carries and the words that say what it does.
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

const { LibraryView } = await import("./library-view");

const film = (id: string, name: string) => ({
  id, title: name, releaseYear: 2020, monitored: true, wantedStatus: "missing", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null,
  genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null
});
const CONTROLS = {
  kind: "movies",
  filterFields: [{ id: "year", label: "Year", hint: "When it came out.", group: "title", valueKind: "year", operators: ["min", "max"], options: null }],
  sortFields: [{ id: "title", label: "Title", hint: "A to Z." }, { id: "year", label: "Year", hint: "Release year." }, { id: "added", label: "Date added", hint: "Newest first." }],
  posterOptions: []
};
const SAVED: Record<string, unknown> & { automationAction: string | null; id: string; name: string } = {
  id: "view-1", userId: "u", variant: "movies", libraryId: "lib-b", name: "Recent kids films", quickFilter: "missing", monitoring: "monitored",
  sortField: "year", sortDirection: "asc", viewMode: "list", cardSize: "lg", displayOptionsJson: JSON.stringify({ showRating: true }),
  rulesJson: JSON.stringify([{ field: "year", operator: "min", values: ["2015"] }]), automationAction: null, createdUtc: "2026-01-01T00:00:00Z", updatedUtc: "2026-01-01T00:00:00Z"
};

let views: Array<typeof SAVED>;
let pages: URLSearchParams[];
let saved: Array<{ url: string; method: string; body: any }>;
let restoreLayout: () => void;

beforeEach(() => {
  restoreLayout = stubVirtualLayout();
  window.localStorage.clear();
  window.sessionStorage.clear();
  views = [];
  pages = [];
  saved = [];
  mocks.fetchJson.mockImplementation(async (path: string, init?: RequestInit) => {
    if (path.startsWith("/api/movies/page")) {
      pages.push(new URLSearchParams(path.split("?")[1]));
      return { items: [film("m1", "Alien")], totalCount: 1, nextPageToken: null, facets: { all: 1, unmonitored: 0, covered: 0 } };
    }
    if (path === "/api/movies/controls") return CONTROLS;
    if (path === "/api/libraries") return [{ id: "lib-a", name: "4K films", mediaType: "movies", rootPath: "/a", qualityProfileId: "p1" }, { id: "lib-b", name: "Kids films", mediaType: "movies", rootPath: "/b", qualityProfileId: "p1" }];
    if (path.startsWith("/api/library-views?")) return views;
    if (path === "/api/library-views" && init?.method === "POST") {
      const body = JSON.parse(String(init.body));
      saved.push({ url: path, method: "POST", body });
      return { ...SAVED, ...body, id: "view-new", automationAction: null };
    }
    if (path.startsWith("/api/library-views/") && init?.method === "PUT") {
      const body = JSON.parse(String(init.body));
      saved.push({ url: path, method: "PUT", body });
      if (path.endsWith("/refuse")) throw new Error("refused");
      return { ...SAVED, ...body, id: path.split("/").pop() };
    }
    return [];
  });
  mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    saved.push({ url: String(input), method: init?.method ?? "GET", body: undefined });
    return init?.method === "DELETE" ? new Response(null, { status: 204 }) : new Response("{}", { status: 200 });
  });
});
afterEach(() => {
  restoreLayout();
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
  vi.clearAllMocks();
});

async function openFilterDrawer() {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: ["/"] });
  render(<RouterProvider router={router} />);
  await screen.findByRole("button", { name: /^Search these \d+$/ });
  fireEvent.click(screen.getByText("Filter"));
  return await screen.findByRole("dialog");
}

describe("saving the current view", () => {
  it("sends the library, pill, monitoring, sort, direction, layout, size, poster options and finished conditions under the typed name", async () => {
    const drawer = await openFilterDrawer();
    fireEvent.click(within(drawer).getByRole("button", { name: "Add a field" }));
    fireEvent.click(within(drawer).getByRole("button", { name: /^Year/ }));
    fireEvent.change(within(drawer).getByRole("spinbutton", { name: "Year" }), { target: { value: "2010" } });
    fireEvent.click(within(drawer).getByRole("button", { name: "Add a field" }));
    fireEvent.click(within(drawer).getByRole("button", { name: /^Year/ }));
    fireEvent.change(within(drawer).getByPlaceholderText("Name this filter"), { target: { value: "  Newer films  " } });

    fireEvent.click(within(drawer).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(saved.filter((call) => call.method === "POST")).toHaveLength(1));
    expect(saved[0].body).toMatchObject({
      variant: "movies", libraryId: null, name: "Newer films", quickFilter: "all", monitoring: "any", sortField: expect.any(String),
      sortDirection: expect.stringMatching(/^(asc|desc)$/), viewMode: "grid", cardSize: "md", displayOptionsJson: expect.any(String)
    });
    expect(JSON.parse(saved[0].body.rulesJson)).toEqual([{ field: "year", operator: "min", values: ["2010"] }]);
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Custom filter saved"));
    expect(await within(drawer).findByText("Newer films")).toBeInTheDocument();
    expect((within(drawer).getByPlaceholderText("Name this filter") as HTMLInputElement).value).toBe("");
  });

  it("an empty name toasts Give the custom filter a name first. and sends nothing", async () => {
    const drawer = await openFilterDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Save" }));

    expect(mocks.toast.info).toHaveBeenCalledWith("Give the custom filter a name first.");
    expect(saved).toEqual([]);
  });

  it("shows Saving… on the button while the save is in flight, and says plainly when the save fails", async () => {
    let fail!: (reason: Error) => void;
    const inner = mocks.fetchJson.getMockImplementation()!;
    mocks.fetchJson.mockImplementation((path: string, init?: RequestInit) =>
      path === "/api/library-views" && init?.method === "POST" ? new Promise((_, reject) => { fail = reject; }) : inner(path, init));
    const drawer = await openFilterDrawer();
    fireEvent.change(within(drawer).getByPlaceholderText("Name this filter"), { target: { value: "Doomed" } });

    fireEvent.click(within(drawer).getByRole("button", { name: "Save" }));

    expect(await within(drawer).findByRole("button", { name: "Saving…" })).toBeDisabled();
    fail(new Error("down"));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not save this custom filter."));
    expect(within(drawer).getByRole("button", { name: "Save" })).toBeEnabled();
    expect(within(drawer).queryByText("Doomed")).toBeNull();
  });
});

describe("applying a saved filter", () => {
  it("puts back the library, pill, monitoring, sort, direction, layout and conditions it holds and toasts Applied {name}", async () => {
    views = [SAVED];
    const drawer = await openFilterDrawer();

    fireEvent.click(await within(drawer).findByText("Recent kids films"));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Applied Recent kids films"));
    await waitFor(() => {
      const last = pages.at(-1)!;
      expect(last.get("libraryId")).toBe("lib-b");
      expect(last.get("status")).toBe("missing");
      expect(last.get("monitored")).toBe("true");
      expect(last.get("sort")).toBe("year");
      expect(last.get("direction")).toBe("asc");
      expect(last.getAll("f")).toEqual(["year:min:2015"]);
    });
    expect(screen.getByText(/[0-9]+ narrowing/)).toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    expect(await screen.findByRole("grid")).toBeInTheDocument();
  });

  it("says where it looks and what it narrows, and an unreadable condition list reads as no narrowing", async () => {
    views = [SAVED, { ...SAVED, id: "view-2", name: "Everything", libraryId: null, rulesJson: "not json", quickFilter: "all", monitoring: "any" }];
    const drawer = await openFilterDrawer();

    expect(await within(drawer).findByText(/Kids films · Year at least 2015/)).toBeInTheDocument();
    expect(within(drawer).getByText(/All libraries · no narrowing/)).toBeInTheDocument();
  });
});

describe("a saved filter's automation menu", () => {
  it("offers View only and Search on cycle, sends PUT /api/library-views/{id} with the choice, and says what it now does", async () => {
    views = [SAVED];
    const drawer = await openFilterDrawer();
    const menu = await within(drawer).findByRole("combobox", { name: "Automation for Recent kids films" });
    expect(menu).toHaveTextContent("View only");

    fireEvent.click(menu);
    expect(within(screen.getByRole("listbox", { name: "Automation for Recent kids films" })).getAllByRole("option").map((option) => option.textContent)).toEqual([
      expect.stringContaining("View only"), expect.stringContaining("Search on cycle")
    ]);
    fireEvent.click(screen.getByRole("option", { name: /^Search on cycle/ }));

    await waitFor(() => expect(saved.filter((call) => call.method === "PUT")).toHaveLength(1));
    expect(saved[0].url).toBe("/api/library-views/view-1");
    expect(saved[0].body).toMatchObject({ libraryId: "lib-b", name: "Recent kids films", quickFilter: "missing", monitoring: "monitored", automationAction: "search" });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Recent kids films will scope the next library search cycle."));
    expect(screen.getByRole("combobox", { name: "Automation for Recent kids films" })).toHaveTextContent("Search on cycle");
  });

  it("going back to View only sends no action and says the filter is view-only again", async () => {
    views = [{ ...SAVED, automationAction: "search" }];
    const drawer = await openFilterDrawer();
    const menu = await within(drawer).findByRole("combobox", { name: "Automation for Recent kids films" });
    expect(menu).toHaveTextContent("Search on cycle");

    fireEvent.click(menu);
    fireEvent.click(screen.getByRole("option", { name: /^View only/ }));

    await waitFor(() => expect(saved.filter((call) => call.method === "PUT")).toHaveLength(1));
    expect(saved[0].body.automationAction).toBeNull();
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Recent kids films is view-only again."));
  });

  it("a change the server refuses is said and the menu keeps showing what is saved", async () => {
    views = [{ ...SAVED, id: "refuse" }];
    const drawer = await openFilterDrawer();
    const menu = await within(drawer).findByRole("combobox", { name: "Automation for Recent kids films" });

    fireEvent.click(menu);
    fireEvent.click(screen.getByRole("option", { name: /^Search on cycle/ }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not update automation for Recent kids films."));
    expect(screen.getByRole("combobox", { name: "Automation for Recent kids films" })).toHaveTextContent("View only");
  });
});

describe("removing a saved filter", () => {
  it("sends DELETE /api/library-views/{id} and takes it off the list; a refusal says so and keeps it", async () => {
    views = [SAVED];
    const drawer = await openFilterDrawer();
    await within(drawer).findByText("Recent kids films");

    fireEvent.click(within(drawer).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(saved.some((call) => call.method === "DELETE" && call.url === "/api/library-views/view-1")).toBe(true));
    await waitFor(() => expect(within(drawer).queryByText("Recent kids films")).toBeNull());
    expect(within(drawer).getByText(/Nothing saved yet\./)).toBeInTheDocument();
  });

  it("a delete the server refuses says so and keeps the filter", async () => {
    views = [SAVED];
    mocks.authedFetch.mockImplementation(async () => new Response("{}", { status: 500 }));
    const drawer = await openFilterDrawer();
    await within(drawer).findByText("Recent kids films");

    fireEvent.click(within(drawer).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not remove this custom filter."));
    expect(within(drawer).getByText("Recent kids films")).toBeInTheDocument();
  });
});
