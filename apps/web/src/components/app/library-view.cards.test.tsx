import { configure, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { stubVirtualLayout } from "../../test/virtual-layout";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The poster's own actions and the toolbar's Add button (coverage register, area library): the real LibraryView is rendered whole;
 * only the network and the live connection are stood in for. The Search and Refresh icons on a card send one request each for that
 * title and say, on the card, what it did in a short line.
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

const title = (id: string, name: string, over: Record<string, unknown> = {}) => ({
  id, title: name, releaseYear: 2020, monitored: true, wantedStatus: "missing", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null,
  genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null, ...over
});
const page = (items: unknown[]) => ({ items, totalCount: items.length, nextPageToken: null, facets: { all: items.length, unmonitored: 0, covered: 0 } });

let shelf: unknown[];
let reply: (url: string) => { ok: boolean; status?: number; body: unknown } | "garbled";
let posts: string[];
let restoreLayout: () => void;

beforeEach(() => {
  restoreLayout = stubVirtualLayout();
  window.localStorage.clear();
  window.sessionStorage.clear();
  posts = [];
  shelf = [title("s1", "Severance"), title("s2", "Silo", { hasFile: true, currentQuality: "WEB 1080p", fileSizeBytes: 1000 })];
  reply = () => ({ ok: true, body: {} });
  mocks.fetchJson.mockImplementation(async (path: string) => {
    if (path.startsWith("/api/movies/page") || path.startsWith("/api/series/page")) return page(shelf);
    if (path.endsWith("/controls")) return { kind: path.includes("series") ? "shows" : "movies", filterFields: [], sortFields: [], posterOptions: [] };
    if (path === "/api/libraries") return [{ id: "lib-a", name: "Films", mediaType: "movies", rootPath: "/films", qualityProfileId: "p1" }, { id: "lib-t", name: "Shows", mediaType: "tv", rootPath: "/shows", qualityProfileId: "p2" }];
    return [];
  });
  mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    posts.push(`${init?.method ?? "GET"} ${url}`);
    const answered = reply(url);
    if (answered === "garbled") return new Response("<html>not json</html>", { status: 200 });
    return new Response(JSON.stringify(answered.body), { status: answered.status ?? (answered.ok ? 200 : 500), headers: { "Content-Type": "application/json" } });
  });
});
afterEach(() => {
  restoreLayout();
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
  vi.clearAllMocks();
});

async function renderShelf(variant: "movies" | "shows") {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant={variant} /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: ["/"] });
  render(<RouterProvider router={router} />);
  await screen.findByRole("button", { name: /^Search these \d+$/ });
}
const result = (name: string) => screen.findByRole("status", { name: `Result for ${name}` });

describe("the Search icon on a TV poster", () => {
  it("sends the automatic search for that show alone to the series route, never the preview", async () => {
    reply = () => ({ ok: true, body: { outcome: "checked" } });
    await renderShelf("shows");

    fireEvent.click(await screen.findByRole("button", { name: "Search for Severance now" }));

    await waitFor(() => expect(posts).toEqual(["POST /api/series/s1/search"]));
    expect(await result("Severance")).toHaveTextContent("Nothing found yet");
  });

  it.each([
    ["a release went to a client by name", { releaseName: "Severance.S01E01", dispatchStatus: "sent", downloadClientName: "qBittorrent" }, "Sent to qBittorrent"],
    ["a release went to an unnamed client", { releaseName: "Severance.S01E01", dispatchStatus: "sent" }, "Sent to your download client"],
    ["the client refused the release", { releaseName: "Severance.S01E01", dispatchStatus: "failed" }, "Your download client refused it"],
    ["the release was held for the person", { releaseName: "Severance.S01E01", outcome: "held" }, "Found one, waiting on you"],
    ["a release was found and nothing was sent", { releaseName: "Severance.S01E01", outcome: "checked" }, "Found one, nothing sent"],
    ["nothing was found for a show with no file", { outcome: "checked" }, "Nothing found yet"]
  ])("when %s the card says %s", async (_what, body, words) => {
    reply = () => ({ ok: true, body });
    await renderShelf("shows");

    fireEvent.click(await screen.findByRole("button", { name: "Search for Severance now" }));

    expect(await result("Severance")).toHaveTextContent(words);
  });

  it("nothing found for a show that already holds a file says Nothing better found", async () => {
    reply = () => ({ ok: true, body: { outcome: "checked" } });
    await renderShelf("shows");

    fireEvent.click(await screen.findByRole("button", { name: "Search for Silo now" }));

    expect(await result("Silo")).toHaveTextContent("Nothing better found");
  });

  it("an answer it cannot read says Search finished, and a failed request says Search failed", async () => {
    reply = () => "garbled";
    await renderShelf("shows");
    fireEvent.click(await screen.findByRole("button", { name: "Search for Severance now" }));
    expect(await result("Severance")).toHaveTextContent("Search finished");
  });

  it("a failed request says Search failed on that card only", async () => {
    reply = () => ({ ok: false, status: 500, body: {} });
    await renderShelf("shows");

    fireEvent.click(await screen.findByRole("button", { name: "Search for Severance now" }));

    expect(await result("Severance")).toHaveTextContent("Search failed");
    expect(screen.queryByRole("status", { name: "Result for Silo" })).toBeNull();
  });
});

describe("the Refresh icon on a card", () => {
  it("posts that title's /metadata/refresh and says Metadata refreshed", async () => {
    await renderShelf("shows");

    fireEvent.click(await screen.findByRole("button", { name: "Refresh metadata for Severance" }));

    await waitFor(() => expect(posts).toEqual(["POST /api/series/s1/metadata/refresh"]));
    expect(await result("Severance")).toHaveTextContent("Metadata refreshed");
  });

  it("on the film shelf it goes to the movies route, and a failure says Refresh failed", async () => {
    shelf = [title("m1", "Alien")];
    reply = () => ({ ok: false, status: 500, body: {} });
    await renderShelf("movies");

    fireEvent.click(await screen.findByRole("button", { name: "Refresh metadata for Alien" }));

    await waitFor(() => expect(posts).toEqual(["POST /api/movies/m1/metadata/refresh"]));
    expect(await result("Alien")).toHaveTextContent("Refresh failed");
  });
});

describe("the Add button on the toolbar", () => {
  it.each([
    ["movies", "Add movie", /Add movie/],
    ["shows", "Add TV show", /Add TV show/]
  ] as const)("on the %s shelf, %s opens the Add dialog and pressing it again closes it", async (variant, name, dialogName) => {
    await renderShelf(variant);

    fireEvent.click(screen.getAllByRole("button", { name })[0]);
    expect(await screen.findByRole("dialog", { name: dialogName })).toBeInTheDocument();
    fireEvent.click(screen.getAllByRole("button", { name, hidden: true })[0]);

    await waitFor(() => expect(screen.queryByRole("dialog", { name: dialogName })).toBeNull());
  });
});
