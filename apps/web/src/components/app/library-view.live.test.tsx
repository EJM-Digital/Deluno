import { act, configure, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { stubVirtualLayout } from "../../test/virtual-layout";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The shelf follows what happens elsewhere (coverage register, area library): a title already on the shelf is read again on its own and
 * swapped in place; a new title, a removal, a library change, a burst of more than 12 changes, or a change that moves a title out of
 * the current filter reads the whole shelf again behind the one on screen, without blanking it or dropping the selection.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  handlers: {} as Record<string, Array<(event: { id: string }) => void>>,
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn(() => "toast-1"), dismiss: vi.fn() })
}));

vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast, realtimeToast: {}, Toaster: () => null }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Library: (id: string) => `library:${id}` },
  useSignalREvent: (event: string, _subject: unknown, handler: (payload: { id: string }) => void) => {
    mocks.handlers[event] = [handler];
  }
}));

window.matchMedia ??= ((query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

let LibraryView: typeof import("./library-view").LibraryView;

const film = (id: string, name: string, monitored = true) => ({
  id, title: name, releaseYear: 2020, monitored, wantedStatus: "missing", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null,
  genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null
});

let shelf: ReturnType<typeof film>[];
let titleReads: string[];
let wholeReads: number;
let countReads: number;
let holdShelf: Promise<void> | null;
let restoreLayout: () => void;

const emit = async (event: string, ...ids: string[]) => { await act(async () => { for (const id of ids) for (const handler of mocks.handlers[event] ?? []) handler({ id }); }); };
const select = (name: string) => screen.findByRole("button", { name: `Select ${name}` });
const present = (name: string) => screen.queryByRole("button", { name: new RegExp(`^(De)?select ${name}$`, "i") });

beforeEach(async () => {
  // The shelf keeps a snapshot of what it last read in module memory; each test starts from a fresh copy of it.
  vi.resetModules();
  ({ LibraryView } = await import("./library-view"));
  restoreLayout = stubVirtualLayout();
  window.localStorage.clear();
  window.sessionStorage.clear();
  for (const key of Object.keys(mocks.handlers)) delete mocks.handlers[key];
  shelf = [film("m1", "Alien"), film("m2", "Aliens"), film("m3", "Heat")];
  titleReads = [];
  wholeReads = 0;
  countReads = 0;
  holdShelf = null;
  mocks.fetchJson.mockImplementation(async (path: string) => {
    if (path.startsWith("/api/movies/page")) {
      const params = new URLSearchParams(path.split("?")[1]);
      if (params.get("pageSize") === "1") countReads += 1;
      else {
        wholeReads += 1;
        if (holdShelf && wholeReads > 1) await holdShelf;
      }
      return { items: shelf, totalCount: shelf.length, nextPageToken: null, facets: { all: shelf.length, unmonitored: shelf.filter((entry) => !entry.monitored).length, covered: 0 } };
    }
    const single = /^\/api\/movies\/(m[^/?]*)$/.exec(path);
    if (single) {
      titleReads.push(single[1]);
      return shelf.find((entry) => entry.id === single[1]);
    }
    if (path.endsWith("/controls")) return { kind: "movies", filterFields: [], sortFields: [], posterOptions: [] };
    if (path === "/api/libraries") return [{ id: "lib-a", name: "Films", mediaType: "movies", rootPath: "/films", qualityProfileId: "p1" }];
    return [];
  });
  mocks.authedFetch.mockResolvedValue(new Response("{}", { status: 200 }));
});
afterEach(() => {
  restoreLayout();
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
  vi.clearAllMocks();
});

async function renderShelf(url = "/") {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: [url] });
  render(<RouterProvider router={router} />);
  await screen.findByRole("button", { name: "Search these 3" });
  await waitFor(() => expect(present("Alien")).not.toBeNull());
}

