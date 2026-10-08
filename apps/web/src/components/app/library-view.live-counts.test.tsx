import { act, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #1179: unmonitoring a title moved it from one filter pill to another on the server, but the
 * shelf patched only the title and kept the counts it first read ("Unmonitored 0"). Every title
 * change now reads the counts again.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  handlers: {} as Record<string, Array<(event: { id: string }) => void>>
}));

vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: mocks.fetchJson
}));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Library: (id: string) => `library:${id}` },
  useSignalREvent: (event: string, _subject: unknown, handler: (payload: { id: string }) => void) => {
    (mocks.handlers[event] ??= []).push(handler);
  }
}));
vi.mock("./library-control-rail", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./library-control-rail")>()),
  ControlRail: ({ facets }: { facets: { unmonitored: number; covered: number } | null }) => (
    <div data-testid="pills">Unmonitored {facets?.unmonitored ?? "?"} / Quality met {facets?.covered ?? "?"}</div>
  )
}));
vi.mock("./library-results", () => ({ LibraryResults: () => null }));

const { LibraryView } = await import("./library-view");

function movie(id: string, monitored: boolean) {
  return { id, title: `Film ${id}`, releaseYear: 2020, monitored, wantedStatus: "covered", imdbId: null, posterUrl: null, libraryId: "lib-a", metadataJson: null, genres: null, backdropUrl: null, currentQuality: null, targetQuality: null, fileSizeBytes: null, rating: null, ratings: [], createdUtc: "2026-01-01T00:00:00Z", overview: null };
}

function page(unmonitored: number, covered: number) {
  return {
    items: [movie("m1", unmonitored === 0)],
    totalCount: 12,
    nextPageToken: null,
    facets: { all: 12, unmonitored, covered }
  };
}

describe("the grids' filter pill counts follow a live change (#1179)", () => {
  beforeEach(() => {
    window.localStorage.clear();
    for (const key of Object.keys(mocks.handlers)) delete mocks.handlers[key];
  });
  afterEach(() => { vi.restoreAllMocks(); mocks.fetchJson.mockReset(); });

  it("reads the counts again when a title that stays on the shelf changes", async () => {
    let unmonitoredNow = 0;
    mocks.fetchJson.mockImplementation((path: string) => {
      if (path.startsWith("/api/movies/page")) return Promise.resolve(page(unmonitoredNow, 12 - unmonitoredNow));
      if (path === "/api/movies/m1") return Promise.resolve(movie("m1", unmonitoredNow === 0));
      if (path.endsWith("/controls")) return Promise.resolve({ kind: "movies", filterFields: [], sortFields: [], posterOptions: [] });
      return Promise.resolve([]);
    });
    const router = createMemoryRouter([{ path: "/", element: <LibraryView variant="movies" /> }]);
    render(<RouterProvider router={router} />);

    await waitFor(() => expect(screen.getByTestId("pills").textContent).toBe("Unmonitored 0 / Quality met 12"), { timeout: 3000 });

    unmonitoredNow = 1;
    await act(async () => { for (const handler of mocks.handlers.MovieChanged ?? []) handler({ id: "m1" }); });

    await waitFor(() => expect(screen.getByTestId("pills").textContent).toBe("Unmonitored 1 / Quality met 11"), { timeout: 3000 });
  });
});
