import { configure, render, screen, waitFor } from "@testing-library/react";
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

const shelf = [film("m1", "Alien"), film("m2", "Aliens"), film("m3", "Heat")];
let gate: Promise<void> | null;
let restoreLayout: () => void;
const present = (name: string) => screen.queryByRole("button", { name: new RegExp(`^(De)?select ${name}$`, "i") });

beforeEach(async () => {
  // The snapshot lives in module memory: a fresh copy of the module starts every test with none.
  vi.resetModules();
  ({ LibraryView } = await import("./library-view"));
  restoreLayout = stubVirtualLayout();
  window.localStorage.clear();
  window.sessionStorage.clear();
  for (const key of Object.keys(mocks.handlers)) delete mocks.handlers[key];
  gate = null;
  mocks.fetchJson.mockImplementation(async (path: string) => {
    if (path.startsWith("/api/movies/page")) {
      const params = new URLSearchParams(path.split("?")[1]);
      if (params.get("pageSize") !== "1" && gate) await gate;
      return { items: shelf, totalCount: shelf.length, nextPageToken: null, facets: { all: shelf.length, unmonitored: 0, covered: 0 } };
    }
    if (path.endsWith("/controls")) return { kind: "movies", filterFields: [], sortFields: [], posterOptions: [] };
    if (path === "/api/libraries") return ["lib-a", "lib-b", ...Array.from({ length: 9 }, (_, index) => `lib-${index + 1}`)].map((id) => ({ id, name: `Films ${id}`, mediaType: "movies", rootPath: `/${id}`, qualityProfileId: "p1" }));
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

function mount(url: string) {
  const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" /> }, { path: "*", element: <p>elsewhere</p> }], { initialEntries: [url] });
  return render(<RouterProvider router={router} />);
}

describe("the shelf's last-snapshot paint", () => {
  it("paints the rows of the same query at once on a revisit, before the new read lands", async () => {
    const first = mount("/");
    await waitFor(() => expect(present("Alien")).not.toBeNull());
    first.unmount();

    let release!: () => void;
    gate = new Promise<void>((resolve) => { release = resolve; });
    mount("/");

    // No waiting: the read is held, and the rows are already there.
    expect(present("Alien")).not.toBeNull();
    expect(present("Heat")).not.toBeNull();
    release();
  });

  it("never replays one query's rows for another: a different library shows no rows until its own read lands", async () => {
    const first = mount("/");
    await waitFor(() => expect(present("Alien")).not.toBeNull());
    first.unmount();

    let release!: () => void;
    gate = new Promise<void>((resolve) => { release = resolve; });
    mount("/?library=lib-b");

    expect(present("Alien")).toBeNull();
    expect(present("Heat")).toBeNull();
    release();
    await waitFor(() => expect(present("Alien")).not.toBeNull());
  });

  it("keeps the eight most recent queries and lets the oldest go", async () => {
    for (let n = 1; n <= 9; n += 1) {
      const view = mount(`/?library=lib-${n}`);
      await waitFor(() => expect(present("Alien")).not.toBeNull());
      view.unmount();
    }

    let release!: () => void;
    gate = new Promise<void>((resolve) => { release = resolve; });

    // The ninth is the newest and is still held; the first was pushed out by it.
    const newest = mount("/?library=lib-9");
    expect(present("Alien")).not.toBeNull();
    newest.unmount();
    mount("/?library=lib-1");
    expect(present("Alien")).toBeNull();
    release();
  });
});