describe("a change to a title already on the shelf", () => {
  it("reads that title alone and the counts again, swaps the title in place, and leaves the shelf and the selection as they were", async () => {
    await renderShelf();
    fireEvent.click(await select("Aliens"));
    const reloads = wholeReads;
    shelf = shelf.map((entry) => (entry.id === "m1" ? { ...entry, title: "Alien (Director's Cut)" } : entry));

    await emit("MovieChanged", "m1");

    await waitFor(() => expect(present("Alien \\(Director's Cut\\)")).not.toBeNull());
    expect(titleReads).toEqual(["m1"]);
    expect(wholeReads).toBe(reloads);
    expect(countReads).toBeGreaterThan(0);
    expect(present("Aliens")).toHaveAccessibleName("Deselect Aliens");
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("a burst of changes to the same title is gathered into one read", async () => {
    await renderShelf();
    shelf = shelf.map((entry) => (entry.id === "m3" ? { ...entry, title: "Heat (1995)" } : entry));

    await emit("MovieChanged", "m3", "m3", "m3");

    await waitFor(() => expect(present("Heat \\(1995\\)")).not.toBeNull());
    expect(titleReads).toEqual(["m3"]);
  });
});

describe("a change that needs the whole shelf read again", () => {
  it.each([
    ["a title that is not on the shelf", () => emit("MovieChanged", "m9")],
    ["a change to every title", () => emit("MovieChanged", "*")],
    ["a library change", () => emit("LibraryChanged", "lib-a")]
  ])("%s reads the shelf again behind the one on screen, which stays up until the new one arrives, and keeps the selection", async (_what, announce) => {
    await renderShelf();
    fireEvent.click(await select("Aliens"));
    let release!: () => void;
    holdShelf = new Promise<void>((resolve) => { release = resolve; });
    const before = wholeReads;
    shelf = [...shelf, film("m9", "Zodiac")];

    await announce();

    await waitFor(() => expect(wholeReads).toBe(before + 1));
    expect(present("Alien")).not.toBeNull();
    expect(present("Zodiac")).toBeNull();
    expect(screen.queryByText("Your movies library is empty")).toBeNull();
    release();
    await waitFor(() => expect(present("Zodiac")).not.toBeNull());
    expect(present("Aliens")).toHaveAccessibleName("Deselect Aliens");
    expect(titleReads).toEqual([]);
  });

  it("a removal drops the title from the shelf and from the selection", async () => {
    await renderShelf();
    fireEvent.click(await select("Aliens"));
    fireEvent.click(await select("Heat"));
    shelf = shelf.filter((entry) => entry.id !== "m2");

    await emit("MovieChanged", "m2");

    await waitFor(() => expect(present("Aliens")).toBeNull());
    expect(present("Heat")).toHaveAccessibleName("Deselect Heat");
    expect(screen.getByRole("button", { name: "Clear selection" })).toBeInTheDocument();
    expect(screen.getByText("item selected")).toBeInTheDocument();
  });

  it("more than twelve changed titles are one read of the whole shelf, not thirteen reads of single titles", async () => {
    await renderShelf();
    const before = wholeReads;
    shelf = [...shelf, film("m10", "Ten")];

    await emit("MovieChanged", ...Array.from({ length: 13 }, (_, index) => `m${index + 1}`));

    await waitFor(() => expect(wholeReads).toBe(before + 1));
    await waitFor(() => expect(present("Ten")).not.toBeNull());
    expect(titleReads).toEqual([]);
  });

  it("a title that no longer fits the current filter sends the shelf to be read again, which says where it goes", async () => {
    await renderShelf("/?filter=upgrades");
    await waitFor(() => expect(present("Alien")).not.toBeNull());
    const before = wholeReads;
    shelf = shelf.map((entry) => (entry.id === "m1" ? { ...entry, monitored: false } : entry));
    shelf = shelf.filter((entry) => entry.id !== "m1");

    await emit("MovieChanged", "m1");

    await waitFor(() => expect(wholeReads).toBe(before + 1));
    await waitFor(() => expect(present("Alien")).toBeNull());
  });

  it("a read that fails leaves the shelf as it was", async () => {
    await renderShelf();
    const inner = mocks.fetchJson.getMockImplementation()!;
    mocks.fetchJson.mockImplementation(async (path: string) => {
      if (path.startsWith("/api/movies/page")) throw new Error("down");
      return inner(path);
    });

    await emit("LibraryChanged", "lib-a");
    await new Promise((resolve) => setTimeout(resolve, 900)); // reason: longer than the 500 ms the changes are gathered for

    expect(present("Alien")).not.toBeNull();
    expect(present("Heat")).not.toBeNull();
  });
});
