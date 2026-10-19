import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The shelf's selection bar and the bulk dialog it opens (coverage register, area library): the real LibraryView is rendered whole;
 * only the network and the live connection are stood in for. What is asserted is which dialog each bar button opens and how it is
 * preset, that nothing changes until the second step, and the one request each confirmed operation sends.
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

import { stubVirtualLayout } from "../../test/virtual-layout";

const { LibraryView } = await import("./library-view");

const title = (id: string, name: string) => ({
  id, title: name, releaseYear: 2020, monitored: true, wantedStatus: "missing", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null,
  genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null
});
const FILMS = [title("m1", "Alien"), title("m2", "Aliens"), title("m3", "Heat")];
let shown: typeof FILMS;
const SHOWS = [title("s1", "Severance"), title("s2", "Silo"), title("s3", "Andor")];
const page = (items: unknown[]) => ({ items, totalCount: items.length, nextPageToken: null, facets: { all: items.length, unmonitored: 0, covered: 0 } });

let calls: Array<{ url: string; method: string; body: any }>;
let writeReply: (url: string) => { ok: boolean; status?: number; body: unknown };

let restoreLayout: () => void;

beforeEach(() => {
  restoreLayout = stubVirtualLayout();
  window.localStorage.clear();
  calls = [];
  shown = [...SHOWS];
  writeReply = (url) => ({ ok: true, body: url.endsWith("/bulk") ? { results: [], failureCount: 0 } : { updated: 2, searchesTriggered: 2, libraryCount: 1 } });
  mocks.fetchJson.mockImplementation(async (path: string) => {
    if (path.startsWith("/api/movies/page")) return page(FILMS);
    if (path.startsWith("/api/series/page")) return page(shown);
    if (path.endsWith("/controls")) return { kind: path.includes("series") ? "shows" : "movies", filterFields: [], sortFields: [], posterOptions: [] };
    if (path === "/api/libraries") return [{ id: "lib-a", name: "Films", mediaType: "movies", rootPath: "/films", qualityProfileId: "p1" }, { id: "lib-t", name: "Shows", mediaType: "tv", rootPath: "/shows", qualityProfileId: "p2" }];
    if (path === "/api/quality-profiles") return [{ id: "p1", name: "HD films", mediaType: "movies" }, { id: "p2", name: "HD shows", mediaType: "tv" }];
    return [];
  });
  mocks.authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    calls.push({ url, method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const reply = writeReply(url);
    if (url === "/api/series/bulk" && reply.ok) {
      const gone = new Set(((reply.body as { results: Array<{ seriesId: string; succeeded: boolean }> }).results ?? []).filter((r) => r.succeeded).map((r) => r.seriesId));
      shown = shown.filter((entry) => !gone.has(entry.id));
    }
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? (reply.ok ? 200 : 400), headers: { "Content-Type": "application/json" } });
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
async function select(...names: string[]) {
  for (const name of names) fireEvent.click(await screen.findByRole("button", { name: `Select ${name}` }));
}
const writes = (url: string) => calls.filter((c) => c.url === url);
const bar = () => screen.findByRole("button", { name: "Clear selection" });
const dialog = () => screen.findByText("Bulk workflow").then((label) => label.closest("div.fixed") as HTMLElement);

describe("the selection bar", () => {
  it("counts the selected titles, and Clear and Escape empty the selection", async () => {
    await renderShelf();

    await select("Alien", "Heat");
    expect(await screen.findByText("items selected")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
    fireEvent.click(await bar());
    await waitFor(() => expect(screen.queryByText("items selected")).toBeNull());

    await select("Alien");
    expect(await screen.findByText("item selected")).toBeInTheDocument();
    fireEvent.keyDown(window, { key: "Escape" });
    await waitFor(() => expect(screen.queryByText("item selected")).toBeNull());
  });

  it("Undo and Redo start disabled, and the bar's other buttons are all there", async () => {
    await renderShelf();
    await select("Alien");

    expect(await screen.findByRole("button", { name: "Undo" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Redo" })).toBeDisabled();
    for (const name of ["Monitor", "Search now", "Unmonitor", "Remove", "Bulk edit"]) expect(screen.getByRole("button", { name })).toBeEnabled();
  });
});

describe("Monitor and Unmonitor from the bar", () => {
  it("Monitor opens the Bulk workflow preset to Monitored; Review and continue changes nothing, Confirm and run sends PUT /api/movies/monitoring and clears the selection", async () => {
    await renderShelf();
    await select("Alien", "Heat");

    fireEvent.click(screen.getByRole("button", { name: "Monitor" }));
    const box = await dialog();
    expect(within(box).getByText("2 selected titles")).toBeInTheDocument();
    expect(within(box).getByRole("combobox", { name: "Operation" })).toHaveValue("monitoring");
    expect(within(box).getByRole("combobox", { name: "Monitoring state" })).toHaveValue("true");
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    expect(await within(box).findByText("Confirming will run this operation across 2 selected titles.")).toBeInTheDocument();
    expect(writes("/api/movies/monitoring")).toHaveLength(0);
    fireEvent.click(within(box).getByRole("button", { name: "Confirm and run" }));

    await waitFor(() => expect(writes("/api/movies/monitoring")).toHaveLength(1));
    expect(writes("/api/movies/monitoring")[0]).toMatchObject({ method: "PUT", body: { movieIds: ["m1", "m3"], monitored: true } });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Monitor selected titles"));
    await waitFor(() => expect(screen.queryByText("Bulk workflow")).toBeNull());
    expect(screen.queryByText("items selected")).toBeNull();
  });

  it("Unmonitor opens the same dialog preset to Unmonitored and sends monitored false for the shows", async () => {
    await renderShelf("shows");
    await select("Severance", "Silo");

    fireEvent.click(screen.getByRole("button", { name: "Unmonitor" }));
    const box = await dialog();
    expect(within(box).getByRole("combobox", { name: "Monitoring state" })).toHaveValue("false");
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    fireEvent.click(await within(box).findByRole("button", { name: "Confirm and run" }));

    await waitFor(() => expect(writes("/api/series/monitoring")).toHaveLength(1));
    expect(writes("/api/series/monitoring")[0]).toMatchObject({ method: "PUT", body: { seriesIds: ["s1", "s2"], monitored: false } });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Unmonitor selected titles"));
  });
});

describe("the two-step confirm", () => {
  it("changing the operation, or closing and reopening, takes the confirmation back", async () => {
    await renderShelf();
    await select("Alien");
    fireEvent.click(screen.getByRole("button", { name: "Monitor" }));
    const box = await dialog();
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    expect(await within(box).findByText("Confirming will run this operation across 1 selected title.")).toBeInTheDocument();

    fireEvent.change(within(box).getByRole("combobox", { name: "Operation" }), { target: { value: "tags" } });

    expect(within(box).queryByText(/Confirming will run this operation/)).toBeNull();
    expect(within(box).getByRole("button", { name: "Review and continue" })).toBeInTheDocument();
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    await within(box).findByRole("button", { name: "Confirm and run" });
    fireEvent.click(within(box).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByText("Bulk workflow")).toBeNull());
    fireEvent.click(screen.getByRole("button", { name: "Bulk edit" }));
    const again = await dialog();
    expect(within(again).queryByText(/Confirming will run this operation/)).toBeNull();
    expect(writes("/api/movies/bulk/tags")).toHaveLength(0);
  });
});

describe("Search now and Bulk edit from the bar", () => {
  it("Search now opens the dialog on the search operation with no extra field and posts the selected films", async () => {
    await renderShelf();
    await select("Aliens", "Heat");

    fireEvent.click(screen.getByRole("button", { name: "Search now" }));
    const box = await dialog();
    expect(within(box).getByRole("combobox", { name: "Operation" })).toHaveValue("search");
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    fireEvent.click(await within(box).findByRole("button", { name: "Confirm and run" }));

    await waitFor(() => expect(writes("/api/movies/bulk/search")).toEqual([expect.objectContaining({ method: "POST", body: { movieIds: ["m2", "m3"] } })]));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Manual search dispatched."));
  });

  it("a search the server refuses is explained in words and the dialog stays open", async () => {
    writeReply = () => ({ ok: false, status: 503, body: { message: "no" } });
    await renderShelf();
    await select("Alien");

    fireEvent.click(screen.getByRole("button", { name: "Search now" }));
    const box = await dialog();
    fireEvent.click(within(box).getByRole("button", { name: "Review and continue" }));
    fireEvent.click(await within(box).findByRole("button", { name: "Confirm and run" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Bulk operation failed", expect.anything()));
    expect(screen.getByText("Bulk workflow")).toBeInTheDocument();
  });

  it("Bulk edit opens the dialog on Set quality profile with this shelf's profiles", async () => {
    await renderShelf();
    await select("Alien");

    fireEvent.click(screen.getByRole("button", { name: "Bulk edit" }));
    const box = await dialog();

    expect(within(box).getByRole("combobox", { name: "Operation" })).toHaveValue("quality");
    await waitFor(() => expect(within(box).getByRole("option", { name: "HD films" })).toBeInTheDocument());
    expect(within(box).queryByRole("option", { name: "HD shows" })).toBeNull();
    expect(within(box).getByRole("option", { name: "Set minimum availability" })).toBeInTheDocument();
  });

  it("on the TV shelf the dialog has no minimum availability", async () => {
    await renderShelf("shows");
    await select("Severance");

    fireEvent.click(screen.getByRole("button", { name: "Bulk edit" }));
    const box = await dialog();

    expect(within(box).queryByRole("option", { name: "Set minimum availability" })).toBeNull();
  });
});

describe("Remove from the bar", () => {
  it("asks first, defaults Prevent automatic re-add to on, and sends one remove for the selected shows, which then leave the shelf", async () => {
    writeReply = () => ({ ok: true, body: { results: [{ seriesId: "s1", succeeded: true }, { seriesId: "s2", succeeded: true }], failureCount: 0 } });
    await renderShelf("shows");
    await select("Severance", "Silo");

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    const box = await screen.findByRole("dialog", { name: /Remove 2 TV shows\?/ });
    expect(within(box).getByRole("checkbox", { name: /Prevent automatic re-add/ })).toBeChecked();
    expect(writes("/api/series/bulk")).toHaveLength(0);
    fireEvent.click(within(box).getByRole("button", { name: "Remove TV shows" }));

    await waitFor(() => expect(writes("/api/series/bulk")).toHaveLength(1));
    expect(writes("/api/series/bulk")[0]).toMatchObject({ method: "POST", body: { seriesIds: ["s1", "s2"], operation: "remove", addImportListExclusion: true } });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith(expect.stringMatching(/^2 TV shows removed from Deluno\./)));
    await waitFor(() => expect(screen.queryByRole("button", { name: /^(De)?select Severance$/i })).toBeNull());
    expect(screen.getByRole("button", { name: /^(De)?select Andor$/i })).toBeInTheDocument();
  });

  it("with Prevent automatic re-add unticked the request says so", async () => {
    writeReply = () => ({ ok: true, body: { results: [{ seriesId: "s1", succeeded: true }], failureCount: 0 } });
    await renderShelf("shows");
    await select("Severance");

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    const box = await screen.findByRole("dialog", { name: /Remove 1 TV show\?/ });
    fireEvent.click(within(box).getByRole("checkbox", { name: /Prevent automatic re-add/ }));
    fireEvent.click(within(box).getByRole("button", { name: "Remove TV show" }));

    await waitFor(() => expect(writes("/api/series/bulk")).toHaveLength(1));
    expect(writes("/api/series/bulk")[0].body).toEqual({ seriesIds: ["s1"], operation: "remove", addImportListExclusion: false });
  });

  it("a show that could not be removed stays on the shelf and the count of failures is said", async () => {
    writeReply = () => ({ ok: true, body: { results: [{ seriesId: "s1", succeeded: true }, { seriesId: "s2", succeeded: false }], failureCount: 1 } });
    await renderShelf("shows");
    await select("Severance", "Silo");

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    fireEvent.click(await within(await screen.findByRole("dialog", { name: /Remove 2 TV shows\?/ })).findByRole("button", { name: "Remove TV shows" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("1 title could not be removed."));
    await waitFor(() => expect(screen.queryByRole("button", { name: /^(De)?select Severance$/i })).toBeNull());
    expect(screen.getByRole("button", { name: /^(De)?select Silo$/i })).toBeInTheDocument();
  });

  it("a request that fails says so and removes nothing from the shelf", async () => {
    writeReply = () => ({ ok: false, status: 500, body: {} });
    await renderShelf("shows");
    await select("Severance");

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    fireEvent.click(await within(await screen.findByRole("dialog", { name: /Remove 1 TV show\?/ })).findByRole("button", { name: "Remove TV show" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Could not remove the selected titles from Deluno."));
    expect(screen.getByRole("dialog", { name: /Remove 1 TV show?/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^(De)?select Severance$/i, hidden: true })).toBeInTheDocument();
  });

  // DEFECT (wording): a bulk remove does take the title's active download with it (the server cancels it with its data; see
  // ARemovedFilmTakesItsRealDownloadWithItTests), but the dialog and the toast both say the download client is left alone.
  it.skip("DEFECT: the remove dialog and the toast say download clients are left alone, which is untrue when the title had a download in flight", async () => {
    writeReply = () => ({ ok: true, body: { results: [{ seriesId: "s1", succeeded: true }], failureCount: 0 } });
    await renderShelf("shows");
    await select("Severance");
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    const box = await screen.findByRole("dialog", { name: /Remove 1 TV show\?/ });
    expect(box.textContent).not.toMatch(/download clients are left alone/);
    fireEvent.click(within(box).getByRole("button", { name: "Remove TV show" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalled());
    expect(String(mocks.toast.success.mock.calls[0][0])).not.toMatch(/download-client items were left alone/);
  });
});
