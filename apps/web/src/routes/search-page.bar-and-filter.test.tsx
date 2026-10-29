import { configure, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * #1427 and #1428: the search bar with its Filter button (type, indexers and categories live in its menu), a search that
 * starts from the address, and the Indexers and Categories choices laid out the same way with no scrolling box of their own.
 */
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch }));
vi.mock("../components/shell/toaster", () => ({ toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }), realtimeToast: {}, Toaster: () => null }));

const { SearchPage } = await import("./search-page");

const INDEXERS = [
  { id: "idx-1", name: "Enabled One", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid/1", priority: 1, categories: "2000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "movies" },
  { id: "idx-2", name: "Enabled Two", protocol: "newznab", privacy: "public", baseUrl: "https://fixture.invalid/2", priority: 2, categories: "5000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "tv" }
];
const CATEGORIES = [{ value: 2000, label: "Movies" }, { value: 2040, label: "Movies/HD" }, { value: 5000, label: "TV" }];
const RELEASE = { releaseName: "Example.Release.WEB.1080p", indexerId: "idx-1", indexerName: "Enabled One", quality: "WEB 1080p", meetsCutoff: true, downloadUrl: "https://fixture.invalid/x.torrent", indexerProtocol: "torznab" };

let sent: Array<Record<string, unknown>> = [];

beforeEach(() => {
  sent = [];
  authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/api/search" && init?.body) sent.push(JSON.parse(String(init.body)));
    const body = url.startsWith("/api/search/categories") ? CATEGORIES : url.startsWith("/api/search") ? { candidates: [RELEASE], indexersQueried: 2 } : url.startsWith("/api/indexers") ? INDEXERS : {};
    return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
  });
});
afterEach(cleanup);

const renderAt = (address: string) => render(<MemoryRouter initialEntries={[address]}><SearchPage /></MemoryRouter>);
const openFilter = () => fireEvent.click(screen.getByRole("button", { name: /^Filter/ }));

describe("the search bar and its Filter menu (#1427)", () => {
  it("keeps type, indexers and categories out of sight until Filter is opened, and closes on Escape", async () => {
    renderAt("/search");
    expect(screen.queryByRole("radio", { name: "TV" })).toBeNull();
    expect(screen.queryByRole("group", { name: "Search filters" })).toBeNull();

    openFilter();
    const menu = await screen.findByRole("group", { name: "Search filters" });
    expect(within(menu).getByRole("radio", { name: "TV" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Filter/ })).toHaveAttribute("aria-expanded", "true");

    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("group", { name: "Search filters" })).toBeNull());
    expect(screen.getByRole("button", { name: /^Filter/ })).toHaveFocus();
  });

  it("closes the menu when the page outside it is pressed, but not when the menu itself is", async () => {
    renderAt("/search");
    openFilter();
    const menu = await screen.findByRole("group", { name: "Search filters" });
    fireEvent.pointerDown(menu);
    expect(screen.getByRole("group", { name: "Search filters" })).toBeInTheDocument();

    fireEvent.pointerDown(document.body);
    await waitFor(() => expect(screen.queryByRole("group", { name: "Search filters" })).toBeNull());
  });

  it("says how many filters are narrowing, and sends them with the search", async () => {
    renderAt("/search");
    expect(screen.getByRole("button", { name: "Filter" })).toBeInTheDocument();
    openFilter();
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    fireEvent.click(await screen.findByRole("checkbox", { name: "Enabled Two" }));
    fireEvent.click(await screen.findByRole("checkbox", { name: "TV" }));

    expect(screen.getByRole("button", { name: "Filter, 3 on" })).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await screen.findByText("Example.Release.WEB.1080p");
    expect(sent).toEqual([{ query: "Example", searchType: "tv", indexerIds: ["idx-2"], categoryIds: [5000] }]);
  });

  it("starts a search from the address, with its type", async () => {
    renderAt("/search?q=Blade%20Runner&type=tv");
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent).toEqual([{ query: "Blade Runner", searchType: "tv", indexerIds: null, categoryIds: null }]);
    expect(screen.getByPlaceholderText("Search for a release…")).toHaveValue("Blade Runner");
  });

  it("reads an unknown type in the address as a movie search, and a bare address starts nothing", async () => {
    const first = renderAt("/search?q=Example&type=other");
    await screen.findByText("Example.Release.WEB.1080p");
    expect(sent).toEqual([{ query: "Example", searchType: "movie", indexerIds: null, categoryIds: null }]);
    first.unmount();

    sent = [];
    renderAt("/search");
    await screen.findByPlaceholderText("Search for a release…");
    expect(sent).toEqual([]);
  });

  it("trims the words in the address before they go in the bar", async () => {
    renderAt("/search?q=%20%20Example%20%20");
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(screen.getByPlaceholderText("Search for a release…")).toHaveValue("Example");
  });

  it("reads a basic type from the address", async () => {
    renderAt("/search?q=Alien&type=basic");
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ searchType: "basic" });
  });
});

describe("Indexers and Categories look the same and never scroll (#1428)", () => {
  it("lays both out in one grid with no scrolling box", async () => {
    renderAt("/search");
    openFilter();
    const indexerBox = (await screen.findByRole("checkbox", { name: "Enabled One" })).closest("[class*='grid']") as HTMLElement;
    const categoryBox = (await screen.findByRole("checkbox", { name: "Movies" })).closest("[class*='grid']") as HTMLElement;

    expect(indexerBox.className).toBe(categoryBox.className);
    for (const box of [indexerBox, categoryBox]) {
      expect(box.className).not.toMatch(/overflow|max-h/);
      expect(box.className).toMatch(/sm:grid-cols-2/);
    }
  });
});
